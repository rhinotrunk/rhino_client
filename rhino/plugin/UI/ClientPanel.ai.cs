using System.Runtime.InteropServices;
using System.Text.Json;
using RhinoClient.Agent;
using Eto.Forms;
using Rhino;
using Rhino.Runtime;
using Rhino.UI;
using RhinoClient.Commands;
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
    private CancellationTokenSource? _preview;
    private PickSession? _pick;
    private bool _closed;
    private bool _disposed;
    private string _busyMessage = "Working…";

    // Rhino can reload the panel's web page (and recreate the panel) when it is re-docked or loses its host window,
    // which would bring the UI back fresh. The page's own state (tab, selection, open session) and the last status
    // line are kept here per document and handed back when the page says it is ready again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> UiStates = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string Message, bool Error, string Detail)> LastStatus = new();

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
                    if (UiStates.TryGetValue(_documentSerialNumber, out var saved))
                        _bridge.Post(new { type = "ui.state", state = System.Text.Json.Nodes.JsonNode.Parse(saved) });
                    SendSession();
                    if (_busy) SendStatus(_busyMessage, false, true);
                    else if (LastStatus.TryGetValue(_documentSerialNumber, out var last)) SendStatus(last.Message, last.Error, false, last.Detail);
                    else SendStatus("Connected to Rhino", false, false);
                    await _control.Command(command);
                    break;
                case "ui.save":
                    if (command.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                        UiStates[_documentSerialNumber] = state.GetRawText();
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
                        Text(command, "commit"),
                        Unpushed(Text(command, "unpushed")),
                        Text(command, "message"));
                    break;
                case "push":
                    if (CommitPolicy.EnsureSaved(Rhino.RhinoDoc.FromRuntimeSerialNumber(_sync.DocumentSerialNumber), interactive: false) is { } unsaved)
                    {
                        SendStatus(unsaved, true, false);
                        break;
                    }

                    _ = Push(
                        new SyncTarget(Text(command, "server"), Text(command, "repo"), Text(command, "branch")),
                        Text(command, "message"));
                    break;
                case "commit.preview":
                    if (_pick is { } running)
                    {
                        running.Update(new[] { Text(command, "commit") });
                        break;
                    }

                    PreviewCommit(Text(command, "server"), Text(command, "repo"), Text(command, "commit"));
                    break;
                case "commit.prefetch":
                    _ = _sync.PrefetchCommitAsync(Text(command, "server"), Text(command, "repo"), Text(command, "commit"), _unload.Token);
                    break;
                case "commit.preview.clear":
                    _preview?.Cancel();
                    _pick?.Cancel();
                    GeometrySync.ClearPreview();
                    SendStatus("Preview cleared.", false, _busy);
                    break;
                case "merge.pick":
                    PickMerge(new SyncTarget(Text(command, "server"), Text(command, "repo"), Text(command, "branch")), Strings(command, "commits"));
                    break;
                case "task.merge":
                    MergeTask(Text(command, "id"), Text(command, "resolve"));
                    break;
                case "branch.merge":
                    MergeBranch(Text(command, "theirs"), Text(command, "into"), Text(command, "resolve"));
                    break;
                case "task.pr.open":
                    OpenPullRequest(Text(command, "id"));
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

    internal async Task<SyncResult> Checkout(SyncTarget target, string commit, UnpushedChoice unpushed, string message)
    {
        var result = await RunAsync("Checking out…", token => _sync.CheckoutAsync(target, commit, unpushed, message, token));
        if (result.Unpushed > 0) Post(new { type = "checkout.unpushed", commit, changes = result.Unpushed });
        return result;
    }

    private static UnpushedChoice Unpushed(string value) => value switch
    {
        "push" => UnpushedChoice.Push,
        "discard" => UnpushedChoice.Discard,
        _ => UnpushedChoice.Ask,
    };

    internal Task<SyncResult> Push(SyncTarget target, string message) =>
        RunAsync("Pushing…", token => _sync.PushAsync(target, message, token));

    /// <summary>Runs a version-control operation on this panel's checkout, sharing its busy state and status line.</summary>
    internal Task<SyncResult> Run(string busyMessage, Func<GeometrySync, CancellationToken, Task<SyncResult>> action) =>
        RunAsync(busyMessage, token => action(_sync, token));

    // One-click merge of a finished agent task: same sync pipeline as Push and Pull, so it shares their busy state.
    private async void MergeTask(string id, string resolve)
    {
        if (!_control.TryBeginMerge(id, resolve is "ours" or "theirs" ? resolve : null, out var request, out var problem))
        {
            _bridge.Post(new { type = "control.error", message = problem });
            return;
        }

        var branch = TaskBranch.Name(request);
        var into = "";
        SyncResult result;
        try
        {
            var target = _sync.ReadTarget();
            into = target.Branch;
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
                if (result.Conflicts > 0)
                    _bridge.Post(new { type = "merge.conflict", id, title = request.Title, branch, into = request.Into ?? into, conflicts = result.Conflicts });
                else if (result.Compare is not null) ShowComparison(result);
                else if (!result.Ok) _bridge.Post(new { type = "control.error", message = result.Message });
            }
            catch (Exception ex)
            {
                // Never let an exception escape a UI-thread action: it aborts Rhino on macOS.
                RhinoApp.WriteLine("RhinoClient: could not record the merge result (" + ex.Message + ").");
            }
        });
    }

    // Merges any branch into another from the graph. A conflict is not an error: the panel asks which side wins.
    private async void MergeBranch(string theirs, string into, string resolve)
    {
        SyncResult result;
        try
        {
            var target = _sync.ReadTarget();
            var take = resolve is "ours" or "theirs" ? resolve : null;
            result = await RunAsync($"Merging {theirs} into {into}…", token => _sync.MergeBranchAsync(target, theirs, into, take, token));
        }
        catch (Exception ex)
        {
            result = SyncResult.Fail(ex.Message);
        }

        if (result.Conflicts > 0)
            Post(new { type = "merge.conflict", id = "", title = theirs, branch = theirs, into, conflicts = result.Conflicts });
        else if (result.Compare is not null)
            RhinoApp.InvokeOnUiThread(() => ShowComparison(result));
    }

    // A merge the server would refuse because the merged model breaks (two layers with one name, for example):
    // show the incoming branch's changes in the viewport, with the objects on the clashing layers picked out, then
    // put the merge's message back in the status line, which the compare run replaced with its own summary.
    private async void ShowComparison(SyncResult result)
    {
        if (result.Compare is not { } compare || _closed) return;
        RhinoApp.WriteLine("RhinoClient: " + compare.Explanation);
        SyncResult shown;
        try
        {
            var target = _sync.ReadTarget();
            shown = await RunAsync("Showing the clash…", token => _sync.CompareAsync(
                target, compare.Branches, compare.At, CompareMode.Cycle, interactive: false, token,
                focusLayers: compare.FocusLayers, clearHint: "click away from the graph to clear it"));
        }
        catch (Exception ex)
        {
            shown = SyncResult.Fail(ex.Message);
        }

        if (shown.Ok) Post(new { type = "preview.shown" });
        else RhinoApp.WriteLine("RhinoClient: could not show the comparison (" + shown.Message + ").");
        SendStatus(result.Message, false, _busy, compare.Explanation);
    }

    // Opens a pull request for a finished task: its result goes onto its own agent branch and waits for review.
    private async void OpenPullRequest(string id)
    {
        if (!_control.TryBeginPullRequest(id, out var request, out var problem))
        {
            _bridge.Post(new { type = "control.error", message = problem });
            return;
        }

        var branch = TaskBranch.Name(request);
        var baseBranch = "";
        SyncResult result;
        try
        {
            var target = _sync.ReadTarget();
            baseBranch = target.Branch;
            result = await RunAsync($"Opening a pull request for {request.Title}…", token => _sync.OpenPullRequestAsync(target, request, token));
        }
        catch (Exception ex)
        {
            result = SyncResult.Fail(ex.Message);
        }

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                _control.FinishPullRequest(id, result.Ok, result.Message, branch, baseBranch);
                if (!result.Ok) _bridge.Post(new { type = "control.error", message = result.Message });
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("RhinoClient: could not record the pull request (" + ex.Message + ").");
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
        _preview?.Cancel();
        // The preview is about to be discarded; the graph selection that asked for it goes too.
        if (_pick is null) _bridge.Post(new { type = "selection.clear" });
        SendStatus(_busyMessage, false, true);
        try
        {
            var result = await action(_unload.Token);
            _busy = false;
            if (result.Detached is { } detached) Post(new { type = "push.detached", commit = detached });
            SendStatus(result.Message, !result.Ok && !result.Asking, false);
            SendHead();
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
            SendHead();
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
            var head = await OnUiAsync(() => _sync.ReadBaseCommit());
            Post(new
            {
                type = "graph",
                repo = requested,
                truncated = graph.Truncated,
                head = head ?? "",
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

    private static Task<T> OnUiAsync<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        RhinoApp.InvokeOnUiThread(() =>
        {
            try { done.SetResult(action()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
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

        SendHead();
    }

    // The commit the model is on, so the history graph can mark it.
    private void SendHead()
    {
        if (_closed) return;
        RhinoApp.InvokeOnUiThread(() =>
        {
            if (_closed || _busy) return;
            try
            {
                var target = _sync.ReadTarget();
                var commit = _sync.ReadBaseCommit();
                _bridge.Post(new { type = "head", repo = target.Repo, branch = target.Branch, commit });
            }
            catch (Exception)
            {
                // Never let an exception escape a UI-thread action: it aborts Rhino on macOS.
            }
        });
    }

    /// <param name="detail">A longer explanation for the status tooltip; the header shows <paramref name="message"/>.</param>
    private void SendStatus(string message, bool error, bool busy, string? detail = null)
    {
        if (!busy) LastStatus[_documentSerialNumber] = (message, error, detail ?? "");
        if (_closed) return;
        RhinoApp.InvokeOnUiThread(() =>
        {
            if (_closed) return;
            _bridge.Post(new { type = "status", message, error, busy, detail = detail ?? "" });
        });
    }

    // A click on a history dot: cancel the previous preview and draw this one. Not serialized with pull or push.
    private async void PreviewCommit(string server, string repo, string commit)
    {
        _preview?.Cancel();
        var token = (_preview = CancellationTokenSource.CreateLinkedTokenSource(_unload.Token)).Token;
        try
        {
            var result = await _sync.PreviewCommitAsync(server, repo, commit, token);
            if (!token.IsCancellationRequested) SendStatus(result.Message, !result.Ok, _busy);
        }
        catch (Exception ex)
        {
            SendStatus(ex.Message, true, _busy);
        }
    }

    // Two or more dots selected: start merge picking in Rhino, or hand the new selection to the running session.
    private async void PickMerge(SyncTarget target, IReadOnlyList<string> commits)
    {
        _preview?.Cancel();
        if (_pick is { } running)
        {
            running.Update(commits);
            return;
        }

        var session = _pick = new PickSession(commits);
        try
        {
            await Run("Merge picking in Rhino…", (sync, token) => sync.PickMergeAsync(target, session, token));
        }
        finally
        {
            if (ReferenceEquals(_pick, session)) _pick = null;
            SendGraphRefresh();
        }
    }

    private void SendGraphRefresh()
    {
        var target = _sync.ReadTarget();
        if (!string.IsNullOrEmpty(target.Repo)) _ = SendGraph(target.Server, target.Repo);
    }

    private static IReadOnlyList<string> Strings(JsonElement command, string name) =>
        command.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(text => text.Length > 0).ToList()
            : Array.Empty<string>();

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
