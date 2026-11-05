using System.Collections.ObjectModel;

namespace Nightweaver.Execution;

/// <summary>
/// Immutable launch data for one Agent CLI invocation. The runtime consumer must reserve the temporary directory
/// and log path with exclusive creation, reject existing destinations, and inspect path components for reparse
/// points before creating anything. Preparation only computes these paths.
/// </summary>
internal sealed class PreparedAgentInvocation
{
    internal PreparedAgentInvocation(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        string standardInput,
        IReadOnlyDictionary<string, string> environmentOverrides,
        TimeSpan timeout,
        string invocationId,
        string temporaryDirectory,
        string logFilePath,
        string? purpose = null,
        string? stage = null,
        int? repairCycles = null,
        int? maxRepairCycles = null,
        string? configuredModel = null,
        string? effort = null)
    {
        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        WorkingDirectory = workingDirectory;
        StandardInput = standardInput;
        EnvironmentOverrides = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environmentOverrides, StringComparer.Ordinal));
        Timeout = timeout;
        InvocationId = invocationId;
        TemporaryDirectory = temporaryDirectory;
        LogFilePath = logFilePath;
        Purpose = purpose ?? invocationId[(invocationId.LastIndexOf('-') + 1)..];
        Stage = stage;
        RepairCycles = repairCycles;
        MaxRepairCycles = maxRepairCycles;
        ConfiguredModel = configuredModel;
        Effort = effort;
    }

    internal string Executable { get; }
    internal IReadOnlyList<string> Arguments { get; }
    internal string WorkingDirectory { get; }
    internal string StandardInput { get; }
    internal IReadOnlyDictionary<string, string> EnvironmentOverrides { get; }
    internal TimeSpan Timeout { get; }
    internal string InvocationId { get; }
    internal string TemporaryDirectory { get; }
    internal string LogFilePath { get; }
    internal string Purpose { get; }
    internal string? Stage { get; }
    internal int? RepairCycles { get; }
    internal int? MaxRepairCycles { get; }
    internal string? ConfiguredModel { get; }
    internal string? Effort { get; }
}
