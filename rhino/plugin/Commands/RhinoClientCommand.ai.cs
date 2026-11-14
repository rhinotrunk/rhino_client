using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhinoClient.UI;

namespace RhinoClient.Commands;

public sealed class RhinoClientCommand : Command
{
    public override string EnglishName => "RhinoClient";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Panels.OpenPanel(ClientPanel.PanelId);
        return Result.Success;
    }
}
