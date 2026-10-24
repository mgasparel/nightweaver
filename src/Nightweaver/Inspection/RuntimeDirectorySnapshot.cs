namespace Nightweaver.Inspection;

internal enum DirectoryState { Present, Missing, UnsafeLink }
internal sealed record RuntimeDirectoryEntry(string Id, string Path, DirectoryState State, bool AmbiguousIdentity = false);
internal sealed record RuntimeDirectorySnapshot(string Path, DirectoryState State, IReadOnlyList<RuntimeDirectoryEntry> Entries);
