using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Runtime;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using System.Drawing;
using System.Text;

namespace RhinoClient.Sync;

/// <summary>
/// A merge-picking session started from the history graph. The panel updates the selected commits while it runs;
/// the session reloads and redraws without leaving Rhino's prompt.
/// </summary>
public sealed class PickSession
{
    private string[] _commits;
    private volatile bool _dirty = true;
    private volatile bool _cancel;

    public PickSession(IEnumerable<string> commits) => _commits = commits.ToArray();

    internal string[] Commits => Volatile.Read(ref _commits);

    internal bool TakeDirty()
    {
        if (!_dirty) return false;
        _dirty = false;
        return true;
    }

    internal bool CancelRequested => _cancel;

    /// <summary>New selection from the graph. Called on Rhino's UI thread; wakes the waiting prompt.</summary>
    public void Update(IEnumerable<string> commits)
    {
        Volatile.Write(ref _commits, commits.ToArray());
        _dirty = true;
        RhinoApp.SendKeystrokes("_Refresh", true);
    }

    /// <summary>The graph selection was cleared: leave the session without merging.</summary>
    public void Cancel()
    {
        _cancel = true;
        RhinoApp.SendKeystrokes("_Cancel", true);
    }
}

/// <summary>Commit previews from the history graph, and picking a merge from several commits in the viewport.</summary>
public sealed partial class GeometrySync
{
    internal const string PreviewRecipe = "preview-mesh.v1";
    private static readonly Color[] PickColors =
    [
        Color.FromArgb(40, 120, 255), Color.FromArgb(0, 190, 90), Color.FromArgb(235, 130, 0),
        Color.FromArgb(165, 70, 210), Color.FromArgb(0, 170, 175), Color.FromArgb(200, 160, 0),
    ];

    // Display shapes by geometry hash, shared by every preview of this document. Geometry is immutable, so a
    // shape never goes stale; the server keeps the meshes as derived data so other clients skip the meshing.
    private readonly Dictionary<string, CapturedShape> _shapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiffResponse> _diffs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommitInfo> _commits = new(StringComparer.Ordinal);
    private readonly object _previewGate = new();

