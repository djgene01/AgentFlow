using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AgentFlow.App.Services;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Video;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>
/// The "Video studio" page. When it is on, every Claude Code turn AgentFlow runs gets the
/// VMCP-Studio MCP server, so Claude can plan, render and list videos from any project.
/// </summary>
public sealed partial class VideoStudioViewModel : ObservableObject, IMcpIntegrations
{
    private const string EnabledKey = "video.enabled", FolderKey = "video.folder", PortKey = "video.port",
        DataKey = "video.data", AutoAllowKey = "video.autoAllow";

    private readonly IChatStore? _store;
    private readonly IDialogService _dialogs;
    private volatile McpServerSpec? _server; // read by turns on background threads
    private bool _loading = true;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _port = VideoStudio.DefaultPort.ToString();
    [ObservableProperty] private string _dataFolder = "";
    [ObservableProperty] private bool _autoAllowSafe = true;

    [ObservableProperty] private bool _isInstallFound;
    [ObservableProperty] private string _installText = "";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testText = "";
    [ObservableProperty] private bool _testPassed;

    public ObservableCollection<string> Tools { get; } = [];

    public string InstallMark => IsInstallFound ? "✓" : "✗";

    partial void OnIsInstallFoundChanged(bool value) => OnPropertyChanged(nameof(InstallMark));

    public string ServerName => VideoStudio.ServerName;
    public bool HasBundledCopy => Directory.Exists(VideoStudio.BundledFolder);
    public string BundledFolder => VideoStudio.BundledFolder;
    public string SafeToolsText => string.Join(", ", VideoStudio.SafeTools);
    public string PaidToolsText => string.Join(", ", VideoStudio.PaidTools);
    public string AskToolsText => string.Join(", ", VideoStudio.AskTools);

    public string StatusText => !IsEnabled ? "Off: Claude does not see the video studio."
        : !IsInstallFound ? "On, but the studio folder is not usable yet."
        : $"On: every Claude Code turn gets the {ServerName} MCP server.";

    public VideoStudioViewModel(IDialogService dialogs, IChatStore? store = null)
    {
        _dialogs = dialogs;
        _store = store;
        // A saved folder that still works, else the copy installed with AgentFlow, else VMCP-Studio.exe's copy.
        var saved = store?.GetSetting(FolderKey);
        _folder = VideoStudio.Candidates(saved).FirstOrDefault(f => VideoStudio.Detect(f, out _) is not null) ?? saved ?? "";
        // On by default once a usable studio is there; turning it off is remembered.
        var enabled = store?.GetSetting(EnabledKey);
        _isEnabled = enabled == "true" || (enabled is null && VideoStudio.Detect(_folder, out _) is not null);
        _port = store?.GetSetting(PortKey) ?? DefaultPortFor(_folder).ToString();
        _dataFolder = store?.GetSetting(DataKey) ?? "";
        _autoAllowSafe = store?.GetSetting(AutoAllowKey) != "false";
        _loading = false;
        Update();
    }

    private static int DefaultPortFor(string folder) => VideoStudio.IsBuiltIn(folder) ? VideoStudio.BuiltInPort : VideoStudio.DefaultPort;

    public bool HasBuiltInCopy => VideoStudio.Detect(VideoStudio.BuiltInFolder, out _) is not null;
    public string BuiltInFolder => VideoStudio.BuiltInFolder;

    /// <summary>The library the studio uses: the chosen folder, else the built-in copy's own one, else the studio's default.</summary>
    private string? EffectiveDataFolder =>
        !string.IsNullOrWhiteSpace(DataFolder) ? DataFolder.Trim()
        : VideoStudio.IsBuiltIn(Folder) ? VideoStudio.BuiltInDataFolder
        : null;

    public string DataHint => EffectiveDataFolder is { } d && string.IsNullOrWhiteSpace(DataFolder)
        ? $"Empty: {d}" : "Empty: the studio's own data folder";

    // ---- IMcpIntegrations ----

    public IReadOnlyList<McpServerSpec> Servers() => _server is { } s ? [s] : [];

    public IReadOnlyList<string> AllowedTools() =>
        AutoAllowSafe ? VideoStudio.SafeTools.Select(VideoStudio.ToolRule).ToList() : [];

    public bool MustAsk(string toolName) =>
        VideoStudio.PaidTools.Any(t => string.Equals(VideoStudio.ToolRule(t), toolName, StringComparison.Ordinal));

    // ---- state ----

    private int PortNumber => int.TryParse(Port, out var p) && p is > 0 and < 65536 ? p : VideoStudio.DefaultPort;

