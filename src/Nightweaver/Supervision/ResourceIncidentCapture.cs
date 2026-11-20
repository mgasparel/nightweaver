using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Supervision;

internal sealed class ResourceIncidentCapture(HostPaths paths, ProjectConfig project, IProcessRunner runner)
{
    internal async Task<IncidentCapture> CaptureAsync(ResourceIncidentSignal signal, CancellationToken token)
    {
        if (signal == ResourceIncidentSignal.None) return new(IncidentCaptureState.NotRequired, null, null);
        var outputRoot = paths.BackgroundRoot(project.Name);
        using var pins = InvocationRuntimeFiles.PinExistingRuntimeDirectory(outputRoot);
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "Capture-ResourceIncident.ps1");
        var result = await runner.RunAsync(new("pwsh", ["-NoProfile", "-NonInteractive", "-File", script,
            "-Checkout", project.TargetRepository.Path, "-ProductionInstallation", paths.Root,
            "-Project", project.Name.Value, "-OutputRoot", outputRoot, "-Label", "supervision-" + signal],
            paths.Root, new Dictionary<string, string?>(), SupervisionPolicy.CaptureBudget)
            { MaximumOutputBytes = 16 * 1024 }, token).ConfigureAwait(false);
        // Exit 0 alone is insufficient: the collector retains partial evidence and
        // reports individual source errors separately from its process exit.
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var directory = root.GetProperty("directory").GetString();
            if (directory is null || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), outputRoot,
                    StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith("resources-", StringComparison.Ordinal))
                return new(IncidentCaptureState.Failed, null, "capture-path-unconfirmed");
            var errors = InvocationRuntimeFiles.ReadRuntimeFileBounded(Path.Combine(directory, "capture-errors.json"), 32 * 1024);
            using var errorDocument = JsonDocument.Parse(errors);
            return result.ExitCode == 0 && root.GetProperty("samples").GetInt32() == 3
                && root.GetProperty("errors").GetInt32() == 0
                && !root.GetProperty("applicationMutationsRequested").GetBoolean()
                && errorDocument.RootElement.ValueKind == JsonValueKind.Array && errorDocument.RootElement.GetArrayLength() == 0
                ? new(IncidentCaptureState.Captured, directory, null)
                : new(IncidentCaptureState.Failed, directory, "capture-source-errors");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException
            or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { return new(IncidentCaptureState.Failed, null, "capture-result-unavailable"); }
    }
}
