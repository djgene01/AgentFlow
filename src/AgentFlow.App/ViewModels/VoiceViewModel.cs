using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using AgentFlow.App.Services;
using AgentFlow.Core.Chat;
using AgentFlow.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>The push-to-talk key. It works while AgentFlow's window has focus.</summary>
public enum PushToTalkKey { RightCtrl, CtrlSpace, F8 }

public sealed record VoiceChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Where dictated text goes: live text while speaking, then the final text.</summary>
public interface IDictationTarget
{
    void ShowLive(string text);
    void Finish(string text);
    /// <summary>The project folder, for project names; null for none.</summary>
    string? ProjectFolder { get; }
}

/// <summary>
/// The "Voice" page and the push-to-talk engine: hold a key (or the mic button), speak, and the text appears
/// in the message box while you talk; it is corrected as the sentence goes on and finished when you let go.
/// Speech to text runs on this PC with NVIDIA Parakeet; nothing is sent anywhere.
/// </summary>
public sealed partial class VoiceViewModel : ObservableObject
{
    private const string EnabledKey = "voice.enabled", LanguageKey = "voice.language", DeviceKey = "voice.device",
        PrecisionKey = "voice.precision", GpuKey = "voice.gpu", MicKey = "voice.mic", PttKey = "voice.key",
        SendKey = "voice.sendOnRelease", FillersKey = "voice.fillers", CommandsKey = "voice.commands",
        ProjectWordsKey = "voice.projectWords";

    private readonly IChatStore? _store;
    private readonly bool _loading = true;
    private readonly Dispatcher _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _meter;

    private ParakeetRecognizer? _recognizer;
    private Task<ParakeetRecognizer?>? _loadTask;
    private string _loadedKey = "";
    private DictationSession? _session;
    private IDictationTarget? _target;
    private CancellationTokenSource? _download;
    private readonly Dictionary<string, (DateTime At, Task<ProjectVocabulary> Vocabulary)> _vocabularies = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VoiceChoice<SpeechLanguage>> Languages { get; } =
    [
        new(SpeechLanguage.English, "English (most accurate)"),
        new(SpeechLanguage.European, "25 European languages (detected automatically)"),
    ];

    public IReadOnlyList<VoiceChoice<SpeechDevice>> Devices { get; } =
    [
        new(SpeechDevice.Auto, "Automatic: the GPU if there is one, else the CPU"),
        new(SpeechDevice.Gpu, "GPU (DirectML: AMD, Intel or NVIDIA)"),
        new(SpeechDevice.Cpu, "CPU"),
    ];

    public IReadOnlyList<VoiceChoice<SpeechPrecision?>> Precisions { get; } =
    [
        new(null, "Automatic: full on a GPU, compact on the CPU"),
        new(SpeechPrecision.Full, "Full precision (2.4 GB, best accuracy)"),
        new(SpeechPrecision.Compact, "Compact (0.6 GB, faster on a CPU)"),
    ];

    public IReadOnlyList<VoiceChoice<PushToTalkKey>> Keys { get; } =
    [
        new(PushToTalkKey.RightCtrl, "Right Ctrl (hold)"),
        new(PushToTalkKey.CtrlSpace, "Ctrl+Space (hold)"),
        new(PushToTalkKey.F8, "F8 (hold)"),
    ];

    public IReadOnlyList<Microphone> Microphones { get; private set; } = [];

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private VoiceChoice<SpeechLanguage> _selectedLanguage;
    [ObservableProperty] private VoiceChoice<SpeechDevice> _selectedDevice;
    [ObservableProperty] private VoiceChoice<SpeechPrecision?> _selectedPrecision;
    [ObservableProperty] private string _gpuIndex = "0";
    [ObservableProperty] private Microphone? _selectedMicrophone;
    [ObservableProperty] private VoiceChoice<PushToTalkKey> _selectedKey;
    [ObservableProperty] private bool _sendOnRelease;
    [ObservableProperty] private bool _removeFillers = true;
    [ObservableProperty] private bool _spokenCommands = true;
    [ObservableProperty] private bool _useProjectWords = true;

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadText = "";
    [ObservableProperty] private string _engineText = "";
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private double _level;
    [ObservableProperty] private string _listeningText = "";
    [ObservableProperty] private string _testText = "";
    [ObservableProperty] private string _timingText = "";
    [ObservableProperty] private string _errorText = "";

