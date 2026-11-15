using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using System.Drawing;

namespace RhinoClient.Sync;

internal enum ConflictChoice
{
    KeepExisting,
    KeepIncoming,
    Merge,
}

/// <summary>The reviewer closed the prompt. The document has not been changed.</summary>
internal sealed class PullReviewCancelledException : Exception
{
}

/// <summary>
/// Finds pulled objects whose id is already in the document with different geometry or attributes,
/// and asks, one object at a time, whether to keep the document object, replace it, or add the
/// incoming geometry under a new id.
/// </summary>
internal static class PullConflicts
{
    private const double GhostTransparency = 0.8;

    internal static string ScriptedMessage(IReadOnlyList<ObjectConflict> conflicts)
    {
        var listed = string.Join(", ", conflicts.Take(8).Select(conflict => conflict.Id.ToString("D")));
        var extra = conflicts.Count > 8 ? $", and {conflicts.Count - 8} more" : "";
        return $"Pull stopped. {conflicts.Count} objects already in the document differ: {listed}{extra}. Run RhinoClientPull to review them in the viewport.";
    }

    internal static Dictionary<string, ConflictChoice> Review(RhinoDoc doc, IReadOnlyList<ObjectConflict> conflicts) =>
        Review(doc, conflicts, allowMerge: true, existingLabel: "the one already in the document", incomingLabel: "the incoming object");

    /// <summary>
    /// Steps through conflicts in the viewport. <paramref name="allowMerge"/> offers keeping both copies (pulls only).
    /// The labels name the two sides in the prompt (amber is "existing", blue is "incoming").
    /// </summary>
    internal static Dictionary<string, ConflictChoice> Review(RhinoDoc doc, IReadOnlyList<ObjectConflict> conflicts, bool allowMerge, string existingLabel, string incomingLabel)
    {
        var conduit = PreviewConduit.Current ?? throw new SyncException("RhinoClient is not loaded.");
        var choices = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
        var incomingSolid = true;
        var index = 0;
        RhinoApp.WriteLine(
            "RhinoClient: " + conflicts.Count + " objects differ. Blue is " + incomingLabel + ", amber is " + existingLabel + "." +
            (allowMerge ? " Merge keeps both and gives the incoming object a new id." : ""));
        try
        {
            while (index < conflicts.Count)
            {
                Show(doc, conduit, conflicts, index, incomingSolid);
                switch (Prompt(conflicts[index], index, conflicts.Count, allowMerge))
                {
                    case ReviewPrompt.Cancel:
                        throw new PullReviewCancelledException();
                    case ReviewPrompt.Toggle:
                        incomingSolid = !incomingSolid;
                        break;
                    case ReviewPrompt.KeepExisting:
                        choices[conflicts[index].EntityId] = ConflictChoice.KeepExisting;
                        index++;
                        incomingSolid = true;
                        break;
                    case ReviewPrompt.KeepIncoming:
                        choices[conflicts[index].EntityId] = ConflictChoice.KeepIncoming;
                        index++;
                        incomingSolid = true;
                        break;
                    case ReviewPrompt.Merge:
                        choices[conflicts[index].EntityId] = ConflictChoice.Merge;
                        index++;
                        incomingSolid = true;
                        break;
                    case ReviewPrompt.RestExisting:
                        for (var i = index; i < conflicts.Count; i++)
                            choices[conflicts[i].EntityId] = ConflictChoice.KeepExisting;
                        index = conflicts.Count;
                        break;
                    case ReviewPrompt.RestIncoming:
                        for (var i = index; i < conflicts.Count; i++)
                            choices[conflicts[i].EntityId] = ConflictChoice.KeepIncoming;
                        index = conflicts.Count;
                        break;
                }
            }

            return choices;
        }
        finally
        {
            conduit.Hide();
            doc.Views.Redraw();
        }
    }

