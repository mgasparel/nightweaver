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

    internal static ProcessRequest CreateRecoverableReadiness(string workingDirectory, string recoveryActor)
        => Create(workingDirectory, ["--readonly", "--sandbox", "list", "--ready", "--status", "in_progress", "--assignee", recoveryActor, "--limit", "0", "--json"]);

    private static ProcessRequest Create(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var environment = new Dictionary<string, string?>(GitCommand.Create([], workingDirectory).Environment, StringComparer.OrdinalIgnoreCase);
        // bd 1.3.1: selectors in main.go/configfile; remove ambient database routing.
        // Keep server host/port/TLS/credentials, which may be needed by the target.
        foreach (var name in ScrubbedVariables)
        {
            environment[name] = null;
        }
        // Strict --readonly disables bootstrap/migration/maintenance. Defense in depth
        // for the independently configurable automatic output and mutation settings.
        foreach (var name in new[] { "BD_IMPORT_AUTO", "BD_EXPORT_AUTO", "BD_BACKUP_ENABLED", "BD_DOLT_AUTO_PUSH", "BD_EVENTS_EXPORT" })
        {
            environment[name] = "false";
        }
        // bd metrics can create files and launch a detached flusher even for reads.
        environment["BD_DISABLE_METRICS"] = "1";
        environment["BD_DISABLE_EVENT_FLUSH"] = "1";
        environment["OTEL_SDK_DISABLED"] = "true";
        environment["BD_OTEL_STDOUT"] = "false";
        environment["BD_DOLT_AUTO_COMMIT"] = "off";
        environment["BD_NO_HOOKS"] = "true";
        environment["BD_NO_DB"] = "false";
        environment["BD_DOLT_AUTO_START"] = "false";
        return new ProcessRequest("bd", arguments, workingDirectory, environment, TimeSpan.FromSeconds(30));
    }
}
