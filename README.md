# RhinoClient

A dockable Rhino panel with one sticky header and two tabs. The header holds the repository and branch, **Pull**, **Push**, refresh, an open pull request counter, and worker settings. **Graph** (first) is the commit history with a message row for the next push. **Agents** (second) is the AI control plane: Codex workers propose changes to a copy of the model. Run **RhinoClient** in Rhino's command line to open the panel, or run the version-control commands from the command line: **RhinoClientPull**, **RhinoClientPush**, **RhinoClientBranch**, **RhinoClientMerge** and **RhinoClientCompare**.

## Graph tab and header

Click the repository chip in the header to set the server, repository, and branch. **Pull** is a three-way merge between the commit the model is on, the model, and the branch tip: changes made only on the server are applied (including deletions), changes made only in the model are kept, and objects changed on both sides are reviewed in the viewport. Only the entities that changed since the model's commit, and only their blobs, are downloaded. **Push** commits settings, materials, linetypes, groups, layers, and model geometry using the message typed in the dashed row at the top of the graph. A push only adds to the tip of a branch: when the model is behind it, the panel offers to push to a new branch instead. Notes and other entities this client does not show stay on the server untouched. Results of pull, push, and checkout are written to Rhino's command history and shown at the right of the header.

The graph draws every branch; agent commits are violet. Click a commit to preview it in the viewport against your model before pulling it: added objects green, modified blue with your version hidden, removed as red ghosts, each outlined. Click it again, or click anywhere off the graph, to clear. Previews are cached, so cycling through commits is immediate, and the meshes are stored on the server as `preview-mesh.v1` derived data so other clients do not mesh them again; hovering a commit loads it ahead of the click. Shift-click two or more commits to pick a merge in Rhino: each commit's changes are drawn in its own colour and the graph numbers the picks, click a faint object to take that version and a taken one to drop it (objects the commits change differently start undecided), and press Enter to merge the picks into your branch as one commit, or Esc to leave. Shift-clicking more commits while picking adds them to the session. Choose **Checkout** on a commit (or double-click it) to switch the model to that commit; with unpushed changes the panel asks whether to push or discard them first. Any branch tip can be merged into another branch with **Merge into…**; when objects conflict, the panel asks which side to keep.

Finished agent tasks wait above the history with **Open PR**. An open pull request shows on its agent branch as **Awaiting approval**, with Merge and Close right on the row. The header's pull request counter jumps to the first one.

## Agents tab

Start a **session** with a description; its orchestrator proposes a task tree with dependencies. Review the tasks, then run them individually or together. Each running task owns an external Codex CLI worker and an isolated headless Rhino document copy.

“Ready to merge” means a saved local task result you can preview in the viewport. The preview compares the task's copy with the snapshot it started from: added objects are green, removed ones red, changed ones amber, and everything untouched grey. **Open PR** (in the graph, or beside a finished task's “…” menu) commits the result to its own `agent/…` branch, authored as an agent and branched from the commit the task started at, and leaves it for review; the model is not changed. **Merge** on an open pull request uses the same sync pipeline as Push and Pull: it merges that branch into the model's branch with a merge commit (so history shows the task splitting off as one commit and joining back in), and pulls the merged result into the open model. The model must already be on a repository branch (push it from the Graph tab first), and it must have no unpushed changes, because the final pull makes the model match the merged commit: existing objects are updated in place and objects the commit no longer lists are removed (one undo step). If the merge conflicts, the panel asks whether to keep the model branch's or the task's version; the task branch is kept until then, and **RhinoClientMerge** on that branch steps through the conflicts one by one. **Close** drops the pull request without merging and keeps the branch. Approval rules are not part of this yet. The source model is not changed by task workers.

## Try a session

1. Ensure the Codex CLI is installed and signed in (`codex login status`). Set `RHINOCLIENT_CODEX` or the panel's executable setting if Rhino cannot find it.
2. Build and launch using the commands below, then run `RhinoClient` in Rhino.
3. Choose **New session**, describe the modeling work, and click **Start session**. For example: “Create one box from [0,0,0] to [10,10,10].”
4. The session begins with orchestration. Review its proposed tasks and click **Run task** or **Run reviewed tasks**.
5. Inspect each task's **Chat**, **Records**, and **Details**. Stop tasks independently or stop the session. Inspect the saved copy when the task finishes.

Both orchestration and workers default to **GPT-6 Luna, low reasoning effort**. **GPT-6.1 Terra** is the other budget option in settings. There is no automatic expensive fallback. The plugin uses the existing Codex CLI login; no API key is entered in the panel.

