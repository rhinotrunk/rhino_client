using Rhino;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using System.Net;
using System.Text.Json;

namespace RhinoClient.Sync;

/// <summary>Where a pull or push should read and write.</summary>
public sealed record SyncTarget(string Server, string Repo, string Branch);

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

    private async Task<SyncResult> PullCore(SyncTarget target, CancellationToken cancellationToken)
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
        var listed = await client.ListEntitiesAsync(
            bound.Repo, remote.Commit, doc: Names.DefaultDoc, includeManifests: true, ct: cancellationToken).ConfigureAwait(false);
        var entities = listed.Entities;
        DocumentMapper.RejectUnsupported(entities);
        var blobs = await DownloadAsync(client, bound.Repo, ComponentHashes(entities), cancellationToken).ConfigureAwait(false);
        var objectCount = entities.Count(entity => entity.Kind == EntityKinds.Object);
        var warning = OnUi(() =>
        {
            var serials = DocumentMapper.Apply(_copy.Require(), entities, blobs);
            var entries = new Dictionary<string, BaseEntry>(StringComparer.Ordinal);
            foreach (var entity in entities)
                entries[WorkingCopy.Key(entity.Kind, entity.Id)] = BaseEntry.From(entity);
            return _copy.Replace(listed.Commit, entries, serials);
        });
        return SyncResult.Success(WithWarning($"Pulled {bound.Branch} ({listed.Commit}, {objectCount} objects).", warning));
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

    private static Bound Normalize(SyncTarget target)
    {
        var server = NormalizeServer(target.Server);
        var repo = target.Repo.Trim();
        var branch = target.Branch.Trim();
        if (!Names.IsValidRepo(repo))
            throw new SyncException("Repository name must start with a letter or digit and use only lowercase letters, digits, '.', '_' or '-'.");
        if (!Names.IsValidRefName(branch))
            throw new SyncException($"Branch name '{branch}' is not valid.");
        return new Bound(server, repo, branch);
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
        return app.Invoke(action);
    }

    private static void OnUi(Action action) => OnUi(() =>
    {
        action();
        return true;
    });

    private readonly record struct Bound(string Server, string Repo, string Branch);
}
