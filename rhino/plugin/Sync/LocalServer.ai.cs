using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace RhinoClient.Sync;

/// <summary>Starts the local RhinoTrunk server when a panel request finds it is not listening.</summary>
internal static class LocalServer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;

    internal static bool IsLoopback(string? server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback && uri.Scheme == "http";
    }

    internal static async Task EnsureRunningAsync(string server, CancellationToken cancellationToken)
    {
        if (!IsLoopback(server)) return;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsListeningAsync(server, cancellationToken).ConfigureAwait(false)) return;
            RhinoAppNotice("The server is not running. Starting it…");

            if (_process is { HasExited: false } running)
            {
                await WaitUntilListeningAsync(running, server, log: null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var project = FindProject()
                ?? throw new SyncException($"The server at {Origin(server)} is not running. The RhinoTrunk server project was not found next to the plugin, so it was not started.");
            var dotnet = FindDotnet();
            var log = new StringBuilder();
            Process process;
            try
            {
                process = Start(dotnet, project, Origin(server), log);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new SyncException($"The server at {Origin(server)} is not running, and {dotnet} could not be started. {ex.Message}");
            }

            _process = process;
            await WaitUntilListeningAsync(process, server, log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Process Start(string dotnet, string project, string origin, StringBuilder log)
    {
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = Path.GetDirectoryName(project)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(origin);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_NOLOGO"] = "1";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(log, e.Data);
        process.ErrorDataReceived += (_, e) => Append(log, e.Data);
        if (!process.Start())
            throw new SyncException($"The server at {origin} is not running, and {dotnet} could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitUntilListeningAsync(Process process, string server, StringBuilder? log, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new SyncException($"The server at {Origin(server)} exited before it accepted connections. {Tail(log)}");
            if (await IsListeningAsync(server, cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new SyncException($"The server at {Origin(server)} was started but did not accept connections within 90 seconds. {Tail(log)}");
    }

    private static void RhinoAppNotice(string message)
    {
        var write = () => Rhino.RhinoApp.WriteLine("RhinoClient: " + message);
        if (!Rhino.RhinoApp.InvokeRequired) write();
        else Eto.Forms.Application.Instance?.Invoke(write);
    }

    private static async Task<bool> IsListeningAsync(string server, CancellationToken cancellationToken)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await probe.GetAsync(Origin(server), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Origin(string server)
    {
        var uri = new Uri(server);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string? FindProject()
    {
        var configured = Environment.GetEnvironmentVariable("RHINOTRUNK_SERVER_PROJECT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(LocalServer).Assembly.Location)!);
        while (dir is not null)
        {
            var sibling = Path.Combine(dir.FullName, "git_engine", "src", "RhinoTrunk.Server", "RhinoTrunk.Server.csproj");
            if (File.Exists(sibling)) return sibling;
            var nested = Path.Combine(dir.FullName, "src", "RhinoTrunk.Server", "RhinoTrunk.Server.csproj");
            if (File.Exists(nested)) return nested;
            dir = dir.Parent;
        }

        return null;
    }

    private static string FindDotnet()
    {
        var configured = Environment.GetEnvironmentVariable("RHINOCLIENT_DOTNET");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var fileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", fileName);
        return File.Exists(home) ? home : fileName;
    }

    private static void Append(StringBuilder log, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (log)
        {
            if (log.Length > 4000) log.Remove(0, log.Length - 3000);
            log.AppendLine(line.Trim());
        }
    }

    private static string Tail(StringBuilder? log)
    {
        if (log is null) return "";
        lock (log) return log.Length == 0 ? "" : log.ToString().Trim();
    }
}
