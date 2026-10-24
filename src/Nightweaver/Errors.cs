namespace Nightweaver;

internal abstract class NightweaverException(string message) : Exception(message);

internal sealed class UsageException(string message) : NightweaverException(message);

internal sealed class ProjectConfigurationException(
    string message,
    string? jsonPath,
    string configFilePath) : NightweaverException(message)
{
    internal string? JsonPath { get; } = jsonPath;
    internal string ConfigFilePath { get; } = configFilePath;
}

internal sealed class TargetRepositoryException(string message) : NightweaverException(message);

internal sealed class ToolExecutionException(string message, string? standardError = null) : NightweaverException(message)
{
    internal string? StandardError { get; } = standardError;
}
