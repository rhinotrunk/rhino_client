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

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
        SyncCommands.Run(doc, pull: false, interactive: mode != RunMode.Scripted);
}

[Guid("5b1d7e2c-3f84-4a96-9c0e-8a2f6d1b4e73")]
public sealed class RhinoClientBranchCommand : Command
{
    public override string EnglishName => "RhinoClientBranch";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (!SyncCommands.TryReadTarget(doc, out var target)) return Result.Failure;
        var get = new GetString();
        get.SetCommandPrompt($"New branch from {target.Branch} (the model switches to it)");
        get.AcceptNothing(false);
        if (get.Get() != GetResult.String) return Result.Cancel;
        var name = get.StringResult().Trim();
        return SyncCommands.Execute(doc, "Creating branch…", (sync, token) => sync.CreateBranchAsync(target, name, token));
    }
}

[Guid("c4e8a2f1-6d39-4b7a-8e15-2f9b0c6d3a84")]
public sealed class RhinoClientMergeCommand : Command
{
    public override string EnglishName => "RhinoClientMerge";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (!SyncCommands.TryReadTarget(doc, out var target)) return Result.Failure;
        var into = "";
        var approveRules = new OptionToggle(false, "No", "Yes");
        var get = new GetOption();
        get.AcceptNothing(true);
        while (true)
        {
            get.SetCommandPrompt($"Merge {target.Branch} into {(into.Length == 0 ? "its parent branch" : into)}");
            get.ClearCommandOptions();
            var intoOption = get.AddOption("Into");
            get.AddOptionToggle("ApproveRules", ref approveRules);
            var result = get.Get();
            if (result == GetResult.Cancel) return Result.Cancel;
            if (result == GetResult.Nothing) break;
            if (result == GetResult.Option && get.Option()?.Index == intoOption)
            {
                var text = into;
                if (RhinoGet.GetString("Branch to merge into", true, ref text) == Result.Cancel) return Result.Cancel;
                into = text.Trim();
            }
        }

        return SyncCommands.Execute(doc, "Merging…", (sync, token) =>
            sync.MergeBranchAsync(target, into.Length == 0 ? null : into, interactive: mode != RunMode.Scripted, approveRules.CurrentValue, token));
    }
}

[Guid("9a3f5c2e-1b74-4d8e-a6c9-3e0d7b2f5a16")]
public sealed class RhinoClientCompareCommand : Command
{
    public override string EnglishName => "RhinoClientCompare";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (!SyncCommands.TryReadTarget(doc, out var target)) return Result.Failure;
        var options = "";
        var at = "main";
        var modes = new[] { "Cycle", "Overlay" };
        var modeIndex = 0;
        var get = new GetOption();
        get.AcceptNothing(true);
        while (true)
        {
            get.SetCommandPrompt($"Compare {(options.Length == 0 ? "branches made from " + at : options)} on {at}");
            get.ClearCommandOptions();
            var optionsOption = get.AddOption("Options");
            var atOption = get.AddOption("At");
            var modeOption = get.AddOptionList("Mode", modes, modeIndex);
            var offOption = get.AddOption("Off");
            var result = get.Get();
            if (result == GetResult.Cancel) return Result.Cancel;
            if (result == GetResult.Nothing) break;
            if (result != GetResult.Option) continue;
            var picked = get.Option();
            if (picked?.Index == offOption)
            {
                GeometrySync.ClearPreview();
                return Result.Success;
            }

            if (picked?.Index == modeOption) modeIndex = picked.CurrentListOptionIndex;
            if (picked?.Index == optionsOption)
            {
                var text = options;
                if (RhinoGet.GetString("Branches to compare, separated by commas (empty: every branch made from At)", true, ref text) == Result.Cancel)
                    return Result.Cancel;
                options = text.Trim();
            }

            if (picked?.Index == atOption)
            {
                var text = at;
                if (RhinoGet.GetString("Branch, tag or commit to compare on", true, ref text) == Result.Cancel) return Result.Cancel;
                at = text.Trim();
            }
        }

        var list = options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var compareMode = (CompareMode)modeIndex;
        return SyncCommands.Execute(doc, "Comparing…", (sync, token) =>
            sync.CompareAsync(target, list, at, compareMode, interactive: mode != RunMode.Scripted, token));
    }
}

/// <summary>COMMIT needs a saved file: a push saves the document first, or reverts it, or stops.</summary>
internal static class CommitPolicy
{
    /// <summary>Returned by <see cref="EnsureSaved"/> when the user chose to revert instead of committing.</summary>
    internal const string Revert = "\0revert";

