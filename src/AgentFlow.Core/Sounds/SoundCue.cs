using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;

namespace AgentFlow.Core.Sounds;

/// <summary>Everything AgentFlow can make a sound for.</summary>
public enum SoundCue
{
    PermissionRequest,
    ReviewWaiting,
    Answered,
    AutoAllowed,
    MessageSent,
    TurnFinished,
    TurnFailed,
    Stopped,
    RunStarted,
    StageStarted,
    StageFinished,
    ReviewPassed,
    ChangesRequested,
    RunCompleted,
    RunFailed,
    Error,
    GitSuccess,
    GitFailed,
}

public sealed record SoundCueInfo(SoundCue Cue, string Label, string Description, int Priority, bool OnByDefault = true);

public static class SoundCues
{
    /// <summary>Settings-page names, in the order shown. Priority decides which of two near-simultaneous cues wins.</summary>
    public static IReadOnlyList<SoundCueInfo> All { get; } =
    [
        new(SoundCue.PermissionRequest, "Permission request", "Claude Code asks to do something. Repeats until you answer.", 9),
        new(SoundCue.ReviewWaiting, "Waiting for your review", "A stage finished and waits at a gate for you.", 8),
        new(SoundCue.Answered, "You answered", "You allowed, denied or approved something.", 2),
        new(SoundCue.AutoAllowed, "Allowed automatically", "The auto-approve setting allowed an action without asking.", 1),
        new(SoundCue.MessageSent, "Message sent", "Your message went to Claude Code.", 1),
        new(SoundCue.TurnFinished, "Reply finished", "Claude Code finished replying in direct chat.", 4),
        new(SoundCue.TurnFailed, "Reply failed", "A turn ended with an error.", 6),
        new(SoundCue.Stopped, "Stopped", "You stopped a turn or a run.", 5),
        new(SoundCue.RunStarted, "Workflow started", "A workflow run started or resumed.", 3),
        new(SoundCue.StageStarted, "Stage started", "The next stage of a workflow started.", 2),
        new(SoundCue.StageFinished, "Stage finished", "A workflow stage finished its turn.", 3),
        new(SoundCue.ReviewPassed, "Review passed", "A review stage's verdict is pass.", 5),
        new(SoundCue.ChangesRequested, "Changes requested", "A review stage asked for changes.", 5),
        new(SoundCue.RunCompleted, "Workflow completed", "The whole workflow finished.", 7),
        new(SoundCue.RunFailed, "Workflow failed", "A workflow run failed or was cancelled.", 7),
        new(SoundCue.Error, "Error", "An error notice in the chat.", 6),
        new(SoundCue.GitSuccess, "Git success", "A push, commit or other git action worked.", 4),
        new(SoundCue.GitFailed, "Git failed", "A git action failed.", 6),
    ];

    public static SoundCueInfo Info(SoundCue cue) => All.First(i => i.Cue == cue);

    /// <summary>The cue for a chat entry that was just added (or updated), or null for none.</summary>
    /// <param name="inWorkflow">True while a workflow run is active: turn endings are stage endings.</param>
    public static SoundCue? ForEntry(ChatEntryData data, bool added, bool inWorkflow) => (data, added) switch
    {
        (ApprovalData { Status: ApprovalStatus.Pending }, true) => SoundCue.PermissionRequest,
        (ApprovalData { Status: ApprovalStatus.AutoAllowed }, true) => SoundCue.AutoAllowed,
        (ApprovalData { Status: ApprovalStatus.Allowed or ApprovalStatus.AlwaysAllowed or ApprovalStatus.Denied }, false) => SoundCue.Answered,
        (GateData { Status: GateStatus.Pending }, true) => SoundCue.ReviewWaiting,
        (GateData { Status: GateStatus.Approved or GateStatus.ChangesRequested }, false) => SoundCue.Answered,
        // Messages queued while a turn runs, and a workflow's task, are not "sent" yet.
        (UserMessageData { QueuedFor: null }, true) when !inWorkflow => SoundCue.MessageSent,
        (TurnSummaryData { WasStopped: true }, true) => SoundCue.Stopped,
        (TurnSummaryData { IsError: true }, true) => SoundCue.TurnFailed,
        (TurnSummaryData, true) => inWorkflow ? SoundCue.StageFinished : SoundCue.TurnFinished,
        (StageDividerData, true) => SoundCue.StageStarted,
        (VerdictData v, true) => v.Verdict == Verdict.ChangesRequested ? SoundCue.ChangesRequested : SoundCue.ReviewPassed,
        (RunStatusData r, true) => r.Status switch
        {
            "started" or "resumed" => SoundCue.RunStarted,
            "completed" => SoundCue.RunCompleted,
            "cancelled" => SoundCue.Stopped,
            _ => SoundCue.RunFailed,
        },
        (NoticeData { Level: NoticeLevel.Error }, true) => SoundCue.Error,
        _ => null,
    };
}
