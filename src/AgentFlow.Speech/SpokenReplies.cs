using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NAudio.Wave;

namespace AgentFlow.Speech;

/// <summary>
/// Turns a Markdown reply into what is worth hearing: code blocks, tables and URLs are left out, links read
/// as their text, list markers and emphasis dropped. Sentences are taken as soon as they are complete, so
/// speaking can start while the reply is still streaming.
/// </summary>
public static partial class SpokenText
{
    public static string FromMarkdown(string markdown, bool final)
    {
        var text = markdown.Replace("\r\n", "\n");
        // An unclosed code fence while streaming: everything after it is code (so far).
        var fences = Fence().Matches(text);
        if (fences.Count % 2 == 1 && !final) text = text[..fences[^1].Index];
        text = FencedBlock().Replace(text, "\n");
        if (fences.Count % 2 == 1) text = Fence().Split(text)[0];

        var sb = new StringBuilder();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            // The same clean-up for the line still being written as for finished ones, so text already
            // spoken keeps its position as the reply grows (only the line's own full stop waits).
            if (line.StartsWith('|') || TableRule().IsMatch(line)) continue; // tables
            line = Heading().Replace(line, "");
            line = Bullet().Replace(line, "");
            line = Quote().Replace(line, "");
            if (!final && i == lines.Length - 1)
            {
                sb.Append(line); // still being written: no full stop of our own yet
                break;
            }
            if (line.Length == 0) { sb.Append('\n'); continue; }
            sb.Append(line);
            // A line without its own full stop (a heading or list item) still ends a thought.
            sb.Append(EndsSentence(line) ? " " : ". ");
            sb.Append('\n');
        }
        text = sb.ToString();
        text = Image().Replace(text, "");
        text = Link().Replace(text, "$1");
        text = Url().Replace(text, "a link");
        text = InlineCode().Replace(text, m => m.Groups[1].Value.Length <= 40 ? m.Groups[1].Value : "the code");
        text = Emphasis().Replace(text, "$2");
        text = text.Replace("→", " to ").Replace("—", ", ").Replace("–", ", ").Replace("…", "...");
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n");
        return text;
    }

    private static bool EndsSentence(string line) => line.Length > 0 && ".!?:;".Contains(line[^1]);

    /// <summary>
    /// The complete sentences of <paramref name="text"/> after <paramref name="from"/>, grouped into pieces of a
    /// comfortable length, and where the next call should continue. With final, the rest is taken too.
    /// </summary>
    public static (List<string> Pieces, int Next) TakeSentences(string text, int from, bool final)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        from = Math.Clamp(from, 0, text.Length); // the text can get shorter (an edit, or Markdown closing up)
        var next = from;
        foreach (Match m in SentenceEnd().Matches(text, from))
        {
            var end = m.Index + m.Length;
            current.Append(text, next, end - next);
            next = end;
            if (current.Length >= 60 || (from == 0 && pieces.Count == 0)) // the reply's first sentence goes alone, so speaking starts sooner
            {
                Flush();
            }
        }
        if (final && next < text.Length)
        {
            current.Append(text, next, text.Length - next);
            next = text.Length;
        }
        if (final || current.Length > 0) Flush();
        // Unfinished text stays for the next call; only whole sentences were counted into Next.
        return (pieces, next);

        void Flush()
        {
            var s = current.ToString().Trim();
            if (s.Any(char.IsLetterOrDigit)) pieces.Add(s);
            current.Clear();
        }
    }

    [GeneratedRegex(@"^\s*(```|~~~)", RegexOptions.Multiline)] private static partial Regex Fence();
    [GeneratedRegex(@"^\s*(```|~~~)[^\n]*\n[\s\S]*?^\s*\1[^\n]*$", RegexOptions.Multiline)] private static partial Regex FencedBlock();
    [GeneratedRegex(@"^\|?\s*:?-{3,}")] private static partial Regex TableRule();
    [GeneratedRegex(@"^#{1,6}\s+")] private static partial Regex Heading();
    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?")] private static partial Regex Bullet();
    [GeneratedRegex(@"^>\s?")] private static partial Regex Quote();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"https?://\S+?(?=[.,;:!?)]*(?:\s|$))")] private static partial Regex Url(); // keeps a full stop after it
    [GeneratedRegex(@"`([^`\n]+)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"(\*\*|__|\*|_|~~)(\S(?:.*?\S)?)\1")] private static partial Regex Emphasis();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n+")] private static partial Regex BlankLines();
    [GeneratedRegex(@"[.!?]+[""')\]]*(?=\s|$)|\n")] private static partial Regex SentenceEnd();
}

