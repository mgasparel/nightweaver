using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Application;

internal sealed record StartupCleanupDiagnostic(string BeadId, string Path, string Reason);

/// <summary>Reconciles durable completed-environment obligations before admission under repository exclusion.</summary>
internal sealed class StartupEnvironmentCleaner
{
    private readonly CompletedEnvironmentCleanup cleanup;

    internal StartupEnvironmentCleaner(BeadsReader reader, BeadsWriter writer, IProcessRunner processRunner)
    {
        _ = writer; // Startup no longer publishes housekeeping failures into Bead workflow state.
        cleanup = new CompletedEnvironmentCleanup(reader, processRunner);
    }

    internal Task<IReadOnlyList<StartupCleanupDiagnostic>> CleanAsync(HostPaths paths, ProjectSlug project,
        ValidatedTargetRepository repository, string actor, CancellationToken cancellationToken) =>
        cleanup.ReconcileAsync(paths, project, repository, actor, cancellationToken);

    internal static Task<string?> ValidateCheckoutIdentityAsync(IProcessRunner processRunner,
        ValidatedTargetRepository repository, RemoveWorktree candidate, CancellationToken cancellationToken) =>
        CompletedEnvironmentCleanup.ValidateCheckoutIdentityAsync(processRunner, repository, candidate, cancellationToken);
}
