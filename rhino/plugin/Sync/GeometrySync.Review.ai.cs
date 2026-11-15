using Rhino;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Runtime;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using System.Drawing;
using System.Text;
using System.Text.Json;

namespace RhinoClient.Sync;

/// <summary>How COMPARE lays out the options it shows.</summary>
public enum CompareMode
{
    /// <summary>One option at a time.</summary>
    Cycle,
    /// <summary>Every option at once, ghosted over the model.</summary>
    Overlay,
    /// <summary>The model and a copy per option, side by side in the viewport.</summary>
    Side,
}

/// <summary>BRANCH, MERGE and COMPARE: operations that read or move branches and show the result in the viewport.</summary>
public sealed partial class GeometrySync
{
    internal static readonly Color AddedColor = Color.FromArgb(0, 220, 70);
    internal static readonly Color RemovedColor = Color.FromArgb(235, 40, 40);
    internal static readonly Color ModifiedColor = Color.FromArgb(40, 120, 255);
    internal static readonly Color NeutralColor = Color.FromArgb(150, 150, 150);
    private const double GhostTransparency = 0.7;

    /// <summary>Creates a branch at the commit the model is on, records its parent branch, and switches the model to it.</summary>
    public Task<SyncResult> CreateBranchAsync(SyncTarget current, string name, CancellationToken cancellationToken = default) =>
        Run(() => BranchCore(current, name, cancellationToken), cancellationToken);

    /// <summary>
    /// Merges the model's branch into <paramref name="into"/> (default: the branch it was created from). Shows the
    /// merge in the viewport, reviews conflicts, asks for confirmation, merges, and switches the model to the target.
    /// </summary>
    public Task<SyncResult> MergeBranchAsync(SyncTarget current, string? into, bool interactive, bool approveRules, CancellationToken cancellationToken = default) =>
        Run(() => MergeBranchCore(current, into, interactive, approveRules, cancellationToken), cancellationToken);

    /// <summary>
    /// Shows option branches as they would land on <paramref name="at"/> (a branch, tag or commit; default main):
    /// added objects green, removed red ghosts, modified blue with the current version hidden.
    /// Interactive: step through options and modes until Done. Scripted: leaves the preview on screen.
    /// </summary>
    public Task<SyncResult> CompareAsync(SyncTarget current, IReadOnlyList<string>? options, string at, CompareMode mode, bool interactive, CancellationToken cancellationToken = default) =>
        Run(() => CompareCore(current, options, at, mode, interactive, cancellationToken), cancellationToken);

    /// <summary>Removes any merge or compare preview from the viewport.</summary>
    public static void ClearPreview()
    {
        PreviewConduit.Current?.Hide();
        RhinoDoc.ActiveDoc?.Views.Redraw();
    }

    // =========================================================================================
    // BRANCH
    // =========================================================================================

    private async Task<SyncResult> BranchCore(SyncTarget current, string name, CancellationToken cancellationToken)
    {
        var bound = Normalize(current);
        var branch = name.Trim();
        if (!Names.IsValidRefName(branch)) return SyncResult.Fail($"Branch name '{branch}' is not valid.");
        OnUi(() =>
        {
            Rebind(bound.Server, bound.Repo);
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
        });
        var basis = _copy.BaseCommit;
        if (basis is null) return SyncResult.Fail("This model is not on a commit yet. Push or pull it first; a branch starts at the commit the model is on.");
        if (branch == bound.Branch) return SyncResult.Fail($"The model is already on '{branch}'.");

        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        var existing = await client.GetBranchAsync(bound.Repo, branch, cancellationToken).ConfigureAwait(false);
        string note;
        if (existing is null)
        {
            await client.CreateBranchAsync(bound.Repo, new CreateBranchRequest
            {
                Name = branch,
                From = basis,
                Parent = bound.Branch,
                Author = Author(),
            }, cancellationToken).ConfigureAwait(false);
            note = $"Created branch {branch} from {bound.Branch} at {ShortCommit(basis)}.";
        }
        else if (existing.Commit == basis)
        {
            note = $"Branch {branch} already exists at this commit.";
        }
        else
        {
            return SyncResult.Fail($"Branch '{branch}' already exists at another commit. Pull it to work on it.");
        }

        OnUi(() => _copy.UseTarget(bound.Server, bound.Repo, branch));
        return SyncResult.Success(note + $" The model is now on {branch}; your next push goes there.");
    }

