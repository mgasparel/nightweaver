using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

[SupportedOSPlatform("windows")]
public sealed class PlanningInputFilesTests
{
    private sealed class Files : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal HostPaths Paths { get; }
        internal string Checkout { get; }
        internal string Target { get; }
        internal string Source => Path.Combine(Checkout, "synthetic.json");
        internal byte[] Bytes { get; } = Encoding.UTF8.GetBytes("synthetic input sentinel\r\n\u00e9");
        internal PlanningLocalInput Input { get; }
        internal PlanningInputFiles Store() => new(Paths, ProjectSlug.Parse("demo"), new string('a', 64), Target);
        internal string Object => Path.Combine(Store().Root, "sha256", Input.ExpectedSha256);

        internal Files()
        {
            Paths = new(Path.Combine(temp.Path, "host"));
            Checkout = Paths.WorktreesRoot(ProjectSlug.Parse("demo"));
            Target = Path.Combine(temp.Path, "target");
            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(Checkout);
            File.WriteAllBytes(Source, Bytes);
            PrivateFile(Source);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Bytes));
            Input = new("export", "synthetic.json", new("synthetic source", "reported revision", "do not execute"),
                hash, new("demo", new string('a', 64)), "sha256/" + hash);
        }
        public void Dispose() => temp.Dispose();
    }

    private static void PrivateFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileInfo(path).GetAccessControl();
        security.SetAccessRuleProtection(true, false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void Broaden(string path, bool directory)
    {
        var rule = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow);
        if (directory)
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl(); security.AddAccessRule(rule); info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl(); security.AddAccessRule(rule); info.SetAccessControl(security);
        }
    }

    [Fact]
    public void PreservesExactBytesAndReportedSourceAcrossRestartAndPlanningCleanup()
    {
        using var f = new Files();
        var manifest = Assert.Single(f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Equal(f.Input.Source, manifest.Source);
        Assert.Equal(f.Input.SourcePath, manifest.SourcePath);
        Assert.Equal(f.Bytes.Length, manifest.SizeBytes);
        Assert.Equal(f.Input.ExpectedSha256, manifest.Sha256);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Object, "bytes")));
        var record = File.ReadAllBytes(Path.Combine(f.Object, "manifest.json"));
        var written = File.GetLastWriteTimeUtc(Path.Combine(f.Object, "bytes"));
        Assert.Equal(manifest, Assert.Single(f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(f.Object, "bytes")));
        Assert.Single(Directory.GetFiles(f.Checkout)); // No input output in the Target Repository.
        Directory.Delete(f.Checkout, true); // Only the disposable Planning checkout.
        Assert.Equal(manifest, f.Store().Verify(f.Input));
        Assert.Equal(manifest, Assert.Single(f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken)));
        Assert.Equal(record, File.ReadAllBytes(Path.Combine(f.Object, "manifest.json")));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Object, "bytes")));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("object")]
    [InlineData("bytes")]
    [InlineData("manifest.json")]
    public void BroadAccessCannotProduceSuccess(string item)
    {
        using var f = new Files();
        f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken);
        var path = item == "root" ? f.Store().Root : item == "object" ? f.Object : Path.Combine(f.Object, item);
        Broaden(path, item is "root" or "object");
        Assert.Throws<InvalidDataException>(() => f.Store().Verify(f.Input));
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Object, "bytes")));
    }

    [Fact]
    public void BroadSourceAndBroadExistingRootRemainUnchanged()
    {
        using var f = new Files();
        Broaden(f.Source, false);
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(f.Object));
        PrivateFile(f.Source);
        Broaden(f.Store().Root, true);
        var acl = new DirectoryInfo(f.Store().Root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Equal(acl, new DirectoryInfo(f.Store().Root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("manifest.json")]
    public void CorruptOrIncompleteObjectIsRetained(string item)
    {
        using var f = new Files();
        f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken);
        var path = Path.Combine(f.Object, item);
        File.WriteAllText(path, "incomplete synthetic record");
        if (item == "bytes")
            Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        else
            Assert.Throws<JsonException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Equal("incomplete synthetic record", File.ReadAllText(path));
    }

    [Fact]
    public void MissingSourceCannotCreateOrChangeSourceBinding()
    {
        using var f = new Files();
        var absent = f.Input with { SourcePath = "absent.json" };
        Assert.Throws<IOException>(() => f.Store().Preserve([absent], f.Checkout, TestContext.Current.CancellationToken));
        f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken);
        File.Delete(f.Source);
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([absent], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => f.Store().Verify(f.Input with { Source = new("another source") }));
        Assert.Equal(f.Input.Source, f.Store().Verify(f.Input).Source);
    }

    [Fact]
    public void ChangedSourceDoesNotReplaceVerifiedEvidence()
    {
        using var f = new Files();
        f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken);
        File.WriteAllText(f.Source, "changed source");
        var error = Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("changed source", error.Message);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Object, "bytes")));
        Assert.Equal(f.Input.ExpectedSha256, f.Store().Verify(f.Input).Sha256);
    }

    [Fact]
    public void HashMismatchAndInterruptedStageRemainForInspection()
    {
        using var f = new Files();
        File.WriteAllText(f.Source, "mismatched synthetic bytes");
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(f.Object));
        var pending = f.Object + ".pending";
        Assert.Equal("mismatched synthetic bytes", File.ReadAllText(Path.Combine(pending, "bytes")));
        File.WriteAllBytes(f.Source, f.Bytes);
        var error = Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Contains("Interrupted", error.Message);
        Assert.Throws<IOException>(() => f.Store().Verify(f.Input));
        Assert.Equal("mismatched synthetic bytes", File.ReadAllText(Path.Combine(pending, "bytes")));
    }

    [Theory]
    [InlineData("../synthetic.json")]
    [InlineData("./synthetic.json")]
    [InlineData("synthetic.json:stream")]
    [InlineData("synthetic.json.")]
    [InlineData("NUL")]
    public void UnsafeSourceSpellingCannotBeRead(string path)
    {
        using var f = new Files();
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input with { SourcePath = path }], f.Checkout, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(f.Object));
    }

    [Theory]
    [InlineData("locator")]
    [InlineData("project")]
    [InlineData("repository")]
    [InlineData("hash")]
    public void ForeignOrMalformedRequestsAreRefusedBeforeStorage(string change)
    {
        using var f = new Files();
        var input = change switch
        {
            "locator" => f.Input with { Locator = "../outside" },
            "project" => f.Input with { Store = f.Input.Store with { Project = "foreign" } },
            "repository" => f.Input with { Store = f.Input.Store with { RepositoryIdentity = new string('b', 64) } },
            _ => f.Input with { ExpectedSha256 = "unsafe" }
        };
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(f.Paths.PlanningInputsRoot(ProjectSlug.Parse("demo"))));
    }

    [Fact]
    public void OversizedSourceAndCandidateAreRefused()
    {
        using var f = new Files();
        using (var file = File.OpenWrite(f.Source)) file.SetLength(PlanningInputFiles.MaximumInputBytes + 1);
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(f.Object));
        var bytes = new byte[PlanningInputFiles.MaximumInputBytes];
        File.WriteAllBytes(f.Source, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var input = f.Input with { ExpectedSha256 = hash, Locator = "sha256/" + hash };
        // Shared content still counts once for each declared input.
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve(Enumerable.Range(0, 5).Select(i => input with { Id = "input-" + i }).ToArray(), f.Checkout, TestContext.Current.CancellationToken));
        Assert.Equal(PlanningInputFiles.MaximumInputBytes, f.Store().Verify(input).SizeBytes);
    }

    [Fact]
    public void WriterLeaseAndSourceWriteHandlePreventSuccess()
    {
        using var f = new Files();
        f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken);
        using (var held = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(f.Store().Root, "writer.lock"), false))
            Assert.Throws<IOException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        using (var held = new FileStream(f.Source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Assert.Throws<IOException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.Store().Preserve([f.Input], f.Checkout, cancellation.Token));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Object, "bytes")));
    }

    [Fact]
    public async Task JunctionAndHardLinkCannotReadOrReplaceOutsideEvidence()
    {
        using var f = new Files();
        var outside = Path.Combine(f.Paths.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel"); File.WriteAllBytes(sentinel, f.Bytes); PrivateFile(sentinel);
        var junction = Path.Combine(f.Checkout, "link");
        var result = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", junction, outside],
            f.Checkout, null, TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        try
        {
            Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input with { SourcePath = "link/sentinel" }], f.Checkout, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(junction); }
        var hardLink = Path.Combine(f.Checkout, "hard-link");
        Assert.True(CreateHardLinkW(hardLink, sentinel, IntPtr.Zero));
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input with { SourcePath = "hard-link" }], f.Checkout, TestContext.Current.CancellationToken));
        f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken);
        var objectBytes = Path.Combine(f.Object, "bytes");
        File.Delete(objectBytes);
        Assert.True(CreateHardLinkW(@"\\?\" + objectBytes, @"\\?\" + sentinel, IntPtr.Zero));
        Assert.Throws<InvalidDataException>(() => f.Store().Verify(f.Input));
        var lease = Path.Combine(f.Store().Root, "writer.lock");
        File.Delete(lease);
        Assert.True(CreateHardLinkW(@"\\?\" + lease, @"\\?\" + sentinel, IntPtr.Zero));
        Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Equal(f.Bytes, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public async Task StoreJunctionCannotUseOutsideDirectory()
    {
        using var f = new Files();
        var outside = Path.Combine(f.Paths.Root, "outside-store");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel"), "retain outside data");
        var junction = Path.Combine(f.Paths.Root, "evidence");
        var result = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", junction, outside],
            f.Checkout, null, TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        try
        {
            Assert.Throws<InvalidDataException>(() => f.Store().Preserve([f.Input], f.Checkout, TestContext.Current.CancellationToken));
            Assert.Equal("retain outside data", File.ReadAllText(Path.Combine(outside, "sentinel")));
            Assert.Single(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void AbsoluteEmptyInputAndBoundedUnicodeSourceRecordRoundTrip()
    {
        using var f = new Files();
        File.WriteAllBytes(f.Source, []);
        var hash = Convert.ToHexStringLower(SHA256.HashData([]));
        var input = f.Input with
        {
            SourcePath = f.Source, ExpectedSha256 = hash, Locator = "sha256/" + hash,
            Source = new(new string('\u754c', 1024), new string('\u754c', 1024), new string('\u754c', 4096))
        };
        var record = Assert.Single(f.Store().Preserve([input], f.Checkout, TestContext.Current.CancellationToken));
        Assert.Equal(0, record.SizeBytes);
        Assert.Equal(input.Source, f.Store().Verify(input).Source);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr attributes);
}
