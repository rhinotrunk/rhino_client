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

            var launch = FindServer(Origin(server))
                ?? throw new SyncException($"The server at {Origin(server)} is not running. Neither git_engine_rs nor the RhinoTrunk server project was found next to the plugin, so it was not started. Set RHINOTRUNK_SERVER_EXE to the rhinotrunk-server binary.");
            var log = new StringBuilder();
            Process process;
            try
            {
                process = Start(launch, log);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new SyncException($"The server at {Origin(server)} is not running, and {launch.FileName} could not be started. {ex.Message}");
            }

            _process = process;
            await WaitUntilListeningAsync(process, server, log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>How to start the server: a program, its arguments and working directory.</summary>
    internal sealed record Launch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

    private static Process Start(Launch launch, StringBuilder log)
    {
        var start = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_NOLOGO"] = "1";
        var data = Environment.GetEnvironmentVariable("RHINOTRUNK_DATA");
        if (!string.IsNullOrWhiteSpace(data)) start.Environment["Storage__Root"] = data;

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(log, e.Data);
        process.ErrorDataReceived += (_, e) => Append(log, e.Data);
        if (!process.Start())
            throw new SyncException($"{launch.FileName} could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitUntilListeningAsync(Process process, string server, StringBuilder? log, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new SyncException($"The server at {Origin(server)} exited before it accepted connections. {Tail(log)}");
            if (await IsListeningAsync(server, cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new SyncException($"The server at {Origin(server)} was started but did not accept connections within 180 seconds (a first `cargo run` compiles the server). {Tail(log)}");
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

    /// <summary>
    /// Finds the server. In order: <c>RHINOTRUNK_SERVER_EXE</c> (a rhinotrunk-server binary),
    /// <c>RHINOTRUNK_SERVER_PROJECT</c> (a server .csproj, legacy), then a <c>git_engine_rs</c> checkout next to the
    /// plugin (its release binary, else <c>cargo run</c>), then the legacy C# server in <c>git_engine</c>.
    /// The server keeps its data in <c>data</c> under its working directory unless <c>RHINOTRUNK_DATA</c> is set.
    /// </summary>
    internal static Launch? FindServer(string origin)
    {
        var exeName = OperatingSystem.IsWindows() ? "rhinotrunk-server.exe" : "rhinotrunk-server";
        var exe = Environment.GetEnvironmentVariable("RHINOTRUNK_SERVER_EXE");
        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            return new Launch(Path.GetFullPath(exe), ["--urls", origin], Path.GetDirectoryName(Path.GetFullPath(exe))!);
        var configured = Environment.GetEnvironmentVariable("RHINOTRUNK_SERVER_PROJECT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return DotnetProject(Path.GetFullPath(configured), origin);

        string? legacy = null;
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(LocalServer).Assembly.Location)!);
        while (dir is not null)
        {
            foreach (var root in new[] { Path.Combine(dir.FullName, "git_engine_rs"), dir.FullName })
            {
                if (!File.Exists(Path.Combine(root, "crates", "server", "Cargo.toml"))) continue;
                foreach (var profile in new[] { "release", "debug" })
                {
                    var binary = Path.Combine(root, "target", profile, exeName);
                    if (File.Exists(binary)) return new Launch(binary, ["--urls", origin], root);
                }

                return new Launch(FindCargo(), ["run", "--release", "-p", "rhinotrunk-server", "--", "--urls", origin], root);
            }

            legacy ??= new[]
            {
                Path.Combine(dir.FullName, "git_engine", "src", "RhinoTrunk.Server", "RhinoTrunk.Server.csproj"),
                Path.Combine(dir.FullName, "src", "RhinoTrunk.Server", "RhinoTrunk.Server.csproj"),
            }.FirstOrDefault(File.Exists);
            dir = dir.Parent;
        }

        return legacy is null ? null : DotnetProject(legacy, origin);
    }

    private static Launch DotnetProject(string project, string origin) =>
        new(FindDotnet(), ["run", "--project", project, "--urls", origin], Path.GetDirectoryName(project)!);

    private static string FindCargo()
    {
        var fileName = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin", fileName);
        return File.Exists(home) ? home : fileName;
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
