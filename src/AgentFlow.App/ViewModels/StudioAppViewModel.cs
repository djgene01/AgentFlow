using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AgentFlow.App.Services;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Studios;
using AgentFlow.Core.Video;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>
/// The page for a desktop studio with an MCP server (Pixel Art Studio, SfxForge). When it is on, every Claude Code
/// turn AgentFlow runs gets the studio's MCP server, so Claude can use it from any project.
/// </summary>
public sealed partial class StudioAppViewModel : ObservableObject, IMcpIntegrations
{
    private readonly IChatStore? _store;
    private readonly IDialogService _dialogs;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private volatile McpServerSpec? _server; // read by turns on background threads
    private volatile bool _serverUp;          // HTTP studios: the app's server answered before this turn
    private bool _loading = true;

    public StudioApp App { get; }

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private bool _autoAllowSafe = true;
    [ObservableProperty] private bool _autoStart = true;

    [ObservableProperty] private bool _isInstallFound;
    [ObservableProperty] private string _installText = "";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testText = "";
    [ObservableProperty] private bool _testPassed;
    [ObservableProperty] private string _openText = "";

    public ObservableCollection<string> Tools { get; } = [];

    public string Title => App.DisplayName;
    public string Blurb => App.Blurb;
    public string ServerName => App.ServerName;
    public string InstallMark => IsInstallFound ? "✓" : "✗";
    public string SafeToolsText => string.Join(", ", App.SafeTools);
    public string AskToolsText => string.Join(", ", App.AskTools);
    public bool IsInApp => App.Transport == StudioTransport.HttpInApp;
    public bool HasBuiltInCopy => App.Detect(App.BuiltInFolder, out _) is not null;
    public bool HasInstalledCopy => App.Detect(App.InstalledFolder, out _) is not null;
    public string InstalledFolder => App.InstalledFolder;

    public string TryIt => App.Key == StudioApps.PixelArtStudio.Key
        ? "In any project chat: \"Draw a 32x32 knight sprite with a 4-frame walk cycle and export it as a GIF to this project's assets folder.\" " +
          "Watch it appear in the Pixel Art Studio window; Ctrl+Z there undoes Claude's steps one by one."
        : "In any project chat: \"Design a coin pickup and a laser sound for this game, play them for me, and export both as .wav files " +
          "to this project's sounds folder.\" Open SfxForge to see the waveforms move while Claude works.";

    public string HowItRuns => IsInApp
        ? $"Its MCP server runs inside the {App.DisplayName} window at {App.McpUrl(PortNumber)}, so the app has to be open for Claude to use it."
        : $"AgentFlow starts {App.McpExe} for each turn. It drives the {App.DisplayName} window when that is open, and works without it when it is closed.";

    public string StatusText => !IsEnabled ? $"Off: Claude does not see {App.DisplayName}."
        : !IsInstallFound ? $"On, but {App.DisplayName} was not found yet."
        : $"On: every Claude Code turn gets the {ServerName} MCP server.";

    partial void OnIsInstallFoundChanged(bool value) => OnPropertyChanged(nameof(InstallMark));

    private string Key(string name) => $"{App.Key}.{name}";

    public StudioAppViewModel(StudioApp app, IDialogService dialogs, IChatStore? store = null)
    {
        App = app;
        _dialogs = dialogs;
        _store = store;
        // A saved folder that still works, else the copy installed with AgentFlow, else the studio's own install.
        var saved = store?.GetSetting(Key("folder"));
        _folder = app.Candidates(saved).FirstOrDefault(f => app.Detect(f, out _) is not null) ?? saved ?? "";
        // On by default once the studio is there; turning it off is remembered.
        var enabled = store?.GetSetting(Key("enabled"));
        _isEnabled = enabled == "true" || (enabled is null && app.Detect(_folder, out _) is not null);
        _port = store?.GetSetting(Key("port")) ?? app.DefaultPort.ToString();
        _autoAllowSafe = store?.GetSetting(Key("autoAllow")) != "false";
        _autoStart = store?.GetSetting(Key("autoStart")) != "false";
        _loading = false;
        Update();
    }

    // ---- IMcpIntegrations ----

    public IReadOnlyList<McpServerSpec> Servers() =>
        _server is { } s && (!IsInApp || _serverUp) ? [s] : [];

    public IReadOnlyList<string> AllowedTools() =>
        AutoAllowSafe ? App.SafeTools.Select(App.ToolRule).ToList() : [];

    public bool MustAsk(string toolName) => false;

    /// <summary>
    /// Pixel Art Studio's server lives in its window: when it is not answering, start the app (if allowed) and wait
    /// for it, so the server is there when Claude Code connects. Otherwise it is left out of this turn.
    /// </summary>
    public async Task PrepareAsync(CancellationToken ct)
    {
        // Each path sets _serverUp once at the end, so a turn starting in another chat never sees it reset midway.
        if (!IsInApp || _server is null || App.Detect(Folder, out _) is not { } install) return;
        var port = PortNumber;
        if (await StudioApps.IsListeningAsync(port, TimeSpan.FromMilliseconds(500), ct)) { _serverUp = true; return; }
        if (!AutoStart) { _serverUp = false; return; }

        await _starting.WaitAsync(ct);
        try
        {
            // Another chat may have started it while this one waited.
            if (!await StudioApps.IsListeningAsync(port, TimeSpan.FromMilliseconds(500), ct))
            {
                AppLog.Write($"{App.DisplayName}: starting it for a Claude Code turn.");
                App.Launch(install, port);
            }
            _serverUp = await StudioApps.WaitForPortAsync(port, TimeSpan.FromSeconds(45), ct);
            if (!_serverUp) AppLog.Write($"{App.DisplayName}: its MCP server did not answer on port {port} within 45 seconds; left out of this turn.");
        }
        finally
        {
            _starting.Release();
        }
    }

