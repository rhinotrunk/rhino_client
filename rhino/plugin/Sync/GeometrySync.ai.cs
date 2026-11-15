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
public sealed partial class GeometrySync : IDisposable
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
        PushAsync(target, message, interactive: true, cancellationToken);

    /// <summary>
    /// Commits the document's changes. When the branch has moved on, its changes are pulled in first (keeping the
    /// document's own); objects changed on both sides are reviewed, or stop a scripted push.
    /// </summary>
    public Task<SyncResult> PushAsync(SyncTarget target, string message, bool interactive, CancellationToken cancellationToken = default) =>
        Run(() => PushCore(target, message, interactive, cancellationToken), cancellationToken);

    public Task<SyncResult> PullAsync(SyncTarget target, CancellationToken cancellationToken = default) =>
        PullAsync(target, interactive: true, cancellationToken);

    /// <summary>
    /// Merges a finished agent task into the document's branch with the same pieces as push and pull:
    /// the task's result is committed to its own branch (as an agent), that branch is merged into the target
    /// branch, and the result is pulled into the document. Refuses when the document has unpushed changes.
    /// </summary>
    public Task<SyncResult> MergeTaskAsync(SyncTarget target, TaskMergeRequest task, CancellationToken cancellationToken = default) =>
        Run(() => MergeTaskCore(target, task, cancellationToken), cancellationToken);

    /// <summary>Pulls the branch tip. An interactive pull reviews objects whose ids already differ. A scripted pull stops instead of prompting.</summary>
    public Task<SyncResult> PullAsync(SyncTarget target, bool interactive, CancellationToken cancellationToken = default) =>
        Run(() => PullCore(target, commit: null, interactive, cancellationToken), cancellationToken);

    /// <summary>Pulls the branch tip. With <paramref name="discardLocal"/> the document is made to match it exactly.</summary>
    public Task<SyncResult> PullAsync(SyncTarget target, bool interactive, bool discardLocal, CancellationToken cancellationToken = default) =>
        Run(() => PullCore(target, commit: null, interactive, cancellationToken, discardLocal), cancellationToken);

    /// <summary>Checks out a commit into the document, reviewing differing objects when interactive.</summary>
    public Task<SyncResult> CheckoutAsync(SyncTarget target, string commit, CancellationToken cancellationToken = default) =>
        CheckoutAsync(target, commit, interactive: true, cancellationToken);

    /// <summary>Checks out a commit into the document. A scripted checkout stops instead of prompting on conflicts.</summary>
    public Task<SyncResult> CheckoutAsync(SyncTarget target, string commit, bool interactive, CancellationToken cancellationToken = default) =>
        Run(() => PullCore(target, commit, interactive, cancellationToken), cancellationToken);

    /// <summary>Commits reachable from every ref, newest first. Does not change the document.</summary>
    public Task<GraphResponse> LoadGraphAsync(string server, string repo, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadGraphCore(server, repo, cancellationToken), cancellationToken);

    /// <summary>Repositories on the server, in the server's order. Does not change the document.</summary>
    public Task<IReadOnlyList<string>> ListReposAsync(string server, CancellationToken cancellationToken = default) =>
        Task.Run(() => ListReposCore(server, cancellationToken), cancellationToken);

    /// <summary>Branches in the repository, in the server's order. An unknown repository has none.</summary>
    public Task<IReadOnlyList<string>> ListBranchesAsync(string server, string repo, CancellationToken cancellationToken = default) =>
        Task.Run(() => ListBranchesCore(server, repo, cancellationToken), cancellationToken);

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

    private async Task<SyncResult> PushCore(SyncTarget target, string message, bool interactive, CancellationToken cancellationToken)
    {
        var bound = Normalize(target);
        var commitMessage = string.IsNullOrWhiteSpace(message) ? "Update geometry" : message.Trim();
        var previousBranch = OnUi(() =>
        {
            _copy.Read();
            var sameRepo = string.Equals(_copy.Server, bound.Server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_copy.Repo, bound.Repo, StringComparison.Ordinal);
            var branch = sameRepo ? _copy.Branch : null;
            if (!sameRepo && _copy.BaseCommit is not null) _copy.Detach();
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
            return branch;
        });
        var client = ClientFor(bound.Server);
        await EnsureBaseMapAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        if (_copy.BaseCommit is not null && !_copy.HasEntries)
            return SyncResult.Fail("The base commit could not be loaded.");

        string? created = null;
        string? updated = null;
        if (_copy.BaseCommit is null)
        {
            await EnsureRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
            var remote = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false);
            if (remote is not null)
                return SyncResult.Fail($"This document is not based on '{bound.Branch}'. Pull that branch first; your objects are kept as local changes.");
        }
        else
        {
            await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
            var remote = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false);
            if (remote is null)
            {
                await CreateBranchAsync(client, bound, _copy.BaseCommit, previousBranch, cancellationToken).ConfigureAwait(false);
                created = $"Created branch {bound.Branch} at {ShortCommit(_copy.BaseCommit)}.";
            }
            else if (remote.Commit != _copy.BaseCommit)
            {
                // The branch moved on: bring its changes in first (keeping ours), then push on top of it.
                var pulled = await PullCommitAsync(target, commit: null, interactive, discardLocal: false, cancellationToken).ConfigureAwait(false);
                if (!pulled.Ok) return SyncResult.Fail("The branch has new commits and updating from them failed: " + pulled.Message);
                updated = $"Updated from {bound.Branch} first.";
            }
        }

        var captured = await CaptureLocalAsync(client, bound.Repo, () => _copy.Require(), _copy, cancellationToken).ConfigureAwait(false);
        var prefix = string.Join(" ", new[] { created, updated }.Where(text => text is not null));
        if (captured.Changes.Count == 0) return NothingToCommit(captured, prefix.Length == 0 ? null : prefix);

        if (captured.Blobs.Count > 0)
            await client.UploadAsync(bound.Repo, captured.Blobs.Values.ToList(), cancellationToken).ConfigureAwait(false);

        CommitResponse response;
        try
        {
            response = await client.CommitAsync(bound.Repo, new CommitRequest
            {
                Branch = bound.Branch,
                ExpectedHead = _copy.BaseCommit,
                Message = commitMessage,
                Author = Author(),
                Changes = captured.Changes,
                Parent = _copy.BaseCommit is null ? null : previousBranch,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex) when (ex.Error.Code == ErrorCodes.NoChanges)
        {
            var remembered = OnUi(() => _copy.RememberSerials(captured.Serials));
            return SyncResult.Success(WithWarning(prefix.Length == 0 ? "Nothing to commit." : prefix, remembered));
        }

        foreach (var version in response.Versions)
        {
            if (captured.Entries.TryGetValue(WorkingCopy.Key(version.Kind, version.Id), out var entry))
                entry.Version = version.Version;
        }

        var warning = OnUi(() => _copy.Replace(response.Commit, captured.Entries, captured.Serials));
        var pushed = $"Pushed {captured.Changes.Count} changes to {bound.Branch} ({response.Commit}).";
        return SyncResult.Success(WithWarning(WithSkips(prefix.Length == 0 ? pushed : prefix + " " + pushed, captured.Skipped), warning));
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

        // Pulling the merged result replaces model content, so never run it over unpushed edits.
        var local = await CaptureLocalAsync(client, bound.Repo, () => _copy.Require(), _copy, cancellationToken).ConfigureAwait(false);
        if (local.Changes.Count > 0)
            return SyncResult.Fail($"This model has {local.Changes.Count} unpushed changes. Push them first; pulling the merged result would overwrite them.");

        var branch = TaskBranch.Name(task);
        var changeNote = "";
        var existing = await client.GetBranchAsync(bound.Repo, branch, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.Commit == task.BaseCommit)
        {
            // Diff the result against the commit the task started from, not the model's current base, so
            // work pushed since the snapshot is not mistaken for deletions by the task.
            var listing = await client.ListEntitiesAsync(
                bound.Repo, task.BaseCommit, doc: Names.DefaultDoc, includeManifests: true, ct: cancellationToken).ConfigureAwait(false);
            var taskBase = new WorkingCopy(0);
            taskBase.Adopt(listing);
            RhinoDoc? result = null;
            CapturedPush captured;
            try
            {
                result = OnUi(() => RhinoDoc.OpenHeadless(task.ResultPath)) ?? throw new SyncException("Rhino could not open the task's saved result.");
                var opened = result;
                captured = await CaptureLocalAsync(client, bound.Repo, () => opened, taskBase, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (result is not null) OnUi(() => result.Dispose());
            }

            if (captured.Changes.Count == 0)
                return SyncResult.Success(WithSkips($"'{task.Title}' made no changes, so there is nothing to merge.", captured.Skipped));

            changeNote = $" ({Describe(captured.Changes)})";
            var author = new AuthorInfo { Id = "agent:" + task.TaskId, Name = "Agent: " + task.Title, Kind = AuthorKinds.Agent };
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

        // Preview first: conflicts are reported with the objects involved, and nothing moves.
        var preview = await client.PreviewMergeAsync(bound.Repo, new MergePreviewRequest
        {
            Branch = bound.Branch,
            Theirs = branch,
            Author = Author(),
            NoFastForward = true,
        }, cancellationToken).ConfigureAwait(false);
        if (!preview.CanMerge)
            return SyncResult.Fail($"'{task.Title}' cannot be merged into '{bound.Branch}' yet: {MergeProblems(preview)} " +
                $"Its result is kept on branch '{branch}'; open it with RhinoClientMerge to resolve the conflicts.");

        MergeResponse merged;
        try
        {
            merged = await client.MergeAsync(bound.Repo, new MergeRequest
            {
                Branch = bound.Branch,
                ExpectedHead = preview.Ours,
                Theirs = preview.Theirs,
                Message = "Merge agent task: " + task.Title,
                Author = Author(),
                // Always record a merge commit, even when the branch could fast-forward, so history shows the
                // task splitting off the branch as one commit and joining back in.
                NoFastForward = true,
                ApproveRuleResolutions = preview.AutoResolved.Count > 0,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex)
        {
            return SyncResult.Fail($"Branch '{branch}' holds the task's result but could not be merged into '{bound.Branch}'. {Format(ex)} The branch is kept for review.");
        }

        // The model has no unpushed edits (checked above), so the pull makes it match the merged commit.
        var pulled = await PullCore(new SyncTarget(bound.Server, bound.Repo, bound.Branch), commit: null, interactive: false, cancellationToken).ConfigureAwait(false);
        return pulled.Ok
            ? SyncResult.Success($"Merged '{task.Title}'{changeNote} into {bound.Branch} via {branch} ({merged.Commit}). {pulled.Message}")
            : SyncResult.Fail($"Merged '{task.Title}' into {bound.Branch} ({merged.Commit}) but pulling it into the model failed: {pulled.Message} Try Pull.");
    }

    /// <summary>Conflicts and invariant violations of a merge preview, in words.</summary>
    internal static string MergeProblems(MergePreviewResponse preview)
    {
        var parts = new List<string>();
        if (preview.Conflicts.Count > 0)
            parts.Add($"{preview.Conflicts.Count} objects or layers were changed on both sides ({string.Join(", ", preview.Conflicts.Take(5).Select(c => c.Kind + " " + c.Id[..Math.Min(8, c.Id.Length)]))})");
        if (preview.Validation.Count > 0)
            parts.Add("the result would break the model: " + string.Join("; ", preview.Validation.Take(3).Select(v => v.Message)));
        return parts.Count == 0 ? "the server refused it." : string.Join("; ", parts) + ".";
    }

    // "1 added, 0 changed" style summary so it is obvious the task's commit holds only its own changes.
    private static string Describe(IReadOnlyCollection<Change> changes)
    {
        var objects = changes.Where(change => change.Kind == EntityKinds.Object).ToList();
        var deleted = objects.Count(change => change.Op == ChangeOps.Delete);
        var others = changes.Count - objects.Count;
        return $"{objects.Count - deleted} objects added or changed, {deleted} deleted" + (others > 0 ? $", {others} layers/settings" : "");
    }

    private async Task<SyncResult> PullCore(
        SyncTarget target, string? commit, bool interactive, CancellationToken cancellationToken, bool discardLocal = false)
    {
        try
        {
            return await PullCommitAsync(target, commit, interactive, discardLocal, cancellationToken).ConfigureAwait(false);
        }
        catch (PullReviewCancelledException)
        {
            return SyncResult.Fail("Cancelled.");
        }
    }

    /// <summary>
    /// Captures the document against its base. Objects whose serialized geometry differs from the base are checked
    /// again against the decoded base geometry, so a file save or a decode/encode round trip is not a change.
    /// </summary>
    private static async Task<CapturedPush> CaptureLocalAsync(
        RhinoTrunkClient client, string repo, Func<RhinoDoc> document, WorkingCopy copy, CancellationToken cancellationToken)
    {
        var first = OnUi(() => DocumentMapper.Capture(document(), copy));
        if (first.GeometryCandidates.Count == 0) return first;
        var blobs = await DownloadAsync(client, repo, first.GeometryCandidates.Values.Distinct(StringComparer.Ordinal).ToList(), cancellationToken)
            .ConfigureAwait(false);
        return OnUi(() =>
        {
            var before = DocumentMapper.DecodeBaseGeometry(first.GeometryCandidates, blobs);
            try
            {
                return DocumentMapper.Capture(document(), copy, before);
            }
            finally
            {
                foreach (var geometry in before.Values) geometry.Dispose();
            }
        });
    }

    private static string KindOf(string key) => key[..key.IndexOf('\n')];

    private static string IdOf(string key) => key[(key.IndexOf('\n') + 1)..];

    /// <summary>
    /// Three-way pull: base (the commit the document is at), local (the document) and target (the branch tip or a
    /// checked-out commit). Changes made only on the server are applied, including deletes; changes made only in the
    /// document are kept; objects changed on both sides are reviewed (or stop a scripted pull). With
    /// <paramref name="discardLocal"/> the document is made to match the target exactly.
    /// </summary>
    private async Task<SyncResult> PullCommitAsync(SyncTarget target, string? commit, bool interactive, bool discardLocal, CancellationToken cancellationToken)
    {
        var checkout = commit is not null;
        string revision;
        Bound bound;
        if (checkout)
        {
            var repo = NormalizeRepo(target.Server, target.Repo);
            var text = commit!.Trim();
            if (!ContentHash.IsValid(text)) throw new SyncException("Commit id is not valid.");
            var requestedBranch = target.Branch.Trim();
            if (requestedBranch.Length > 0 && !Names.IsValidRefName(requestedBranch))
                throw new SyncException($"Branch name '{requestedBranch}' is not valid.");
            var savedBranch = OnUi(() =>
            {
                Rebind(repo.Server, repo.Repo);
                var activeBranch = requestedBranch.Length > 0 ? requestedBranch : _copy.Branch;
                _copy.UseTarget(repo.Server, repo.Repo, activeBranch);
                return activeBranch;
            });
            bound = new Bound(repo.Server, repo.Repo, savedBranch);
            var checkoutClient = ClientFor(bound.Server);
            await RequireRepoAsync(checkoutClient, bound.Repo, cancellationToken).ConfigureAwait(false);
            revision = (await checkoutClient.GetCommitAsync(bound.Repo, text, cancellationToken).ConfigureAwait(false)).Id;
        }
        else
        {
            bound = Normalize(target);
            OnUi(() =>
            {
                Rebind(bound.Server, bound.Repo);
                _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
            });
            var pullClient = ClientFor(bound.Server);
            await RequireRepoAsync(pullClient, bound.Repo, cancellationToken).ConfigureAwait(false);
            var remote = await pullClient.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false);
            if (remote is null) throw new SyncException($"Branch '{bound.Branch}' does not exist.");
            revision = remote.Commit;
        }

        var client = ClientFor(bound.Server);
        await EnsureBaseMapAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        var baseCommit = _copy.HasEntries ? _copy.BaseCommit : null;
        var local = await CaptureLocalAsync(client, bound.Repo, () => _copy.Require(), _copy, cancellationToken).ConfigureAwait(false);
        var localKeys = DocumentMapper.ChangedKeys(local);

        // The target state, and the supported entities that differ between the base and the target.
        var targetEntries = new Dictionary<string, BaseEntry>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, EntityVersion?>(StringComparer.Ordinal);
        string? skipped = null;
        if (baseCommit is null)
        {
            var listed = await client.ListEntitiesAsync(
                bound.Repo, revision, doc: Names.DefaultDoc, includeManifests: true, ct: cancellationToken).ConfigureAwait(false);
            foreach (var entity in listed.Entities) targetEntries[WorkingCopy.Key(entity.Kind, entity.Id)] = BaseEntry.From(entity);
            foreach (var entity in DocumentMapper.Supported(listed.Entities, complete: true, out skipped))
                incoming[WorkingCopy.Key(entity.Kind, entity.Id)] = entity;
        }
        else
        {
            foreach (var entry in _copy.Entries) targetEntries[WorkingCopy.Key(entry.Kind, entry.Id)] = entry.Copy();
            if (baseCommit != revision)
            {
                var diff = await client.DiffAsync(bound.Repo, baseCommit, revision, includeManifests: true, cancellationToken).ConfigureAwait(false);
                var changed = new List<EntityVersion>();
                foreach (var change in diff.Changes)
                {
                    if (change.Doc != Names.DefaultDoc) continue;
                    var key = WorkingCopy.Key(change.Kind, change.Id);
                    if (change.To is null)
                    {
                        targetEntries.Remove(key);
                        if (DocumentMapper.IsSupported(change.Kind, change.FromManifest?.Subtype)) incoming[key] = null;
                        continue;
                    }

                    var entity = new EntityVersion { Doc = change.Doc, Kind = change.Kind, Id = change.Id, Version = change.To, Manifest = change.ToManifest };
                    targetEntries[key] = BaseEntry.From(entity);
                    changed.Add(entity);
                }

                foreach (var entity in DocumentMapper.Supported(changed, complete: false, out skipped))
                    incoming[WorkingCopy.Key(entity.Kind, entity.Id)] = entity;
            }
        }

        if (discardLocal)
        {
            // Local changes the target does not touch go back to the target's version, or away.
            foreach (var key in localKeys)
            {
                if (incoming.ContainsKey(key)) continue;
                incoming[key] = targetEntries.TryGetValue(key, out var entry) && DocumentMapper.IsSupported(entry.Kind, entry.Subtype)
                    ? entry.ToEntityVersion()
                    : null;
            }

            localKeys.Clear();
        }

        // Classify every incoming change.
        var apply = new List<(string Key, EntityVersion? Entity)>();
        var conflictKeys = new List<string>();
        var keptLocalTables = 0;
        foreach (var (key, entity) in incoming)
        {
            if (!localKeys.Contains(key))
            {
                apply.Add((key, entity));
                continue;
            }

            if (DocumentMapper.LocalEquals(local, key, entity is null ? null : targetEntries[key]))
            {
                localKeys.Remove(key); // the same change on both sides
                continue;
            }

            if (KindOf(key) == EntityKinds.Object)
            {
                conflictKeys.Add(key);
            }
            else if (baseCommit is null)
            {
                localKeys.Remove(key); // an unlinked document takes the commit's settings and layers
                apply.Add((key, entity));
            }
            else
            {
                keptLocalTables++; // a layer or the settings changed on both sides: the document's version stays, to be pushed
            }
        }

        var needed = apply.Where(item => item.Entity is not null).Select(item => item.Entity!)
            .Concat(conflictKeys.Select(key => incoming[key]).Where(entity => entity is not null).Select(entity => entity!))
            .ToList();
        var blobs = await DownloadAsync(client, bound.Repo, ComponentHashes(needed), cancellationToken).ConfigureAwait(false);

        var result = OnUi(() =>
        {
            var doc = _copy.Require();
            var objectPuts = needed.Where(entity => entity.Kind == EntityKinds.Object).ToList();
            var decoded = DocumentMapper.DecodeObjects(objectPuts, blobs);
            var decodedById = decoded.ToDictionary(item => item.Entity.Id, StringComparer.Ordinal);
            var conflicts = new List<ObjectConflict>();
            try
            {
                var deletedObjects = DocumentMapper.DeletedModelObjects(doc);
                foreach (var key in conflictKeys)
                {
                    var id = IdOf(key);
                    var guid = DocumentMapper.ParseObjectId(id);
                    var live = doc.Objects.FindId(guid);
                    var existing = live is { IsDeleted: false } ? live : deletedObjects.GetValueOrDefault(guid);
                    conflicts.Add(ObjectConflict.Create(id, guid, existing is null || existing.IsDeleted, existing?.Geometry,
                        decodedById.TryGetValue(id, out var item) ? item.Geometry : null));
                }

                IReadOnlyDictionary<string, ConflictChoice> choices = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
                if (conflicts.Count > 0)
                {
                    if (!interactive) throw new SyncException(PullConflicts.ScriptedMessage(conflicts));
                    choices = PullConflicts.Review(doc, conflicts);
                }

                var changes = new IncomingChanges { RemoveEmptyLocalLayers = baseCommit is null && !discardLocal };
                foreach (var key in targetEntries.Keys.Where(key => KindOf(key) == EntityKinds.Layer))
                    changes.TargetLayers.Add(Guid.Parse(IdOf(key)));
                foreach (var (key, entity) in apply)
                {
                    var id = IdOf(key);
                    var kind = KindOf(key);
                    if (entity is null)
                    {
                        if (kind == EntityKinds.Object) changes.ObjectDeletes.Add(id);
                        else if (kind == EntityKinds.Layer) changes.LayerDeletes.Add(id);
                        else if (DocumentMapper.IsTableKind(kind)) changes.TableDeletes.Add((kind, id));
                        continue;
                    }

                    if (kind == EntityKinds.Settings) changes.Settings = DocumentMapper.RequireAttributes(entity, blobs);
                    else if (kind == EntityKinds.Layer) changes.LayerPuts.Add(new LayerPut(entity, DocumentMapper.RequireAttributes(entity, blobs)));
                    else if (DocumentMapper.IsTableKind(kind)) changes.TablePuts.Add((entity, DocumentMapper.RequireAttributes(entity, blobs)));
                    else changes.ObjectPuts.Add((decodedById[id], ConflictChoice.KeepIncoming));
                }

                foreach (var key in conflictKeys)
                {
                    var id = IdOf(key);
                    var choice = choices.TryGetValue(id, out var picked) ? picked : ConflictChoice.KeepExisting;
                    if (decodedById.TryGetValue(id, out var item)) changes.ObjectPuts.Add((item, choice));
                    else if (choice == ConflictChoice.KeepIncoming) changes.ObjectDeletes.Add(id);
                    if (choice == ConflictChoice.KeepIncoming) localKeys.Remove(key);
                }

                var outcome = changes.IsEmpty ? new ApplyOutcome() : DocumentMapper.ApplyChanges(doc, changes);
                // Template layers of an unlinked document that the pull removed are no longer local changes.
                localKeys.RemoveWhere(key => KindOf(key) == EntityKinds.Layer && doc.Layers.FindId(Guid.Parse(IdOf(key))) is not { IsDeleted: false });

                // A serial number vouches that an object still equals its entry, so objects with local changes get none.
                var serials = new Dictionary<string, uint>(local.Serials, StringComparer.Ordinal);
                foreach (var key in localKeys) serials.Remove(IdOf(key));
                foreach (var pair in outcome.Serials) serials[pair.Key] = pair.Value;
                var warning = _copy.Replace(revision, targetEntries, serials);
                return (Warning: warning, Choices: choices, Applied: apply.Count(item => KindOf(item.Key) == EntityKinds.Object),
                    KeptLayers: outcome.KeptLayers);
            }
            finally
            {
                foreach (var conflict in conflicts) conflict.Dispose();
                foreach (var item in decoded) item.Geometry.Dispose();
            }
        });

        var objectCount = targetEntries.Keys.Count(key => KindOf(key) == EntityKinds.Object);
        var message = PullSummary(bound.Branch, revision, objectCount, result.Choices, checkout, baseCommit == revision && result.Applied == 0);
        if (result.Applied > 0) message += $" {result.Applied} objects added, changed or removed.";
        if (localKeys.Count > 0) message += $" Kept {localKeys.Count} local changes; push them, or pull with Discard to drop them.";
        if (keptLocalTables > 0) message += $" {keptLocalTables} layers or settings changed on both sides; your version is kept.";
        if (result.KeptLayers.Count > 0) message += " Kept layers that still hold your objects: " + string.Join(", ", result.KeptLayers) + ".";
        if (!string.IsNullOrEmpty(skipped)) message += $" Not shown in Rhino (kept on the server): {skipped}.";
        return SyncResult.Success(WithWarning(message, result.Warning));
    }

    /// <summary>Switching server or repository drops the base: a base commit only exists in the repository it came from.</summary>
    private void Rebind(string server, string repo)
    {
        _copy.Require();
        _copy.Read();
        var same = string.Equals(_copy.Server, server, StringComparison.OrdinalIgnoreCase) && string.Equals(_copy.Repo, repo, StringComparison.Ordinal);
        if (!same && _copy.BaseCommit is not null) _copy.Detach();
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

    private async Task<IReadOnlyList<string>> ListReposCore(string server, CancellationToken cancellationToken)
    {
        var normalized = NormalizeServer(server);
        try
        {
            return await FetchReposAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (LocalServer.IsLoopback(normalized))
        {
            await LocalServer.EnsureRunningAsync(normalized, cancellationToken).ConfigureAwait(false);
            return await FetchReposAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> FetchReposAsync(string server, CancellationToken cancellationToken)
    {
        var listed = await ClientFor(server).ListReposAsync(cancellationToken).ConfigureAwait(false);
        return listed.Repos.Select(repo => repo.Name).ToList();
    }

    private async Task<IReadOnlyList<string>> ListBranchesCore(string server, string repo, CancellationToken cancellationToken)
    {
        var bound = NormalizeRepo(server, repo);
        try
        {
            return await FetchBranchesAsync(bound, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (LocalServer.IsLoopback(bound.Server))
        {
            await LocalServer.EnsureRunningAsync(bound.Server, cancellationToken).ConfigureAwait(false);
            return await FetchBranchesAsync(bound, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> FetchBranchesAsync(ServerRepo bound, CancellationToken cancellationToken)
    {
        var client = ClientFor(bound.Server);
        try
        {
            await client.GetRepoAsync(bound.Repo, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        var listed = await client.ListBranchesAsync(bound.Repo, cancellationToken).ConfigureAwait(false);
        return listed.Branches.Select(branch => branch.Name).ToList();
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

    /// <summary>Starts a branch at the base commit. A branch someone else just created is left for the commit's head check.</summary>
    private static async Task CreateBranchAsync(
        RhinoTrunkClient client, Bound bound, string commit, string? parent, CancellationToken cancellationToken)
    {
        try
        {
            await client.CreateBranchAsync(bound.Repo, new CreateBranchRequest
            {
                Name = bound.Branch,
                From = commit,
                Parent = parent == bound.Branch ? null : parent,
                Author = Author(),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex) when (ex.Error.Code == ErrorCodes.RefConflict)
        {
        }
    }

    private SyncResult NothingToCommit(CapturedPush captured, string? created)
    {
        var remembered = OnUi(() => _copy.RememberSerials(captured.Serials));
        return SyncResult.Success(WithWarning(WithSkips(created ?? "Nothing to commit.", captured.Skipped), remembered));
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
            return "Someone pushed to the branch at the same moment. Run the command again to update and retry.";
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

    private static string PullSummary(string branch, string commit, int objectCount, IReadOnlyDictionary<string, ConflictChoice> choices, bool checkout, bool upToDate)
    {
        var message = checkout
            ? $"Checked out {ShortCommit(commit)} ({objectCount} objects)."
            : upToDate
                ? $"{branch} is up to date ({ShortCommit(commit)})."
                : $"Pulled {branch} ({commit}, {objectCount} objects).";
        if (choices.Count == 0) return message;
        var existing = 0;
        var incoming = 0;
        var merged = 0;
        foreach (var choice in choices.Values)
        {
            switch (choice)
            {
                case ConflictChoice.KeepExisting: existing++; break;
                case ConflictChoice.KeepIncoming: incoming++; break;
                case ConflictChoice.Merge: merged++; break;
            }
        }

        return message + $" Reviewed {choices.Count} conflicts: kept {existing} existing, kept {incoming} incoming, merged {merged}.";
    }

    private static string ShortCommit(string commit)
    {
        const string prefix = "sha256:";
        var hex = commit.StartsWith(prefix, StringComparison.Ordinal) ? commit[prefix.Length..] : commit;
        return hex.Length <= 8 ? hex : hex[..8];
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
