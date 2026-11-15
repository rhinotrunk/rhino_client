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
        Scene = scene;
        Enabled = true;
    }

    internal void Hide()
    {
        Enabled = false;
        Scene = null;
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
        for (var pass = 0; pass < 2; pass++)
        {
            var opaquePass = pass == 0;
            foreach (var item in scene.Items)
            {
                if ((item.Transparency <= 0.001) != opaquePass) continue;
                if (item.Mesh is { Vertices.Count: > 0 } mesh)
                {
                    display.DrawMeshWires(mesh, item.Outline, item.OutlineWidth);
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
        if (Scene is not { Hud.Count: > 0 } scene) return;
        var y = 28;
        for (var i = 0; i < scene.Hud.Count; i++)
        {
            e.Display.Draw2dText(scene.Hud[i], Color.FromArgb(25, 25, 25), new Point2d(18, y), false, i == 0 ? 15 : 13);
            y += i == 0 ? 20 : 17;
        }
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
}

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
}
