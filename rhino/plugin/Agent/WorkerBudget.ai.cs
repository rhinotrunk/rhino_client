namespace RhinoClient.Agent;

// Per turn: a resumed task gets a fresh budget, while keeping its saved geometry.
internal sealed class WorkerBudget
{
    public const int MaxToolCalls = 512;
    public const int MaxConsecutiveErrors = 5;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(15);
    private int _calls;
    private int _errors;

    public void BeginTool()
    {
        if (++_calls > MaxToolCalls)
            throw new WorkerLimitException($"Worker stopped after {MaxToolCalls} tool calls. Review the saved copy and split or clarify the task before resuming.");
    }

    public void Succeeded() => _errors = 0;

    public void Failed()
    {
        if (++_errors >= MaxConsecutiveErrors)
            throw new WorkerLimitException($"Worker stopped after {MaxConsecutiveErrors} consecutive tool errors. Review Records and revise the task before resuming.");
    }
}

internal sealed class WorkerLimitException(string message) : Exception(message);
