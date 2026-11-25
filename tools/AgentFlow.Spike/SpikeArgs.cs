namespace AgentFlow.Spike;

internal sealed class SpikeArgs
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private init; }
    public List<string> Positional { get; } = [];

    public string? Get(string name) => _options.GetValueOrDefault(name);
    public string Get(string name, string fallback) => _options.GetValueOrDefault(name) ?? fallback;

    public static SpikeArgs Parse(string[] args)
    {
        var result = new SpikeArgs { Command = args.Length > 0 ? args[0].ToLowerInvariant() : null };
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                result._options[args[i][2..]] = args[++i];
            else
                result.Positional.Add(args[i]);
        }
        return result;
    }
}
