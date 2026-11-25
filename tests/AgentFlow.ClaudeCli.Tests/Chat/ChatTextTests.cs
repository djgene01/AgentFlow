using AgentFlow.Core.Chat;

namespace AgentFlow.ClaudeCli.Tests.Chat;

public class ChatTextTests
{
    private static long _seq;
    private static ChatEntry E(ChatEntryData d) => new(Guid.NewGuid(), "p", ++_seq, d, DateTimeOffset.UtcNow);

    private static readonly List<ChatEntry> Chat =
    [
        E(new UserMessageData("earlier chat", "haiku", null, "plan")),
        E(new AssistantTextData("unrelated answer", false)),
        E(new UserMessageData("Add login", null, null, null)),
        E(new RunStatusData("r1", "Flow", "started", "Workflow \"Flow\" started: Plan → Review")),
        E(new StageDividerData("r1", "plan", "Plan", 1, 1, "claude-fable-5-2", "high", "read only", "fresh session")),
        E(new ToolCallData("t1", "Read", """{"file_path":"C:\\src\\Login.cs"}""", "content", false, null)),
        E(new AssistantTextData("1. Add a **Login** method", false)),
        E(new GateData("r1", "plan", "Plan", "1. Add a Login method", GateStatus.Approved, "keep it small")),
        E(new StageDividerData("r1", "review", "Review", 2, 1, "claude-sonnet-5-5", "high", "read only + commands", "fresh session")),
        E(new VerdictData("r1", "review", "changes_requested", "One bug", [new VerdictFinding("major", "Login.cs", 3, "No null check", "Check for null")])),
        E(new RunStatusData("r1", "Flow", "completed", "Workflow \"Flow\" finished. 2 min 3 s")),
        E(new UserMessageData("after the run", "haiku", null, "plan")),
    ];

    [Fact]
    public void Search_looks_inside_every_kind_of_entry_ignoring_case()
    {
        // The task, the Read of Login.cs, the plan text, the gate and the verdict's finding.
        Assert.Equal(5, Chat.Count(e => ChatText.Matches(e.Data, "login")));
        Assert.Contains(Chat, e => e.Data is VerdictData && ChatText.Matches(e.Data, "NULL CHECK"));
        Assert.Contains(Chat, e => e.Data is ToolCallData && ChatText.Matches(e.Data, "Login.cs"));
        Assert.DoesNotContain(Chat, e => ChatText.Matches(e.Data, ""));
    }

    [Fact]
    public void A_run_is_its_task_plus_everything_from_start_to_finish()
    {
        var run = ChatText.RunEntries(Chat, "r1");

        Assert.Equal("Add login", Assert.IsType<UserMessageData>(run[0].Data).Text);
        Assert.IsType<RunStatusData>(run[^1].Data);
        Assert.Equal(9, run.Count);
        Assert.Empty(ChatText.RunEntries(Chat, "nope"));
    }

    [Fact]
    public void Markdown_export_reads_as_a_transcript()
    {
        var md = ChatText.ToMarkdown("Flow run", @"C:\src", ChatText.RunEntries(Chat, "r1"));

        Assert.StartsWith("# Flow run", md);
        Assert.Contains("> Add login", md);
        Assert.Contains("## Stage 1: Plan", md);
        Assert.Contains("- ✓ **Read** `C:\\src\\Login.cs`", md);
        Assert.Contains("1. Add a **Login** method", md);
        Assert.Contains("**Approval: Approved** — keep it small", md);
        Assert.Contains("**Verdict: changes_requested** — One bug", md);
        Assert.Contains("[major] `Login.cs:3` No null check Fix: Check for null", md);
        Assert.Contains("finished. 2 min 3 s", md);
        Assert.DoesNotContain("earlier chat", md);
    }
}
