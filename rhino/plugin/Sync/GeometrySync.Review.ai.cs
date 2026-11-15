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
}

/// <summary>BRANCH, MERGE and COMPARE: operations that read or move branches and show the result in the viewport.</summary>
public sealed partial class GeometrySync
{
    internal static readonly Color AddedColor = Color.FromArgb(0, 220, 70);
    internal static readonly Color RemovedColor = Color.FromArgb(235, 40, 40);
    internal static readonly Color ModifiedColor = Color.FromArgb(40, 120, 255);
    internal static readonly Color NeutralColor = Color.FromArgb(150, 150, 150);
    /// <summary>Objects on the layers a blocked merge trips over.</summary>
    internal static readonly Color ClashColor = Color.FromArgb(245, 150, 0);
    /// <summary>Removed objects show at 10% opacity inside their red outline.</summary>
    private const double GhostTransparency = 0.9;
    private const int OutlinePixels = 3;

    /// <summary>"● 3 added ● 1 modified ● 2 removed" for a HUD line, with dots in the preview's colours.</summary>
    internal static string Legend(int added, int modified, int removed) =>
        $"{PreviewConduit.Swatch(AddedColor)}{added} added {PreviewConduit.Swatch(ModifiedColor)}{modified} modified {PreviewConduit.Swatch(RemovedColor)}{removed} removed";

    /// <summary>A light tint of a change colour for the object's fill; the outline carries the full colour.</summary>
    internal static Color Tint(Color color, double amount = 0.3) => Color.FromArgb(
        (int)(225 + (color.R - 225) * amount), (int)(225 + (color.G - 225) * amount), (int)(225 + (color.B - 225) * amount));

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
    /// <param name="focusLayers">
    /// Layers to pick out: objects on them or their sublayers are drawn amber and the rest of each option's changes dimmed.
    /// </param>
    /// <param name="clearHint">How the user clears a preview left on screen; defaults to the Off option.</param>
    public Task<SyncResult> CompareAsync(
        SyncTarget current, IReadOnlyList<string>? options, string at, CompareMode mode, bool interactive, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? focusLayers = null, string? clearHint = null) =>
        Run(() => CompareCore(current, options, at, mode, interactive, cancellationToken, focusLayers, clearHint), cancellationToken);

    /// <summary>Tag of a scene showing an agent task's result, so clearing that preview leaves other previews alone.</summary>
    internal const string TaskSceneTag = "task";

    /// <summary>
    /// Shows an agent task's result the way commit and merge previews look: against the snapshot the task started
    /// from, added objects green, modified blue (the model's version hidden), removed as red ghosts, each with the
    /// same fill, GLSL outline and info card. Objects the task did not touch are the model's own, drawn as usual.
    /// Without a snapshot every object of the result is drawn neutral over a hidden model. Call on the UI thread.
    /// </summary>
    /// <param name="baseline">Geometry of the starting snapshot by object id; null when there is none.</param>
    /// <param name="hud">The card's title and detail lines; the counts and a hint are added below them.</param>
    internal static (int Added, int Modified, int Removed) ShowTaskResult(
        RhinoDoc source, RhinoDoc result, IReadOnlyDictionary<Guid, GeometryBase>? baseline, IReadOnlyList<string> hud, bool zoom)
    {
        var scene = new PreviewScene { Tag = TaskSceneTag };
        int added = 0, modified = 0, removed = 0;
        var live = result.Objects.Where(obj => !obj.IsDeleted && !obj.IsHidden).ToList();
        if (baseline is null)
        {
            foreach (var obj in source.Objects.Where(obj => !obj.IsDeleted)) scene.Hidden.Add(obj.Id);
            foreach (var obj in live) AddShape(scene, obj.Geometry, Tint(NeutralColor, 0.6), NeutralColor, 0, Transform.Identity);
        }
        else
        {
            var seen = new HashSet<Guid>();
            foreach (var obj in live)
            {
                seen.Add(obj.Id);
                if (!baseline.TryGetValue(obj.Id, out var before))
                {
                    added++;
                    AddShape(scene, obj.Geometry, Tint(AddedColor), AddedColor, 0, Transform.Identity);
                }
                else if (!SameGeometry(before, obj.Geometry))
                {
                    modified++;
                    scene.Hidden.Add(obj.Id);
                    AddShape(scene, obj.Geometry, Tint(ModifiedColor), ModifiedColor, 0, Transform.Identity);
                }
            }

            foreach (var (id, before) in baseline)
            {
                if (seen.Contains(id)) continue;
                removed++;
                scene.Hidden.Add(id);
                AddShape(scene, before, NeutralColor, RemovedColor, GhostTransparency, Transform.Identity);
            }
        }

        foreach (var item in scene.Items)
            scene.Bounds = Union(scene.Bounds, item.Mesh?.GetBoundingBox(false) ?? item.Curve?.GetBoundingBox(false) ?? BoundingBox.Empty);
        var lines = hud.ToList();
        lines.Add(baseline is null
            ? "No starting snapshot to compare with: showing the task's whole copy."
            : "Against the snapshot it started from: " + Legend(added, modified, removed));
        lines.Add("Open a pull request or merge it from the Agents tab; select another task to compare.");
        scene.Hud = lines;
        Show(source, scene);
        if (zoom && scene.Bounds.IsValid) source.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(scene.Bounds);
        source.Views.Redraw();
        return (added, modified, removed);
    }

