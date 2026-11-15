using System.Text.Json;

namespace RhinoClient.Agent;

internal sealed class GeometryScope
{
    public bool WholeDocument { get; set; }
    public List<Guid> ObjectIds { get; set; } = [];
    public List<Guid> LayerIds { get; set; } = [];
}

internal sealed class TaskEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New task";
    public string Instructions { get; set; } = "";
    public string? ParentId { get; set; }
    public string Status { get; set; } = "draft";
    public bool Archived { get; set; }
    public bool Deleted { get; set; }
    public bool Paused { get; set; }
    public GeometryScope Scope { get; set; } = new();
    public string? InputPath { get; set; }
    public string? WorkingPath { get; set; }
    public string? ResultPath { get; set; }
    /// <summary>Commit the task's input snapshot was taken at; null when the model was not synced with a repository.</summary>
    public string? BaseCommit { get; set; }
    public bool Merging { get; set; }
    /// <summary>True while a pull request is being opened.</summary>
    public bool Opening { get; set; }
    /// <summary>Agent branch of the open pull request, or null when none is open.</summary>
    public string? PrBranch { get; set; }
    /// <summary>Branch the pull request will merge into.</summary>
    public string? PrBase { get; set; }
    public DateTimeOffset? PrOpenedAt { get; set; }
    public string? MergedBranch { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public string? SessionId { get; set; }
    public string? WorkerThreadId { get; set; }
    public string? Model { get; set; }
    public int Revision { get; set; }
    public string? Error { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<ActivityEntry> Activity { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource? Cancellation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public System.Diagnostics.Process? Process { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Rhino.RhinoDoc? Document { get; set; }
}

internal sealed record ActivityEntry(string Kind, string Text, DateTimeOffset At);
internal sealed record LayerEntry(Guid Id, string Name);
internal sealed class SessionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New session";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public GeometryScope Scope { get; set; } = new() { WholeDocument = true };
    public List<ActivityEntry> Conversation { get; set; } = [];
    public bool IsPlanning { get; set; }
    public bool Archived { get; set; }
    public string? PlannerThreadId { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

internal sealed class ControlState
{
    public List<TaskEntry> Tasks { get; set; } = [];
    public List<SessionEntry> Sessions { get; set; } = [];
    public string CodexPath { get; set; } = "codex";
    public const string BudgetModel = "gpt-6-luna";
    public string Model { get; set; } = BudgetModel;
    public int Concurrency { get; set; } = 2;
}

internal static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static string Text(JsonElement value, string name, string fallback = "") =>
        value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;
}

internal static class RhinoThread
{
    public static Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        }));
        return completion.Task;
    }
}
