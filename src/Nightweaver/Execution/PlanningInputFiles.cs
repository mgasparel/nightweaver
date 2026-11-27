using System.ComponentModel;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Nightweaver.Configuration;

namespace Nightweaver.Execution;

internal sealed record PlanningInputManifest(string Schema, PlanningInputStore Store, string Locator,
    string Sha256, long SizeBytes, string SourcePath, PlanningInputSource Source);

internal interface IPlanningInputStore
{
    PlanningInputManifest[] Preserve(IReadOnlyList<PlanningLocalInput> inputs, string checkout, CancellationToken cancellationToken);
    PlanningInputManifest Verify(PlanningLocalInput input);
    string Location(PlanningLocalInput input);
}

// No cleanup ownership. Publication and Delivery share this verification boundary.
internal sealed class PlanningInputFiles : IPlanningInputStore
{
    internal const long MaximumInputBytes = 16 * 1024 * 1024;
    private const long MaximumCandidateBytes = 64 * 1024 * 1024;
    private const int MaximumManifestBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PlanningInputStore binding;
    private readonly string hostRoot;
    private readonly string target;
    internal string Root { get; }

    internal PlanningInputFiles(HostPaths paths, ProjectSlug project, string repositoryIdentity, string targetRepository)
    {
        if (!Hash(repositoryIdentity)) throw new ArgumentException("Invalid repository identity.", nameof(repositoryIdentity));
        binding = new(ProjectSlug.Parse(project.Value).Value, repositoryIdentity);
        hostRoot = paths.Root;
        target = Path.GetFullPath(targetRepository);
        Root = Path.Combine(paths.PlanningInputsRoot(project), repositoryIdentity);
        if (Within(target, Root)) throw new InvalidDataException("Input storage is inside the Target Repository.");
    }

    public PlanningInputManifest[] Preserve(IReadOnlyList<PlanningLocalInput> inputs, string checkout,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count > 64) throw new InvalidDataException("Too many local inputs.");
        foreach (var input in inputs)
        {
            try { Validate(input); }
            catch (InvalidDataException error) { error.Data["planningInputId"] = input?.Id; throw; }
        }
        if (inputs.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
            throw new InvalidDataException("Local input IDs are not unique.");
        using var root = OpenRoot(create: true);
        using var lease = InvocationRuntimeFiles.OpenPlanningInputLock(Path.Combine(Root, "writer.lock"));
        RequirePrivate(lease.Stream);
        if (lease.Stream.Length != 0) throw new InvalidDataException("Input store lock contains retained data.");
        var results = new List<PlanningInputManifest>();
        long total = 0;
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanningInputManifest result;
            try { result = PreserveOne(input, checkout, MaximumCandidateBytes - total, cancellationToken); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { error.Data["planningInputId"] = input.Id; throw; }
            total = checked(total + result.SizeBytes);
            results.Add(result);
        }
        return results.ToArray();
    }

    // Delivery verifies the object only. SourcePath is never a Delivery fallback.
    public PlanningInputManifest Verify(PlanningLocalInput input)
    {
        Validate(input);
        using var root = OpenRoot(create: false);
        return ReadObject(input);
    }

    public string Location(PlanningLocalInput input)
    {
        Validate(input);
        return Path.Combine(ObjectPath(input), "bytes");
    }

