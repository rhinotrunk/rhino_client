using System.Diagnostics;
using System.Text.Json;

namespace RhinoClient.Agent;

// External CLI lifecycle, adapted conceptually from RhinoAI's StreamJsonAgent.
// No provider SDK, model requests, or credentials are hosted in Rhino.
internal static class CodexRunner
{
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

    public static async Task Run(TaskEntry task, ControlState settings, string directory, string url, string prompt,
        Func<string, Task> onEvent, Action<Process> started, CancellationToken ct,
        TimeSpan? idleTimeout = null, TimeSpan? turnTimeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var idlePeriod = idleTimeout ?? WorkerBudget.IdleTimeout;
        using var idle = new CancellationTokenSource(idlePeriod);
        using var deadline = new CancellationTokenSource(turnTimeout ?? WorkerBudget.TurnTimeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token, deadline.Token);
        var token = lifetime.Token;
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
        var model = settings.Model is "gpt-6-luna" or "gpt-6.1-terra" ? settings.Model : ControlState.BudgetModel;
        task.Model = model;
        args.AddRange(["-m", model, "-c", "model_reasoning_effort=\"low\""]);
        args.Add("-");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("Could not launch Codex CLI.");
        using var registration = token.Register(() => { try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var stderr = ReadErrors();
        try
        {
            started(process);
            var completed = false;
            await process.StandardInput.WriteAsync(prompt.AsMemory(), token);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                idle.CancelAfter(idlePeriod);
                await onEvent(line).WaitAsync(token);
                try { using var parsed = JsonDocument.Parse(line); if (AgentJson.Text(parsed.RootElement, "type") == "turn.completed") completed = true; }
                catch (JsonException) { }
            }
            await process.WaitForExitAsync(token);
            var diagnostic = await stderr.WaitAsync(token);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Codex exited with code {process.ExitCode}. {diagnostic}");
            if (!completed) throw new InvalidOperationException("Codex exited without confirming turn completion. Inspect activity before continuing.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(deadline.IsCancellationRequested
                ? "Worker exceeded the time limit. Review the saved copy and split the task before resuming."
                : "Worker stopped responding. Saved geometry is retained; review Records before resuming.");
        }
        finally
        {
            // Parsing errors must not leave a worker with live tool access behind.
            lifetime.Cancel();
            try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await stderr;
        }

        async Task<string> ReadErrors()
        {
            // Bound diagnostics; do not persist the complete CLI environment or config.
            string last = "";
            try
            {
                while (await process.StandardError.ReadLineAsync(token) is { } line)
                    if (!string.IsNullOrWhiteSpace(line)) last = line.Length > 1200 ? line[..1200] : line;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return last;
        }
    }
}
