// AgentFlow.CtrlC <pid>
// Delivers Ctrl+C to one process's console, the Windows equivalent of SIGINT, without touching
// any other console. The runner starts each claude turn on its own hidden console and runs this
// helper to stop it: the helper leaves its own console, attaches to claude's, ignores Ctrl+C
// itself and raises it there. Only claude and the commands it started receive the signal.
// Exit codes: 0 sent, 1 could not attach, 2 bad arguments, 3 could not send.

using System.Runtime.InteropServices;

if (args.Length != 1 || !uint.TryParse(args[0], out var pid))
    return 2;

FreeConsole();
if (!AttachConsole(pid))
    return 1;

SetConsoleCtrlHandler(IntPtr.Zero, true);
var sent = GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
Thread.Sleep(300); // the event is delivered asynchronously; stay ignoring it until it has passed
FreeConsole();
return sent ? 0 : 3;

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool AttachConsole(uint dwProcessId);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool FreeConsole();

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
