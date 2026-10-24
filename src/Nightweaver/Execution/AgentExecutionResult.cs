namespace Nightweaver.Execution;

internal enum AgentExecutionStatus
{
    Exited,
    TimedOut
}

/// <summary>The process outcome after its job and all descendant processes have been confirmed quiescent.</summary>
internal sealed record AgentExecutionResult(
    AgentExecutionStatus Status,
    int? ExitCode,
    string InvocationId,
    string LogFilePath);

/// <summary>Execution outcome plus a bounded stdout-only capture for protocols that consume machine output.</summary>
internal sealed record AgentStandardOutputCaptureResult(
    AgentExecutionResult Execution,
    byte[] StandardOutput,
    bool Overflowed);

internal sealed class AgentInvocationExecutionException : NightweaverException
{
    internal AgentInvocationExecutionException(
        string invocationId,
        string logFilePath,
        string message,
        Exception innerException)
        : base($"Agent invocation '{invocationId}' (log '{logFilePath}') failed: {message}", innerException)
    {
        InvocationId = invocationId;
        LogFilePath = logFilePath;
    }

    internal string InvocationId { get; }
    internal string LogFilePath { get; }
}
