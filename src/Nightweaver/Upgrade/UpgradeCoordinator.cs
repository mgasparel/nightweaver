using Nightweaver.Supervision;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Upgrade;

internal enum UpgradeActivationOutcome { Refused, Pending, Failed, PayloadVerifiedStopped, LiveAccepted }
internal sealed record UpgradeActivationResult(string OperationId, UpgradeActivationOutcome Outcome,
    UpgradeProgress? Progress, string Code);

internal sealed record UpgradeCoordinatorOperations(
    Func<UpgradeJournalRead> ReadProgress,
    Func<long, UpgradeProgress, CancellationToken, Task<UpgradeJournalRead>> SaveProgress,
    Action<UpgradeProgress> VerifyRecoveryManager,
    Func<CancellationToken, Task<UpgradePreflightReport>> Preflight,
    Func<long, CancellationToken, Task<UpgradeProgress>> BeginDrain,
    Func<long, CancellationToken, Task<UpgradeProgress>> ConfirmSettlement,
    Func<long, string, CancellationToken, Task<UpgradeProgress>> ReplacePayload,
    Func<long, string, CancellationToken, Task<UpgradeProgress>> ResumeReplacement,
    Func<long, bool, CancellationToken, Task<UpgradeProgress>> Activate,
    Func<long, CancellationToken, Task<UpgradeProgress>> ConfirmAccepted);

