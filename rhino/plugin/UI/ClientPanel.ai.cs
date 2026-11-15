using System.Runtime.InteropServices;
using System.Text.Json;
using RhinoClient.Agent;
using Eto.Forms;
using Rhino;
using Rhino.Runtime;
using Rhino.UI;
using RhinoClient.Sync;

namespace RhinoClient.UI;

[Guid("d94f345a-aebb-46fb-bac5-bfce679a3943")]
public sealed class ClientPanel : Panel
{
    public static Guid PanelId => typeof(ClientPanel).GUID;

    private readonly PanelBridge _bridge;
    private readonly TaskControl _control;
    private readonly uint _documentSerialNumber;
    private readonly GeometrySync _sync;
    private CancellationTokenSource _unload = new();
    private int _graphGeneration;
    private int _reposGeneration;
    private int _branchesGeneration;
    private bool _busy;
    private bool _closed;
    private bool _disposed;
    private string _busyMessage = "Working…";

    public uint DocumentSerialNumber => _documentSerialNumber;

    public ClientPanel() : this(0) { }

    public ClientPanel(uint documentSerialNumber)
    {
        _control = TaskControl.For(documentSerialNumber);
        _control.Changed += SendState;
        _control.SelectionCaptured += SendSelection;
        _documentSerialNumber = documentSerialNumber;
        _sync = new GeometrySync(documentSerialNumber);
        var view = new WebView();
        _bridge = new PanelBridge(view, OnCommand);
        Content = view;
        Padding = 4;

#if DEBUG
        if (Environment.GetEnvironmentVariable("RHINOCLIENT_DEV") == "1")
        {
            view.Url = new Uri("http://127.0.0.1:5173/panel.host.html");
            return;
        }
#endif

        using var stream = typeof(ClientPanel).Assembly.GetManifestResourceStream("RhinoClient.panel.html")
            ?? throw new InvalidOperationException("The embedded panel is missing. Rebuild RhinoClient.");
        using var reader = new StreamReader(stream);
        view.LoadHtml(reader.ReadToEnd());
    }

    // Panel messages are routed by type: git sync commands go to GeometrySync, everything else to the
    // agent task control. "ready" is answered by both so each tab can initialise independently.
    private async void OnCommand(JsonElement command)
    {
        var type = Text(command, "type");
        try
        {
            switch (type)
            {
                case "ready":
                    SendTheme();
                    SendSession();
                    SendStatus(_busy ? _busyMessage : "Connected to Rhino", false, _busy);
                    await _control.Command(command);
                    break;
                case "repos":
                    _ = SendRepos(Text(command, "server"));
                    break;
                case "branches":
                    _ = SendBranches(Text(command, "server"), Text(command, "repo"));
                    break;
                case "graph":
                    _ = SendGraph(Text(command, "server"), Text(command, "repo"));
                    break;
                case "pull":
                    _ = Pull(new SyncTarget(Text(command, "server"), Text(command, "repo"), Text(command, "branch")));
                    break;
                case "checkout":
                    _ = Checkout(
                        new SyncTarget(Text(command, "server"), Text(command, "repo"), Text(command, "branch")),
                        Text(command, "commit"));
                    break;
                case "push":
                    _ = Push(
                        new SyncTarget(Text(command, "server"), Text(command, "repo"), Text(command, "branch")),
                        Text(command, "message"));
                    break;
                case "task.merge":
                    MergeTask(Text(command, "id"));
                    break;
                default:
                    await _control.Command(command);
                    break;
            }
        }
        catch (Exception ex)
        {
            _bridge.Post(new { type = "control.error", message = ex.Message });
        }
    }

    private void SendState() => _bridge.Post(_control.Snapshot());
    private void SendSelection(List<Guid> ids) => _bridge.Post(new { type = "scope.selection", objectIds = ids });

    internal SyncTarget ReadTarget() => _sync.ReadTarget();

    internal Task<SyncResult> Pull(SyncTarget target, bool interactive = true) =>
        RunAsync("Pulling…", token => _sync.PullAsync(target, interactive, token));

    internal Task<SyncResult> Checkout(SyncTarget target, string commit, bool interactive = true) =>
        RunAsync("Checking out…", token => _sync.CheckoutAsync(target, commit, interactive, token));

    internal Task<SyncResult> Push(SyncTarget target, string message) =>
        RunAsync("Pushing…", token => _sync.PushAsync(target, message, token));

