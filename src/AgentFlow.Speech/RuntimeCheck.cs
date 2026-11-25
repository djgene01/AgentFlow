using System.Diagnostics;

namespace AgentFlow.Speech;

/// <summary>
/// ONNX Runtime needs the Microsoft Visual C++ runtime 14.40 or newer (Visual Studio 2022 17.10+). With an older
/// one in System32 it fails to load ("A dynamic link library (DLL) initialization routine failed").
/// The installer ships Microsoft's redistributable next to AgentFlow so the Voice page can install it.
/// </summary>
public static class RuntimeCheck
{
    public static readonly Version Required = new(14, 40);

    public static Version? InstalledVcRuntime
    {
        get
        {
            var path = Path.Combine(Environment.SystemDirectory, "msvcp140.dll");
            if (!File.Exists(path)) return null;
            var v = FileVersionInfo.GetVersionInfo(path);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
        }
    }

    public static bool NeedsVcRuntime => InstalledVcRuntime is not { } v || v < Required;

    /// <summary>Microsoft's vc_redist.x64.exe, shipped next to AgentFlow.exe by build-installer.ps1.</summary>
    public static string BundledRedistributable => Path.Combine(AppContext.BaseDirectory, "vc_redist.x64.exe");

    /// <summary>A friendlier message for the usual load failures.</summary>
    public static string Explain(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        if (ex is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException
            || message.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
        {
            return NeedsVcRuntime
                ? $"The speech engine needs a newer Microsoft Visual C++ runtime (installed: {InstalledVcRuntime?.ToString() ?? "none"}, needed: {Required} or newer). Press \"Install the Visual C++ runtime\"."
                : "The speech engine (ONNX Runtime) could not load: " + message;
        }
        return message;
    }
}
