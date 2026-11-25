using System.IO;
using System.Windows.Threading;
using AgentFlow.App.Services;
using AgentFlow.Core.Chat;
using AgentFlow.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>Which replies Kokoro reads aloud.</summary>
public enum ReadAloudMode { Off, SpokenMessages, AllReplies }

/// <summary>
/// Reading replies aloud with Kokoro-82M. Claude's reply is spoken sentence by sentence while it streams
/// (the next sentence is synthesized while one plays); code, tables and links are left out. Talking again,
/// Esc in the chat or the Stop button cuts it off.
/// </summary>
public sealed partial class VoiceViewModel
{
    private const string ReadModeKey = "tts.mode", TtsVoiceKey = "tts.voice", TtsSpeedKey = "tts.speed", TtsVolumeKey = "tts.volume";

    public IReadOnlyList<VoiceChoice<ReadAloudMode>> ReadModes { get; } =
    [
        new(ReadAloudMode.Off, "Off"),
        new(ReadAloudMode.SpokenMessages, "Replies to messages I spoke"),
        new(ReadAloudMode.AllReplies, "Every reply in direct chat"),
    ];

    public IReadOnlyList<KokoroVoice> TtsVoices => KokoroModel.Voices;

    [ObservableProperty] private VoiceChoice<ReadAloudMode> _selectedReadMode = null!;
    [ObservableProperty] private KokoroVoice _selectedTtsVoice = null!;
    [ObservableProperty] private double _ttsSpeed = 1.0;
    [ObservableProperty] private double _ttsVolume = 100;
    [ObservableProperty] private bool _isTtsDownloading;
    [ObservableProperty] private double _ttsDownloadPercent;
    [ObservableProperty] private string _ttsDownloadText = "";
    [ObservableProperty] private string _ttsEngineText = "";
    [ObservableProperty] private bool _isSpeaking;
    [ObservableProperty] private string _ttsSample = "Hello! I'm reading Claude's replies aloud. Hold the push-to-talk key whenever you want to answer.";

    private KokoroTts? _tts;
    private SpeechPlayer? _player;
    private Task<SpeechPlayer?>? _ttsLoad;
    private DispatcherTimer _speakingPoll = null!;
    private readonly Dictionary<Guid, int> _spokenUpTo = [];          // reply entry -> characters of its spoken text already queued
    private readonly HashSet<string> _voiceTurn = new(StringComparer.Ordinal); // projects whose current turn came from push-to-talk
    private readonly Dictionary<string, string> _lastDictated = new(StringComparer.Ordinal);

    public bool IsTtsDownloaded => KokoroModel.IsDownloaded && File.Exists(KokoroModel.VoiceFile(SelectedTtsVoice.Id));
    public string TtsModelText => KokoroModel.IsDownloaded
        ? $"Downloaded: Kokoro-82M v1.0. Stored in {KokoroModel.Folder}."
        : $"Not downloaded yet: Kokoro-82M v1.0 (about {KokoroModel.ApproxBytes / 1048576} MB, plus about 0.5 MB per voice).";

#pragma warning disable MVVMTK0034 // loading saved settings: set the fields so nothing is saved back or triggered
    private void InitSpeech(IChatStore? store)
    {
        _selectedReadMode = ReadModes.FirstOrDefault(m => m.Value.ToString() == store?.GetSetting(ReadModeKey)) ?? ReadModes[2];
        _selectedTtsVoice = KokoroModel.Voices.FirstOrDefault(v => v.Id == store?.GetSetting(TtsVoiceKey)) ?? KokoroModel.Voices[0];
        _ttsSpeed = double.TryParse(store?.GetSetting(TtsSpeedKey), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0.6, 1.6) : 1.0;
        _ttsVolume = double.TryParse(store?.GetSetting(TtsVolumeKey), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 100) : 100;
        _speakingPoll = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            IsSpeaking = _player?.IsSpeaking == true;
            if (!IsSpeaking) _speakingPoll.Stop();
        }, _ui);
        _speakingPoll.Stop();
    }
