using System.Diagnostics;
using System.Text.Json;

namespace RhinoClient.Agent;

// Claude models run through the Claude Code CLI in print mode with only the rhino MCP server and no built-in
// tools. Its stream-json lines are translated to the events the panel already understands from Codex, so
// activity, token counts and resume work the same way.
internal static class ClaudeRunner
{
    public static bool IsAvailable(string path) => ProcessLauncher.Exists(path);

    public static string FindExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("RHINOCLIENT_CLAUDE");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[]
        {
            Path.Combine(home, ".claude", "local", "claude"), Path.Combine(home, ".local", "bin", "claude"),
            "/opt/homebrew/bin/claude", "/usr/local/bin/claude",
        })
            if (File.Exists(candidate)) return candidate;
        return "claude";
    }

    public static Task Run(TaskEntry task, ControlState settings, string model, string directory, string url, string prompt,
        Func<string, Task> onEvent, Action<Process> started, CancellationToken ct,
        TimeSpan? idleTimeout = null, TimeSpan? turnTimeout = null)
    {
        var psi = new ProcessStartInfo(settings.ClaudePath)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var mcp = JsonSerializer.Serialize(new { mcpServers = new { rhino = new { type = "http", url } } });
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--model", model };
        if (task.WorkerThreadId is not null) args.AddRange(["--resume", task.WorkerThreadId]);
        // Only the rhino tools: no shell, files, web, or user MCP servers. Anything not allowed is denied, not prompted.
        args.AddRange(["--strict-mcp-config", "--mcp-config", mcp, "--allowedTools", "mcp__rhino",
            "--disallowedTools", "Bash,Edit,Write,Read,Glob,Grep,WebFetch,WebSearch,Task,NotebookEdit,TodoWrite"]);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["MCP_TOOL_TIMEOUT"] = "120000";
        task.Model = model;
        return ProcessLauncher.Execute("Claude", psi, prompt, onEvent, started, ct, idleTimeout, turnTimeout, Translate);
    }

    internal static IEnumerable<string> Translate(string line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { yield break; }
        using (document)
        {
            var root = document.RootElement;
            var type = AgentJson.Text(root, "type");
            if (type == "system" && AgentJson.Text(root, "subtype") == "init" && AgentJson.Text(root, "session_id") is { Length: > 0 } session)
                yield return JsonSerializer.Serialize(new { type = "thread.started", thread_id = session });
            else if (type == "assistant" && root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                    if (AgentJson.Text(block, "type") == "text" && AgentJson.Text(block, "text") is { Length: > 0 } text)
                        yield return JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text } });
            }
            else if (type == "result")
            {
                var failed = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True
                    || AgentJson.Text(root, "subtype").StartsWith("error", StringComparison.Ordinal);
                if (failed)
                {
                    yield return JsonSerializer.Serialize(new { type = "turn.failed", error = AgentJson.Text(root, "result", "Claude turn failed.") });
                    yield break;
                }
                long input = 0, output = 0;
                if (root.TryGetProperty("usage", out var usage))
                {
                    input = Count(usage, "input_tokens") + Count(usage, "cache_creation_input_tokens") + Count(usage, "cache_read_input_tokens");
                    output = Count(usage, "output_tokens");
                }
                yield return JsonSerializer.Serialize(new { type = "turn.completed", usage = new { input_tokens = input, output_tokens = output } });
            }
        }
    }

    private static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