Current geometry tools cover context/object inspection, polyline/box/sphere creation, move/rotate/scale/copy, and deletion. Object/layer scope is optional; an empty scope allows edits throughout the isolated copy. Arbitrary scripts and commands are not exposed.

Worker turns stop after 512 tool calls, five consecutive tool errors, three minutes without CLI output, or 15 minutes overall. Failures retain the saved task copy and explain the reason in Records. Review and clarify or split the task before resuming; a resumed turn gets a fresh limit. The orchestrator proposes tasks right away and states any assumed dimensions, spacing or bay counts in the task instructions; it asks at most a couple of short questions, and only when it cannot plan without the answer. Review the assumptions before running.

Browser preview simulates agents and geometry and labels itself accordingly. The actual CLI/MCP transport has been tested with the budget model, including conversation continuation; full native geometry execution still needs verification inside Rhino.

## Requirements

- Rhino **8.20 or newer**, running its **.NET 8** runtime. Intel and Apple Silicon Macs are supported, as is Windows. On Windows, use `SetDotNetRuntime` to select .NET Core if needed, then restart Rhino.
- .NET **8 SDK**, Node.js **20 or newer**, and npm for development.
- VS Code with the recommended C# extension for debugging.

The RhinoCommon package is pinned to 8.19 for its .NET SDK assets. Our supported host baseline is 8.20 because it includes and defaults to .NET 8. Rhino provides RhinoCommon and Eto at runtime; they are not shipped with the plugin.

## Build and run

From this directory:

```sh
npm run setup
npm run build
npm start
```

Once Rhino opens, run `RhinoClient`. The panel uses Rhino's standard docking system: dock it with other panels or float it. Repeating the command opens/selects the existing panel.

A pull or push to a local `http://localhost` address starts the RhinoTrunk server when nothing is listening. The plugin looks for a `git_engine_rs` checkout next to itself and runs its built `rhinotrunk-server` (or `cargo run --release` the first time); it falls back to the older C# server in `git_engine`. Set `RHINOTRUNK_SERVER_EXE` to a `rhinotrunk-server` binary, or `RHINOTRUNK_SERVER_PROJECT` to a C# server `.csproj`, to choose the server, and `RHINOTRUNK_DATA` to choose its data folder.

The commands use the server, repository, and branch stored on the document; press Enter to accept them. When the panel is open for this document, a command uses the panel's checkout, so the panel status stays in step.

| Command | What it does | Options |
| --- | --- | --- |
| `RhinoClientPull` | Three-way pull of the branch tip. Conflicts are reviewed in the viewport (blue incoming, amber yours); a scripted pull stops on them instead. | `Server`, `Repository`, `Branch`, `Discard` (make the model match the branch exactly) |
| `RhinoClientPush` | Commits the model. A commit needs a saved file: an unsaved model prompts for a location, unsaved edits prompt to **Save** or **Revert** (back to the last commit). Pushing to a new branch name creates that branch at the model's commit. | `Server`, `Repository`, `Branch`, `Message`, `Path` (save location when scripting) |
| `RhinoClientBranch` | Creates a branch at the model's commit, records the current branch as its parent, and switches the model to it. | the branch name |
| `RhinoClientMerge` | Merges the model's branch into its parent (or `Into`). Shows the merged result in the viewport (green added, blue modified with the current version hidden, red removed ghosts), steps through conflicts (take the target's or the branch's version), shows rule-resolved conflicts, asks for confirmation, merges with a merge commit, and switches the model to the target. Merging into a protected branch: your confirmation is the human approval. | `Into`, `ApproveRules` (scripted merges whose conflicts merge rules resolved) |
| `RhinoClientCompare` | Shows option branches as they would land on a point of main: added green, removed red ghost, modified blue, outlined beneath the object. | `Options` (comma-separated; default every branch made from `At`), `At` (branch, tag or commit; default `main`), `Mode` (`Cycle`, `Overlay`, `Side`), `Off` |

A script can pass options, for example `_-RhinoClientPush _Repository demo _Branch main _Message "First" _Enter`. Scripted merges and pulls never prompt; they stop with a message where a person has to decide.

`npm start` launches Rhino 8 with `RHINO_PACKAGE_DIRS` pointing to the build output. It does not use an already running Rhino instance. Restart this development instance after changing C# or rebuilding the embedded UI.

The build script finds the .NET 8 SDK on PATH or in `~/.dotnet`. Set `RHINOCLIENT_DOTNET` to an explicit `dotnet` executable if needed. Set `RHINOCLIENT_RHINO` to override the Rhino executable used by `npm start`.

