using System.Collections.ObjectModel;
using AgentFlow.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.App.ViewModels;

public sealed partial class StageStatusViewModel(StageDefinition stage, int number) : ObservableObject
{
    // Latest figures per stage run (a stage can run several rounds; each round is saved more than once).
    private readonly Dictionary<string, (decimal Cost, long Ms)> _byRun = new(StringComparer.Ordinal);

    public string Key => stage.Key;
    public string Title => $"{number}. {stage.Name}";
    public string Details => $"{ShortModel(stage.Model)}{(stage.Effort is null ? "" : " · " + stage.Effort)}";

    [ObservableProperty] private StageRunStatus _status = StageRunStatus.Pending;
    [ObservableProperty] private string _statusText = "Waiting";
    [ObservableProperty] private string _metrics = "";

    public decimal Cost => _byRun.Values.Sum(v => v.Cost);

    public void Apply(StageRunRecord s)
    {
        Status = s.Status;
        _byRun[s.Id] = (s.CostUsd ?? 0, s.DurationMs ?? 0);
        StatusText = s.Status switch
        {
            StageRunStatus.Running => "Running",
            StageRunStatus.AwaitingApproval => "Waiting for you",
            StageRunStatus.Succeeded => "Done",
            StageRunStatus.Failed => "Failed",
            StageRunStatus.Cancelled => "Stopped",
            _ => s.Status.ToString(),
        } + (s.Attempt > 1 ? $" · round {s.Attempt}" : "");
        var ms = _byRun.Values.Sum(v => v.Ms);
        Metrics = ms == 0 ? "" : $"{ms / 1000.0:0} s · est. ${Cost:0.00}";
    }

    /// <summary>The run ended before this stage was needed, for example Refine after a passing review.</summary>
    public void MarkNotRun(RunStatus runStatus)
    {
        if (Status != StageRunStatus.Pending) return;
        Status = StageRunStatus.Skipped;
        StatusText = runStatus == RunStatus.Completed ? "Not needed" : "Not run";
    }

    private static string ShortModel(string? m) => m switch
    {
        null => "default model",
        _ when m.StartsWith("claude-", StringComparison.Ordinal) => m["claude-".Length..].Replace('-', ' '),
        _ => m,
    };
}

/// <summary>The right-hand panel: the stages of the current (or last) run and their status.</summary>
public sealed partial class RunPanelViewModel : ObservableObject
{
    public string WorkflowName { get; }
    public ObservableCollection<StageStatusViewModel> Stages { get; } = [];

    [ObservableProperty] private string _statusText = "Running";
    [ObservableProperty] private string _totals = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _isAwaitingApproval;

    /// <summary>Permission prompts in this run are allowed without asking (the unattended setting).</summary>
    [ObservableProperty] private bool _isUnattended;

    public RunPanelViewModel(WorkflowDefinition wf)
    {
        WorkflowName = wf.Name;
        var n = 1;
        foreach (var s in wf.Stages) Stages.Add(new StageStatusViewModel(s, n++));
    }

    public void Apply(StageRunRecord s)
    {
        Stages.FirstOrDefault(x => x.Key == s.StageKey)?.Apply(s);
        Totals = $"est. ${Stages.Sum(x => x.Cost):0.00} so far";
    }

    public void Apply(RunRecord r)
    {
        IsActive = r.Status is RunStatus.Running or RunStatus.AwaitingApproval;
        IsAwaitingApproval = r.Status == RunStatus.AwaitingApproval;
        StatusText = r.Status switch
        {
            RunStatus.Running => "Running",
            RunStatus.AwaitingApproval => "Waiting for your approval",
            RunStatus.Completed => "Finished",
            RunStatus.Failed => "Stopped: a stage failed",
            RunStatus.Cancelled => "Stopped",
            _ => r.Status.ToString(),
        };
        if (!IsActive)
        {
            foreach (var s in Stages) s.MarkNotRun(r.Status);
            Totals = $"est. ${Stages.Sum(x => x.Cost):0.00} in total";
        }
    }
}
