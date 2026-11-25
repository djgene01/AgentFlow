using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;

namespace AgentFlow.ClaudeCli.Tests;

/// <summary>
/// Real stream-json output recorded by the phase 0 spike against Claude Code 2.1.327
/// (user paths replaced). Add a folder per CLI version when the format is re-recorded.
/// </summary>
public class RecordedFixtureTests
{
    private const string Version = "2.1.327";

    private static List<AgentEvent> Load(string file) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorded", Version, file))
            .Where(l => l.Length > 0)
            .Select(StreamJsonParser.Parse)
            .ToList();

    public static TheoryData<string> CompleteTurns => new()
    {
        "01-stdin-prompt.ndjson",
        "02-json-schema.ndjson",
        "03a-resume-first.ndjson",
        "03b-resume-second.ndjson",
    };

    [Theory]
    [MemberData(nameof(CompleteTurns))]
    public void Complete_turns_parse_fully_with_no_unknown_events(string file)
    {
        var events = Load(file);

        Assert.DoesNotContain(events, e => e is ParseError);
        Assert.DoesNotContain(events, e => e is UnknownEvent);
        Assert.IsType<SessionStarted>(events.First(e => e is SessionStarted));
        var result = Assert.Single(events.OfType<TurnResult>());
        Assert.Equal("success", result.Subtype);
        Assert.False(result.IsError);
        Assert.NotNull(result.SessionId);
        Assert.NotNull(result.Usage);
        Assert.True(result.TotalCostUsd > 0);
    }

    [Fact]
    public void Streamed_text_matches_the_final_result()
    {
        var events = Load("01-stdin-prompt.ndjson");
        var streamed = string.Concat(events.OfType<TextDelta>().Where(d => d.ParentToolUseId is null).Select(d => d.Text));
        Assert.Equal(events.OfType<TurnResult>().Single().ResultText?.Trim(), streamed.Trim());
    }

    [Fact]
    public void Json_schema_arrives_as_StructuredOutput_tool_and_structured_output_field()
    {
        var events = Load("02-json-schema.ndjson");

        Assert.Contains(events.OfType<AssistantMessage>().SelectMany(a => a.ToolUses), t => t.Name == "StructuredOutput");
        var so = events.OfType<TurnResult>().Single().StructuredOutput!.Value;
        Assert.Equal(5, so.GetProperty("answer").GetInt32());
    }

    [Fact]
    public void Resume_keeps_session_id_and_switches_model()
    {
        var a = Load("03a-resume-first.ndjson").OfType<SessionStarted>().First();
        var b = Load("03b-resume-second.ndjson").OfType<SessionStarted>().First();

        Assert.Equal(a.SessionId, b.SessionId);
        Assert.Contains("haiku", a.Model);
        Assert.Contains("sonnet", b.Model);
    }

    [Fact]
    public void Rate_limit_event_reports_window_utilization()
    {
        var rl = Load("01-stdin-prompt.ndjson").OfType<RateLimitStatus>().First();

        Assert.Equal("allowed", rl.Status);
        Assert.Contains("five_hour", rl.Utilization.Keys);
        Assert.NotNull(rl.ResetsAt);
    }

    [Fact]
    public void Ctrl_c_stop_still_ends_with_an_error_during_execution_result()
    {
        var events = Load("04a-stop.ndjson");

        Assert.DoesNotContain(events, e => e is ParseError);
        var r = Assert.Single(events.OfType<TurnResult>());
        Assert.Equal("error_during_execution", r.Subtype);
        Assert.NotNull(r.SessionId);
    }

    [Fact]
    public void Session_resumes_after_a_stop()
    {
        var stopped = Load("04a-stop.ndjson").OfType<SessionStarted>().First();
        var resumed = Load("04b-resume-after-stop.ndjson");

        Assert.Equal(stopped.SessionId, resumed.OfType<SessionStarted>().First().SessionId);
        Assert.Contains("RESUMED", resumed.OfType<TurnResult>().Single().ResultText);
    }

    [Fact]
    public void Plan_mode_returns_the_plan_as_result_text_without_ExitPlanMode()
    {
        var events = Load("05-plan-mode.ndjson");

        var r = Assert.Single(events.OfType<TurnResult>());
        Assert.Equal("success", r.Subtype);
        Assert.Contains("Plan", r.ResultText);
        Assert.DoesNotContain(events.OfType<AssistantMessage>().SelectMany(a => a.ToolUses), t => t.Name is "ExitPlanMode" or "Write" or "Edit");
        Assert.Equal("plan", events.OfType<SessionStarted>().First().PermissionMode);
    }

    [Fact]
    public void Output_cut_off_by_a_kill_ends_in_one_parse_error_and_nothing_throws()
    {
        var events = Load("04a-stop-truncated-by-kill.ndjson");

        Assert.IsType<ParseError>(events[^1]);
        Assert.Single(events.OfType<ParseError>());
        Assert.Empty(events.OfType<TurnResult>());
    }
}