    public PushToTalkKey Key => SelectedKey.Value;
    public string KeyLabel => SelectedKey.Label.Replace(" (hold)", "");

    public SpeechModel Model => SpeechModels.Get(SelectedLanguage.Value, EffectivePrecision);

    private SpeechPrecision EffectivePrecision =>
        SelectedPrecision.Value ?? (SelectedDevice.Value == SpeechDevice.Cpu ? SpeechPrecision.Compact : SpeechPrecision.Full);

    public bool IsModelDownloaded => Model.IsDownloaded;
    public bool CanDictate => IsEnabled && IsModelDownloaded;

    public string ModelText => IsModelDownloaded
        ? $"Downloaded: Parakeet TDT 0.6B, {Model.Label}. Stored in {Model.Folder}."
        : $"Not downloaded yet: Parakeet TDT 0.6B, {Model.Label} (about {Model.ApproxBytes / 1024d / 1024 / 1024:0.0} GB).";

    public string MicHint => CanDictate
        ? $"Hold to talk (or hold {KeyLabel})"
        : IsEnabled ? "Download the speech model on the Voice page first" : "Voice input is off (Voice page)";

    public bool NeedsVcRuntime => RuntimeCheck.NeedsVcRuntime;
    public string VcRuntimeText => $"Microsoft Visual C++ runtime: {RuntimeCheck.InstalledVcRuntime?.ToString() ?? "not installed"} (the speech engine needs {RuntimeCheck.Required} or newer).";

    /// <summary>Raised when the page should open (the mic button when no model is there yet).</summary>
    public event Action? OpenRequested;

    public VoiceViewModel(IChatStore? store = null)
    {
        _store = store;
        _isEnabled = store?.GetSetting(EnabledKey) != "false";
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value.ToString() == store?.GetSetting(LanguageKey)) ?? Languages[0];
        _selectedDevice = Devices.FirstOrDefault(d => d.Value.ToString() == store?.GetSetting(DeviceKey)) ?? Devices[0];
        _selectedPrecision = Precisions.FirstOrDefault(p => (p.Value?.ToString() ?? "auto") == (store?.GetSetting(PrecisionKey) ?? "auto")) ?? Precisions[0];
        _gpuIndex = store?.GetSetting(GpuKey) ?? "0";
        _selectedKey = Keys.FirstOrDefault(k => k.Value.ToString() == store?.GetSetting(PttKey)) ?? Keys[0];
        _sendOnRelease = store?.GetSetting(SendKey) == "true";
        _removeFillers = store?.GetSetting(FillersKey) != "false";
        _spokenCommands = store?.GetSetting(CommandsKey) != "false";
        _useProjectWords = store?.GetSetting(ProjectWordsKey) != "false";
        RefreshMicrophones();
        var mic = store?.GetSetting(MicKey);
        _selectedMicrophone = Microphones.FirstOrDefault(m => m.Name == mic) ?? Microphones.FirstOrDefault();