    // One-click merge of a finished agent task: same sync pipeline as Push and Pull, so it shares their busy state.
    private async void MergeTask(string id)
    {
        if (!_control.TryBeginMerge(id, out var request, out var problem))
        {
            _bridge.Post(new { type = "control.error", message = problem });
            return;
        }

        var branch = TaskBranch.Name(request);
        SyncResult result;
        try
        {
            var target = _sync.ReadTarget();
            result = await RunAsync($"Merging {request.Title}…", token => _sync.MergeTaskAsync(target, request, token));
        }
        catch (Exception ex)
        {
            result = SyncResult.Fail(ex.Message);
        }

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                _control.FinishMerge(id, result.Ok, result.Message, branch);
                if (!result.Ok) _bridge.Post(new { type = "control.error", message = result.Message });
            }
            catch (Exception ex)
            {
                // Never let an exception escape a UI-thread action: it aborts Rhino on macOS.
                RhinoApp.WriteLine("RhinoClient: could not record the merge result (" + ex.Message + ").");
            }
        });
    }

    private async Task<SyncResult> RunAsync(string busyMessage, Func<CancellationToken, Task<SyncResult>> action)
    {
        if (_closed) return SyncResult.Fail("The panel has closed.");
        if (_busy)
        {
            var busy = SyncResult.Fail("A pull or push is already running.");
            RhinoApp.WriteLine("RhinoClient: " + busy.Message);
            return busy;
        }

        _busy = true;
        _busyMessage = busyMessage;
        SendStatus(_busyMessage, false, true);
        try
        {
            var result = await action(_unload.Token);
            _busy = false;
            SendStatus(result.Message, !result.Ok, false);
            return result;
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            if (!_closed) SendStatus("Cancelled.", true, false);
            return SyncResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            _busy = false;
            SendStatus(ex.Message, true, false);
            return SyncResult.Fail(ex.Message);
        }
    }

    private async Task SendRepos(string server)
    {
        if (_closed) return;
        var requested = server.Trim();
        var generation = ++_reposGeneration;
        try
        {
            var repos = await _sync.ListReposAsync(requested, _unload.Token);
            if (_closed || generation != _reposGeneration) return;
            Post(new { type = "repos", server = requested, repos, error = false, message = "" });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SyncException or RhinoTrunk.Client.RhinoTrunkApiException or HttpRequestException)
        {
            if (_closed || generation != _reposGeneration) return;
            Post(new { type = "repos", server = requested, repos = Array.Empty<string>(), error = true, message = ex.Message });
        }
    }

    private async Task SendBranches(string server, string repo)
    {
        if (_closed) return;
        var requestedServer = server.Trim();
        var requestedRepo = repo.Trim();
        var generation = ++_branchesGeneration;
        try
        {
            var branches = await _sync.ListBranchesAsync(requestedServer, requestedRepo, _unload.Token);
            if (_closed || generation != _branchesGeneration) return;
            Post(new { type = "branches", server = requestedServer, repo = requestedRepo, branches, error = false, message = "" });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SyncException or RhinoTrunk.Client.RhinoTrunkApiException or HttpRequestException)
        {
            if (_closed || generation != _branchesGeneration) return;
            Post(new
            {
                type = "branches",
                server = requestedServer,
                repo = requestedRepo,
                branches = Array.Empty<string>(),
                error = true,
                message = ex.Message,
            });
        }
    }

    private async Task SendGraph(string server, string repo)
    {
        if (_closed || _busy) return;
        var requested = repo.Trim();
        var generation = ++_graphGeneration;
        try
        {
            var graph = await _sync.LoadGraphAsync(server, requested, _unload.Token);
            if (_closed || generation != _graphGeneration) return;
            Post(new
            {
                type = "graph",
                repo = requested,
                truncated = graph.Truncated,
                error = false,
                message = "",
                commits = graph.Commits.Select(commit => new
                {
                    id = commit.Id,
                    parents = commit.Parents,
                    generation = commit.Generation,
                    timestamp = commit.Timestamp,
                    message = commit.Message,
                    author = new { id = commit.Author.Id, name = commit.Author.Name, kind = commit.Author.Kind },
                    refs = commit.Refs,
                }),
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SyncException or RhinoTrunk.Client.RhinoTrunkApiException or HttpRequestException)
        {
            if (_closed || generation != _graphGeneration) return;
            Post(new
            {
                type = "graph",
                repo = requested,
                truncated = false,
                error = true,
                message = ex.Message,
                commits = Array.Empty<object>(),
            });
        }
    }

    private void Post(object message)
    {
        if (_closed) return;
        RhinoApp.InvokeOnUiThread(() =>
        {
            if (_closed) return;
            _bridge.Post(message);
        });
    }

    private void SendSession()
    {
        try
        {
            var target = _sync.ReadTarget();
            _bridge.Post(new { type = "session", server = target.Server, repo = target.Repo, branch = target.Branch });
        }
        catch (SyncException ex)
        {
            SendStatus(ex.Message, true, false);
        }
    }

    private void SendStatus(string message, bool error, bool busy)
    {
        if (_closed) return;
        RhinoApp.InvokeOnUiThread(() =>
        {
            if (_closed) return;
            _bridge.Post(new { type = "status", message, error, busy });
        });
    }

    private static string Text(JsonElement command, string name) =>
        command.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private void SendTheme() => _bridge.Post(new
    {
        type = "theme",
        scheme = HostUtils.RunningInDarkMode ? "dark" : "light",
    });

    private void OnThemeChanged(object? sender, EventArgs e) => SendTheme();

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ThemeSettings.ThemeChanged += OnThemeChanged;
        // Rhino can unload and reload a docked panel (re-docking, tab switches). OnUnLoad closes the panel
        // to stop in-flight calls; reopen it here so status updates and sync calls work again.
        if (_closed && !_disposed)
        {
            _closed = false;
            if (_unload.IsCancellationRequested)
            {
                _unload.Dispose();
                _unload = new CancellationTokenSource();
            }
            SendSession();
            SendStatus(_busy ? _busyMessage : "Connected to Rhino", false, _busy);
        }
    }

    protected override void OnUnLoad(EventArgs e)
    {
        StopCalls();
        ThemeSettings.ThemeChanged -= OnThemeChanged;
        base.OnUnLoad(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            StopCalls();
            ThemeSettings.ThemeChanged -= OnThemeChanged;
            _control.Changed -= SendState;
            _control.SelectionCaptured -= SendSelection;
            _bridge.Dispose();
            _sync.Dispose();
            _unload.Dispose();
        }
        base.Dispose(disposing);
    }

    private void StopCalls()
    {
        if (_closed) return;
        _closed = true;
        _unload.Cancel();
    }
}
