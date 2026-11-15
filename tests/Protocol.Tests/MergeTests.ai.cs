using System.Text.RegularExpressions;
using RhinoClient.Sync;

static class MergeTests
{
    public static void Run()
    {
        var valid = new Regex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,198}$");
        var cases = new[]
        {
            new TaskMergeRequest("1a2b3c4d5e6f", "Create a sphere and a box", 1, "r.3dm", "sha256:aa"),
            new TaskMergeRequest("ABCDEF1234", "  Façade — option #2 (south)!  ", 3, "r.3dm", "sha256:aa"),
            new TaskMergeRequest("0", "???", 0, "r.3dm", "sha256:aa"),
            new TaskMergeRequest("a".PadRight(40, 'b'), new string('x', 300), 7, "r.3dm", "sha256:aa"),
        };
        foreach (var task in cases)
        {
            var name = TaskBranch.Name(task);
            if (!valid.IsMatch(name) || name.Contains("..") || name.Contains("//") || name.EndsWith('/') || name.EndsWith('.'))
                throw new Exception($"Invalid branch name '{name}'.");
            if (name != TaskBranch.Name(task)) throw new Exception("Branch name is not deterministic.");
        }

        if (TaskBranch.Name(cases[0]) != "agent/create-a-sphere-and-a-box-1a2b3c4d-r1")
            throw new Exception("Unexpected branch name: " + TaskBranch.Name(cases[0]));
        if (TaskBranch.Name(cases[0]) == TaskBranch.Name(cases[0] with { Revision = 2 }))
            throw new Exception("A new result revision must get its own branch.");
        Console.WriteLine("PASS: task merge branch names are valid ref names, deterministic, and unique per result revision.");
    }
}
