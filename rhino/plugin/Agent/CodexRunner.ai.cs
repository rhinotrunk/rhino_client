using System.Diagnostics;
using System.Text.Json;

namespace RhinoClient.Agent;

// External CLI lifecycle, adapted conceptually from RhinoAI's StreamJsonAgent.
// No provider SDK, model requests, or credentials are hosted in Rhino.
internal static class CodexRunner
{
    public static bool IsAvailable(string path) => ProcessLauncher.Exists(path);

    public static string FindExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("RHINOCLIENT_CODEX");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var file = Path.Combine(dir, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(file)) return file;
        }
        // GUI-launched Rhino on macOS often has a shorter PATH than a terminal.
        var extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (Directory.Exists(extensions))
            foreach (var dir in Directory.GetDirectories(extensions, "openai.chatgpt-*").OrderDescending())
            {
                var bin = Path.Combine(dir, "bin");
                if (!Directory.Exists(bin)) continue;
                var match = Directory.GetFiles(bin, "codex", SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null) return match;
            }
        return "codex";
    }

    public static Task Run(TaskEntry task, ControlState settings, string model, string directory, string url, string prompt,
        Func<string, Task> onEvent, Action<Process> started, CancellationToken ct,
        TimeSpan? idleTimeout = null, TimeSpan? turnTimeout = null)
    {
        var psi = new ProcessStartInfo(settings.CodexPath)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // Use the existing CLI login, but exclude unrelated user MCP servers and instructions.
        var args = new List<string> { "exec" };
        if (task.WorkerThreadId is not null) args.AddRange(["resume", task.WorkerThreadId]);
        args.AddRange(["--json", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules"]);
        args.AddRange(["-c", "sandbox_mode=\"read-only\"", "-c", "approval_policy=\"never\"",
            "-c", "features.shell_tool=false", "-c", "features.unified_exec=false", "-c", "web_search=\"disabled\"",
            "-c", $"mcp_servers.rhino.url={JsonSerializer.Serialize(url)}",
            "-c", "mcp_servers.rhino.default_tools_approval_mode=\"approve\"",
            "-c", "mcp_servers.rhino.tool_timeout_sec=120"]);
        task.Model = model;
        args.AddRange(["-m", model, "-c", "model_reasoning_effort=\"low\""]);
        args.Add("-");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return ProcessLauncher.Execute("Codex", psi, prompt, onEvent, started, ct, idleTimeout, turnTimeout, translate: null);
    }
}
