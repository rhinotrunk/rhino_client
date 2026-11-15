using System.Text.Json;
using RhinoClient.Agent;
using RhinoClient.UI;

if (args.FirstOrDefault() == "exec") { await WorkerTests.FakeWorker(); return; }
await WorkerTests.Run();
MergeTests.Run();

var view = new Eto.Forms.WebView();
using var bridge = new PanelBridge(view, _ => { });
view.Load();
var session = new SessionEntry { Id = "session-1", Title = "Facade study" };
session.Conversation.Add(new("assistant", "Four tasks proposed.", DateTimeOffset.UtcNow));
var task = new TaskEntry { Id = "task-1", SessionId = session.Id, Title = "Create grid", Scope = new() { WholeDocument = true } };
bridge.Post(new { type = "control.state", sessions = new[] { session }, tasks = new[] { task } });
var script = view.LastScript ?? throw new Exception("No state delivered.");
var start = script.IndexOf('(') + 1;
using var state = JsonDocument.Parse(script[start..script.LastIndexOf(')')]);
var root = state.RootElement;
var receivedSession = root.GetProperty("sessions")[0];
if (receivedSession.GetProperty("id").GetString() != "session-1" || receivedSession.GetProperty("title").GetString() != "Facade study") throw new Exception("Session wire names do not match TypeScript.");
if (receivedSession.GetProperty("conversation")[0].GetProperty("kind").GetString() != "assistant") throw new Exception("Activity wire names do not match TypeScript.");
var receivedTask = root.GetProperty("tasks")[0];
if (receivedTask.GetProperty("sessionId").GetString() != "session-1" || !receivedTask.GetProperty("scope").GetProperty("wholeDocument").GetBoolean()) throw new Exception("Nested task data is not camelCase.");
if (receivedSession.TryGetProperty("Id", out _)) throw new Exception("PascalCase leaked onto the wire.");
Console.WriteLine("PASS: production bridge serializes sessions, tasks, scope and activity with TypeScript-compatible property names.");

// Host doubles keep this wire-contract regression executable without Rhino.
namespace Eto.Forms
{
    public sealed class WebView
    {
        public event EventHandler<WebViewMessageEventArgs>? MessageReceived;
        public event EventHandler<WebViewLoadingEventArgs>? DocumentLoading;
        public event EventHandler<WebViewLoadedEventArgs>? DocumentLoaded;
        public string? LastScript { get; private set; }
        public Task ExecuteScriptAsync(string script) { LastScript = script; return Task.CompletedTask; }
        public void Load() { DocumentLoading?.Invoke(this, new()); DocumentLoaded?.Invoke(this, new()); }
        public void Receive(string message) => MessageReceived?.Invoke(this, new() { Message = message });
    }
    public sealed class WebViewMessageEventArgs : EventArgs { public string Message { get; init; } = ""; }
    public sealed class WebViewLoadingEventArgs : EventArgs { public bool IsMainFrame => true; }
    public sealed class WebViewLoadedEventArgs : EventArgs { }
}
namespace Rhino
{
    public sealed class RhinoDoc { }
    public static class RhinoApp
    {
        public static void WriteLine(string message) => Console.WriteLine(message);
        public static void InvokeOnUiThread(Action action) => action();
    }
}
