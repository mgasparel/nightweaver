using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Telemetry;

internal sealed record FactoryTelemetrySnapshot(
    int SchemaVersion,
    string Project,
    string TargetRepository,
    string RuntimeId,
    long Revision,
    DateTimeOffset ObservedAt,
    string RuntimeStatus,
    FactoryPoolObservation Delivery,
    FactoryPoolObservation Merger);

internal sealed record FactoryPoolObservation(int Capacity, IReadOnlyList<FactorySlotObservation> Slots);

internal sealed record FactorySlotObservation(
    string SlotId,
    string Status,
    string? BeadId,
    string? Stage,
    string? Role,
    string? WorktreePath,
    string? Branch,
    int? RepairCycles,
    int? MaxRepairCycles,
    FactoryInvocationObservation? Invocation,
    FactoryDiffObservation? Diff);

internal sealed record FactoryInvocationObservation(
    string BeadId,
    string InvocationId,
    string Purpose,
    string Cli,
    string? ConfiguredModel,
    string Effort,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? DeadlineAt,
    int? ProcessId,
    int? ExitCode);

internal sealed record FactoryDiffObservation(int? Added, int? Deleted, int? Untracked, int? Binary, string Status);

/// <summary>Publishes the live runtime's own observations to a bounded, atomically replaced snapshot.</summary>
internal sealed class FactoryTelemetryPublisher : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        MaxDepth = 16,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly object sync = new();
    private readonly HostPaths paths;
    private readonly ProjectSlug project;
    private readonly int deliveryCapacity;
    private readonly int mergerCapacity;
    private readonly Dictionary<string, FactorySlotObservation> delivery = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FactorySlotObservation> merger = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private readonly string targetRepository;
    private readonly Action<string>? reportFailure;
    private readonly IProcessRunner? processRunner;
    private Task? writer;
    private Task? diffSampler;
    private long revision;
    private string runtimeStatus = "live";
    private bool disposed;

    internal FactoryTelemetryPublisher(HostPaths paths, ProjectConfig project,
        ValidatedTargetRepository repository, int deliveryCapacity, int mergerCapacity,
        Action<string>? reportFailure = null, IProcessRunner? processRunner = null)
    {
        this.paths = paths;
        this.project = project.Name;
        this.deliveryCapacity = deliveryCapacity;
        this.mergerCapacity = mergerCapacity;
        this.reportFailure = reportFailure;
        this.processRunner = processRunner;
        targetRepository = Path.GetFullPath(repository.Path);
        for (var index = 1; index <= deliveryCapacity; index++)
        {
            var slotId = $"delivery-{index}";
            delivery.Add(slotId, EmptySlot(slotId));
        }
        for (var index = 1; index <= mergerCapacity; index++)
        {
            var slotId = $"merger-{index}";
            merger.Add(slotId, EmptySlot(slotId));
        }
    }

    internal string RuntimeId => runtimeId;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WriteSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            ReportFailure(exception);
        }
        writer = Task.Run(() => WriterLoopAsync(stop.Token), CancellationToken.None);
        if (processRunner is not null)
            diffSampler = Task.Run(() => DiffSamplerLoopAsync(stop.Token), CancellationToken.None);
        SignalWriter();
    }

    internal string SlotForReservation(string pool, string beadId)
    {
        lock (sync)
        {
            var slots = GetPool(pool);
            var existing = slots.Values.FirstOrDefault(slot => slot.Status != "idle" && slot.BeadId == beadId);
            if (existing is not null) return existing.SlotId;
            var free = slots.Values.FirstOrDefault(slot => slot.Status == "idle");
            return free?.SlotId ?? throw new InvalidOperationException($"No free {pool} telemetry slot is available.");
        }
    }

    internal void Reserve(string pool, string slotId, string beadId)
    {
        Update(pool, slotId, slot => slot with
        {
            Status = "reserved", BeadId = beadId, Stage = null, Role = null,
            WorktreePath = null, Branch = null, RepairCycles = null, MaxRepairCycles = null,
            Invocation = null, Diff = null
        });
    }

    internal void UpdateSlot(string pool, string slotId, string status, string? beadId = null,
        string? stage = null, string? role = null, string? worktreePath = null,
        int? repairCycles = null, int? maxRepairCycles = null)
    {
        Update(pool, slotId, slot => slot with
        {
            Status = status,
            BeadId = beadId ?? slot.BeadId,
            Stage = stage ?? slot.Stage,
            Role = role ?? slot.Role,
            WorktreePath = worktreePath ?? slot.WorktreePath,
            RepairCycles = repairCycles ?? slot.RepairCycles,
            MaxRepairCycles = maxRepairCycles ?? slot.MaxRepairCycles
        });
    }

    internal void SetInvocation(string pool, string slotId, FactoryInvocationObservation invocation)
    {
        Update(pool, slotId, slot => slot with
        {
            Status = invocation.Status == "running" ? "running" : "settling",
            Invocation = invocation
        });
    }

    internal void ObservePreparedInvocation(string pool, string slotId, string beadId,
        PreparedAgentInvocation invocation)
    {
        Update(pool, slotId, slot => slot with
        {
            Status = "preparing",
            BeadId = beadId,
            Stage = invocation.Stage,
            Role = invocation.Purpose,
            WorktreePath = invocation.WorkingDirectory,
            RepairCycles = invocation.RepairCycles,
            MaxRepairCycles = invocation.MaxRepairCycles,
            Invocation = null
        });
    }

    internal void FinishInvocation(string pool, string slotId, string invocationId, string status, int? exitCode)
    {
        Update(pool, slotId, slot => slot.Invocation?.InvocationId != invocationId ? slot : slot with
        {
            Status = "settling",
            Invocation = slot.Invocation with { Status = status, ExitCode = exitCode }
        });
    }

    private void UpdateDiff(string slotId, string beadId, string? branch, FactoryDiffObservation diff)
    {
        Update("delivery", slotId, slot => slot.BeadId != beadId ? slot : slot with
        {
            Branch = branch,
            Diff = diff
        });
    }

    internal void Release(string pool, string slotId, string beadId)
    {
        Update(pool, slotId, slot => slot with
        {
            Status = "idle", BeadId = null, Stage = null, Role = null, WorktreePath = null,
            Branch = null, RepairCycles = null, MaxRepairCycles = null, Diff = null
        });
    }

    internal void MarkStopped()
    {
        lock (sync) runtimeStatus = "stopped";
        SignalWriter();
    }

    private static FactorySlotObservation EmptySlot(string slotId) => new(
        slotId, "idle", null, null, null, null, null, null, null, null,
        new FactoryDiffObservation(null, null, null, null, "unknown"));

    private Dictionary<string, FactorySlotObservation> GetPool(string pool) => pool switch
    {
        "delivery" => delivery,
        "merger" => merger,
        _ => throw new ArgumentOutOfRangeException(nameof(pool), pool, "Unknown telemetry pool.")
    };

    private void Update(string pool, string slotId, Func<FactorySlotObservation, FactorySlotObservation> change)
    {
        lock (sync)
        {
            if (disposed) return;
            var slots = GetPool(pool);
            if (!slots.TryGetValue(slotId, out var current)) return;
            slots[slotId] = change(current);
            revision++;
        }
        SignalWriter();
    }

    private void SignalWriter()
    {
        if (signal.CurrentCount == 0)
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                try
                {
                    await WriteSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    failureReported = false;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    ReportFailure(exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task DiffSamplerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FactorySlotObservation[] occupied;
                lock (sync) occupied = delivery.Values.Where(slot => slot.Status != "idle"
                    && slot.BeadId is not null && slot.WorktreePath is not null).ToArray();
                await Task.WhenAll(occupied.Select(slot => SampleDiffAsync(slot, cancellationToken))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SampleDiffAsync(FactorySlotObservation slot, CancellationToken cancellationToken)
    {
        if (processRunner is null || slot.WorktreePath is null || slot.BeadId is null) return;
        var path = slot.WorktreePath;
        var timeout = TimeSpan.FromSeconds(2);
        var branchTask = processRunner.RunAsync(new ProcessRequest("git", ["-C", path, "rev-parse", "--abbrev-ref", "HEAD"],
            path, new Dictionary<string, string?>(), timeout), cancellationToken);
        var diffTask = processRunner.RunAsync(new ProcessRequest("git", ["-C", path, "diff", "--numstat", "HEAD", "--"],
            path, new Dictionary<string, string?>(), timeout), cancellationToken);
        var untrackedTask = processRunner.RunAsync(new ProcessRequest("git", ["-C", path, "ls-files", "--others", "--exclude-standard", "-z"],
            path, new Dictionary<string, string?>(), timeout), cancellationToken);
        try
        {
            await Task.WhenAll(branchTask, diffTask, untrackedTask).ConfigureAwait(false);
            var branch = branchTask.Result.ExitCode == 0 ? branchTask.Result.StandardOutput.Trim() : string.Empty;
            var diffResult = diffTask.Result;
            var untrackedResult = untrackedTask.Result;
            if (branchTask.Result.ExitCode != 0 || diffResult.ExitCode != 0 || untrackedResult.ExitCode != 0
                || Encoding.UTF8.GetByteCount(diffResult.StandardOutput) > 1024 * 1024
                || Encoding.UTF8.GetByteCount(untrackedResult.StandardOutput) > 1024 * 1024)
            {
                UpdateDiff(slot.SlotId, slot.BeadId, null,
                    new FactoryDiffObservation(null, null, null, null, "unavailable"));
                return;
            }

            var added = 0;
            var deleted = 0;
            var binary = 0;
            foreach (var line in diffResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var columns = line.TrimEnd('\r').Split('\t', 3);
                if (columns.Length != 3) continue;
                if (columns[0] == "-" || columns[1] == "-") { binary++; continue; }
                if (int.TryParse(columns[0], out var lineAdded)) added = checked(added + lineAdded);
                if (int.TryParse(columns[1], out var lineDeleted)) deleted = checked(deleted + lineDeleted);
            }
            var untracked = untrackedResult.StandardOutput.Count(character => character == '\0');
            UpdateDiff(slot.SlotId, slot.BeadId, branch == "HEAD" ? null : branch,
                new FactoryDiffObservation(added, deleted, untracked, binary, "available"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or InvalidOperationException
            or OverflowException or ArgumentException)
        {
            UpdateDiff(slot.SlotId, slot.BeadId, null,
                new FactoryDiffObservation(null, null, null, null, "unavailable"));
        }
    }

    private bool failureReported;

    private void ReportFailure(Exception exception)
    {
        if (failureReported) return;
        failureReported = true;
        try
        {
            var message = exception.Message.Length <= 300 ? exception.Message : exception.Message[..300];
            reportFailure?.Invoke("Factory telemetry could not publish its snapshot: " + message);
        }
        catch (Exception) { }
    }

    private async Task WriteSnapshotAsync(CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        FactoryTelemetrySnapshot snapshot;
        lock (sync)
        {
            snapshot = new FactoryTelemetrySnapshot(1, project.Value, targetRepository, runtimeId,
                revision, DateTimeOffset.UtcNow, runtimeStatus,
                new FactoryPoolObservation(deliveryCapacity, delivery.Values.OrderBy(slot => slot.SlotId, StringComparer.Ordinal).ToArray()),
                new FactoryPoolObservation(mergerCapacity, merger.Values.OrderBy(slot => slot.SlotId, StringComparer.Ordinal).ToArray()));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        await InvocationRuntimeFiles.WriteFactorySnapshotAtomicallyAsync(paths, project, runtimeId, bytes,
            cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            runtimeStatus = "stopped";
            revision++;
        }
        SignalWriter();
        try { await WriteSnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { }
        lock (sync) disposed = true;
        stop.Cancel();
        if (writer is not null)
        {
            try { await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        if (diffSampler is not null)
        {
            try { await diffSampler.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        stop.Dispose();
        signal.Dispose();
        writeGate.Dispose();
    }
}

internal enum FactoryTelemetryAvailability { Live, Stale, Stopped, Unavailable }

internal sealed record FactoryTelemetryReadResult(
    FactoryTelemetryAvailability Availability,
    string? Reason,
    FactoryTelemetrySnapshot? Snapshot);

internal sealed class FactoryTelemetryReader(HostPaths paths, ProjectConfig project)
{
    private const int MaximumSnapshotBytes = 1024 * 1024;

    internal FactoryTelemetryReadResult Read()
    {
        var path = paths.FactoryTelemetryPath(project.Name);
        try
        {
            using var pinned = OperatingSystem.IsWindows()
                ? InvocationRuntimeFiles.OpenExistingFactorySnapshotForRead(paths, project.Name)
                : null;
            using var portable = pinned is null
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                : null;
            var stream = pinned?.Stream ?? portable!;
            if (stream.Length is <= 0 or > MaximumSnapshotBytes)
                return new(FactoryTelemetryAvailability.Unavailable, "invalid-size", null);
            var bytes = ReadBounded(stream, MaximumSnapshotBytes);
            if (bytes is null)
                return new(FactoryTelemetryAvailability.Unavailable, "invalid-size", null);
            var snapshot = JsonSerializer.Deserialize<FactoryTelemetrySnapshot>(bytes, JsonOptions);
            if (snapshot is null || snapshot.SchemaVersion != 1
                || snapshot.Project is null || snapshot.TargetRepository is null
                || snapshot.RuntimeId is null || snapshot.RuntimeStatus is null
                || snapshot.Delivery is null || snapshot.Merger is null
                || !string.Equals(snapshot.Project, project.Name.Value, StringComparison.Ordinal)
                || !PathEquals(snapshot.TargetRepository, project.TargetRepository.Path)
                || !IsValid(snapshot))
                return new(FactoryTelemetryAvailability.Unavailable, "invalid-or-mismatched", null);
            if (snapshot.RuntimeStatus == "stopped")
                return new(FactoryTelemetryAvailability.Stopped, null, snapshot);
            var age = DateTimeOffset.UtcNow - snapshot.ObservedAt;
            if (age < TimeSpan.FromMinutes(-1) || age > TimeSpan.FromSeconds(10))
                return new(FactoryTelemetryAvailability.Stale, "heartbeat-expired", snapshot);
            return new(FactoryTelemetryAvailability.Live, null, snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or NotSupportedException)
        {
            return new(FactoryTelemetryAvailability.Unavailable, "unreadable-or-corrupt", null);
        }
    }

    private static byte[]? ReadBounded(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream(Math.Min((int)stream.Length, maximumBytes));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maximumBytes + 1 - (int)output.Length;
            if (remaining <= 0) return null;
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
        }
    }

    private bool IsValid(FactoryTelemetrySnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Project is null || snapshot.TargetRepository is null
            || snapshot.RuntimeId is null || snapshot.RuntimeStatus is null
            || snapshot.Delivery is null || snapshot.Merger is null
            || snapshot.RuntimeId.Length is < 16 or > 64 || snapshot.Revision < 0
            || snapshot.Delivery.Capacity != project.Concurrency.MaxConcurrentDelivery
            || snapshot.Merger.Capacity != 1
            || !PathEquals(snapshot.TargetRepository, project.TargetRepository.Path))
            return false;
        if (!ValidPool(snapshot.Delivery, "delivery") || !ValidPool(snapshot.Merger, "merger")) return false;
        return snapshot.RuntimeStatus is "live" or "stopped";
    }

    private static bool ValidPool(FactoryPoolObservation? pool, string prefix)
    {
        if (pool?.Slots is null || pool.Capacity < 1 || pool.Slots.Count != pool.Capacity
            || pool.Slots.Any(slot => slot is null))
            return false;
        var expected = Enumerable.Range(1, pool.Capacity).Select(index => $"{prefix}-{index}").ToHashSet(StringComparer.Ordinal);
        return pool.Slots.All(slot => slot.SlotId is not null && expected.Remove(slot.SlotId)
            && slot.Status is "idle" or "reserved" or "preparing" or "running" or "settling"
            && (slot.BeadId is null || SafeSegment(slot.BeadId))
            && (slot.Invocation is null || SafeInvocation(slot.Invocation)));
    }

    private static bool SafeInvocation(FactoryInvocationObservation? invocation)
        => invocation is not null
            && invocation.BeadId is not null
            && invocation.InvocationId is not null
            && invocation.Purpose is not null
            && invocation.Cli is not null
            && invocation.Effort is not null
            && invocation.Status is not null
            && SafeSegment(invocation.BeadId)
            && invocation.InvocationId.Length is >= 25 and <= 64
            && invocation.InvocationId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            && invocation.Purpose.Length <= 48 && invocation.Cli.Length <= 48
            && (invocation.ConfiguredModel?.Length ?? 0) <= 128 && invocation.Effort.Length <= 32
            && invocation.Status is "running" or "completed" or "failed" or "timed_out" or "cancelled"
                or "launch_failed" or "launch_denied" or "preparation_failed" or "unknown";

    private static bool SafeSegment(string? value) => value is not null && value.Length is > 0 and <= 128
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool PathEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
