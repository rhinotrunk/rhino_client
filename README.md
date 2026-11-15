# RhinoClient

A dockable Rhino agent control plane. Run **RhinoClient** in Rhino's command line to open it. Start a **session** with a description; its orchestrator proposes a task tree with dependencies. Review the tasks, then run them individually or together. Each running task owns an external Codex CLI worker and an isolated headless Rhino document copy.

Git history and PR acceptance are deferred. “PR ready” currently means a saved local task result, available through **Inspect document copy**. The source model is not changed by task workers.

## Try a session

1. Ensure the Codex CLI is installed and signed in (`codex login status`). Set `RHINOCLIENT_CODEX` or the panel's executable setting if Rhino cannot find it.
2. Build and launch using the commands below, then run `RhinoClient` in Rhino.
3. Choose **New session**, describe the modeling work, and click **Start session**. For example: “Create one box from [0,0,0] to [10,10,10].”
4. The session begins with orchestration. Review its proposed tasks and click **Run task** or **Run reviewed tasks**.
5. Inspect each task's **Chat**, **Records**, and **Details**. Stop tasks independently or stop the session. Inspect the saved copy when the task finishes.

Both orchestration and workers default to **GPT-6 Luna, low reasoning effort**. **GPT-6.1 Terra** is the other budget option in settings. There is no automatic expensive fallback. The plugin uses the existing Codex CLI login; no API key is entered in the panel.

Current geometry tools cover context/object inspection, polyline/box/sphere creation, move/rotate/scale/copy, and deletion. Object/layer scope is optional; an empty scope allows edits throughout the isolated copy. Arbitrary scripts and commands are not exposed.

Worker turns stop after 512 tool calls, five consecutive tool errors, three minutes without CLI output, or 15 minutes overall. Failures retain the saved task copy and explain the reason in Records. Review and clarify or split the task before resuming; a resumed turn gets a fresh limit. Ambiguous grid dimensions and bay counts should be clarified before planning geometry.

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

`npm start` launches Rhino 8 with `RHINO_PACKAGE_DIRS` pointing to the build output. It does not use an already running Rhino instance. Restart this development instance after changing C# or rebuilding the embedded UI.

The build script finds the .NET 8 SDK on PATH or in `~/.dotnet`. Set `RHINOCLIENT_DOTNET` to an explicit `dotnet` executable if needed. Set `RHINOCLIENT_RHINO` to override the Rhino executable used by `npm start`.

Output: `rhino/plugin/bin/Debug/net8.0/RhinoClient.rhp`. For a release build, run `npm run build:release`.

## Develop the UI in a browser

```sh
npm run dev
```

Open <http://127.0.0.1:5173/panel.html>. TypeScript and CSS changes rebuild and reload the page automatically. Preview mode simulates session planning and task execution without Rhino or model requests. `npm run typecheck` checks TypeScript independently; the dev server checks types at startup, then uses esbuild for fast rebuilds.

## Live reload inside Rhino

Build the Debug plugin once with `npm run build`. Start `npm run dev` in one terminal, then run this in another:

```sh
npm run start:dev
```

Run `RhinoClient` in the new Rhino instance. This Debug-only mode loads <http://127.0.0.1:5173/panel.host.html> with the real Rhino bridge. Saving TypeScript or CSS automatically rebuilds and reloads the panel without restarting Rhino. Reloads reset view state; sessions and tasks are owned by C# and sent back when the page reconnects. C# changes still require a rebuild and Rhino restart.

Keep the dev server running while using this mode. Regular `npm start` uses the embedded UI, and Release builds always use the embedded UI.

Production uses a separate `panel.host.html`, embedded in the plugin. It never substitutes a simulated host if Rhino's bridge is unavailable. No web server or Node.js process is needed inside Rhino.

## Debug in VS Code

Open this folder, install the recommended extension, select **RhinoClient — Rhino 8**, and press F5. The launch task builds the plugin and starts Rhino with the plugin folder registered for that process. Run `RhinoClient` in Rhino. **Attach to Rhino** is also available for a development instance launched with `npm start`.

## Structure

```text
rhino/
  plugin/
    RhinoClientPlugin.ai.cs           plugin entry point and panel registration
    Commands/RhinoClientCommand.ai.cs command that opens the panel
    UI/ClientPanel.ai.cs              Eto WebView host and control-plane adapter
    Agent/                           sessions, task supervisor, Codex runner, scoped MCP tools
    UI/PanelBridge.ai.cs              JSON messages and page lifecycle
  panel/
    src/main.tsx                     React entry point
    src/app.tsx                      session list, chat, task tree and execution controls
    src/components/ui/               local shadcn/ui components
    src/protocol/events.ts           TypeScript message definitions
    src/protocol/bridge.ts           native transport and browser preview
    src/styles/panel.css             light/dark panel styles
    build.mjs                        bundle and self-contained HTML
scripts/                             build and development launch helpers
.vscode/                             build task and debugger configuration
```

TypeScript sends session, task, settings and scope commands. C# replies with `control.state`, `control.error`, selection results and `theme`, using `window.rhinoClient.receive(...)`. The native transport uses Eto's `window.eto.postMessage` on macOS and Eto's WebView2 message channel on Windows. Extend these messages to build the UI while keeping Rhino operations in C#.

All newly AI-authored C# files use `.ai.cs`, as described in `AGENTS.md`.

## UI components

The panel uses React and local shadcn/ui components with Tailwind CSS 3 for compatibility with older Rhino embedded browsers. Button, Badge, and Separator are included; their source lives in `rhino/panel/src/components/ui`. Lucide icons bundle as inline SVG. Theme tokens in `src/styles/panel.css` follow Rhino’s light/dark theme, with the OS preference used in browser preview.

To add another component, run from `rhino/panel`:

```sh
npx shadcn@latest add input
```

`components.json` configures the component locations and `@/` import alias. Import components into `src/app.tsx` or other React components. The build compiles Tailwind into the embedded HTML and watches source class changes for live reload. Restart the dev server after changing build or Tailwind configuration.

Build output, dependencies, packages, logs, local environment files, and editor caches are excluded by `.gitignore`. Keep source files, `package-lock.json`, `.env.example` templates, and the shared `.vscode` configuration in Git.
