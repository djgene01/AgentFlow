using System.Diagnostics;

namespace AgentFlow.ClaudeCli.Interop;

/// <summary>
/// Sends Ctrl+C to one claude process through the AgentFlow.CtrlC helper.
/// Each claude turn runs on its own hidden console (CreateNoWindow), so the signal reaches only
/// that process and the commands it started, never the host, its shell or `dotnet run`.
/// (The spike showed that raising Ctrl+C on a shared console stops every process on it.)
/// </summary>
internal static class ConsoleInterrupt
{
    public const string HelperFileName = "AgentFlow.CtrlC.exe";

    public static string? FindHelper(string? overridePath)
    {
        if (overridePath is not null) return File.Exists(overridePath) ? overridePath : null;
        var path = Path.Combine(AppContext.BaseDirectory, HelperFileName);
        return File.Exists(path) ? path : null;
    }

    public static async Task<bool> TrySendAsync(string helperPath, int pid)
    {
        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}
