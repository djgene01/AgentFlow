using System.Text;
using AgentFlow.Core.Agents;

namespace AgentFlow.Core.Chat;

/// <summary>
/// Turns the events of one agent turn into chat entries: streaming text bubbles, tool cards
/// (subagent tool calls keep their parent's id so the UI can nest them), notices and a summary.
/// Pure logic, no UI and no storage.
/// </summary>
public sealed class TurnProjector(Func<ChatEntryData, ChatEntry> create)
{
    private readonly Dictionary<string, ChatEntry> _tools = new(StringComparer.Ordinal);
    private ChatEntry? _streaming;
    private readonly StringBuilder _streamText = new();
    private bool _sawResult;

    /// <summary>Raised when a new entry is created.</summary>
    public event Action<ChatEntry>? Added;

    /// <summary>Raised when an entry's Data was replaced.</summary>
    public event Action<ChatEntry>? Updated;

    /// <summary>Raised when a rate_limit_event reports plan usage.</summary>
    public event Action<RateLimitStatus>? UsageReported;

    public string? SessionId { get; private set; }
    public string? Model { get; private set; }

    public void Apply(AgentEvent ev)
    {
        switch (ev)
        {
            case SessionStarted s:
                SessionId = s.SessionId;
                Model = s.Model;
                break;

            case TextDelta { ParentToolUseId: null } d:
                if (_streaming is null)
                {
                    _streamText.Clear();
                    _streaming = Add(new AssistantTextData("", IsStreaming: true));
                }
                _streamText.Append(d.Text);
                Update(_streaming, new AssistantTextData(_streamText.ToString(), IsStreaming: true));
                break;

            case AssistantMessage a:
                ApplyAssistant(a);
                break;

            case UserMessage u:
                foreach (var r in u.ToolResults)
                {
                    if (_tools.TryGetValue(r.ToolUseId, out var entry) && entry.Data is ToolCallData t)
                        Update(entry, t with { Result = r.Content, IsError = r.IsError });
                }
                break;

            case ApiRetry r:
                Add(new NoticeData(NoticeLevel.Warning,
                    $"API {r.Error ?? "error"}{(r.ErrorStatus is { } status ? $" ({status})" : "")}. Retry {r.Attempt} of {r.MaxRetries} in {r.RetryDelayMs / 1000.0:0.#} s."));
                break;

            case PermissionDenied p:
                Add(new NoticeData(NoticeLevel.Warning, $"Permission denied: {p.ToolName ?? "a tool call"}. Change the permission mode to allow it."));
                break;

            case RateLimitStatus rl:
                UsageReported?.Invoke(rl);
                break;

            case TurnResult r:
                _sawResult = true;
                FinishStreaming();
                SessionId = r.SessionId ?? SessionId;
                if (r.IsError && r.Subtype != "error_during_execution")
                    Add(new NoticeData(NoticeLevel.Error, string.IsNullOrWhiteSpace(r.ResultText) ? $"The turn ended with {r.Subtype}." : r.ResultText!));
                else if (r.Subtype.StartsWith("error_max", StringComparison.Ordinal))
                    Add(new NoticeData(NoticeLevel.Warning, $"Stopped: {r.Subtype.Replace('_', ' ')}."));
                Add(new TurnSummaryData(SessionId, Model, r.Subtype, r.IsError, r.DurationMs, r.NumTurns, r.TotalCostUsd,
                    r.Usage?.InputTokens, r.Usage?.OutputTokens, WasStopped: false));
                break;

            case ProcessExited x:
                FinishStreaming();
                foreach (var t in _tools.Values.Where(t => t.Data is ToolCallData { IsPending: true }))
                    Update(t, ((ToolCallData)t.Data) with { Result = "(no result: the turn ended)", IsError = true });
                if (x.WasStopped)
                    Add(new NoticeData(NoticeLevel.Info, x.StopOutcome == StopOutcome.Killed ? "Stopped (the process was ended)." : "Stopped."));
                else if (!_sawResult)
                    Add(new NoticeData(NoticeLevel.Error,
                        $"Claude Code exited with code {x.ExitCode} before finishing.{(x.StandardError.Length > 0 ? " " + x.StandardError.Trim() : "")}"));
                break;
        }
    }

    private void ApplyAssistant(AssistantMessage a)
    {
        foreach (var block in a.Content)
        {
            switch (block)
            {
                case TextBlock t when a.ParentToolUseId is null:
                    if (_streaming is not null)
                    {
                        // The complete message is authoritative over the streamed deltas.
                        Update(_streaming, new AssistantTextData(t.Text, IsStreaming: false));
                        _streaming = null;
                    }
                    else if (!string.IsNullOrWhiteSpace(t.Text))
                    {
                        Add(new AssistantTextData(t.Text, IsStreaming: false));
                    }
                    break;

                case ToolUseBlock tu when !_tools.ContainsKey(tu.Id):
                    FinishStreaming();
                    _tools[tu.Id] = Add(new ToolCallData(tu.Id, tu.Name, tu.Input.ValueKind == default ? "{}" : tu.Input.GetRawText(),
                        Result: null, IsError: false, a.ParentToolUseId));
                    break;
            }
        }
    }

    private void FinishStreaming()
    {
        if (_streaming is null) return;
        Update(_streaming, new AssistantTextData(_streamText.ToString(), IsStreaming: false));
        _streaming = null;
    }

    private ChatEntry Add(ChatEntryData data)
    {
        var e = create(data);
        Added?.Invoke(e);
        return e;
    }

    private void Update(ChatEntry e, ChatEntryData data)
    {
        e.Data = data;
        Updated?.Invoke(e);
    }
}
