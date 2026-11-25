using AgentFlow.ClaudeCli;
using AgentFlow.Core.Agents;
using AgentFlow.Spike;

// Phase 0 spike: proves the claude CLI integration before any UI is built.
//   doctor                      CLI path, version, sign-in state
//   run "<prompt>" [options]    one streamed turn, printed live
//   checks [options]            the five spike checks; writes a report and NDJSON fixtures
// Common options: --exe <path>  --model <m>  --effort <e>  --cwd <dir>  --mode <permission-mode>
//                 --resume <session-id>  --record <file.ndjson>  --out <dir>

var cli = SpikeArgs.Parse(args);
var exe = ClaudeCliLocator.Find(cli.Get("exe"));

switch (cli.Command)
{
    case "doctor":
        return await Doctor.RunAsync(exe);

    case "run":
        if (exe is null) return Doctor.NotFound();
        return await SingleRun.RunAsync(exe, cli);

    case "checks":
        if (exe is null) return Doctor.NotFound();
        return await SpikeChecks.RunAsync(exe, cli);

    case "approvals":
        if (exe is null) return Doctor.NotFound();
        return await ApprovalSpike.RunAsync(exe, cli);

    default:
        Console.WriteLine("""
            AgentFlow phase 0 spike

              doctor                     Show CLI path, version and sign-in state
              run "<prompt>" [options]   Run one streamed turn
              checks [options]           Run the five spike checks, write report + fixtures

            Options: --exe <path> --model <alias> --effort <level> --cwd <dir>
                     --mode <permission-mode> --resume <id> --record <file> --out <dir>
                     --model-a <alias> --model-b <alias> (checks: models for the resume test)
            """);
        return cli.Command is null ? 0 : 2;
}
