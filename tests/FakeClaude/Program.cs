// FakeClaude: stands in for `claude -p` in tests. It replays an NDJSON fixture to stdout.
//   FakeClaude <fixture.ndjson> [--delay-ms N] [--exit-code N] [--hang] [--echo-args <file>] [--echo-stdin <file>] [claude args...]
// Unknown arguments (the real claude flags the runner passes) are ignored, except that
// --echo-args / --echo-stdin write what was received so tests can assert on it.

using System.Text;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: FakeClaude <fixture.ndjson> [options]");
    return 2;
}

var fixture = args[0];
var delayMs = 0;
var exitCode = 0;
var hang = false;
string? echoArgs = null, echoStdin = null;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--delay-ms": delayMs = int.Parse(args[++i]); break;
        case "--exit-code": exitCode = int.Parse(args[++i]); break;
        case "--hang": hang = true; break;
        case "--echo-args": echoArgs = args[++i]; break;
        case "--echo-stdin": echoStdin = args[++i]; break;
    }
}

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
var stdin = await Console.In.ReadToEndAsync();

if (echoArgs is not null) await File.WriteAllLinesAsync(echoArgs, args);
if (echoStdin is not null) await File.WriteAllTextAsync(echoStdin, stdin);

foreach (var line in await File.ReadAllLinesAsync(fixture))
{
    Console.Out.WriteLine(line);
    await Console.Out.FlushAsync();
    if (delayMs > 0) await Task.Delay(delayMs);
}

if (hang) await Task.Delay(Timeout.Infinite);

Console.Error.WriteLine("fake stderr line");
return exitCode;
