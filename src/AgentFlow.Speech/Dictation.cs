using System.Diagnostics;
using NAudio.Wave;

namespace AgentFlow.Speech;

/// <summary>A microphone, as Windows lists it. Number -1 is the default device.</summary>
public sealed record Microphone(int Number, string Name)
{
    public override string ToString() => Name;

    public static IReadOnlyList<Microphone> List()
    {
        var list = new List<Microphone> { new(-1, "Default microphone") };
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            list.Add(new Microphone(i, WaveInEvent.GetCapabilities(i).ProductName));
        return list;
    }
}

/// <summary>Records 16 kHz mono from a microphone into memory.</summary>
public sealed class MicRecorder : IDisposable
{
    private readonly WaveInEvent _wave;
    private readonly List<float> _samples = new(ParakeetRecognizer.SampleRate * 30);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();

    /// <summary>Loudness of the latest buffer, 0 to 1, for a level meter.</summary>
    public float Level { get; private set; }

    /// <summary>Recording stops by itself at this length.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);

    public MicRecorder(int deviceNumber)
    {
        _wave = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(ParakeetRecognizer.SampleRate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        _wave.DataAvailable += OnData;
        _wave.RecordingStopped += (_, e) =>
        {
            if (e.Exception is { } ex) _stopped.TrySetException(ex);
            else _stopped.TrySetResult();
        };
    }

    public void Start() => _wave.StartRecording();

    /// <summary>Stops and waits for the last buffer to arrive.</summary>
    public async Task StopAsync()
    {
        _wave.StopRecording();
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    public int Count
    {
        get { lock (_lock) return _samples.Count; }
    }

    public float[] Snapshot()
    {
        lock (_lock) return _samples.ToArray();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var peak = 0f;
        lock (_lock)
        {
            if (_samples.Count >= MaxLength.TotalSeconds * ParakeetRecognizer.SampleRate) return;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                var s = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                _samples.Add(s);
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }
        }
        Level = peak;
    }

    public void Dispose() => _wave.Dispose();
}

/// <summary>
/// One push-to-talk: records while the key is held and keeps re-transcribing the whole recording, so the text
/// appears while you speak and earlier words are corrected as more of the sentence arrives. On release it
/// records a short tail (people let go a little early), then makes the final transcription.
/// </summary>
public sealed class DictationSession : IAsyncDisposable
{
    private readonly ParakeetRecognizer _recognizer;
    private readonly MicRecorder _mic;
    private readonly Func<string, string> _polish;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _liveLoop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _decodedCount;

    /// <summary>Raised on a worker thread with the latest live text.</summary>
    public event Action<string>? Partial;

    public TimeSpan Elapsed => _clock.Elapsed;
    public float Level => _mic.Level;

    /// <summary>How long the last transcription took, for the Voice page.</summary>
    public TimeSpan LastDecodeTime { get; private set; }

    /// <summary>Set when a live pass failed.</summary>
    public string? LiveError { get; private set; }

    public static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LiveInterval = TimeSpan.FromMilliseconds(250);

    public DictationSession(ParakeetRecognizer recognizer, int microphone, Func<string, string>? polish = null)
    {
        _recognizer = recognizer;
        _polish = polish ?? (s => s);
        _mic = new MicRecorder(microphone);
        _mic.Start();
        _liveLoop = Task.Run(LiveLoopAsync);
    }

    private async Task LiveLoopAsync()
    {
        var minimum = ParakeetRecognizer.SampleRate * 4 / 10; // wait for 0.4 s of audio
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(LiveInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var count = _mic.Count;
            // Each pass starts only when the last one has finished, so a slower PC simply updates less often.
            if (count < minimum || count - _decodedCount < ParakeetRecognizer.SampleRate / 8) continue;
            try
            {
                var text = Decode(_mic.Snapshot());
                _decodedCount = count;
                if (!_cts.IsCancellationRequested && text.Length > 0) Partial?.Invoke(text);
            }
            catch (Exception ex)
            {
                // Live text stops; the final transcription on release still runs (and reports the error if it fails too).
                if (!_cts.IsCancellationRequested) LiveError = ex.Message;
                break;
            }
        }
    }

    private string Decode(float[] audio)
    {
        var sw = Stopwatch.StartNew();
        var text = _polish(_recognizer.Transcribe(audio));
        LastDecodeTime = sw.Elapsed;
        return text;
    }

    /// <summary>Stops recording and returns the final text.</summary>
    public async Task<string> FinishAsync()
    {
        await Task.Delay(ReleaseTail).ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
        await _mic.StopAsync().ConfigureAwait(false);
        await _liveLoop.ConfigureAwait(false);
        return await Task.Run(() => Decode(_mic.Snapshot())).ConfigureAwait(false);
    }

    /// <summary>Stops without a final transcription (for example when the key turned out to be part of a shortcut).</summary>
    public async Task CancelAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _mic.StopAsync().ConfigureAwait(false); } catch { /* the device may be gone */ }
        await _liveLoop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) await CancelAsync().ConfigureAwait(false);
        _mic.Dispose();
        _cts.Dispose();
    }
}
