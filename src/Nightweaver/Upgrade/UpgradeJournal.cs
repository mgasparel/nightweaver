using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Execution;
using Nightweaver.Supervision;

namespace Nightweaver.Upgrade;

internal sealed record UpgradeScope(string Root, string UserSid, AffectedUpgradeProject[] Projects);
internal sealed record UpgradeMaintenance(string Project, string? Token, long Generation, long Revision);
internal sealed record UpgradeOwner(string Project, int ProcessId, DateTimeOffset CreatedUtc,
    string RunId, string OwnerId, string RuntimeId, string JobName);
internal enum UpgradeFileResult { Pending, Old, New, Removed, Unknown }
internal sealed record UpgradeFileProgress(string Path, string IntentId, string? ExpectedHash,
    string? ObservedHash, UpgradeFileResult Result);
internal enum UpgradeEvidenceKind { Failure, Recovery, RunningAcceptance, StoppedAcceptance }
internal sealed record UpgradeEvidence(string Id, UpgradeEvidenceKind Kind, string Code, DateTimeOffset Utc);
internal sealed record UpgradeProgress(int Version, string OperationId, long Revision, UpgradeScope Scope,
    string OldPackageDigest, string NewPackageDigest, string ManagerDigest, UpgradePhase Phase,
    string? PendingOperation, string? LastConfirmedOperation, string BackupId,
    UpgradeMaintenance[] Maintenance, UpgradeOwner[] Owners, UpgradeFileProgress[] Files, UpgradeEvidence[] Evidence);
internal enum UpgradeJournalAvailability { Missing, Available, Unavailable }
internal enum UpgradePublicationBoundary { Flushed, Renamed }
internal sealed record UpgradeJournalRead(UpgradeJournalAvailability Availability, UpgradeProgress? Progress,
    string Path, string? Diagnostic);