    // =========================================================================================
    // MERGE
    // =========================================================================================

    private async Task<SyncResult> MergeBranchCore(SyncTarget current, string? intoName, bool interactive, bool approveRules, CancellationToken cancellationToken)
    {
        var bound = Normalize(current);
        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        OnUi(() =>
        {
            Rebind(bound.Server, bound.Repo);
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
        });
        await EnsureBaseMapAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        if (_copy.BaseCommit is null) return SyncResult.Fail("This model is not on a commit yet. Push it first.");
        var local = await CaptureLocalAsync(client, bound.Repo, () => _copy.Require(), _copy, cancellationToken).ConfigureAwait(false);
        if (local.Changes.Count > 0)
            return SyncResult.Fail($"This model has {local.Changes.Count} unpushed changes. Push them first, so the merge contains them.");

        var head = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false)
            ?? throw new SyncException($"Branch '{bound.Branch}' does not exist. Push it first.");
        if (head.Commit != _copy.BaseCommit)
        {
            var updated = await PullCommitAsync(current, null, interactive, false, cancellationToken).ConfigureAwait(false);
            if (!updated.Ok) return updated;
        }

        var info = await client.GetBranchInfoAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false);
        var into = string.IsNullOrWhiteSpace(intoName) ? info.Parent : intoName.Trim();
        if (string.IsNullOrEmpty(into))
            return SyncResult.Fail($"'{bound.Branch}' has no recorded parent branch. Choose the branch to merge into with the Into option.");
        if (into == bound.Branch) return SyncResult.Fail("A branch cannot be merged into itself.");
        if (await client.GetBranchAsync(bound.Repo, into, cancellationToken).ConfigureAwait(false) is null)
            return SyncResult.Fail($"Branch '{into}' does not exist.");

        var resolutions = new List<MergeResolution>();
        MergePreviewResponse preview;
        while (true)
        {
            preview = await client.PreviewMergeAsync(bound.Repo, new MergePreviewRequest
            {
                Branch = into,
                Theirs = head.Commit,
                Author = Author(),
                Manifests = true,
                NoFastForward = true,
                Resolutions = resolutions.Count == 0 ? null : resolutions,
            }, cancellationToken).ConfigureAwait(false);
            if (preview.Conflicts.Count == 0) break;
            if (!interactive)
                return SyncResult.Fail($"Merging {bound.Branch} into {into} has conflicts: {MergeProblems(preview)} Run RhinoClientMerge interactively to resolve them.");
            var picked = await ResolveConflictsAsync(client, bound.Repo, into, bound.Branch, preview.Conflicts, cancellationToken).ConfigureAwait(false);
            if (picked is null) return SyncResult.Fail("Cancelled.");
            resolutions.AddRange(picked);
        }

        if (preview.Status == MergePreviewStatuses.UpToDate)
            return SyncResult.Success($"{into} already contains everything on {bound.Branch}.");
        if (!preview.CanMerge) return SyncResult.Fail($"Merging {bound.Branch} into {into} would break the model: {MergeProblems(preview)}");
        if (preview.AutoResolved.Count > 0 && !interactive && !approveRules)
            return SyncResult.Fail($"Merge rules resolved {preview.AutoResolved.Count} conflicts ({string.Join(", ", preview.AutoResolved.Select(a => a.Rule).Distinct())}). Review them and pass ApproveRules=Yes.");

        // The same preview twice: relative to the target branch (what the merge changes there, coloured) and
        // relative to the model (what the screen must change to show the merged result).
        var screen = await client.PreviewMergeAsync(bound.Repo, new MergePreviewRequest
        {
            Branch = into,
            Theirs = head.Commit,
            Author = Author(),
            Manifests = true,
            NoFastForward = true,
            Resolutions = resolutions.Count == 0 ? null : resolutions,
            CompareTo = _copy.BaseCommit,
        }, cancellationToken).ConfigureAwait(false);
        var geometry = await DownloadGeometryAsync(client, bound.Repo, preview.Changes.Concat(screen.Changes), cancellationToken).ConfigureAwait(false);
        try
        {
            var approval = preview.ApprovalReasons.Contains(ApprovalReasons.ProtectedBranch)
                ? $"'{into}' is protected: confirming is your approval as {Author().Id}."
                : "No approval needed beyond your confirmation.";
            var hud = new List<string>
            {
                $"Merge {bound.Branch} into {into}",
                $"{preview.Incoming.Added} added (green), {preview.Incoming.Modified} modified (blue), {preview.Incoming.Deleted} removed (red)",
                approval,
            };
            if (preview.AutoResolved.Count > 0)
                hud.Add($"Merge rules resolved {preview.AutoResolved.Count} conflicts: {string.Join(", ", preview.AutoResolved.Select(a => a.Rule).Distinct())}. Merging approves them.");
            if (resolutions.Count > 0) hud.Add($"You resolved {resolutions.Count} conflicts.");

            var confirmed = OnUi(() =>
            {
                var doc = _copy.Require();
                var scene = new PreviewScene { Hud = hud };
                AddResult(scene, doc, preview.Changes, screen.Changes, geometry, Transform.Identity, hide: true);
                Show(doc, scene);
                if (!interactive) return true;
                return Choose($"Merge {bound.Branch} into {into}?", "Merge", "Cancel") == 0;
            });
            if (!confirmed) return SyncResult.Fail("Merge cancelled. Nothing changed.");
        }
        finally
        {
            foreach (var item in geometry.Values) item.Dispose();
            OnUi(() => ClearPreview());
        }

        MergeResponse merged;
        try
        {
            merged = await client.MergeAsync(bound.Repo, new MergeRequest
            {
                Branch = into,
                ExpectedHead = preview.Ours,
                Theirs = preview.Theirs,
                Message = $"Merge {bound.Branch} into {into}",
                Author = Author(),
                // A merge commit even when the target has not moved, so history shows the branch joining back in.
                NoFastForward = true,
                Resolutions = resolutions.Count == 0 ? null : resolutions,
                ApproveRuleResolutions = preview.AutoResolved.Count > 0,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RhinoTrunkApiException ex) when (ex.Error.Code == ErrorCodes.RefConflict)
        {
            return SyncResult.Fail($"'{into}' changed while you were reviewing. Run RhinoClientMerge again to see the new result.");
        }

        var pulled = await PullCommitAsync(new SyncTarget(bound.Server, bound.Repo, into), null, interactive: false, discardLocal: false, cancellationToken).ConfigureAwait(false);
        var done = $"Merged {bound.Branch} into {into} ({ShortCommit(merged.Commit)}, {merged.Status}).";
        return pulled.Ok
            ? SyncResult.Success(done + $" The model is now on {into}.")
            : SyncResult.Fail(done + " Switching the model to it failed: " + pulled.Message);
    }

    /// <summary>Reviews merge conflicts: objects in the viewport (amber = target branch, blue = merged branch), other entities by prompt.</summary>
    private async Task<List<MergeResolution>?> ResolveConflictsAsync(
        RhinoTrunkClient client, string repo, string into, string theirs, IReadOnlyList<MergeConflict> conflicts, CancellationToken cancellationToken)
    {
        var manifests = await ManifestsAsync(client, repo,
            conflicts.Where(c => c.Kind == EntityKinds.Object).SelectMany(c => new[] { c.Ours, c.Theirs }).OfType<string>(), cancellationToken).ConfigureAwait(false);
        var hashes = manifests.Values.Select(m => m.Components.GetValueOrDefault(DocumentMapper.GeometryComponent)).OfType<string>().Distinct().ToList();
        var blobs = await DownloadAsync(client, repo, hashes, cancellationToken).ConfigureAwait(false);
        return OnUi(() =>
        {
            var doc = _copy.Require();
            var resolutions = new List<MergeResolution>();
            var shapes = new List<ObjectConflict>();
            try
            {
                foreach (var conflict in conflicts.Where(c => c.Kind == EntityKinds.Object))
                {
                    var ours = conflict.Ours is { } o && manifests.TryGetValue(o, out var om) ? DecodeGeometry(om, blobs) : null;
                    var theirsGeometry = conflict.Theirs is { } t && manifests.TryGetValue(t, out var tm) ? DecodeGeometry(tm, blobs) : null;
                    shapes.Add(ObjectConflict.Create(conflict.Id, Guid.Parse(conflict.Id), ours is null, ours, theirsGeometry));
                    ours?.Dispose();
                    theirsGeometry?.Dispose();
                }

                if (shapes.Count > 0)
                {
                    var choices = PullConflicts.Review(doc, shapes, allowMerge: false, existingLabel: $"the version on {into}", incomingLabel: $"the version on {theirs}");
                    foreach (var conflict in conflicts.Where(c => c.Kind == EntityKinds.Object))
                    {
                        var take = choices.TryGetValue(conflict.Id, out var choice) && choice == ConflictChoice.KeepIncoming ? MergeTakes.Theirs : MergeTakes.Ours;
                        resolutions.Add(new MergeResolution { Doc = conflict.Doc, Kind = conflict.Kind, Id = conflict.Id, Take = take });
                    }
                }

                foreach (var conflict in conflicts.Where(c => c.Kind != EntityKinds.Object))
                {
                    var fields = conflict.Fields is { Count: > 0 } f ? " (" + string.Join(", ", f) + ")" : "";
                    var pick = Choose($"{conflict.Kind} {conflict.Id[..Math.Min(8, conflict.Id.Length)]} changed on both sides{fields}. Keep which?",
                        "Take" + Option(into), "Take" + Option(theirs), "Cancel");
                    if (pick is < 0 or 2) return null;
                    resolutions.Add(new MergeResolution { Doc = conflict.Doc, Kind = conflict.Kind, Id = conflict.Id, Take = pick == 0 ? MergeTakes.Ours : MergeTakes.Theirs });
                }

                return resolutions;
            }
            catch (PullReviewCancelledException)
            {
                return null;
            }
            finally
            {
                foreach (var shape in shapes) shape.Dispose();
            }
        });
    }

    // =========================================================================================
    // COMPARE
    // =========================================================================================

    private sealed record ComparedOption(string Name, MergePreviewResponse Preview);

    private async Task<SyncResult> CompareCore(
        SyncTarget current, IReadOnlyList<string>? requested, string at, CompareMode mode, bool interactive, CancellationToken cancellationToken)
    {
        var bound = Normalize(current);
        var client = ClientFor(bound.Server);
        await RequireRepoAsync(client, bound.Repo, cancellationToken).ConfigureAwait(false);
        OnUi(() =>
        {
            Rebind(bound.Server, bound.Repo);
            _copy.UseTarget(bound.Server, bound.Repo, bound.Branch);
        });
        if (_copy.BaseCommit is null) return SyncResult.Fail("This model is not on a commit yet. Pull a branch first.");
        var atName = string.IsNullOrWhiteSpace(at) ? Names.DefaultBranch : at.Trim();
        if (ContentHash.IsValid(atName)) atName = ShortCommit(atName);
        var atCommit = (await client.GetCommitAsync(bound.Repo, string.IsNullOrWhiteSpace(at) ? Names.DefaultBranch : at.Trim(), cancellationToken).ConfigureAwait(false)).Id;

        var branches = (await client.ListBranchesAsync(bound.Repo, cancellationToken).ConfigureAwait(false)).Branches;
        var names = requested is { Count: > 0 }
            ? requested.Select(n => n.Trim()).Where(n => n.Length > 0).ToList()
            : branches.Where(b => b.Name != atName && (b.Parent == atName || branches.All(x => x.Name != b.Parent))).Select(b => b.Name).ToList();
        if (names.Count == 0) return SyncResult.Fail($"There are no branches to compare with {atName}. Name them with the Options option.");

        var options = new List<ComparedOption>();
        foreach (var name in names)
        {
            var preview = await client.PreviewMergeAsync(bound.Repo, new MergePreviewRequest
            {
                Ours = atCommit,
                Theirs = name,
                Manifests = true,
                CompareTo = atCommit,
            }, cancellationToken).ConfigureAwait(false);
            options.Add(new ComparedOption(name, preview));
        }

        // What the screen must change to show `at` as the baseline (nothing when the model is on it).
        var baseline = _copy.BaseCommit == atCommit
            ? new List<DiffEntry>()
            : (await client.DiffAsync(bound.Repo, _copy.BaseCommit, atCommit, includeManifests: true, cancellationToken).ConfigureAwait(false)).Changes;
        var geometry = await DownloadGeometryAsync(client, bound.Repo, options.SelectMany(o => o.Preview.Changes).Concat(baseline), cancellationToken)
            .ConfigureAwait(false);
        var keep = !interactive;
        try
        {
            var outcome = OnUi(() =>
            {
                var doc = _copy.Require();
                var index = 0;
                while (true)
                {
                    Show(doc, BuildCompareScene(doc, options, baseline, geometry, mode, index, atName));
                    if (!interactive) return "";
                    var choice = Choose(
                        mode == CompareMode.Cycle ? $"Option {index + 1}/{options.Count}: {options[index].Name}" : $"{options.Count} options on {atName} ({mode})",
                        "Next", "Previous", "Cycle", "Overlay", "Side", "Done");
                    switch (choice)
                    {
                        case 0: index = (index + 1) % options.Count; break;
                        case 1: index = (index + options.Count - 1) % options.Count; break;
                        case 2: mode = CompareMode.Cycle; break;
                        case 3: mode = CompareMode.Overlay; break;
                        case 4: mode = CompareMode.Side; break;
                        default: return options[index].Name;
                    }
                }
            });
            var summary = string.Join("; ", options.Select(o =>
                $"{o.Name}: +{o.Preview.Incoming.Added} ~{o.Preview.Incoming.Modified} -{o.Preview.Incoming.Deleted}" +
                (o.Preview.Conflicts.Count > 0 ? $", {o.Preview.Conflicts.Count} conflicts" : "")));
            return SyncResult.Success($"Compared with {atName}: {summary}." + (keep ? " The preview stays on screen; run RhinoClientCompare Off to clear it." : ""));
        }
        finally
        {
            if (!keep)
            {
                foreach (var item in geometry.Values) item.Dispose();
                OnUi(() => ClearPreview());
            }
        }
    }

    private static PreviewScene BuildCompareScene(
        RhinoDoc doc, IReadOnlyList<ComparedOption> options, IReadOnlyList<DiffEntry> baseline, IReadOnlyDictionary<string, GeometryBase> geometry,
        CompareMode mode, int index, string atName)
    {
        var scene = new PreviewScene();
        var hud = new List<string>();
        switch (mode)
        {
            case CompareMode.Cycle:
            {
                var option = options[index];
                AddResult(scene, doc, option.Preview.Changes, baseline, geometry, Transform.Identity, hide: true);
                hud.Add($"Option {index + 1} of {options.Count}: {option.Name} on {atName}");
                hud.Add(Counts(option.Preview));
                break;
            }
            case CompareMode.Overlay:
            {
                AddResult(scene, doc, Array.Empty<DiffEntry>(), baseline, geometry, Transform.Identity, hide: true);
                foreach (var option in options)
                    AddResult(scene, doc, option.Preview.Changes, Array.Empty<DiffEntry>(), geometry, Transform.Identity, hide: true, ghost: true);
                hud.Add($"{options.Count} options overlaid on {atName}");
                hud.AddRange(options.Select(o => o.Name + ": " + Counts(o.Preview)));
                break;
            }
            default:
            {
                var model = ModelBounds(doc);
                var step = Math.Max(model.Diagonal.X, 1) * 1.25;
                AddResult(scene, doc, Array.Empty<DiffEntry>(), baseline, geometry, Transform.Identity, hide: true);
                scene.Labels.Add(new PreviewLabel(atName, Above(model, 0), Color.FromArgb(40, 40, 40), 16));
                for (var i = 0; i < options.Count; i++)
                {
                    var shift = Transform.Translation(step * (i + 1), 0, 0);
                    var changed = options[i].Preview.Changes.Select(c => c.Id).Concat(baseline.Select(c => c.Id)).ToHashSet(StringComparer.Ordinal);
                    foreach (var obj in doc.Objects.GetObjectList(new Rhino.DocObjects.ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false }))
                        if (!changed.Contains(obj.Id.ToString("D"))) scene.Copies.Add((obj.Id, shift));
                    AddResult(scene, doc, options[i].Preview.Changes, baseline, geometry, shift, hide: false);
                    scene.Labels.Add(new PreviewLabel(options[i].Name, Above(model, step * (i + 1)), Color.FromArgb(40, 40, 40), 16));
                }

                hud.Add($"{options.Count} options side by side, {atName} first");
                hud.AddRange(options.Select(o => o.Name + ": " + Counts(o.Preview)));
                break;
            }
        }

        hud.Add("Green added, red removed, blue modified.");
        scene.Hud = hud;
        return scene;
    }

    private static string Counts(MergePreviewResponse preview) =>
        $"+{preview.Incoming.Added} ~{preview.Incoming.Modified} -{preview.Incoming.Deleted}" +
        (preview.Conflicts.Count > 0 ? $", {preview.Conflicts.Count} conflicts (shown as on the target)" : "");

    // =========================================================================================
    // Scenes
    // =========================================================================================

    /// <summary>
    /// Draws a merge result. <paramref name="coloured"/> are changes relative to the target (green, blue, red);
    /// <paramref name="neutral"/> are further changes that bring the screen to the result, drawn grey (or hidden).
    /// With <paramref name="hide"/> the document objects being replaced are culled; otherwise everything is drawn
    /// with <paramref name="shift"/> as a separate copy.
    /// </summary>
    private static void AddResult(
        PreviewScene scene, RhinoDoc doc, IEnumerable<DiffEntry> coloured, IEnumerable<DiffEntry> neutral,
        IReadOnlyDictionary<string, GeometryBase> geometry, Transform shift, bool hide, bool ghost = false)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in coloured.Where(c => c.Kind == EntityKinds.Object))
        {
            done.Add(change.Id);
            var id = Guid.Parse(change.Id);
            if (hide) scene.Hidden.Add(id);
            switch (change.Change)
            {
                case DiffChanges.Added:
                    AddShape(scene, Geometry(geometry, change.ToManifest), AddedColor, ghost ? 0.4 : 0, shift);
                    break;
                case DiffChanges.Modified:
                    AddShape(scene, Geometry(geometry, change.ToManifest), ModifiedColor, ghost ? 0.4 : 0, shift);
                    break;
                default:
                    AddShape(scene, Geometry(geometry, change.FromManifest) ?? doc.Objects.FindId(id)?.Geometry, RemovedColor, GhostTransparency, shift);
                    break;
            }
        }

        foreach (var change in neutral.Where(c => c.Kind == EntityKinds.Object && !done.Contains(c.Id)))
        {
            var id = Guid.Parse(change.Id);
            if (hide) scene.Hidden.Add(id);
            if (change.Change != DiffChanges.Deleted)
                AddShape(scene, Geometry(geometry, change.ToManifest), NeutralColor, 0, shift, silhouette: false);
        }

        foreach (var drawable in scene.Items) scene.Bounds = Union(scene.Bounds, drawable.Mesh?.GetBoundingBox(false) ?? drawable.Curve?.GetBoundingBox(false) ?? BoundingBox.Empty);
    }

    private static void AddShape(PreviewScene scene, GeometryBase? source, Color color, double transparency, Transform shift, bool silhouette = true)
    {
        if (source is null) return;
        using var moved = source.Duplicate();
        if (!shift.IsIdentity) moved.Transform(shift);
        var shape = CapturedShape.From(moved);
        scene.Owned.Add(shape);
        if (!shape.HasDrawable) return;
        scene.Items.Add(new PreviewDrawable
        {
            Mesh = shape.Mesh,
            Curve = shape.Curve,
            Point = shape.Point,
            Cloud = shape.Cloud,
            Fill = color,
            Outline = color,
            Transparency = transparency,
            OutlineWidth = 2,
            Silhouette = silhouette,
        });
    }

    private static GeometryBase? Geometry(IReadOnlyDictionary<string, GeometryBase> geometry, Manifest? manifest) =>
        manifest?.Components.TryGetValue(DocumentMapper.GeometryComponent, out var hash) == true && geometry.TryGetValue(hash!, out var found) ? found : null;

    private static void Show(RhinoDoc doc, PreviewScene scene)
    {
        var conduit = PreviewConduit.Install();
        conduit.Show(scene);
        doc.Views.Redraw();
    }

    private static BoundingBox ModelBounds(RhinoDoc doc)
    {
        var box = BoundingBox.Empty;
        // Annotations are left out: their size follows the view and text scale, not the model.
        foreach (var obj in doc.Objects.GetObjectList(new Rhino.DocObjects.ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true }))
            if (obj.Geometry is { } geometry and not AnnotationBase and not TextDot) box = Union(box, geometry.GetBoundingBox(false));
        return box.IsValid ? box : new BoundingBox(Point3d.Origin, new Point3d(1, 1, 1));
    }

    private static Point3d Above(BoundingBox box, double dx) =>
        new(box.Center.X + dx, box.Max.Y + box.Diagonal.Y * 0.1, box.Max.Z);

    private static BoundingBox Union(BoundingBox left, BoundingBox right)
    {
        if (!left.IsValid) return right;
        if (!right.IsValid) return left;
        left.Union(right);
        return left;
    }

    /// <summary>A command-line choice on Rhino's UI thread. Returns the option index, or -1 when cancelled.</summary>
    private static int Choose(string prompt, params string[] options)
    {
        var get = new GetOption();
        get.SetCommandPrompt(prompt);
        get.AcceptNothing(false);
        var indexes = options.Select(option => get.AddOption(Option(option))).ToList();
        if (get.Get() != GetResult.Option) return -1;
        var picked = get.Option()?.Index ?? -1;
        return indexes.IndexOf(picked);
    }

    /// <summary>A command option name: letters and digits only.</summary>
    private static string Option(string text)
    {
        var name = new string(text.Where(char.IsLetterOrDigit).ToArray());
        return name.Length == 0 ? "Option" : name;
    }

    // =========================================================================================
    // Blobs
    // =========================================================================================

    private static async Task<Dictionary<string, GeometryBase>> DownloadGeometryAsync(
        RhinoTrunkClient client, string repo, IEnumerable<DiffEntry> changes, CancellationToken cancellationToken)
    {
        var hashes = changes.Where(c => c.Kind == EntityKinds.Object)
            .SelectMany(c => new[] { c.ToManifest, c.FromManifest })
            .Select(m => m?.Components.GetValueOrDefault(DocumentMapper.GeometryComponent))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var blobs = await DownloadAsync(client, repo, hashes, cancellationToken).ConfigureAwait(false);
        var decoded = new Dictionary<string, GeometryBase>(StringComparer.Ordinal);
        foreach (var (hash, bytes) in blobs)
        {
            try
            {
                if (CommonObject.FromJSON(Encoding.UTF8.GetString(bytes)) is GeometryBase geometry) decoded[hash] = geometry;
            }
            catch (Exception)
            {
            }
        }

        return decoded;
    }

    private static async Task<Dictionary<string, Manifest>> ManifestsAsync(RhinoTrunkClient client, string repo, IEnumerable<string> hashes, CancellationToken cancellationToken)
    {
        var blobs = await DownloadAsync(client, repo, hashes.Distinct(StringComparer.Ordinal).ToList(), cancellationToken).ConfigureAwait(false);
        var manifests = new Dictionary<string, Manifest>(StringComparer.Ordinal);
        foreach (var (hash, bytes) in blobs)
        {
            if (JsonSerializer.Deserialize<Manifest>(bytes, RhinoTrunkJson.Options) is { } manifest) manifests[hash] = manifest;
        }

        return manifests;
    }

    private static GeometryBase? DecodeGeometry(Manifest manifest, IReadOnlyDictionary<string, byte[]> blobs)
    {
        if (!manifest.Components.TryGetValue(DocumentMapper.GeometryComponent, out var hash) || !blobs.TryGetValue(hash, out var bytes)) return null;
        try
        {
            return CommonObject.FromJSON(Encoding.UTF8.GetString(bytes)) as GeometryBase;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
