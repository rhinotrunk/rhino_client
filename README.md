# RhinoClient

A minimal dockable Rhino panel. Run **RhinoClient** to open it, or run **RhinoClientPull** and **RhinoClientPush** from the command line. Enter a server, repository, and branch, then pull or push. Pull checks the branch out into a document that has no model objects. Push commits settings, layers, and model geometry. The panel draws the repository history. The result of a pull or push is written to Rhino's command history and shown under the buttons.

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

A pull or push to a local `http://localhost` address starts the RhinoTrunk server when nothing is listening. The plugin looks for `git_engine` next to itself and runs it with `dotnet`. Set `RHINOTRUNK_SERVER_PROJECT` to the server `.csproj` if that project lives somewhere else.

`RhinoClientPull` and `RhinoClientPush` use the server, repository, and branch stored on the document. Press Enter to accept them. `Server`, `Repository`, `Branch`, and, for push, `Message` change a value first. `Commit` on pull lists recent commit messages; click one, or type a message or a commit id. Leave it empty to pull the branch tip. A script can pass those options, for example `_-RhinoClientPush _Repository demo _Branch main _Enter`. When the panel has been opened for this document, the command uses that same checkout, so the panel status stays in step with the command.

`npm start` launches Rhino 8 with `RHINO_PACKAGE_DIRS` pointing to the build output. It does not use an already running Rhino instance. Restart this development instance after changing C# or rebuilding the embedded UI.

The build script finds the .NET 8 SDK on PATH or in `~/.dotnet`. Set `RHINOCLIENT_DOTNET` to an explicit `dotnet` executable if needed. Set `RHINOCLIENT_RHINO` to override the Rhino executable used by `npm start`.

Output: `rhino/plugin/bin/Debug/net8.0/RhinoClient.rhp`. For a release build, run `npm run build:release`.

## Develop the UI in a browser

```sh
npm run dev
```

Open <http://127.0.0.1:5173/panel.html>. TypeScript and CSS changes rebuild and reload the page automatically. Preview mode simulates the host response: Pull and Push show a status without Rhino or the server, and the history card draws a sample graph. `npm run typecheck` checks TypeScript independently; the dev server checks types at startup, then uses esbuild for fast rebuilds.

## Live reload inside Rhino

Build the Debug plugin once with `npm run build`. Start `npm run dev` in one terminal, then run this in another:

```sh
npm run start:dev
```

Run `RhinoClient` in the new Rhino instance. This Debug-only mode loads <http://127.0.0.1:5173/panel.host.html> with the real Rhino bridge. Saving TypeScript or CSS automatically rebuilds and reloads the panel without restarting Rhino. Reloads reset JavaScript state; the document's saved server, repository, and branch are sent back when the page reconnects. C# changes still require a rebuild and Rhino restart.

Keep the dev server running while using this mode. Regular `npm start` uses the embedded UI, and Release builds always use the embedded UI.

Production uses a separate `panel.host.html`, embedded in the plugin. It never substitutes a simulated host if Rhino's bridge is unavailable. No web server or Node.js process is needed inside Rhino.

## Debug in VS Code

Open this folder, install the recommended extension, select **RhinoClient — Rhino 8**, and press F5. On Windows the launch task builds the plugin and starts Rhino with the plugin folder registered for that process. On Mac the same configuration builds, starts `/Applications/Rhino 8.app`, and attaches, because the debugger cannot launch that app through `dotnet`. That path uses the `rhinoClient.launch` command from `.vscode/rhino-debug`. Run `RhinoClient` in Rhino. **Attach to Rhino** is also available for a development instance launched with `npm start`.

## Structure

```text
rhino/
  plugin/
    RhinoClientPlugin.ai.cs           plugin entry point and panel registration
    Commands/RhinoClientCommand.ai.cs command that opens the panel
    Commands/GeometryCommands.ai.cs   RhinoClientPull and RhinoClientPush
    UI/ClientPanel.ai.cs              Eto WebView host and pull/push actions
    UI/PanelBridge.ai.cs              JSON messages and page lifecycle
  panel/
    src/main.tsx                     React entry
    src/panel.tsx                    pull, push, and history card
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

TypeScript sends `ready`, `repos`, `branches`, `commits`, `pull`, `push`, and `graph`. C# replies with `session`, `repos`, `branches`, `commits`, `status`, `graph`, and `theme`, using `window.rhinoClient.receive(...)`. The native transport uses Eto's `window.eto.postMessage` on macOS and Eto's WebView2 message channel on Windows. Extend these messages to build the UI while keeping Rhino operations in C#.

All newly AI-authored C# files use `.ai.cs`, as described in `AGENTS.md`.

Build output, dependencies, packages, logs, local environment files, and editor caches are excluded by `.gitignore`. Keep source files, `package-lock.json`, `.env.example` templates, and the shared `.vscode` configuration in Git.
