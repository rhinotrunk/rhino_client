using System.Diagnostics;
using RhinoClient.Agent;

internal static class WorkerTests
{
    // This test executable also acts as a CLI worker, with no model requests or Rhino.
    public static async Task FakeWorker()
    {
        var mode = await Console.In.ReadToEndAsync();
        if (mode == "active" || mode == "endless")
        {
            for (var i = 0; mode == "endless" || i < 15; i++)
            {
                Console.WriteLine("{\"type\":\"item.started\"}");
                await Task.Delay(100);
            }
        }
        if (mode == "stall") await Task.Delay(Timeout.Infinite);
        if (mode != "incomplete") Console.WriteLine("{\"type\":\"turn.completed\"}");
        if (mode == "hang-after-completion") await Task.Delay(Timeout.Infinite);
    }

    public static async Task Run()
    {
        var budget = new WorkerBudget();
        for (var i = 0; i < WorkerBudget.MaxToolCalls; i++) { budget.BeginTool(); budget.Succeeded(); }
        ExpectLimit(budget.BeginTool);
        budget.Succeeded();
        ExpectLimit(budget.BeginTool); // A success cannot replenish the turn's call budget.
        budget = new();
        for (var i = 0; i < WorkerBudget.MaxConsecutiveErrors - 1; i++) budget.Failed();
        budget.Succeeded();
        for (var i = 0; i < WorkerBudget.MaxConsecutiveErrors - 1; i++) budget.Failed();
        ExpectLimit(budget.Failed);
        Console.WriteLine("PASS: runaway tool calls and repeated errors stop at their limits; successful calls reset consecutive errors.");

        await RunWorker("complete", null);
        await RunWorker("active", null); // Activity extends the idle deadline.
        await RunWorker("incomplete", typeof(InvalidOperationException));
        await RunWorker("stall", typeof(TimeoutException));
        await RunWorker("hang-after-completion", typeof(TimeoutException));
        await RunWorker("complete", typeof(TimeoutException), blockEvent: true);
        await RunWorker("endless", typeof(TimeoutException), turnTimeout: TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await RunWorker("stall", typeof(OperationCanceledException), cancellation.Token);
        await RunWorker("stall", typeof(InvalidDataException), failEvent: true);
        Console.WriteLine("PASS: CLI completion, activity, idle timeout, total timeout, user stop and callback failure all clean up the worker.");
    }

    private static void ExpectLimit(Action action)
    {
        try { action(); }
        catch (WorkerLimitException) { return; }
        throw new Exception("Expected an execution limit.");
    }

    private static async Task RunWorker(string mode, Type? expected, CancellationToken ct = default,
        TimeSpan? turnTimeout = null, bool failEvent = false, bool blockEvent = false)
    {
        // Environment.ProcessPath is the apphost when started with dotnet run.
        var settings = new ControlState { CodexPath = Environment.ProcessPath! };
        int? pid = null;
        Exception? failure = null;
        try
        {
            await CodexRunner.Run(new(), settings, "gpt-6-luna", Path.GetTempPath(), "http://127.0.0.1:1/unused/", failEvent ? "complete" : mode,
                _ => blockEvent ? new TaskCompletionSource().Task : failEvent ? Task.FromException(new InvalidDataException("Event parsing failed.")) : Task.CompletedTask,
                process => pid = process.Id, ct, TimeSpan.FromSeconds(1), turnTimeout ?? TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { failure = ex; }
        if (expected is null ? failure is not null : failure is null || !expected.IsInstanceOfType(failure))
            throw new Exception($"{mode}: expected {expected?.Name ?? "completion"}, got {failure?.ToString() ?? "completion"}.");
        if (pid is null) throw new Exception("Worker did not start.");
        for (var i = 0; i < 50; i++)
        {
            try { using var process = Process.GetProcessById(pid.Value); if (process.HasExited) return; }
            catch (ArgumentException) { return; }
            await Task.Delay(20);
        }
        throw new Exception($"{mode}: worker was left alive.");
    }
}