    private static bool SameGeometry(GeometryBase before, GeometryBase after)
    {
        try
        {
            return GeometryBase.GeometryEquals(before, after);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Removes an agent task's preview, if that is what the viewport is showing.</summary>
    internal static void ClearTaskPreview()
    {
        if (PreviewConduit.Current?.Scene?.Tag == TaskSceneTag) ClearPreview();
    }

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

        // Layers both branches created separately at the same path: this branch takes over the target's ids first, so
        // the merge sees one layer instead of two with the same name. The model has no unpushed edits (checked above),
        // so it follows the branch to that commit.
        var layerNote = "";
        var adopted = await AdoptLayersAsync(client, bound.Repo, bound.Branch, head.Commit, into, $"'{into}'", cancellationToken).ConfigureAwait(false);
        if (adopted is not null)
        {
            head = await client.GetBranchAsync(bound.Repo, bound.Branch, cancellationToken).ConfigureAwait(false)
                ?? throw new SyncException($"Branch '{bound.Branch}' disappeared.");
            var followed = await PullCommitAsync(current, null, interactive: false, discardLocal: true, cancellationToken).ConfigureAwait(false);
            if (!followed.Ok) return SyncResult.Fail($"Took over the layers of '{into}' on {bound.Branch}, but updating the model failed: {followed.Message}");
            layerNote = $"{bound.Branch} took over {(adopted.Paths.Count == 1 ? "the layer" : $"{adopted.Paths.Count} layers")} {Quoted(adopted.Paths)} from '{into}' first, since both branches had created them separately. ";
            RhinoApp.WriteLine("RhinoClient: " + layerNote.TrimEnd());
        }

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
        if (!preview.CanMerge)
        {
            var problem = $"Cannot merge {bound.Branch} into {into}. " + (preview.Validation.Count > 0 ? DescribeValidation(preview.Validation) : MergeProblems(preview));
            if (preview.Validation.Count == 0) return SyncResult.Fail(problem);
            // Show why: each branch's changes on the commit they split from, one at a time, until the user is done looking.
            RhinoApp.WriteLine("RhinoClient: " + problem);
            var at = preview.MergeBase ?? preview.Ours;
            var focus = preview.Validation.Where(issue => issue.Kind == EntityKinds.Layer && !string.IsNullOrEmpty(issue.Id)).Select(issue => issue.Id!).ToList();
            if (interactive)
                await CompareCore(current, new[] { bound.Branch, into }, at, CompareMode.Cycle, interactive: true, cancellationToken, focus).ConfigureAwait(false);
            return SyncResult.Fail(problem + " Rename or remove the layers on one branch, push, and merge again.");
        }
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
                Legend(preview.Incoming.Added, preview.Incoming.Modified, preview.Incoming.Deleted),
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
        var done = layerNote + $"Merged {bound.Branch} into {into} ({ShortCommit(merged.Commit)}, {merged.Status}).";
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
        SyncTarget current, IReadOnlyList<string>? requested, string at, CompareMode mode, bool interactive, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? focusLayers = null, string? clearHint = null)
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
                    Show(doc, BuildCompareScene(doc, options, baseline, geometry, mode, index, atName, focusLayers));
                    if (!interactive) return "";
                    var choice = Choose(
                        mode == CompareMode.Cycle ? $"Option {index + 1}/{options.Count}: {options[index].Name}" : $"{options.Count} options on {atName} ({mode})",
                        "Next", "Previous", "Cycle", "Overlay", "Done");
                    switch (choice)
                    {
                        case 0: index = (index + 1) % options.Count; break;
                        case 1: index = (index + options.Count - 1) % options.Count; break;
                        case 2: mode = CompareMode.Cycle; break;
                        case 3: mode = CompareMode.Overlay; break;
                        default: return options[index].Name;
                    }
                }
            });
            var summary = string.Join("; ", options.Select(o =>
                $"{o.Name}: +{o.Preview.Incoming.Added} ~{o.Preview.Incoming.Modified} -{o.Preview.Incoming.Deleted}" +
                (o.Preview.Conflicts.Count > 0 ? $", {o.Preview.Conflicts.Count} conflicts" : "")));
            return SyncResult.Success($"Compared with {atName}: {summary}." + (keep ? $" The preview stays on screen; {clearHint ?? "run RhinoClientCompare Off to clear it"}." : ""));
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
        CompareMode mode, int index, string atName, IReadOnlyCollection<string>? focusLayers = null)
    {
        var scene = new PreviewScene();
        var hud = new List<string>();
        // Objects on the layers a merge trips over (and their sublayers) are picked out; the rest are dimmed.
        var focus = focusLayers is { Count: > 0 }
            ? WithSublayers(focusLayers, options.SelectMany(o => o.Preview.Changes).Concat(baseline))
            : null;
        switch (mode)
        {
            case CompareMode.Cycle:
            {
                var option = options[index];
                AddResult(scene, doc, option.Preview.Changes, baseline, geometry, Transform.Identity, hide: true, focus: focus);
                hud.Add($"Option {index + 1} of {options.Count}: {option.Name} on {atName}");
                hud.Add(Counts(option.Preview));
                break;
            }
            default:
            {
                AddResult(scene, doc, Array.Empty<DiffEntry>(), baseline, geometry, Transform.Identity, hide: true);
                foreach (var option in options)
                    AddResult(scene, doc, option.Preview.Changes, Array.Empty<DiffEntry>(), geometry, Transform.Identity, hide: true, ghost: true);
                hud.Add($"{options.Count} options overlaid on {atName}");
                hud.AddRange(options.Select(o => o.Name + ": " + Counts(o.Preview)));
                break;
            }
        }

        if (focus is not null)
            hud.Add($"{PreviewConduit.Swatch(ClashColor)}On the layers both branches created {PreviewConduit.Swatch(NeutralColor)}The branch's other changes");
        scene.Hud = hud;
        return scene;
    }

    private static string Counts(MergePreviewResponse preview) =>
        Legend(preview.Incoming.Added, preview.Incoming.Modified, preview.Incoming.Deleted) +
        (preview.Conflicts.Count > 0 ? $"  ·  {preview.Conflicts.Count} conflicts (shown as on the target)" : "");

    // =========================================================================================
    // Scenes
    // =========================================================================================

    /// <summary>
    /// Draws a merge result. <paramref name="coloured"/> are changes relative to the target (green, blue, red);
    /// <paramref name="neutral"/> are further changes that bring the screen to the result, drawn grey (or hidden).
    /// With <paramref name="hide"/> the document objects being replaced are culled; otherwise everything is drawn
    /// with <paramref name="shift"/> as a separate copy.
    /// </summary>
    /// <param name="focus">
    /// When set, objects on these layers are drawn amber and every other coloured change is dimmed grey, so a merge
    /// problem on a few layers stands out from a branch's other work.
    /// </param>
    private static void AddResult(
        PreviewScene scene, RhinoDoc doc, IEnumerable<DiffEntry> coloured, IEnumerable<DiffEntry> neutral,
        IReadOnlyDictionary<string, GeometryBase> geometry, Transform shift, bool hide, bool ghost = false, IReadOnlySet<string>? focus = null)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in coloured.Where(c => c.Kind == EntityKinds.Object))
        {
            done.Add(change.Id);
            var id = Guid.Parse(change.Id);
            if (hide) scene.Hidden.Add(id);
            if (focus is not null)
            {
                var manifest = change.ToManifest ?? change.FromManifest;
                var shape = Geometry(geometry, manifest) ?? doc.Objects.FindId(id)?.Geometry;
                if (LayerOf(manifest) is { } layer && focus.Contains(layer))
                    AddShape(scene, shape, Tint(ClashColor, 0.5), ClashColor, change.Change == DiffChanges.Deleted ? GhostTransparency : 0, shift);
                else
                    AddShape(scene, shape, NeutralColor, NeutralColor, 0.75, shift, silhouette: false);
                continue;
            }

            switch (change.Change)
            {
                case DiffChanges.Added:
                    AddShape(scene, Geometry(geometry, change.ToManifest), Tint(AddedColor), AddedColor, ghost ? 0.4 : 0, shift);
                    break;
                case DiffChanges.Modified:
                    AddShape(scene, Geometry(geometry, change.ToManifest), Tint(ModifiedColor), ModifiedColor, ghost ? 0.4 : 0, shift);
                    break;
                default:
                    AddShape(scene, Geometry(geometry, change.FromManifest) ?? doc.Objects.FindId(id)?.Geometry, NeutralColor, RemovedColor, GhostTransparency, shift);
                    break;
            }
        }

        foreach (var change in neutral.Where(c => c.Kind == EntityKinds.Object && !done.Contains(c.Id)))
        {
            var id = Guid.Parse(change.Id);
            if (hide) scene.Hidden.Add(id);
            if (change.Change != DiffChanges.Deleted)
                AddShape(scene, Geometry(geometry, change.ToManifest), NeutralColor, NeutralColor, 0, shift, silhouette: false);
        }

        foreach (var drawable in scene.Items) scene.Bounds = Union(scene.Bounds, drawable.Mesh?.GetBoundingBox(false) ?? drawable.Curve?.GetBoundingBox(false) ?? BoundingBox.Empty);
    }

    private static void AddShape(PreviewScene scene, GeometryBase? source, Color fill, Color outline, double transparency, Transform shift, bool silhouette = true)
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
            Fill = fill,
            Outline = outline,
            Transparency = transparency,
            OutlineWidth = OutlinePixels,
            Silhouette = silhouette,
        });
    }

    private static string? LayerOf(Manifest? manifest) =>
        manifest?.Refs.FirstOrDefault(reference => reference.Role == RefRoles.Layer && reference.Kind == EntityKinds.Layer)?.Id;

    /// <summary><paramref name="roots"/> and every layer below them, from the layer entries in <paramref name="changes"/>.</summary>
    private static HashSet<string> WithSublayers(IEnumerable<string> roots, IEnumerable<DiffEntry> changes)
    {
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(c => c.Kind == EntityKinds.Layer))
        {
            var parent = (change.ToManifest ?? change.FromManifest)?.Refs
                .FirstOrDefault(reference => reference.Role == RefRoles.Parent && reference.Kind == EntityKinds.Layer)?.Id;
            if (parent is not null) parents[change.Id] = parent;
        }

        var layers = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (child, parent) in parents)
                if (layers.Contains(parent) && layers.Add(child)) grew = true;
        }

        return layers;
    }

    private static GeometryBase? Geometry(IReadOnlyDictionary<string, GeometryBase> geometry, Manifest? manifest) =>
        manifest?.Components.TryGetValue(DocumentMapper.GeometryComponent, out var hash) == true && geometry.TryGetValue(hash!, out var found) ? found : null;

    private static void Show(RhinoDoc doc, PreviewScene scene)
    {
        var conduit = PreviewConduit.Install();
        conduit.Show(scene);
        doc.Views.Redraw();
    }

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
