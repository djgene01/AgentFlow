# AgentFlow guide

The full reference for AgentFlow: every feature, setting and project in the repository.
For the overview, see the [README](../README.md).

AgentFlow is a WPF frontend that runs multi-stage workflows (Plan, Develop, Review, Refine)
through the official, unmodified Claude Code CLI. It runs Claude Code; it is not made by Anthropic.

The app never calls Anthropic directly and never reads or stores credentials. Each turn is one
`claude -p --output-format stream-json` process: the prompt goes in on stdin, events come back as JSON lines.

## Layout

| Project | Purpose |
| --- | --- |
| `src/AgentFlow.Core` | Agent contracts (`IAgentRunner`, `StageTurnRequest`, `AgentEvent`), project chats, the workflow engine, approvals, Git, and the MCP integrations (`Video/` for VMCP-Studio, `Studios/` for Pixel Art Studio and SfxForge) |
| `src/AgentFlow.ClaudeCli` | CLI locator, argument builder, process runner (Job Object, Ctrl+C then kill), stream-json parser, version and auth probes |
| `src/AgentFlow.CtrlC` | Helper that raises Ctrl+C on one claude process's own console, used to stop a turn cleanly |
| `src/AgentFlow.Permissions` | The local permission server (MCP over HTTP) that brings Claude Code's permission prompts into the chat |
| `src/AgentFlow.Storage` | SQLite store (`%LOCALAPPDATA%\AgentFlow\app.db`): projects, chat items, raw CLI events, settings |
| `src/AgentFlow.Speech` | Voice: push-to-talk speech to text (NVIDIA Parakeet on ONNX Runtime with DirectML), text clean-up, and Kokoro text to speech |
| `src/AgentFlow.App` | WPF app: sidebar (projects and tools, with the jelly bounce in `Views/Jelly.cs` and `Themes/Sidebar.xaml`), one chat per project, workflows, Git, Voice, the studio pages and the Claude Code status page |
| `tools/AgentFlow.Spike` | Phase 0 console prototype |
| `tools/AgentFlow.UiSnapshot` | Dev tool: opens the main window off-screen on a throwaway database and saves PNGs |
| `tools/IconGen` | Draws the app icon and the installer's wizard images |
| `tests/FakeClaude` | Replays an NDJSON fixture to stdout, standing in for `claude -p` |
| `tests/AgentFlow.ClaudeCli.Tests` | Parser, argument builder, runner, workflow, approval, speech and studio tests |

## Build the installer

```powershell
.\build-installer.ps1
```

Produces `artifacts\AgentFlow-<version>-x64.msi`. It also carries the video studio, Pixel Art Studio and
SfxForge (see their sections), the speech engine and the Visual C++ redistributable. It publishes the app self-contained for
win-x64, so the target PC needs no .NET install, then builds `installer\AgentFlow.Installer.wixproj`
(WiX v6, restored from NuGet; nothing to install). The MSI installs per-machine into
`C:\Program Files\AgentFlow` (one admin prompt), adds Desktop and Start menu shortcuts, shows the icon in
Settings > Apps, offers "Launch AgentFlow" at the end, and upgrades older versions in place. Raise
`<Version>` in `src\AgentFlow.App\AgentFlow.App.csproj` before building a new release. Uninstalling keeps
your chats and workflows in `%LOCALAPPDATA%\AgentFlow` and `%APPDATA%\AgentFlow`.

The icon and the installer's wizard images are drawn by `tools\IconGen\make-icon.cs`:

```powershell
dotnet run tools/IconGen/make-icon.cs -- src/AgentFlow.App/Assets installer
```

WiX v7 needs the Open Source Maintenance Fee EULA accepted before it builds, so the project stays on v6.
See https://docs.firegiant.com/wix/osmf/ for whether the fee applies to you.

## Run the app

```powershell
dotnet run --project src/AgentFlow.App
```

