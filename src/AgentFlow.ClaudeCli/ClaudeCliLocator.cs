namespace AgentFlow.ClaudeCli;

/// <summary>Finds the claude CLI that is already installed. The app never bundles or downloads it.</summary>
public static class ClaudeCliLocator
{
    public static string? Find(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static IEnumerable<string> Candidates()
    {
        var names = new[] { "claude.exe", "claude.cmd" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
                yield return Path.Combine(dir, name);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".local", "bin", "claude.exe");   // native installer
        yield return Path.Combine(appData, "npm", "claude.cmd");          // npm global install
    }
}
