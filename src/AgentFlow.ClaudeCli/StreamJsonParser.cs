using System.Text.Json;
using AgentFlow.Core.Agents;

namespace AgentFlow.ClaudeCli;

/// <summary>
/// Turns one line of `claude -p --output-format stream-json` output into an <see cref="AgentEvent"/>.
/// Tolerant by design: unknown types and missing fields never throw.
/// </summary>
public static class StreamJsonParser
{
    public static AgentEvent Parse(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return new ParseError(ex.Message, line);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ParseError("Line is not a JSON object.", line);

            var type = Str(root, "type");
            var subtype = Str(root, "subtype");

            try
            {
                return type switch
                {
                    "system" => ParseSystem(root, subtype, line),
                    "stream_event" => ParseStreamEvent(root, line),
                    "assistant" => new AssistantMessage(
                        Str(Obj(root, "message"), "id"),
                        ParseContent(Obj(root, "message")),
                        Str(root, "parent_tool_use_id"),
                        line),
                    "user" => new UserMessage(
                        ParseContent(Obj(root, "message")),
                        Str(root, "parent_tool_use_id"),
                        line),
                    "result" => ParseResult(root, subtype, line),
                    "rate_limit_event" => ParseRateLimit(root, line),
                    _ => new UnknownEvent(type, subtype, line),
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
            {
                return new ParseError($"Unexpected shape for type '{type}': {ex.Message}", line);
            }
        }
    }

    private static AgentEvent ParseSystem(JsonElement root, string? subtype, string line) => subtype switch
    {
        "init" => new SessionStarted(
            Str(root, "session_id") ?? "",
            Str(root, "model"),
            StrArray(root, "tools"),
            StrArray(root, "capabilities"),
            Str(root, "permissionMode") ?? Str(root, "permission_mode"),
            line),
        "api_retry" => new ApiRetry(
            Int(root, "attempt") ?? 0,
            Int(root, "max_retries") ?? 0,
            Int(root, "retry_delay_ms") ?? 0,
            Int(root, "error_status"),
            Str(root, "error"),
            line),
        "permission_denied" => new PermissionDenied(
            Str(root, "tool_name") ?? Str(Obj(root, "tool"), "name"),
            line),
        _ => new SystemNotice(subtype ?? "", line),
    };

    private static AgentEvent ParseStreamEvent(JsonElement root, string line)
    {
        var ev = Obj(root, "event");
        var evType = Str(ev, "type") ?? "";
        var parent = Str(root, "parent_tool_use_id");

        if (evType == "content_block_delta")
        {
            var delta = Obj(ev, "delta");
            switch (Str(delta, "type"))
            {
                case "text_delta":
                    return new TextDelta(Str(delta, "text") ?? "", parent, line);
                case "thinking_delta":
                    return new ThinkingDelta(Str(delta, "thinking") ?? "", parent, line);
            }
        }

        return new IgnoredStreamEvent(evType, line);
    }

    private static TurnResult ParseResult(JsonElement root, string? subtype, string line)
    {
        TokenUsage? usage = null;
        var u = Obj(root, "usage");
        if (u.ValueKind == JsonValueKind.Object)
        {
            usage = new TokenUsage(
                Long(u, "input_tokens") ?? 0,
                Long(u, "output_tokens") ?? 0,
                Long(u, "cache_read_input_tokens") ?? 0,
                Long(u, "cache_creation_input_tokens") ?? 0);
        }

        return new TurnResult(
            subtype ?? "",
            Bool(root, "is_error") ?? false,
            Str(root, "result"),
            Str(root, "session_id"),
            Dec(root, "total_cost_usd"),
            Long(root, "duration_ms"),
            Int(root, "num_turns"),
            usage,
            Clone(root, "permission_denials"),
            Clone(root, "structured_output"),
            line);
    }

    private static RateLimitStatus ParseRateLimit(JsonElement root, string line)
    {
        var info = Obj(root, "rate_limit_info");
        var windows = new Dictionary<string, double>(StringComparer.Ordinal);
        var unified = Obj(info, "unifiedWindows");
        if (unified.ValueKind == JsonValueKind.Object)
        {
            foreach (var w in unified.EnumerateObject())
            {
                var u = Prop(w.Value, "utilization");
                if (u.ValueKind == JsonValueKind.Number) windows[w.Name] = u.GetDouble();
            }
        }

        var resets = Long(info, "resetsAt");
        return new RateLimitStatus(
            Str(info, "status"),
            Str(info, "rateLimitType"),
            resets is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            windows,
            line);
    }

    private static IReadOnlyList<ContentBlock> ParseContent(JsonElement message)
    {
        var content = Prop(message, "content");
        if (content.ValueKind == JsonValueKind.String)
            return [new TextBlock(content.GetString() ?? "")];
        if (content.ValueKind != JsonValueKind.Array)
            return [];

        var blocks = new List<ContentBlock>();
        foreach (var b in content.EnumerateArray())
        {
            var t = Str(b, "type") ?? "";
            blocks.Add(t switch
            {
                "text" => new TextBlock(Str(b, "text") ?? ""),
                "thinking" => new ThinkingBlock(Str(b, "thinking") ?? ""),
                "tool_use" => new ToolUseBlock(Str(b, "id") ?? "", Str(b, "name") ?? "", Clone(b, "input") ?? default),
                "tool_result" => new ToolResultBlock(Str(b, "tool_use_id") ?? "", FlattenToolResult(Prop(b, "content")), Bool(b, "is_error") ?? false),
                _ => new OtherBlock(t),
            });
        }
        return blocks;
    }

    private static string FlattenToolResult(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
            .Select(p => Str(p, "type") == "text" ? Str(p, "text") ?? "" : $"[{Str(p, "type")}]")),
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        _ => content.GetRawText(),
    };

    // ---- tolerant accessors ----

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static JsonElement Obj(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Object ? v : default;
    }

    private static string? Str(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int? Int(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    private static long? Long(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    }

    private static decimal? Dec(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    }

    private static bool? Bool(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static IReadOnlyList<string> StrArray(JsonElement e, string name)
    {
        var v = Prop(e, name);
        if (v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : Str(x, "name"))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
    }

    /// <summary>Clones so the element outlives the JsonDocument.</summary>
    private static JsonElement? Clone(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : v.Clone();
    }
}
