using System.Runtime.InteropServices;
using System.Text.Json;
using RhinoClient.Agent;
using Eto.Forms;
using Rhino;
using Rhino.Runtime;
using Rhino.UI;

namespace RhinoClient.UI;

[Guid("d94f345a-aebb-46fb-bac5-bfce679a3943")]
public sealed class ClientPanel : Panel
{
    public static Guid PanelId => typeof(ClientPanel).GUID;

    private readonly PanelBridge _bridge;
    private readonly TaskControl _control;

    public ClientPanel() : this(0) { }

    public ClientPanel(uint documentSerialNumber)
    {
        _control = TaskControl.For(documentSerialNumber);
        _control.Changed += SendState;
        _control.SelectionCaptured += SendSelection;
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

    private async void OnCommand(JsonElement command)
    {
        try
        {
            if (AgentJson.Text(command, "type") == "ready") SendTheme();
            await _control.Command(command);
        }
        catch (Exception ex)
        {
            _bridge.Post(new { type = "control.error", message = ex.Message });
        }
    }

    private void SendState() => _bridge.Post(_control.Snapshot());
    private void SendSelection(List<Guid> ids) => _bridge.Post(new { type = "scope.selection", objectIds = ids });

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
        ThemeSettings.ThemeChanged -= OnThemeChanged;
        base.OnUnLoad(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeSettings.ThemeChanged -= OnThemeChanged;
            _control.Changed -= SendState;
            _control.SelectionCaptured -= SendSelection;
            _bridge.Dispose();
        }
        base.Dispose(disposing);
    }
}
