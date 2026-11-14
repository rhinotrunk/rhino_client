using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoClient.Sync;
using RhinoClient.UI;

namespace RhinoClient.Commands;

[Guid("7c2a1e4b-9d63-4f58-a1c0-6e5b8d4a21f7")]
public sealed class RhinoClientPullCommand : Command
{
    public override string EnglishName => "RhinoClientPull";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SyncCommands.Run(doc, pull: true);
}

[Guid("3e8b6c14-5a27-4d90-b7e2-1f0c9a6d58b3")]
public sealed class RhinoClientPushCommand : Command
{
    public override string EnglishName => "RhinoClientPush";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SyncCommands.Run(doc, pull: false);
}

internal static class SyncCommands
{
    public static Result Run(RhinoDoc doc, bool pull)
    {
        var panel = Panels.GetPanel<ClientPanel>(doc.RuntimeSerialNumber);
        GeometrySync? owned = panel is null ? new GeometrySync(doc.RuntimeSerialNumber) : null;
        try
        {
            SyncTarget stored;
            try
            {
                stored = panel is not null ? panel.ReadTarget() : owned!.ReadTarget();
            }
            catch (SyncException ex)
            {
                RhinoApp.WriteLine("RhinoClient: " + ex.Message);
                return Result.Failure;
            }

            if (!TryChoose(pull, stored, out var target, out var message))
                return Result.Cancel;

            var task = panel is not null
                ? pull ? panel.Pull(target) : panel.Push(target, message)
                : pull ? owned!.PullAsync(target) : owned!.PushAsync(target, message);
            var result = Wait(task);
            return result.Ok ? Result.Success : Result.Failure;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static bool TryChoose(bool pull, SyncTarget stored, out SyncTarget target, out string message)
    {
        var server = stored.Server;
        var repo = stored.Repo;
        var branch = stored.Branch;
        message = "Update geometry";
        target = stored;
        var get = new GetOption();
        get.AcceptNothing(true);
        while (true)
        {
            get.SetCommandPrompt(pull
                ? $"Pull {Label(repo)} {branch} from {server}"
                : $"Push {Label(repo)} {branch} to {server}");
            get.ClearCommandOptions();
            var serverOption = get.AddOption("Server");
            var repoOption = get.AddOption("Repository");
            var branchOption = get.AddOption("Branch");
            var messageOption = pull ? 0 : get.AddOption("Message");
            switch (get.Get())
            {
                case GetResult.Cancel:
                    return false;
                case GetResult.Nothing:
                    if (!string.IsNullOrWhiteSpace(repo))
                    {
                        target = new SyncTarget(server, repo, branch);
                        return true;
                    }

                    if (!Ask("Repository", ref repo)) return false;
                    break;
                case GetResult.Option:
                    var index = get.Option()?.Index ?? 0;
                    if (index == serverOption && !Ask("Server", ref server)) return false;
                    if (index == repoOption && !Ask("Repository", ref repo)) return false;
                    if (index == branchOption && !Ask("Branch", ref branch)) return false;
                    if (index == messageOption && messageOption != 0 && !Ask("Message", ref message)) return false;
                    break;
                default:
                    return false;
            }
        }
    }

    private static bool Ask(string prompt, ref string value)
    {
        var text = value;
        var result = RhinoGet.GetString(prompt, true, ref text);
        if (result == Result.Cancel) return false;
        if (result == Result.Success) value = text.Trim();
        return true;
    }

    private static string Label(string repo) => string.IsNullOrWhiteSpace(repo) ? "(no repository)" : repo;

    private static SyncResult Wait(Task<SyncResult> task)
    {
        while (!task.IsCompleted)
            RhinoApp.Wait();
        return task.GetAwaiter().GetResult();
    }
}
