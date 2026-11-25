using System.Diagnostics;
using AgentFlow.ClaudeCli;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.App.ViewModels;

/// <summary>
/// Shows whether the claude CLI is installed and signed in. Sign-in always happens in Anthropic's
/// own flow, in a terminal the user controls; the app never sees or stores credentials.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    [ObservableProperty] private string _cliPath = "Checking…";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _signInText = "";
    [ObservableProperty] private bool _isCliFound;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _isChecking;

    public string DatabasePath { get; }

    public bool IsReady => IsCliFound && IsSignedIn;

    public const string ApprovalsSettingKey = "approvals";

    private readonly AgentFlow.Core.Chat.IChatStore? _store;

    /// <summary>What happens to permission prompts: ask in the chat, deny, or allow during workflow runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsk), nameof(IsDeny), nameof(IsAllowInWorkflows), nameof(IsAllowEverywhere), nameof(IsAutoAllow))]
    private AgentFlow.Core.Agents.ApprovalPolicy _approvalPolicy = AgentFlow.Core.Agents.ApprovalPolicy.Ask;

    // One property per radio button; setting one to true picks that policy.
    public bool IsAsk
    {
        get => ApprovalPolicy == AgentFlow.Core.Agents.ApprovalPolicy.Ask;
        set { if (value) ApprovalPolicy = AgentFlow.Core.Agents.ApprovalPolicy.Ask; }
    }

    public bool IsDeny
    {
        get => ApprovalPolicy == AgentFlow.Core.Agents.ApprovalPolicy.Deny;
        set { if (value) ApprovalPolicy = AgentFlow.Core.Agents.ApprovalPolicy.Deny; }
    }

    public bool IsAllowInWorkflows
    {
        get => ApprovalPolicy == AgentFlow.Core.Agents.ApprovalPolicy.AllowInWorkflows;
        set { if (value) ApprovalPolicy = AgentFlow.Core.Agents.ApprovalPolicy.AllowInWorkflows; }
    }

    public bool IsAllowEverywhere
    {
        get => ApprovalPolicy == AgentFlow.Core.Agents.ApprovalPolicy.AllowEverywhere;
        set { if (value) ApprovalPolicy = AgentFlow.Core.Agents.ApprovalPolicy.AllowEverywhere; }
    }

    /// <summary>Either auto-approve choice: shows the caution note.</summary>
    public bool IsAutoAllow => IsAllowInWorkflows || IsAllowEverywhere;

    /// <summary>Whether the app's permission server started; without it prompts are always denied.</summary>
    [ObservableProperty] private string _approvalServerText = "";

    public DiagnosticsViewModel(string databasePath, AgentFlow.Core.Chat.IChatStore? store = null, string? approvalServerText = null)
    {
        DatabasePath = databasePath;
        _store = store;
        _approvalPolicy = store?.GetSetting(ApprovalsSettingKey) switch
        {
            "deny" => AgentFlow.Core.Agents.ApprovalPolicy.Deny,
            "allow-workflows" => AgentFlow.Core.Agents.ApprovalPolicy.AllowInWorkflows,
            "allow-all" => AgentFlow.Core.Agents.ApprovalPolicy.AllowEverywhere,
            _ => AgentFlow.Core.Agents.ApprovalPolicy.Ask,
        };
        _approvalServerText = approvalServerText ?? "";
    }

    partial void OnApprovalPolicyChanged(AgentFlow.Core.Agents.ApprovalPolicy value) =>
        _store?.SetSetting(ApprovalsSettingKey, value switch
        {
            AgentFlow.Core.Agents.ApprovalPolicy.Deny => "deny",
            AgentFlow.Core.Agents.ApprovalPolicy.AllowInWorkflows => "allow-workflows",
            AgentFlow.Core.Agents.ApprovalPolicy.AllowEverywhere => "allow-all",
            _ => "ask",
        });

    // ---- appearance ----

    public const string ThemeSettingKey = "theme";

    public static IReadOnlyList<Choice> Themes { get; } =
    [
        new("system", "Match Windows"),
        new("light", "Light"),
        new("dark", "Dark"),
    ];

    [ObservableProperty] private Choice _selectedTheme = Themes[0];

    public IReadOnlyList<Choice> ThemeChoices => Themes;

    /// <summary>Sound settings; null in tools that run without audio.</summary>
    public AgentFlow.App.Services.SoundService? Sounds { get; set; }

    public string LogFolder => AgentFlow.App.Services.AppLog.Folder;

    /// <summary>Reads the saved theme and applies it. Called once at startup.</summary>
    public void LoadTheme()
    {
        SelectedTheme = Choices.Find(Themes, _store?.GetSetting(ThemeSettingKey) ?? "system");
        ApplyTheme(SelectedTheme.Value);
    }

    partial void OnSelectedThemeChanged(Choice value)
    {
        _store?.SetSetting(ThemeSettingKey, value.Value);
        ApplyTheme(value.Value);
    }

#pragma warning disable WPF0001 // ThemeMode is marked experimental in WPF
    public static void ApplyTheme(string? theme)
    {
        if (System.Windows.Application.Current is not { } app) return;
        app.ThemeMode = theme switch
        {
            "light" => System.Windows.ThemeMode.Light,
            "dark" => System.Windows.ThemeMode.Dark,
            _ => System.Windows.ThemeMode.System,
        };
    }
#pragma warning restore WPF0001

    [RelayCommand]
    private void OpenLogFolder()
    {
        System.IO.Directory.CreateDirectory(LogFolder);
        Process.Start(new ProcessStartInfo(LogFolder) { UseShellExecute = true });
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsChecking = true;
        try
        {
            var exe = ClaudeCliLocator.Find();
            IsCliFound = exe is not null;
            if (exe is null)
            {
                CliPath = "Not found on PATH, in %USERPROFILE%\\.local\\bin or in %APPDATA%\\npm.";
                Version = "";
                IsSignedIn = false;
                SignInText = "Install Claude Code from Anthropic, then sign in.";
                return;
            }

            CliPath = exe;
            Version = await ClaudeCliProbe.GetVersionAsync(exe) ?? "(could not read the version)";
            var auth = await ClaudeCliProbe.GetAuthStatusAsync(exe);
            IsSignedIn = auth.SignedIn;
            SignInText = auth.SignedIn
                ? $"Signed in ({auth.AuthMethod switch { "claude.ai" => "Claude account", "api_key" => "API key", var m => m }})"
                : "Not signed in";
        }
        catch (Exception ex)
        {
            SignInText = "Check failed: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(IsReady));
        }
    }

    /// <summary>Opens a normal PowerShell window running `claude auth login` so the user signs in themselves.</summary>
    [RelayCommand]
    private void OpenSignInTerminal()
    {
        var exe = ClaudeCliLocator.Find();
        if (exe is null) return;
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
        };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"& '{exe.Replace("'", "''")}' auth login");
        Process.Start(psi);
    }

    [RelayCommand]
    private static void OpenInstallPage() =>
        Process.Start(new ProcessStartInfo("https://code.claude.com/docs/en/setup") { UseShellExecute = true });
}
