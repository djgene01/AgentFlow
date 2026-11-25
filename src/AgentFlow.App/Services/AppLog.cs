using System.IO;

namespace AgentFlow.App.Services;

/// <summary>
/// A small daily log in %LOCALAPPDATA%\AgentFlow\logs for errors and failed turns, kept for 14 days.
/// Prompts and answers are not written here; they live in the chat database.
/// </summary>
public static class AppLog
{
    private static readonly Lock Gate = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentFlow", "logs");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(Folder, $"agentflow-{DateTime.Now:yyyyMMdd}.log"), line);
            }
        }
        catch (IOException) { } // logging must never take the app down
        catch (UnauthorizedAccessException) { }
    }

    public static void Prune(int keepDays = 14)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.EnumerateFiles(Folder, "agentflow-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
