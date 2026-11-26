// Development tool: opens the real AgentFlow main window off-screen against a throwaway database
// and project folder, optionally sends one message through the real claude CLI, and saves PNGs
// of what the window shows. Nothing touches the user's real app data or projects.
//   AgentFlow.UiSnapshot <out-dir> [--send "<message>"] [--model haiku]

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentFlow.App;
using AgentFlow.App.Services;
using AgentFlow.App.ViewModels;
using AgentFlow.ClaudeCli;
using AgentFlow.Core.Chat;
using AgentFlow.Storage;

internal static class Program
{
    private static string? _builderTask;
    private static bool _loopTest;

    /// <summary>A Haiku copy of the default workflow where the reviewer is bound to request changes once.</summary>
    private static AgentFlow.Core.Workflows.WorkflowDefinition LoopTestWorkflow()
    {
        var wf = AgentFlow.Core.Workflows.DefaultWorkflows.PlanDevelopReviewRefine();
        return wf with
        {
            Name = "Loop test",
            Stages = wf.Stages.Select(s => s.Key switch
            {
                "develop" => s with { PromptTemplate = s.PromptTemplate + "\n\nHouse rule for this repo: do not write any code comments." },
                "review" => s with
                {
                    RolePrompt = s.RolePrompt + "\nHouse rule for this repo: every method must have a /// XML doc comment. A method without one is a major finding.",
                },
                _ => s,
            } with { Model = "claude-haiku-5-5", Effort = null }).ToList(),
        };
    }

