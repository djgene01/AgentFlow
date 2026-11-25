using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;

namespace AgentFlow.ClaudeCli.Tests;

public class ClaudeArgumentBuilderTests
{
    private static StageTurnRequest Req() => new() { WorkingDirectory = @"C:\repo", Prompt = "ignored" };

    [Fact]
    public void Always_requests_streaming_json_and_never_passes_the_prompt()
    {
        var id = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var args = ClaudeArgumentBuilder.Build(Req() with { Session = new SessionSpec.New(id) });

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
             "--session-id", "550e8400-e29b-41d4-a716-446655440000"],
            args);
        Assert.DoesNotContain("ignored", args);
    }

    [Fact]
    public void Full_stage_configuration()
    {
        var args = ClaudeArgumentBuilder.Build(Req() with
        {
            Model = "opus",
            Effort = "medium",
            PermissionMode = "acceptEdits",
            AllowedTools = ["Bash(dotnet test *)", "Bash(git diff *)"],
            DisallowedTools = ["WebFetch"],
            Session = new SessionSpec.Resume("abc", Fork: true),
            MaxTurns = 40,
            MaxBudgetUsd = 2.5m,
            JsonSchema = """{"type":"object"}""",
            IncludePartialMessages = false,
        }, systemPromptFile: @"C:\tmp\role.md");

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose",
             "--model", "opus",
             "--effort", "medium",
             "--permission-mode", "acceptEdits",
             "--allowedTools", "Bash(dotnet test *),Bash(git diff *)",
             "--disallowedTools", "WebFetch",
             "--resume", "abc", "--fork-session",
             "--append-system-prompt-file", @"C:\tmp\role.md",
             "--max-turns", "40",
             "--max-budget-usd", "2.5",
             "--json-schema", """{"type":"object"}"""],
            args);
    }

    [Fact]
    public void Approvals_add_the_mcp_config_and_prompt_tool()
    {
        var args = ClaudeArgumentBuilder.Build(Req(), mcpConfigFile: @"C:\tmp\mcp.json", promptTool: "mcp__agentflow__approval_prompt");
        var i = args.ToList().IndexOf("--mcp-config");
        Assert.Equal(@"C:\tmp\mcp.json", args[i + 1]);
        Assert.Equal("--permission-prompt-tool", args[i + 2]);
        Assert.Equal("mcp__agentflow__approval_prompt", args[i + 3]);
        Assert.DoesNotContain("--strict-mcp-config", args); // the project's own MCP servers still load
    }

    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("yolo")]
    public void Refuses_unsafe_or_unknown_permission_modes(string mode)
    {
        Assert.Throws<ArgumentException>(() => ClaudeArgumentBuilder.Build(Req() with { PermissionMode = mode }));
    }

    [Fact]
    public void Refuses_unknown_effort()
    {
        Assert.Throws<ArgumentException>(() => ClaudeArgumentBuilder.Build(Req() with { Effort = "extreme" }));
    }
}
