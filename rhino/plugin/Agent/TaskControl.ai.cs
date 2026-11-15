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
    private static readonly HashSet<string> Claimed = [];
    private readonly string? _claim;
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
        // Sessions belong to the repository the model is synced with, so they come back after Save As, a restart,
        // or for an unsaved model. A model with no repository falls back to its file, and an unsaved one to this run.
        var doc = RhinoDoc.FromRuntimeSerialNumber(serial);
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var sourcePath = doc?.Path;
        var pathIdentity = string.IsNullOrWhiteSpace(sourcePath) ? null : Hash(Path.GetFullPath(sourcePath));
        var repo = doc?.Strings.GetValue(RhinoClient.Sync.WorkingCopy.RepoKey);
        var server = (doc?.Strings.GetValue(RhinoClient.Sync.WorkingCopy.ServerKey) ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        var repoIdentity = string.IsNullOrWhiteSpace(repo) ? null : Hash("repo:" + server + "/" + repo.Trim());
        // Two open models on one repository would overwrite each other's file, so the second one keeps its own.
        var identity = repoIdentity is not null && Claimed.Add(repoIdentity) ? repoIdentity : pathIdentity ?? $"unsaved-{Environment.ProcessId}-{serial}";
        _claim = identity == repoIdentity ? repoIdentity : null;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RhinoClient", "sessions");
        _directory = Path.Combine(root, identity);
        Directory.CreateDirectory(_directory);
        // Carry history saved under the file's own folder over to the repository's folder the first time.
        if (_claim is not null && pathIdentity is not null)
        {
            var from = Path.Combine(root, pathIdentity, "tasks.json");
            var to = Path.Combine(_directory, "tasks.json");
            try { if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to); } catch (IOException) { }
        }
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
            else { State.CodexPath = CodexRunner.FindExecutable(); State.ClaudePath = ClaudeRunner.FindExecutable(); }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { State = new() { CodexPath = CodexRunner.FindExecutable(), ClaudePath = ClaudeRunner.FindExecutable() }; RhinoApp.WriteLine("RhinoClient: could not restore task history: " + ex.Message); }
        if (!ModelCatalog.IsValid(State.Model)) State.Model = ControlState.BudgetModel;
        if (string.IsNullOrWhiteSpace(State.ClaudePath)) State.ClaudePath = ClaudeRunner.FindExecutable();
        foreach (var saved in State.Sessions) if (saved.Model is not null && !ModelCatalog.IsValid(saved.Model)) saved.Model = null;
        foreach (var session in State.Sessions) session.IsPlanning = false;
        foreach (var task in State.Tasks) task.Merging = false;
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
        settings = new { codexPath = State.CodexPath, claudePath = State.ClaudePath, model = State.Model, concurrency = State.Concurrency },
        models = ModelCatalog.All(State),
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
    // The commit the open model was last pushed to or pulled from; tasks remember it so a result can be merged later.
    private string? SourceBaseCommit()
    {
        var value = Source.Strings.GetValue(RhinoClient.Sync.WorkingCopy.BaseKey);
        return string.IsNullOrEmpty(value) ? null : value;
    }
    private static string? ReadModel(JsonElement cmd)
    {
        var model = AgentJson.Text(cmd, "model").Trim();
        if (model.Length == 0) return null;
        if (!ModelCatalog.IsValid(model)) throw new InvalidOperationException("That is not a valid model id.");
        return model;
    }
    private string ModelOf(string? sessionId) =>
        (sessionId is null ? null : State.Sessions.FirstOrDefault(x => x.Id == sessionId)?.Model) ?? State.Model;
    // Runs one turn with whichever CLI serves the model. A thread started by the other provider cannot be resumed.
    private Task RunAgent(TaskEntry task, string model, string url, string prompt, CancellationToken token, IReadOnlyList<PromptImage>? images = null)
    {
        var provider = ModelCatalog.ProviderOf(model);
        if (task.WorkerProvider is not null && task.WorkerProvider != provider) task.WorkerThreadId = null;
        task.WorkerProvider = provider;
        Func<string, Task> onEvent = line => ParseEvent(task, line);
        Action<System.Diagnostics.Process> started = p => task.Process = p;
        return provider == ModelCatalog.Claude
            ? ClaudeRunner.Run(task, State, model, _directory, url, prompt, onEvent, started, token, images: images)
            : CodexRunner.Run(task, State, model, _directory, url, prompt, onEvent, started, token, images: images);
    }
    // What the tasks this one builds on found, oldest first, so a subtask starts from their resolution and not just their geometry.
    private string UpstreamContext(TaskEntry task)
    {
        var chain = new List<TaskEntry>();
        for (var parent = task.ParentId; parent is not null && chain.Count < 12;)
        {
            var upstream = State.Tasks.FirstOrDefault(t => t.Id == parent);
            if (upstream is null || chain.Contains(upstream)) break;
            chain.Insert(0, upstream); parent = upstream.ParentId;
        }
        if (chain.Count == 0) return "";
        static string Clip(string text, int limit) { text = text.Trim(); return text.Length <= limit ? text : text[..limit] + "…"; }
        var lines = chain.Select(upstream =>
        {
            var outcome = upstream.Activity.LastOrDefault(a => a.Kind == "assistant")?.Text;
            var failure = upstream.Status == "failed" ? " It did not finish: " + Clip(upstream.Error ?? "failed", 200) : "";
            return $"- {upstream.Title} ({upstream.Status}). Asked: {Clip(upstream.Instructions, 400)}\n  Resolution: {(string.IsNullOrWhiteSpace(outcome) ? "no summary was recorded." : Clip(outcome, 1500))}{failure}";
        });
        return "\nEarlier tasks this one builds on, oldest first. Their geometry is already in your document copy; use their resolutions for names, dimensions, positions and layers instead of re-deriving or duplicating them:\n" + string.Join("\n", lines) + "\n";
    }
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

    /// <summary>Checks a task can be merged and marks it as merging. Call on the UI thread.</summary>
    public bool TryBeginMerge(string id, string? resolve, out RhinoClient.Sync.TaskMergeRequest request, out string problem)
    {
        request = null!;
        var task = State.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) problem = "Task not found.";
        else if (task.Merging) problem = "This task is already being merged.";
        else if (task.MergedBranch is not null) problem = $"This task was already merged via {task.MergedBranch}.";
        else if (task.Status != "ready" || task.ResultPath is null || !File.Exists(task.ResultPath)) problem = "Only a finished task with a saved result can be merged.";
        else if (task.BaseCommit is null) problem = "This task started from a model that was not on a repository branch. Push the model from the Graph tab, then run the task again.";
        else
        {
            task.Merging = true; Publish();
            request = new(task.Id, task.Title, task.Revision, task.ResultPath, task.BaseCommit) { Resolve = resolve, Into = task.PrBase };
            problem = "";
            return true;
        }
        return false;
    }
    /// <summary>Checks a task can open a pull request and marks it as opening. Call on the UI thread.</summary>
    public bool TryBeginPullRequest(string id, out RhinoClient.Sync.TaskMergeRequest request, out string problem)
    {
        request = null!;
        var task = State.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) problem = "Task not found.";
        else if (task.Opening || task.Merging) problem = "This task is busy.";
        else if (task.PrBranch is not null) problem = "A pull request is already open for this task.";
        else if (task.MergedBranch is not null) problem = $"This task was already merged via {task.MergedBranch}.";
        else if (task.Status != "ready" || task.ResultPath is null || !File.Exists(task.ResultPath)) problem = "Only a finished task with a saved result can open a pull request.";
        else if (task.BaseCommit is null) problem = "This task started from a model that was not on a repository branch. Push the model from the Graph tab, then run the task again.";
        else
        {
            task.Opening = true; Publish();
            request = new(task.Id, task.Title, task.Revision, task.ResultPath, task.BaseCommit);
            problem = "";
            return true;
        }
        return false;
    }
    /// <summary>Records the outcome of opening a pull request. Call on the UI thread.</summary>
    public void FinishPullRequest(string id, bool ok, string message, string branch, string baseBranch)
    {
        var task = State.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;
        task.Opening = false;
        if (ok && !message.Contains("nothing to merge", StringComparison.OrdinalIgnoreCase))
        { task.PrBranch = branch; task.PrBase = baseBranch; task.PrOpenedAt = DateTimeOffset.UtcNow; }
        Log(task, ok ? "status" : "error", message);
    }
    /// <summary>Closes a task's pull request without merging. The agent branch stays in the repository.</summary>
    public void ClosePullRequest(string id)
    {
        var task = Find(id);
        if (task.PrBranch is null || task.Merging) return;
        Log(task, "status", $"Closed the pull request for {task.PrBranch} without merging.");
        task.PrBranch = null; task.PrBase = null; task.PrOpenedAt = null;
        Publish();
    }
    /// <summary>Records the outcome of a merge. Call on the UI thread.</summary>
    public void FinishMerge(string id, bool ok, string message, string branch)
    {
        var task = State.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;
        task.Merging = false;
        if (ok && task.MergedBranch is null && !message.Contains("nothing to merge", StringComparison.OrdinalIgnoreCase) && _previewTaskId == id)
        { _preview.Clear(); _previewTaskId = null; }
        if (ok && !message.Contains("nothing to merge", StringComparison.OrdinalIgnoreCase)) { task.MergedBranch = branch; task.MergedAt = DateTimeOffset.UtcNow; task.PrBranch = null; task.PrBase = null; task.PrOpenedAt = null; }
        Log(task, ok ? "status" : "error", message);
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
                State.ClaudePath = AgentJson.Text(cmd, "claudePath", "claude");
                State.Model = ReadModel(cmd) ?? ControlState.BudgetModel;
                State.Concurrency = Math.Clamp(cmd.GetProperty("concurrency").GetInt32(), 1, 4);
                Publish(); Pump(); break;
            case "scope.selection":
                var selection = Source.Objects.GetSelectedObjects(false, false).Select(x => x.Id).ToList();
                SelectionCaptured?.Invoke(selection); break;
            case "session.create":
                var initialPrompt = AgentJson.Text(cmd, "prompt").Trim();
                if (initialPrompt.Length == 0) throw new InvalidOperationException("Describe the work to start a session.");
                var session = new SessionEntry { Model = ReadModel(cmd), Id = AgentJson.Text(cmd, "id", Guid.NewGuid().ToString("N")), Title = AgentJson.Text(cmd, "title", initialPrompt[..Math.Min(70, initialPrompt.Length)]), Scope = ReadScope(cmd) };
                if (State.Sessions.Any(s => s.Id == session.Id)) throw new InvalidOperationException("Session already exists.");
                State.Sessions.Add(session);
                StartPlanning(session, initialPrompt, PromptImages.Save(cmd, _directory));
                break;
            case "session.model":
                // Applies to the planner and to tasks run from now on; finished work keeps the model that made it.
                Session(id).Model = ReadModel(cmd);
                Publish(); break;
            case "session.message":
                var existingSession = Session(id);
                if (cmd.TryGetProperty("scope", out _)) existingSession.Scope = ReadScope(cmd);
                StartPlanning(existingSession, AgentJson.Text(cmd, "prompt"), PromptImages.Save(cmd, _directory));
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
            case "task.pr.close": ClosePullRequest(id); break;
            case "task.pr.base":
                var pr = Find(id);
                var into = AgentJson.Text(cmd, "branch").Trim();
                if (pr.PrBranch is null || pr.Merging) throw new InvalidOperationException("There is no open pull request to retarget.");
                if (into.Length == 0 || into.StartsWith("agent/", StringComparison.Ordinal)) throw new InvalidOperationException("Choose a branch to merge into.");
                pr.PrBase = into; Publish(); break;
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
                    added.BaseCommit = origin.BaseCommit;
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
                var snapshotBase = snapshot is null ? null : SourceBaseCommit();
                foreach (var task in pending) { if (task.ParentId is null) { task.InputPath = snapshot; task.BaseCommit = snapshotBase; } Queue(task); }
                Pump(); break;
            case "task.stop":
                Stop(Find(id)); Pump(); break;
            case "task.message":
                var continuing = Find(id);
                if (continuing.Deleted || continuing.Archived || Session(continuing.SessionId!).Archived) throw new InvalidOperationException("Restore the task and session before continuing.");
                continuing.Paused = false;
                if (continuing.Status is not ("ready" or "stopped" or "failed" or "interrupted")) throw new InvalidOperationException("Stop the task or wait for it to finish before sending a follow-up.");
                var message = AgentJson.Text(cmd, "message");
                var attached = PromptImages.Save(cmd, _directory);
                if (string.IsNullOrWhiteSpace(message) && attached.Count == 0) break;
                if (string.IsNullOrWhiteSpace(message)) message = "See the attached image.";
                // Follow-ups reuse the task's working copy and CLI conversation.
                continuing.Instructions = message; continuing.Status = "queued"; continuing.Error = null;
                continuing.PendingImages = attached;
                Log(continuing, "user", message + PromptImages.Note(attached)); Pump(); break;
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
        // A merged result is real model geometry now, so show the model itself instead of a preview overlay.
        if (task.MergedBranch is not null) { _preview.Clear(); _previewTaskId = null; return; }
        if (task.Document is not null) _preview.Show(task.Document, true, task.InputPath);
        else
        {
            var path = task.ResultPath ?? task.WorkingPath;
            if (path is null || !File.Exists(path)) { _previewTaskId = task.Id; _preview.Clear(); return; }
            using var doc = RhinoDoc.OpenHeadless(path) ?? throw new IOException("Could not open task preview.");
            _preview.Show(doc, true, task.InputPath);
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
            task.BaseCommit = SourceBaseCommit();
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
                task.BaseCommit = upstream.BaseCommit;
            }
            task.Status = "running"; task.Error = null; task.StartedAt = DateTimeOffset.UtcNow; task.FinishedAt = null;
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
    private void StartPlanning(SessionEntry session, string request, IReadOnlyList<PromptImage> images)
    {
        if (session.Archived) throw new InvalidOperationException("Restore this session to continue the conversation.");
        if (session.IsPlanning) throw new InvalidOperationException("This session is already preparing tasks.");
        if (string.IsNullOrWhiteSpace(request) && images.Count == 0) throw new InvalidOperationException("Enter a message first.");
        var planner = new TaskEntry { Title = "Session planner", SessionId = session.Id, WorkerThreadId = session.PlannerThreadId, WorkerProvider = session.PlannerProvider, Cancellation = new(), Scope = ScopeCopy(session.Scope), Document = Source };
        _planners[session.Id] = planner;
        session.IsPlanning = true;
        Log(planner, "user", request + PromptImages.Note(images));
        _ = RunPlanner(planner, session, request, images);
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
            if (task.ParentId is not null && State.Tasks.FirstOrDefault(t => t.Id == task.ParentId) is { Status: "ready" } upstream) { task.InputPath = upstream.ResultPath; task.BaseCommit = upstream.BaseCommit; }
            keys.Add(key, task.Id); tasks.Add(task);
        }
        State.Tasks.AddRange(tasks); Publish();
        return new { created = tasks.Select(t => new { t.Id, t.Title }).ToArray(), message = "Tasks are ready for human review within this session. No workers have launched." };
    }
    private async Task RunPlanner(TaskEntry planner, SessionEntry session, string request, IReadOnlyList<PromptImage> images)
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
            prompt += $"\nPropose tasks first. Default to calling propose_plan right away on the initial request; do not open with questions. Where the request leaves details open (dimensions, spacing, bay counts), choose sensible, modest values, write them into the task instructions as explicit assumptions, and mention them briefly in your summary so the user can correct them. Ask a question only when you genuinely cannot make a reasonable plan without the answer, and then ask at most one or two short questions in a single message. A count of units or bays is not a spacing in document units; never turn an ambiguous count into thousands of objects. Include explicit dimensions, spacing and estimated object count in geometry task instructions. Each worker turn permits at most {WorkerBudget.MaxToolCalls} tool calls; split larger work only when its scale is explicit.";
            await RunAgent(planner, ModelOf(session.Id), mcp.Url, prompt, planner.Cancellation.Token, images);
        }
        catch (OperationCanceledException) { await RhinoThread.Run(() => { Log(planner, "status", "Session planning stopped."); return true; }); }
        catch (Exception ex)
        {
            planner.Cancellation?.Cancel();
            await RhinoThread.Run(() => { Log(planner, "error", ex.Message); return true; });
        }
        finally
        {
            await RhinoThread.Run(() =>
            {
                session.IsPlanning = false; session.PlannerThreadId = planner.WorkerThreadId; session.PlannerProvider = planner.WorkerProvider;
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
            using var mcp = new TaskMcp(task, false, _ => throw new InvalidOperationException(), (k, t) => Log(task, k, t), () => { WriteCopy(task.Document!, task.WorkingPath!); if (_previewTaskId == task.Id) _preview.Show(task.Document!, false, task.InputPath); }, token);
            var prompt = "You are a Rhino geometry worker. Use only the rhino MCP tools to inspect and edit your assigned document copy. Begin with get_context and list_objects. Scope is enforced by the tools. Never use scripts, shell, external MCP servers, or ActiveDoc. Coordinates use document units. Only polyline/box/sphere creation, transforms and deletion are available. If your request requires unsupported operations or scope expansion, explain that limitation rather than claiming success. Do the requested geometry work, then summarize actual changes and limitations. The shared model is unchanged and PR acceptance is deferred.\nTask: " + task.Title + "\n" + task.Instructions;
            prompt += UpstreamContext(task);
            prompt += $"\nBefore editing, estimate the number of objects and tool calls. Your turn is limited to {WorkerBudget.MaxToolCalls} tool calls and stops after {WorkerBudget.MaxConsecutiveErrors} consecutive tool errors. If dimensions or counts are ambiguous, or the requested work exceeds the limit, explain and request clarification or a smaller task before creating geometry. Do not repeatedly retry invalid geometry or unsupported operations.";
            var turnImages = task.PendingImages; task.PendingImages = [];
            await RunAgent(task, ModelOf(task.SessionId), mcp.Url, prompt, token, turnImages);
            await RhinoThread.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                WriteCopy(task.Document!, task.WorkingPath!);
                var result = Path.Combine(_directory, $"result-{task.Id}-{++task.Revision}.3dm");
                File.Copy(task.WorkingPath!, result);
                task.ResultPath = result; task.Status = "ready";
                if (_previewTaskId == task.Id) _preview.Show(task.Document!, true, task.InputPath);
                Log(task, "status", "Ready for review. Open a pull request to commit this result to its own branch, then merge it into the model's branch.");
                return true;
            });
        }
        catch (OperationCanceledException) { await RhinoThread.Run(() => { task.Status = task.Error is null ? "stopped" : "failed"; Log(task, "status", "Stopped. Saved geometry and activity are retained."); return true; }); }
        catch (Exception ex)
        {
            // Revoke already accepted MCP calls before closing the task document.
            task.Cancellation?.Cancel();
            await RhinoThread.Run(() => { task.Status = "failed"; task.Error = ex.Message; Log(task, "error", ex.Message); return true; });
        }
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
        if (task.Cancellation?.IsCancellationRequested is not false) return false;
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
        if (_claim is not null) Claimed.Remove(_claim);
        foreach (var planner in _planners.Values) planner.Cancellation?.Cancel();
        foreach (var task in State.Tasks)
        {
            if (task.Status is "running" or "stopping" or "queued") task.Status = "interrupted";
            task.Cancellation?.Cancel();
        }
        Publish();
    }
}