/// <summary>Historical evidence only. No payload, lifecycle, interlock or cleanup operations.
/// Each revision is immutable. An interrupted sibling blocks all further writes.</summary>
internal sealed class UpgradeJournal
{
    internal const int MaximumBytes = 1024 * 1024;
    private readonly UpgradeScope scope;
    private readonly string operationId;
    private readonly string directory;
    private readonly Action<UpgradePublicationBoundary>? publicationBoundary;
    private static readonly JsonSerializerOptions Options = new(PreparedRelease.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter<UpgradePhase>(allowIntegerValues: false),
            new JsonStringEnumConverter<UpgradeFileResult>(allowIntegerValues: false),
            new JsonStringEnumConverter<UpgradeEvidenceKind>(allowIntegerValues: false) }
    };

    internal UpgradeJournal(UpgradeScope scope, string operationId,
        Action<UpgradePublicationBoundary>? publicationBoundary = null)
    {
        ValidateScope(scope);
        SupervisionValidation.ValidateId(operationId);
        // Snapshot mutable caller arrays before retaining the expected binding.
        this.scope = JsonSerializer.Deserialize<UpgradeScope>(JsonSerializer.Serialize(scope, Options), Options)!;
        this.operationId = operationId;
        this.publicationBoundary = publicationBoundary;
        directory = System.IO.Path.Combine(scope.Root, "run", "upgrade", "operations", operationId);
    }

    internal string DirectoryPath => directory;
    internal string RetainedPackageRoot => System.IO.Path.Combine(scope.Root, "run", "upgrade", "packages", operationId);

    internal UpgradeJournalRead Read()
    {
        try
        {
            using var pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
            var entries = Directory.EnumerateFileSystemEntries(directory).Take(4098).ToArray();
            var revisions = entries.Where(p => System.IO.Path.GetFileName(p) != "writer.lock")
                .Order(StringComparer.Ordinal).ToArray();
            if (revisions.Length == 0)
                throw new InvalidDataException("Operation directory has no committed evidence.");
            if (revisions.Length > 4096) throw new InvalidDataException("Upgrade revision budget exceeded.");
            UpgradeProgress? previous = null;
            for (var i = 0; i < revisions.Length; i++)
            {
                if (System.IO.Path.GetFileName(revisions[i]) != RevisionName(i + 1))
                    throw new InvalidDataException("Interrupted or unknown upgrade publication; retain all evidence.");
                var bytes = InvocationRuntimeFiles.ReadRuntimeFileBounded(revisions[i], MaximumBytes);
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                PreparedRelease.RejectDuplicateProperties(json.RootElement);
                var progress = JsonSerializer.Deserialize<UpgradeProgress>(bytes, Options)
                    ?? throw new InvalidDataException("Missing upgrade progress.");
                Validate(progress);
                if (progress.Revision != i + 1 || progress.OperationId != operationId || !Same(progress.Scope, scope))
                    throw new InvalidDataException("Foreign upgrade identity or revision.");
                if (previous is not null) ValidateContinuation(previous, progress);
                previous = progress;
            }
            return new(UpgradeJournalAvailability.Available, previous, directory, null);
        }
        catch (IOException exception) when (IsMissing(exception))
        {
            // Only absence of the operation directory is Missing. A lost revision is unavailable.
            try
            {
                using var pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
                return new(UpgradeJournalAvailability.Unavailable, null, directory, "Upgrade evidence is missing.");
            }
            catch (IOException missing) when (IsMissing(missing))
            { return new(UpgradeJournalAvailability.Missing, null, directory, null); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        { return new(UpgradeJournalAvailability.Unavailable, null, directory, exception.Message); }
    }

    internal async Task<UpgradeJournalRead> SaveAsync(long? expectedRevision, UpgradeProgress next,
        CancellationToken cancellationToken)
    {
        Validate(next);
        if (next.OperationId != operationId || !Same(next.Scope, scope) || next.Revision != (expectedRevision ?? 0))
            throw new InvalidDataException("Candidate identity or revision does not match.");
        // A directory created by a failed first save is evidence, not a fresh operation.
        var before = Read();
        if (before.Availability == UpgradeJournalAvailability.Unavailable)
            throw new InvalidDataException(before.Diagnostic);
        using var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
            System.IO.Path.Combine(directory, "writer.lock"), createMissingDirectories: true);
        var current = Read();
        // The new writer.lock alone is allowed only for the first publisher that saw absence.
        var first = expectedRevision is null && before.Availability == UpgradeJournalAvailability.Missing
            && Directory.EnumerateFileSystemEntries(directory).All(p => System.IO.Path.GetFileName(p) == "writer.lock");
        if (!first && current.Availability != UpgradeJournalAvailability.Available)
            throw new InvalidDataException(current.Diagnostic);
        if (current.Progress?.Revision != expectedRevision)
            throw new InvalidOperationException("Upgrade revision changed; reread before publication.");
        var committed = next with { Revision = checked((expectedRevision ?? 0) + 1) };
        if (committed.Revision > 4096) throw new InvalidDataException("Upgrade revision budget exceeded.");
        if (current.Progress is { } previous) ValidateContinuation(previous, committed);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(committed, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Upgrade metadata budget exceeded.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var destination = System.IO.Path.Combine(directory, RevisionName(committed.Revision));
        var pending = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // Never remove an uncertain publication, even on cancellation or sharing failure.
        using var pins = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
        await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        publicationBoundary?.Invoke(UpgradePublicationBoundary.Flushed);
        deadline.Token.ThrowIfCancellationRequested();
        File.Move(pending, destination, overwrite: false);
        publicationBoundary?.Invoke(UpgradePublicationBoundary.Renamed);
        // A successful return is still evidence only. Consumers must inspect durable state and
        // revalidate live facts under boundary locks before any filesystem or process action.
        var read = Read();
        if (read.Availability != UpgradeJournalAvailability.Available || !Same(read.Progress, committed))
            throw new IOException("Upgrade publication is uncertain; inspect retained evidence.");
        return read;
    }

    private static string RevisionName(long revision) => $"{revision:D8}.json";
    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);
    private static bool IsMissing(IOException exception) => exception is FileNotFoundException or DirectoryNotFoundException
        || exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static void ValidateScope(UpgradeScope scope)
    {
        if (scope is null || scope.Projects is null || scope.Projects.Length is < 1 or > 256)
            throw new InvalidDataException("Missing affected Project inventory.");
        PreparedRelease.CanonicalRoot(scope.Root);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in scope.Projects)
        {
            if (project is null || !names.Add(project.Name) || string.IsNullOrEmpty(project.Name)
                || project.Name.Length > 80 || !Hash(project.ConfigurationDigest)
                || string.IsNullOrEmpty(project.PhysicalRepositoryIdentity) || project.PhysicalRepositoryIdentity.Length > 256)
                throw new InvalidDataException("Invalid affected Project inventory.");
            SupervisionValidation.Validate(new SupervisionIdentity(scope.Root, project.Configuration, project.GitCommonDirectory, scope.UserSid));
            PreparedRelease.CanonicalRoot(project.Repository);
            PreparedRelease.CanonicalRoot(project.GitCommonDirectory);
        }
    }

    private static void Validate(UpgradeProgress progress)
    {
        ValidateScope(progress.Scope);
        SupervisionValidation.ValidateId(progress.OperationId);
        SupervisionValidation.ValidateId(progress.BackupId);
        if (progress.Version != 1 || progress.Revision < 0 || !Hash(progress.OldPackageDigest)
            || !Hash(progress.NewPackageDigest) || !Hash(progress.ManagerDigest) || !Enum.IsDefined(progress.Phase)
            || progress.Maintenance is null || progress.Maintenance.Length > 256 || progress.Owners is null
            || progress.Owners.Length > 256 || progress.Files is null || progress.Files.Length > 4096
            || progress.Evidence is null || progress.Evidence.Length > 256)
            throw new InvalidDataException("Invalid upgrade v1 progress.");
        if (progress.PendingOperation is not null) SupervisionValidation.ValidateId(progress.PendingOperation);
        if (progress.LastConfirmedOperation is not null) SupervisionValidation.ValidateId(progress.LastConfirmedOperation);
        var projects = progress.Scope.Projects.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var maintenance = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in progress.Maintenance)
        {
            if (item is null || !projects.Contains(item.Project) || !maintenance.Add(item.Project)
                || item.Generation < 0 || item.Revision < 0) throw new InvalidDataException("Invalid maintenance evidence.");
            if (item.Token is not null) SupervisionValidation.ValidateId(item.Token);
        }
        foreach (var owner in progress.Owners)
        {
            if (owner is null || !projects.Contains(owner.Project) || owner.ProcessId <= 0
                || owner.CreatedUtc.Offset != TimeSpan.Zero || owner.CreatedUtc == default
                || string.IsNullOrEmpty(owner.JobName) || owner.JobName.Length > 256 || owner.JobName.Any(char.IsControl))
                throw new InvalidDataException("Invalid historical native owner.");
            SupervisionValidation.ValidateId(owner.RunId);
            SupervisionValidation.ValidateId(owner.OwnerId);
            SupervisionValidation.ValidateId(owner.RuntimeId);
        }
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in progress.Files)
        {
            if (file is null || !PreparedRelease.SafePayloadPath(file.Path) || !files.Add(file.Path)
                || file.ExpectedHash is not null && !Hash(file.ExpectedHash)
                || file.ObservedHash is not null && !Hash(file.ObservedHash) || !Enum.IsDefined(file.Result))
                throw new InvalidDataException("Invalid per-file evidence.");
            SupervisionValidation.ValidateId(file.IntentId);
        }
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in progress.Evidence)
        {
            if (evidence is null || !evidenceIds.Add(evidence.Id) || !Enum.IsDefined(evidence.Kind)
                || evidence.Utc.Offset != TimeSpan.Zero || evidence.Utc == default || string.IsNullOrEmpty(evidence.Code)
                || evidence.Code.Length > 80 || evidence.Code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new InvalidDataException("Invalid allowlisted upgrade evidence.");
            SupervisionValidation.ValidateId(evidence.Id);
        }
    }

    private static void ValidateContinuation(UpgradeProgress old, UpgradeProgress next)
    {
        if (old.OperationId != next.OperationId || !Same(old.Scope, next.Scope)
            || old.OldPackageDigest != next.OldPackageDigest || old.NewPackageDigest != next.NewPackageDigest
            || old.ManagerDigest != next.ManagerDigest || old.BackupId != next.BackupId
            || old.Evidence.Any(e => !next.Evidence.Contains(e))
            || old.Owners.Any(o => !next.Owners.Contains(o)))
            throw new InvalidDataException("Upgrade binding or retained evidence changed.");
    }
}