Add a project folder, pick a model, effort and permission mode, and chat. Each project keeps one
Claude Code session: close the app, reopen it, and the next message resumes that session.
"New session" starts a fresh one; the history stays. Stop sends Ctrl+C to the running turn.

Permission modes offered: Read only (plan), Can edit files (acceptEdits), Allowed tools only (dontAsk).
Anything that would need approval asks you in the chat (see Approvals in the chat below).

While it starts, AgentFlow shows an animated pixel-art splash (`src\AgentFlow.App\Assets\splash.gif`, played
by `Views\GifAnimation.cs` because WPF only shows a GIF's first frame) for at least two loops of its loading bar.
It is drawn at a whole multiple of its 960 x 540 pixels, so it stays crisp at any display scale.

The sidebar lists your projects (a coloured tile with the first letter; a pulsing ring while Claude works;
hover for the remove button, or right-click) and the tools: Workflows, Git and GitHub, Video studio,
Pixel Art Studio, SfxForge and Voice, with Claude Code status at the bottom (a green dot when the CLI is
found and signed in). Rows bounce softly on hover and click; that motion is off when Windows' "Show
animations" setting is off.

## Workflows

Pick a workflow in the composer instead of "Direct chat", describe the task and press Run workflow.
Every stage appears in the same chat with a divider; the panel on the right shows each stage's status,
rounds, time and estimated cost.

The default workflow, `%APPDATA%\AgentFlow\workflows\plan-develop-review-refine.json`, is written on
first start and never overwritten, so you can edit it:

| Stage | Model | Effort | Permissions | Session |
| --- | --- | --- | --- | --- |
| Plan | claude-fable-5-2 | high | read only (plan mode), waits for your approval | new |
| Develop | claude-opus-5-5 | medium | can edit + build/test/git commands | new, given the approved plan |
| Review | claude-sonnet-5-5 | high | read only + build/test/git commands, structured verdict | new, given plan + git diff |
| Refine | claude-opus-5-5 | high | can edit + build/test/git commands | continues Develop's session, given the findings |

At the plan gate you can approve, edit the plan before approving, add notes for the next stage, ask
for changes (the planner revises in its own session), or cancel. "changes_requested" from the
reviewer goes to Refine and back to Review, at most twice.

### During and after a run

- **Queue:** while a stage works, type a message and press Queue: it becomes that stage's next turn in
  the same session. While the run waits at a gate, a queued message becomes a note for the next stage.
  In direct chat, queued messages are sent one after another once the current turn ends.
- **Retry / Skip:** when a run fails or is stopped, its last card offers to retry the stage it
  stopped at, or skip it and carry on.
- **Run again from here:** on the newest run, each stage header can run that stage and everything
  after it again, reusing the earlier stages' results.
- **Loop limit:** when the reviewer still asks for changes after the allowed rounds, the chat asks
  whether to run another round or finish the run with the findings open.
- **After a crash:** if AgentFlow closed mid-run, opening the project shows a card to resume it.
  An unanswered approval comes back without re-running the stage; an interrupted stage continues its
  own Claude Code session. Every claude process the app started ends with the app.
- The run's last card shows its totals: time, stage turns, tokens and estimated cost.

### Approvals in the chat

When Claude Code wants to use a tool that no rule or permission mode allows, a card appears in the
chat: **Allow once**, **Always allow** (saves a rule such as `PowerShell(node --test *)` for this
project), or **Deny** with an optional reason that Claude sees. Compound commands (`&&`, `|`, `;`,
redirection) can only be allowed once. Saved rules are listed under the chat header and passed to
every later turn as `--allowedTools`; remove one to be asked again.

How it works: the app runs a small MCP server on `127.0.0.1` (random port, a secret token per turn)
and starts each turn with `--mcp-config` and `--permission-prompt-tool mcp__agentflow__approval_prompt`.
The CLI hides that tool from Claude, so only the CLI can call it. With approvals on, the
"read only + commands" preset uses the default mode so it asks instead of denying; edits stay denied.
Clarifying questions (AskUserQuestion) are redirected to ordinary text in the chat.
On the Claude Code status page you choose what happens to permission prompts:

| Setting | Workflow runs | Direct chat |
| --- | --- | --- |
| Ask me in the chat (default) | Card, waits for you | Card, waits for you |
| Allow automatically during workflow runs (unattended) | Allowed at once, recorded as an "Allowed automatically" card | Card, waits for you |
| Allow automatically in workflow runs and direct chat | Allowed at once, recorded | Allowed at once, recorded |
| Deny automatically | Denied | Denied |

Tools that cost money (the video studio's `generate_music`) always ask, whatever the setting.
The auto-approve settings never use `bypassPermissions` and never bypass Claude Code's own checks: each stage's deny rules, the read-only
presets (Plan, Review) and plan mode are applied by the CLI before it asks, so they still hold. Nothing is
saved as an always-allow rule. The run panel shows "Unattended" while it is on.

### Everyday features

- **Search** (Ctrl+F): matches are marked in the chat; Enter / F3 for next, Shift+Enter / Shift+F3 for previous, Esc to close.
- **Export**: "Export…" in the chat header saves the whole chat as Markdown; "Export run…" on a run's last card saves just that run.
- **Open in terminal**: continues the project's Claude Code session in the interactive CLI (Windows Terminal if installed,
  otherwise PowerShell). Don't send from AgentFlow while you work there; afterwards AgentFlow carries on from that session.
- **Theme**: Match Windows, Light or Dark, on the Claude Code status page, which also lists the keyboard shortcuts.
- **Log**: errors and failed turns go to `%LOCALAPPDATA%\AgentFlow\logs` (kept 14 days); prompts and answers are not logged.

### Git and GitHub

**Git and GitHub** in the sidebar (or **Git** in a chat header) walks through the setup and shows a
checklist at the top:

1. **Git on this computer**: finds Git for Windows and Git Credential Manager (included with Git for
   Windows), and offers to set `credential.helper` to `manager` if git keeps passwords elsewhere.
2. **Your name and email**: sets the global `user.name` / `user.email` that commits record. GitHub shows
   them publicly; use your GitHub "noreply" address to keep your email private.
3. **GitHub account**: **Sign in with browser** runs `git credential-manager github login --browser`,
   so you sign in on GitHub's own page. Git Credential Manager keeps the sign-in in Windows Credential
   Manager; AgentFlow never sees a password or token. Signed-in accounts are listed with **Sign out**.
4. **Project**: **Make it a git repository** (`git init -b main`, plus a `.gitignore` if there is
   none), **Allow this folder** when git refuses a network-drive folder (adds git's own suggested
   `safe.directory` entry after you confirm), **Create on GitHub…** (opens GitHub's new-repository page
   with the name filled in; create it empty, then paste its address and press **Connect**),
   **Commit all changes**, **Push** (always asks first, then `git push -u origin <branch>`), and
   **Pull** (fast-forward only).

If a push is rejected because GitHub already has commits (typically a README added when the
repository was created), the page lists those commits and files and offers **Combine with GitHub's
commits** (merges them in, also when the histories are unrelated; on a conflict it undoes the merge and
names the files) or **Replace GitHub's version…** (asks first, then `--force-with-lease` against the
exact commit you were shown, so nothing newer is overwritten).