    private PlanningInputManifest PreserveOne(PlanningLocalInput input, string checkout, long remaining,
        CancellationToken cancellationToken)
    {
        var objectPath = ObjectPath(input);
        var pending = objectPath + ".pending";
        if (Path.Exists(pending)) throw new InvalidDataException("Interrupted input preservation requires inspection: " + input.Locator);
        var retained = Path.Exists(objectPath) ? ReadObject(input) : null;
        InvocationRuntimeFiles.PinnedLogReadStream source;
        // Check the unnormalized spelling first, so traversal is not silently erased.
        var sourcePath = SourcePath(input.SourcePath, checkout);
        try { source = InvocationRuntimeFiles.OpenExistingPackageFileForRead(sourcePath); }
        catch (IOException e) when (retained is not null && Missing(e))
        {
            if (retained.SizeBytes > remaining) throw new InvalidDataException("Candidate input size exceeds 64 MiB.");
            return retained;
        }
        using (source)
        {
            RequireCanonical(source.Stream, sourcePath);
            RequirePrivate(source.Stream);
            var size = source.Stream.Length;
            if (size > MaximumInputBytes || size > remaining) throw new InvalidDataException("Local input size exceeds its limit.");
            if (retained is not null)
            {
                var hash = Digest(source.Stream, null, size, cancellationToken);
                if (hash != input.ExpectedSha256 || size != retained.SizeBytes)
                    throw new InvalidDataException("Source differs from the approved input: " + input.Id);
                return ReadObject(input);
            }
            CreatePrivateDirectory(pending);
            var manifest = new PlanningInputManifest("nightweaver-local-input-v1", binding, input.Locator,
                input.ExpectedSha256, size, input.SourcePath, input.Source);
            using (var bytes = InvocationRuntimeFiles.OpenPackageFileForMutation(Path.Combine(pending, "bytes"), true))
            {
                RequirePrivate(bytes.Stream);
                var hash = Digest(source.Stream, bytes.Stream, size, cancellationToken);
                bytes.Stream.Flush(true);
                if (hash != input.ExpectedSha256) throw new InvalidDataException("Source hash differs from the approved input: " + input.Id);
            }
            using (var record = InvocationRuntimeFiles.OpenPackageFileForMutation(Path.Combine(pending, "manifest.json"), true))
            {
                RequirePrivate(record.Stream);
                record.Stream.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                record.Stream.Flush(true);
            }
            // Verify the complete private staging object before one no-replace rename.
            ReadObject(input, pending);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(pending, objectPath);
            return ReadObject(input);
        }
    }