    /// <summary>
    /// Makes sure the document is saved before a push. Interactive: a file that was never saved prompts for a location,
    /// and unsaved edits prompt to save them or revert the model to its last commit. Not interactive: a file with a path
    /// is saved; one that was never saved needs <paramref name="path"/>. Returns null when the push may go ahead.
    /// </summary>
    internal static string? EnsureSaved(RhinoDoc? doc, bool interactive, string? path = null)
    {
        if (doc is null) return "The document is not open.";
        if (string.IsNullOrEmpty(doc.Path))
        {
            if (string.IsNullOrWhiteSpace(path) && interactive)
            {
                var dialog = new SaveFileDialog { Title = "Save the model before committing", Filter = "Rhino 3D Models (*.3dm)|*.3dm", DefaultExt = "3dm" };
                if (!dialog.ShowSaveDialog()) return "Cancelled. A commit needs a saved file.";
                path = dialog.FileName;
            }

            if (string.IsNullOrWhiteSpace(path))
                return "Save the file first: a commit needs a saved file (File > Save, or the Path option when scripting).";
            RhinoApp.RunScript($"_-SaveAs \"{path}\" _Enter", false);
            return string.IsNullOrEmpty(doc.Path) ? $"Could not save the file to {path}." : null;
        }

        if (!doc.Modified) return null;
        if (interactive)
        {
            var get = new GetOption();
            get.SetCommandPrompt("The model has unsaved changes. Save them and commit, or revert the model to its last commit?");
            var save = get.AddOption("Save");
            var revert = get.AddOption("Revert");
            get.AddOption("Cancel");
            if (get.Get() != GetResult.Option) return "Cancelled.";
            var picked = get.Option()?.Index;
            if (picked == revert) return Revert;
            if (picked != save) return "Cancelled.";
        }

        return doc.Save() ? null : "Could not save the file, so nothing was committed.";
    }
}

internal static class SyncCommands
{
    public static Result Run(RhinoDoc doc, bool pull, bool interactive)
    {
        if (!TryReadTarget(doc, out var stored)) return Result.Failure;
        if (!TryChoose(pull, stored, out var target, out var message, out var discard, out var path))
            return Result.Cancel;

        if (!pull)
        {
            var saved = CommitPolicy.EnsureSaved(doc, interactive, path);
            if (saved == CommitPolicy.Revert)
                return Execute(doc, "Reverting…", (sync, token) => sync.PullAsync(target, interactive: false, discardLocal: true, token));
            if (saved is not null)
            {
                RhinoApp.WriteLine("RhinoClient: " + saved);
                return Result.Failure;
            }
        }

        return pull
            ? Execute(doc, "Pulling…", (sync, token) => sync.PullAsync(target, interactive, discard, token))
            : Execute(doc, "Pushing…", (sync, token) => sync.PushAsync(target, message, interactive, token));
    }

    /// <summary>Runs an operation on the panel's checkout when the panel is open for this document, else on a temporary one.</summary>
    public static Result Execute(RhinoDoc doc, string busy, Func<GeometrySync, CancellationToken, Task<SyncResult>> action)
    {
        var panel = Panels.GetPanel<ClientPanel>(doc.RuntimeSerialNumber);
        using var owned = panel is null ? new GeometrySync(doc.RuntimeSerialNumber) : null;
        var task = panel is not null ? panel.Run(busy, action) : action(owned!, CancellationToken.None);
        var result = Wait(task);
        return result.Ok ? Result.Success : Result.Failure;
    }

    public static bool TryReadTarget(RhinoDoc doc, out SyncTarget target)
    {
        var panel = Panels.GetPanel<ClientPanel>(doc.RuntimeSerialNumber);
        using var owned = panel is null ? new GeometrySync(doc.RuntimeSerialNumber) : null;
        try
        {
            target = panel is not null ? panel.ReadTarget() : owned!.ReadTarget();
            return true;
        }
        catch (SyncException ex)
        {
            RhinoApp.WriteLine("RhinoClient: " + ex.Message);
            target = new SyncTarget("", "", "");
            return false;
        }
    }

    private static bool TryChoose(bool pull, SyncTarget stored, out SyncTarget target, out string message, out bool discard, out string? path)
    {
        var server = stored.Server;
        var repo = stored.Repo;
        var branch = stored.Branch;
        message = "Update geometry";
        path = null;
        discard = false;
        target = stored;
        var discardToggle = new OptionToggle(false, "No", "Yes");
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
            var messageOption = pull ? -1 : get.AddOption("Message");
            var pathOption = pull ? -1 : get.AddOption("Path");
            if (pull) get.AddOptionToggle("Discard", ref discardToggle);
            switch (get.Get())
            {
                case GetResult.Cancel:
                    return false;
                case GetResult.Nothing:
                    if (!string.IsNullOrWhiteSpace(repo))
                    {
                        target = new SyncTarget(server, repo, branch);
                        discard = discardToggle.CurrentValue;
                        return true;
                    }

                    if (!Ask("Repository", ref repo)) return false;
                    break;
                case GetResult.Option:
                    var index = get.Option()?.Index ?? 0;
                    if (index == serverOption && !Ask("Server", ref server)) return false;
                    if (index == repoOption && !Ask("Repository", ref repo)) return false;
                    if (index == branchOption && !Ask("Branch", ref branch)) return false;
                    if (index == messageOption && !Ask("Message", ref message)) return false;
                    if (index == pathOption)
                    {
                        var text = path ?? "";
                        if (!Ask("Save the file as", ref text)) return false;
                        path = text;
                    }

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

    private static T Wait<T>(Task<T> task)
    {
        while (!task.IsCompleted)
            RhinoApp.Wait();
        return task.GetAwaiter().GetResult();
    }
}
