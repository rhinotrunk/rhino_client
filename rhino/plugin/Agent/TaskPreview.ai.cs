using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoClient.Agent;

// Transient display only: never inserts task objects into the source document.
internal sealed class TaskPreview(uint sourceSerial) : DisplayConduit, IDisposable
{
    private readonly List<GeometryBase> _geometry = [];
    private BoundingBox _bounds = BoundingBox.Empty;
    private bool ForSource(DrawEventArgs e) => e.RhinoDoc?.RuntimeSerialNumber == sourceSerial;
    public void Show(RhinoDoc document, bool zoom)
    {
        foreach (var g in _geometry) g.Dispose();
        _geometry.Clear(); _bounds = BoundingBox.Empty;
        foreach (var obj in document.Objects.Where(o => !o.IsDeleted && !o.IsHidden))
        {
            var copy = obj.Geometry.Duplicate();
            _geometry.Add(copy); _bounds.Union(copy.GetBoundingBox(true));
        }
        Enabled = true;
        var source = RhinoDoc.FromRuntimeSerialNumber(sourceSerial);
        if (zoom && _bounds.IsValid) source?.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(_bounds);
        source?.Views.Redraw();
    }
    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    { if (ForSource(e) && _bounds.IsValid) e.IncludeBoundingBox(_bounds); }
    protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e) => CalculateBoundingBox(e);
    protected override void PreDrawObject(DrawObjectEventArgs e)
    { if (ForSource(e)) e.DrawObject = false; }
    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (!ForSource(e)) return;
        var color = Color.FromArgb(85, 155, 190);
        foreach (var geometry in _geometry)
            switch (geometry)
            {
                case Curve curve: e.Display.DrawCurve(curve, color, 2); break;
                case Brep brep: e.Display.DrawBrepWires(brep, color, 1); break;
                case Mesh mesh: e.Display.DrawMeshWires(mesh, color); break;
                case Rhino.Geometry.Point point: e.Display.DrawPoint(point.Location, color); break;
                default: e.Display.DrawBox(geometry.GetBoundingBox(true), Color.Gray); break;
            }
    }
    public void Clear()
    {
        Enabled = false;
        foreach (var g in _geometry) g.Dispose();
        _geometry.Clear(); _bounds = BoundingBox.Empty;
        RhinoDoc.FromRuntimeSerialNumber(sourceSerial)?.Views.Redraw();
    }
    public void Dispose() => Clear();
}
