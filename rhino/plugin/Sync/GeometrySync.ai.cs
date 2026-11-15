using Rhino;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using System.Net;
using System.Text.Json;

namespace RhinoClient.Sync;

/// <summary>Where a pull or push should read and write. An empty pull commit means the branch tip.</summary>
public sealed record SyncTarget(string Server, string Repo, string Branch, string? Commit = null);

/// <summary>One commit the pull command or panel can offer.</summary>
public sealed record CommitChoice(string Id, string Message);

public sealed record SyncResult(bool Ok, string Message)
{
    public static SyncResult Success(string message) => new(true, message);
    public static SyncResult Fail(string message) => new(false, message);
}

public sealed class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
    public SyncException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Pulls and pushes settings, layers, and model geometry for one document.
/// The returned tasks finish off the UI thread. Await them; do not block Rhino on the result.
/// </summary>
public sealed class GeometrySync : IDisposable
{
    private readonly WorkingCopy _copy;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _clientGate = new();
    private RhinoTrunkClient? _client;
    private string? _clientServer;
    private volatile bool _disposed;

    public GeometrySync(uint documentSerialNumber) => _copy = new WorkingCopy(documentSerialNumber);

    public uint DocumentSerialNumber => _copy.DocumentSerialNumber;

    /// <summary>Reads the server, repository, and branch stored on the document. Call on Rhino's UI thread.</summary>
    public SyncTarget ReadTarget()
    {
        _copy.Read();
        return new SyncTarget(_copy.Server, _copy.Repo, _copy.Branch);
    }

    public Task<SyncResult> PushAsync(SyncTarget target, string message, CancellationToken cancellationToken = default) =>
        Run(() => PushCore(target, message, cancellationToken), cancellationToken);

    public Task<SyncResult> PullAsync(SyncTarget target, CancellationToken cancellationToken = default) =>
        Run(() => PullCore(target, cancellationToken), cancellationToken);

    /// <summary>
    /// Merges a finished agent task into the document's branch with the same pieces as push and pull:
    /// the task's result is committed to its own branch (as an agent), that branch is merged into the target
    /// branch, and the result is pulled into the document. Refuses when the document has unpushed changes.
    /// </summary>
    public Task<SyncResult> MergeTaskAsync(SyncTarget target, TaskMergeRequest task, CancellationToken cancellationToken = default) =>
        Run(() => MergeTaskCore(target, task, cancellationToken), cancellationToken);

    /// <summary>Recent commits on the branch, newest first. Does not run a pull.</summary>
    public Task<IReadOnlyList<CommitChoice>> ListCommitsAsync(SyncTarget target, CancellationToken cancellationToken = default) =>
        Task.Run(() => ListCommitsCore(target, cancellationToken), cancellationToken);

