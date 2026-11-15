using Rhino.Display;
using Rhino.Geometry;
using System.Drawing;

namespace RhinoClient.Sync;

/// <summary>
/// One viewport overlay for a pull review. Document objects in <see cref="PreviewScene.Hidden"/>
/// are culled, and the incoming and existing geometry are drawn in their place.
/// </summary>
internal sealed class PreviewConduit : DisplayConduit
{
    internal static readonly Color IncomingOutline = Color.FromArgb(40, 120, 255);
    internal static readonly Color ExistingOutline = Color.FromArgb(255, 170, 0);

    private readonly Dictionary<(int Color, int Transparency), DisplayMaterial> _materials = new();

    internal static PreviewConduit? Current { get; private set; }

    internal static PreviewConduit Install()
    {
        Current ??= new PreviewConduit();
        return Current;
    }

    private PreviewConduit()
    {
        Enabled = false;
    }

    internal PreviewScene? Scene { get; private set; }

    internal void Show(PreviewScene scene)
    {
        _silhouettes.Clear();
        if (Scene is { } previous && !ReferenceEquals(previous, scene)) previous.DisposeOwned();
        Scene = scene;
        Enabled = true;
    }

    internal void Hide()
    {
        Enabled = false;
        Scene?.DisposeOwned();
        Scene = null;
        _silhouettes.Clear();
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (Scene is { Bounds.IsValid: true } scene) e.IncludeBoundingBox(scene.Bounds);
    }

    protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e)
    {
        if (Scene is { Bounds.IsValid: true } scene) e.IncludeBoundingBox(scene.Bounds);
    }

    protected override void ObjectCulling(CullObjectEventArgs e)
    {
        if (Scene is { } scene && e.RhinoObject is { } obj && scene.Hidden.Contains(obj.Id))
            e.CullObject = true;
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (Scene is not { } scene) return;
        var display = e.Display;
        foreach (var copy in scene.Copies)
        {
            if (e.RhinoDoc?.Objects.FindId(copy.ObjectId) is { IsDeleted: false } obj) display.DrawObject(obj, copy.Transform);
        }

        // GLSL outlines first, all in one go; the shaded meshes drawn next cover their inner half.
        var outlined = scene.Items.Where(item => item.Silhouette && Drawable(item) && item.Mesh is { Vertices.Count: > 0 })
            .Select(item => (item.Mesh!, item.Outline, (float)item.OutlineWidth)).ToList();
        var glsl = OutlineRenderer.Draw(e.Viewport, outlined);

        for (var pass = 0; pass < 2; pass++)
        {
            var opaquePass = pass == 0;
            foreach (var item in scene.Items)
            {
                if ((item.Transparency <= 0.001) != opaquePass) continue;
                if (!Drawable(item)) continue;
                if (item.Mesh is { Vertices.Count: > 0 } mesh)
                {
                    if (item.Silhouette && glsl)
                    {
                    }
                    else if (item.Silhouette)
                    {
                        // The outline is drawn first, thick and depth tested, so the shaded mesh drawn over it
                        // covers its inner half: what remains reads as an outline beneath the object.
                        foreach (var curve in Silhouettes(item, mesh, e.Viewport))
                            display.DrawCurve(curve, item.Outline, item.OutlineWidth * 2);
                    }
                    else
                    {
                        display.DrawMeshWires(mesh, item.Outline, item.OutlineWidth);
                    }

                    display.DrawMeshShaded(mesh, Material(item.Fill, item.Transparency));
                }
                else if (item.Curve is { } curve)
                {
                    display.DrawCurve(curve, item.Outline, item.OutlineWidth + 3);
                    var stroke = item.Transparency > 0.5 ? Color.FromArgb(120, item.Fill) : item.Fill;
                    display.DrawCurve(curve, stroke, 2);
                }
                else if (item.Point is { } point)
                {
                    display.DrawPoint(point, PointStyle.RoundSimple, item.OutlineWidth + 4, item.Outline);
                    display.DrawPoint(point, PointStyle.RoundSimple, 4, item.Fill);
                }
                else if (item.Cloud is { Count: > 0 } cloud)
                {
                    for (var i = 0; i < cloud.Count; i++)
                        display.DrawPoint(cloud.PointAt(i), PointStyle.RoundSimple, 4, item.Transparency > 0.5 ? item.Outline : item.Fill);
                }
            }
        }
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        if (Scene is not { } labelled) return;
        foreach (var label in labelled.Labels)
            e.Display.Draw2dText(label.Text, label.Color, label.Location, true, label.Height);
        if (Scene is not { Hud.Count: > 0 } scene) return;
        var y = 28;
        for (var i = 0; i < scene.Hud.Count; i++)
        {
            e.Display.Draw2dText(scene.Hud[i], Color.FromArgb(25, 25, 25), new Point2d(18, y), false, i == 0 ? 15 : 13);
            y += i == 0 ? 20 : 17;
        }
    }

    /// <summary>
    /// A preview must never take Rhino down: geometry that has been disposed since the scene was built is skipped.
    /// </summary>
    private static bool Drawable(PreviewDrawable item) =>
        (item.Mesh is null || !item.Mesh.Disposed) && (item.Curve is null || !item.Curve.Disposed) && (item.Cloud is null || !item.Cloud.Disposed);

    private readonly Dictionary<PreviewDrawable, (string Camera, Curve[] Curves)> _silhouettes = new();

    /// <summary>Silhouette curves of a mesh for the viewport's camera, cached until the camera moves.</summary>
    private Curve[] Silhouettes(PreviewDrawable item, Mesh mesh, RhinoViewport viewport)
    {
        var parallel = viewport.IsParallelProjection;
        var camera = parallel ? viewport.CameraDirection.ToString() : viewport.CameraLocation.ToString();
        if (_silhouettes.TryGetValue(item, out var cached) && cached.Camera == camera) return cached.Curves;
        var tolerance = Math.Max(mesh.GetBoundingBox(false).Diagonal.Length * 1e-4, 1e-6);
        var found = parallel
            ? Silhouette.Compute(mesh, SilhouetteType.Boundary | SilhouetteType.Projecting, viewport.CameraDirection, tolerance, 0.01)
            : Silhouette.Compute(mesh, SilhouetteType.Boundary | SilhouetteType.Projecting, viewport.CameraLocation, tolerance, 0.01);
        var curves = found?.Select(silhouette => silhouette.Curve).Where(curve => curve is not null).ToArray() ?? Array.Empty<Curve>();
        if (_silhouettes.Count > 2000) _silhouettes.Clear();
        _silhouettes[item] = (camera, curves);
        return curves;
    }

    private DisplayMaterial Material(Color color, double transparency)
    {
        var key = (color.ToArgb(), (int)Math.Round(transparency * 1000));
        if (_materials.TryGetValue(key, out var material)) return material;
        material = new DisplayMaterial(color, transparency);
        _materials[key] = material;
        return material;
    }
}

