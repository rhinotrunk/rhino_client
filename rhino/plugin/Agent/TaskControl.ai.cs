using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Rhino;
using Rhino.FileIO;

namespace RhinoClient.Agent;

// Owns state independently of the WebView. All state/document mutations are on the UI thread.
internal sealed class TaskControl : IDisposable
{
    private static readonly Dictionary<uint, TaskControl> Controls = [];
    public static TaskControl For(uint serial)
    {
        if (serial == 0) serial = RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0;
        if (!Controls.TryGetValue(serial, out var control)) Controls[serial] = control = new(serial);
        return control;
    }
    public static void Shutdown() { foreach (var c in Controls.Values) c.Dispose(); Controls.Clear(); }
    private readonly uint _sourceSerial;
    private readonly string _directory;
    private readonly string _stateFile;
    private readonly TaskPreview _preview;
    private string? _previewTaskId;
    private bool _disposed;
    private readonly Dictionary<string, TaskEntry> _planners = [];
    public ControlState State { get; private set; } = new();
    public event Action? Changed;
    private RhinoDoc Source => RhinoDoc.FromRuntimeSerialNumber(_sourceSerial) ?? throw new InvalidOperationException("The source document has closed.");

    private TaskControl(uint serial)
    {
        _sourceSerial = serial;
        _preview = new TaskPreview(serial);
        // Saved documents retain their task history across Rhino restarts. Unsaved models
        // get a process-scoped identity so unrelated untitled documents never share tasks.
        var sourcePath = RhinoDoc.FromRuntimeSerialNumber(serial)?.Path;
        var identity = string.IsNullOrWhiteSpace(sourcePath) ? $"unsaved-{Environment.ProcessId}-{serial}"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath))));
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RhinoClient", "sessions", identity);
        Directory.CreateDirectory(_directory);
        _stateFile = Path.Combine(_directory, "tasks.json");
        try
        {
            if (File.Exists(_stateFile))
            {
                var saved = JsonNode.Parse(File.ReadAllText(_stateFile))?.AsObject() ?? new JsonObject();
                // Older builds stored CLI thread IDs under task.sessionId and had one global plan.
                if (saved["sessions"] is null)
                {
                    var importedId = Guid.NewGuid().ToString("N");
                    foreach (var task in saved["tasks"]?.AsArray() ?? new JsonArray())
                    {
                        if (task is null) continue;
                        task["workerThreadId"] = task["sessionId"]?.DeepClone();
                        task["sessionId"] = importedId;
                    }
                    var imported = new SessionEntry { Id = importedId, Title = "Previous modeling session" };
                    if (saved["planning"] is JsonArray conversation)
                        imported.Conversation = conversation.Deserialize<List<ActivityEntry>>(AgentJson.Options) ?? [];
                    saved["sessions"] = JsonSerializer.SerializeToNode(new[] { imported }, AgentJson.Options);
                }
                State = saved.Deserialize<ControlState>(AgentJson.Options) ?? new();
            }
            else State.CodexPath = CodexRunner.FindExecutable();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { State = new() { CodexPath = CodexRunner.FindExecutable() }; RhinoApp.WriteLine("RhinoClient: could not restore task history: " + ex.Message); }
        if (State.Model is not ("gpt-6-luna" or "gpt-6.1-terra")) State.Model = ControlState.BudgetModel;
        foreach (var session in State.Sessions) session.IsPlanning = false;
        foreach (var task in State.Tasks)
            if (task.Status is "running" or "stopping" or "queued")
            { task.Status = "interrupted"; task.Activity.Add(new("status", "Rhino restarted. Inspect the saved copy before continuing; no geometry operations were replayed.", DateTimeOffset.UtcNow)); }
        RhinoDoc.CloseDocument += OnClose;
    }
    private void OnClose(object? sender, DocumentEventArgs e)
    { if (e.DocumentSerialNumber == _sourceSerial) { Dispose(); Controls.Remove(_sourceSerial); } }
    public object Snapshot() => new
    {
        type = "control.state", tasks = State.Tasks, sessions = State.Sessions, previewTaskId = _previewTaskId,
        settings = new { codexPath = State.CodexPath, model = State.Model, concurrency = State.Concurrency },
        document = RhinoDoc.FromRuntimeSerialNumber(_sourceSerial)?.Name ?? "Untitled",
        layers = RhinoDoc.FromRuntimeSerialNumber(_sourceSerial)?.Layers.Where(x => !x.IsDeleted).Select(x => new LayerEntry(x.Id, x.FullPath)).ToArray() ?? [],
        storagePath = _directory,
    };
    private void Publish()
    {
        var temp = _stateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(State, AgentJson.Options));
        File.Move(temp, _stateFile, true);
        Changed?.Invoke();
    }
    private void Log(TaskEntry task, string kind, string text)
    {
        var entry = new ActivityEntry(kind, text, DateTimeOffset.UtcNow);
        var list = task.SessionId is not null && _planners.TryGetValue(task.SessionId, out var planner) && ReferenceEquals(planner, task) ? Session(task.SessionId).Conversation : task.Activity;
        list.Add(entry);
        File.AppendAllText(Path.Combine(_directory, "activity.jsonl"), JsonSerializer.Serialize(new { sessionId = task.SessionId, taskId = task.Id, entry }, AgentJson.Options) + "\n");
        Publish();
    }
    private SessionEntry Session(string id) => State.Sessions.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("Session not found.");
    private TaskEntry Find(string id) => State.Tasks.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Task not found.");
    private static GeometryScope ScopeCopy(GeometryScope scope) => new() { WholeDocument = scope.WholeDocument, ObjectIds = [.. scope.ObjectIds], LayerIds = [.. scope.LayerIds] };
    private GeometryScope ReadScope(JsonElement cmd)
    {
        var scope = cmd.TryGetProperty("scope", out var value) ? value.Deserialize<GeometryScope>(AgentJson.Options) ?? new() : new GeometryScope();
        scope.WholeDocument = scope.ObjectIds.Count + scope.LayerIds.Count == 0;
        return scope;
    }
    private static void WriteCopy(RhinoDoc doc, string path)
    {
        using var options = new FileWriteOptions { SuppressDialogBoxes = true, SuppressAllInput = true, UpdateDocumentPath = false, WriteSelectedObjectsOnly = false };
        if (!doc.Write3dmFile(path, options)) throw new IOException("Could not save the document copy.");
    }

    public async Task Command(JsonElement cmd)
    {
        if (_disposed) throw new InvalidOperationException("Task workspace has closed.");
        var type = AgentJson.Text(cmd, "type");
        var id = AgentJson.Text(cmd, "id");
        switch (type)
        {
            case "ready": Publish(); break;
            case "panel.error":
                var diagnostic = AgentJson.Text(cmd, "message");
                File.AppendAllText(Path.Combine(_directory, "panel-errors.log"), $"{DateTimeOffset.UtcNow:O} {diagnostic}\n");
                RhinoApp.WriteLine("RhinoClient panel: " + diagnostic);
                break;
            case "settings.save":
                State.CodexPath = AgentJson.Text(cmd, "codexPath", "codex");
                var selectedModel = AgentJson.Text(cmd, "model", ControlState.BudgetModel);
                if (selectedModel is not ("gpt-6-luna" or "gpt-6.1-terra")) throw new InvalidOperationException("Choose a supported budget model: GPT-6 Luna or GPT-6.1 Terra.");
                State.Model = selectedModel;
                State.Concurrency = Math.Clamp(cmd.GetProperty("concurrency").GetInt32(), 1, 4);
                Publish(); Pump(); break;
            case "scope.selection":
                var selection = Source.Objects.GetSelectedObjects(false, false).Select(x => x.Id).ToList();
                SelectionCaptured?.Invoke(selection); break;
            case "session.create":
                var initialPrompt = AgentJson.Text(cmd, "prompt").Trim();
                if (initialPrompt.Length == 0) throw new InvalidOperationException("Describe the work to start a session.");
                var session = new SessionEntry { Id = AgentJson.Text(cmd, "id", Guid.NewGuid().ToString("N")), Title = AgentJson.Text(cmd, "title", initialPrompt[..Math.Min(70, initialPrompt.Length)]), Scope = ReadScope(cmd) };
                if (State.Sessions.Any(s => s.Id == session.Id)) throw new InvalidOperationException("Session already exists.");
                State.Sessions.Add(session);
                StartPlanning(session, initialPrompt);
                break;
            case "session.message":
                var existingSession = Session(id);
                if (cmd.TryGetProperty("scope", out _)) existingSession.Scope = ReadScope(cmd);
                StartPlanning(existingSession, AgentJson.Text(cmd, "prompt"));
                break;
            case "session.stop":
                var stoppedSession = Session(id);
                if (_planners.TryGetValue(id, out var planner)) planner.Cancellation?.Cancel();
                foreach (var t in State.Tasks.Where(t => t.SessionId == stoppedSession.Id)) Stop(t);
                Publish(); Pump();
                break;
            case "session.archive":
            case "session.restore":
                var archiveSession = Session(id);
                archiveSession.Archived = type == "session.archive";
                if (archiveSession.Archived)
                {
                    if (_planners.TryGetValue(id, out var activePlanner)) activePlanner.Cancellation?.Cancel();
                    foreach (var member in State.Tasks.Where(t => t.SessionId == id)) Stop(member);
                }
                Publish(); Pump(); break;
            case "task.pause":
                var held = Find(id); held.Paused = true; Stop(held); Publish(); Pump(); break;
            case "task.resume":
                var resumed = Find(id);
                if (resumed.Status == "stopping") throw new InvalidOperationException("Wait for the worker to finish stopping before resuming.");
                resumed.Paused = false;
                if (resumed.Status is "stopped" or "interrupted" or "failed")
                {
                    resumed.Instructions = "Inspect the saved geometry and continue the remaining work without duplicating completed operations. Original request: " + resumed.Instructions;
                    resumed.Status = "queued";
                }
                Publish(); Pump(); break;
            case "task.archive":
            case "task.restore":
            case "task.remove":
                var affected = new HashSet<string> { Find(id).Id };
                while (true)
                {
                    var count = affected.Count;
                    foreach (var child in State.Tasks.Where(t => t.ParentId is not null && affected.Contains(t.ParentId))) affected.Add(child.Id);
                    if (count == affected.Count) break;
                }
                foreach (var member in State.Tasks.Where(t => affected.Contains(t.Id)))
                {
                    if (type == "task.remove") member.Deleted = true;
                    else member.Archived = type == "task.archive";
                    if (type != "task.restore") Stop(member);
                }
                if (_previewTaskId is not null && affected.Contains(_previewTaskId)) { _preview.Clear(); _previewTaskId = null; }
                Publish(); Pump(); break;
            case "task.add":
                var scope = ReadScope(cmd);
                var parent = AgentJson.Text(cmd, "parentId");
                var taskSession = Session(AgentJson.Text(cmd, "sessionId"));
                if (parent.Length > 0 && Find(parent).SessionId != taskSession.Id) throw new InvalidOperationException("Dependencies must belong to the same session.");
                var added = new TaskEntry { SessionId = taskSession.Id, Id = AgentJson.Text(cmd, "id", Guid.NewGuid().ToString("N")), Title = AgentJson.Text(cmd, "title", "New task"), Instructions = AgentJson.Text(cmd, "instructions"), ParentId = parent.Length == 0 ? null : parent, Scope = scope };
                if (cmd.TryGetProperty("branchFromResult", out var branch) && branch.GetBoolean())
                {
                    var origin = Find(parent);
                    if (origin.Status != "ready" || origin.ResultPath is null) throw new InvalidOperationException("Branching requires a completed task result.");
                    added.InputPath = origin.ResultPath;
                }
                if (State.Tasks.Any(t => t.Id == added.Id)) throw new InvalidOperationException("Task already exists.");
                State.Tasks.Add(added); Publish();
                if (cmd.TryGetProperty("launch", out var launch) && launch.GetBoolean()) { Queue(added); Pump(); }
                break;
            case "task.update":
                var draft = Find(id);
                if (draft.Status != "draft") throw new InvalidOperationException("Only draft tasks can be edited. Branch a result to explore a revision.");
                draft.Title = AgentJson.Text(cmd, "title", draft.Title);
                draft.Instructions = AgentJson.Text(cmd, "instructions", draft.Instructions);
                var editedScope = ReadScope(cmd); draft.Scope = editedScope;
                Publish(); break;
            case "task.launch": Queue(Find(id)); Pump(); break;
            case "tasks.launch":
                // Pin all newly launched root tasks to the same snapshot.
                var launchSession = Session(AgentJson.Text(cmd, "sessionId"));
                if (launchSession.Archived) throw new InvalidOperationException("Restore the session before launching tasks.");
                var pending = State.Tasks.Where(x => x.Status == "draft" && x.SessionId == launchSession.Id && !x.Paused && !x.Archived && !x.Deleted).ToArray();
                foreach (var task in pending) { if (string.IsNullOrWhiteSpace(task.Instructions)) throw new InvalidOperationException("Each task needs instructions."); }
                string? snapshot = null;
                if (pending.Any(x => x.ParentId is null)) { snapshot = Path.Combine(_directory, $"main-{Guid.NewGuid():N}.3dm"); WriteCopy(Source, snapshot); }
                foreach (var task in pending) { if (task.ParentId is null) task.InputPath = snapshot; Queue(task); }
                Pump(); break;
            case "task.stop":
                Stop(Find(id)); Pump(); break;
            case "task.message":
                var continuing = Find(id);
                if (continuing.Deleted || continuing.Archived || Session(continuing.SessionId!).Archived) throw new InvalidOperationException("Restore the task and session before continuing.");
                continuing.Paused = false;
                if (continuing.Status is not ("ready" or "stopped" or "failed" or "interrupted")) throw new InvalidOperationException("Stop the task or wait for it to finish before sending a follow-up.");
                var message = AgentJson.Text(cmd, "message");
                if (string.IsNullOrWhiteSpace(message)) break;
                // Follow-ups reuse the task's working copy and CLI conversation.
                continuing.Instructions = message; continuing.Status = "queued"; continuing.Error = null;
                Log(continuing, "user", message); Pump(); break;
            case "preview.clear":
                _previewTaskId = null; _preview.Clear(); Publish(); break;
            case "task.show":
                ShowTask(Find(id)); Publish(); break;
            case "task.preview":
                var preview = Find(id);
                if (preview.Status is "running" or "stopping" or "queued") throw new InvalidOperationException("Wait for the task to stop before inspecting its document copy.");
                var path = preview.ResultPath ?? preview.WorkingPath;
                if (path is null || !File.Exists(path)) throw new InvalidOperationException("This task has no saved document yet.");
                // Open a disposable preview, so manual changes cannot alter a pinned dependency.
                var previewPath = Path.Combine(_directory, $"preview-{Guid.NewGuid():N}.3dm");
                File.Copy(path, previewPath);
                if (OperatingSystem.IsMacOS()) RhinoDoc.Open(previewPath, out _);
                else
                {
                    var executable = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Could not locate Rhino.");
                    var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
                    start.ArgumentList.Add("/nosplash"); start.ArgumentList.Add(previewPath);
                    System.Diagnostics.Process.Start(start);
                }
                break;
            default: throw new InvalidOperationException("Unknown panel command.");
        }
        await Task.CompletedTask;
    }
    private void ShowTask(TaskEntry task)
    {
        if (task.Document is not null) _preview.Show(task.Document, true);
        else
        {
            var path = task.ResultPath ?? task.WorkingPath;
            if (path is null || !File.Exists(path)) { _previewTaskId = task.Id; _preview.Clear(); return; }
            using var doc = RhinoDoc.OpenHeadless(path) ?? throw new IOException("Could not open task preview.");
            _preview.Show(doc, true);
        }
        _previewTaskId = task.Id;
    }
    public event Action<List<Guid>>? SelectionCaptured;
    private void Queue(TaskEntry task)
    {
        if (task.Deleted || task.Archived || task.Paused || Session(task.SessionId!).Archived) throw new InvalidOperationException("Restore or resume this task before running it.");
        if (task.Status != "draft") return;
        if (string.IsNullOrWhiteSpace(task.Instructions)) throw new InvalidOperationException("Each task needs instructions.");
        if (task.ParentId is null && task.InputPath is null)
        {
            var snapshot = Path.Combine(_directory, $"main-{Guid.NewGuid():N}.3dm");
            WriteCopy(Source, snapshot);
            task.InputPath = snapshot;
        }
        task.Status = "queued";
        Log(task, "status", task.ParentId is null ? "Queued from a snapshot of the current model." : "Queued. Waiting for the upstream result if needed.");
    }
    private void Pump()
    {
        if (_disposed) return;
        var slots = State.Concurrency - State.Tasks.Count(x => x.Status is "running" or "stopping");
        foreach (var task in State.Tasks.Where(x => x.Status == "queued" && !x.Paused && !x.Archived && !x.Deleted && !Session(x.SessionId!).Archived).ToArray())
        {
            if (slots <= 0) break;
            if (task.ParentId is not null && task.WorkingPath is null && task.InputPath is null)
            {
                var upstream = Find(task.ParentId);
                if (upstream.Paused || upstream.Archived || upstream.Deleted || upstream.Status != "ready" || upstream.ResultPath is null) continue;
                task.InputPath = upstream.ResultPath;
            }
            task.Status = "running"; task.StartedAt = DateTimeOffset.UtcNow; task.FinishedAt = null;
            task.Cancellation = new(); slots--;
            _ = RunTask(task);
        }
        Publish();
    }
    private void Stop(TaskEntry task)
    {
        if (task.Status == "queued") { task.Status = "stopped"; Log(task, "status", "Stopped before launch."); }
        else if (task.Status == "running") { task.Status = "stopping"; task.Cancellation?.Cancel(); Log(task, "status", "Stopping. An in-flight geometry operation may finish."); }
    }
    private void StartPlanning(SessionEntry session, string request)
    {
        if (session.Archived) throw new InvalidOperationException("Restore this session to continue the conversation.");
        if (session.IsPlanning) throw new InvalidOperationException("This session is already preparing tasks.");
        if (string.IsNullOrWhiteSpace(request)) throw new InvalidOperationException("Enter a message first.");
        var planner = new TaskEntry { Title = "Session planner", SessionId = session.Id, WorkerThreadId = session.PlannerThreadId, Cancellation = new(), Scope = ScopeCopy(session.Scope), Document = Source };
        _planners[session.Id] = planner;
        session.IsPlanning = true;
        Log(planner, "user", request);
        _ = RunPlanner(planner, session, request);
    }
    private object Propose(SessionEntry session, JsonElement arguments)
    {
        if (session.Archived) throw new InvalidOperationException("Session is archived.");
        var list = arguments.GetProperty("tasks").EnumerateArray().ToArray();
        if (list.Length is < 1 or > 24) throw new InvalidOperationException("Plans must contain 1–24 tasks.");
        var keys = State.Tasks.Where(t => t.SessionId == session.Id && !t.Deleted && !t.Archived).ToDictionary(t => t.Id, t => t.Id);
        var tasks = new List<TaskEntry>();
        foreach (var item in list)
        {
            var key = AgentJson.Text(item, "key");
            var parent = AgentJson.Text(item, "parentKey");
            if (key.Length == 0 || keys.ContainsKey(key)) throw new InvalidOperationException("Task keys must be unique and nonempty.");
            if (parent.Length > 0 && !keys.ContainsKey(parent)) throw new InvalidOperationException("Parents must precede children; cycles are not allowed.");
            var task = new TaskEntry { SessionId = session.Id, Title = AgentJson.Text(item, "title"), Instructions = AgentJson.Text(item, "instructions"), ParentId = parent.Length > 0 ? keys[parent] : null, Scope = ScopeCopy(session.Scope) };
            if (string.IsNullOrWhiteSpace(task.Title) || string.IsNullOrWhiteSpace(task.Instructions)) throw new InvalidOperationException("Every task needs a title and instructions.");
            if (task.ParentId is not null && State.Tasks.FirstOrDefault(t => t.Id == task.ParentId) is { Status: "ready" } upstream) task.InputPath = upstream.ResultPath;
            keys.Add(key, task.Id); tasks.Add(task);
        }
        State.Tasks.AddRange(tasks); Publish();
        return new { created = tasks.Select(t => new { t.Id, t.Title }).ToArray(), message = "Tasks are ready for human review within this session. No workers have launched." };
    }
    private async Task RunPlanner(TaskEntry planner, SessionEntry session, string request)
    {
        try
        {
            var proposed = false;
            using var mcp = new TaskMcp(planner, true, args =>
            {
                if (proposed) throw new InvalidOperationException("Tasks have already been proposed for this turn. Summarize them for review.");
                var result = Propose(session, args); proposed = true; return result;
            }, (k, t) => Log(planner, k, t), () => { }, planner.Cancellation!.Token);
            var existing = State.Tasks.Where(t => t.SessionId == session.Id && !t.Deleted && !t.Archived).Select(t => new { t.Id, t.Title, t.Status, t.ParentId, t.Paused, summary = t.Activity.LastOrDefault(a => a.Kind == "assistant")?.Text });
            var prompt = "You coordinate a Rhino modeling session. Planning is the beginning of this session, not a separate user workflow. Inspect model context before planning. For an initial request, call propose_plan once with the smallest useful set of concrete tasks: a simple request needs only one task. Shared prerequisites are parents. Every requested design option MUST be its own sibling task and its own document copy. Never bundle several options in a task such as Develop-three-versions. A grid plus three facade options requires four tasks: one grid parent and three facade children. Do not execute geometry. For follow-up discussion, answer questions or propose additional tasks only when requested. Existing task IDs may be used as parentKey. Do not duplicate existing tasks. The human must launch reviewed tasks; acceptance is deferred. Available worker operations: polyline/box/sphere creation, move, rotate, scale, copy, delete. No scripts or arbitrary commands.\nExisting tasks: " + JsonSerializer.Serialize(existing, AgentJson.Options) + "\nUser: " + request;
            await CodexRunner.Run(planner, State, _directory, mcp.Url, prompt, line => ParseEvent(planner, line), p => planner.Process = p, planner.Cancellation.Token);
        }
        catch (OperationCanceledException) { await RhinoThread.Run(() => { Log(planner, "status", "Session planning stopped."); return true; }); }
        catch (Exception ex) { await RhinoThread.Run(() => { Log(planner, "error", ex.Message); return true; }); }
        finally
        {
            await RhinoThread.Run(() =>
            {
                session.IsPlanning = false; session.PlannerThreadId = planner.WorkerThreadId;
                session.InputTokens += planner.InputTokens; session.OutputTokens += planner.OutputTokens;
                planner.Process = null; planner.Cancellation?.Dispose(); _planners.Remove(session.Id);
                Publish(); return true;
            });
        }
    }
    private async Task RunTask(TaskEntry task)
    {
        var token = task.Cancellation!.Token;
        try
        {
            await RhinoThread.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (task.WorkingPath is null)
                {
                    task.WorkingPath = Path.Combine(_directory, $"task-{task.Id}.3dm");
                    File.Copy(task.InputPath ?? throw new InvalidOperationException("Missing input snapshot."), task.WorkingPath, true);
                }
                task.Document = RhinoDoc.OpenHeadless(task.WorkingPath) ?? throw new InvalidOperationException("Rhino could not open the task copy.");
                Log(task, "status", "Opened isolated document copy. Read-only context is available outside the editable scope.");
                if (task.Activity.LastOrDefault(x => x.Kind == "user")?.Text != task.Instructions) Log(task, "user", task.Instructions);
                return true;
            });
            using var mcp = new TaskMcp(task, false, _ => throw new InvalidOperationException(), (k, t) => Log(task, k, t), () => { WriteCopy(task.Document!, task.WorkingPath!); if (_previewTaskId == task.Id) _preview.Show(task.Document!, false); }, token);
            var prompt = "You are a Rhino geometry worker. Use only the rhino MCP tools to inspect and edit your assigned document copy. Begin with get_context and list_objects. Scope is enforced by the tools. Never use scripts, shell, external MCP servers, or ActiveDoc. Coordinates use document units. Only polyline/box/sphere creation, transforms and deletion are available. If your request requires unsupported operations or scope expansion, explain that limitation rather than claiming success. Do the requested geometry work, then summarize actual changes and limitations. The shared model is unchanged and PR acceptance is deferred.\nTask: " + task.Title + "\n" + task.Instructions;
            await CodexRunner.Run(task, State, _directory, mcp.Url, prompt, line => ParseEvent(task, line), p => task.Process = p, token);
            await RhinoThread.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                WriteCopy(task.Document!, task.WorkingPath!);
                var result = Path.Combine(_directory, $"result-{task.Id}-{++task.Revision}.3dm");
                File.Copy(task.WorkingPath!, result);
                task.ResultPath = result; task.Status = "ready";
                if (_previewTaskId == task.Id) _preview.Show(task.Document!, true);
                Log(task, "status", "PR ready (local proposal). Acceptance and Git integration are deferred.");
                return true;
            });
        }
        catch (OperationCanceledException) { await RhinoThread.Run(() => { task.Status = "stopped"; Log(task, "status", "Stopped. Saved geometry and activity are retained."); return true; }); }
        catch (Exception ex) { await RhinoThread.Run(() => { task.Status = "failed"; task.Error = ex.Message; Log(task, "error", ex.Message); return true; }); }
        finally
        {
            await RhinoThread.Run(() =>
            {
                task.Process = null; task.Document?.Dispose(); task.Document = null;
                task.FinishedAt = DateTimeOffset.UtcNow; task.Cancellation?.Dispose(); task.Cancellation = null;
                Publish(); Pump(); return true;
            });
        }
    }
    private Task ParseEvent(TaskEntry task, string line) => RhinoThread.Run(() =>
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var type = AgentJson.Text(root, "type");
            if (type == "thread.started") task.WorkerThreadId = AgentJson.Text(root, "thread_id");
            if (type == "item.completed" && root.TryGetProperty("item", out var item))
            {
                var kind = AgentJson.Text(item, "type");
                if (kind == "agent_message") Log(task, "assistant", AgentJson.Text(item, "text"));
                if (kind == "error") throw new InvalidOperationException(AgentJson.Text(item, "message"));
            }
            if (type == "turn.failed" || type == "error") throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.ToString() : AgentJson.Text(root, "message", "Agent turn failed."));
            if (type == "turn.completed" && root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var input)) task.InputTokens += input.GetInt64();
                if (usage.TryGetProperty("output_tokens", out var output)) task.OutputTokens += output.GetInt64();
                Publish();
            }
        }
        catch (JsonException) { /* Ignore non-JSON startup output from CLI wrappers. */ }
        return true;
    });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _preview.Dispose(); RhinoDoc.CloseDocument -= OnClose;
        foreach (var planner in _planners.Values) planner.Cancellation?.Cancel();
        foreach (var task in State.Tasks)
        {
            if (task.Status is "running" or "stopping" or "queued") task.Status = "interrupted";
            task.Cancellation?.Cancel();
        }
        Publish();
    }
}
