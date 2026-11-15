using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoClient.Agent;

// Transient display only: never inserts task objects into the source document.
// Objects are colored by what the task did to the snapshot it started from:
// green = added, red = removed, amber = changed, grey = untouched.
internal sealed class TaskPreview(uint sourceSerial) : DisplayConduit, IDisposable
{
    private enum Change { Unchanged, Added, Changed, Removed }
    private sealed record Item(GeometryBase Geometry, Change Change);

    private static readonly Color Neutral = Color.FromArgb(85, 155, 190);
    private static readonly Color Grey = Color.FromArgb(150, 150, 150);
    private static readonly Color Green = Color.FromArgb(60, 200, 110);
    private static readonly Color Amber = Color.FromArgb(240, 170, 50);
    private static readonly Color Red = Color.FromArgb(230, 70, 70);

    private readonly List<Item> _items = [];
    private BoundingBox _bounds = BoundingBox.Empty;
    private bool _compared;
    private string? _baselinePath;
    private Dictionary<Guid, GeometryBase>? _baseline;

    private bool ForSource(DrawEventArgs e) => e.RhinoDoc?.RuntimeSerialNumber == sourceSerial;

    /// <param name="baselinePath">The 3dm the task started from; without it every object is drawn in one neutral color.</param>
    public void Show(RhinoDoc document, bool zoom, string? baselinePath = null)
    {
        DisposeItems();
        var baseline = LoadBaseline(baselinePath);
        _compared = baseline is not null;
        var live = document.Objects.Where(o => !o.IsDeleted).ToList();
        var seen = new HashSet<Guid>(live.Select(o => o.Id));
        foreach (var obj in live.Where(o => !o.IsHidden))
        {
            var geometry = Normalize(obj.Geometry.Duplicate());
            var change = baseline is null ? Change.Unchanged
                : !baseline.TryGetValue(obj.Id, out var before) ? Change.Added
                : GeometryBase.GeometryEquals(before, geometry) ? Change.Unchanged
                : Change.Changed;
            Add(geometry, change);
        }

        if (baseline is not null)
            foreach (var pair in baseline.Where(pair => !seen.Contains(pair.Key)))
                Add(pair.Value.Duplicate(), Change.Removed);

        Enabled = true;
        var source = RhinoDoc.FromRuntimeSerialNumber(sourceSerial);
        if (zoom && _bounds.IsValid) source?.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(_bounds);
        source?.Views.Redraw();
    }

    private void Add(GeometryBase geometry, Change change)
    {
        _items.Add(new Item(geometry, change));
        _bounds.Union(geometry.GetBoundingBox(true));
    }

    // Extrusions and bare surfaces are drawn as breps so they get edges and a fill like any solid.
    private static GeometryBase Normalize(GeometryBase geometry)
    {
        Brep? brep = geometry switch
        {
            Extrusion extrusion => extrusion.ToBrep(),
            Brep => null,
            Surface surface => surface.ToBrep(),
            _ => null,
        };
        if (brep is null) return geometry;
        geometry.Dispose();
        return brep;
    }

    private Dictionary<Guid, GeometryBase>? LoadBaseline(string? path)
    {
        if (path is null || !File.Exists(path)) { DropBaseline(); return null; }
        if (path == _baselinePath && _baseline is not null) return _baseline;
        DropBaseline();
        using var doc = RhinoDoc.OpenHeadless(path);
        if (doc is null) return null;
        var map = new Dictionary<Guid, GeometryBase>();
        foreach (var obj in doc.Objects.Where(o => !o.IsDeleted && !o.IsHidden))
            map[obj.Id] = Normalize(obj.Geometry.Duplicate());
        _baselinePath = path;
        _baseline = map;
        return map;
    }

    private void DropBaseline()
    {
        if (_baseline is not null) foreach (var geometry in _baseline.Values) geometry.Dispose();
        _baseline = null;
        _baselinePath = null;
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    { if (ForSource(e) && _bounds.IsValid) e.IncludeBoundingBox(_bounds); }
    protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e) => CalculateBoundingBox(e);
    protected override void PreDrawObject(DrawObjectEventArgs e)
    { if (ForSource(e)) e.DrawObject = false; }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (!ForSource(e)) return;
        foreach (var item in _items) Draw(e.Display, item);
        if (!_compared) return;
        var y = 48; // below the viewport title tab
        foreach (var (label, color) in new[] { ("Added", Green), ("Changed", Amber), ("Removed", Red), ("Unchanged", Grey) })
        {
            e.Display.Draw2dText(label, color, new Point2d(14, y), false, 14);
            y += 18;
        }
    }

    private void Draw(DisplayPipeline display, Item item)
    {
        var color = !_compared ? Neutral : item.Change switch
        {
            Change.Added => Green,
            Change.Changed => Amber,
            Change.Removed => Red,
            _ => Grey,
        };
        var emphasised = _compared && item.Change != Change.Unchanged;
        var weight = emphasised ? 3 : 1;
        switch (item.Geometry)
        {
            case Curve curve: display.DrawCurve(curve, color, emphasised ? 3 : 2); break;
            case Brep brep: display.DrawBrepWires(brep, color, weight); break;
            case Mesh mesh: display.DrawMeshWires(mesh, color); break;
            case Rhino.Geometry.Point point: display.DrawPoint(point.Location, color); break;
            default: display.DrawBox(item.Geometry.GetBoundingBox(true), color); break;
        }
    }

    private void DisposeItems()
    {
        foreach (var item in _items) item.Geometry.Dispose();
        _items.Clear();
        _bounds = BoundingBox.Empty;
    }

    public void Clear()
    {
        Enabled = false;
        DisposeItems();
        DropBaseline();
        RhinoDoc.FromRuntimeSerialNumber(sourceSerial)?.Views.Redraw();
    }
    public void Dispose() => Clear();
}
