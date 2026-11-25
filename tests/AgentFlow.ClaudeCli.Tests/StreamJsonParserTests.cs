using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;

namespace AgentFlow.ClaudeCli.Tests;

// The fixture is synthetic, written from the documented stream-json shapes. Once the phase 0
// spike has recorded real output, those NDJSON files join this folder and get their own tests.
public class StreamJsonParserTests
{
    private static List<AgentEvent> ParseFixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))
            .Where(l => l.Length > 0)
            .Select(StreamJsonParser.Parse)
            .ToList();

    [Fact]
    public void Parses_every_line_without_throwing_and_in_order()
    {
        var events = ParseFixture("synthetic-basic-turn.ndjson");

        Assert.Collection(events,
            e => Assert.IsType<SessionStarted>(e),
            e => Assert.Equal("message_start", Assert.IsType<IgnoredStreamEvent>(e).EventType),
            e => Assert.Equal("Let me ", Assert.IsType<TextDelta>(e).Text),
            e => Assert.Equal("look.", Assert.IsType<TextDelta>(e).Text),
            e => Assert.IsType<AssistantMessage>(e),
            e => Assert.IsType<UserMessage>(e),
            e => Assert.IsType<ApiRetry>(e),
            e => Assert.Equal("future_event_type", Assert.IsType<UnknownEvent>(e).Type),
            e => Assert.IsType<ParseError>(e),
            e => Assert.IsType<AssistantMessage>(e),
            e => Assert.IsType<TurnResult>(e));
    }

    [Fact]
    public void Init_carries_session_model_tools_and_capabilities()
    {
        var init = ParseFixture("synthetic-basic-turn.ndjson").OfType<SessionStarted>().Single();

        Assert.Equal("11111111-2222-3333-4444-555555555555", init.SessionId);
        Assert.Equal("claude-haiku-5-5", init.Model);
        Assert.Equal(["Read", "Edit", "Bash"], init.Tools);
        Assert.Contains("interrupt_receipt_v1", init.Capabilities);
        Assert.Equal("dontAsk", init.PermissionMode);
    }

    [Fact]
    public void Assistant_message_splits_text_and_tool_use()
    {
        var msg = ParseFixture("synthetic-basic-turn.ndjson").OfType<AssistantMessage>().First();

        Assert.Equal("Let me look.", msg.Text);
        var tool = Assert.Single(msg.ToolUses);
        Assert.Equal("toolu_1", tool.Id);
        Assert.Equal("Read", tool.Name);
        Assert.Equal(@"C:\repo\README.md", tool.Input.GetProperty("file_path").GetString());
        Assert.Null(msg.ParentToolUseId);
    }

    [Fact]
    public void Tool_result_content_array_is_flattened()
    {
        var result = ParseFixture("synthetic-basic-turn.ndjson").OfType<UserMessage>().Single().ToolResults.Single();

        Assert.Equal("toolu_1", result.ToolUseId);
        Assert.Equal("# Readme", result.Content);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Result_carries_cost_usage_and_structured_output()
    {
        var r = ParseFixture("synthetic-basic-turn.ndjson").OfType<TurnResult>().Single();

        Assert.Equal("success", r.Subtype);
        Assert.False(r.IsError);
        Assert.Equal("The readme has a heading.", r.ResultText);
        Assert.Equal(0.0123m, r.TotalCostUsd);
        Assert.Equal(4210, r.DurationMs);
        Assert.Equal(2, r.NumTurns);
        Assert.Equal(new TokenUsage(120, 45, 3000, 0), r.Usage);
        Assert.Equal(5, r.StructuredOutput!.Value.GetProperty("answer").GetInt32());
    }

    [Fact]
    public void Api_retry_fields_are_read()
    {
        var r = ParseFixture("synthetic-basic-turn.ndjson").OfType<ApiRetry>().Single();

        Assert.Equal(1, r.Attempt);
        Assert.Equal(10, r.MaxRetries);
        Assert.Equal(500, r.RetryDelayMs);
        Assert.Equal(529, r.ErrorStatus);
        Assert.Equal("overloaded", r.Error);
    }

    [Theory]
    [InlineData("""{"type":"assistant"}""")]
    [InlineData("""{"type":"assistant","message":{"content":"plain string"}}""")]
    [InlineData("""{"type":"result"}""")]
    [InlineData("""{"type":"system"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta"}}}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"type":42}""")]
    public void Odd_shapes_never_throw(string line)
    {
        var ev = StreamJsonParser.Parse(line);
        Assert.NotNull(ev);
        Assert.Equal(line, ev.RawJson);
    }

    [Fact]
    public void Subagent_messages_keep_parent_tool_use_id()
    {
        var ev = StreamJsonParser.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]},"parent_tool_use_id":"toolu_agent"}""");
        Assert.Equal("toolu_agent", Assert.IsType<AssistantMessage>(ev).ParentToolUseId);
    }
}