    // ---- state ----

    private int PortNumber => App.ParsePort(Port);

    private void Update()
    {
        var install = App.Detect(Folder, out var problem);
        IsInstallFound = install is not null;
        InstallText = install is null ? problem
            : App.IsBuiltIn(install.Folder) ? $"The copy installed with AgentFlow. Nothing else to install."
            : $"{App.DisplayName} in {install.Folder}";
        _server = IsEnabled && install is not null ? App.ToMcpServer(install, PortNumber) : null;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HowItRuns));
    }

    private void Save(string key, string value)
    {
        if (!_loading) _store?.SetSetting(Key(key), value);
    }

    partial void OnIsEnabledChanged(bool value) { Save("enabled", value ? "true" : "false"); Update(); }
    partial void OnFolderChanged(string value) { Save("folder", value.Trim()); TestText = ""; Tools.Clear(); Update(); }
    partial void OnPortChanged(string value) { Save("port", value.Trim()); Update(); }
    partial void OnAutoAllowSafeChanged(bool value) => Save("autoAllow", value ? "true" : "false");
    partial void OnAutoStartChanged(bool value) => Save("autoStart", value ? "true" : "false");

    // ---- commands ----

    [RelayCommand]
    private void BrowseFolder()
    {
        if (_dialogs.PickFolder() is { } f) Folder = f;
    }

    [RelayCommand]
    private void UseBuiltInCopy()
    {
        Folder = App.BuiltInFolder;
        Port = App.DefaultPort.ToString();
    }

    [RelayCommand]
    private void UseInstalledCopy() => Folder = App.InstalledFolder;

    /// <summary>Connects to the studio's MCP server the way Claude Code will, and lists its tools.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        var install = App.Detect(Folder, out var problem);
        if (install is null)
        {
            TestPassed = false;
            TestText = problem;
            return;
        }
        IsTesting = true;
        Tools.Clear();
        try
        {
            McpProbeResult r;
            if (IsInApp)
            {
                TestText = $"Starting {App.DisplayName} if it is not open…";
                if (await EnsureOpenAsync(install) is { } notOpen)
                {
                    TestPassed = false;
                    TestText = notOpen;
                    return;
                }
                TestText = "Asking its MCP server for its tools…";
                r = await StudioApps.ProbeHttpAsync(App.McpUrl(PortNumber), TimeSpan.FromSeconds(15));
            }
            else
            {
                TestText = $"Starting {App.McpExe}…";
                r = await VideoStudio.ProbeAsync(App.ToMcpServer(install, PortNumber), install.Folder, TimeSpan.FromSeconds(20));
            }
            TestPassed = r.Ok;
            if (!r.Ok)
            {
                TestText = "The MCP server did not answer: " + r.Error;
                AppLog.Write($"{App.DisplayName}: MCP test failed: {r.Error}");
                return;
            }
            foreach (var t in r.Tools) Tools.Add(App.ToolRule(t));
            var unknown = r.Tools.Where(t => !App.SafeTools.Contains(t) && !App.AskTools.Contains(t)).ToList();
            TestText = $"Works: {r.ServerName} {r.Version} offers {r.Tools.Count} tools." +
                       (unknown.Count > 0 ? $" New tools that will ask first: {string.Join(", ", unknown)}." : "");
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Opens the studio's window (for Pixel Art Studio this also starts its MCP server).</summary>
    [RelayCommand]
    private async Task OpenAppAsync()
    {
        if (App.Detect(Folder, out var problem) is not { } install)
        {
            OpenText = problem;
            return;
        }
        OpenText = $"Opening {App.DisplayName}…";
        OpenText = await EnsureOpenAsync(install) ?? "";
    }

    /// <summary>Starts the app unless its port already answers. Returns null when it is up, else the problem.</summary>
    private async Task<string?> EnsureOpenAsync(StudioInstall install)
    {
        var port = PortNumber;
        if (await StudioApps.IsListeningAsync(port, TimeSpan.FromMilliseconds(500))) return null;
        await _starting.WaitAsync();
        try
        {
            if (!await StudioApps.IsListeningAsync(port, TimeSpan.FromMilliseconds(500))) App.Launch(install, port);
            return await StudioApps.WaitForPortAsync(port, TimeSpan.FromSeconds(45)) ? null
                : $"{App.DisplayName} did not open its server on port {port} within 45 seconds. If another copy is already open on that port, close it or choose another port.";
        }
        catch (Exception ex)
        {
            return $"Could not start {App.DisplayName}: {ex.Message}";
        }
        finally
        {
            _starting.Release();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(Folder)) Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
    }
}
