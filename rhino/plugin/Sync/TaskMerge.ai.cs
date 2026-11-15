using System.Text;

namespace RhinoClient.Sync;

/// <summary>
/// A finished agent task whose result copy should be merged into the model's branch.
/// <paramref name="BaseCommit"/> is the commit the task's snapshot was taken at.
/// </summary>
public sealed record TaskMergeRequest(string TaskId, string Title, int Revision, string ResultPath, string BaseCommit)
{
    /// <summary>Which side wins when both changed the same object: "ours" (the model's branch) or "theirs" (the task). Null asks first.</summary>
    public string? Resolve { get; init; }

    /// <summary>Branch the pull request merges into; null means the branch the model is on.</summary>
    public string? Into { get; init; }
}

public static class TaskBranch
{
    /// <summary>
    /// Deterministic branch name for a task result, e.g. <c>agent/create-a-sphere-and-a-box-1a2b3c4d-r1</c>.
    /// The revision is included so a re-run task never reuses the branch of its earlier result, while
    /// retrying the same result finds the branch it already created.
    /// </summary>
    public static string Name(TaskMergeRequest task)
    {
        var slug = new StringBuilder();
        foreach (var c in task.Title.ToLowerInvariant())
        {
            var keep = char.IsAsciiLetterOrDigit(c);
            if (keep) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }

        var text = slug.ToString().Trim('-');
        if (text.Length > 32) text = text[..32].Trim('-');
        if (text.Length == 0) text = "task";
        var id = new string(task.TaskId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        if (id.Length == 0) id = "x";
        return $"agent/{text}-{id.ToLowerInvariant()}-r{Math.Max(task.Revision, 0)}";
    }
}
