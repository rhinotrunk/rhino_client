using System.Runtime.InteropServices;
using System.Text.Json;
using Eto.Forms;
using Rhino;
using Rhino.Runtime;
using Rhino.UI;
using RhinoClient.Sync;

namespace RhinoClient.UI;

[Guid("d94f345a-aebb-46fb-bac5-bfce679a3943")]
public sealed class ClientPanel : Panel
{
    public static Guid PanelId => typeof(ClientPanel).GUID;

    private readonly PanelBridge _bridge;
    private readonly uint _documentSerialNumber;
    private readonly GeometrySync _sync;
    private readonly CancellationTokenSource _unload = new();
    private bool _busy;
    private bool _closed;
    private bool _disposed;
    private string _busyMessage = "Working…";

    public uint DocumentSerialNumber => _documentSerialNumber;

    public ClientPanel() : this(0) { }

    public ClientPanel(uint documentSerialNumber)
    {
        _documentSerialNumber = documentSerialNumber;
        _sync = new GeometrySync(documentSerialNumber);
        var view = new WebView();
        _bridge = new PanelBridge(view, OnCommand);
        Content = view;
        Padding = 4;

#if DEBUG
        if (Environment.GetEnvironmentVariable("RHINOCLIENT_DEV") == "1")
        {
            view.Url = new Uri("http://127.0.0.1:5173/panel.host.html");
            return;
        }
#endif

        using var stream = typeof(ClientPanel).Assembly.GetManifestResourceStream("RhinoClient.panel.html")
            ?? throw new InvalidOperationException("The embedded panel is missing. Rebuild RhinoClient.");
        using var reader = new StreamReader(stream);
        view.LoadHtml(reader.ReadToEnd());
    }

    private void OnCommand(JsonElement command)
    {
        var type = command.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString()
            : null;
        switch (type)
        {
            case "ready":
                SendTheme();
                SendSession();
                SendStatus(_busy ? _busyMessage : "Connected to Rhino", false, _busy);
                break;
            case "commits":
                _ = SendCommits(new SyncTarget(Text(command, "server"), Text(command, "repo"), Text(command, "branch")));
                break;
            case "pull":
                _ = Pull(new SyncTarget(
                    Text(command, "server"), Text(command, "repo"), Text(command, "branch"), NullIfEmpty(Text(command, "commit"))));
                break;
            case "push":
                _ = Push(
                    new SyncTarget(Text(command, "server"), Text(command, "repo"), Text(command, "branch")),
                    Text(command, "message"));
                break;
        }
    }

    internal SyncTarget ReadTarget() => _sync.ReadTarget();

    internal Task<IReadOnlyList<CommitChoice>> ListCommits(SyncTarget target) =>
        _sync.ListCommitsAsync(target, _unload.Token);

    internal Task<SyncResult> Pull(SyncTarget target) => RunAsync(true, target, "");

    internal Task<SyncResult> Push(SyncTarget target, string message) => RunAsync(false, target, message);

    private async Task<SyncResult> RunAsync(bool pull, SyncTarget target, string message)
    {
        if (_closed) return SyncResult.Fail("The panel has closed.");
        if (_busy)
        {
            var busy = SyncResult.Fail("A pull or push is already running.");
            RhinoApp.WriteLine("RhinoClient: " + busy.Message);
            return busy;
        }

        _busy = true;
        _busyMessage = pull ? "Pulling…" : "Pushing…";
        SendStatus(_busyMessage, false, true);
        try
        {
            var result = pull
                ? await _sync.PullAsync(target, _unload.Token)
                : await _sync.PushAsync(target, message, _unload.Token);
            _busy = false;
            SendStatus(result.Message, !result.Ok, false);
            return result;
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            if (!_closed) SendStatus("Cancelled.", true, false);
            return SyncResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            _busy = false;
            SendStatus(ex.Message, true, false);
            return SyncResult.Fail(ex.Message);
        }
    }

    private async Task SendCommits(SyncTarget target)
    {
        if (_closed || _busy) return;
        try
        {
            var commits = await _sync.ListCommitsAsync(target, _unload.Token);
            if (_closed) return;
            Post(new
            {
                type = "commits",
                commits = commits.Select(commit => new { id = commit.Id, message = commit.Message }),
                error = false,
                message = "",
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SyncException or RhinoTrunk.Client.RhinoTrunkApiException or HttpRequestException)
        {
            if (_closed) return;
            Post(new { type = "commits", commits = Array.Empty<CommitChoice>(), error = true, message = ex.Message });
        }
    }

    private void Post(object message)
    {
        if (_closed) return;
        RhinoApp.InvokeOnUiThread(() =>
        {
            if (_closed) return;
            _bridge.Post(message);
        });
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void SendSession()
    {
        try
        {
            var target = _sync.ReadTarget();
            _bridge.Post(new { type = "session", server = target.Server, repo = target.Repo, branch = target.Branch });
        }
        catch (SyncException ex)
        {
            SendStatus(ex.Message, true, false);
        }
    }

    private void SendStatus(string message, bool error, bool busy)
    {
        if (_closed) return;
        RhinoApp.InvokeOnUiThread(() =>
        {
            if (_closed) return;
            _bridge.Post(new { type = "status", message, error, busy });
        });
    }

    private static string Text(JsonElement command, string name) =>
        command.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private void SendTheme() => _bridge.Post(new
    {
        type = "theme",
        scheme = HostUtils.RunningInDarkMode ? "dark" : "light",
    });

    private void OnThemeChanged(object? sender, EventArgs e) => SendTheme();

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ThemeSettings.ThemeChanged += OnThemeChanged;
    }

    protected override void OnUnLoad(EventArgs e)
    {
        StopCalls();
        ThemeSettings.ThemeChanged -= OnThemeChanged;
        base.OnUnLoad(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            StopCalls();
            ThemeSettings.ThemeChanged -= OnThemeChanged;
            _bridge.Dispose();
            _sync.Dispose();
            _unload.Dispose();
        }
        base.Dispose(disposing);
    }

    private void StopCalls()
    {
        if (_closed) return;
        _closed = true;
        _unload.Cancel();
    }
}
