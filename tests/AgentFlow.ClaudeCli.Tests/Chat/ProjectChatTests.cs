using System.Runtime.CompilerServices;
using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;
using AgentFlow.Storage;

namespace AgentFlow.ClaudeCli.Tests.Chat;

/// <summary>Phase 1 acceptance: chat, close the app, reopen it and continue the same session.</summary>
public class ProjectChatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentflow-chat-").FullName;
    private string DbPath => Path.Combine(_dir, "app.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class ScriptedRunner(string fixture) : IAgentRunner
    {
        public List<StageTurnRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(StageTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorded", "2.1.327", fixture)))
            {
                if (line.Length == 0) continue;
                await Task.Yield();
                yield return StreamJsonParser.Parse(line);
            }
            yield return new ProcessExited(0, false, null, "");
        }
    }

    private static Project NewProject(string folder) =>
        new(Guid.NewGuid().ToString("D"), "Demo", folder, null, null, null, "acceptEdits", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Second_message_after_restart_resumes_the_saved_session()
    {
        var project = NewProject(_dir);
        string firstSession;

        // First app run
        using (var store = new SqliteChatStore(DbPath))
        {
            store.SaveProject(project);
            var runner = new ScriptedRunner("03a-resume-first.ndjson");
            var chat = new ProjectChat(project, store, runner);
            await chat.SendAsync("Remember ORCHID-42", new DirectChatOptions("haiku", null, "dontAsk"));

            Assert.IsType<SessionSpec.New>(runner.Requests[0].Session);
            firstSession = chat.Project.LastSessionId!;
            Assert.False(string.IsNullOrEmpty(firstSession));
        }

        // Second app run: everything comes back from the database
        using (var store = new SqliteChatStore(DbPath))
        {
            var reloaded = store.GetProject(project.Id)!;
            Assert.Equal(firstSession, reloaded.LastSessionId);
            Assert.Equal("haiku", reloaded.LastModel);

            var history = store.GetEntries(project.Id);
            Assert.IsType<UserMessageData>(history[0].Data);
            Assert.Contains(history, e => e.Data is AssistantTextData { IsStreaming: false });
            Assert.Contains(history, e => e.Data is TurnSummaryData);
            Assert.Equal(history.Select(e => e.Seq).OrderBy(s => s), history.Select(e => e.Seq));

            var runner = new ScriptedRunner("03b-resume-second.ndjson");
            var chat = new ProjectChat(reloaded, store, runner);
            await chat.SendAsync("What was the codeword?", new DirectChatOptions("sonnet", "low", "dontAsk"));

            var resume = Assert.IsType<SessionSpec.Resume>(runner.Requests[0].Session);
            Assert.Equal(firstSession, resume.SessionId);
            Assert.Equal("sonnet", runner.Requests[0].Model);
            Assert.Equal("low", runner.Requests[0].Effort);
            Assert.Equal(_dir, runner.Requests[0].WorkingDirectory);
        }
    }

    [Fact]
    public async Task New_session_adds_a_divider_and_the_next_turn_starts_fresh()
    {
        var project = NewProject(_dir);
        using var store = new SqliteChatStore(DbPath);
        store.SaveProject(project);
        var runner = new ScriptedRunner("01-stdin-prompt.ndjson");
        var chat = new ProjectChat(project, store, runner);

        await chat.SendAsync("one", new DirectChatOptions(null, null, "plan"));
        chat.StartNewSession();
        await chat.SendAsync("two", new DirectChatOptions(null, null, "plan"));

        Assert.IsType<SessionSpec.New>(runner.Requests[1].Session);
        Assert.Contains(store.GetEntries(project.Id), e => e.Data is SessionDividerData);
    }

    [Fact]
    public async Task Streaming_bubbles_are_never_saved_half_finished()
    {
        var project = NewProject(_dir);
        using var store = new SqliteChatStore(DbPath);
        store.SaveProject(project);
        var chat = new ProjectChat(project, store, new ScriptedRunner("01-stdin-prompt.ndjson"));

        await chat.SendAsync("hi", new DirectChatOptions(null, null, "plan"));

        Assert.DoesNotContain(store.GetEntries(project.Id), e => e.Data is AssistantTextData { IsStreaming: true });
    }

    [Fact]
    public void Deleting_a_project_removes_its_chat()
    {
        var project = NewProject(_dir);
        using var store = new SqliteChatStore(DbPath);
        store.SaveProject(project);
        store.SaveEntry(new ChatEntry(Guid.NewGuid(), project.Id, store.NextSeq(project.Id), new NoticeData(NoticeLevel.Info, "x"), DateTimeOffset.UtcNow));

        store.DeleteProject(project.Id);

        Assert.Null(store.GetProject(project.Id));
        Assert.Empty(store.GetEntries(project.Id));
    }
}