Output: `rhino/plugin/bin/Debug/net8.0/RhinoClient.rhp`. For a release build, run `npm run build:release`.

## Develop the UI in a browser

```sh
npm run dev
```

Open <http://127.0.0.1:5173/panel.html>. TypeScript and CSS changes rebuild and reload the page automatically. Preview mode simulates the host response: Pull and Push show a status without Rhino or the server, the history card draws a sample graph, and the Agents tab simulates session planning and task execution without Rhino or model requests. `npm run typecheck` checks TypeScript independently; the dev server checks types at startup, then uses esbuild for fast rebuilds.

## Live reload inside Rhino

Build the Debug plugin once with `npm run build`. Start `npm run dev` in one terminal, then run this in another:

```sh
npm run start:dev
```

Run `RhinoClient` in the new Rhino instance. This Debug-only mode loads <http://127.0.0.1:5173/panel.host.html> with the real Rhino bridge. Saving TypeScript or CSS automatically rebuilds and reloads the panel without restarting Rhino. Reloads reset view state; the document's saved server, repository, and branch, and the sessions and tasks owned by C#, are sent back when the page reconnects. C# changes still require a rebuild and Rhino restart.

Keep the dev server running while using this mode. Regular `npm start` uses the embedded UI, and Release builds always use the embedded UI.

Production uses a separate `panel.host.html`, embedded in the plugin. It never substitutes a simulated host if Rhino's bridge is unavailable. No web server or Node.js process is needed inside Rhino.

## Debug in VS Code

Open this folder, install the recommended extension, select **RhinoClient — Rhino 8**, and press F5. On Windows the launch task builds the plugin and starts Rhino with the plugin folder registered for that process. On Mac the same configuration builds, starts `/Applications/Rhino 8.app`, and attaches, because the debugger cannot launch that app through `dotnet`. That path uses the `rhinoClient.launch` command from the small extension in `.vscode/rhino-debug`. VS Code does not load it by itself, so install it once with `ln -s "$PWD/.vscode/rhino-debug" ~/.vscode/extensions/rhino-client.rhino-debug-0.0.1` and run **Developer: Reload Window**; without it F5 reports that the command `rhinoClient.launch` was not found. Run `RhinoClient` in Rhino. **Attach to Rhino** is also available for a development instance launched with `npm start`.

## Structure

```text
rhino/
  plugin/
    RhinoClientPlugin.ai.cs           plugin entry point and panel registration
    Commands/RhinoClientCommand.ai.cs command that opens the panel
    Commands/GeometryCommands.ai.cs   RhinoClientPull, Push, Branch, Merge and Compare, and the commit save policy
    UI/ClientPanel.ai.cs              Eto WebView host: routes git sync and agent control messages
    Sync/                             pull/push, working copy, document mapper (RhinoTrunk client)
    Agent/                            sessions, task supervisor, Codex runner, scoped MCP tools
    UI/PanelBridge.ai.cs              JSON messages and page lifecycle
  panel/
    src/main.tsx                     React entry and the Repository / Agents tab shell
    src/header.tsx, src/graph/        Sticky header, history graph, pull requests
    src/app.tsx                      Agents tab: session list, chat, task tree and execution controls
    src/graph/story.ts               oldest-first branch walk
    src/graph/render.ts              gitgraph drawing
    src/graph/fixture.ts             browser preview history
    src/components/ui/               shadcn button, input, label, card, badge, scroll area
    src/protocol/events.ts           TypeScript message definitions
    src/protocol/bridge.ts           native transport and browser preview
    src/styles/panel.css             Tailwind theme
    build.mjs                        bundle and self-contained HTML
scripts/                             build and development launch helpers
.vscode/                             build task and debugger configuration
```

Git sync: TypeScript sends `ready`, `repos`, `branches`, `pull`, `checkout`, `push`, and `graph`. C# replies with `session`, `repos`, `branches`, `status`, `graph`, and `theme`. Agents: TypeScript sends session, task, settings, scope and `task.merge` commands. C# replies with `control.state`, `control.error` and selection results. All replies use `window.rhinoClient.receive(...)`. `ClientPanel` routes each message by type and answers `ready` for both tabs. The native transport uses Eto's `window.eto.postMessage` on macOS and Eto's WebView2 message channel on Windows. Extend these messages to build the UI while keeping Rhino operations in C#.

All newly AI-authored C# files use `.ai.cs`, as described in `AGENTS.md`.

Build output, dependencies, packages, logs, local environment files, and editor caches are excluded by `.gitignore`. Keep source files, `package-lock.json`, `.env.example` templates, and the shared `.vscode` configuration in Git.
