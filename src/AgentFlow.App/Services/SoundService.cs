using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AgentFlow.App.ViewModels;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Sounds;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.Services;

/// <summary>One row on the settings page: a cue you can turn off or preview.</summary>
public sealed partial class SoundCueItemViewModel(SoundCueInfo info, bool isEnabled, Action<SoundCueItemViewModel> changed, Action<SoundCue> preview) : ObservableObject
{
    public SoundCue Cue => info.Cue;
    public string Label => info.Label;
    public string Description => info.Description;

    [ObservableProperty] private bool _isEnabled = isEnabled;

    partial void OnIsEnabledChanged(bool value) => changed(this);

    [RelayCommand]
    private void Preview() => preview(Cue);
}

/// <summary>
/// Plays AgentFlow's synthesized sound effects for chat and git events. A pending permission
/// request pings again every few seconds until you answer it; a stage waiting at a gate reminds
/// you every 30 seconds. Sounds are rendered once into WAV files under %LOCALAPPDATA%\AgentFlow.
/// </summary>
public sealed partial class SoundService : ObservableObject
{
    private const string EnabledKey = "sounds.enabled", VolumeKey = "sounds.volume", RepeatKey = "sounds.repeat",
        RemindKey = "sounds.remindReviews", OffKey = "sounds.off";
    private static readonly TimeSpan ReviewReminder = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Overlap = TimeSpan.FromMilliseconds(350);

    private readonly IChatStore? _store;
    private readonly string _folder;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly List<MediaPlayer> _playing = [];
    private readonly Dictionary<Guid, (Func<bool> StillPending, bool IsGate)> _waiting = [];
    private readonly HashSet<SoundCue> _off = [];
    private DateTime _lastPlayed = DateTime.MinValue, _lastPermissionPing, _lastReviewPing;
    private int _lastPriority;
    private Task? _rendering;

    public static IReadOnlyList<Choice> RepeatChoices { get; } =
    [
        new("3", "Every 3 seconds"),
        new("5", "Every 5 seconds"),
        new("10", "Every 10 seconds"),
        new("20", "Every 20 seconds"),
        new("0", "Don't repeat"),
    ];

    public IReadOnlyList<Choice> RepeatOptions => RepeatChoices;
    public ObservableCollection<SoundCueItemViewModel> Cues { get; } = [];

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private double _volume = 70;
    [ObservableProperty] private Choice _selectedRepeat = RepeatChoices[1];
    [ObservableProperty] private bool _remindReviews = true;

