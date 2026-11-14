using System.Runtime.InteropServices;
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
    private int _clicks;

    public ClientPanel() : this(0) { }

    public ClientPanel(uint documentSerialNumber)
    {
        var view = new WebView();
        _bridge = new PanelBridge(view, OnCommand);
        Content = view;
        Padding = 4;

        using var stream = typeof(ClientPanel).Assembly.GetManifestResourceStream("RhinoClient.panel.html")
            ?? throw new InvalidOperationException("The embedded panel is missing. Rebuild RhinoClient.");
        using var reader = new StreamReader(stream);
        view.LoadHtml(reader.ReadToEnd());
    }

    private void OnCommand(string type)
    {
        switch (type)
        {
            case "ready":
                SendTheme();
                SendState();
                break;
            case "button.click":
                _clicks++;
                RhinoApp.WriteLine($"RhinoClient: button clicked ({_clicks}).");
                SendState();
                break;
        }
    }

    private void SendState() => _bridge.Post(new
    {
        type = "state",
        clicks = _clicks,
        message = _clicks == 0 ? "Connected to Rhino" : $"Rhino received click {_clicks}",
    });

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
            _bridge.Dispose();
        }
        base.Dispose(disposing);
    }
}