    /// <summary>
    /// Draws a commit in the viewport relative to the model: added green, modified blue with the model's version
    /// hidden, removed as red ghosts. Read-only and not serialized with pull or push, so the graph can be cycled
    /// quickly; repeated previews come from memory.
    /// </summary>
    public async Task<SyncResult> PreviewCommitAsync(string server, string repo, string commit, CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _previewGeneration);
        try
        {
            var loaded = await LoadCommitPreviewAsync(server, repo, commit, cancellationToken).ConfigureAwait(false);
            if (loaded is null) return SyncResult.Fail("This model is not on a commit of that repository; pull it first to preview commits.");
            var (info, diff, basis) = loaded.Value;
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _previewGeneration) || _operation.CurrentCount == 0)
                return SyncResult.Fail("The model changed while the preview was loading; click the commit again.");
            var counts = OnUi(() =>
            {
                var doc = _copy.Require();
                var scene = new PreviewScene();
                var (added, modified, removed) = AddCommitChanges(scene, doc, diff.Changes);
                scene.Hud = new[]
                {
                    $"{ShortCommit(info.Id)}  {FirstLine(info.Message)}",
                    $"{info.Author.Name ?? info.Author.Id} ({info.Author.Kind}), {info.Timestamp.LocalDateTime:g}",
                    info.Id == basis ? "This is the commit the model is on." : $"Against your model: {Legend(added, modified, removed)}",
                    "Click another dot to compare; shift-click several to pick a merge.",
                };
                Show(doc, scene);
                return (added, modified, removed);
            });
            return SyncResult.Success($"Previewing {ShortCommit(info.Id)}: +{counts.added} ~{counts.modified} -{counts.removed}.");
        }
        catch (OperationCanceledException)
        {
            return SyncResult.Fail("Cancelled.");
        }
        catch (Exception ex) when (ex is SyncException or RhinoTrunkApiException or HttpRequestException)
        {
            return Describe(ex);
        }
    }

    /// <summary>Loads a commit's preview data and meshes without drawing, so a later click is immediate.</summary>
    public async Task PrefetchCommitAsync(string server, string repo, string commit, CancellationToken cancellationToken = default)
    {
        try
        {
            await LoadCommitPreviewAsync(server, repo, commit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the click loads it again and reports any problem.
        }
    }

    private async Task<(CommitInfo Info, DiffResponse Diff, string? Basis)?> LoadCommitPreviewAsync(
        string server, string repo, string commit, CancellationToken cancellationToken)
    {
        var bound = NormalizeRepo(server, repo);
        var client = ClientFor(bound.Server);
        var basis = OnUi(() =>
        {
            _copy.Require();
            _copy.Read();
            var same = string.Equals(_copy.Server, bound.Server, StringComparison.OrdinalIgnoreCase) && _copy.Repo == bound.Repo;
            return same ? _copy.BaseCommit : null;
        });
        CommitInfo? info;
        lock (_previewGate) _commits.TryGetValue(commit, out info);
        if (info is null)
        {
            info = await client.GetCommitAsync(bound.Repo, commit, cancellationToken).ConfigureAwait(false);
            lock (_previewGate) _commits[commit] = info;
        }

        var key = (basis ?? "") + ">" + info.Id;
        DiffResponse? diff;
        lock (_previewGate) _diffs.TryGetValue(key, out diff);
        if (diff is null)
        {
            diff = await client.DiffAsync(bound.Repo, basis, info.Id, includeManifests: true, cancellationToken).ConfigureAwait(false);
            lock (_previewGate) _diffs[key] = diff;
        }

        var hashes = diff.Changes.Where(c => c.Kind == EntityKinds.Object)
            .Select(c => c.ToManifest?.Components.GetValueOrDefault(DocumentMapper.GeometryComponent)).OfType<string>();
        await EnsureShapesAsync(client, bound.Repo, hashes, cancellationToken).ConfigureAwait(false);
        return (info, diff, basis);
    }

    /// <summary>
    /// Fills the shape cache: from memory, else from the server's stored preview mesh, else by decoding and meshing
    /// the geometry once and storing that mesh on the server for everyone.
    /// </summary>
    private async Task EnsureShapesAsync(RhinoTrunkClient client, string repo, IEnumerable<string> hashes, CancellationToken cancellationToken)
    {
        List<string> missing;
        lock (_previewGate) missing = hashes.Distinct(StringComparer.Ordinal).Where(h => !_shapes.ContainsKey(h)).ToList();
        if (missing.Count == 0) return;

        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var throttle = new SemaphoreSlim(8))
        {
            await Task.WhenAll(missing.Select(async hash =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var record = await client.GetDerivedAsync(repo, hash, PreviewRecipe, cancellationToken).ConfigureAwait(false);
                    if (record is not null) lock (stored) stored[hash] = record.Result;
                }
                catch (Exception ex) when (ex is RhinoTrunkApiException or HttpRequestException)
                {
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false);
        }

        var meshBlobs = await DownloadAsync(client, repo, stored.Values.Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var needGeometry = new List<string>();
        foreach (var hash in missing)
        {
            if (stored.TryGetValue(hash, out var meshHash) && meshBlobs.TryGetValue(meshHash, out var bytes)
                && Decode(bytes) is { } meshGeometry)
            {
                using (meshGeometry) Remember(hash, CapturedShape.From(meshGeometry));
            }
            else
            {
                needGeometry.Add(hash);
            }
        }

        if (needGeometry.Count == 0) return;
        var geometryBlobs = await DownloadAsync(client, repo, needGeometry, cancellationToken).ConfigureAwait(false);
        var uploads = new List<(string Source, byte[] Mesh)>();
        foreach (var hash in needGeometry)
        {
            if (!geometryBlobs.TryGetValue(hash, out var bytes) || Decode(bytes) is not { } geometry) continue;
            using (geometry)
            {
                var shape = CapturedShape.From(geometry);
                Remember(hash, shape);
                if (shape.Mesh is { } mesh && geometry is not Mesh)
                {
                    var json = mesh.ToJSON(new Rhino.FileIO.SerializationOptions { RhinoVersion = 8, WriteUserData = false });
                    if (!string.IsNullOrEmpty(json)) uploads.Add((hash, Encoding.UTF8.GetBytes(json)));
                }
            }
        }

        if (uploads.Count > 0) _ = StorePreviewMeshesAsync(client, repo, uploads);
    }

    private static async Task StorePreviewMeshesAsync(RhinoTrunkClient client, string repo, List<(string Source, byte[] Mesh)> uploads)
    {
        foreach (var (source, mesh) in uploads)
        {
            try
            {
                var result = await client.PutObjectAsync(repo, mesh).ConfigureAwait(false);
                await client.SetDerivedAsync(repo, source, PreviewRecipe, result).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A missing stored mesh only costs the next client one meshing.
            }
        }
    }

    /// <summary>Takes any commit, merge or compare preview off the screen and stops a preview still loading.</summary>
    private void DiscardPreview()
    {
        Interlocked.Increment(ref _previewGeneration);
        try { OnUi(ClearPreview); }
        catch (Exception)
        {
        }
    }

    private int _previewGeneration;

    /// <summary>
    /// Caches a shape. Cached shapes may be on screen in a scene at any time, so they are never disposed here:
    /// a duplicate (a prefetch and a click loading the same commit at once) loses and is disposed instead, and an
    /// overflowing cache is only forgotten, leaving its shapes to the garbage collector once no scene draws them.
    /// </summary>
    private void Remember(string hash, CapturedShape shape)
    {
        lock (_previewGate)
        {
            if (_shapes.ContainsKey(hash))
            {
                shape.Dispose();
                return;
            }

            if (_shapes.Count > 20000) _shapes.Clear();
            _shapes[hash] = shape;
        }
    }

    private CapturedShape? CachedShape(Manifest? manifest)
    {
        if (manifest?.Components.TryGetValue(DocumentMapper.GeometryComponent, out var hash) != true) return null;
        lock (_previewGate) return _shapes.GetValueOrDefault(hash!);
    }

    private static GeometryBase? Decode(byte[] bytes)
    {
        try
        {
            return CommonObject.FromJSON(Encoding.UTF8.GetString(bytes)) as GeometryBase;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Adds a commit's object changes to a scene, using cached shapes (not owned by the scene).</summary>
    private (int Added, int Modified, int Removed) AddCommitChanges(PreviewScene scene, RhinoDoc doc, IEnumerable<DiffEntry> changes)
    {
        int added = 0, modified = 0, removed = 0;
        foreach (var change in changes.Where(c => c.Kind == EntityKinds.Object))
        {
            var id = Guid.Parse(change.Id);
            scene.Hidden.Add(id);
            if (change.Change == DiffChanges.Deleted)
            {
                removed++;
                if (doc.Objects.FindId(id)?.Geometry is { } current)
                {
                    var ghost = CapturedShape.From(current);
                    scene.Owned.Add(ghost);
                    AddCached(scene, ghost, NeutralColor, RemovedColor, GhostTransparency);
                }

                continue;
            }

            if (change.Change == DiffChanges.Added) added++;
            else modified++;
            if (CachedShape(change.ToManifest) is { } shape)
            {
                var color = change.Change == DiffChanges.Added ? AddedColor : ModifiedColor;
                AddCached(scene, shape, Tint(color), color, 0);
            }
        }

        foreach (var item in scene.Items)
            scene.Bounds = Union(scene.Bounds, item.Mesh?.GetBoundingBox(false) ?? item.Curve?.GetBoundingBox(false) ?? BoundingBox.Empty);
        return (added, modified, removed);
    }

    private static PreviewDrawable? AddCached(PreviewScene scene, CapturedShape shape, Color fill, Color outline, double transparency, int width = OutlinePixels)
    {
        if (!shape.HasDrawable) return null;
        var drawable = new PreviewDrawable
        {
            Mesh = shape.Mesh,
            Curve = shape.Curve,
            Point = shape.Point,
            Cloud = shape.Cloud,
            Fill = fill,
            Outline = outline,
            Transparency = transparency,
            OutlineWidth = width,
            Silhouette = true,
        };
        scene.Items.Add(drawable);
        return drawable;
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 70 ? line[..70] + "…" : line;
    }

    // =========================================================================================
    // Merge picking
    // =========================================================================================

    private sealed record PickCandidate(int Source, string Change, EntityVersion? Entity);

    private sealed class PickState
    {
        internal List<CommitInfo> Sources { get; } = new();
        /// <summary>Object id to the versions the selected commits offer.</summary>
        internal Dictionary<string, List<PickCandidate>> Objects { get; } = new(StringComparer.Ordinal);
        /// <summary>Layers, materials, linetypes, groups and settings: taken from the first commit that changes them.</summary>
        internal Dictionary<string, PickCandidate> Tables { get; } = new(StringComparer.Ordinal);
        /// <summary>Object id to the chosen candidate index; missing means keep the model's version.</summary>
        internal Dictionary<string, int> Choices { get; } = new(StringComparer.Ordinal);
        internal List<(PreviewDrawable Drawable, string Id, int Candidate)> Pickable { get; } = new();
    }

    /// <summary>
    /// Merge picking: every change the selected commits would bring into the model's branch is drawn in its commit's
    /// colour. Click a ghost to take that version, click a taken object to drop it. Enter merges the picks into the
    /// branch as one commit; Esc leaves without changing anything.
    /// </summary>
    public Task<SyncResult> PickMergeAsync(SyncTarget target, PickSession session, CancellationToken cancellationToken = default) =>
        Run(() => PickMergeCore(target, session, cancellationToken), cancellationToken);

    private async Task<SyncResult> PickMergeCore(SyncTarget target, PickSession session, CancellationToken cancellationToken)
    {
        var bound = Normalize(target);
        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        OnUi(() =>
        {
            Rebind(bound.Server, bound.Repo);
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
        });
        await EnsureBaseMapAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        if (_copy.BaseCommit is null) return SyncResult.Fail("This model is not on a commit yet. Pull or push it first.");
        var local = await CaptureLocalAsync(client, bound.Repo, () => _copy.Require(), _copy, cancellationToken).ConfigureAwait(false);
        if (local.Changes.Count > 0)
            return SyncResult.Fail($"This model has {local.Changes.Count} unpushed changes. Push them first, so picking starts from a clean model.");

        PickState? state = null;
        var message = "";
        try
        {
            while (true)
            {
                if (session.CancelRequested) return SyncResult.Fail("Merge picking cancelled. Nothing changed.");
                if (session.TakeDirty() || state is null)
                {
                    var previous = state;
                    state = await LoadPickStateAsync(client, bound.Repo, _copy.BaseCommit, session.Commits, cancellationToken).ConfigureAwait(false);
                    if (previous is not null)
                    {
                        // Keep earlier picks that still exist in the new selection.
                        foreach (var (id, index) in previous.Choices)
                        {
                            var source = previous.Objects[id][index];
                            var sourceCommit = previous.Sources[source.Source].Id;
                            if (state.Objects.TryGetValue(id, out var offered)
                                && offered.FindIndex(c => state.Sources[c.Source].Id == sourceCommit) is var kept and >= 0)
                                state.Choices[id] = kept;
                        }
                    }
                }

                var current = state;
                var step = OnUi(() => PickStep(current, bound.Branch));
                switch (step)
                {
                    case PickResult.Cancel:
                        return SyncResult.Fail("Merge picking cancelled. Nothing changed.");
                    case PickResult.Accept:
                        if (current.Choices.Count == 0) return SyncResult.Fail("Nothing was picked, so nothing was merged.");
                        message = PickMessage(current);
                        goto accept;
                }
            }
        }
        finally
        {
            OnUi(() => ClearPreview());
        }

        accept:
        var picked = state!;
        var apply = new List<(string Key, EntityVersion? Entity)>();
        foreach (var (id, index) in picked.Choices)
        {
            var candidate = picked.Objects[id][index];
            apply.Add((WorkingCopy.Key(EntityKinds.Object, id), candidate.Change == DiffChanges.Deleted ? null : candidate.Entity));
        }

        foreach (var (key, candidate) in picked.Tables)
            apply.Add((key, candidate.Change == DiffChanges.Deleted ? null : candidate.Entity));

        var puts = apply.Where(a => a.Entity is not null).Select(a => a.Entity!).ToList();
        var blobs = await DownloadAsync(client, bound.Repo, ComponentHashes(puts), cancellationToken).ConfigureAwait(false);
        OnUi(() =>
        {
            var doc = _copy.Require();
            var decoded = DocumentMapper.DecodeObjects(puts.Where(e => e.Kind == EntityKinds.Object).ToList(), blobs);
            try
            {
                var byId = decoded.ToDictionary(d => d.Entity.Id, StringComparer.Ordinal);
                var changes = new IncomingChanges();
                foreach (var (key, entity) in apply)
                {
                    var kind = KindOf(key);
                    var id = IdOf(key);
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
                    else if (byId.TryGetValue(id, out var item)) changes.ObjectPuts.Add((item, ConflictChoice.KeepIncoming));
                }

                DocumentMapper.ApplyChanges(doc, changes);
            }
            finally
            {
                foreach (var item in decoded) item.Geometry.Dispose();
            }
        });

        var pushed = await PushCore(target, message, interactive: true, cancellationToken).ConfigureAwait(false);
        return pushed.Ok
            ? SyncResult.Success($"Merged {picked.Choices.Count} picked objects into {bound.Branch}. {pushed.Message}")
            : SyncResult.Fail($"The picks are in the model but were not pushed: {pushed.Message} Push again, or pull with Discard to drop them.");
    }

    private async Task<PickState> LoadPickStateAsync(RhinoTrunkClient client, string repo, string basis, string[] commits, CancellationToken cancellationToken)
    {
        var state = new PickState();
        foreach (var commit in commits)
        {
            var info = await client.GetCommitAsync(repo, commit, cancellationToken).ConfigureAwait(false);
            var source = state.Sources.Count;
            state.Sources.Add(info);
            var preview = await client.PreviewMergeAsync(repo, new MergePreviewRequest
            {
                Ours = basis,
                Theirs = info.Id,
                CompareTo = basis,
                Manifests = true,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var change in preview.Changes)
            {
                var entity = change.To is null ? null : new EntityVersion { Doc = change.Doc, Kind = change.Kind, Id = change.Id, Version = change.To, Manifest = change.ToManifest };
                Offer(state, source, change.Kind, change.Id, change.Change, entity);
            }

            // Conflicting entities stay at our version in the merged tree; offer their version as a pick too.
            var conflicted = preview.Conflicts.Where(c => c.Theirs is not null).ToList();
            var manifests = await ManifestsAsync(client, repo, conflicted.Select(c => c.Theirs!), cancellationToken).ConfigureAwait(false);
            foreach (var conflict in preview.Conflicts)
            {
                if (conflict.Theirs is null)
                {
                    Offer(state, source, conflict.Kind, conflict.Id, DiffChanges.Deleted, null);
                    continue;
                }

                if (!manifests.TryGetValue(conflict.Theirs, out var manifest)) continue;
                var entity = new EntityVersion { Doc = conflict.Doc, Kind = conflict.Kind, Id = conflict.Id, Version = conflict.Theirs, Manifest = manifest };
                Offer(state, source, conflict.Kind, conflict.Id, conflict.Ours is null ? DiffChanges.Added : DiffChanges.Modified, entity);
            }
        }

        // An object offered by exactly one commit is taken by default; differing offers wait for a click.
        foreach (var (id, offers) in state.Objects)
            if (offers.Count == 1) state.Choices[id] = 0;

        var hashes = state.Objects.Values.SelectMany(o => o).Select(c => c.Entity?.Manifest?.Components.GetValueOrDefault(DocumentMapper.GeometryComponent)).OfType<string>();
        await EnsureShapesAsync(client, repo, hashes, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private static void Offer(PickState state, int source, string kind, string id, string change, EntityVersion? entity)
    {
        if (!DocumentMapper.IsSupported(kind, entity?.Manifest?.Subtype)) return;
        if (kind == EntityKinds.Object)
        {
            if (!state.Objects.TryGetValue(id, out var offers)) state.Objects[id] = offers = new List<PickCandidate>();
            if (offers.All(o => o.Entity?.Version != entity?.Version)) offers.Add(new PickCandidate(source, change, entity));
            return;
        }

        state.Tables.TryAdd(WorkingCopy.Key(kind, id), new PickCandidate(source, change, entity));
    }

    private enum PickResult { Continue, Accept, Cancel }

    /// <summary>Draws the picking scene and waits for one click or option. Runs on Rhino's UI thread.</summary>
    private PickResult PickStep(PickState state, string branch)
    {
        var doc = _copy.Require();
        var scene = new PreviewScene();
        state.Pickable.Clear();
        foreach (var (id, offers) in state.Objects)
        {
            var guid = Guid.Parse(id);
            var chosen = state.Choices.TryGetValue(id, out var index) ? index : -1;
            if (chosen >= 0) scene.Hidden.Add(guid);
            for (var i = 0; i < offers.Count; i++)
            {
                var offer = offers[i];
                var color = PickColors[offer.Source % PickColors.Length];
                var taken = i == chosen;
                CapturedShape? shape;
                if (offer.Change == DiffChanges.Deleted)
                {
                    shape = doc.Objects.FindId(guid)?.Geometry is { } current ? CapturedShape.From(current) : null;
                    if (shape is not null) scene.Owned.Add(shape);
                    color = RemovedColor;
                }
                else
                {
                    shape = CachedShape(offer.Entity?.Manifest);
                }

                if (shape is null) continue;
                var deleted = offer.Change == DiffChanges.Deleted;
                var drawable = taken
                    ? AddCached(scene, shape, deleted ? NeutralColor : Tint(color), color, deleted ? GhostTransparency : 0, OutlinePixels)
                    : AddCached(scene, shape, color, color, 0.85, 1);
                if (drawable is not null) state.Pickable.Add((drawable, id, i));
            }
        }

        foreach (var item in scene.Items)
            scene.Bounds = Union(scene.Bounds, item.Mesh?.GetBoundingBox(false) ?? item.Curve?.GetBoundingBox(false) ?? BoundingBox.Empty);
        var hud = new List<string> { $"Pick a merge into {branch}: click a faint object to take it, click a taken one to drop it." };
        for (var s = 0; s < state.Sources.Count; s++)
        {
            var taken = state.Choices.Count(c => state.Objects[c.Key][c.Value].Source == s);
            var offered = state.Objects.Count(o => o.Value.Any(c => c.Source == s));
            hud.Add($"{PreviewConduit.Swatch(PickColors[s % PickColors.Length])}{ShortCommit(state.Sources[s].Id)} {FirstLine(state.Sources[s].Message)}: {taken} of {offered} taken");
        }

        var undecided = state.Objects.Count(o => o.Value.Count > 1 && !state.Choices.ContainsKey(o.Key));
        if (undecided > 0) hud.Add($"{undecided} objects differ between the selected commits; click the version you want.");
        if (state.Tables.Count > 0) hud.Add($"{state.Tables.Count} layer, material and settings changes come along with the picks.");
        hud.Add("Enter merges the picks as one commit. Esc leaves without changes.");
        scene.Hud = hud;
        Show(doc, scene);

        var get = new GetPoint();
        get.SetCommandPrompt($"Pick objects to merge into {branch} ({state.Choices.Count} taken)");
        get.AcceptNothing(true);
        var all = get.AddOption("TakeAll");
        var none = get.AddOption("TakeNone");
        var refresh = get.AddOption("Refresh");
        var cancel = get.AddOption("Cancel");
        var result = get.Get();
        switch (result)
        {
            case GetResult.Nothing:
                return PickResult.Accept;
            case GetResult.Option:
                var picked = get.Option()?.Index;
                if (picked == cancel) return PickResult.Cancel;
                if (picked == all)
                    foreach (var (id, offers) in state.Objects)
                        if (!state.Choices.ContainsKey(id)) state.Choices[id] = 0;
                if (picked == none) state.Choices.Clear();
                _ = refresh;
                return PickResult.Continue;
            case GetResult.Point:
                if (Pick(state, get.View(), get.Point2d()) is { } hit)
                {
                    if (state.Choices.TryGetValue(hit.Id, out var current) && current == hit.Candidate) state.Choices.Remove(hit.Id);
                    else state.Choices[hit.Id] = hit.Candidate;
                }

                return PickResult.Continue;
            default:
                return PickResult.Cancel;
        }
    }

    /// <summary>The nearest drawn candidate under a screen point.</summary>
    private static (string Id, int Candidate)? Pick(PickState state, RhinoView? view, System.Drawing.Point screen)
    {
        if (view is null) return null;
        using var pick = new PickContext { View = view, PickStyle = PickStyle.PointPick };
        pick.SetPickTransform(view.ActiveViewport.GetPickTransform(screen));
        pick.UpdateClippingPlanes();
        (string Id, int Candidate)? best = null;
        var bestDepth = double.NegativeInfinity;
        var bestDistance = double.PositiveInfinity;
        foreach (var (drawable, id, candidate) in state.Pickable)
        {
            double depth, distance;
            bool hit;
            if (drawable.Mesh is { } mesh)
                hit = pick.PickFrustumTest(mesh, PickContext.MeshPickStyle.ShadedModePicking, out _, out depth, out distance, out _, out _);
            else if (drawable.Curve is { } curve)
            {
                using var nurbs = curve.ToNurbsCurve();
                hit = pick.PickFrustumTest(nurbs, out _, out depth, out distance);
            }
            else if (drawable.Point is { } point)
                hit = pick.PickFrustumTest(point, out depth, out distance);
            else continue;
            if (!hit) continue;
            if (distance < bestDistance - 1e-9 || (Math.Abs(distance - bestDistance) < 1e-9 && depth > bestDepth))
            {
                best = (id, candidate);
                bestDepth = depth;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static string PickMessage(PickState state)
    {
        var parts = state.Sources.Select((info, s) =>
        {
            var taken = state.Choices.Count(c => state.Objects[c.Key][c.Value].Source == s);
            return taken == 0 ? null : $"{taken} from {ShortCommit(info.Id)} ({FirstLine(info.Message)})";
        }).OfType<string>();
        return "Merge picks: " + string.Join(", ", parts);
    }

    private static string ColorName(Color color) =>
        color.B > 200 && color.R < 100 ? "blue" : color.G > 150 && color.R < 100 && color.B < 120 ? "green"
        : color.R > 200 && color.G > 100 && color.B < 50 ? (color.G > 150 ? "yellow" : "orange")
        : color.R > 150 && color.B > 150 ? "purple" : color.G > 150 && color.B > 150 ? "teal" : "grey";
}