        _meter = new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background, (_, _) => UpdateMeter(), _ui);
        _meter.Stop();
        InitSpeech(store);
        _loading = false;
        UpdateEngineText();
    }

    // ---- settings ----

    private void Save(string key, string value)
    {
        if (!_loading) _store?.SetSetting(key, value);
    }

    private void ModelSettingsChanged()
    {
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(IsModelDownloaded));
        OnPropertyChanged(nameof(CanDictate));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(MicHint));
        UpdateEngineText();
    }

    partial void OnIsEnabledChanged(bool value) { Save(EnabledKey, value ? "true" : "false"); ModelSettingsChanged(); }
    partial void OnSelectedLanguageChanged(VoiceChoice<SpeechLanguage> value) { Save(LanguageKey, value.Value.ToString()); ModelSettingsChanged(); }
    partial void OnSelectedDeviceChanged(VoiceChoice<SpeechDevice> value) { Save(DeviceKey, value.Value.ToString()); ModelSettingsChanged(); }
    partial void OnSelectedPrecisionChanged(VoiceChoice<SpeechPrecision?> value) { Save(PrecisionKey, value.Value?.ToString() ?? "auto"); ModelSettingsChanged(); }
    partial void OnGpuIndexChanged(string value) { Save(GpuKey, value.Trim()); ModelSettingsChanged(); }
    partial void OnSelectedMicrophoneChanged(Microphone? value) { if (value is not null) Save(MicKey, value.Name); }
    partial void OnSelectedKeyChanged(VoiceChoice<PushToTalkKey> value) { Save(PttKey, value.Value.ToString()); OnPropertyChanged(nameof(Key)); OnPropertyChanged(nameof(KeyLabel)); OnPropertyChanged(nameof(MicHint)); }
    partial void OnSendOnReleaseChanged(bool value) => Save(SendKey, value ? "true" : "false");
    partial void OnRemoveFillersChanged(bool value) => Save(FillersKey, value ? "true" : "false");
    partial void OnSpokenCommandsChanged(bool value) => Save(CommandsKey, value ? "true" : "false");
    partial void OnUseProjectWordsChanged(bool value) => Save(ProjectWordsKey, value ? "true" : "false");

    private int Gpu => int.TryParse(GpuIndex, out var g) && g >= 0 ? g : 0;
    private string LoadKey => $"{Model.Folder}|{SelectedDevice.Value}|{Gpu}";

    [RelayCommand]
    private void RefreshMicrophones()
    {
        var keep = SelectedMicrophone?.Name;
        try { Microphones = Microphone.List(); }
        catch (Exception ex) { AppLog.Write("Voice: listing microphones failed", ex); Microphones = [new Microphone(-1, "Default microphone")]; }
        OnPropertyChanged(nameof(Microphones));
        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Name == keep) ?? Microphones.FirstOrDefault();
    }

    private void UpdateEngineText()
    {
        if (IsLoading) { EngineText = "Loading the speech model…"; return; }
        EngineText = _recognizer is { } r && _loadedKey == LoadKey
            ? $"Ready: {r.Info.Provider}, {r.Info.Model}. Loaded in {r.Info.LoadTime.TotalSeconds:0.0} s."
            : IsModelDownloaded ? "The model loads when you first talk (or press Load now)." : "";
    }

    // ---- model download and loading ----

    [RelayCommand(AllowConcurrentExecutions = true)] // a second press stops the download
    private async Task DownloadAsync()
    {
        if (IsDownloading) { _download?.Cancel(); return; }
        var model = Model;
        _download = new CancellationTokenSource();
        IsDownloading = true;
        ErrorText = "";
        try
        {
            var progress = new Progress<(long Done, long Total, string File)>(p =>
            {
                DownloadPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                DownloadText = $"Downloading {p.File}: {p.Done / 1048576d:0} of {p.Total / 1048576d:0} MB";
            });
            await SpeechModels.DownloadAsync(model, progress, _download.Token);
            DownloadText = "Downloaded.";
            _ = PreloadAsync();
        }
        catch (OperationCanceledException)
        {
            DownloadText = "Download stopped. Press Download again to continue.";
        }
        catch (Exception ex)
        {
            DownloadText = "";
            ErrorText = "The download failed: " + ex.Message;
            AppLog.Write("Voice: model download failed", ex);
        }
        finally
        {
            IsDownloading = false;
            _download.Dispose();
            _download = null;
            ModelSettingsChanged();
        }
    }

    [RelayCommand]
    private void DeleteModel()
    {
        if (IsListening) return;
        Unload();
        try { SpeechModels.Delete(Model); }
        catch (Exception ex) { ErrorText = "Could not delete the model: " + ex.Message; }
        ModelSettingsChanged();
    }

    [RelayCommand]
    private void OpenModelFolder()
    {
        Directory.CreateDirectory(SpeechModels.Root);
        Process.Start(new ProcessStartInfo(SpeechModels.Root) { UseShellExecute = true });
    }

    [RelayCommand]
    private Task LoadNowAsync() => EnsureLoadedAsync();

    /// <summary>Loads the model in the background at start-up, so the first push-to-talk is instant.</summary>
    public async Task PreloadAsync()
    {
        if (!CanDictate) return;
        await EnsureLoadedAsync();
    }

    private void Unload()
    {
        var old = _recognizer;
        _recognizer = null;
        _loadTask = null;
        _loadedKey = "";
        old?.Dispose();
        UpdateEngineText();
    }

    private Task<ParakeetRecognizer?> EnsureLoadedAsync()
    {
        if (_recognizer is not null && _loadedKey == LoadKey) return Task.FromResult<ParakeetRecognizer?>(_recognizer);
        if (_loadTask is not null && _loadedKey == LoadKey) return _loadTask;
        if (_session is not null) return Task.FromResult(_recognizer); // never swap the model mid-sentence

        Unload();
        var model = Model;
        var device = SelectedDevice.Value;
        var gpu = Gpu;
        _loadedKey = LoadKey;
        IsLoading = true;
        ErrorText = "";
        UpdateEngineText();
        var key = _loadedKey;
        _loadTask = Task.Run(() => ParakeetRecognizer.Load(model, device, gpu)).ContinueWith(t =>
        {
            ParakeetRecognizer? r = null;
            _ui.Invoke(() =>
            {
                IsLoading = false;
                if (t.IsFaulted)
                {
                    var ex = t.Exception!.GetBaseException();
                    ErrorText = RuntimeCheck.Explain(ex);
                    AppLog.Write("Voice: loading the speech model failed", ex);
                    _loadedKey = "";
                    _loadTask = null;
                    OnPropertyChanged(nameof(NeedsVcRuntime));
                }
                else if (key == _loadedKey)
                {
                    r = _recognizer = t.Result;
                    AppLog.Write($"Voice: loaded {r.Info.Model} on {r.Info.Provider} in {r.Info.LoadTime.TotalSeconds:0.0} s");
                    if (r.GpuProblem is { } gpu) AppLog.Write("Voice: speech to text on the GPU failed, using the CPU: " + gpu);
                }
                else t.Result.Dispose(); // the settings changed while it loaded
                UpdateEngineText();
            });
            return r;
        }, TaskScheduler.Default);
        return _loadTask;
    }

    // ---- dictation ----

    public bool IsBusy => _session is not null || IsTranscribing;

    /// <summary>Starts recording for a target. Returns false (and says why) when it cannot.</summary>
    public async Task<bool> BeginAsync(IDictationTarget target)
    {
        StopSpeaking(); // talking cuts the reader off
        if (IsBusy) return false;
        if (!IsEnabled) return false;
        if (!IsModelDownloaded)
        {
            OpenRequested?.Invoke();
            return false;
        }
        _target = target;
        IsListening = true; // shows at once; the model may still be loading
        ListeningText = "Starting…";
        ErrorText = "";
        try
        {
            var recognizer = await EnsureLoadedAsync();
            if (recognizer is null)
            {
                ListeningText = ErrorText;
                Stop();
                return false;
            }
            if (_target != target || !IsListening) return false; // released while loading
            var polish = BuildPolish(target.ProjectFolder);
            _session = new DictationSession(recognizer, SelectedMicrophone?.Number ?? -1, polish);
            _session.Partial += text => _ui.BeginInvoke(() => { if (_target == target && IsListening) target.ShowLive(text); });
            _meter.Start();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = "The microphone could not start: " + ex.Message;
            AppLog.Write("Voice: starting dictation failed", ex);
            Stop();
            return false;
        }
    }

    /// <summary>Key or button released: finishes the text.</summary>
    public async Task EndAsync()
    {
        if (IsTranscribing) return; // already finishing
        var session = _session;
        var target = _target;
        if (session is null || target is null)
        {
            Stop(); // released before the microphone started (for example while the model loaded)
            target?.Finish("");
            return;
        }
        _session = null;
        _meter.Stop();
        IsListening = false;
        IsTranscribing = true;
        ListeningText = "Finishing…";
        try
        {
            var text = await session.FinishAsync();
            TimingText = $"Last transcription: {session.LastDecodeTime.TotalMilliseconds:0} ms for {session.Elapsed.TotalSeconds:0.0} s of speech.";
            target.Finish(text);
            if (session.LiveError is { } live) AppLog.Write("Voice: live text failed: " + live);
        }
        catch (Exception ex)
        {
            ErrorText = "Transcription failed: " + RuntimeCheck.Explain(ex);
            AppLog.Write("Voice: transcription failed", ex);
            target.Finish("");
        }
        finally
        {
            await session.DisposeAsync();
            IsTranscribing = false;
            ListeningText = "";
            _target = null;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>Drops the recording without text (the key was part of a shortcut).</summary>
    public async Task CancelAsync()
    {
        var session = _session;
        var target = _target;
        Stop();
        target?.Finish("");
        if (session is not null)
        {
            await session.CancelAsync();
            await session.DisposeAsync();
        }
    }

    private void Stop()
    {
        _session = null;
        _target = null;
        _meter.Stop();
        IsListening = false;
        Level = 0;
        if (!IsTranscribing) ListeningText = "";
        OnPropertyChanged(nameof(IsBusy));
    }

    private void UpdateMeter()
    {
        if (_session is not { } s) return;
        Level = Math.Min(1, s.Level * 1.6);
        ListeningText = $"Listening… {s.Elapsed:m\\:ss}";
    }

    private Func<string, string> BuildPolish(string? folder)
    {
        Task<ProjectVocabulary>? vocabulary = null;
        if (UseProjectWords && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            if (!_vocabularies.TryGetValue(folder, out var cached) || DateTime.UtcNow - cached.At > TimeSpan.FromMinutes(10))
            {
                cached = (DateTime.UtcNow, Task.Run(() => ProjectVocabulary.FromFolder(folder)));
                _vocabularies[folder] = cached;
            }
            vocabulary = cached.Vocabulary;
        }
        var fillers = RemoveFillers;
        var commands = SpokenCommands;
        // The project names are used once they are read (a moment after the first talk in a project).
        return text => TextPolisher.Polish(text, new PolishOptions(fillers, commands,
            vocabulary is { IsCompletedSuccessfully: true } v ? v.Result : null));
    }

    // ---- the Voice page's own test ----

    private sealed class TestTarget(VoiceViewModel vm) : IDictationTarget
    {
        public string? ProjectFolder => null;
        public void ShowLive(string text) => vm.TestText = text;
        public void Finish(string text) { if (text.Length > 0) vm.TestText = text; }
    }

    public void BeginTest() { TestText = ""; _ = BeginAsync(new TestTarget(this)); }
    public void EndTest() => _ = EndAsync();

    // ---- Visual C++ runtime ----

    [RelayCommand]
    private async Task InstallVcRuntimeAsync()
    {
        var setup = RuntimeCheck.BundledRedistributable;
        if (!File.Exists(setup))
        {
            Process.Start(new ProcessStartInfo("https://aka.ms/vs/17/release/vc_redist.x64.exe") { UseShellExecute = true });
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(setup, "/install /passive /norestart") { UseShellExecute = true, Verb = "runas" });
            if (p is not null) await p.WaitForExitAsync();
            OnPropertyChanged(nameof(NeedsVcRuntime));
            OnPropertyChanged(nameof(VcRuntimeText));
            ErrorText = RuntimeCheck.NeedsVcRuntime ? "The Visual C++ runtime was not updated." : "Installed. Restart AgentFlow to use voice input.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ErrorText = "The Visual C++ runtime install was cancelled.";
        }
    }
}
