using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Core.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Permissions;

/// <summary>
/// A minimal MCP server (Streamable HTTP, JSON responses) inside the app, bound to 127.0.0.1 on a
/// random port. It offers one tool, which Claude Code calls through --permission-prompt-tool when a
/// tool call needs approval. Each turn gets its own secret bearer token, so a request is routed to
/// the chat that started that turn and nothing else on the machine can answer for it.
/// </summary>
public sealed class PermissionServer : IApprovalChannel, IAsyncDisposable
{
    public const string ServerName = "agentflow";
    public const string ToolName = "approval_prompt";
    public const string PromptToolName = $"mcp__{ServerName}__{ToolName}";

    private readonly ConcurrentDictionary<string, Func<ApprovalRequest, CancellationToken, Task<ApprovalAnswer>>> _turns = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _turnCancellation = new();
    private WebApplication? _app;

    public Uri? Endpoint { get; private set; }

    /// <summary>Every request the server receives, for diagnostics and the phase 5 spike.</summary>
    public event Action<string>? Trace;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/mcp", HandleAsync);
        app.MapGet("/mcp", () => Results.StatusCode(405)); // no server-initiated streams
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Endpoint = new Uri(new Uri(address.Replace("[::]", "127.0.0.1")), "/mcp");
        _app = app;
    }

    public ApprovalRegistration Register(Func<ApprovalRequest, CancellationToken, Task<ApprovalAnswer>> handler)
    {
        if (Endpoint is null) throw new InvalidOperationException("The permission server is not running.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _turns[token] = handler;
        _turnCancellation[token] = new CancellationTokenSource();

        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = Endpoint.ToString(),
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
                },
            },
        };

        return new ApprovalRegistration(config.ToJsonString(), PromptToolName, () =>
        {
            _turns.TryRemove(token, out _);
            if (_turnCancellation.TryRemove(token, out var cts))
            {
                cts.Cancel(); // a prompt still open when the turn ends is answered with a deny
                cts.Dispose();
            }
        });
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "";
        if (!_turns.TryGetValue(token, out var handler) || !_turnCancellation.TryGetValue(token, out var turnCts))
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        Trace?.Invoke(body?.ToJsonString() ?? "");

        // A batch (older protocol versions) or a single message.
        var messages = body is JsonArray arr ? arr.ToList() : [body];
        var responses = new JsonArray();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, turnCts.Token);
        foreach (var m in messages)
        {
            if (m is not JsonObject msg) continue;
            if (await HandleMessageAsync(msg, handler, linked.Token) is { } response) responses.Add(response);
        }

        if (responses.Count == 0)
        {
            ctx.Response.StatusCode = 202; // notifications only
            return;
        }
        ctx.Response.ContentType = "application/json";
        var payload = body is JsonArray ? responses.ToJsonString() : responses[0]!.ToJsonString();
        Trace?.Invoke("<- " + payload);
        await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
    }

    private static async Task<JsonNode?> HandleMessageAsync(JsonObject msg, Func<ApprovalRequest, CancellationToken, Task<ApprovalAnswer>> handler, CancellationToken ct)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        if (id is null) return null; // notification

        JsonNode Result(JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        JsonNode Error(int code, string message) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

        switch (method)
        {
            case "initialize":
                return Result(new JsonObject
                {
                    ["protocolVersion"] = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "AgentFlow permissions", ["version"] = "1.0" },
                });

            case "ping":
                return Result(new JsonObject());

            case "tools/list":
                return Result(new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = ToolName,
                            ["description"] = "Internal to AgentFlow: answers Claude Code permission prompts by asking the user. Do not call it yourself.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["tool_name"] = new JsonObject { ["type"] = "string" },
                                    ["input"] = new JsonObject { ["type"] = "object" },
                                    ["tool_use_id"] = new JsonObject { ["type"] = "string" },
                                },
                                ["required"] = new JsonArray { "tool_name", "input" },
                            },
                        },
                    },
                });

            case "tools/call":
                var p = msg["params"];
                if (p?["name"]?.GetValue<string>() != ToolName) return Error(-32602, "Unknown tool.");
                var args = p["arguments"] as JsonObject ?? new JsonObject();
                var toolName = args["tool_name"]?.GetValue<string>() ?? "";
                var inputNode = args["input"] ?? new JsonObject();
                using (var inputDoc = JsonDocument.Parse(inputNode.ToJsonString()))
                {
                    var request = new ApprovalRequest(toolName, inputDoc.RootElement.Clone(), args["tool_use_id"]?.GetValue<string>(), args.ToJsonString());
                    ApprovalAnswer answer;
                    try
                    {
                        answer = await handler(request, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        answer = new ApprovalAnswer(ApprovalChoice.Deny, "The user stopped the turn before answering.");
                    }

                    var decision = answer.Choice == ApprovalChoice.Deny
                        ? new JsonObject { ["behavior"] = "deny", ["message"] = answer.Message ?? "The user denied this action." }
                        : new JsonObject { ["behavior"] = "allow", ["updatedInput"] = inputNode.DeepClone() };

                    return Result(new JsonObject
                    {
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = decision.ToJsonString() } },
                    });
                }

            default:
                return Error(-32601, $"Method '{method}' is not supported.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _turnCancellation.Values) cts.Cancel();
        if (_app is not null) await _app.DisposeAsync();
    }
}