When the folder's history is not connected to GitHub's at all (a downloaded ZIP that was made into a new
repository), the box says so and finds which GitHub commit the files are closest to. **Put my work on top
of GitHub's…** then keeps every file exactly as it is and records it as one new commit after GitHub's
latest (`git reset --soft` plus a commit reusing your message and author), so a normal push adds it and
GitHub's history stays. Your previous commits are kept on a `backup/<branch>-before-github-history-…`
branch. It refuses while there are uncommitted changes, puts everything back if the files would not come
out identical, and warns when the folder looks like an older version than GitHub's latest.

Every command and its output appear under Activity. Because the sign-in lives in Git Credential
Manager, `git push` also works from Claude Code in a stage or chat when you allow that command.

### Building workflows

Press **Workflows** in the sidebar. The list shows your library and, if a project was open, that
project's workflows. **New** starts with a Planner stage; **Add stage** offers Planner, Developer,
Reviewer, Refiner and Custom starting points. Drag stages (or use ↑ ↓) to reorder them. For each stage
you set the name, key, model, effort, permissions, session, approval gate, verdict output, limits,
role, prompt (with **Insert variable**) and the rules for what runs next. Problems are listed live
at the top; renaming a stage key updates every reference to it. **Save to** picks your library or the
open project for a new workflow. **Delete** moves the file into a `.deleted` folder beside it.
**Import** and **Export** copy workflow JSON files in and out. The composer's workflow list
updates as soon as you save.