    private static void Show(RhinoDoc doc, PreviewConduit conduit, IReadOnlyList<ObjectConflict> conflicts, int index, bool incomingSolid)
    {
        var scene = new PreviewScene();
        var bounds = BoundingBox.Empty;
        for (var i = 0; i < conflicts.Count; i++)
        {
            var conflict = conflicts[i];
            if (!conflict.Deleted) scene.Hidden.Add(conflict.Id);
            var current = i == index;
            var width = current ? 6 : 2;
            Add(scene, conflict.Incoming, PreviewConduit.IncomingOutline, solid: !current || incomingSolid, width);
            Add(scene, conflict.Existing, PreviewConduit.ExistingOutline, solid: current && !incomingSolid, width);
            bounds = Union(bounds, conflict.Bounds);
        }

        var currentConflict = conflicts[index];
        scene.Bounds = bounds;
        scene.Hud = Hud(currentConflict, index, conflicts.Count, incomingSolid);
        conduit.Show(scene);
        Zoom(doc, currentConflict.Bounds);
        doc.Views.Redraw();
    }

    private static void Add(PreviewScene scene, CapturedShape shape, Color color, bool solid, int width)
    {
        if (!shape.HasDrawable) return;
        scene.Items.Add(new PreviewDrawable
        {
            Mesh = shape.Mesh,
            Curve = shape.Curve,
            Point = shape.Point,
            Cloud = shape.Cloud,
            Fill = color,
            Outline = color,
            Transparency = solid ? 0 : GhostTransparency,
            OutlineWidth = width,
        });
    }

    private static string[] Hud(ObjectConflict conflict, int index, int count, bool incomingSolid)
    {
        var which = incomingSolid
            ? "Incoming is solid blue. The document object is ghosted amber."
            : "The document object is solid amber. Incoming is ghosted blue.";
        var what = conflict.IncomingDeleted
            ? "The incoming commit deletes this object; you changed it."
            : conflict.Deleted
                ? "You deleted this object; the incoming commit changed it."
                : "You and the incoming commit both changed this object.";
        return new[]
        {
            "Conflict " + (index + 1) + " of " + count,
            conflict.Id.ToString("D"),
            what,
            which,
        };
    }

    private static BoundingBox Union(BoundingBox left, BoundingBox right)
    {
        if (!left.IsValid) return right;
        if (!right.IsValid) return left;
        left.Union(right);
        return left;
    }

    private static void Zoom(RhinoDoc doc, BoundingBox bounds)
    {
        var viewport = doc.Views.ActiveView?.ActiveViewport;
        if (viewport is null || !bounds.IsValid) return;
        var box = bounds;
        var pad = Math.Max(box.Diagonal.Length * 0.08, 1);
        box.Inflate(pad);
        viewport.ZoomBoundingBox(box);
    }

    private static ReviewPrompt Prompt(ObjectConflict conflict, int index, int count, bool allowMerge)
    {
        var id = conflict.Id.ToString("D");
        var get = new GetOption();
        get.SetCommandPrompt("Conflict " + (index + 1) + "/" + count + " " + id[..8] + ". Keep which object?");
        get.AcceptNothing(false);
        var keepExisting = get.AddOption("KeepExisting");
        var keepIncoming = get.AddOption("KeepIncoming");
        var merge = allowMerge ? get.AddOption("Merge") : -1;
        var toggle = get.AddOption("Toggle");
        var restExisting = get.AddOption("RestExisting");
        var restIncoming = get.AddOption("RestIncoming");
        if (get.Get() != GetResult.Option) return ReviewPrompt.Cancel;
        var picked = get.Option()?.Index ?? 0;
        if (picked == keepExisting) return ReviewPrompt.KeepExisting;
        if (picked == keepIncoming) return ReviewPrompt.KeepIncoming;
        if (picked == merge) return ReviewPrompt.Merge;
        if (picked == toggle) return ReviewPrompt.Toggle;
        if (picked == restExisting) return ReviewPrompt.RestExisting;
        if (picked == restIncoming) return ReviewPrompt.RestIncoming;
        return ReviewPrompt.Cancel;
    }

    private enum ReviewPrompt
    {
        Cancel,
        Toggle,
        KeepExisting,
        KeepIncoming,
        Merge,
        RestExisting,
        RestIncoming,
    }
}

internal sealed class ObjectConflict : IDisposable
{
    private ObjectConflict(string entityId, Guid id, bool deleted, CapturedShape existing, CapturedShape incoming)
    {
        EntityId = entityId;
        Id = id;
        Deleted = deleted;
        Existing = existing;
        Incoming = incoming;
        Bounds = Union(existing.Bounds, incoming.Bounds);
    }

    internal string EntityId { get; }
    internal Guid Id { get; }
    internal bool Deleted { get; }
    internal CapturedShape Existing { get; }
    internal CapturedShape Incoming { get; }
    internal BoundingBox Bounds { get; }

