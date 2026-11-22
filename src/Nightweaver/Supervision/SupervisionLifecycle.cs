using System.ComponentModel;
using System.Security.Principal;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Supervision;

/// <summary>Operator intent only. Never initializes supervision or grants process/repository authority.</summary>
internal sealed class SupervisionLifecycle(SupervisionStore store)
{
    internal SupervisionStore Store => store;
    internal static async Task<SupervisionLifecycle?> OpenAsync(HostPaths paths, ProjectConfig project,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Manual deployments incur no Git probe and no new supervision files. Access denial,
        // reparse paths and interrupted stores must not be mistaken for manual opt-out.
        try
        {
            using var pins = InvocationRuntimeFiles.PinExistingRuntimeDirectory(paths.SupervisionRoot(project.Name));
        }
        catch (IOException exception) when (exception is DirectoryNotFoundException or FileNotFoundException
            || exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 }) { return null; }

        return await ForInstallationAsync(paths, project, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SupervisionLifecycle> ForInstallationAsync(HostPaths paths, ProjectConfig project,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var repository = await new TargetRepositoryValidator(new SystemProcessRunner())
            .ValidateAsync(project.TargetRepository, project.ConfigFilePath, cancellationToken).ConfigureAwait(false);
        // Reject filesystem aliases rather than trusting a serialized/common-directory spelling.
        using var repositoryPins = InvocationRuntimeFiles.PinExistingRuntimeDirectory(repository.GitCommonDirectory);
        using var user = WindowsIdentity.GetCurrent();
        var identity = new SupervisionIdentity(paths.Root, project.ConfigFilePath,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.GitCommonDirectory)),
            user.User?.Value ?? throw new InvalidDataException("Current Windows SID unavailable."));
        return new(new SupervisionStore(paths, project.Name, identity));
    }

    internal SupervisionRead Read() => store.Read();

    internal async Task<SupervisionRead> ApplyAsync(SupervisionLifecycleAction action, string? token,
        long? revision, CancellationToken cancellationToken)
    {
        var read = store.Read();
        if (read.Availability == SupervisionAvailability.Unavailable)
            throw new InvalidDataException($"Supervision unavailable at '{read.Path}': {read.Diagnostic}");
        if (read.State is not { } state)
        {
            if (action is SupervisionLifecycleAction.Start or SupervisionLifecycleAction.Stop) return read;
            throw new InvalidOperationException("Maintenance requires an existing supervision installation.");
        }
        var next = Transition(state, action, token, revision);
        try
        {
            await store.SaveAsync(state.Revision, next, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or OperationCanceledException)
        {
            // A failed write is not a durable stop. Retain a separate fail-closed interlock,
            // even if another writer owns writer.lock or the old JSON still says running.
            if (action == SupervisionLifecycleAction.Stop)
                await store.LatchUncertainStopAsync().ConfigureAwait(false);
            throw;
        }
        var confirmed = store.Read();
        if (confirmed.State?.Revision != state.Revision + 1)
            throw new InvalidDataException("Supervision intent publication is uncertain; inspect the retained store before recovery.");
        return confirmed;
    }

    internal static SupervisionState Transition(SupervisionState state, SupervisionLifecycleAction action,
        string? token = null, long? revision = null)
    {
        switch (action)
        {
            case SupervisionLifecycleAction.Stop:
                return SupervisionPolicy.SetIntent(state, state.Enabled, false, state.MaintenanceToken);
            case SupervisionLifecycleAction.Start:
                if (state.MaintenanceToken is not null || state.PendingAttempt is not null)
                    throw new InvalidOperationException("Release maintenance and reconcile the pending attempt before explicit start.");
                return SupervisionPolicy.SetIntent(state, state.Enabled, true, null);
            case SupervisionLifecycleAction.EnterMaintenance:
                if (state.MaintenanceToken is not null)
                    throw new InvalidOperationException("Maintenance is already active; inspect its current token and revision.");
                return SupervisionPolicy.SetIntent(state, state.Enabled, state.DesiredRunning, Guid.NewGuid().ToString("N"));
            case SupervisionLifecycleAction.LeaveMaintenance:
                if (token is null || token != state.MaintenanceToken || revision != state.Revision)
                    throw new InvalidOperationException("Maintenance token/revision changed; inspect status before releasing the current operation.");
                // Preserve current intent: an intervening stop/disable can never be reversed.
                return SupervisionPolicy.SetIntent(state, state.Enabled, state.DesiredRunning, null);
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}

internal enum SupervisionLifecycleAction { Start, Stop, EnterMaintenance, LeaveMaintenance }
