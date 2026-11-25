using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;
using AgentFlow.Core.Chat;

namespace AgentFlow.ClaudeCli.Tests.Chat;

public class TurnProjectorTests
{
    private static (List<ChatEntry> Entries, TurnProjector Projector) Project(IEnumerable<AgentEvent> events)
    {
        var seq = 0L;
        var entries = new List<ChatEntry>();
        var p = new TurnProjector(d => new ChatEntry(Guid.NewGuid(), "p1", ++seq, d, DateTimeOffset.UtcNow));
        p.Added += entries.Add;
        foreach (var e in events) p.Apply(e);
        return (entries, p);
    }

    private static IEnumerable<AgentEvent> Recorded(string file) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorded", "2.1.327", file))
            .Where(l => l.Length > 0).Select(StreamJsonParser.Parse);

    [Fact]
    public void Simple_turn_gives_one_finished_text_bubble_and_a_summary()
    {
        var (entries, p) = Project(Recorded("01-stdin-prompt.ndjson").Append(new ProcessExited(0, false, null, "")));

        var text = Assert.Single(entries.Select(e => e.Data).OfType<AssistantTextData>());
        Assert.Contains("PONG", text.Text);
        Assert.False(text.IsStreaming);
        var summary = Assert.Single(entries.Select(e => e.Data).OfType<TurnSummaryData>());
        Assert.Equal("success", summary.Subtype);
        Assert.Equal(p.SessionId, summary.SessionId);
        Assert.DoesNotContain(entries, e => e.Data is NoticeData);
    }

    [Fact]
    public void Tool_calls_become_cards_and_receive_their_results()
    {
        var (entries, _) = Project(Recorded("05-plan-mode.ndjson"));

        var tools = entries.Select(e => e.Data).OfType<ToolCallData>().ToList();
        Assert.Contains(tools, t => t.Name == "Glob");
        Assert.Contains(tools, t => t.Name == "Read");
        Assert.All(tools, t => Assert.False(t.IsPending));
        Assert.Contains(entries.Select(e => e.Data).OfType<AssistantTextData>(), t => t.Text.Contains("Plan"));
    }

    [Fact]
    public void Stopped_turn_adds_a_stopped_notice_and_no_error()
    {
        var (entries, _) = Project(Recorded("04a-stop.ndjson").Append(new ProcessExited(0, true, StopOutcome.ExitedAfterInterrupt, "")));

        Assert.Contains(entries.Select(e => e.Data).OfType<NoticeData>(), n => n.Level == NoticeLevel.Info && n.Text.StartsWith("Stopped"));
        Assert.DoesNotContain(entries.Select(e => e.Data).OfType<NoticeData>(), n => n.Level == NoticeLevel.Error);
        Assert.All(entries.Select(e => e.Data).OfType<AssistantTextData>(), t => Assert.False(t.IsStreaming));
    }

    [Fact]
    public void Process_failing_before_a_result_reports_the_exit_code_and_stderr()
    {
        var (entries, _) = Project([new ProcessExited(1, false, null, "Error: model not found")]);

        var n = Assert.Single(entries.Select(e => e.Data).OfType<NoticeData>());
        Assert.Equal(NoticeLevel.Error, n.Level);
        Assert.Contains("code 1", n.Text);
        Assert.Contains("model not found", n.Text);
    }

    [Fact]
    public void Subagent_tool_calls_keep_their_parent_id()
    {
        var (entries, _) = Project(
        [
            StreamJsonParser.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"agent_1","name":"Agent","input":{"description":"Search"}}]},"parent_tool_use_id":null}"""),
            StreamJsonParser.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"grep_1","name":"Grep","input":{"pattern":"TODO"}}]},"parent_tool_use_id":"agent_1"}"""),
        ]);

        var child = entries.Select(e => e.Data).OfType<ToolCallData>().Single(t => t.Name == "Grep");
        Assert.Equal("agent_1", child.ParentToolUseId);
    }

    [Fact]
    public void Rate_limit_events_are_reported_not_added()
    {
        var reports = 0;
        var seq = 0L;
        var p = new TurnProjector(d => new ChatEntry(Guid.NewGuid(), "p1", ++seq, d, DateTimeOffset.UtcNow));
        p.UsageReported += _ => reports++;
        var added = 0;
        p.Added += _ => added++;
        foreach (var e in Recorded("01-stdin-prompt.ndjson").OfType<RateLimitStatus>()) p.Apply(e);

        Assert.True(reports > 0);
        Assert.Equal(0, added);
    }
}
