# RhinoClient

A minimal dockable Rhino panel. Run **RhinoClient** in Rhino's command line to open it. The panel has one **Say hello** button. Clicking it sends a JSON message to C#, writes to Rhino's command history, and shows the C# reply below the button.

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

Open <http://127.0.0.1:5173/panel.html>. TypeScript and CSS changes rebuild automatically; refresh the page to see them. Preview mode simulates the host response so the button works without Rhino. `npm run typecheck` checks TypeScript independently.

Production uses a separate `panel.host.html`, embedded in the plugin. It never substitutes a simulated host if Rhino's bridge is unavailable. No web server or Node.js process is needed inside Rhino.

## Debug in VS Code

Open this folder, install the recommended extension, select **RhinoClient — Rhino 8**, and press F5. The launch task builds the plugin and starts Rhino with the plugin folder registered for that process. Run `RhinoClient` in Rhino. **Attach to Rhino** is also available for a development instance launched with `npm start`.

## Structure

```text
rhino/
  plugin/
    RhinoClientPlugin.ai.cs           plugin entry point and panel registration
    Commands/RhinoClientCommand.ai.cs command that opens the panel
    UI/ClientPanel.ai.cs              Eto WebView host and button behavior
    UI/PanelBridge.ai.cs              JSON messages and page lifecycle
  panel/
    src/main.ts                      button and status UI
    src/protocol/events.ts           TypeScript message definitions
    src/protocol/bridge.ts           native transport and browser preview
    src/styles/panel.css             light/dark panel styles
    build.mjs                        bundle and self-contained HTML
scripts/                             build and development launch helpers
.vscode/                             build task and debugger configuration
```

TypeScript sends `ready` and `button.click`. C# replies with `state` and `theme`, using `window.rhinoClient.receive(...)`. The native transport uses Eto's `window.eto.postMessage` on macOS and Eto's WebView2 message channel on Windows. Extend these messages to build the UI while keeping Rhino operations in C#.

All newly AI-authored C# files use `.ai.cs`, as described in `AGENTS.md`.