A project can also have its own workflows in `<project>\.workflows\*.json` (listed first, can be committed).
Prompt variables: `{{task}}`, `{{notes}}`, `{{stages.<key>.result}}`, `{{stages.<key>.findings}}`,
`{{git.diff}}`, `{{git.baseCommit}}`, `{{changedFiles}}`. The app runs `git` itself for the diff; when
git cannot read the folder, the reviewer gets the list of edited files instead.

## Sounds

AgentFlow plays a short sound for chat, workflow and git events: permission requests, reviews waiting
at a gate, replies finished or failed, stages starting and finishing, review verdicts, workflows
completed or failed, errors, and git actions. A **permission request keeps pinging** (every 5 seconds by
default) until you answer it; a stage waiting for your review reminds you every 30 seconds.

The sounds are synthesized in code (`src/AgentFlow.Core/Sounds/SoundSynth.cs`): FM bells, marimba,
pads and filtered noise, mixed in stereo through a reverb and mastered to -1 dBFS. Nothing is downloaded.
They are rendered once into `%LOCALAPPDATA%\AgentFlow\Sounds\v<version>`; bump `SoundSynth.Version`
after changing a design. **Claude Code status → Sounds** has the master switch, volume, repeat
interval, and a checkbox and **Play** button for every event.

## Voice input (push-to-talk)

Hold **Right Ctrl** (or Ctrl+Space / F8, or the 🎤 button) in a chat and speak. Text appears at the cursor
while you talk: the whole recording is transcribed again every quarter second, so earlier words are corrected
as the sentence goes on. When you let go, 250 ms more is recorded and the final text replaces the live text
(optionally sending it). Everything runs on the PC; no audio leaves it.

- **Model:** NVIDIA Parakeet TDT 0.6B (v2 English, or v3 for 25 European languages; CC-BY-4.0), in the ONNX
  export by istupakov (onnx-asr): `nemo128.onnx` (log-mel features), the encoder and the TDT decoder/joint,
  decoded greedily in `AgentFlow.Speech` (`TdtGreedy`). Downloaded from Hugging Face on the **Voice** page into
  `%LOCALAPPDATA%\AgentFlow\Speech`: full precision (2.4 GB, for a GPU) or compact int8 (0.6 GB, for a CPU).
- **Hardware:** ONNX Runtime with DirectML runs the encoder on any DirectX 12 GPU (AMD, Intel, NVIDIA);
  the preprocessor and the per-frame decoder stay on the CPU. Automatic falls back to the CPU when no GPU starts.
  The model loads when AgentFlow starts, so the first push-to-talk is instant.
- **Context:** project names are read from the project folder (file and folder names, classes and functions
  in source files), so "video studio view model" becomes `VideoStudioViewModel` and "readme dot md" `README.md`.
  Names need three spoken words, except the project's own name. "New line" / "new paragraph" insert breaks;
  filler words are dropped.
- **Visual C++ runtime:** ONNX Runtime needs 14.40 or newer. The installer ships Microsoft's
  `vc_redist.x64.exe`, and the Voice page offers to run it when the installed one is older.

