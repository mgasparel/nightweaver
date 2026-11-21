using Nightweaver.Supervision;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Upgrade;

internal enum UpgradeActivationOutcome { Refused, Pending, Failed, PayloadVerifiedStopped, LiveAccepted }
internal sealed record UpgradeActivationResult(string OperationId, UpgradeActivationOutcome Outcome,
    UpgradeProgress? Progress, string Code);

// Internal one-shot composition. Public apply/resume/rollback remain unavailable.
internal sealed class UpgradeCoordinator(UpgradeJournal journal, UpgradeLifecycle lifecycle,
    UpgradeReplacement replacement,
    Func<CancellationToken, Task<UpgradePreflightReport>> preflight,
    Func<AffectedUpgradeProject, string, CancellationToken, Task> start,
    Func<AffectedUpgradeProject, CancellationToken, Task<DeploymentObservation>> observe)
{
    internal static UpgradeCoordinator Create(HostPaths paths, UpgradeJournal journal, bool sourceBoundarySupported,
        Func<CancellationToken, Task<UpgradePreflightReport>> preflight, AppEnvironment environment, IProcessRunner runner)
    {
        var progress = journal.Read().Progress ?? throw new InvalidDataException("Upgrade operation is unavailable.");
        var lifecycle = new UpgradeLifecycle(paths, journal,
            UpgradeLifecycleInventoryReader.Create(paths, progress.Scope, sourceBoundarySupported, runner));
        return new(journal, lifecycle, new(journal, lifecycle), preflight,
            (project, runId, token) => BackgroundManagerApplication.StartUpgradeAsync(paths, project, runId,
                progress.OperationId, progress.NewPackageDigest, environment, token),
            async (project, token) =>
            {
                var config = ProjectConfigLoader.Load(project.Configuration, ProjectSlug.Parse(project.Name), paths);
                var intent = await SupervisionLifecycle.OpenAsync(paths, config, token).ConfigureAwait(false);
                return await DeploymentObserver.Create(paths, config, project.GitCommonDirectory,
                    () => intent?.Read() ?? new(SupervisionAvailability.Manual, null, "", null), progress.OperationId)
                    .ObserveAsync(token).ConfigureAwait(false);
            });
    }

    internal async Task<UpgradeActivationResult> ApplyAsync(long preparedRevision, string candidateRoot, CancellationToken token)
    {
        var read = journal.Read();
        if (read.Progress is not { } progress || read.Availability != UpgradeJournalAvailability.Available
            || progress.Revision != preparedRevision || progress.Phase != UpgradePhase.Prepared)
            throw new InvalidOperationException("A fresh prepared operation is required.");
        var operation = progress.OperationId;
        try
        {
            var report = await preflight(token).ConfigureAwait(false);
            if (!report.Compatible || !report.RollbackEligible || report.Refusals.Length != 0
                || report.InstallationRoot != progress.Scope.Root || report.CandidateRoot != candidateRoot
                || report.OldPackageDigest != progress.OldPackageDigest || report.NewPackageDigest != progress.NewPackageDigest
                || !report.Projects.SequenceEqual(progress.Scope.Projects))
                return new(operation, UpgradeActivationOutcome.Refused, progress, "preflight-unconfirmed");
            progress = await lifecycle.BeginDrainAsync(progress.Revision, token).ConfigureAwait(false);
            progress = await lifecycle.ConfirmSettlementAsync(progress.Revision, token).ConfigureAwait(false);
            progress = await replacement.ReplaceAsync(progress.Revision, candidateRoot, token).ConfigureAwait(false);
            progress = await lifecycle.ActivateAsync(progress.Revision, start, observe, token).ConfigureAwait(false);
            return new(operation, progress.AcceptedPairs?.Length > 0 ? UpgradeActivationOutcome.LiveAccepted
                : UpgradeActivationOutcome.PayloadVerifiedStopped, progress, "accepted");
        }
        catch (OperationCanceledException)
        {
            return new(operation, UpgradeActivationOutcome.Pending, journal.Read().Progress, "inspect-retained-operation");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or TimeoutException)
        {
            // No automatic retry, rollback, launch replay or suppression release.
            read = journal.Read();
            progress = read.Progress!;
            var pending = progress?.PendingOperation is not null || progress?.Phase == UpgradePhase.Draining;
            if (read.Availability == UpgradeJournalAvailability.Available && progress is not null)
            {
                try
                {
                    progress = (await journal.SaveAsync(progress.Revision, progress with { Evidence = [.. progress.Evidence,
                        new(Guid.NewGuid().ToString("N"), UpgradeEvidenceKind.Failure,
                            pending ? "activation-pending" : "activation-failed", DateTimeOffset.UtcNow)] }, token).ConfigureAwait(false)).Progress!;
                }
                catch (Exception failure) when (failure is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                    or OperationCanceledException) { }
            }
            return new(operation, pending ? UpgradeActivationOutcome.Pending : UpgradeActivationOutcome.Failed,
                progress, "inspect-retained-operation");
        }
    }
}
