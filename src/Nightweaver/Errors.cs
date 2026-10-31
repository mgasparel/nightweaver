namespace Nightweaver;

internal abstract class NightweaverException : Exception
{
    protected NightweaverException(string message) : base(message) { }
    protected NightweaverException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class UsageException(string message) : NightweaverException(message);

internal sealed record ConfigurationError(string? JsonPath, string Message);

internal sealed class ProjectConfigurationException : NightweaverException
{
    internal ProjectConfigurationException(string message, string? jsonPath, string configFilePath)
        : this([new ConfigurationError(jsonPath, message)], configFilePath)
    {
    }

    internal ProjectConfigurationException(IReadOnlyList<ConfigurationError> errors, string configFilePath)
        : base(errors.Count == 1 ? errors[0].Message : $"{errors.Count} configuration errors")
    {
        if (errors.Count == 0)
        {
            throw new ArgumentException("At least one configuration error is required", nameof(errors));
        }

        Errors = Array.AsReadOnly(errors.ToArray());
        JsonPath = Errors[0].JsonPath;
        ConfigFilePath = configFilePath;
    }

    internal IReadOnlyList<ConfigurationError> Errors { get; }
    internal string? JsonPath { get; }
    internal string ConfigFilePath { get; }
}

internal sealed class TargetRepositoryException(string message) : NightweaverException(message);

internal sealed class AgentInvocationPreparationException(string message) : NightweaverException(message);

internal sealed class AgentStageInputException(string message) : NightweaverException(message);

internal sealed class AgentPostExitVerificationException(string invocationId, string logFilePath, string message)
    : NightweaverException($"Agent invocation '{invocationId}' (log '{logFilePath}') could not be fully verified: {message}")
{
    internal string InvocationId { get; } = invocationId;
    internal string LogFilePath { get; } = logFilePath;
}

internal sealed class ToolExecutionException(string message, string? standardError = null) : NightweaverException(message)
{
    internal string? StandardError { get; } = standardError;
}

internal sealed class BeadsGuardConflictException(string message) : NightweaverException(message);