## Reading replies aloud (Kokoro)

The **Voice** page can read Claude's replies aloud with **Kokoro-82M v1.0** (Apache-2.0, the onnx-community ONNX
export, about 330 MB plus 0.5 MB per voice, downloaded into `%LOCALAPPDATA%\AgentFlow\Speech\kokoro-v1.0`).

- **What is read:** top-level replies in direct chat ("every reply", or only "replies to messages I spoke" with
  push-to-talk). Workflow runs and subagent text are not read. `SpokenText` drops code blocks, tables, URLs and
  Markdown markers and reads links as their text.
- **Streaming:** complete sentences are queued while the reply is still arriving; `SpeechPlayer` synthesizes the
  next piece while one plays (24 kHz through NAudio). Push-to-talk, Esc in the chat and "Stop reading" cut it off.
- **Text to phonemes:** MisakiSharp, a C# port of misaki (Kokoro's own G2P), American or British by voice, with
  its espeak-free fallback for unknown words. Phonemes map to ids through the model's `tokenizer.json`; the voice's
  style row is chosen by the token count, as Kokoro expects. Long text is cut at spaces under 510 tokens.
- **Hardware:** the same choice as voice input (DirectML GPU or CPU), on the same ONNX Runtime.

## Video studio (MCP)

**Video studio** in the sidebar gives Claude Code the VMCP-Studio video creation tool as an MCP server
in every project chat and workflow. AgentFlow does not change the studio: for each turn it adds the
studio's own stdio server (`<studio>\mcp\server.js`) to the turn's `--mcp-config` file, next to the
permission server. The studio's server sends Claude its playbook as MCP instructions and starts the
studio's web server when a tool needs it.

- **Built in:** the installer carries the studio (its packaged app with its own `node.exe`, from the studio's
  `desktop\payload\payload.zip`; `build-installer.ps1 -VideoStudio <folder>` picks the source) in
  `<install>\VideoStudio`. It is on by default and needs no setup. Its library is
  `%LOCALAPPDATA%\AgentFlow\VideoStudio` and it runs on port 4610, so it never talks to an older
  VMCP-Studio.exe server left running on 4600. Rebuild the studio's EXE (`npm run build:exe`) before
  building AgentFlow's installer to ship a newer studio.
- **Studio folder:** the built-in copy, the source folder (run with Node from PATH, after `npm install`), or the copy
  VMCP-Studio.exe unpacks to (`%LOCALAPPDATA%\VMCP-Studio\app`, which has its own `node.exe`).
- **Port / Library folder:** passed as `VMCP_PORT` / `VMCP_DATA`. Only used when the studio's web server
  is not running yet. Point the library at an existing studio library to share its videos, voices and settings.
- **Permissions:** the planning, rendering, listing and stock-search tools (`search_stock`, `list_sfx`)
  are allowed without asking (can be turned off). `generate_music` costs credits, so it always asks, even
  in unattended workflow runs. The tools that copy or download files or design voices (`import_asset`,
  `import_music`, `import_stock`, `create_voice`) go through the normal permission prompt.
- **What the studio offers:** the classic and hand-drawn `doodle` styles, narration, music, sound effects,
  and openly licensed stock images, music and sound effects from Openverse (credits are kept with each
  video). New studio tools reach Claude without changes here; only the permission lists above name tools.
- **Test the MCP server** runs the same server the way Claude Code will (initialize and tools/list over stdio).
  **Open the studio** opens its web app (library and live render progress), starting it if needed.

## Pixel Art Studio and SfxForge (MCP)

**Pixel Art Studio** and **SfxForge** in the sidebar give Claude Code those two desktop studios as MCP servers
in every project chat and workflow, the same way as the video studio. AgentFlow does not change them; it
starts them and adds their servers to each turn's `--mcp-config` file. Both are on by default when found.

