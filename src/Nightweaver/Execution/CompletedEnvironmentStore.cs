using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum CleanupStatus { Pending, RetryScheduled, NeedsAttention, Resolved }

internal sealed record CleanupResource(string Path, bool ConfirmedAbsent);

internal sealed record CompletedEnvironmentObligation(
    int Version, string Root, string Project, string Repository, string CommonDirectory,
    string BeadId, WorktreePurpose Purpose, GitWorktreeSnapshot? Registration,
    string? VerifiedHead, string? PullRequest, string? MergeCommit,
    CleanupResource Worktree, CleanupResource Tmp, CleanupStatus Status,
    int Attempts, DateTimeOffset? LastAttempt, DateTimeOffset? NextAttempt,
    DateTimeOffset? ResolvedAt, string? Diagnostic);

internal sealed record CleanupStoreSnapshot(bool Exists, IReadOnlyList<CompletedEnvironmentObligation> Obligations);

/// <summary>Durable housekeeping evidence, independent of runtime telemetry and Bead workflow state.</summary>
internal sealed class CompletedEnvironmentStore(HostPaths paths, ProjectSlug project, ValidatedTargetRepository repository)
{
    internal const int ResolvedHistoryLimit = 100;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal CleanupStoreSnapshot Read()
    {
        var root = RuntimeDirectoryReader.ReadRoot(paths.CleanupRoot(project));
        if (root.State == DirectoryState.Missing) return new(false, []);
        if (root.State != DirectoryState.Present) throw new IOException("Completed-environment cleanup store is unsafe or unavailable.");
        var records = new List<CompletedEnvironmentObligation>();
        foreach (var entry in root.Entries)
        {
            // An interrupted atomic write is evidence, not an empty queue. Preserve it for inspection.
            if (!entry.Path.EndsWith(".json", StringComparison.Ordinal))
                throw new InvalidDataException($"Unexpected cleanup store entry: '{entry.Path}'.");
            var attributes = File.GetAttributes(entry.Path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException($"Unsafe cleanup record: '{entry.Path}'.");
            CompletedEnvironmentObligation record;
            if (OperatingSystem.IsWindows())
            {
                using var pinned = InvocationRuntimeFiles.OpenExistingCleanupRecordForRead(entry.Path);
                record = Deserialize(pinned.Stream, entry.Path);
            }
            else
            {
                using var stream = File.OpenRead(entry.Path);
                record = Deserialize(stream, entry.Path);
            }
            Validate(record);
            if (!string.Equals(Path.GetFileName(entry.Path), record.BeadId + ".json", StringComparison.Ordinal))
                throw new InvalidDataException("Cleanup record filename does not match its Bead identity.");
            records.Add(record);
        }
        if (records.Select(record => record.BeadId).Distinct(RuntimePaths.Comparer).Count() != records.Count)
            throw new InvalidDataException("Ambiguous cleanup obligation identities.");
        return new(true, records.AsReadOnly());
    }

    private static CompletedEnvironmentObligation Deserialize(Stream stream, string path)
    {
        if (stream.Length > 64 * 1024) throw new InvalidDataException($"Oversized cleanup record: '{path}'.");
        return JsonSerializer.Deserialize<CompletedEnvironmentObligation>(stream, Json)
            ?? throw new InvalidDataException($"Null cleanup record: '{path}'.");
    }

    internal void TrimResolvedHistory()
    {
        foreach (var record in Read().Obligations.Where(record => record.Status == CleanupStatus.Resolved)
                     .OrderByDescending(record => record.ResolvedAt).ThenBy(record => record.BeadId, StringComparer.Ordinal)
                     .Skip(ResolvedHistoryLimit))
        {
            var path = Path.Combine(paths.CleanupRoot(project), record.BeadId + ".json");
            if (OperatingSystem.IsWindows())
            {
                using var pinned = InvocationRuntimeFiles.OpenExistingCleanupRecordForRead(path);
                File.Delete(path);
            }
            else File.Delete(path);
        }
    }

    internal async Task WriteAsync(CompletedEnvironmentObligation record, CancellationToken cancellationToken)
    {
        Validate(record);
        // Fail closed on any corrupt/mismatched record; never overwrite evidence to repair a store implicitly.
        _ = Read();
        await InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(
            Path.Combine(paths.CleanupRoot(project), record.BeadId + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(record, Json), cancellationToken).ConfigureAwait(false);
    }

    internal CompletedEnvironmentObligation Create(string beadId, WorktreePurpose purpose,
        GitWorktreeSnapshot? registration, MergerVerifiedMergeEvidence? evidence = null) => new(
            1, RuntimePaths.Normalize(paths.Root), project.Value, RuntimePaths.Normalize(repository.Path),
            RuntimePaths.Normalize(repository.GitCommonDirectory), beadId, purpose, registration,
            evidence?.CheckedHead, evidence?.PullRequest, evidence?.MergeCommit,
            new(Path.Combine(paths.WorktreesRoot(project), beadId), false),
            new(Path.Combine(paths.TmpRoot(project), beadId), false), CleanupStatus.Pending,
            0, null, null, null, null);

    private void Validate(CompletedEnvironmentObligation record)
    {
        if (record.Version != 1 || !WorktreeProvisioner.IsSafeBeadId(record.BeadId)
            || !RuntimePaths.SafeId(record.BeadId)
            || record.Root != RuntimePaths.Normalize(paths.Root) || record.Project != project.Value
            || record.Repository != RuntimePaths.Normalize(repository.Path)
            || record.CommonDirectory != RuntimePaths.Normalize(repository.GitCommonDirectory)
            || RuntimePaths.Comparer.Equals(record.CommonDirectory, record.Repository)
            || record.Worktree.Path != Path.Combine(paths.WorktreesRoot(project), record.BeadId)
            || record.Tmp.Path != Path.Combine(paths.TmpRoot(project), record.BeadId)
            || WorktreeProvisioner.PathsOverlap(record.Worktree.Path, repository.Path)
            || WorktreeProvisioner.PathsOverlap(record.Tmp.Path, repository.Path)
            || WorktreeProvisioner.PathsOverlap(record.Worktree.Path, repository.GitCommonDirectory)
            || WorktreeProvisioner.PathsOverlap(record.Tmp.Path, repository.GitCommonDirectory)
            || record.Purpose is not (WorktreePurpose.Delivery or WorktreePurpose.Planning)
            || !Enum.IsDefined(record.Status) || record.Attempts < 0
            || (record.Attempts == 0) != (record.LastAttempt is null)
            || record.Diagnostic?.Length > 384
            || (record.Status == CleanupStatus.Resolved) != (record.ResolvedAt is not null)
            || record.Status == CleanupStatus.Resolved && (!record.Worktree.ConfirmedAbsent || !record.Tmp.ConfirmedAbsent)
            || (record.Status == CleanupStatus.RetryScheduled) != (record.NextAttempt is not null))
            throw new InvalidDataException("Cleanup obligation identity, version, or resource progress is invalid for the current deployment/Project/repository.");
        if (record.Registration is { } registration
            && (registration.Path != record.Worktree.Path || registration.Main || registration.Bare
                || string.IsNullOrWhiteSpace(registration.Head)
                || record.Purpose == WorktreePurpose.Delivery
                    && (registration.Detached || registration.Branch != $"refs/heads/nightweaver/{record.BeadId}")
                || record.Purpose == WorktreePurpose.Planning && (!registration.Detached || registration.Branch is not null)
                || record.VerifiedHead is not null && !string.Equals(record.VerifiedHead, registration.Head, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Cleanup obligation registration does not match its owned environment.");
        if ((record.VerifiedHead is null) != (record.PullRequest is null)
            || (record.VerifiedHead is null) != (record.MergeCommit is null))
            throw new InvalidDataException("Cleanup completion evidence is incomplete.");
    }
}
