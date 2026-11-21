using System.Security.Cryptography;
using Nightweaver.Execution;

namespace Nightweaver.Upgrade;

internal enum UpgradeReplacementBoundary { BeforeBackup, AfterBackup, BeforeWrite, AfterWrite, BeforeRemove, AfterRemove, BeforeConfirm, AfterConfirm }

/// <summary>Internal, one-shot replacement. Recovery and activation belong to the composed manager.</summary>
internal sealed class UpgradeReplacement(UpgradeJournal journal, UpgradeLifecycle lifecycle,
    Action<UpgradeReplacementBoundary, string>? boundary = null)
{
    internal async Task<UpgradeProgress> ReplaceAsync(long expectedRevision, string candidateRoot, CancellationToken token)
        => await ReplaceCoreAsync(expectedRevision, candidateRoot, false, token).ConfigureAwait(false);

    internal Task<UpgradeProgress> ResumeAsync(long expectedRevision, string candidateRoot, CancellationToken token)
        => ReplaceCoreAsync(expectedRevision, candidateRoot, true, token);

    private async Task<UpgradeProgress> ReplaceCoreAsync(long expectedRevision, string candidateRoot, bool resume, CancellationToken token)
    {
        var read = journal.Read();
        var progress = read.Progress;
        if (read.Availability != UpgradeJournalAvailability.Available || progress?.Revision != expectedRevision
            || (resume ? progress.Phase != UpgradePhase.Replacing
                : progress.Phase != UpgradePhase.Settled || progress.PendingOperation is not null || progress.Files.Length != 0))
            throw new InvalidOperationException("Replacement requires a fresh settled operation.");
        var root = PreparedRelease.CanonicalRoot(progress.Scope.Root);
        candidateRoot = PreparedRelease.CanonicalRoot(candidateRoot);
        var manager = typeof(UpgradeReplacement).Assembly.Location;
        if (PreparedRelease.Contains(root, manager) || PreparedRelease.Contains(candidateRoot, manager)
            || PreparedRelease.Contains(root, candidateRoot) || PreparedRelease.Contains(candidateRoot, root))
            throw new InvalidDataException("Replacement requires an external manager and separate staging.");
        using var managerPin = InvocationRuntimeFiles.OpenExistingPackageFileForRead(manager);
        if (Hash(managerPin.Stream) != progress.ManagerDigest) throw new InvalidDataException("Manager changed.");
        using var rootPin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(root);
        using var candidatePin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(candidateRoot);
        // Before any file intent, live old bytes still supply the complete inventory.
        // After that boundary only the verified retained package supplies it.
        var backupComplete = resume && (progress.Files.Length > 0 || progress.PendingOperation is null);
        var old = PreparedRelease.Inspect(backupComplete ? journal.RetainedPackageRoot : root, !backupComplete);
        var next = PreparedRelease.Inspect(candidateRoot, false);
        if (old.Digest != progress.OldPackageDigest || next.Digest != progress.NewPackageDigest)
            throw new InvalidDataException("Package binding changed.");
        var oldFiles = Entries(old);
        var nextFiles = Entries(next);
        var names = oldFiles.Keys.Union(nextFiles.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var changes = names.Where(name => !oldFiles.TryGetValue(name, out var previous)
            || !nextFiles.TryGetValue(name, out var target) || previous.Length != target.Length
            || !previous.Sha256.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (resume && (progress.Files.Any(f => !changes.Contains(f.Path, StringComparer.Ordinal))
            || progress.Files.Count(f => f.Result == UpgradeFileResult.Pending) > 1
            || progress.Files.Any(f => f.Result == UpgradeFileResult.Pending && f.IntentId != progress.PendingOperation)))
            throw new InvalidDataException("Replacement intent is inconsistent.");
        if (names.Length > 4096 || progress.Revision + 2 * changes.Length + 4 > 4096)
            throw new InvalidDataException("Replacement exceeds the retained evidence budget.");
        foreach (var name in names)
        {
            if (names.Any(other => other.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Package file and directory collide.");
            if (!oldFiles.ContainsKey(name) && !progress.Files.Any(f => f.Path == name)
                && (File.Exists(Path.Combine(root, name)) || Directory.Exists(Path.Combine(root, name))))
                throw new InvalidDataException("New payload collides with an operator file.");
        }
        if (!resume && (Directory.Exists(journal.RetainedPackageRoot) || File.Exists(journal.RetainedPackageRoot)))
            throw new InvalidDataException("Retained package already exists; reconcile it before retry.");
        using var sources = new Pins();
        foreach (var file in nextFiles.Values) sources.Add(candidateRoot, file);
        var backupIntent = resume ? progress.PendingOperation : Guid.NewGuid().ToString("N");
        if (!resume) progress = await Save(progress with { Phase = UpgradePhase.Replacing, PendingOperation = backupIntent }, token);
        // All old files, including retired files and exact inventory bytes, precede the first live write.
        if (!backupComplete) await lifecycle.WithMutationAsync(progress.Revision, async ct =>
        {
            foreach (var file in oldFiles.Values)
            {
                boundary?.Invoke(UpgradeReplacementBoundary.BeforeBackup, file.Path);
                using var source = InvocationRuntimeFiles.OpenExistingPackageFileForRead(Path.Combine(root, file.Path));
                Require(source.Stream, file);
                if (resume && File.Exists(Path.Combine(journal.RetainedPackageRoot, file.Path)))
                {
                    using var retained = InvocationRuntimeFiles.OpenExistingPackageFileForRead(Path.Combine(journal.RetainedPackageRoot, file.Path));
                    Require(retained.Stream, file);
                    continue;
                }
                using var destination = InvocationRuntimeFiles.OpenPackageFileForMutation(Path.Combine(journal.RetainedPackageRoot, file.Path), true);
                await Copy(source.Stream, destination.Stream, ct);
                Require(destination.Stream, file);
                boundary?.Invoke(UpgradeReplacementBoundary.AfterBackup, file.Path);
            }
        }, token);
        using var backups = new Pins();
        foreach (var file in oldFiles.Values) backups.Add(journal.RetainedPackageRoot, file);
        if (PreparedRelease.Inspect(journal.RetainedPackageRoot, false).Digest != old.Digest)
            throw new InvalidDataException("Retained package verification failed.");
        if (!backupComplete) progress = await Save(progress with { PendingOperation = null, LastConfirmedOperation = backupIntent }, token);
        // The inventory is last. It cannot describe a completed new payload while files remain old.
        foreach (var name in changes.OrderBy(n => n == PreparedRelease.InventoryName ? 1 : 0).ThenBy(n => n, StringComparer.Ordinal))
        {
            oldFiles.TryGetValue(name, out var previous);
            nextFiles.TryGetValue(name, out var target);
            var retainedIntent = progress.Files.SingleOrDefault(f => f.Path == name);
            var intent = retainedIntent?.IntentId ?? Guid.NewGuid().ToString("N");
            var pending = new UpgradeFileProgress(name, intent, target?.Sha256, null, UpgradeFileResult.Pending);
            if (retainedIntent is not null && (retainedIntent.ExpectedHash != target?.Sha256
                || retainedIntent.Result == UpgradeFileResult.Pending && progress.PendingOperation != intent))
                throw new InvalidDataException("File intent is inconsistent; retain the package evidence.");
            if (retainedIntent is null)
                progress = await Save(progress with { PendingOperation = intent, Files = [.. progress.Files, pending] }, token);
            await lifecycle.WithMutationAsync(progress.Revision, async ct =>
            {
                if (retainedIntent is not null)
                {
                    var path = Path.Combine(root, name);
                    var actual = Observe(path);
                    var result = ReconcileFile(previous, target, actual.Length, actual.Hash);
                    if (result is UpgradeFileResult.New or UpgradeFileResult.Removed) return;
                    if (retainedIntent.Result != UpgradeFileResult.Pending || result != UpgradeFileResult.Old)
                        throw new InvalidDataException($"File bytes are unresolved: {name}.");
                    // Only an observed exact old file can be changed. Partial bytes remain intact.
                }
                // Retained read handles deny writes and deletion for the complete operation.
                boundary?.Invoke(target is null ? UpgradeReplacementBoundary.BeforeRemove : UpgradeReplacementBoundary.BeforeWrite, name);
                using var destination = InvocationRuntimeFiles.OpenPackageFileForMutation(Path.Combine(root, name), previous is null);
                if (previous is not null) Require(destination.Stream, previous);
                ct.ThrowIfCancellationRequested();
                if (target is null) InvocationRuntimeFiles.RetirePackageFile(destination.Stream);
                else
                {
                    await Copy(sources.Files[name].Stream, destination.Stream, ct);
                    Require(destination.Stream, target);
                }
                boundary?.Invoke(target is null ? UpgradeReplacementBoundary.AfterRemove : UpgradeReplacementBoundary.AfterWrite, name);
            }, token);
            boundary?.Invoke(UpgradeReplacementBoundary.BeforeConfirm, name);
            if (retainedIntent is null || retainedIntent.Result == UpgradeFileResult.Pending)
                progress = await Save(progress with { PendingOperation = null, LastConfirmedOperation = intent,
                Files = progress.Files.Select(f => f.Path == name ? pending with { ObservedHash = target?.Sha256,
                    Result = target is null ? UpgradeFileResult.Removed : UpgradeFileResult.New } : f).ToArray() }, token);
            boundary?.Invoke(UpgradeReplacementBoundary.AfterConfirm, name);
        }
        await lifecycle.WithMutationAsync(progress.Revision, _ =>
        {
            backups.Verify(oldFiles.Values);
            sources.Verify(nextFiles.Values);
            if (PreparedRelease.Inspect(root, true).Digest != next.Digest
                || oldFiles.Keys.Except(nextFiles.Keys, StringComparer.OrdinalIgnoreCase).Any(n => File.Exists(Path.Combine(root, n))))
                throw new InvalidDataException("Installed package verification failed.");
            return Task.CompletedTask;
        }, token);
        return await Save(progress with { Phase = UpgradePhase.Installed }, token);
    }

    private async Task<UpgradeProgress> Save(UpgradeProgress progress, CancellationToken token) =>
        (await journal.SaveAsync(progress.Revision, progress, token).ConfigureAwait(false)).Progress!;

    private static Dictionary<string, ReleaseFile> Entries(ReleaseInspection package)
    {
        var result = package.Inventory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var bytes = PreparedRelease.Read(Path.Combine(package.Root, PreparedRelease.InventoryName), 1024 * 1024);
        result.Add(PreparedRelease.InventoryName, new(PreparedRelease.InventoryName, bytes.Length, package.Digest));
        return result;
    }

    private static string Hash(FileStream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)); }
    private static (long? Length, string? Hash) Observe(string path)
    {
        try
        {
            using var pin = InvocationRuntimeFiles.OpenExistingPackageFileForRead(path);
            return (pin.Stream.Length, Hash(pin.Stream));
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            || exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 })
        { return (null, null); }
    }

    internal static UpgradeFileResult ReconcileFile(ReleaseFile? previous, ReleaseFile? target, long? length, string? hash)
    {
        if (length is null && hash is null)
            return target is null ? UpgradeFileResult.Removed : previous is null ? UpgradeFileResult.Old : UpgradeFileResult.Unknown;
        if (target is not null && target.Length == length && target.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
            return UpgradeFileResult.New;
        return previous is not null && previous.Length == length && previous.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)
            ? UpgradeFileResult.Old : UpgradeFileResult.Unknown;
    }
    private static void Require(FileStream stream, ReleaseFile file)
    {
        if (stream.Length != file.Length || !Hash(stream).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Package bytes changed: {file.Path}.");
    }
    private static async Task Copy(FileStream source, FileStream destination, CancellationToken token)
    {
        source.Position = 0;
        destination.Position = 0;
        destination.SetLength(0);
        await source.CopyToAsync(destination, token).ConfigureAwait(false);
        await destination.FlushAsync(token).ConfigureAwait(false);
        destination.Flush(true);
    }

    private sealed class Pins : IDisposable
    {
        internal Dictionary<string, InvocationRuntimeFiles.PinnedLogReadStream> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal void Add(string root, ReleaseFile file)
        {
            var pin = InvocationRuntimeFiles.OpenExistingPackageFileForRead(Path.Combine(root, file.Path));
            try { Require(pin.Stream, file); Files.Add(file.Path, pin); }
            catch { pin.Dispose(); throw; }
        }
        internal void Verify(IEnumerable<ReleaseFile> files) { foreach (var file in files) Require(Files[file.Path].Stream, file); }
        public void Dispose() { foreach (var pin in Files.Values) pin.Dispose(); }
    }
}