    /// <summary>Builds a three-stage workflow through the builder's view models, saves it to the project, then runs it.</summary>
    private static async Task BuildAndRunAsync(Window window, MainViewModel vm, string outDir, string task)
    {
        var project = vm.SelectedProject!;
        vm.ShowWorkflowsCommand.Execute(null);
        await Settle(600);
        var b = vm.Builder;

        b.NewCommand.Execute(null);
        var ed = b.Editor!;
        ed.Name = "Quick three-step";
        ed.Description = "Plan, build and review with Haiku.";
        ed.SelectedStage = ed.Stages[0];
        ed.AddStageCommand.Execute(AgentFlow.Core.Workflows.StageTemplates.All.First(t => t.Id == "developer"));
        ed.AddStageCommand.Execute(AgentFlow.Core.Workflows.StageTemplates.All.First(t => t.Id == "reviewer"));
        foreach (var s in ed.Stages) { s.Model = "claude-haiku-5-5"; s.Effort = Choices.Efforts[0]; }

        // Rename a key: references in later stages must follow.
        ed.Stages[0].Key = "design";
        Console.WriteLine($"after rename: valid={ed.IsValid}; develop prompt uses design: {ed.Stages[1].PromptTemplate.Contains("{{stages.design.result}}")}");
        // Reorder there and back (Review above Develop breaks nothing structurally, then restore).
        ed.MoveTo(2, 1);
        Console.WriteLine($"after move: order={string.Join(",", ed.Stages.Select(s => s.Key))}");
        ed.MoveTo(1, 2);
        Console.WriteLine($"restored: order={string.Join(",", ed.Stages.Select(s => s.Key))}; valid={ed.IsValid}; errors={string.Join(" | ", ed.Errors)}");

        b.SaveScope = b.Scopes.Last(); // this project
        ed.SelectedStage = ed.Stages[2];
        await Settle(800);
        Save(window, Path.Combine(outDir, "6-builder.png"));
        b.SaveCommand.Execute(null);
        Console.WriteLine("builder: " + b.StatusText);
        await Settle(400);

        vm.SelectedProject = project;
        await Settle(800);
        var chat = (ChatViewModel)vm.CurrentPage!;
        chat.SelectedWorkflow = chat.Workflows.First(w => w.Label.StartsWith("Quick three-step", StringComparison.Ordinal));
        Console.WriteLine("picked: " + chat.SelectedWorkflow.Label);
        chat.Draft = task;
        var run = chat.SendCommand.ExecuteAsync(null);
        GateViewModel? gate = null;
        while (!run.IsCompleted && (gate = chat.Entries.OfType<GateViewModel>().FirstOrDefault(g => g.IsPending)) is null)
            await Settle(1000);
        gate?.ApproveCommand.Execute(null);
        await run;
        await Settle(1200);
        Save(window, Path.Combine(outDir, "7-built-workflow-done.png"));

        foreach (var e in chat.Entries)
        {
            var line = e switch
            {
                StageDividerViewModel s => $"== {s.Title} | {s.Details}",
                VerdictViewModel v => $"   VERDICT {v.Title}",
                GateViewModel g => $"   GATE {g.StatusText}",
                ToolCallViewModel t => $"   tool {t.Name} {t.Summary}{(t.IsError ? " (error)" : "")}",
                NoticeViewModel n => $"   NOTICE {n.Level}: {n.Text}",
                RunStatusViewModel r => $"## {r.Text}",
                _ => null,
            };
            if (line is not null) Console.WriteLine(line);
        }
        foreach (var s in chat.RunPanel?.Stages ?? []) Console.WriteLine($"{s.Title}: {s.StatusText} {s.Metrics}");
        Console.WriteLine("saved file: " + string.Join(", ", Directory.GetFiles(Path.Combine(project.FolderPath, ".workflows"))));
    }
    /// <summary>
    /// Crash test. "crash-a &lt;work&gt;" starts a workflow and kills this process once Develop has a session;
    /// "crash-b &lt;work&gt; &lt;out&gt;" reopens the same data in a new process and resumes from the recovery card.
    /// </summary>
    private static int CrashTest(string phase, string work, string? outDir)
    {
        var app = new App();
        app.InitializeComponent();
        var exitCode = 0;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { exitCode = phase == "crash-a" ? await CrashA(work) : await CrashB(work, outDir!); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        return exitCode;
    }

    private static (MainViewModel Vm, MainWindow Window, SqliteChatStore Store) OpenApp(string work)
    {
        var store = new SqliteChatStore(Path.Combine(work, "app.db"));
        var library = new AgentFlow.Core.Workflows.WorkflowLibrary(Path.Combine(work, "workflows"));
        var dialogs = new DialogService();
        var vm = new MainViewModel(store, store, new ClaudeProcessRunner(new ClaudeRunnerOptions()), library, dialogs,
            new DiagnosticsViewModel(Path.Combine(work, "app.db")), new AgentFlow.App.ViewModels.Builder.WorkflowBuilderViewModel(library, dialogs),
            new AgentFlow.App.ViewModels.GitViewModel(new AgentFlow.Core.Git.GitAssistant(new AgentFlow.Core.Git.GitShell()), dialogs));
        var window = new MainWindow(vm) { Left = -20000, Top = 0, Width = 1200, Height = 860, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        return (vm, window, store);
    }

    private static async Task<int> CrashA(string work)
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(work, "demo-project")).FullName;
        CreateSampleRepo(projectDir);
        AgentFlow.Core.Workflows.WorkflowJson.Save(new AgentFlow.Core.Workflows.WorkflowDefinition
        {
            Name = "Crash test",
            Stages =
            [
                AgentFlow.Core.Workflows.StageTemplates.Planner() with { Model = "claude-haiku-5-5", Effort = null },
                AgentFlow.Core.Workflows.StageTemplates.Developer() with { Model = "claude-haiku-5-5", Effort = null },
                AgentFlow.Core.Workflows.StageTemplates.Reviewer() with { Model = "claude-haiku-5-5", Effort = null },
            ],
        }, Path.Combine(projectDir, ".workflows", "crash-test.json"));

        var (vm, _, store) = OpenApp(work);
        var now = DateTimeOffset.UtcNow;
        store.SaveProject(new Project(Guid.NewGuid().ToString("D"), "demo-project", projectDir, null, null, null, "plan", now, now));
        vm.Projects.Add(new ProjectItemViewModel(store.GetProjects()[0]));
        vm.SelectedProject = vm.Projects[0];
        await Settle(800);
        var chat = (ChatViewModel)vm.CurrentPage!;
        chat.SelectedWorkflow = chat.Workflows.First(w => w.Label.StartsWith("Crash test", StringComparison.Ordinal));
        chat.Draft = "Add a static Square(int n) method to Program.cs, print Square(7), and add a one-line comment above it explaining what it does.";
        _ = chat.SendCommand.ExecuteAsync(null);

        var runs = (AgentFlow.Core.Workflows.IRunStore)store;
        var projectId = store.GetProjects()[0].Id;
        while (true)
        {
            await Settle(500);
            chat.Entries.OfType<GateViewModel>().FirstOrDefault(g => g.IsPending)?.ApproveCommand.Execute(null);
            var run = runs.GetRuns(projectId, 1).FirstOrDefault();
            var develop = run is null ? null : runs.GetStageRuns(run.Id).FirstOrDefault(s => s.StageKey == "develop" && s.SessionId is not null);
            if (develop is not null)
            {
                await Settle(4000); // let it start working
                Console.WriteLine($"Killing the app during Develop (session {develop.SessionId}).");
                Console.Out.Flush();
                System.Diagnostics.Process.GetCurrentProcess().Kill(); // no cleanup, like a crash
            }
        }
    }