// Internal one-shot composition. Public apply/resume/rollback remain unavailable.
internal sealed class UpgradeCoordinator(UpgradeCoordinatorOperations operations)
{
    internal async Task<UpgradeActivationResult> ResumeAsync(long expectedRevision, string candidateRoot, CancellationToken token)
    {
        var read = operations.ReadProgress();
        if (read.Availability != UpgradeJournalAvailability.Available || read.Progress is not { } progress
            || progress.Revision != expectedRevision)
            return new(read.Progress?.OperationId ?? "", UpgradeActivationOutcome.Refused, read.Progress, "Operation revision is unavailable or changed.");
        try
        {
            operations.VerifyRecoveryManager(progress);
            if (progress.Phase == UpgradePhase.Prepared)
                return await ApplyAsync(expectedRevision, candidateRoot, token).ConfigureAwait(false);
            if (progress.Phase == UpgradePhase.Draining)
                progress = await operations.ConfirmSettlement(progress.Revision, token).ConfigureAwait(false);
            if (progress.Phase == UpgradePhase.Settled)
                progress = await operations.ReplacePayload(progress.Revision, candidateRoot, token).ConfigureAwait(false);
            else if (progress.Phase == UpgradePhase.Replacing)
                progress = await operations.ResumeReplacement(progress.Revision, candidateRoot, token).ConfigureAwait(false);
            if (progress.Phase is UpgradePhase.Installed or UpgradePhase.Verifying)
                progress = await operations.Activate(progress.Revision, true, token).ConfigureAwait(false);
            else if (progress.Phase == UpgradePhase.Accepted)
                progress = await operations.ConfirmAccepted(progress.Revision, token).ConfigureAwait(false);
            if (progress.Phase != UpgradePhase.Accepted)
                return new(progress.OperationId, UpgradeActivationOutcome.Refused, progress,
                    progress.Phase == UpgradePhase.Inspected ? "Prepared operation evidence is required." : "Use rollback reconciliation for this phase.");
            return new(progress.OperationId, progress.AcceptedPairs?.Length > 0 ? UpgradeActivationOutcome.LiveAccepted
                : UpgradeActivationOutcome.PayloadVerifiedStopped, progress, "accepted");
        }
        catch (OperationCanceledException)
        {
            return new(progress.OperationId, UpgradeActivationOutcome.Pending, operations.ReadProgress().Progress, "Resume was interrupted. Inspect the retained operation.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or TimeoutException)
        {
            return new(progress.OperationId, UpgradeActivationOutcome.Pending, operations.ReadProgress().Progress, exception.Message);
        }
    }

    internal static UpgradeCoordinator Create(HostPaths paths, UpgradeJournal journal, bool sourceBoundarySupported,
        Func<CancellationToken, Task<UpgradePreflightReport>> preflight, AppEnvironment environment, IProcessRunner runner)
    {
        var progress = journal.Read().Progress ?? throw new InvalidDataException("Upgrade operation is unavailable.");
        var lifecycle = new UpgradeLifecycle(paths, journal,
            UpgradeLifecycleInventoryReader.Create(paths, progress.Scope, sourceBoundarySupported, runner));
        var replacement = new UpgradeReplacement(journal, lifecycle);

        Task StartAsync(AffectedUpgradeProject project, string runId, CancellationToken token) =>
            BackgroundManagerApplication.StartUpgradeAsync(paths, project, runId,
                progress.OperationId, progress.NewPackageDigest, environment, token);

        async Task<DeploymentObservation> ObserveAsync(AffectedUpgradeProject project, CancellationToken token)
        {
            var config = ProjectConfigLoader.Load(project.Configuration, ProjectSlug.Parse(project.Name), paths);
            var intent = await SupervisionLifecycle.OpenAsync(paths, config, token).ConfigureAwait(false);
            return await DeploymentObserver.Create(paths, config, project.GitCommonDirectory,
                () => intent?.Read() ?? new(SupervisionAvailability.Manual, null, "", null), progress.OperationId)
                .ObserveAsync(token).ConfigureAwait(false);
        }

        return new(new(journal.Read, (revision, next, token) => journal.SaveAsync(revision, next, token),
            VerifyRecoveryManager, preflight, lifecycle.BeginDrainAsync, lifecycle.ConfirmSettlementAsync,
            replacement.ReplaceAsync, replacement.ResumeAsync,
            (revision, resume, token) => lifecycle.ActivateAsync(revision, StartAsync, ObserveAsync, token, resume),
            (revision, token) => lifecycle.ConfirmAcceptedAsync(revision, ObserveAsync, token)));
    }

    private static void VerifyRecoveryManager(UpgradeProgress progress)
    {
        using var manager = Execution.InvocationRuntimeFiles.OpenExistingPackageFileForRead(typeof(UpgradeCoordinator).Assembly.Location);
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manager.Stream)) != progress.ManagerDigest)
            throw new InvalidDataException("Recovery manager changed.");
    }

    internal async Task<UpgradeActivationResult> ApplyAsync(long preparedRevision, string candidateRoot, CancellationToken token)
    {
        var read = operations.ReadProgress();
        if (read.Progress is not { } progress || read.Availability != UpgradeJournalAvailability.Available
            || progress.Revision != preparedRevision || progress.Phase != UpgradePhase.Prepared)
            throw new InvalidOperationException("A fresh prepared operation is required.");
        var operation = progress.OperationId;
        try
        {
            var report = await operations.Preflight(token).ConfigureAwait(false);
            if (!report.Compatible || !report.RollbackEligible || report.Refusals.Length != 0
                || report.InstallationRoot != progress.Scope.Root || report.CandidateRoot != candidateRoot
                || report.OldPackageDigest != progress.OldPackageDigest || report.NewPackageDigest != progress.NewPackageDigest
                || !report.Projects.SequenceEqual(progress.Scope.Projects))
                return new(operation, UpgradeActivationOutcome.Refused, progress, "preflight-unconfirmed");
            progress = await operations.BeginDrain(progress.Revision, token).ConfigureAwait(false);
            progress = await operations.ConfirmSettlement(progress.Revision, token).ConfigureAwait(false);
            progress = await operations.ReplacePayload(progress.Revision, candidateRoot, token).ConfigureAwait(false);
            progress = await operations.Activate(progress.Revision, false, token).ConfigureAwait(false);
            return new(operation, progress.AcceptedPairs?.Length > 0 ? UpgradeActivationOutcome.LiveAccepted
                : UpgradeActivationOutcome.PayloadVerifiedStopped, progress, "accepted");
        }
        catch (OperationCanceledException)
        {
            return new(operation, UpgradeActivationOutcome.Pending, operations.ReadProgress().Progress, "inspect-retained-operation");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or TimeoutException)
        {
            // No automatic retry, rollback, launch replay or suppression release.
            read = operations.ReadProgress();
            progress = read.Progress!;
            var pending = progress?.PendingOperation is not null || progress?.Phase == UpgradePhase.Draining;
            if (read.Availability == UpgradeJournalAvailability.Available && progress is not null)
            {
                try
                {
                    progress = (await operations.SaveProgress(progress.Revision, progress with { Evidence = [.. progress.Evidence,
                        new(Guid.NewGuid().ToString("N"), UpgradeEvidenceKind.Failure,
                            pending ? "activation-pending" : "activation-failed", DateTimeOffset.UtcNow)] }, token).ConfigureAwait(false)).Progress!;
                }
                catch (Exception failure) when (failure is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                    or OperationCanceledException) { }
            }
            return new(operation, pending ? UpgradeActivationOutcome.Pending : UpgradeActivationOutcome.Failed,
                progress, exception.Message);
        }
    }
}