internal sealed class PreviewScene
{
    internal List<PreviewDrawable> Items { get; } = new();
    internal HashSet<Guid> Hidden { get; } = new();
    internal BoundingBox Bounds { get; set; } = BoundingBox.Empty;
    internal IReadOnlyList<string> Hud { get; set; } = Array.Empty<string>();
    /// <summary>Document objects drawn again with a transform (side-by-side comparisons).</summary>
    internal List<(Guid ObjectId, Transform Transform)> Copies { get; } = new();
    internal List<PreviewLabel> Labels { get; } = new();
    /// <summary>Geometry the scene owns; disposed when the scene is replaced or hidden.</summary>
    internal List<IDisposable> Owned { get; } = new();

    internal void DisposeOwned()
    {
        foreach (var item in Owned) item.Dispose();
        Owned.Clear();
    }
}

internal sealed record PreviewLabel(string Text, Point3d Location, Color Color, int Height = 14);

internal sealed class PreviewDrawable
{
    internal required Mesh? Mesh { get; init; }
    internal required Curve? Curve { get; init; }
    internal required Point3d? Point { get; init; }
    internal required PointCloud? Cloud { get; init; }
    internal required Color Fill { get; init; }
    internal required Color Outline { get; init; }
    internal required double Transparency { get; init; }
    internal required int OutlineWidth { get; init; }
    /// <summary>Draw a silhouette outline beneath the shaded mesh instead of its wires.</summary>
    internal bool Silhouette { get; init; }
}
