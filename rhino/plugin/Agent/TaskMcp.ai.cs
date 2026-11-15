using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoClient.Agent;

// A stateless, loopback-only MCP endpoint with a per-worker capability URL.
// All document access and scope checks happen on Rhino's UI thread.
internal sealed class TaskMcp : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly TaskEntry _task;
    private readonly bool _planner;
    private readonly Func<JsonElement, object> _plan;
    private readonly Action<string, string> _log;
    private readonly Action _save;
    private readonly CancellationToken _ct;
    public string Url { get; }

    public TaskMcp(TaskEntry task, bool planner, Func<JsonElement, object> plan,
        Action<string, string> log, Action save, CancellationToken ct)
    {
        _task = task; _planner = planner; _plan = plan; _log = log; _save = save; _ct = ct;
        // Retry the ephemeral-port bind if another listener wins the port race.
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/{Guid.NewGuid():N}/";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(Url);
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 4) { }
        }
        _ = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                _ = Serve(context);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
    }

    private async Task Serve(HttpListenerContext ctx)
    {
        try
        {
            var origin = ctx.Request.Headers["Origin"];
            if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !originUri.IsLoopback))
            { ctx.Response.StatusCode = 403; return; }
            if (ctx.Request.HttpMethod != "POST") { ctx.Response.StatusCode = 405; return; }
            if (ctx.Request.ContentLength64 > 1024 * 1024) { ctx.Response.StatusCode = 413; return; }
            using var reader = new StreamReader(ctx.Request.InputStream);
            var buffer = new char[1024 * 1024 + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(count));
                if (read == 0) break;
                count += read;
            }
            if (count == buffer.Length) { ctx.Response.StatusCode = 413; return; }
            using var json = JsonDocument.Parse(new string(buffer, 0, count));
            var root = json.RootElement;
            if (!root.TryGetProperty("id", out var id)) { ctx.Response.StatusCode = 202; return; }
            object response;
            var method = AgentJson.Text(root, "method");
            if (method == "initialize")
                response = new { jsonrpc = "2.0", id = id.Clone(), result = new
                {
                    protocolVersion = "2025-03-26", capabilities = new { tools = new { } },
                    serverInfo = new { name = "RhinoClient scoped task", version = "0.1.0" },
                } };
            else if (method == "ping") response = new { jsonrpc = "2.0", id = id.Clone(), result = new { } };
            else if (method == "tools/list") response = new { jsonrpc = "2.0", id = id.Clone(), result = new { tools = Tools(_planner) } };
            else if (method == "tools/call")
            {
                var p = root.GetProperty("params");
                var name = AgentJson.Text(p, "name");
                var arguments = p.TryGetProperty("arguments", out var a) ? a.Clone() : JsonSerializer.SerializeToElement(new { });
                try
                {
                    var result = await RhinoThread.Run(() =>
                    {
                        _ct.ThrowIfCancellationRequested();
                        _log("tool", $"{name}\n{arguments.GetRawText()}");
                        if (_planner && name is not ("propose_plan" or "get_context" or "list_objects" or "get_object")) throw new InvalidOperationException("Planner cannot edit geometry.");
                        var output = _planner && name == "propose_plan" ? _plan(arguments) : Execute(name, arguments);
                        _log("result", JsonSerializer.Serialize(output, AgentJson.Options));
                        return output;
                    });
                    response = new { jsonrpc = "2.0", id = id.Clone(), result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, AgentJson.Options) } }, isError = false } };
                }
                catch (Exception ex)
                {
                    await RhinoThread.Run(() => { _log("error", ex.Message); return true; });
                    response = new { jsonrpc = "2.0", id = id.Clone(), result = new { content = new[] { new { type = "text", text = ex.Message } }, isError = true } };
                }
            }
            else response = new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Unknown method" } };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, AgentJson.Options));
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException or HttpListenerException or ObjectDisposedException)
        { try { ctx.Response.StatusCode = 400; } catch (ObjectDisposedException) { } }
        finally { try { ctx.Response.Close(); } catch (ObjectDisposedException) { } }
    }

    private static object Tool(string name, string description, string schema) => new
    { name, description, inputSchema = JsonSerializer.Deserialize<JsonElement>(schema) };

    internal static object[] Tools(bool planner) => planner ?
    [Tool("propose_plan", "Create a reviewable modeling task tree. No workers start until the human launches tasks. Parents must appear before children. Use the requested editable scope; include concrete geometry instructions for each task.", """
    {"type":"object","properties":{"tasks":{"type":"array","minItems":1,"maxItems":24,"items":{"type":"object","properties":{"key":{"type":"string"},"parentKey":{"type":["string","null"]},"title":{"type":"string"},"instructions":{"type":"string"}},"required":["key","title","instructions"],"additionalProperties":false}}},"required":["tasks"],"additionalProperties":false}
    """) , .. Tools(false).Take(3)]
    : [
        Tool("get_context", "Inspect this task's document, units, layers and authorized object IDs. All other objects are read-only. Geometry coordinates use document units.", """{"type":"object","properties":{},"additionalProperties":false}"""),
        Tool("list_objects", "List geometry and bounds, including read-only context. Paginate with offset/limit.", """{"type":"object","properties":{"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":200}},"additionalProperties":false}"""),
        Tool("get_object", "Inspect an object's geometry JSON, attributes, bounds and editability.", """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}"""),
        Tool("create_geometry", "Add a polyline, box or sphere on an authorized layer. With whole-document scope, layerId may be omitted to use the current layer. Polyline takes points [[x,y,z],...]; box takes min and max [x,y,z]; sphere takes center and radius. Can create grids and facade panels through repeated calls. Only named typed primitives are supported; no scripts.", """
        {"type":"object","properties":{"kind":{"type":"string","enum":["polyline","box","sphere"]},"layerId":{"type":"string"},"name":{"type":"string"},"points":{"type":"array","items":{"type":"array","items":{"type":"number"}}},"min":{"type":"array","items":{"type":"number"}},"max":{"type":"array","items":{"type":"number"}},"center":{"type":"array","items":{"type":"number"}},"radius":{"type":"number"}},"required":["kind"],"additionalProperties":false}
        """),
        Tool("transform_object", "Move, rotate around world Z, or uniformly scale an authorized object; optionally copy it. For move provide vector; rotate provide center and degrees; scale provide center and factor.", """
        {"type":"object","properties":{"id":{"type":"string"},"operation":{"type":"string","enum":["move","rotate","scale"]},"vector":{"type":"array","items":{"type":"number"}},"center":{"type":"array","items":{"type":"number"}},"degrees":{"type":"number"},"factor":{"type":"number"},"copy":{"type":"boolean"}},"required":["id","operation"],"additionalProperties":false}
        """),
        Tool("delete_object", "Delete an authorized object from this task copy only.", """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""")
    ];

    private bool Editable(RhinoObject obj) => _task.Scope.WholeDocument || _task.Scope.ObjectIds.Contains(obj.Id) ||
        _task.Scope.LayerIds.Contains(_task.Document!.Layers[obj.Attributes.LayerIndex].Id);

    private object Describe(RhinoObject obj) => new
    {
        id = obj.Id, name = obj.Name, kind = obj.Geometry.ObjectType.ToString(),
        layerId = _task.Document!.Layers[obj.Attributes.LayerIndex].Id, editable = Editable(obj),
        bounds = Bounds(obj.Geometry.GetBoundingBox(true)),
    };
    private static object Bounds(BoundingBox box) => box.IsValid ? new { min = new[] { box.Min.X, box.Min.Y, box.Min.Z }, max = new[] { box.Max.X, box.Max.Y, box.Max.Z } } : (object)new { };
    private static Point3d Point(JsonElement a, string key)
    {
        var values = a.GetProperty(key).EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (values.Length != 3 || values.Any(x => !double.IsFinite(x) || Math.Abs(x) > 1e9)) throw new InvalidOperationException($"{key} must contain three finite coordinates within ±1e9.");
        return new Point3d(values[0], values[1], values[2]);
    }
    private static double Number(JsonElement a, string key)
    {
        var v = a.GetProperty(key).GetDouble();
        if (!double.IsFinite(v) || Math.Abs(v) > 1e9) throw new InvalidOperationException("Invalid numeric value.");
        return v;
    }

    private object Execute(string name, JsonElement a)
    {
        var doc = _task.Document ?? throw new InvalidOperationException("Task document is not open.");
        if (name == "get_context") return new
        {
            task = _task.Title, units = doc.ModelUnitSystem.ToString(), tolerance = doc.ModelAbsoluteTolerance,
            scope = _task.Scope,
            layers = doc.Layers.Where(l => !l.IsDeleted).Select(l => new { id = l.Id, name = l.FullPath, editable = _task.Scope.WholeDocument || _task.Scope.LayerIds.Contains(l.Id) }).ToArray(),
        };
        if (name == "list_objects")
        {
            var offset = a.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            var limit = a.TryGetProperty("limit", out var l) ? l.GetInt32() : 100;
            return new { objects = doc.Objects.Where(x => !x.IsDeleted).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200)).Select(Describe).ToArray() };
        }
        if (name == "create_geometry")
        {
            var layerText = AgentJson.Text(a, "layerId");
            var layerId = layerText.Length == 0 && _task.Scope.WholeDocument ? doc.Layers[doc.Layers.CurrentLayerIndex].Id : Guid.Parse(layerText);
            if (!_task.Scope.WholeDocument && !_task.Scope.LayerIds.Contains(layerId)) throw new InvalidOperationException("Creation requires an explicitly editable layer. Ask the user to revise the scope.");
            var layer = doc.Layers.FirstOrDefault(x => x.Id == layerId && !x.IsDeleted) ?? throw new InvalidOperationException("Layer does not exist.");
            GeometryBase geometry;
            switch (AgentJson.Text(a, "kind"))
            {
                case "polyline":
                    var points = a.GetProperty("points").EnumerateArray().ToArray();
                    if (points.Length is < 2 or > 2048) throw new InvalidOperationException("Provide 2–2048 points.");
                    var parsed = points.Select(p => Point(JsonSerializer.SerializeToElement(new { point = p }), "point"));
                    geometry = new PolylineCurve(parsed);
                    break;
                case "box":
                    var min = Point(a, "min"); var max = Point(a, "max");
                    if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z) throw new InvalidOperationException("Box max must exceed min on every axis.");
                    geometry = new BoundingBox(min, max).ToBrep();
                    break;
                case "sphere":
                    var radius = Number(a, "radius");
                    if (radius <= 0) throw new InvalidOperationException("Radius must be positive.");
                    geometry = new Sphere(Point(a, "center"), radius).ToBrep();
                    break;
                default: throw new InvalidOperationException("Unsupported primitive.");
            }
            using (geometry)
            {
                if (!geometry.IsValid) throw new InvalidOperationException("Invalid geometry.");
                var id = doc.Objects.Add(geometry, new ObjectAttributes { LayerIndex = layer.Index, Name = AgentJson.Text(a, "name") });
                if (id == Guid.Empty) throw new InvalidOperationException("Rhino refused to add the geometry.");
                _task.Scope.ObjectIds.Add(id); _save(); return new { id };
            }
        }
        var objectId = Guid.Parse(AgentJson.Text(a, "id"));
        var obj = doc.Objects.FindId(objectId) ?? throw new InvalidOperationException("Object not found.");
        if (name == "get_object") return new { summary = Describe(obj), geometry = obj.Geometry.ToJSON(new Rhino.FileIO.SerializationOptions()) };
        if (!Editable(obj)) throw new InvalidOperationException("Object is outside the task's editable scope.");
        if (name == "delete_object")
        {
            if (!doc.Objects.Delete(obj, true)) throw new InvalidOperationException("Rhino refused to delete the object.");
            _save(); return new { deleted = objectId };
        }
        if (name == "transform_object")
        {
            Transform transform;
            switch (AgentJson.Text(a, "operation"))
            {
                case "move": var v = Point(a, "vector"); transform = Transform.Translation(v.X, v.Y, v.Z); break;
                case "rotate": transform = Transform.Rotation(Number(a, "degrees") * Math.PI / 180, Vector3d.ZAxis, Point(a, "center")); break;
                case "scale":
                    var factor = Number(a, "factor");
                    if (factor <= 0) throw new InvalidOperationException("Scale factor must be positive.");
                    transform = Transform.Scale(Point(a, "center"), factor); break;
                default: throw new InvalidOperationException("Unsupported transform.");
            }
            var copy = a.TryGetProperty("copy", out var c) && c.GetBoolean();
            var id = doc.Objects.Transform(objectId, transform, !copy);
            if (id == Guid.Empty) throw new InvalidOperationException("Rhino refused the transform.");
            _task.Scope.ObjectIds.Add(id); _save(); return new { id };
        }
        throw new InvalidOperationException("Unknown tool.");
    }

    public void Dispose() { _listener.Close(); }
}