    private static async Task<int> CrashB(string work, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var (vm, window, store) = OpenApp(work);
        var runs = (AgentFlow.Core.Workflows.IRunStore)store;
        var project = store.GetProjects()[0];
        var before = runs.GetRuns(project.Id, 1)[0];
        Console.WriteLine($"Run after the crash: status={before.Status}, stage={before.CurrentStageKey}");
        foreach (var s in runs.GetStageRuns(before.Id)) Console.WriteLine($"  {s.StageKey} #{s.Attempt}: {s.Status} session={s.SessionId}");

        vm.SelectedProject = vm.Projects[0];
        await Settle(1200);
        var chat = (ChatViewModel)vm.CurrentPage!;
        var card = chat.Entries.OfType<RecoveryViewModel>().LastOrDefault();
        Console.WriteLine($"Recovery card: {(card is null ? "MISSING" : card.Text)}");
        Save(window, Path.Combine(outDir, "8-recovery-card.png"));
        if (card is null) return 1;

        card.ResumeCommand.Execute(null);
        await Settle(1500);
        while (chat.IsRunning)
        {
            chat.Entries.OfType<GateViewModel>().FirstOrDefault(g => g.IsPending)?.ApproveCommand.Execute(null);
            await Settle(1000);
        }
        await Settle(1000);
        Save(window, Path.Combine(outDir, "9-resumed-done.png"));

        var after = runs.GetRuns(project.Id, 1)[0];
        Console.WriteLine($"Run after resume: status={after.Status}");
        foreach (var s in runs.GetStageRuns(after.Id)) Console.WriteLine($"  {s.StageKey} #{s.Attempt}: {s.Status} session={s.SessionId}");
        foreach (var e in chat.Entries.OfType<RunStatusViewModel>()) Console.WriteLine("## " + e.Text);
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "diff --stat")
        { WorkingDirectory = project.FolderPath, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        Console.WriteLine("git diff --stat: " + (await p.StandardOutput.ReadToEndAsync()).Trim());
        window.Close();
        store.Dispose();
        return after.Status == AgentFlow.Core.Workflows.RunStatus.Completed ? 0 : 2;
    }

