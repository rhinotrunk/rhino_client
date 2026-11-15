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
        // Draw2dText heights are device pixels, so scale them or the text is tiny on high-DPI screens.
        var scale = Math.Max(1.0, e.Display.DpiScale);
        foreach (var label in labelled.Labels)
            e.Display.Draw2dText(label.Text, label.Color, label.Location, true, (int)Math.Round(label.Height * scale), Font);
        if (Scene is { Hud.Count: > 0 } scene) DrawHud(e.Display, scene.Hud, scale);
    }

    // =========================================================================================
    // HUD: a card in the panel's style (its card colour, border, radius, type and muted text)
    // =========================================================================================

    private const char SwatchStart = '\u0001';
    private const char SwatchEnd = '\u0002';

    /// <summary>A coloured dot inside a HUD line, drawn where this marker sits (like the legend dots in the panel).</summary>
    internal static string Swatch(Color color) => $"{SwatchStart}{color.R:X2}{color.G:X2}{color.B:X2}{SwatchEnd}";

    private static string Font => OperatingSystem.IsMacOS() ? "Helvetica Neue" : "Segoe UI";

    /// <summary>The panel's tokens (panel.css :root and .dark), picked by Rhino's own light or dark theme.</summary>
    private static (Color Card, Color Border, Color Text, Color Muted) Theme() => Rhino.Runtime.HostUtils.RunningInDarkMode
        ? (Color.FromArgb(48, 48, 48), Color.FromArgb(82, 82, 82), Color.FromArgb(237, 237, 237), Color.FromArgb(179, 179, 179))
        : (Color.FromArgb(255, 255, 255), Color.FromArgb(204, 204, 204), Color.FromArgb(38, 38, 38), Color.FromArgb(102, 102, 102));

    private static void DrawHud(DisplayPipeline display, IReadOnlyList<string> lines, double scale)
    {
        int Px(double value) => (int)Math.Round(value * scale);
        var theme = Theme();
        var margin = Px(12);
        var padX = Px(14);
        var padY = Px(11);
        var gap = Px(5);
        var dot = Px(8);
        var dotGap = Px(6);
        var spanGap = Px(14);

        // Title like the panel's commit subject; the rest like its secondary text.
        var rows = lines.Select((line, i) => (Spans: Parse(line), Height: Px(i == 0 ? 14 : 12), Color: i == 0 ? theme.Text : theme.Muted)).ToList();
        var lineHeights = rows.Select(row => (int)Math.Round(row.Height * 1.35)).ToList();
        var widths = rows.Select(row => row.Spans.Sum(span =>
            (span.Swatch is null ? 0 : dot + dotGap + (span.First ? 0 : spanGap)) + Measure(display, span.Text, row.Height).Width)).ToList();
        var width = (widths.Count == 0 ? 0 : widths.Max()) + padX * 2;
        var height = lineHeights.Sum() + gap * (rows.Count - 1) + padY * 2;

        FillRounded(display, margin, margin, width, height, Px(8), theme.Border);
        FillRounded(display, margin + 1, margin + 1, width - 2, height - 2, Px(8) - 1, theme.Card);

        var y = margin + padY;
        for (var i = 0; i < rows.Count; i++)
        {
            var x = margin + padX;
            var row = rows[i];
            foreach (var span in row.Spans)
            {
                if (span.Swatch is { } swatch)
                {
                    if (!span.First) x += spanGap;
                    FillRounded(display, x, y + (lineHeights[i] - dot) / 2, dot, dot, dot / 2.0, swatch);
                    x += dot + dotGap;
                }

                if (span.Text.Length == 0) continue;
                var box = Measure(display, span.Text, row.Height);
                // Draw2dText places text by its own baseline rules; align the measured box to the line instead.
                display.Draw2dText(span.Text, row.Color, new Point2d(x - box.Left, y + (lineHeights[i] - box.Height) / 2 - box.Top), false, row.Height, Font);
                x += box.Width;
            }

            y += lineHeights[i] + gap;
        }
    }

    private static Rectangle Measure(DisplayPipeline display, string text, int height) =>
        text.Length == 0 ? Rectangle.Empty : display.Measure2dText(text, new Point2d(0, 0), false, 0, height, Font);

    /// <summary>Splits a HUD line at swatch markers: each span is the text after one dot (or the text before the first).</summary>
    private static List<(Color? Swatch, string Text, bool First)> Parse(string line)
    {
        var spans = new List<(Color?, string, bool)>();
        var rest = line;
        Color? swatch = null;
        while (true)
        {
            var start = rest.IndexOf(SwatchStart);
            var end = start < 0 ? -1 : rest.IndexOf(SwatchEnd, start);
            if (start < 0 || end < 0)
            {
                spans.Add((swatch, rest.Trim(), spans.Count == 0));
                return spans;
            }

            var text = rest[..start].Trim();
            if (text.Length > 0 || swatch is not null) spans.Add((swatch, text, spans.Count == 0));
            swatch = int.TryParse(rest[(start + 1)..end], System.Globalization.NumberStyles.HexNumber, null, out var rgb)
                ? Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255)
                : null;
            rest = rest[(end + 1)..];
        }
    }

    /// <summary>A filled rectangle with rounded corners, drawn one pixel row at a time (the 2D pipeline has no rounded shapes).</summary>
    private static void FillRounded(DisplayPipeline display, int x, int y, int width, int height, double radius, Color color)
    {
        if (width <= 0 || height <= 0) return;
        radius = Math.Min(radius, Math.Min(width, height) / 2.0);
        for (var row = 0; row < height; row++)
        {
            var fromEdge = Math.Min(row + 0.5, height - row - 0.5);
            var inset = fromEdge >= radius ? 0 : radius - Math.Sqrt(Math.Max(0, radius * radius - (radius - fromEdge) * (radius - fromEdge)));
            var left = x + (float)inset;
            var right = x + width - (float)inset;
            display.Draw2dLine(new PointF(left, y + row + 0.5f), new PointF(right, y + row + 0.5f), color, 1f);
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
    /// <summary>Who put the scene up (for example an agent task preview), so it can be cleared without touching others.</summary>
    internal string? Tag { get; init; }
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
