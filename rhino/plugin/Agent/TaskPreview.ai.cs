using Rhino;
using Rhino.Geometry;
using RhinoClient.Sync;

namespace RhinoClient.Agent;

// Transient display only: never inserts task objects into the source document. The result is drawn by the same
// preview as commit and merge reviews (GeometrySync.ShowTaskResult): against the snapshot the task started from,
// added green, modified blue, removed as red ghosts, with the same outlines and info card.
internal sealed class TaskPreview(uint sourceSerial) : IDisposable
{
    private string? _baselinePath;
    private Dictionary<Guid, GeometryBase>? _baseline;

    /// <param name="baselinePath">The 3dm the task started from; without it every object of the result is drawn neutral.</param>
    /// <param name="hud">Title and detail lines for the info card.</param>
    public void Show(RhinoDoc document, bool zoom, string? baselinePath, IReadOnlyList<string> hud)
    {
        var source = RhinoDoc.FromRuntimeSerialNumber(sourceSerial);
        if (source is null) return;
        GeometrySync.ShowTaskResult(source, document, LoadBaseline(baselinePath), hud, zoom);
    }

    // The snapshot is read once per task and kept while that task is shown.
    private Dictionary<Guid, GeometryBase>? LoadBaseline(string? path)
    {
        if (path is null || !File.Exists(path)) { DropBaseline(); return null; }
        if (path == _baselinePath && _baseline is not null) return _baseline;
        DropBaseline();
        using var doc = RhinoDoc.OpenHeadless(path);
        if (doc is null) return null;
        var map = new Dictionary<Guid, GeometryBase>();
        foreach (var obj in doc.Objects.Where(o => !o.IsDeleted && !o.IsHidden))
            map[obj.Id] = obj.Geometry.Duplicate();
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

    public void Clear()
    {
        GeometrySync.ClearTaskPreview();
        DropBaseline();
    }

    public void Dispose() => Clear();
}