    /// <summary>
    /// A three-way conflict: the document and the incoming commit both changed the object since the base.
    /// <paramref name="existing"/> is null when it was deleted locally, <paramref name="incoming"/> when the commit deletes it.
    /// </summary>
    internal static ObjectConflict Create(string entityId, Guid id, bool existingDeleted, GeometryBase? existing, GeometryBase? incoming)
    {
        var existingShape = CapturedShape.From(existing);
        CapturedShape incomingShape;
        try
        {
            incomingShape = CapturedShape.From(incoming);
        }
        catch
        {
            existingShape.Dispose();
            throw;
        }

        return new ObjectConflict(entityId, id, existingDeleted || existing is null, existingShape, incomingShape) { IncomingDeleted = incoming is null };
    }

    internal bool IncomingDeleted { get; private init; }

    public void Dispose()
    {
        Existing.Dispose();
        Incoming.Dispose();
    }

    private static BoundingBox Union(BoundingBox left, BoundingBox right)
    {
        if (!left.IsValid) return right;
        if (!right.IsValid) return left;
        left.Union(right);
        return left;
    }
}

internal sealed class CapturedShape : IDisposable
{
    private CapturedShape(BoundingBox bounds, Mesh? mesh, Curve? curve, Point3d? point, PointCloud? cloud)
    {
        Bounds = bounds;
        Mesh = mesh;
        Curve = curve;
        Point = point;
        Cloud = cloud;
    }

    internal BoundingBox Bounds { get; }
    internal Mesh? Mesh { get; }
    internal Curve? Curve { get; }
    internal Point3d? Point { get; }
    internal PointCloud? Cloud { get; }
    internal bool HasDrawable => Mesh is not null || Curve is not null || Point is not null || Cloud is { Count: > 0 };

    internal static CapturedShape From(GeometryBase? source)
    {
        if (source is null) return new CapturedShape(BoundingBox.Empty, null, null, null, null);
        var bounds = source.GetBoundingBox(true);
        var copy = source.Duplicate();
        switch (copy)
        {
            case Curve curve:
                return new CapturedShape(bounds, null, curve, null, null);
            case Rhino.Geometry.Point point:
                var location = point.Location;
                point.Dispose();
                return new CapturedShape(bounds, null, null, location, null);
            case PointCloud cloud:
                return new CapturedShape(bounds, null, null, null, cloud);
            case Mesh mesh:
                var drawn = mesh.DuplicateMesh();
                mesh.Dispose();
                drawn.Normals.ComputeNormals();
                return new CapturedShape(bounds, drawn, null, null, null);
            default:
                var meshed = ToMesh(copy);
                copy.Dispose();
                return new CapturedShape(bounds, meshed, null, null, null);
        }
    }

    public void Dispose()
    {
        Mesh?.Dispose();
        Curve?.Dispose();
        Cloud?.Dispose();
    }

    private static Mesh? ToMesh(GeometryBase geometry)
    {
        switch (geometry)
        {
            case Brep brep:
                return Join(Mesh.CreateFromBrep(brep, MeshingParameters.Default));
            case Extrusion extrusion:
                var fromExtrusion = extrusion.ToBrep(false);
                if (fromExtrusion is null) return null;
                using (fromExtrusion)
                    return Join(Mesh.CreateFromBrep(fromExtrusion, MeshingParameters.Default));
            case SubD subd:
                var fromSubD = subd.ToBrep();
                if (fromSubD is null) return null;
                using (fromSubD)
                    return Join(Mesh.CreateFromBrep(fromSubD, MeshingParameters.Default));
            default:
                var converted = Brep.TryConvertBrep(geometry);
                if (converted is null) return null;
                using (converted)
                    return Join(Mesh.CreateFromBrep(converted, MeshingParameters.Default));
        }
    }

    private static Mesh? Join(Mesh[]? parts)
    {
        if (parts is null || parts.Length == 0) return null;
        if (parts.Length == 1)
        {
            parts[0].Normals.ComputeNormals();
            return parts[0];
        }

        var mesh = new Mesh();
        foreach (var part in parts)
        {
            mesh.Append(part);
            part.Dispose();
        }

        mesh.Normals.ComputeNormals();
        return mesh;
    }
}
