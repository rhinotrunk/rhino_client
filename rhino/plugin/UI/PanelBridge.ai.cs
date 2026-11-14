using System.Text.Json;
using Eto.Forms;
using Rhino;

namespace RhinoClient.UI;

internal sealed class PanelBridge : IDisposable
{
    private readonly WebView _view;
    private readonly Action<JsonElement> _receive;
    private readonly Queue<string> _pending = new();
    private bool _loaded;
    private bool _disposed;

    public PanelBridge(WebView view, Action<JsonElement> receive)
    {
        _view = view;
        _receive = receive;
        view.MessageReceived += OnMessage;
        view.DocumentLoading += OnLoading;
        view.DocumentLoaded += OnLoaded;
    }

    private void OnMessage(object? sender, WebViewMessageEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.Message);
            if (message.RootElement.ValueKind == JsonValueKind.Object)
                _receive(message.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            RhinoApp.WriteLine($"RhinoClient: invalid panel message ({ex.Message}).");
        }
    }

    private void OnLoading(object? sender, WebViewLoadingEventArgs e)
    {
        if (!e.IsMainFrame) return;
        _loaded = false;
        _pending.Clear();
    }

    private void OnLoaded(object? sender, WebViewLoadedEventArgs e)
    {
        _loaded = true;
        while (_pending.TryDequeue(out var json)) _ = DeliverAsync(json);
    }

    public void Post(object message)
    {
        if (_disposed) return;
        var json = JsonSerializer.Serialize(message);
        if (!_loaded) _pending.Enqueue(json);
        else _ = DeliverAsync(json);
    }

    private async Task DeliverAsync(string json)
    {
        try
        {
            await _view.ExecuteScriptAsync($"window.rhinoClient?.receive({json});");
        }
        catch (Exception ex)
        {
            if (!_disposed) RhinoApp.WriteLine($"RhinoClient: panel delivery failed ({ex.Message}).");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _view.MessageReceived -= OnMessage;
        _view.DocumentLoading -= OnLoading;
        _view.DocumentLoaded -= OnLoaded;
        _pending.Clear();
    }
}