    private void Update()
    {
        var install = VideoStudio.Detect(Folder, out var problem);
        IsInstallFound = install is not null;
        InstallText = install is null ? problem
            : VideoStudio.IsBuiltIn(install.Folder) ? "The studio installed with AgentFlow, with its own Node. Nothing else to install."
            : install.IsBundled ? $"VMCP-Studio.exe's copy, with its own Node: {install.Node}"
            : $"Source folder, run with Node from {install.Node}";
        _server = IsEnabled && install is not null ? VideoStudio.ToMcpServer(install, PortNumber, EffectiveDataFolder) : null;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DataHint));
    }

    private void Save(string key, string value)
    {
        if (!_loading) _store?.SetSetting(key, value);
    }

    partial void OnIsEnabledChanged(bool value)
    {
        Save(EnabledKey, value ? "true" : "false");
        Update();
        if (value && !_loading) _ = WarmUpAsync();
    }
    partial void OnFolderChanged(string value) { Save(FolderKey, value.Trim()); TestText = ""; Tools.Clear(); Update(); }
    partial void OnPortChanged(string value) { Save(PortKey, value.Trim()); Update(); }
    partial void OnDataFolderChanged(string value) { Save(DataKey, value.Trim()); Update(); }
    partial void OnAutoAllowSafeChanged(bool value) => Save(AutoAllowKey, value ? "true" : "false");

    // ---- commands ----

    [RelayCommand]
    private void BrowseFolder()
    {
        if (_dialogs.PickFolder() is { } f) Folder = f;
    }

    [RelayCommand]
    private void UseBundledCopy() => Folder = VideoStudio.BundledFolder;

    [RelayCommand]
    private void UseBuiltInCopy()
    {
        Folder = VideoStudio.BuiltInFolder;
        Port = VideoStudio.BuiltInPort.ToString();
    }

    [RelayCommand]
    private void BrowseDataFolder()
    {
        if (_dialogs.PickFolder() is { } f) DataFolder = f;
    }

    /// <summary>Starts the studio's MCP server the way Claude Code will, and lists its tools.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        var install = VideoStudio.Detect(Folder, out var problem);
        if (install is null)
        {
            TestPassed = false;
            TestText = problem;
            return;
        }
        IsTesting = true;
        TestText = "Starting the studio's MCP server…";
        Tools.Clear();
        try
        {
            var spec = VideoStudio.ToMcpServer(install, PortNumber, EffectiveDataFolder);
            var r = await VideoStudio.ProbeAsync(spec, install.Folder, TimeSpan.FromSeconds(20));
            TestPassed = r.Ok;
            if (!r.Ok)
            {
                TestText = "The MCP server did not start: " + r.Error;
                AppLog.Write("Video studio: MCP test failed: " + r.Error);
                return;
            }
            foreach (var t in r.Tools) Tools.Add(VideoStudio.ToolRule(t));
            TestText = $"Works: {r.ServerName} {r.Version} offers {r.Tools.Count} tools." +
                       (r.Instructions is { Length: > 0 } ? " It sends Claude its own instructions on how to use the studio." : "");
        }
        finally
        {
            IsTesting = false;
        }
    }

    [ObservableProperty] private string _openText = "";

    /// <summary>Opens the studio's web app (library and live progress), starting its server first if needed.</summary>
    [RelayCommand]
    private async Task OpenStudioAsync()
    {
        OpenText = "Starting the studio…";
        if (await EnsureRunningAsync(TimeSpan.FromSeconds(60)) is { } problem)
        {
            OpenText = problem;
            return;
        }
        OpenText = "";
        Process.Start(new ProcessStartInfo($"http://localhost:{PortNumber}") { UseShellExecute = true });
    }

    /// <summary>
    /// Starts the studio's web server in the background when AgentFlow opens, so it is ready before Claude's
    /// first video tool call (a first start on a new library can take longer than the MCP server waits).
    /// </summary>
    public async Task WarmUpAsync()
    {
        if (!IsEnabled || !IsInstallFound) return;
        try
        {
            if (await EnsureRunningAsync(TimeSpan.FromSeconds(90)) is { } problem) AppLog.Write("Video studio: " + problem);
        }
        catch (Exception ex)
        {
            AppLog.Write("Video studio: warm-up failed: " + ex.Message);
        }
    }

    /// <summary>Starts the web server if it is not answering yet. Returns null when it runs, else the problem.</summary>
    private async Task<string?> EnsureRunningAsync(TimeSpan timeout)
    {
        var port = PortNumber;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
        async Task<bool> Healthy()
        {
            try { return (await http.GetAsync($"http://127.0.0.1:{port}/api/health")).IsSuccessStatusCode; }
            catch { return false; }
        }

        if (await Healthy()) return null;
        if (VideoStudio.Detect(Folder, out var problem) is not { } install) return problem;
        var psi = new ProcessStartInfo(install.Node) { WorkingDirectory = install.Folder, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(Path.Combine(install.Folder, "server", "index.js"));
        psi.Environment["VMCP_PORT"] = port.ToString();
        if (EffectiveDataFolder is { } data) psi.Environment["VMCP_DATA"] = data;
        // Not in AgentFlow's job: the studio keeps running after AgentFlow closes, as when Claude starts it.
        Process.Start(psi)?.Dispose();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            if (await Healthy()) return null;
        }
        return $"The studio did not start on port {port} within {timeout.TotalSeconds:0} seconds. Run \"npm start\" in {install.Folder} to see why.";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(Folder)) Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
    }
}