    private PlanningInputManifest ReadObject(PlanningLocalInput input, string? directory = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        directory ??= ObjectPath(input);
        using var pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
        RequirePrivate(new DirectoryInfo(directory).GetAccessControl());
        using var record = InvocationRuntimeFiles.OpenExistingPackageFileForRead(Path.Combine(directory, "manifest.json"));
        RequirePrivate(record.Stream);
        if (record.Stream.Length is <= 0 or > MaximumManifestBytes) throw new InvalidDataException("Invalid input manifest size.");
        var raw = new byte[(int)record.Stream.Length];
        record.Stream.ReadExactly(raw);
        var manifest = JsonSerializer.Deserialize<PlanningInputManifest>(raw, Json)
            ?? throw new InvalidDataException("Missing input manifest.");
        var expected = new PlanningInputManifest("nightweaver-local-input-v1", binding, input.Locator,
            input.ExpectedSha256, manifest.SizeBytes, input.SourcePath, input.Source);
        if (manifest != expected || manifest.SizeBytes is < 0 or > MaximumInputBytes
            || !raw.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, Json)))
            throw new InvalidDataException("Input manifest binding or content differs: " + input.Locator);
        using var bytes = InvocationRuntimeFiles.OpenExistingPackageFileForRead(Path.Combine(directory, "bytes"));
        RequirePrivate(bytes.Stream);
        if (bytes.Stream.Length != manifest.SizeBytes || Digest(bytes.Stream, null, manifest.SizeBytes, default) != manifest.Sha256)
            throw new InvalidDataException("Stored input bytes differ: " + input.Locator);
        return manifest;
    }

    private DirectoryPins OpenRoot(bool create)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Input storage requires Windows file safety and access control.");
        using var host = InvocationRuntimeFiles.PinExistingRuntimeDirectory(hostRoot);
        using var repository = InvocationRuntimeFiles.PinExistingRuntimeDirectory(target);
        if (Within(InvocationRuntimeFiles.ResolveExistingDirectoryPath(target), InvocationRuntimeFiles.ResolveExistingDirectoryPath(hostRoot)))
            throw new InvalidDataException("Input storage host is inside the Target Repository.");
        var pins = new List<IDisposable>();
        try
        {
            var current = hostRoot;
            foreach (var segment in Path.GetRelativePath(hostRoot, Path.Combine(Root, "sha256")).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (create && !Path.Exists(current)) CreatePrivateDirectory(current);
                pins.Add(InvocationRuntimeFiles.PinExistingRuntimeDirectory(current));
                if (!string.Equals(current, InvocationRuntimeFiles.ResolveExistingDirectoryPath(current), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Input store path is an alias.");
                RequirePrivate(new DirectoryInfo(current).GetAccessControl());
            }
            return new DirectoryPins(pins);
        }
        catch { new DirectoryPins(pins).Dispose(); throw; }
    }

    private sealed class DirectoryPins(List<IDisposable> pins) : IDisposable
    {
        public void Dispose() { for (var i = pins.Count - 1; i >= 0; i--) pins[i].Dispose(); }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectorySecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidDataException("Host account identity is unavailable.");
        security.SetOwner(owner);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in Trusted(owner))
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject()
            };
            var nativePath = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            if (!CreateDirectoryW(nativePath, ref attributes))
                throw new IOException("Could not create a private input directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int InheritHandle; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    private static void RequireCanonical(FileStream stream, string path)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(stream.SafeFileHandle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException("Cannot verify input path identity.");
        var actual = new string(buffer, 0, (int)length);
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Input source path is an alias.");
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] Trusted(SecurityIdentifier owner) =>
        [owner, new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)];

    private static void RequirePrivate(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RequirePrivate(stream.GetAccessControl());
    }

    private static void RequirePrivate(FileSystemSecurity security)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidDataException("Host account identity is unavailable.");
        var trusted = Trusted(user);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
            throw new InvalidDataException("Input owner is not authorized.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var hostAllowed = false;
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || !trusted.Contains(rule.IdentityReference))
                throw new InvalidDataException("Input access is broad or cannot be verified.");
            if (rule.IdentityReference == user && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)
                hostAllowed = true;
        }
        if (!hostAllowed)
            throw new InvalidDataException("Input access is broad or cannot be verified.");
    }

    private static string Digest(Stream source, Stream? destination, long size, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long read = 0;
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read += count;
            if (read > size || read > MaximumInputBytes) throw new InvalidDataException("Input changed during preservation.");
            hash.AppendData(buffer, 0, count);
            destination?.Write(buffer, 0, count);
        }
        if (read != size) throw new InvalidDataException("Input changed during preservation.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string ObjectPath(PlanningLocalInput input) => Path.Combine(Root, "sha256", input.ExpectedSha256);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Within(string parent, string child) => Path.GetRelativePath(parent, child) is var relative
        && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !Path.IsPathFullyQualified(relative);
    private static bool Missing(IOException e) => e is FileNotFoundException or DirectoryNotFoundException
        || e.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };

    private static string SourcePath(string path, string checkout)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Input source path is not supported.");
        var relative = path[(Path.GetPathRoot(path)?.Length ?? 0)..];
        if (relative.Replace('\\', '/').Split('/').Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains(':')))
            throw new InvalidDataException("Input source traversal or alias is not permitted.");
        return Path.IsPathFullyQualified(path) ? path : Path.Combine(checkout, path);
    }

    private void Validate(PlanningLocalInput input)
    {
        static bool Text(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);
        if (input is null || input.Store != binding || !Hash(input.ExpectedSha256) || input.Locator != "sha256/" + input.ExpectedSha256
            || !Text(input.Id, 64) || input.Id[0] is < 'a' or > 'z' || input.Id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            || !Text(input.SourcePath, 4096) || input.Source is null || !Text(input.Source.Identity, 1024)
            || input.Source.Revision is not null && !Text(input.Source.Revision, 1024)
            || input.Source.ExportCommand is not null && !Text(input.Source.ExportCommand, 4096))
            throw new InvalidDataException("Invalid or foreign local input binding.");
    }
}