    /// <summary>"approvals &lt;out&gt;": a direct chat asks to run a command; the card is answered with Always allow.</summary>
    private static int ApprovalTest(string outDir)
    {
        var app = new App();
        app.InitializeComponent();
        var exitCode = 0;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { exitCode = await ApprovalTestAsync(outDir); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        return exitCode;
    }

    private static async Task<int> ApprovalTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var work = Directory.CreateTempSubdirectory("agentflow-ui-").FullName;
        var projectDir = Directory.CreateDirectory(Path.Combine(work, "demo-project")).FullName;
        await using var server = new AgentFlow.Permissions.PermissionServer();
        await server.StartAsync();

        using var store = new SqliteChatStore(Path.Combine(work, "app.db"));
        var library = new AgentFlow.Core.Workflows.WorkflowLibrary(Path.Combine(work, "workflows"));
        var dialogs = new DialogService();
        var diagnostics = new DiagnosticsViewModel(Path.Combine(work, "app.db"), store, "test");
        var vm = new MainViewModel(store, store, new ClaudeProcessRunner(new ClaudeRunnerOptions { ApprovalChannel = server }), library, dialogs,
            diagnostics, new AgentFlow.App.ViewModels.Builder.WorkflowBuilderViewModel(library, dialogs),
            new AgentFlow.App.ViewModels.GitViewModel(new AgentFlow.Core.Git.GitAssistant(new AgentFlow.Core.Git.GitShell()), dialogs));
        var window = new MainWindow(vm) { Left = -20000, Top = 0, Width = 1200, Height = 860, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        var now = DateTimeOffset.UtcNow;
        store.SaveProject(new Project(Guid.NewGuid().ToString("D"), "demo-project", projectDir, null, null, null, "default", now, now));
        vm.Projects.Add(new ProjectItemViewModel(store.GetProjects()[0]));
        vm.SelectedProject = vm.Projects[0];
        await Settle(800);

        var chat = (ChatViewModel)vm.CurrentPage!;
        chat.SelectedModel = Choices.Find(Choices.Models, "haiku");
        chat.SelectedPermissionMode = Choices.Find(Choices.PermissionModes, "default");
        Console.WriteLine($"approvals: {diagnostics.ApprovalPolicy}; mode: {chat.SelectedPermissionMode.Label}");

        async Task<int> Ask(string prompt, bool answer, string shot)
        {
            chat.Draft = prompt;
            var turn = chat.SendCommand.ExecuteAsync(null);
            var cards = 0;
            while (!turn.IsCompleted)
            {
                await Settle(500);
                if (chat.Entries.OfType<ApprovalViewModel>().FirstOrDefault(a => a.IsPending) is { } card)
                {
                    cards++;
                    await Settle(600);
                    Save(window, Path.Combine(outDir, shot));
                    Console.WriteLine($"card: {card.Title} | {card.Summary} | {card.AlwaysLabel}");
                    if (answer) card.AlwaysAllowCommand.Execute(null); else card.DenyCommand.Execute(null);
                }
            }
            await turn;
            return cards;
        }

        var first = await Ask("Run exactly this shell command once: git init demo-repo   Then reply DONE, or DENIED if you were not allowed. Use no other tool.", true, "10-approval-card.png");
        var second = await Ask("Now run exactly this shell command once: git init second-repo   Then reply DONE, or DENIED if you were not allowed. Use no other tool.", true, "11-unexpected-card.png");
        await Settle(800);
        Save(window, Path.Combine(outDir, "12-approvals-done.png"));

        Console.WriteLine($"first turn cards={first}, demo-repo created={Directory.Exists(Path.Combine(projectDir, "demo-repo", ".git"))}");
        Console.WriteLine($"second turn cards={second}, second-repo created={Directory.Exists(Path.Combine(projectDir, "second-repo", ".git"))}");
        Console.WriteLine("saved rules: " + string.Join(", ", chat.AllowRules));
        foreach (var e in chat.Entries.OfType<AssistantTextViewModel>()) Console.WriteLine("reply: " + e.Text.Trim());
        foreach (var e in chat.Entries.OfType<ApprovalViewModel>()) Console.WriteLine("card status: " + e.StatusText);
        window.Close();
        return 0;
    }

    /// <summary>"polish &lt;out&gt;": a seeded chat (no CLI calls) in light and dark, with search open, plus a Markdown export.</summary>
    /// <summary>The animated splash: two captures half a loop apart should differ (the animation runs).</summary>
    private static int SplashTest(string outDir)
    {
        var app = new App();
        app.InitializeComponent();
        Directory.CreateDirectory(outDir);
        var exitCode = 0;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                var splash = new AgentFlow.App.Views.SplashWindow { ShowActivated = false };
                splash.Show();
                splash.Left = -20000;
                await Settle(700);
                Console.WriteLine($"splash: {splash.Width:0}x{splash.Height:0} DIP, loop {splash.LoopDuration.TotalMilliseconds} ms");
                Save(splash, Path.Combine(outDir, "splash-a.png"));
                await Settle(450);
                Save(splash, Path.Combine(outDir, "splash-b.png"));
                var a = File.ReadAllBytes(Path.Combine(outDir, "splash-a.png"));
                var b = File.ReadAllBytes(Path.Combine(outDir, "splash-b.png"));
                Console.WriteLine(a.SequenceEqual(b) ? "splash: NOT animating (captures identical)" : "splash: animating (captures differ)");
                splash.Close();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        return exitCode;
    }

    private static int PolishTest(string outDir)
    {
        var app = new App();
        app.InitializeComponent();
        var exitCode = 0;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { exitCode = await PolishAsync(outDir); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        return exitCode;
    }

    private static async Task<int> PolishAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var work = Directory.CreateTempSubdirectory("agentflow-ui-").FullName;
        using var store = new SqliteChatStore(Path.Combine(work, "app.db"));
        var now = DateTimeOffset.UtcNow;
        var project = new Project(Guid.NewGuid().ToString("D"), "Calculator", Path.Combine(work, "Calculator"), "11111111-2222-3333-4444-555555555555", null, null, "plan", now, now);
        store.SaveProject(project);
        void Add(ChatEntryData d) => store.SaveEntry(new ChatEntry(Guid.NewGuid(), project.Id, store.NextSeq(project.Id), d, DateTimeOffset.UtcNow));
        Add(new UserMessageData("Add a login page with validation", null, null, null));
        Add(new RunStatusData("r1", "Plan, develop, review, refine", "started", "Workflow \"Plan, develop, review, refine\" started: Plan → Develop → Review → Refine"));
        Add(new StageDividerData("r1", "plan", "Plan", 1, 1, "claude-fable-5-2", "high", "read only", "fresh session"));
        Add(new ToolCallData("t1", "Read", """{"file_path":"C:\\Calculator\\src\\app.js"}""", "1  const app = …", false, null));
        Add(new AssistantTextData("## Plan\n\n1. Add `login.html` with a **form**\n2. Validate the email and password in `login.js`\n3. Add tests in `test/login.test.js`", false));
        Add(new GateData("r1", "plan", "Plan", "1. Add login.html …", GateStatus.Approved, "Keep the styling plain"));
        Add(new StageDividerData("r1", "review", "Review", 3, 1, "claude-sonnet-5-5", "high", "read only + build/test/git", "fresh session"));
        Add(new VerdictData("r1", "review", "changes_requested", "The login form works, but validation misses one case.",
            [new VerdictFinding("major", "src/login.js", 14, "Empty password passes validation", "Reject empty strings")]));
        Add(new ApprovalData("PowerShell", "node --test", "{\"command\":\"node --test\"}", "Run unit tests", "PowerShell(node --test *)", "Review", ApprovalStatus.AlwaysAllowed));
        Add(new RunStatusData("r1", "Plan, develop, review, refine", "completed", "Workflow \"Plan, develop, review, refine\" finished. 6 min 12 s · 5 stage turns · 41,230 tokens · est. $2.10"));

        var library = new AgentFlow.Core.Workflows.WorkflowLibrary(Path.Combine(work, "workflows"));
        var dialogs = new DialogService();
        var diagnostics = new DiagnosticsViewModel(Path.Combine(work, "app.db"), store, "test")
        {
            Sounds = new AgentFlow.App.Services.SoundService(store, Path.Combine(work, "sounds")),
        };
        var vm = new MainViewModel(store, store, new ClaudeProcessRunner(new ClaudeRunnerOptions()), library, dialogs, diagnostics,
            new AgentFlow.App.ViewModels.Builder.WorkflowBuilderViewModel(library, dialogs),
            new AgentFlow.App.ViewModels.GitViewModel(new AgentFlow.Core.Git.GitAssistant(new AgentFlow.Core.Git.GitShell()), dialogs),
            video: new VideoStudioViewModel(dialogs, store),
            studios: AgentFlow.Core.Studios.StudioApps.All.Select(a => new StudioAppViewModel(a, dialogs, store)));
        var window = new MainWindow(vm) { Left = -20000, Top = 0, Width = 1200, Height = 860, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        vm.SelectedProject = vm.Projects[0];
        await Settle(1000);
        var chat = (ChatViewModel)vm.CurrentPage!;

        // Pixel Art Studio and SfxForge pages (pointed at a folder when AGENTFLOW_SFXFORGE is set).
        if (Environment.GetEnvironmentVariable("AGENTFLOW_SFXFORGE") is { } sfxFolder) vm.Sfx!.Folder = sfxFolder;
        vm.ShowPixelArtCommand.Execute(null);
        await Settle(900);
        Save(window, Path.Combine(outDir, "20-pixel-art-studio.png"));
        vm.ShowSfxCommand.Execute(null);
        if (vm.Sfx!.IsInstallFound) await vm.Sfx.TestCommand.ExecuteAsync(null);
        await Settle(900);
        Save(window, Path.Combine(outDir, "21-sfxforge.png"));
        vm.SelectedProject = vm.Projects[0];
        await Settle(500);

        // Video studio page, pointed at the studio on this PC when there is one, after a real MCP test.
        if (Environment.GetEnvironmentVariable("AGENTFLOW_VIDEO_STUDIO") is { } studioFolder)
        {
            vm.Video!.Folder = studioFolder;
            vm.Video.IsEnabled = true;
            vm.ShowVideoCommand.Execute(null);
            await vm.Video.TestCommand.ExecuteAsync(null);
            await Settle(1200);
            Save(window, Path.Combine(outDir, "19-video-studio.png"));
            Console.WriteLine("video: " + vm.Video.TestText);
            vm.SelectedProject = vm.Projects[0];
            await Settle(500);
        }

        foreach (var theme in new[] { "light", "dark" })
        {
            diagnostics.SelectedTheme = Choices.Find(DiagnosticsViewModel.Themes, theme);
            chat.CloseSearchCommand.Execute(null);
            await Settle(900);
            Save(window, Path.Combine(outDir, $"13-chat-{theme}.png"));
            chat.OpenSearchCommand.Execute(null);
            chat.SearchText = "validation";
            await Settle(900);
            Console.WriteLine($"{theme}: search 'validation' -> {chat.SearchStatus}");
            chat.NextMatchCommand.Execute(null);
            await Settle(500);
            Console.WriteLine($"{theme}: after F3 -> {chat.SearchStatus}");
            Save(window, Path.Combine(outDir, $"14-search-{theme}.png"));
        }
        vm.ShowDiagnosticsCommand.Execute(null);
        await Settle(1200);
        Save(window, Path.Combine(outDir, "15-settings-dark.png"));
        foreach (var tb in TreeOf(window).OfType<System.Windows.Controls.TextBlock>().Where(t => t.Text == "Which events make a sound"))
            tb.BringIntoView();
        await Settle(800);
        Save(window, Path.Combine(outDir, "15b-settings-sounds.png"));

        // Git page: a folder that is not a repository yet, then after "Make it a git repository".
        Directory.CreateDirectory(project.FolderPath);
        File.WriteAllText(Path.Combine(project.FolderPath, "app.js"), "console.log('hi');");
        vm.SelectedProject = vm.Projects[0];
        await Settle(400);
        vm.ShowGitCommand.Execute(null);
        await Settle(2500);
        Save(window, Path.Combine(outDir, "16-git-new.png"));
        await vm.Git.InitCommand.ExecuteAsync(null);
        vm.Git.RemoteUrlInput = "https://github.com/you/calculator.git";
        await vm.Git.ConnectRemoteCommand.ExecuteAsync(null);
        vm.Git.CommitMessage = "First version";
        diagnostics.SelectedTheme = Choices.Find(DiagnosticsViewModel.Themes, "light");
        await Settle(2500);
        static IEnumerable<System.Windows.DependencyObject> Tree(System.Windows.DependencyObject d)
        {
            yield return d;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                foreach (var c in Tree(System.Windows.Media.VisualTreeHelper.GetChild(d, i))) yield return c;
        }
        Save(window, Path.Combine(outDir, "17-git-repo.png"));
        foreach (var sv in Tree(window).OfType<System.Windows.Controls.ScrollViewer>().Where(s => s.ScrollableHeight > 100).ToList())
            sv.ScrollToEnd();
        await Settle(800);
        Save(window, Path.Combine(outDir, "18-git-repo-bottom.png"));
        Console.WriteLine("git steps: " + string.Join(" | ", vm.Git.Steps.Select(s => $"{s.Mark} {s.Title}: {s.Detail}")));

        var md = ChatText.ToMarkdown("Plan, develop, review, refine run in Calculator", project.FolderPath, ChatText.RunEntries(store.GetEntries(project.Id), "r1"));
        File.WriteAllText(Path.Combine(outDir, "run-export.md"), md);
        Console.WriteLine("--- export ---\n" + md);
        window.Close();
        return 0;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "polish")
            return PolishTest(Path.GetFullPath(args[1]));
        if (args.Length >= 2 && args[0] == "splash")
            return SplashTest(Path.GetFullPath(args[1]));
        if (args.Length >= 2 && args[0] is "crash-a" or "crash-b")
            return CrashTest(args[0], Path.GetFullPath(args[1]), args.Length > 2 ? Path.GetFullPath(args[2]) : null);
        if (args.Length >= 2 && args[0] == "approvals")
            return ApprovalTest(Path.GetFullPath(args[1]));

        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-snapshots");
        string? send = null, workflowTask = null, builderTask = null;
        var model = "haiku";
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--send") send = args[++i];
            else if (args[i] == "--model") model = args[++i];
            else if (args[i] == "--workflow-task") workflowTask = args[++i];
            else if (args[i] == "--loop-test") { workflowTask = args[++i]; _loopTest = true; }
            else if (args[i] == "--builder-task") { builderTask = args[++i]; workflowTask ??= null; }
        }
        _builderTask = builderTask;
        Directory.CreateDirectory(outDir);

        var work = Directory.CreateTempSubdirectory("agentflow-ui-").FullName;
        var projectDir = Directory.CreateDirectory(Path.Combine(work, "demo-project")).FullName;
        File.WriteAllText(Path.Combine(projectDir, "README.md"), "# Demo project\n\nA tiny folder used to check the AgentFlow UI.\n");
        if (workflowTask is not null || builderTask is not null) CreateSampleRepo(projectDir);

        var app = new App();
        app.InitializeComponent(); // loads theme and templates; OnStartup is not run
        var exitCode = 0;
        var frame = new DispatcherFrame();

        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                exitCode = await RunAsync(outDir, work, projectDir, send, model, workflowTask);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally
            {
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return exitCode;
    }

    /// <summary>A minimal .NET console app in a fresh git repository (no NuGet packages needed).</summary>
    private static void CreateSampleRepo(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), """
            // A tiny sample app used to try AgentFlow workflows.
            Console.WriteLine("Sample app");
            """);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n");
        foreach (var args in new[] { "init -q", "add -A", "-c user.name=AgentFlow -c user.email=sample@example.invalid commit -q -m initial" })
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args)
            { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit();
        }
    }

    private static async Task<int> RunAsync(string outDir, string work, string projectDir, string? send, string model, string? workflowTask)
    {
        var dbPath = Path.Combine(work, "app.db");
        using var store = new SqliteChatStore(dbPath);
        var now = DateTimeOffset.UtcNow;
        store.SaveProject(new Project(Guid.NewGuid().ToString("D"), "demo-project", projectDir, null, model, null, "plan", now, now));

        var diagnostics = new DiagnosticsViewModel(dbPath);
        var library = new AgentFlow.Core.Workflows.WorkflowLibrary(Path.Combine(work, "workflows"));
        library.EnsureDefault();
        var dialogs = new DialogService();
        var vm = new MainViewModel(store, store, new ClaudeProcessRunner(new ClaudeRunnerOptions()), library, dialogs, diagnostics,
            new AgentFlow.App.ViewModels.Builder.WorkflowBuilderViewModel(library, dialogs),
            new AgentFlow.App.ViewModels.GitViewModel(new AgentFlow.Core.Git.GitAssistant(new AgentFlow.Core.Git.GitShell()), dialogs));
        var window = new MainWindow(vm) { Left = -20000, Top = 0, Width = 1200, Height = 860, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        await Settle(1500);

        vm.ShowDiagnosticsCommand.Execute(null);
        await Settle(2500);
        Save(window, Path.Combine(outDir, "1-diagnostics.png"));

        vm.SelectedProject = vm.Projects[0];
        await Settle(800);

        if (send is not null && vm.CurrentPage is ChatViewModel chat)
        {
            chat.SelectedModel = Choices.Find(Choices.Models, model);
            chat.Draft = send;
            var task = chat.SendCommand.ExecuteAsync(null);
            await Settle(2500);
            Save(window, Path.Combine(outDir, "2-chat-streaming.png"));
            await task;
            await Settle(1200);

            // Expand the first tool card, if any, to check the expanded layout.
            if (chat.Entries.OfType<ToolCallViewModel>().FirstOrDefault() is { } tool) tool.IsExpanded = true;
            await Settle(600);
        }
        if (_builderTask is not null)
            await BuildAndRunAsync(window, vm, outDir, _builderTask);

        if (workflowTask is not null && vm.CurrentPage is ChatViewModel wfChat)
        {
            if (_loopTest)
            {
                library.SaveNew(LoopTestWorkflow(), AgentFlow.Core.Workflows.WorkflowFile.LibraryScope, null);
                wfChat.RefreshWorkflowsCommand.Execute(null);
            }
            wfChat.SelectedWorkflow = wfChat.Workflows.First(w => w.File is not null && (!_loopTest || w.Label == "Loop test"));
            wfChat.Draft = workflowTask;
            var run = wfChat.SendCommand.ExecuteAsync(null);

            // Wait for the plan gate, take a picture, then approve it.
            GateViewModel? gate = null;
            while (!run.IsCompleted && (gate = wfChat.Entries.OfType<GateViewModel>().FirstOrDefault(g => g.IsPending)) is null)
                await Settle(1000);
            if (gate is not null)
            {
                await Settle(800);
                Save(window, Path.Combine(outDir, "4-workflow-gate.png"));
                Console.WriteLine("--- plan at the gate ---\n" + gate.Text + "\n---");
                gate.ApproveCommand.Execute(null);
            }
            await run;
            await Settle(1200);
            Save(window, Path.Combine(outDir, "5-workflow-done.png"));

            Console.WriteLine("--- timeline ---");
            foreach (var e in wfChat.Entries)
            {
                Console.WriteLine(e switch
                {
                    StageDividerViewModel s => $"== {s.Title} | {s.Details}",
                    VerdictViewModel v => $"   VERDICT {v.Title}: {v.Summary} {string.Join(" | ", v.Findings.Select(f => f.Location + " " + f.Issue))}",
                    GateViewModel g => $"   GATE {g.StatusText}",
                    ToolCallViewModel t => $"   tool {t.Name} {t.Summary}{(t.IsError ? " (error)" : "")}",
                    NoticeViewModel n => $"   NOTICE {n.Level}: {n.Text}",
                    TurnSummaryViewModel ts => $"   summary {ts.Text}",
                    RunStatusViewModel r => $"## {r.Text}",
                    AssistantTextViewModel a => $"   text {a.Text.ReplaceLineEndings(" ")[..Math.Min(160, a.Text.Length)]}",
                    _ => $"   {e.GetType().Name}",
                });
            }
            Console.WriteLine("--- run panel ---");
            foreach (var s in wfChat.RunPanel?.Stages ?? []) Console.WriteLine($"{s.Title}: {s.StatusText} {s.Metrics}");
            Console.WriteLine(wfChat.RunPanel?.StatusText + " " + wfChat.RunPanel?.Totals);
            Console.WriteLine("--- git diff in the sample repo ---");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "status --short")
            { WorkingDirectory = projectDir, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            Console.WriteLine(await p.StandardOutput.ReadToEndAsync());
        }

        Save(window, Path.Combine(outDir, "3-chat-done.png"));

        window.Close();
        Console.WriteLine($"Saved snapshots to {outDir}");
        return 0;
    }

    private static IEnumerable<System.Windows.DependencyObject> TreeOf(System.Windows.DependencyObject d)
    {
        yield return d;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            foreach (var c in TreeOf(System.Windows.Media.VisualTreeHelper.GetChild(d, i))) yield return c;
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(root);
        var bmp = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Paint the window background first; the root grid itself is transparent.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
