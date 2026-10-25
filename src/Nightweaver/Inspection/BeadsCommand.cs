namespace Nightweaver.Inspection;

internal static class BeadsCommand
{
    internal static readonly string[] ScrubbedVariables =
    [ "BEADS_DIR", "BEADS_DB", "BD_DB", "BEADS_DOLT_SERVER_DATABASE", "BEADS_DOLT_DATA_DIR",
            "BEADS_DOLT_SERVER_MODE", "BEADS_DOLT_SHARED_SERVER",
            "BEADS_MEM_PROFILE", "BEADS_MEM_STATS", "BD_CPU_PROFILE", "BD_MEM_PROFILE", "BD_TRACE" ];

    internal static ProcessRequest Create(string workingDirectory)
        => Create(workingDirectory,
            ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"]);

    internal static ProcessRequest CreateUnassignedReadiness(string workingDirectory)
        => Create(workingDirectory, ["--readonly", "--sandbox", "ready", "--unassigned", "--limit", "0", "--json"]);

    internal static ProcessRequest CreateExplorerReadiness(string workingDirectory)
        => Create(workingDirectory, ["--readonly", "--sandbox", "ready", "--limit", "0", "--json"]);

    internal static ProcessRequest CreateExplorerBlocked(string workingDirectory)
        => Create(workingDirectory, ["--readonly", "--sandbox", "blocked", "--json"]);

    internal static ProcessRequest CreateRecoverableReadiness(string workingDirectory, string recoveryActor)
        => Create(workingDirectory, ["--readonly", "--sandbox", "list", "--ready", "--status", "in_progress", "--assignee", recoveryActor, "--limit", "0", "--json"]);

    internal static ProcessRequest CreateUpDependencies(string workingDirectory, string beadId)
        => Create(workingDirectory, ["--readonly", "--sandbox", "dep", "list", beadId, "--direction", "up", "--json"]);

    internal static ProcessRequest CreateWrite(string workingDirectory, IReadOnlyList<string> arguments, string? actor = null)
    {
        var environment = CreateEnvironment(workingDirectory, readOnly: false);
        var actorArguments = actor is null ? Array.Empty<string>() : new[] { "--actor", actor };
        return new ProcessRequest("bd", ["--sandbox", .. actorArguments, "--dolt-auto-commit", "on", .. arguments], workingDirectory,
            environment, TimeSpan.FromSeconds(30));
    }

    private static ProcessRequest Create(string workingDirectory, IReadOnlyList<string> arguments)
        => new("bd", arguments, workingDirectory, CreateEnvironment(workingDirectory, readOnly: true), TimeSpan.FromSeconds(30));

    private static Dictionary<string, string?> CreateEnvironment(string workingDirectory, bool readOnly)
    {
        var environment = new Dictionary<string, string?>(GitCommand.Create([], workingDirectory).Environment, StringComparer.OrdinalIgnoreCase);
        // bd 1.3.1: selectors in main.go/configfile; remove ambient database routing.
        // Keep server host/port/TLS/credentials, which may be needed by the target.
        foreach (var name in ScrubbedVariables)
        {
            environment[name] = null;
        }
        // Keep tracker routing local and suppress automatic exports/pushes for both reads and writes.
        foreach (var name in new[] { "BD_IMPORT_AUTO", "BD_EXPORT_AUTO", "BD_BACKUP_ENABLED", "BD_DOLT_AUTO_PUSH", "BD_EVENTS_EXPORT" })
        {
            environment[name] = "false";
        }
        // bd metrics can create files and launch a detached flusher.
        environment["BD_DISABLE_METRICS"] = "1";
        // Read-only inspections suppress journaling. Escalation writes retain normal journal behavior.
        environment["BD_DISABLE_EVENT_FLUSH"] = readOnly ? "1" : null;
        environment["OTEL_SDK_DISABLED"] = "true";
        environment["BD_OTEL_STDOUT"] = "false";
        environment["BD_DOLT_AUTO_COMMIT"] = readOnly ? "off" : "on";
        environment["BD_NO_HOOKS"] = "true";
        environment["BD_NO_DB"] = "false";
        environment["BD_DOLT_AUTO_START"] = "false";
        return environment;
    }
}
