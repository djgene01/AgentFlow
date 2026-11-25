using AgentFlow.Core.Chat;
using AgentFlow.Core.Sounds;
using Xunit;

namespace AgentFlow.ClaudeCli.Tests.Sounds;

public sealed class SoundSynthTests
{
    public static TheoryData<SoundCue> Cues()
    {
        var d = new TheoryData<SoundCue>();
        foreach (var c in Enum.GetValues<SoundCue>()) d.Add(c);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cues))]
    public void Every_cue_renders_clean_audio(SoundCue cue)
    {
        var (l, r) = SoundSynth.Render(cue);
        var seconds = (double)l.Length / SoundSynth.Rate;
        Assert.InRange(seconds, 0.1, 4.0);

        var peak = l.Concat(r).Max(Math.Abs);
        Assert.InRange(peak, 0.1, 0.9); // never clips; mastered at or below -1 dBFS
        Assert.True(Math.Abs(l[0]) < 0.01 && Math.Abs(r[0]) < 0.01, "starts without a click");
        Assert.True(Math.Abs(l[^1]) < 0.001 && Math.Abs(r[^1]) < 0.001, "ends without a click");
        Assert.All(l.Concat(r), x => Assert.False(float.IsNaN(x) || float.IsInfinity(x)));
    }

    [Fact]
    public void Wav_has_a_valid_header_and_is_the_same_every_time()
    {
        var a = SoundSynth.RenderWav(SoundCue.PermissionRequest);
        var b = SoundSynth.RenderWav(SoundCue.PermissionRequest);
        Assert.Equal(a, b);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(a, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(a, 8, 4));
        Assert.Equal(48000, BitConverter.ToInt32(a, 24));
        Assert.Equal(2, BitConverter.ToInt16(a, 22));
        Assert.Equal(a.Length - 44, BitConverter.ToInt32(a, 40));
    }

    [Fact]
    public void Every_cue_has_settings_info() =>
        Assert.Equal(Enum.GetValues<SoundCue>().Length, SoundCues.All.Select(i => i.Cue).Distinct().Count());

    [Fact]
    public void Entries_map_to_cues()
    {
        var pending = new ApprovalData("Bash", "Run ls", "{}", null, null, null, ApprovalStatus.Pending);
        Assert.Equal(SoundCue.PermissionRequest, SoundCues.ForEntry(pending, added: true, inWorkflow: false));
        Assert.Equal(SoundCue.Answered, SoundCues.ForEntry(pending with { Status = ApprovalStatus.Denied }, added: false, inWorkflow: false));
        Assert.Null(SoundCues.ForEntry(pending with { Status = ApprovalStatus.NotAnswered }, added: false, inWorkflow: false));
        Assert.Equal(SoundCue.AutoAllowed, SoundCues.ForEntry(pending with { Status = ApprovalStatus.AutoAllowed }, added: true, inWorkflow: true));

        var summary = new TurnSummaryData(null, null, "success", false, null, null, null, null, null, false);
        Assert.Equal(SoundCue.TurnFinished, SoundCues.ForEntry(summary, true, inWorkflow: false));
        Assert.Equal(SoundCue.StageFinished, SoundCues.ForEntry(summary, true, inWorkflow: true));
        Assert.Equal(SoundCue.TurnFailed, SoundCues.ForEntry(summary with { IsError = true }, true, false));
        Assert.Equal(SoundCue.Stopped, SoundCues.ForEntry(summary with { WasStopped = true, IsError = true }, true, false));
        Assert.Null(SoundCues.ForEntry(summary, added: false, inWorkflow: false));

        Assert.Equal(SoundCue.RunCompleted, SoundCues.ForEntry(new RunStatusData("r", "w", "completed", ""), true, true));
        Assert.Equal(SoundCue.RunFailed, SoundCues.ForEntry(new RunStatusData("r", "w", "failed", ""), true, true));
        Assert.Equal(SoundCue.ChangesRequested, SoundCues.ForEntry(new VerdictData("r", "s", "changes_requested", null, []), true, true));
        Assert.Equal(SoundCue.ReviewWaiting, SoundCues.ForEntry(new GateData("r", "s", "Plan", "", GateStatus.Pending), true, true));
        Assert.Null(SoundCues.ForEntry(new UserMessageData("hi", null, null, null, QueuedFor: "the next turn"), true, false));
        Assert.Null(SoundCues.ForEntry(new AssistantTextData("hi", true), true, false));
    }
}