    public SoundService(IChatStore? store = null, string? folder = null)
    {
        _store = store;
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentFlow", "Sounds", $"v{SoundSynth.Version}");
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
        _timer.Stop();

        _isEnabled = store?.GetSetting(EnabledKey) != "false";
        _volume = double.TryParse(store?.GetSetting(VolumeKey), System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 100) : 70;
        _selectedRepeat = Choices.Find(RepeatChoices, store?.GetSetting(RepeatKey) ?? "5");
        _remindReviews = store?.GetSetting(RemindKey) != "false";
        foreach (var name in (store?.GetSetting(OffKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<SoundCue>(name, out var c)) _off.Add(c);

        foreach (var info in SoundCues.All)
            Cues.Add(new SoundCueItemViewModel(info, !_off.Contains(info.Cue), OnCueToggled, c => Play(c, preview: true)));
    }

    /// <summary>Renders any missing WAV files in the background. Safe to call more than once.</summary>
    public Task PrepareAsync() => _rendering ??= Task.Run(() =>
    {
        try
        {
            Directory.CreateDirectory(_folder);
            foreach (var cue in Enum.GetValues<SoundCue>()) EnsureFile(cue);
        }
        catch (Exception ex)
        {
            AppLog.Write("Sounds: could not prepare the sound files", ex);
        }
    });

    /// <summary>Plays sounds for this chat's events and keeps pinging while it waits for you.</summary>
    public void Watch(ProjectChat chat)
    {
        chat.EntryAdded += e => OnEntry(chat, e, added: true);
        chat.EntryUpdated += e => OnEntry(chat, e, added: false);
    }

    private void OnEntry(ProjectChat chat, ChatEntry e, bool added)
    {
        // Runs on the chat's thread: read the state now, play on the UI thread.
        var cue = SoundCues.ForEntry(e.Data, added, chat.ActiveRun is not null);
        if (cue is null) return;
        Func<bool>? pending = cue switch
        {
            SoundCue.PermissionRequest => () => chat.IsApprovalPending(e.Id),
            SoundCue.ReviewWaiting => () => chat.IsGatePending(e.Id),
            _ => null,
        };
        _dispatcher.BeginInvoke(() =>
        {
            if (pending is not null)
            {
                _waiting[e.Id] = (pending, cue == SoundCue.ReviewWaiting);
                if (cue == SoundCue.PermissionRequest) _lastPermissionPing = DateTime.UtcNow;
                else _lastReviewPing = DateTime.UtcNow;
                _timer.Start();
            }
            Play(cue.Value);
        });
    }

    /// <summary>Called by the git page after an action.</summary>
    public void PlayGit(bool ok) => _dispatcher.BeginInvoke(() => Play(ok ? SoundCue.GitSuccess : SoundCue.GitFailed));

    private void Tick()
    {
        foreach (var id in _waiting.Where(w => !w.Value.StillPending()).Select(w => w.Key).ToList())
            _waiting.Remove(id);
        if (_waiting.Count == 0)
        {
            _timer.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var repeat = int.TryParse(SelectedRepeat.Value, out var s) ? s : 0;
        if (repeat > 0 && _waiting.Values.Any(w => !w.IsGate) && now - _lastPermissionPing >= TimeSpan.FromSeconds(repeat))
        {
            _lastPermissionPing = now;
            Play(SoundCue.PermissionRequest);
        }
        else if (RemindReviews && _waiting.Values.Any(w => w.IsGate) && now - _lastReviewPing >= ReviewReminder)
        {
            _lastReviewPing = now;
            Play(SoundCue.ReviewWaiting);
        }
    }

    /// <summary>Plays a cue on the UI thread. Two cues close together: the more important one wins.</summary>
    public void Play(SoundCue cue, bool preview = false)
    {
        if (!preview && (!IsEnabled || _off.Contains(cue))) return;
        var priority = SoundCues.Info(cue).Priority;
        var now = DateTime.UtcNow;
        if (!preview && now - _lastPlayed < Overlap)
        {
            if (priority <= _lastPriority) return;
            StopAll();
        }
        _lastPlayed = now;
        _lastPriority = priority;

        try
        {
            var path = EnsureFile(cue);
            var player = new MediaPlayer { Volume = Math.Pow(Volume / 100.0, 2) }; // squared: the slider feels even
            player.MediaEnded += (_, _) => Close(player);
            player.MediaFailed += (_, a) =>
            {
                Close(player);
                AppLog.Write($"Sounds: could not play {cue}", a.ErrorException);
                try { new System.Media.SoundPlayer(path).Play(); } catch { /* no audio device */ }
            };
            _playing.Add(player);
            player.Open(new Uri(path));
            player.Play();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Sounds: could not play {cue}", ex);
        }
    }

    private void Close(MediaPlayer p)
    {
        p.Close();
        _playing.Remove(p);
    }

    private void StopAll()
    {
        foreach (var p in _playing.ToList()) { p.Stop(); Close(p); }
    }

    private readonly object _fileLock = new();

    private string EnsureFile(SoundCue cue)
    {
        var path = Path.Combine(_folder, cue + ".wav");
        lock (_fileLock)
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(_folder);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, SoundSynth.RenderWav(cue));
            File.Move(tmp, path, overwrite: true);
        }
        return path;
    }

    // ---- settings ----

    partial void OnIsEnabledChanged(bool value)
    {
        _store?.SetSetting(EnabledKey, value ? "true" : "false");
        if (!value) StopAll();
    }

    partial void OnVolumeChanged(double value) =>
        _store?.SetSetting(VolumeKey, Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture));

    partial void OnSelectedRepeatChanged(Choice value) => _store?.SetSetting(RepeatKey, value.Value ?? "5");

    partial void OnRemindReviewsChanged(bool value) => _store?.SetSetting(RemindKey, value ? "true" : "false");

    private void OnCueToggled(SoundCueItemViewModel item)
    {
        if (item.IsEnabled) _off.Remove(item.Cue); else _off.Add(item.Cue);
        _store?.SetSetting(OffKey, string.Join(',', _off));
    }

    [RelayCommand]
    private void TestPermissionPing() => Play(SoundCue.PermissionRequest, preview: true);
}