    /// <summary>Commits reachable from every ref, newest first. Does not change the document.</summary>
    public Task<GraphResponse> LoadGraphAsync(string server, string repo, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadGraphCore(server, repo, cancellationToken), cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_clientGate)
        {
            _client?.Http.Dispose();
            _client = null;
        }
    }

    private async Task<SyncResult> PushCore(SyncTarget target, string message, CancellationToken cancellationToken)
    {
        var bound = Normalize(target);
        var commitMessage = string.IsNullOrWhiteSpace(message) ? "Update geometry" : message.Trim();
        OnUi(() =>
        {
            _copy.Read();
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
        });
        var client = ClientFor(bound.Server);
        await EnsureBaseMapAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        if (_copy.HasUnsupported)
            return SyncResult.Fail("The base commit contains entities this client cannot update yet: " + _copy.UnsupportedSummary + ".");
        if (_copy.BaseCommit is not null && !_copy.HasEntries)
            return SyncResult.Fail("The base commit could not be loaded.");

        var captured = OnUi(() => DocumentMapper.Capture(_copy.Require(), _copy));
        if (captured.Changes.Count == 0)
        {
            var remembered = OnUi(() => _copy.RememberSerials(captured.Serials));
            return SyncResult.Success(WithWarning(WithSkips("Nothing to commit.", captured.Skipped), remembered));
        }

        if (_copy.BaseCommit is null)
            await EnsureRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        else
            await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        var remote = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false);
        string? expectedHead;
        if (_copy.BaseCommit is null)
        {
            if (remote is not null)
                return SyncResult.Fail($"This document is not based on '{bound.Branch}'. Pull that branch into an empty document before pushing.");
            expectedHead = null;
        }
        else if (remote is null)
        {
            return SyncResult.Fail($"Branch '{bound.Branch}' does not exist.");
        }
        else
        {
            expectedHead = _copy.BaseCommit;
        }

        if (captured.Blobs.Count > 0)
            await client.UploadAsync(bound.Repo, captured.Blobs.Values.ToList(), cancellationToken).ConfigureAwait(false);

        CommitResponse response;
        try
        {
            response = await client.CommitAsync(bound.Repo, new CommitRequest
            {
                Branch = bound.Branch,
                ExpectedHead = expectedHead,
                Message = commitMessage,
                Author = Author(),
                Changes = captured.Changes,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex) when (ex.Error.Code == ErrorCodes.NoChanges)
        {
            var remembered = OnUi(() => _copy.RememberSerials(captured.Serials));
            return SyncResult.Success(WithWarning("Nothing to commit.", remembered));
        }

        foreach (var version in response.Versions)
        {
            if (captured.Entries.TryGetValue(WorkingCopy.Key(version.Kind, version.Id), out var entry))
                entry.Version = version.Version;
        }

        var warning = OnUi(() => _copy.Replace(response.Commit, captured.Entries, captured.Serials));
        return SyncResult.Success(WithWarning(WithSkips(
            $"Pushed {captured.Changes.Count} changes to {bound.Branch} ({response.Commit}).", captured.Skipped), warning));
    }

    private async Task<SyncResult> MergeTaskCore(SyncTarget target, TaskMergeRequest task, CancellationToken cancellationToken)
    {
        var bound = Normalize(target);
        if (!File.Exists(task.ResultPath))
            return SyncResult.Fail("The task's saved result is missing, so it cannot be merged.");
        OnUi(() =>
        {
            _copy.Read();
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
        });
        if (_copy.BaseCommit is null)
            return SyncResult.Fail($"This model is not on '{bound.Branch}' yet. Push it from the Repository tab first, then run the task so it starts from a known commit.");

        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        var remote = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false)
            ?? throw new SyncException($"Branch '{bound.Branch}' does not exist.");
        await EnsureBaseMapAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        if (_copy.HasUnsupported)
            return SyncResult.Fail("The base commit contains entities this client cannot update yet: " + _copy.UnsupportedSummary + ".");

        // Pulling the merged result replaces model content, so never run it over unpushed edits.
        var local = OnUi(() => DocumentMapper.Capture(_copy.Require(), _copy));
        if (local.Changes.Count > 0)
            return SyncResult.Fail($"This model has {local.Changes.Count} unpushed changes. Push them first; pulling the merged result would overwrite them.");

        var branch = TaskBranch.Name(task);
        var existing = await client.GetBranchAsync(bound.Repo, branch, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.Commit == task.BaseCommit)
        {
            // Diff the result against the commit the task started from, not the model's current base, so
            // work pushed since the snapshot is not mistaken for deletions by the task.
            var listing = await client.ListEntitiesAsync(
                bound.Repo, task.BaseCommit, doc: Names.DefaultDoc, includeManifests: true, ct: cancellationToken).ConfigureAwait(false);
            var taskBase = new WorkingCopy(0);
            taskBase.Adopt(listing);
            if (taskBase.HasUnsupported)
                return SyncResult.Fail("The task's base commit contains entities this client cannot update yet: " + taskBase.UnsupportedSummary + ".");
            var captured = OnUi(() => CaptureResult(task.ResultPath, taskBase));
            if (captured.Changes.Count == 0)
                return SyncResult.Success(WithSkips($"'{task.Title}' made no changes, so there is nothing to merge.", captured.Skipped));

            var author = new AuthorInfo { Id = "agent-" + task.TaskId[..Math.Min(8, task.TaskId.Length)], Name = "Agent: " + task.Title, Kind = AuthorKinds.Agent };
            if (existing is null)
            {
                await client.CreateBranchAsync(bound.Repo, new CreateBranchRequest
                {
                    Name = branch,
                    From = task.BaseCommit,
                    Parent = bound.Branch,
                    Author = author,
                }, cancellationToken).ConfigureAwait(false);
            }

            if (captured.Blobs.Count > 0)
                await client.UploadAsync(bound.Repo, captured.Blobs.Values.ToList(), cancellationToken).ConfigureAwait(false);
            await client.CommitAsync(bound.Repo, new CommitRequest
            {
                Branch = branch,
                ExpectedHead = task.BaseCommit,
                Message = "Agent task: " + task.Title,
                Author = author,
                Changes = captured.Changes,
                Meta = new Dictionary<string, string> { ["task"] = task.TaskId, ["revision"] = task.Revision.ToString() },
            }, cancellationToken).ConfigureAwait(false);
        }

        MergeResponse merged;
        try
        {
            merged = await client.MergeAsync(bound.Repo, new MergeRequest
            {
                Branch = bound.Branch,
                ExpectedHead = remote.Commit,
                Theirs = branch,
                Message = "Merge agent task: " + task.Title,
                Author = Author(),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex)
        {
            return SyncResult.Fail($"Branch '{branch}' holds the task's result but could not be merged into '{bound.Branch}'. {Format(ex)} The branch is kept for review.");
        }

        // The model has no unpushed edits (checked above), so make it match the merged commit exactly.
        var pulled = await PullCore(new SyncTarget(bound.Server, bound.Repo, bound.Branch), cancellationToken, matchCommit: true).ConfigureAwait(false);
        return pulled.Ok
            ? SyncResult.Success($"Merged '{task.Title}' into {bound.Branch} via {branch} ({merged.Commit}). {pulled.Message}")
            : SyncResult.Fail($"Merged '{task.Title}' into {bound.Branch} ({merged.Commit}) but pulling it into the model failed: {pulled.Message} Try Pull.");
    }

    private static CapturedPush CaptureResult(string path, WorkingCopy taskBase)
    {
        using var doc = RhinoDoc.OpenHeadless(path) ?? throw new SyncException("Rhino could not open the task's saved result.");
        return DocumentMapper.Capture(doc, taskBase);
    }

    private async Task<SyncResult> PullCore(SyncTarget target, CancellationToken cancellationToken, bool matchCommit = false)
    {
        var bound = Normalize(target);
        OnUi(() =>
        {
            var doc = _copy.Require();
            _copy.Read();
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
            //TODO: Add Conflicts management
            //if (DocumentMapper.ModelObjectCount(doc) > 0)
            //    throw new SyncException("Pull replaces the document and only runs when it has no model objects.");
        });

        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        var remote = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false);
        if (remote is null) throw new SyncException($"Branch '{bound.Branch}' does not exist.");
        var revision = await ResolvePullRevisionAsync(client, bound, target.Commit, remote.Commit, cancellationToken).ConfigureAwait(false);
        var listed = await client.ListEntitiesAsync(
            bound.Repo, revision, doc: Names.DefaultDoc, includeManifests: true, ct: cancellationToken).ConfigureAwait(false);
        var entities = listed.Entities;
        DocumentMapper.RejectUnsupported(entities);
        var blobs = await DownloadAsync(client, bound.Repo, ComponentHashes(entities), cancellationToken).ConfigureAwait(false);
        var objectCount = entities.Count(entity => entity.Kind == EntityKinds.Object);
        var warning = OnUi(() =>
        {
            var serials = DocumentMapper.Apply(_copy.Require(), entities, blobs, matchCommit);
            var entries = new Dictionary<string, BaseEntry>(StringComparer.Ordinal);
            foreach (var entity in entities)
                entries[WorkingCopy.Key(entity.Kind, entity.Id)] = BaseEntry.From(entity);
            return _copy.Replace(listed.Commit, entries, serials);
        });
        return SyncResult.Success(WithWarning($"Pulled {bound.Branch} ({listed.Commit}, {objectCount} objects).", warning));
    }

    private async Task<IReadOnlyList<CommitChoice>> ListCommitsCore(SyncTarget target, CancellationToken cancellationToken)
    {
        var bound = Normalize(target);
        try
        {
            return await FetchCommitsAsync(bound, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (LocalServer.IsLoopback(bound.Server))
        {
            await LocalServer.EnsureRunningAsync(bound.Server, cancellationToken).ConfigureAwait(false);
            return await FetchCommitsAsync(bound, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GraphResponse> LoadGraphCore(string server, string repo, CancellationToken cancellationToken)
    {
        var bound = NormalizeRepo(server, repo);
        try
        {
            return await FetchGraphAsync(bound, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (LocalServer.IsLoopback(bound.Server))
        {
            await LocalServer.EnsureRunningAsync(bound.Server, cancellationToken).ConfigureAwait(false);
            return await FetchGraphAsync(bound, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GraphResponse> FetchGraphAsync(ServerRepo bound, CancellationToken cancellationToken)
    {
        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        return await client.GraphAsync(bound.Repo, ct: cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CommitChoice>> FetchCommitsAsync(Bound bound, CancellationToken cancellationToken)
    {
        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        var remote = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false);
        if (remote is null) throw new SyncException($"Branch '{bound.Branch}' does not exist.");
        var graph = await client.GraphAsync(bound.Repo, 30, [bound.Branch], cancellationToken).ConfigureAwait(false);
        return graph.Commits.Select(commit => new CommitChoice(commit.Id, commit.Message)).ToList();
    }

    private async Task<string> ResolvePullRevisionAsync(
        RhinoTrunkClient client, Bound bound, string? requested, string tip, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested)) return tip;
        var text = requested.Trim();
        if (ContentHash.IsValid(text))
        {
            var commit = await client.GetCommitAsync(bound.Repo, text, cancellationToken).ConfigureAwait(false);
            return commit.Id;
        }

        var graph = await client.GraphAsync(bound.Repo, 100, [bound.Branch], cancellationToken).ConfigureAwait(false);
        var byId = graph.Commits.Where(commit => IdMatches(commit.Id, text)).ToList();
        if (byId.Count == 1) return byId[0].Id;
        if (byId.Count > 1)
            throw new SyncException($"'{text}' matches {byId.Count} commits. Use the full commit id.");

        var byMessage = graph.Commits
            .Where(commit => string.Equals(commit.Message.Trim(), text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byMessage.Count == 1) return byMessage[0].Id;
        if (byMessage.Count > 1)
            throw new SyncException($"'{text}' matches {byMessage.Count} commits. Use the commit id.");
        throw new SyncException($"No commit on '{bound.Branch}' matches '{text}'.");
    }

    private static bool IdMatches(string id, string text)
    {
        const string prefix = "sha256:";
        var hex = id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;
        var typed = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text;
        return typed.Length >= 4 && hex.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureBaseMapAsync(RhinoTrunkClient client, string repo, CancellationToken cancellationToken)
    {
        if (_copy.BaseCommit is null || _copy.HasEntries || _copy.HasUnsupported) return;
        var listed = await client.ListEntitiesAsync(
            repo, _copy.BaseCommit, doc: Names.DefaultDoc, includeManifests: true, ct: cancellationToken).ConfigureAwait(false);
        OnUi(() => _copy.Adopt(listed));
    }

    private static async Task RequireRepoAsync(RhinoTrunkClient client, string repo, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetRepoAsync(repo, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SyncException($"Repository '{repo}' does not exist.");
        }
    }

    private static async Task EnsureRepoAsync(RhinoTrunkClient client, string repo, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetRepoAsync(repo, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                await client.CreateRepoAsync(repo, cancellationToken).ConfigureAwait(false);
            }
            catch (RhinoTrunkApiException created) when (created.Error.Code == ErrorCodes.RepoExists)
            {
            }
        }
    }

    private static async Task<Dictionary<string, byte[]>> DownloadAsync(
        RhinoTrunkClient client, string repo, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken)
    {
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var chunk in hashes.Chunk(1000))
        {
            var part = await client.BatchGetAsync(repo, chunk, cancellationToken).ConfigureAwait(false);
            foreach (var pair in part) blobs[pair.Key] = pair.Value;
        }

        return blobs;
    }

    private static List<string> ComponentHashes(IEnumerable<EntityVersion> entities)
    {
        var hashes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (entity.Manifest is null) continue;
            foreach (var pair in entity.Manifest.Components)
            {
                if (pair.Key is not (DocumentMapper.AttributesComponent or DocumentMapper.GeometryComponent)) continue;
                if (seen.Add(pair.Value)) hashes.Add(pair.Value);
            }
        }

        return hashes;
    }

    private Task<SyncResult> Run(Func<Task<SyncResult>> action, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (_disposed) return SyncResult.Fail("The panel has closed.");
            if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return SyncResult.Fail("A pull or push is already running.");
            try
            {
                var result = await action().ConfigureAwait(false);
                Report(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                var cancelled = SyncResult.Fail("Cancelled.");
                Report(cancelled);
                return cancelled;
            }
            catch (HttpRequestException) when (_clientServer is { } server && LocalServer.IsLoopback(server))
            {
                try
                {
                    await LocalServer.EnsureRunningAsync(server, cancellationToken).ConfigureAwait(false);
                    var result = await action().ConfigureAwait(false);
                    Report(result);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    var cancelled = SyncResult.Fail("Cancelled.");
                    Report(cancelled);
                    return cancelled;
                }
                catch (Exception retry) when (retry is SyncException or RhinoTrunkApiException or HttpRequestException or JsonException or ObjectDisposedException)
                {
                    var failed = Describe(retry);
                    Report(failed);
                    return failed;
                }
            }
            catch (Exception ex) when (ex is SyncException or RhinoTrunkApiException or HttpRequestException or JsonException or ObjectDisposedException)
            {
                var failed = Describe(ex);
                Report(failed);
                return failed;
            }
            finally
            {
                _operation.Release();
            }
        }, cancellationToken);

    private RhinoTrunkClient ClientFor(string server)
    {
        lock (_clientGate)
        {
            if (_disposed) throw new SyncException("The panel has closed.");
            if (_client is not null && _clientServer == server) return _client;
            _client?.Http.Dispose();
            _client = new RhinoTrunkClient(new Uri(server));
            _clientServer = server;
            return _client;
        }
    }

    private static ServerRepo NormalizeRepo(string server, string repo)
    {
        var name = repo.Trim();
        if (!Names.IsValidRepo(name))
            throw new SyncException("Repository name must start with a letter or digit and use only lowercase letters, digits, '.', '_' or '-'.");
        return new ServerRepo(NormalizeServer(server), name);
    }

    private static Bound Normalize(SyncTarget target)
    {
        var repo = NormalizeRepo(target.Server, target.Repo);
        var branch = target.Branch.Trim();
        if (!Names.IsValidRefName(branch))
            throw new SyncException($"Branch name '{branch}' is not valid.");
        return new Bound(repo.Server, repo.Repo, branch);
    }

    private static string NormalizeServer(string server)
    {
        var text = server.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new SyncException("Server must be an absolute http or https URL.");
        var absolute = uri.AbsoluteUri;
        return absolute.EndsWith('/') ? absolute : absolute + "/";
    }

    private static AuthorInfo Author()
    {
        var name = Environment.UserName;
        if (string.IsNullOrWhiteSpace(name)) name = "rhino";
        return new AuthorInfo { Id = name, Name = name, Kind = AuthorKinds.Human };
    }

    private static SyncResult Describe(Exception exception) => SyncResult.Fail(exception switch
    {
        RhinoTrunkApiException api => Format(api),
        HttpRequestException => "Could not reach the server. " + exception.Message,
        _ => exception.Message,
    });

    private static string Format(RhinoTrunkApiException exception)
    {
        if (exception.Error.Code == ErrorCodes.RefConflict)
            return "The branch has new commits. Update is not available yet. " + exception.Error.Message;
        if (exception.Error.Errors is not { Count: > 0 } details)
            return string.IsNullOrEmpty(exception.Error.Message)
                ? exception.Error.Code
                : exception.Error.Code + ": " + exception.Error.Message;
        var parts = details.Select(detail =>
        {
            var who = string.IsNullOrEmpty(detail.Kind) ? "" : detail.Kind + " " + detail.Id + ": ";
            return who + (string.IsNullOrEmpty(detail.Message) ? detail.Code : detail.Message);
        });
        return exception.Error.Message + " " + string.Join(" ", parts);
    }

    private static string WithSkips(string message, int skipped) => skipped == 0
        ? message
        : message + $" Skipped {skipped} objects that are blocks, layout geometry, or have no geometry.";

    private static string WithWarning(string message, string? warning) => string.IsNullOrEmpty(warning)
        ? message
        : message + " The base map was not saved beside the file: " + warning;

    private static void Report(SyncResult result)
    {
        try { OnUi(() => RhinoApp.WriteLine("RhinoClient: " + result.Message)); }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Runs on Rhino's UI thread and does not return until the action has finished.
    /// RhinoApp.InvokeOnUiThread can return before the delegate runs, which leaves the result unset.
    /// </summary>
    private static T OnUi<T>(Func<T> action)
    {
        if (!RhinoApp.InvokeRequired) return action();
        var app = Eto.Forms.Application.Instance ?? throw new SyncException("Rhino's UI is not running.");
        // An exception that escapes an action on Rhino's UI thread aborts Rhino on macOS, so catch it there
        // and rethrow it on the calling thread.
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        app.Invoke(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        failure?.Throw();
        return result;
    }

    private static void OnUi(Action action) => OnUi(() =>
    {
        action();
        return true;
    });

    private readonly record struct ServerRepo(string Server, string Repo);
    private readonly record struct Bound(string Server, string Repo, string Branch);
}
