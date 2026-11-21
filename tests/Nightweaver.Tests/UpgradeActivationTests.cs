using System.Security.Cryptography;
using Nightweaver.Application;
using Nightweaver.Background;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Supervision;
using Nightweaver.Upgrade;

namespace Nightweaver.Tests;

public sealed class UpgradeActivationTests
{
    [Theory]
    [InlineData("missing-activation")]
    [InlineData("interrupted-activation")]
    [InlineData("partial-maintenance-release")]
    public async Task ResumeRetainsUncertainActivationPublicationAndMaintenanceRelease(string fault)
    {
        using var f = new Fixture(true, fault, twoProjects: fault == "partial-maintenance-release");
        await f.Prepare();
        var pending = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.Pending, pending.Outcome);
        var retained = f.Journal.Read().Progress!;
        Assert.Equal(UpgradePhase.Verifying, retained.Phase);
        Assert.NotNull(retained.PendingOperation);
        Assert.Null(retained.AcceptedPairs);
        Assert.Equal(0, f.Starts);
        var activation = Path.Combine(UpgradeInterlock.Root(f.Paths), retained.OperationId, "activation.json");
        if (fault == "partial-maintenance-release")
        {
            Assert.Equal(2, retained.Maintenance.Length);
            Assert.All(retained.Maintenance, item => Assert.NotNull(item.Token));
            Assert.Null(f.Store("demo").Read().State!.MaintenanceToken);
            Assert.Equal(retained.Maintenance.Single(m => m.Project == "other").Token,
                f.Store("other").Read().State!.MaintenanceToken);
            Assert.True(File.Exists(activation));
        }
        else
        {
            Assert.False(File.Exists(activation));
            Assert.Equal(fault == "interrupted-activation", File.Exists(activation + ".partial.tmp"));
        }
        // Reuse the retained operation through later stop and foreign maintenance.
        // Each intent gets a fresh evidence snapshot and two explicit resume calls.
        foreach (var intent in new[] { "unchanged", "stop", "maintenance" })
        {
            await f.ChangeIntent(intent);
            var evidence = f.RecoveryEvidence();
            var resumed = await f.Coordinator().ResumeAsync(retained.Revision, f.Candidate, f.Token);
            Assert.Equal(UpgradeActivationOutcome.Pending, resumed.Outcome);
            Assert.Equal(retained.Revision, resumed.Progress!.Revision);
            Assert.Null(resumed.Progress.AcceptedPairs);
            if (fault == "interrupted-activation") Assert.Contains("Interrupted upgrade interlock", resumed.Code);
            else if (fault == "partial-maintenance-release" || intent != "unchanged")
                Assert.Equal("Activation intent changed; retain the upgrade interlock.", resumed.Code);
            else Assert.Contains("activation.json", resumed.Code);
            Assert.Equal(evidence, f.RecoveryEvidence());
            if (fault == "interrupted-activation")
                Assert.Throws<InvalidDataException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
            else Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
            var repeated = await f.Coordinator().ResumeAsync(resumed.Progress.Revision, f.Candidate, f.Token);
            Assert.Equal(resumed.Code, repeated.Code);
            Assert.Equal(UpgradeActivationOutcome.Pending, repeated.Outcome);
            Assert.Equal(retained.Revision, repeated.Progress!.Revision);
            Assert.Equal(evidence, f.RecoveryEvidence());
            Assert.Equal(0, f.Starts);
            Assert.Equal(0, f.Observations);
        }
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Journal.RetainedPackageRoot, false).Digest);
        Assert.Equal(PreparedRelease.Inspect(f.Candidate, false).Digest, PreparedRelease.Inspect(f.Paths.Root, true).Digest);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(f.Paths.Root, "worktrees", "sentinel")));
    }

    [Theory]
    [InlineData(false, "lost-launch-reply")]
    [InlineData(true, "lost-launch-reply")]
    [InlineData(false, "lost-acceptance-reply")]
    [InlineData(true, "lost-acceptance-reply")]
    public async Task ResumeAcknowledgesLostLaunchReplyWithoutAnotherLaunch(bool supervised, string fault)
    {
        using var f = new Fixture(true, fault, supervised);
        await f.Prepare();
        var pending = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(fault == "lost-launch-reply" ? UpgradeActivationOutcome.Pending : UpgradeActivationOutcome.Failed, pending.Outcome);
        Assert.Equal(1, f.Starts);
        var resumed = await f.Coordinator().ResumeAsync(pending.Progress!.Revision, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.LiveAccepted, resumed.Outcome);
        Assert.Equal(1, f.Starts);
        var repeated = await f.Coordinator().ResumeAsync(resumed.Progress!.Revision, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.LiveAccepted, repeated.Outcome);
        Assert.Equal(resumed.Progress.Revision, repeated.Progress!.Revision);
        Assert.Equal(1, f.Starts);
    }

    [Theory]
    [InlineData(false, "Installed")]
    [InlineData(true, "Installed")]
    [InlineData(false, "Draining")]
    [InlineData(false, "Settled")]
    public async Task ResumeContinuesInstalledPayloadAndIsIdempotent(bool running, string phase)
    {
        using var f = new Fixture(running);
        await f.Prepare();
        var installed = await f.Install(Enum.Parse<UpgradePhase>(phase));
        var result = await f.Coordinator().ResumeAsync(installed.Revision, f.Candidate, f.Token);
        Assert.Equal(running ? UpgradeActivationOutcome.LiveAccepted : UpgradeActivationOutcome.PayloadVerifiedStopped, result.Outcome);
        var again = await f.Coordinator().ResumeAsync(result.Progress!.Revision, f.Candidate, f.Token);
        Assert.Equal(result.Outcome, again.Outcome);
        Assert.Equal(running ? 1 : 0, f.Starts);
        Assert.Equal(result.Progress.Revision, again.Progress!.Revision);
    }

    [Fact]
    public async Task ResumeRequiresTheExactObservedRevision()
    {
        using var f = new Fixture(true);
        await f.Prepare();
        var result = await f.Coordinator().ResumeAsync(2, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.Refused, result.Outcome);
        Assert.Equal(0, f.Starts);
        Assert.Equal(1, f.Journal.Read().Progress!.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsoleStopWaitsForSharedLocksButForceDoesNotWait(bool admission)
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.Path);
        var project = ProjectSlug.Parse("demo");
        var token = TestContext.Current.CancellationToken;
        var reservation = new UpgradeReservation(1, paths.Root, new('a', 32), new('b', 64));
        await UpgradeInterlock.ReserveAsync(paths, reservation, token);
        using var state = new BackgroundOwnerState(new(1, "running", new('c', 32), 1234, DateTimeOffset.UtcNow,
            paths.Root, "config", "demo", null, null, "http://127.0.0.1:5000", "healthy", "live", "", "", null));
        var exits = 0;
        using var signals = new RuntimeShutdownSignals(_ => exits++);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await BackgroundOwnerApplication.InitializeLifecycleAsync(signals, _ => Task.FromResult<SupervisionLifecycle?>(null), lifecycle =>
        {
            entered.SetResult();
            BackgroundOwnerApplication.PersistConsoleStopAsync(paths, project, lifecycle, state,
                BackgroundOwnerRecord.PathFor(paths, project)).GetAwaiter().GetResult();
        });
        using var held = admission ? UpgradeInterlock.AcquireAdmission(paths) : BackgroundFileLock.AcquireStart(paths, project);
        var first = Task.Run(signals.RequestStop, token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(first.IsCompleted);
            Assert.True(signals.StopGate.IsStopRequested);
            Assert.False(signals.FirstStopToken.IsCancellationRequested);
            signals.RequestStop();
            Assert.Equal(1, exits);
            Assert.True(signals.StopGate.IsForceStopRequested);
        }
        finally
        {
            held.Dispose();
            await first.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        Assert.Null(signals.IntentPersistenceFailure);
        Assert.Equal("OperatorDrain", state.Current.StopReason);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireActivationAllowed(paths, reservation, project));
        Assert.False(Directory.Exists(paths.SupervisionRoot(project)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedStoppedOwnerDoesNotNeedAControlPipe(bool supervised)
    {
        using var f = new Fixture(false, "retained-stopped", supervised);
        await f.Prepare();
        var original = UpgradeLifecycle.OwnerEvidence(f.Owner);
        var result = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.PayloadVerifiedStopped, result.Outcome);
        Assert.Contains(original, result.Progress!.Owners);
        Assert.Equal(0, f.Drains);
        Assert.Equal(0, f.Starts);
        Assert.Equal(UpgradePhase.Accepted, result.Progress.Phase);
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Journal.RetainedPackageRoot, false).Digest);
    }

    [Theory]
    [InlineData("unsettled")]
    [InlineData("reused")]
    [InlineData("unknown")]
    [InlineData("lock-unknown")]
    public async Task RetainedOwnerWithoutSettlementCannotReplacePayload(string fault)
    {
        using var f = new Fixture(false, fault, supervised: false);
        await f.Prepare();
        var result = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.Pending, result.Outcome);
        Assert.Equal(0, f.Starts);
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Paths.Root, true).Digest);
        Assert.Contains(UpgradeLifecycle.OwnerEvidence(f.Owner), f.Journal.Read().Progress!.Owners);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
        var pending = f.Journal.Read().Progress!;
        var resumed = await f.Coordinator().ResumeAsync(pending.Revision, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.Pending, resumed.Outcome);
        Assert.Equal(0, f.Starts);
        Assert.Equal(pending.Revision, resumed.Progress!.Revision);
    }

    [Theory]
    [InlineData("console-before-capture")]
    [InlineData("console-before-drain")]
    public async Task ManualConsoleStopPreventsReplacementLaunch(string fault)
    {
        using var f = new Fixture(true, fault, supervised: false);
        await f.Prepare();
        if (fault == "console-before-capture") await f.ConsoleStop();
        var result = await f.Coordinator().ResumeAsync(1, f.Candidate, f.Token);
        Assert.Equal(0, f.Starts);
        if (fault == "console-before-capture")
        {
            Assert.Equal(UpgradeActivationOutcome.PayloadVerifiedStopped, result.Outcome);
            Assert.False(Assert.Single(result.Progress!.Maintenance).StartRequested);
        }
        else
        {
            Assert.Equal(UpgradeActivationOutcome.Failed, result.Outcome);
            Assert.True(Assert.Single(result.Progress!.Maintenance).StartRequested);
            Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
        }
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Journal.RetainedPackageRoot, false).Digest);
        Assert.False(Directory.Exists(f.Paths.SupervisionRoot(ProjectSlug.Parse("demo"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualStopAfterSettlementPreventsReplacementLaunch(bool force)
    {
        using var f = new Fixture(true, supervised: false);
        await f.Prepare();
        var installed = await f.Install();
        Assert.True(Assert.Single(installed.Maintenance).StartRequested);
        Assert.Equal(0, await f.Stop(force));
        // A repeated stop must preserve the same durable suppression.
        Assert.Equal(0, await f.Stop(force));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Activate(installed.Revision));
        Assert.Equal(0, f.Starts);
        Assert.Equal(UpgradePhase.Installed, f.Journal.Read().Progress!.Phase);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
        Assert.False(Directory.Exists(f.Paths.SupervisionRoot(ProjectSlug.Parse("demo"))));
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Journal.RetainedPackageRoot, false).Digest);
    }

    [Fact]
    public async Task StopForAnotherProjectDoesNotInvalidateReplacement()
    {
        using var f = new Fixture(true, supervised: false);
        await f.Prepare();
        var installed = await f.Install();
        using (BackgroundFileLock.AcquireStart(f.Paths, ProjectSlug.Parse("other")))
            await UpgradeInterlock.RecordStopAsync(f.Paths, ProjectSlug.Parse("other"), f.Token);
        Assert.Equal(UpgradePhase.Accepted, (await f.Activate(installed.Revision)).Phase);
        Assert.Equal(1, f.Starts);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("foreign")]
    [InlineData("partial")]
    public async Task InvalidStopRecordRetainsSuppressionEvenAfterRelease(string fault)
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.Path);
        var reservation = new UpgradeReservation(1, paths.Root, new('a', 32), new('b', 64));
        var token = TestContext.Current.CancellationToken;
        await UpgradeInterlock.ReserveAsync(paths, reservation, token);
        await UpgradeInterlock.ReleaseAsync(paths, reservation, token);
        var directory = Path.Combine(UpgradeInterlock.Root(paths), reservation.OperationId);
        var path = Path.Combine(directory, fault == "partial" ? "stopped-demo.json.partial.tmp" : "stopped-demo.json");
        File.WriteAllText(path, fault == "foreign"
            ? System.Text.Json.JsonSerializer.Serialize(reservation with { OperationId = new('c', 32) }, PreparedRelease.JsonOptions)
            : "{}");
        Assert.ThrowsAny<Exception>(() => UpgradeInterlock.RequireStartupAllowed(paths));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(1234, 0, 1, true)]
    [InlineData(1235, 0, 1, false)]
    [InlineData(1234, 1, 1, false)]
    [InlineData(1234, 0, 2, false)]
    public void WebAttestationMustMatchLaunchedNativeChild(int pid, int seconds, int version, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var release = new LiveRelease(version, new('a', 64), new('b', 40), 1234, now, "root", "config", "demo");
        Assert.Equal(expected, LiveReleaseIdentity.MatchesProcess(release, pid, now.AddSeconds(seconds)));
    }

    [Fact]
    public async Task MalformedActivationRemainsSuppressedEvenWithAReleaseRecord()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.Path);
        var reservation = new UpgradeReservation(1, paths.Root, new('a', 32), new('b', 64));
        var token = TestContext.Current.CancellationToken;
        await UpgradeInterlock.ReserveAsync(paths, reservation, token);
        await UpgradeInterlock.ActivateAsync(paths, reservation, new(1, reservation.OperationId, new('c', 64), []), token);
        await UpgradeInterlock.ReleaseAsync(paths, reservation, token);
        var path = Path.Combine(UpgradeInterlock.Root(paths), reservation.OperationId, "activation.json");
        File.WriteAllText(path, "{}");
        Assert.Throws<System.Text.Json.JsonException>(() => UpgradeInterlock.RequireStartupAllowed(paths));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ComposesReplacementAndRecordsActualPairOrStoppedPayload(bool running, bool supervised)
    {
        using var f = new Fixture(running, supervised: supervised);
        await f.Prepare();
        var result = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(running ? UpgradeActivationOutcome.LiveAccepted : UpgradeActivationOutcome.PayloadVerifiedStopped, result.Outcome);
        Assert.Equal(UpgradePhase.Accepted, result.Progress!.Phase);
        Assert.Equal(running ? 1 : 0, f.Starts);
        Assert.Equal(running ? 1 : 0, result.Progress.AcceptedPairs!.Length);
        if (running)
        {
            var pair = Assert.Single(result.Progress.AcceptedPairs);
            Assert.Equal(f.Owner.ProcessId, pair.Owner.ProcessId);
            Assert.Equal(f.Owner.RuntimeId, pair.Owner.RuntimeId);
            Assert.Equal(f.Owner.CliRelease, pair.Cli);
            Assert.Equal(f.Owner.WebRelease, pair.Web);
        }
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Journal.RetainedPackageRoot, false).Digest);
        UpgradeInterlock.RequireStartupAllowed(f.Paths);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(f.Paths.Root, "worktrees", "sentinel")));
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("stop")]
    [InlineData("disable")]
    [InlineData("maintenance")]
    [InlineData("unavailable")]
    [InlineData("payload")]
    [InlineData("second-owner")]
    [InlineData("too-close")]
    [InlineData("expired")]
    [InlineData("cancel")]
    [InlineData("stop-during-health")]
    [InlineData("manual-stop-during-health")]
    public async Task RefusesFalseAcceptanceAndRetainsOperationWithoutReplay(string fault)
    {
        using var f = new Fixture(true, fault, supervised: fault != "manual-stop-during-health");
        await f.Prepare();
        var result = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.Pending, result.Outcome);
        Assert.Equal(1, f.Starts);
        Assert.Equal(UpgradePhase.Verifying, result.Progress!.Phase);
        Assert.NotNull(result.Progress.PendingOperation);
        Assert.Null(result.Progress.AcceptedPairs);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator().ApplyAsync(result.Progress.Revision, f.Candidate, f.Token));
        Assert.Equal(1, f.Starts);
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Journal.RetainedPackageRoot, false).Digest);
        var resumed = await f.Coordinator().ResumeAsync(result.Progress.Revision, f.Candidate, f.Token);
        Assert.Equal(1, f.Starts);
        if (fault is "second-owner" or "too-close") Assert.Equal(UpgradeActivationOutcome.LiveAccepted, resumed.Outcome);
        else
        {
            Assert.Equal(UpgradeActivationOutcome.Pending, resumed.Outcome);
            Assert.Equal(result.Progress.Revision, resumed.Progress!.Revision);
            Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
        }
    }

    [Fact]
    public async Task UnsupportedPreflightDoesNotDrainOrReserve()
    {
        using var f = new Fixture(true, "preflight");
        await f.Prepare();
        var result = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(UpgradeActivationOutcome.Refused, result.Outcome);
        Assert.Equal(UpgradePhase.Prepared, f.Journal.Read().Progress!.Phase);
        Assert.Equal(0, f.Starts);
        UpgradeInterlock.RequireStartupAllowed(f.Paths);
    }

    [Theory]
    [InlineData("final-web-failure")]
    [InlineData("final-runtime-failure")]
    [InlineData("final-state")]
    [InlineData("final-failure")]
    [InlineData("final-stop-reason")]
    [InlineData("final-web-health")]
    [InlineData("final-runtime-health")]
    public async Task FinalOwnerFailureRejectsAcceptanceWhileNativeOwnerRemainsAlive(string fault)
    {
        using var f = new Fixture(true, fault);
        await f.Prepare();
        var result = await f.Coordinator().ApplyAsync(1, f.Candidate, f.Token);
        Assert.Equal(2, f.Observations);
        Assert.True(f.Active);
        Assert.Equal(UpgradeActivationOutcome.Pending, result.Outcome);
        Assert.Equal(UpgradePhase.Verifying, result.Progress!.Phase);
        Assert.NotNull(result.Progress.PendingOperation);
        Assert.Null(result.Progress.AcceptedPairs);
        Assert.Equal(1, f.Starts);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(f.Paths));
        Assert.Equal(f.OldDigest, PreparedRelease.Inspect(f.Journal.RetainedPackageRoot, false).Digest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator().ApplyAsync(result.Progress.Revision, f.Candidate, f.Token));
        Assert.Equal(1, f.Starts);
    }

    [Theory]
    [InlineData("legacy-cli")]
    [InlineData("legacy-web")]
    [InlineData("old-cli")]
    [InlineData("old-web")]
    [InlineData("wrong-build")]
    [InlineData("wrong-web-project")]
    [InlineData("wrong-web-root")]
    [InlineData("wrong-cli-process")]
    [InlineData("same-process")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("runtime-stale")]
    [InlineData("control-stale")]
    [InlineData("failed")]
    [InlineData("wrong-repository")]
    [InlineData("reused")]
    [InlineData("healthy")]
    public void ChecksLivePairWithoutPackageOrNativeSetup(string fault)
    {
        var now = DateTimeOffset.UtcNow;
        var root = Path.GetFullPath(".");
        var project = new AffectedUpgradeProject("demo", Path.Combine(root, "demo.json"), new('0', 64), root, root, "physical");
        var scope = new UpgradeScope(root, "sid", [project]);
        var progress = new UpgradeProgress(1, new('a', 32), 1, scope, new('0', 64), new('1', 64), new('2', 64),
            UpgradePhase.Verifying, null, null, new('b', 32), [], [], [], []);
        var cli = new LiveRelease(1, progress.NewPackageDigest, new('3', 40), 1234, now, root, project.Configuration, "demo");
        var owner = new BackgroundOwnerRecord(1, "running", new('c', 32), cli.ProcessId, now, root, project.Configuration,
            "demo", root, new('d', 32), "http://127.0.0.1:5000", "healthy", "live", "", "", null)
            { CliRelease = cli, WebRelease = cli with { ProcessId = 1235 }, GitCommonDirectory = root, ContainmentVersion = 1 };
        owner = fault switch
        {
            "legacy-cli" => owner with { CliRelease = null },
            "legacy-web" => owner with { WebRelease = null },
            "old-cli" => owner with { CliRelease = cli with { PackageDigest = progress.OldPackageDigest } },
            "old-web" => owner with { WebRelease = owner.WebRelease! with { PackageDigest = progress.OldPackageDigest } },
            "wrong-build" => owner with { WebRelease = owner.WebRelease! with { Build = new('0', 40) } },
            "wrong-web-project" => owner with { WebRelease = owner.WebRelease! with { Project = "other" } },
            "wrong-web-root" => owner with { WebRelease = owner.WebRelease! with { Root = "other" } },
            "wrong-cli-process" => owner with { CliRelease = cli with { ProcessId = 9999 } },
            "same-process" => owner with { WebRelease = cli },
            "wrong-repository" => owner with { TargetRepository = Path.Combine(root, "other") },
            _ => owner
        };
        var observation = new DeploymentObservation(1, fault == "stale" ? now.AddSeconds(-30) : fault == "future" ? now.AddSeconds(30) : now,
            "test", 1, new(root, project.Configuration, "demo", root, root), owner,
            fault == "failed" ? DeploymentHealth.Failed : DeploymentHealth.Healthy, "test", true,
            new(fault == "reused" ? OwnerProcessState.Reused : OwnerProcessState.Alive, false),
            fault == "runtime-stale" ? now.AddSeconds(-30) : now, fault == "control-stale" ? now.AddSeconds(-30) : now);
        if (fault == "healthy") Assert.Equal(cli, UpgradeAcceptance.Confirm(progress, project, cli.Build, owner.RunId, observation, now).Cli);
        else Assert.Throws<InvalidOperationException>(() => UpgradeAcceptance.Confirm(progress, project, cli.Build, owner.RunId, observation, now));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private readonly bool running;
        private readonly bool supervised;
        private readonly string? fault;
        private readonly UpgradeScope scope;
        private readonly UpgradeLifecycle lifecycle;
        private readonly SupervisionStore store;
        private readonly Dictionary<string, SupervisionStore> stores = [];
        private readonly string nextDigest;
        private readonly string build;
        private bool active;
        private bool unavailable;
        private int observations;
        private bool acceptanceInterrupted;
        private bool publicationInterrupted;
        internal int Observations => observations;
        internal bool Active => active;
        private readonly Clock clock = new();
        internal HostPaths Paths { get; }
        internal string Candidate { get; }
        internal string OldDigest { get; }
        internal UpgradeJournal Journal { get; }
        internal BackgroundOwnerRecord Owner { get; private set; }
        internal int Starts { get; private set; }
        internal int Drains { get; private set; }
        internal CancellationToken Token { get; } = TestContext.Current.CancellationToken;
        internal Fixture(bool running, string? fault = null, bool supervised = true, bool twoProjects = false)
        {
            this.running = running;
            this.fault = fault;
            this.supervised = supervised;
            Paths = new(temp.CreateDirectory("installed"));
            Candidate = temp.CreateDirectory("candidate");
            UpgradeReplacementTests.Package(Paths.Root, false);
            UpgradeReplacementTests.Package(Candidate, true);
            var old = PreparedRelease.Inspect(Paths.Root, true);
            OldDigest = old.Digest;
            build = old.BuildVersion;
            nextDigest = PreparedRelease.Inspect(Candidate, false).Digest;
            var repo = temp.CreateDirectory("repository");
            var config = temp.WriteFile("demo.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                name = "demo", target_repository = new { path = repo }
            }));
            scope = new(Paths.Root, "S-1-5-21-1001", [new("demo", config, Hash(File.ReadAllBytes(config)), repo, repo, "physical")]);
            if (twoProjects)
            {
                var otherRepo = temp.CreateDirectory("other-repository");
                var otherConfig = temp.WriteFile("other.json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    name = "other", target_repository = new { path = otherRepo }
                }));
                scope = scope with { Projects = [.. scope.Projects,
                    new("other", otherConfig, Hash(File.ReadAllBytes(otherConfig)), otherRepo, otherRepo, "other-physical")] };
            }
            Journal = new(scope, new('a', 32), boundary =>
            {
                if (fault is "missing-activation" or "interrupted-activation" && !publicationInterrupted
                    && boundary == UpgradePublicationBoundary.Renamed
                    && new UpgradeJournal(scope, new('a', 32)).Read().Progress?.Phase == UpgradePhase.Verifying)
                {
                    publicationInterrupted = true;
                    if (fault == "interrupted-activation")
                        File.WriteAllText(Path.Combine(UpgradeInterlock.Root(Paths), new string('a', 32), "activation.json.partial.tmp"), "{");
                    throw new IOException("activation publication interrupted");
                }
                if (fault == "lost-acceptance-reply" && !acceptanceInterrupted && boundary == UpgradePublicationBoundary.Renamed
                    && new UpgradeJournal(scope, new('a', 32)).Read().Progress?.Phase == UpgradePhase.Accepted)
                {
                    acceptanceInterrupted = true;
                    throw new IOException("acceptance reply lost");
                }
            });
            store = new(Paths, ProjectSlug.Parse("demo"), new(Paths.Root, config, repo, scope.UserSid));
            foreach (var project in scope.Projects)
                stores.Add(project.Name, project.Name == "demo" ? store : new(Paths, ProjectSlug.Parse(project.Name),
                    new(Paths.Root, project.Configuration, project.GitCommonDirectory, scope.UserSid)));
            Owner = new(1, running ? "running" : "stopped", new('b', 32), 1234, DateTimeOffset.UtcNow.AddMinutes(-1), Paths.Root,
                config, "demo", repo, new('c', 32), "http://127.0.0.1:5000", "healthy", "live", "", "", null)
                { ContainmentVersion = 1, GitCommonDirectory = repo };
            active = running;
            lifecycle = new(Paths, Journal, new(_ => Task.FromResult(Inventory()),
                (project, _) =>
                {
                    if (fault == "partial-maintenance-release" && !publicationInterrupted && project == "other"
                        && Journal.Read().Progress?.Phase == UpgradePhase.Verifying && store.Read().State!.MaintenanceToken is null)
                    {
                        publicationInterrupted = true;
                        throw new IOException("maintenance release interrupted");
                    }
                    return unavailable ? throw new InvalidDataException("unavailable")
                        : Task.FromResult<SupervisionLifecycle?>(supervised ? new(stores[project]) : null);
                },
                async (_, operation, maintenance, token) =>
                {
                    Drains++;
                    if (fault == "retained-stopped") throw new IOException("Control pipe is unavailable.");
                    if (fault == "console-before-drain") await ConsoleStop();
                    if (fault is "console-before-capture" or "console-before-drain")
                    {
                        using var state = new BackgroundOwnerState(Owner);
                        var project = ProjectConfigLoader.Load(scope.Projects[0].Configuration, ProjectSlug.Parse("demo"), Paths);
                        var options = new BackgroundOwnerOptions(Paths.Root, Owner.ConfigPath, "demo", Owner.RunId, Owner.WebUrl);
                        var reply = await BackgroundOwnerApplication.HandleRequestAsync(
                            new("upgrade", Owner.RunId, Maintenance: true, UpgradeOperationId: operation, MaintenanceToken: maintenance),
                            options, Paths, project, state, BackgroundOwnerRecord.PathFor(Paths, ProjectSlug.Parse("demo")),
                            () => null, null, token);
                        Assert.True(reply.Accepted);
                        Owner = state.Current;
                    }
                    active = false;
                    return true;
                },
                _ => new(!running && fault == "reused" ? OwnerProcessState.Reused : fault == "unknown" ? OwnerProcessState.Unavailable
                    : active ? OwnerProcessState.Alive : OwnerProcessState.Exited, !active && fault != "unsettled"),
                _ => fault == "lock-unknown" ? null : active), clock,
                async (elapsed, token) =>
                {
                    clock.Advance(fault == "expired" ? TimeSpan.FromSeconds(61) : elapsed);
                    if (fault == "stop-during-health")
                    {
                        using var command = BackgroundFileLock.AcquireStart(Paths, ProjectSlug.Parse("demo"));
                        await new SupervisionLifecycle(store).ApplyAsync(SupervisionLifecycleAction.Stop, null, null, token);
                    }
                    if (fault == "manual-stop-during-health") Assert.Equal(0, await Stop(false));
                });
            temp.WriteFile("installed/worktrees/sentinel", "preserve");
        }
        private UpgradeLifecycleInventory Inventory()
        {
            // Both live probes have succeeded. Only the final inventory records
            // failure; the original native identity and held owner lock remain.
            if (observations == 2)
            {
                var failure = new BackgroundFailure(clock.GetUtcNow(), "WebFailure", Owner.RunId,
                    Owner.RuntimeId, Owner.ProcessId, Owner.ProcessStartedAtUtc);
                Owner = fault switch
                {
                    "final-web-failure" => Owner with { State = "degraded", WebHealth = "stopped", RuntimeHealth = "stale",
                        StopReason = "WebFailure", Failure = failure },
                    "final-runtime-failure" => Owner with { State = "degraded", RuntimeHealth = "failed",
                        StopReason = "RuntimeFailure", Failure = failure with { Reason = "RuntimeFailure" } },
                    "final-state" => Owner with { State = "degraded" },
                    "final-failure" => Owner with { Failure = failure },
                    "final-stop-reason" => Owner with { StopReason = "RuntimeFailure" },
                    "final-web-health" => Owner with { WebHealth = "unavailable" },
                    "final-runtime-health" => Owner with { RuntimeHealth = "unavailable" },
                    _ => Owner
                };
            }
            return new(scope, running || fault is not null ? [Owner] : [], true);
        }
        internal async Task Prepare()
        {
            foreach (var project in scope.Projects)
            {
                var initial = SupervisionState.Manual(new(Paths.Root, project.Configuration, project.GitCommonDirectory, scope.UserSid))
                    with { Enabled = true, DesiredRunning = running };
                if (supervised) await stores[project.Name].SaveAsync(null, initial, Token);
            }
            await Journal.SaveAsync(null, new(1, new('a', 32), 0, scope, OldDigest, nextDigest,
                Hash(File.ReadAllBytes(typeof(UpgradeCoordinator).Assembly.Location)), UpgradePhase.Prepared, null, null,
                new('d', 32), [], [], [], []), Token);
        }
        internal SupervisionStore Store(string project) => stores[project];
        internal async Task ChangeIntent(string intent)
        {
            if (intent == "unchanged") return;
            using var command = BackgroundFileLock.AcquireStart(Paths, ProjectSlug.Parse("demo"));
            var lifecycle = new SupervisionLifecycle(store);
            if (intent == "stop")
            {
                await lifecycle.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, Token);
                Assert.False(store.Read().State!.DesiredRunning);
            }
            else
            {
                var current = store.Read().State!;
                if (current.MaintenanceToken is not null)
                    await lifecycle.ApplyAsync(SupervisionLifecycleAction.LeaveMaintenance, current.MaintenanceToken, current.Revision, Token);
                var foreign = await lifecycle.ApplyAsync(SupervisionLifecycleAction.EnterMaintenance, null, null, Token);
                Assert.DoesNotContain(Journal.Read().Progress!.Maintenance, m => m.Token == foreign.State!.MaintenanceToken);
                Assert.False(foreign.State!.DesiredRunning);
            }
        }
        internal string[] RecoveryEvidence() => new[] { Journal.DirectoryPath, UpgradeInterlock.Root(Paths) }
            .SelectMany(directory => Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            .Concat(stores.Values.Select(s => s.Path)).Order(StringComparer.Ordinal)
            .Select(path => path + ":" + Hash(File.ReadAllBytes(path))).ToArray();
        internal UpgradeCoordinator Coordinator() => new(Journal, lifecycle, new(Journal, lifecycle),
            _ => Task.FromResult(new UpgradePreflightReport("test", Paths.Root, Candidate, OldDigest, nextDigest, scope.Projects,
                fault != "preflight", true, [])), Start, Observe);
        internal async Task<UpgradeProgress> Install(UpgradePhase phase = UpgradePhase.Installed)
        {
            var progress = await lifecycle.BeginDrainAsync(1, Token);
            if (phase == UpgradePhase.Draining) return progress;
            progress = await lifecycle.ConfirmSettlementAsync(progress.Revision, Token);
            if (phase == UpgradePhase.Settled) return progress;
            return await new UpgradeReplacement(Journal, lifecycle).ReplaceAsync(progress.Revision, Candidate, Token);
        }
        internal Task<UpgradeProgress> Activate(long revision) => lifecycle.ActivateAsync(revision, Start, Observe, Token);
        internal Task<int> Stop(bool force) => BackgroundManagerApplication.RunAsync(
            new(BackgroundAction.Stop, new ProjectSelector.ByConfigPath(scope.Projects[0].Configuration), Force: force),
            new AppEnvironment(Paths.Root, Paths.Root, new StringWriter(), new StringWriter()), Token);
        internal async Task ConsoleStop()
        {
            using var state = new BackgroundOwnerState(Owner);
            using var signals = new RuntimeShutdownSignals(_ => { });
            await BackgroundOwnerApplication.InitializeLifecycleAsync(signals, _ => Task.FromResult<SupervisionLifecycle?>(null),
                lifecycle => BackgroundOwnerApplication.PersistConsoleStopAsync(Paths, ProjectSlug.Parse("demo"), lifecycle,
                    state, BackgroundOwnerRecord.PathFor(Paths, ProjectSlug.Parse("demo"))).GetAwaiter().GetResult());
            signals.RequestStop();
            Assert.Null(signals.IntentPersistenceFailure);
            Owner = state.Current;
        }
        private async Task Start(AffectedUpgradeProject project, string runId, CancellationToken token)
        {
            Starts++;
            Assert.Throws<IOException>(() => BackgroundFileLock.AcquireStart(Paths, ProjectSlug.Parse(project.Name)));
            Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(Paths));
            if (fault == "launch") throw new IOException("launch failed");
            if (fault == "cancel") throw new OperationCanceledException(token);
            active = true;
            var created = DateTimeOffset.UtcNow;
            var cli = new LiveRelease(1, nextDigest, build, 5678, created, Paths.Root, project.Configuration, project.Name);
            var web = cli with { ProcessId = 5679 };
            Owner = Owner with { RunId = runId, RuntimeId = new('e', 32), ProcessId = cli.ProcessId,
                ProcessStartedAtUtc = created, CliRelease = cli, WebRelease = web, ObservedAtUtc = created };
            if (fault == "lost-launch-reply") throw new IOException("launch reply lost");
            if (fault is "stop" or "disable" or "maintenance")
            {
                var state = store.Read().State!;
                var changed = fault switch
                {
                    "stop" => SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.Stop),
                    "maintenance" => SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.EnterMaintenance),
                    _ => SupervisionPolicy.SetIntent(state, false, state.DesiredRunning, null)
                };
                await store.SaveAsync(state.Revision, changed, token);
            }
            if (fault == "unavailable") unavailable = true;
            if (fault == "payload") File.WriteAllText(Path.Combine(Paths.Root, "added.txt"), "changed");
        }
        private Task<DeploymentObservation> Observe(AffectedUpgradeProject project, CancellationToken token)
        {
            var owner = Owner;
            var now = clock.GetUtcNow();
            observations++;
            if (fault == "second-owner" && observations == 2) owner = owner with { RuntimeId = new('f', 32) };
            if (fault == "too-close" && observations == 2) now -= TimeSpan.FromSeconds(10);
            owner = fault switch
            {
                "legacy-cli" => owner with { CliRelease = null },
                "legacy-web" => owner with { WebRelease = null },
                "old-cli" => owner with { CliRelease = owner.CliRelease! with { PackageDigest = OldDigest } },
                "old-web" => owner with { WebRelease = owner.WebRelease! with { PackageDigest = OldDigest } },
                "wrong-build" => owner with { WebRelease = owner.WebRelease! with { Build = new('0', 40) } },
                "wrong-web-project" => owner with { WebRelease = owner.WebRelease! with { Project = "other" } },
                "wrong-web-root" => owner with { WebRelease = owner.WebRelease! with { Root = Candidate } },
                "wrong-cli-process" => owner with { CliRelease = owner.CliRelease! with { ProcessId = 9999 } },
                "same-process" => owner with { WebRelease = owner.WebRelease! with { ProcessId = owner.ProcessId } },
                "wrong-repository" => owner with { TargetRepository = Candidate },
                _ => owner
            };
            return Task.FromResult(new DeploymentObservation(1, fault == "stale" ? now.AddSeconds(-30) : fault == "future" ? now.AddSeconds(30) : now,
                "test", 1, new(Paths.Root, project.Configuration, project.Name, project.Repository, project.GitCommonDirectory),
                owner, fault == "failed" ? DeploymentHealth.Failed : DeploymentHealth.Healthy, "test", true,
                new(fault == "reused" ? OwnerProcessState.Reused : OwnerProcessState.Alive, false),
                fault == "runtime-stale" ? now.AddSeconds(-30) : now, fault == "control-stale" ? now.AddSeconds(-30) : now));
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        public void Dispose() => temp.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset utc = DateTimeOffset.UtcNow;
        private long ticks;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan elapsed) { utc += elapsed; ticks += elapsed.Ticks; }
    }
}