- **Pixel Art Studio** serves MCP from inside its window (`http://127.0.0.1:47821/mcp`), so AgentFlow adds it
  as an HTTP server. Before a turn, AgentFlow checks the port; if the app is closed it opens it (and waits up
  to 45 seconds), unless you turn **Open … when a turn starts** off, in which case Claude only gets it while
  you have it open. The port is passed as `PIXELARTSTUDIO_MCP_PORT`. Claude's drawing shows live and each tool
  call is one undo step in the app.
- **SfxForge** runs its stdio companion, `SfxForge.Mcp.exe --studio-url http://127.0.0.1:5174`, for each turn.
  It drives the SfxForge window when that is open and synthesizes without it when it is closed, so nothing
  has to be started first (Claude can open the window with `launch_studio_ui`).
- **Built in:** the installer carries both in `<install>\PixelArtStudio` and `<install>\SfxForge`.
  `build-installer.ps1` publishes SfxForge self-contained from its source (`-SfxForge <folder>`) and copies Pixel
  Art Studio's own `artifacts\publish` folder (`-PixelArtStudio <folder>`), because its Release build needs a Six
  Labors licence; publish Pixel Art Studio first to ship a newer one (the script warns when its source is newer).
  A copy installed by the studio's own installer (`C:\Program Files\Pixel Studio`, `C:\Program Files\SfxForge`)
  or any folder you choose works too.
- **Permissions:** tools that only work inside the app are allowed without asking (can be turned off): drawing,
  layers, frames and previews in Pixel Art Studio (each is undoable), sound design, playback and the preset
  bank in SfxForge. Tools that read or write files on disk or replace the open work ask:
  `new_sprite`, `open_file`, `save`, `export`, `add_reference_image` and `export_wav`, `delete_preset`.
  A tool a newer studio adds asks until it is put in one of the lists (`src\AgentFlow.Core\Studios\StudioApps.cs`).
- **Test the MCP server** connects the way Claude Code will and lists the tools; it names any tool not in the lists.
- Tests: `AGENTFLOW_SFXFORGE=<folder with SfxForge.Mcp.exe>` and `AGENTFLOW_PIXELSTUDIO_PORT=<port of a running
  Pixel Art Studio>` turn on checks that the real servers offer exactly the classified tools.

## Phase 0: run the spike

Prerequisites: install Claude Code and sign in with your own account.

```powershell
claude auth login
dotnet build AgentFlow.slnx
dotnet run --project tools/AgentFlow.Spike -- doctor
dotnet run --project tools/AgentFlow.Spike -- checks
```

`checks` runs the five spike checks from the development plan in a throwaway temp folder, never in a
real project, with at most a few turns per check. Defaults: `--model-a haiku --model-b sonnet --effort-b low`.
It writes `spike-output/<timestamp>/spike-report.md` and one NDJSON fixture per turn under `fixtures/`.

| Check | Question |
| --- | --- |
| 1 | Does a prompt on stdin (no prompt argument) work with `-p`? |
| 2 | Does `--json-schema` fill `structured_output` when output is `stream-json`? |
| 3 | Does `--resume` with a different `--model` and `--effort` keep the conversation? |
| 4 | Does Ctrl+C end a turn cleanly on Windows, and does the session still resume afterwards? |
| 5 | In `--permission-mode plan`, does the plan arrive as result text or in an `ExitPlanMode` tool call? |

Use `--only 4,5` to run a subset. Recorded fixtures for Claude Code 2.1.327 are already in
`tests/AgentFlow.ClaudeCli.Tests/Fixtures/recorded/2.1.327/`, with user paths replaced. After a CLI update,
re-run the checks and add a folder for the new version.

Try a single streamed turn:

```powershell
dotnet run --project tools/AgentFlow.Spike -- run "Summarise this folder" --cwd C:\some\project --model sonnet --mode plan
```

Press Ctrl+C once to stop the turn; the spike reports how the process ended.
