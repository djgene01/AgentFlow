using System.Windows;
using System.Windows.Threading;
using AgentFlow.App.Services;
using AgentFlow.App.ViewModels;
using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using AgentFlow.Permissions;
using AgentFlow.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFlow.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private PermissionServer? _permissions;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => AppLog.Write("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Write("Unobserved task exception", a.Exception); a.SetObserved(); };
        AppLog.Prune();
        AppLog.Write("AgentFlow started");

        // The animated splash, up while the rest starts. It is the first window, so WPF makes it MainWindow
        // for now; the real main window takes over below before the splash closes.
        var splash = new AgentFlow.App.Views.SplashWindow();
        splash.Show();
        await Dispatcher.Yield(DispatcherPriority.Render); // paint it before the work below

        // The permission server answers Claude Code's permission prompts by asking you in the chat.
        string serverText;
        try
        {
            _permissions = new PermissionServer();
            await _permissions.StartAsync();
            serverText = $"Running on {_permissions.Endpoint} (this computer only)";
        }
        catch (Exception ex)
        {
            _permissions = null;
            serverText = "Could not start, so permission prompts are denied: " + ex.Message;
        }

        var dbPath = SqliteChatStore.DefaultPath();
        var store = new SqliteChatStore(dbPath);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IChatStore>(store);
        services.AddSingleton<IRunStore>(store);
        services.AddSingleton(_ =>
        {
            var library = new WorkflowLibrary(WorkflowLibrary.DefaultFolder());
            library.EnsureDefault();
            return library;
        });
        services.AddSingleton<IAgentRunner>(_ => new ClaudeProcessRunner(new ClaudeRunnerOptions { ApprovalChannel = _permissions }));
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton(_ => new SoundService(store));
        services.AddSingleton(sp => new DiagnosticsViewModel(dbPath, store, serverText) { Sounds = sp.GetRequiredService<SoundService>() });
        services.AddSingleton<AgentFlow.App.ViewModels.Builder.WorkflowBuilderViewModel>();
        services.AddSingleton(_ => new AgentFlow.Core.Git.GitAssistant(new AgentFlow.Core.Git.GitShell()));
        services.AddSingleton<GitViewModel>();
        services.AddSingleton(sp => new VideoStudioViewModel(sp.GetRequiredService<IDialogService>(), store));
        services.AddSingleton(_ => new VoiceViewModel(store));
        foreach (var studio in AgentFlow.Core.Studios.StudioApps.All)
            services.AddSingleton(sp => new StudioAppViewModel(studio, sp.GetRequiredService<IDialogService>(), store));
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        _services.GetRequiredService<MainViewModel>().ApprovalsAvailable = _permissions is not null;
        _services.GetRequiredService<DiagnosticsViewModel>().LoadTheme();
        _ = _services.GetRequiredService<SoundService>().PrepareAsync();
        _ = _services.GetRequiredService<VideoStudioViewModel>().WarmUpAsync();
        _ = _services.GetRequiredService<VoiceViewModel>().PreloadAsync();
        _ = _services.GetRequiredService<VoiceViewModel>().PreloadTtsAsync();
        MainWindow = _services.GetRequiredService<MainWindow>();
        // Two loops of the animation (its loading bar sweeps twice), however fast startup was.
        await splash.WaitShownAsync(splash.LoopDuration * 2);
        MainWindow.Show();
        MainWindow.Activate();
        splash.FadeOutAndClose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose(); // closes the database; the Job Object ends any claude process still running
        _permissions?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("UI exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "AgentFlow error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
