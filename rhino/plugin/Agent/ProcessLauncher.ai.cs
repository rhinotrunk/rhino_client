using System.Diagnostics;
using System.Text.Json;

namespace RhinoClient.Agent;

// Shared lifecycle for the external agent CLIs: feed the prompt on stdin, read one JSON event per line,
// enforce idle and overall time limits, and never leave a worker with live tool access behind.
internal static class ProcessLauncher
{
    public static bool Exists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains(Path.DirectorySeparatorChar) || path.Contains('/')) return File.Exists(path);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { path + ".exe", path + ".cmd" } : new[] { path })
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name))) return true;
        return false;
    }

    /// <param name="translate">Turns one CLI line into zero or more Codex-shaped events, or null when the CLI already speaks that shape.</param>
    public static async Task Execute(string label, ProcessStartInfo psi, string prompt, Func<string, Task> onEvent,
        Action<Process> started, CancellationToken ct, TimeSpan? idleTimeout, TimeSpan? turnTimeout, Func<string, IEnumerable<string>>? translate)
    {
        ct.ThrowIfCancellationRequested();
        var idlePeriod = idleTimeout ?? WorkerBudget.IdleTimeout;
        using var idle = new CancellationTokenSource(idlePeriod);
        using var deadline = new CancellationTokenSource(turnTimeout ?? WorkerBudget.TurnTimeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token, deadline.Token);
        var token = lifetime.Token;
        using var process = new Process { StartInfo = psi };
        try { if (!process.Start()) throw new InvalidOperationException($"Could not launch {label} CLI."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException($"Could not launch the {label} CLI ({ex.Message}). Check its path in Settings.", ex); }
        using var registration = token.Register(() => { try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var stderr = ReadErrors();
        try
        {
            started(process);
            var completed = false;
            await process.StandardInput.WriteAsync(prompt.AsMemory(), token);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(token) is { } raw)
            {
                idle.CancelAfter(idlePeriod);
                foreach (var line in translate is null ? [raw] : translate(raw))
                {
                    await onEvent(line).WaitAsync(token);
                    try { using var parsed = JsonDocument.Parse(line); if (AgentJson.Text(parsed.RootElement, "type") == "turn.completed") completed = true; }
                    catch (JsonException) { }
                }
            }
            await process.WaitForExitAsync(token);
            var diagnostic = await stderr.WaitAsync(token);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"{label} exited with code {process.ExitCode}. {diagnostic}");
            if (!completed) throw new InvalidOperationException($"{label} exited without confirming turn completion. Inspect activity before continuing.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(deadline.IsCancellationRequested
                ? "Worker exceeded the time limit. Review the saved copy and split the task before resuming."
                : "Worker stopped responding. Saved geometry is retained; review Records before resuming.");
        }
        finally
        {
            lifetime.Cancel();
            try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await stderr;
        }

        async Task<string> ReadErrors()
        {
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
