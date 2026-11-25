using AgentFlow.ClaudeCli;

namespace AgentFlow.Spike;

internal static class Doctor
{
    public static async Task<int> RunAsync(string? exe)
    {
        if (exe is null) return NotFound();

        Console.WriteLine($"CLI path:   {exe}");
        var version = await ClaudeCliProbe.GetVersionAsync(exe);
        Console.WriteLine($"Version:    {version ?? "(could not read)"}");

        var auth = await ClaudeCliProbe.GetAuthStatusAsync(exe);
        Console.WriteLine($"Signed in:  {(auth.SignedIn ? "yes" : "no")}");
        Console.WriteLine($"Method:     {auth.AuthMethod ?? "(unknown)"}");
        if (!auth.SignedIn)
            Console.WriteLine("Run 'claude auth login' in a terminal to sign in with your own account.");
        return auth.SignedIn ? 0 : 1;
    }

    public static int NotFound()
    {
        Console.Error.WriteLine("The claude CLI was not found on PATH, in %USERPROFILE%\\.local\\bin or %APPDATA%\\npm.");
        Console.Error.WriteLine("Install Claude Code (see https://code.claude.com/docs/en/setup), then run 'claude auth login'.");
        Console.Error.WriteLine("Or pass --exe <path-to-claude.exe>.");
        return 3;
    }
}
