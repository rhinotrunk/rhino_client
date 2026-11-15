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

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        SyncCommands.Run(doc, pull: true, interactive: mode != RunMode.Scripted);
}

[Guid("3e8b6c14-5a27-4d90-b7e2-1f0c9a6d58b3")]
public sealed class RhinoClientPushCommand : Command
{
    public override string EnglishName => "RhinoClientPush";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SyncCommands.Run(doc, pull: false);
}

internal static class SyncCommands
{
    public static Result Run(RhinoDoc doc, bool pull, bool interactive = true)
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

            if (!TryChoose(pull, stored, List, out var target, out var message))
                return Result.Cancel;

            var task = panel is not null
                ? pull ? panel.Pull(target, interactive) : panel.Push(target, message)
                : pull ? owned!.PullAsync(target, interactive) : owned!.PushAsync(target, message);
            var result = Wait(task);
            return result.Ok ? Result.Success : Result.Failure;
        }
        finally
        {
            owned?.Dispose();
        }

        IReadOnlyList<CommitChoice> List(SyncTarget target)
        {
            var sync = panel is not null ? null : owned;
            var task = panel is not null ? panel.ListCommits(target) : sync!.ListCommitsAsync(target);
            return Wait(task);
        }
    }

    private static bool TryChoose(
        bool pull, SyncTarget stored, Func<SyncTarget, IReadOnlyList<CommitChoice>> commits, out SyncTarget target, out string message)
    {
        var server = stored.Server;
        var repo = stored.Repo;
        var branch = stored.Branch;
        var commit = "";
        message = "Update geometry";
        target = stored;
        var get = new GetOption();
        get.AcceptNothing(true);
        while (true)
        {
            var commitLabel = string.IsNullOrWhiteSpace(commit)
                ? "branch tip"
                : commit.StartsWith("sha256:", StringComparison.Ordinal) ? ShortId(commit) : commit;
            get.SetCommandPrompt(pull
                ? $"Pull {Label(repo)} {branch} ({commitLabel}) from {server}"
                : $"Push {Label(repo)} {branch} to {server}");
            get.ClearCommandOptions();
            var serverOption = get.AddOption("Server");
            var repoOption = get.AddOption("Repository");
            var branchOption = get.AddOption("Branch");
            var commitOption = pull ? get.AddOption("Commit") : 0;
            var messageOption = pull ? 0 : get.AddOption("Message");
            switch (get.Get())
            {
                case GetResult.Cancel:
                    return false;
                case GetResult.Nothing:
                    if (!string.IsNullOrWhiteSpace(repo))
                    {
                        target = new SyncTarget(server, repo, branch, string.IsNullOrWhiteSpace(commit) ? null : commit);
                        return true;
                    }

                    if (!Ask("Repository", ref repo)) return false;
                    break;
                case GetResult.Option:
                    var index = get.Option()?.Index ?? 0;
                    if (index == serverOption && !Ask("Server", ref server)) return false;
                    if (index == repoOption && !Ask("Repository", ref repo)) return false;
                    if (index == branchOption && !Ask("Branch", ref branch)) return false;
                    if (index == commitOption && commitOption != 0 && !ChooseCommit(server, repo, branch, commits, ref commit)) return false;
                    if (index == messageOption && messageOption != 0 && !Ask("Message", ref message)) return false;
                    break;
                default:
                    return false;
            }
        }
    }

    private static bool ChooseCommit(
        string server, string repo, string branch, Func<SyncTarget, IReadOnlyList<CommitChoice>> commits, ref string commit)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            RhinoApp.WriteLine("RhinoClient: Set a repository before choosing a commit.");
            return true;
        }

        IReadOnlyList<CommitChoice> recent = [];
        try
        {
            recent = commits(new SyncTarget(server, repo, branch));
        }
        catch (Exception ex) when (ex is SyncException or RhinoTrunk.Client.RhinoTrunkApiException or HttpRequestException)
        {
            RhinoApp.WriteLine("RhinoClient: " + ex.Message);
        }

        var names = new Dictionary<int, CommitChoice>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var get = new GetString();
        get.SetCommandPrompt("Commit message or id. Enter for the branch tip");
        get.AcceptNothing(true);
        if (!string.IsNullOrWhiteSpace(commit)) get.SetDefaultString(commit);
        for (var i = 0; i < recent.Count; i++)
        {
            var name = OptionName(recent[i].Message, i + 1, used);
            var index = get.AddOption(name);
            if (index <= 0) continue;
            names[index] = recent[i];
            RhinoApp.WriteLine($"RhinoClient: {name} — {recent[i].Message} ({ShortId(recent[i].Id)})");
        }

        switch (get.Get())
        {
            case GetResult.Cancel:
                return false;
            case GetResult.Nothing:
                commit = "";
                return true;
            case GetResult.Option:
                if (get.Option() is { } option && names.TryGetValue(option.Index, out var choice))
                    commit = choice.Id;
                return true;
            case GetResult.String:
                commit = get.StringResult()?.Trim() ?? "";
                return true;
            default:
                return false;
        }
    }

    private static string OptionName(string message, int number, HashSet<string> used)
    {
        var letters = new string(message.Where(char.IsLetterOrDigit).ToArray());
        if (letters.Length == 0 || char.IsDigit(letters[0])) letters = "Commit" + letters;
        if (letters.Length > 24) letters = letters[..24];
        var name = letters;
        var suffix = 2;
        while (suffix < 100 && (used.Contains(name) || !CommandLineOption.IsValidOptionName(name)))
            name = letters + suffix++;
        used.Add(name);
        return name;
    }

    private static string ShortId(string id)
    {
        const string prefix = "sha256:";
        var hex = id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;
        return hex.Length <= 8 ? hex : hex[..8];
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

    private static T Wait<T>(Task<T> task)
    {
        while (!task.IsCompleted)
            RhinoApp.Wait();
        return task.GetAwaiter().GetResult();
    }
}
