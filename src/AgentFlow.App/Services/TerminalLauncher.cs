using System.Diagnostics;
using System.IO;

namespace AgentFlow.App.Services;

/// <summary>Opens the interactive Claude Code CLI in a terminal window you control, resuming a session.</summary>
public static class TerminalLauncher
{
    public static void OpenClaudeSession(string claudeExe, string folder, string sessionId)
    {
        var command = $"& '{claudeExe.Replace("'", "''")}' --resume {sessionId}";

        // Windows Terminal when it is installed, otherwise a plain PowerShell window.
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        ProcessStartInfo psi;
        if (File.Exists(wt))
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = true };
            foreach (var a in new[] { "-d", folder, "powershell.exe", "-NoExit", "-Command", command }) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder };
            foreach (var a in new[] { "-NoExit", "-Command", command }) psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
        AppLog.Write($"Opened session {sessionId} in a terminal for {folder}");
    }
}
