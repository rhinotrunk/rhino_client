using Rhino;
using Rhino.PlugIns;
using Rhino.UI;
using RhinoClient.UI;

namespace RhinoClient;

public sealed class RhinoClientPlugin : PlugIn
{
    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        if (RhinoApp.ExeVersion < 8 || (RhinoApp.ExeVersion == 8 && RhinoApp.ExeServiceRelease < 20))
        {
            errorMessage = "RhinoClient requires Rhino 8.20 or later running .NET 8.";
            return LoadReturnCode.ErrorShowDialog;
        }

        Panels.RegisterPanel(this, typeof(ClientPanel), "RhinoClient",
            typeof(RhinoClientPlugin).Assembly, "RhinoClient.panel.svg", PanelType.PerDoc);
        return LoadReturnCode.Success;
    }
    protected override void OnShutdown() => RhinoClient.Agent.TaskControl.Shutdown();
}
