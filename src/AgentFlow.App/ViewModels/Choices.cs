namespace AgentFlow.App.ViewModels;

public sealed record Choice(string? Value, string Label)
{
    public override string ToString() => Label;
}

public static class Choices
{
    public static IReadOnlyList<Choice> Models { get; } =
    [
        new(null, "Default model"),
        new("fable", "Fable"),
        new("opus", "Opus"),
        new("sonnet", "Sonnet"),
        new("haiku", "Haiku"),
    ];

    public static IReadOnlyList<Choice> Efforts { get; } =
    [
        new(null, "Default effort"),
        new("low", "Low effort"),
        new("medium", "Medium effort"),
        new("high", "High effort"),
        new("xhigh", "Extra-high effort"),
        new("max", "Max effort"),
    ];

    /// <summary>bypassPermissions is deliberately not offered.</summary>
    public static IReadOnlyList<Choice> PermissionModes { get; } =
    [
        new("plan", PermissionLabels.Short("plan")!),
        new("default", PermissionLabels.Short("default")!),
        new("acceptEdits", PermissionLabels.Short("acceptEdits")!),
        new("dontAsk", PermissionLabels.Short("dontAsk")!),
    ];

    public static Choice Find(IReadOnlyList<Choice> list, string? value) =>
        list.FirstOrDefault(c => c.Value == value) ?? list[0];
}

public static class PermissionLabels
{
    public static string? Short(string? mode) => mode switch
    {
        "plan" => "Read only (plan)",
        "default" => "Ask me first",
        "acceptEdits" => "Edits OK, ask for the rest",
        "dontAsk" => "Allowed tools only",
        null => null,
        _ => mode,
    };
}