/// <summary>
/// Remembers how much of each reply has been read aloud, so every sentence is spoken exactly once while a
/// reply streams in: updates may arrive late, twice, or after the reply is finished, and none of that
/// repeats a sentence. Not thread-safe: call it from one thread (the UI's).
/// </summary>
public sealed class ReplyReader
{
    private const int Done = int.MaxValue;
    private readonly Dictionary<Guid, int> _spokenUpTo = [];

    /// <summary>The new pieces to speak for this update of a reply (none when nothing new is complete).</summary>
    public IReadOnlyList<string> Next(Guid reply, string markdown, bool streaming)
    {
        var from = _spokenUpTo.GetValueOrDefault(reply);
        if (from == Done) return []; // finished and fully read: a late or repeated update says nothing new
        var spoken = SpokenText.FromMarkdown(markdown, final: !streaming);
        var (pieces, next) = SpokenText.TakeSentences(spoken, from, final: !streaming);
        _spokenUpTo[reply] = streaming ? next : Done;
        if (_spokenUpTo.Count > 500) Prune();
        return pieces;
    }

    /// <summary>Marks a reply as read (for example when speaking was stopped), so later updates stay quiet.</summary>
    public void Finish(Guid reply) => _spokenUpTo[reply] = Done;

    public void FinishAll()
    {
        foreach (var id in _spokenUpTo.Keys.ToList()) _spokenUpTo[id] = Done;
    }

    private void Prune()
    {
        // Old finished replies never come back; keep the ones still streaming.
        foreach (var id in _spokenUpTo.Where(kv => kv.Value == Done).Select(kv => kv.Key).ToList()) _spokenUpTo.Remove(id);
    }
}

/// <summary>
/// Speaks text through the speakers one piece after another: the next piece is synthesized while the current
/// one plays. <see cref="Stop"/> cuts it off at once (for example when you start talking).
/// </summary>
public sealed class SpeechPlayer : IDisposable
{
    private readonly Func<string, CancellationToken, float[]> _synthesize;
    private readonly Channel<(string Text, int Generation)> _queue = Channel.CreateUnbounded<(string, int)>();
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOutEvent _out;
    private readonly Task _worker;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource _current = new();
    private int _generation;
    private int _pending;

    /// <summary>Raised on a worker thread when a piece could not be spoken.</summary>
    public event Action<Exception>? Failed;

    public SpeechPlayer(Func<string, CancellationToken, float[]> synthesize, int sampleRate)
    {
        _synthesize = synthesize;
        // 16-bit PCM: every Windows audio driver takes it (some reject 32-bit float through the wave mapper).
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMinutes(10),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // silence while waiting for the next piece keeps the device open (no clicks)
        };
        _out = new WaveOutEvent { DesiredLatency = 150 };
        _out.Init(_buffer);
        _worker = Task.Run(WorkAsync);
    }

    public float Volume
    {
        get => _out.Volume;
        set => _out.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>True while something is queued, being synthesized or still playing.</summary>
    public bool IsSpeaking => Volatile.Read(ref _pending) > 0 || _buffer.BufferedDuration > TimeSpan.FromMilliseconds(30);

    public void Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Interlocked.Increment(ref _pending);
        _queue.Writer.TryWrite((text, Volatile.Read(ref _generation)));
    }

    /// <summary>Drops everything queued and stops the sound now.</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        var old = Interlocked.Exchange(ref _current, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        _buffer.ClearBuffer();
    }

    private async Task WorkAsync()
    {
        var silence = new float[_buffer.WaveFormat.SampleRate / 12]; // a short breath between pieces
        try
        {
            await foreach (var (text, generation) in _queue.Reader.ReadAllAsync(_closing.Token))
            {
                try
                {
                    if (generation != Volatile.Read(ref _generation)) continue; // stopped since it was queued
                    var ct = _current.Token;
                    var samples = _synthesize(text, ct);
                    if (generation != Volatile.Read(ref _generation) || ct.IsCancellationRequested) continue;
                    Add(samples);
                    Add(silence);
                    if (_out.PlaybackState != PlaybackState.Playing) _out.Play();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Failed?.Invoke(ex); }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Add(float[] samples)
    {
        var bytes = ToPcm16(samples);
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    /// <summary>Float samples in [-1, 1] to 16-bit little-endian PCM, clipped.</summary>
    public static byte[] ToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)v;
            bytes[2 * i + 1] = (byte)(v >> 8);
        }
        return bytes;
    }

    public void Dispose()
    {
        Stop();
        _closing.Cancel();
        _queue.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _out.Dispose();
        _closing.Dispose();
    }
}