#pragma warning restore MVVMTK0034

    partial void OnSelectedReadModeChanged(VoiceChoice<ReadAloudMode> value)
    {
        Save(ReadModeKey, value.Value.ToString());
        if (value.Value == ReadAloudMode.Off) StopSpeaking();
    }

    partial void OnSelectedTtsVoiceChanged(KokoroVoice value)
    {
        Save(TtsVoiceKey, value.Id);
        OnPropertyChanged(nameof(IsTtsDownloaded));
        if (KokoroModel.IsDownloaded && !File.Exists(KokoroModel.VoiceFile(value.Id))) _ = DownloadTtsAsync(); // just the voice, about 0.5 MB
    }

    partial void OnTtsSpeedChanged(double value) => Save(TtsSpeedKey, value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

    partial void OnTtsVolumeChanged(double value)
    {
        Save(TtsVolumeKey, value.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        if (_player is not null) _player.Volume = (float)(value / 100);
    }

    // ---- download and load ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadTtsAsync()
    {
        if (IsTtsDownloading) { _ttsDownloadCts?.Cancel(); return; }
        _ttsDownloadCts = new CancellationTokenSource();
        IsTtsDownloading = true;
        ErrorText = "";
        try
        {
            var progress = new Progress<(long Done, long Total, string File)>(p =>
            {
                TtsDownloadPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                TtsDownloadText = p.File.Length > 0 ? $"Downloading {p.File}: {p.Done / 1048576d:0} of about {p.Total / 1048576d:0} MB" : "Downloaded.";
            });
            await KokoroModel.DownloadAsync(SelectedTtsVoice.Id, progress, _ttsDownloadCts.Token);
            TtsDownloadText = "Downloaded.";
        }
        catch (OperationCanceledException) { TtsDownloadText = "Download stopped. Press Download again to continue."; }
        catch (Exception ex)
        {
            TtsDownloadText = "";
            ErrorText = "The Kokoro download failed: " + ex.Message;
            AppLog.Write("Voice: Kokoro download failed", ex);
        }
        finally
        {
            IsTtsDownloading = false;
            _ttsDownloadCts.Dispose();
            _ttsDownloadCts = null;
            OnPropertyChanged(nameof(IsTtsDownloaded));
            OnPropertyChanged(nameof(TtsModelText));
        }
    }

    private CancellationTokenSource? _ttsDownloadCts;

    [RelayCommand]
    private void DeleteTts()
    {
        UnloadTts();
        try { KokoroModel.Delete(); }
        catch (Exception ex) { ErrorText = "Could not delete Kokoro: " + ex.Message; }
        OnPropertyChanged(nameof(IsTtsDownloaded));
        OnPropertyChanged(nameof(TtsModelText));
    }

    private void UnloadTts()
    {
        StopSpeaking();
        _player?.Dispose();
        _tts?.Dispose();
        _player = null;
        _tts = null;
        _ttsLoad = null;
        TtsEngineText = "";
    }

    /// <summary>Loads Kokoro when there is something to say (and at start-up when replies are read aloud).</summary>
    private Task<SpeechPlayer?> EnsureTtsAsync()
    {
        if (_player is not null) return Task.FromResult<SpeechPlayer?>(_player);
        if (_ttsLoad is not null) return _ttsLoad;
        if (!IsTtsDownloaded) return Task.FromResult<SpeechPlayer?>(null);
        var device = SelectedDevice.Value;
        var gpu = Gpu;
        TtsEngineText = "Loading Kokoro…";
        _ttsLoad = Task.Run(() =>
        {
            var tts = KokoroTts.Load(device, gpu);
            tts.Phonemizer.Phonemes("Warm up.", british: false); // the G2P dictionaries load on first use
            return tts;
        }).ContinueWith(t =>
        {
            SpeechPlayer? player = null;
            _ui.Invoke(() =>
            {
                if (t.IsFaulted)
                {
                    var ex = t.Exception!.GetBaseException();
                    ErrorText = "Kokoro could not load: " + RuntimeCheck.Explain(ex);
                    TtsEngineText = "";
                    AppLog.Write("Voice: loading Kokoro failed", ex);
                    _ttsLoad = null;
                    OnPropertyChanged(nameof(NeedsVcRuntime));
                    return;
                }
                _tts = t.Result;
                try
                {
                    player = _player = new SpeechPlayer((text, ct) => _tts!.Synthesize(text, SelectedTtsVoiceSnapshot, (float)TtsSpeedSnapshot, ct), KokoroModel.SampleRate)
                    {
                        Volume = (float)(TtsVolume / 100),
                    };
                }
                catch (Exception ex)
                {
                    ErrorText = "The speakers could not be opened for Kokoro: " + ex.Message;
                    AppLog.Write("Voice: opening the audio output failed", ex);
                    _tts.Dispose();
                    _tts = null;
                    _ttsLoad = null;
                    TtsEngineText = "";
                    return;
                }
                _player.Failed += ex =>
                {
                    AppLog.Write("Voice: speaking failed", ex);
                    _ui.BeginInvoke(() => ErrorText = "Kokoro could not speak: " + RuntimeCheck.Explain(ex));
                };
                TtsEngineText = $"Kokoro ready: {_tts.Provider}. Loaded in {_tts.LoadTime.TotalSeconds:0.0} s.";
                if (_tts.GpuProblem is { } gpu)
                {
                    TtsEngineText += " The GPU could not run Kokoro, so it runs on the CPU.";
                    AppLog.Write("Voice: Kokoro on the GPU failed, using the CPU: " + gpu);
                }
            });
            return player;
        }, TaskScheduler.Default);
        return _ttsLoad;
    }

    // Read by the synthesis thread.
    private volatile KokoroVoice? _voiceSnapshot;
    private double _speedSnapshot = 1.0;
    private KokoroVoice SelectedTtsVoiceSnapshot => _voiceSnapshot ?? KokoroModel.Voices[0];
    private double TtsSpeedSnapshot => Volatile.Read(ref _speedSnapshot);

    private void Speak(string text)
    {
        _voiceSnapshot = SelectedTtsVoice;
        Volatile.Write(ref _speedSnapshot, TtsSpeed);
        if (_player is { } p)
        {
            p.Enqueue(text);
            IsSpeaking = true;
            _speakingPoll.Start();
            return;
        }
        _ = EnsureTtsAsync().ContinueWith(t => _ui.BeginInvoke(() => { if (t.Result is not null) Speak(text); }), TaskScheduler.Default);
    }

    [RelayCommand]
    public void StopSpeaking()
    {
        _player?.Stop();
        IsSpeaking = false;
    }

    [RelayCommand]
    private void SpeakSample()
    {
        StopSpeaking();
        if (!IsTtsDownloaded) { ErrorText = "Download Kokoro first."; return; }
        foreach (var piece in SpokenText.TakeSentences(SpokenText.FromMarkdown(TtsSample, final: true), 0, final: true).Pieces) Speak(piece);
    }

    /// <summary>Loads Kokoro at start-up when replies are read aloud, so the first reply is spoken at once.</summary>
    public async Task PreloadTtsAsync()
    {
        if (SelectedReadMode.Value != ReadAloudMode.Off && IsTtsDownloaded) await EnsureTtsAsync();
    }

    // ---- replies ----

    /// <summary>Follows a chat and reads its replies aloud as the settings say.</summary>
    public void Watch(ProjectChat chat)
    {
        chat.EntryAdded += e => _ui.BeginInvoke(() => OnEntry(chat, e));
        chat.EntryUpdated += e => _ui.BeginInvoke(() => OnEntry(chat, e));
    }

    /// <summary>Push-to-talk put text into a chat's message box; its reply counts as a reply to a spoken message.</summary>
    internal void NoteDictated(string? projectId, string text)
    {
        if (projectId is not null && text.Length > 0) _lastDictated[projectId] = text;
    }

    private void OnEntry(ProjectChat chat, ChatEntry e)
    {
        var project = chat.Project.Id;
        switch (e.Data)
        {
            case UserMessageData u:
                // A message counts as spoken when what push-to-talk wrote is in it (it may have been edited a little).
                if (_lastDictated.Remove(project, out var dictated) && u.Text.Contains(dictated.Length > 40 ? dictated[..40] : dictated, StringComparison.Ordinal))
                    _voiceTurn.Add(project);
                else
                    _voiceTurn.Remove(project);
                break;

            case TurnSummaryData:
                _voiceTurn.Remove(project);
                break;

            case AssistantTextData a when a.ParentToolUseId is null && ShouldRead(chat):
                var spoken = SpokenText.FromMarkdown(a.Text, final: !a.IsStreaming);
                var from = _spokenUpTo.GetValueOrDefault(e.Id);
                var (pieces, next) = SpokenText.TakeSentences(spoken, from, final: !a.IsStreaming);
                _spokenUpTo[e.Id] = next;
                foreach (var piece in pieces) Speak(piece);
                if (!a.IsStreaming) _spokenUpTo.Remove(e.Id);
                break;
        }
    }

    private bool ShouldRead(ProjectChat chat) =>
        IsTtsDownloaded && chat.ActiveRun is null && SelectedReadMode.Value switch // workflow runs are long: not read aloud
        {
            ReadAloudMode.AllReplies => true,
            ReadAloudMode.SpokenMessages => _voiceTurn.Contains(chat.Project.Id),
            _ => false,
        };
}
