using Nightweaver.Background;
using Nightweaver.Supervision;
using Nightweaver.Upgrade;

namespace Nightweaver.Tests;

public sealed class UpgradeActivationTests
{
    private static readonly string[] ApplySteps = ["preflight", "drain", "settle", "replace", "activate"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyConfirmsPreflightThenDrainsReplacesAndActivates(bool live)
    {
        var scenario = new Scenario { Live = live };
        var result = await scenario.Coordinator.ApplyAsync(1, Scenario.Candidate, TestContext.Current.CancellationToken);

        Assert.Equal(live ? UpgradeActivationOutcome.LiveAccepted : UpgradeActivationOutcome.PayloadVerifiedStopped, result.Outcome);
        Assert.Equal(UpgradePhase.Accepted, result.Progress!.Phase);
        Assert.Equal(ApplySteps, scenario.Calls);
    }

    [Theory]
    [InlineData("incompatible")]
    [InlineData("no-rollback")]
    [InlineData("refusals")]
    [InlineData("installation")]
    [InlineData("candidate")]
    [InlineData("old-digest")]
    [InlineData("new-digest")]
    [InlineData("projects")]
    public async Task UnconfirmedPreflightRefusesBeforeDrain(string mismatch)
    {
        var scenario = new Scenario();
        var report = scenario.Report;
        scenario.Report = mismatch switch
        {
            "incompatible" => report with { Compatible = false },
            "no-rollback" => report with { RollbackEligible = false },
            "refusals" => report with { Refusals = ["unsupported"] },
            "installation" => report with { InstallationRoot = "other" },
            "candidate" => report with { CandidateRoot = "other" },
            "old-digest" => report with { OldPackageDigest = "other" },
            "new-digest" => report with { NewPackageDigest = "other" },
            "projects" => report with { Projects = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        var result = await scenario.Coordinator.ApplyAsync(1, Scenario.Candidate, TestContext.Current.CancellationToken);

        Assert.Equal(UpgradeActivationOutcome.Refused, result.Outcome);
        Assert.Equal("preflight-unconfirmed", result.Code);
        Assert.Equal("preflight", Assert.Single(scenario.Calls));
    }

    [Theory]
    [InlineData("Prepared", "manager preflight drain settle replace activate")]
    [InlineData("Draining", "manager settle replace resume-activation")]
    [InlineData("Settled", "manager replace resume-activation")]
    [InlineData("Replacing", "manager resume-replacement resume-activation")]
    [InlineData("Installed", "manager resume-activation")]
    [InlineData("Verifying", "manager resume-activation")]
    [InlineData("Accepted", "manager confirm-accepted")]
    public async Task ResumeContinuesFromRetainedPhaseWithoutReplayingCompletedSteps(string phase, string expected)
    {
        var scenario = new Scenario(Enum.Parse<UpgradePhase>(phase)) { Live = true };
        var result = await scenario.Coordinator.ResumeAsync(1, Scenario.Candidate, TestContext.Current.CancellationToken);

        Assert.Equal(UpgradeActivationOutcome.LiveAccepted, result.Outcome);
        Assert.Equal(expected.Split(' '), scenario.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRevisionOrUnavailableJournalCannotStartWork(bool unavailable)
    {
        var scenario = new Scenario
        {
            Availability = unavailable ? UpgradeJournalAvailability.Unavailable : UpgradeJournalAvailability.Available
        };
        var revision = unavailable ? 1 : 2;
        var token = TestContext.Current.CancellationToken;

        var result = await scenario.Coordinator.ResumeAsync(revision, Scenario.Candidate, token);
        Assert.Equal(UpgradeActivationOutcome.Refused, result.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Coordinator.ApplyAsync(revision, Scenario.Candidate, token));
        Assert.Empty(scenario.Calls);
    }

    [Fact]
    public async Task ChangedRecoveryManagerCannotResumeActivation()
    {
        var scenario = new Scenario(UpgradePhase.Verifying)
        {
            FailAt = "manager",
            Failure = new InvalidDataException("Recovery manager changed.")
        };
        var result = await scenario.Coordinator.ResumeAsync(1, Scenario.Candidate, TestContext.Current.CancellationToken);

        Assert.Equal(UpgradeActivationOutcome.Pending, result.Outcome);
        Assert.Equal("Recovery manager changed.", result.Code);
        Assert.Equal("manager", Assert.Single(scenario.Calls));
    }

    [Theory]
    [InlineData("Inspected")]
    [InlineData("RollingBack")]
    [InlineData("RolledBack")]
    public async Task UnrelatedRecoveryPhasesRefuseActivation(string phase)
    {
        var scenario = new Scenario(Enum.Parse<UpgradePhase>(phase));
        var result = await scenario.Coordinator.ResumeAsync(1, Scenario.Candidate, TestContext.Current.CancellationToken);

        Assert.Equal(UpgradeActivationOutcome.Refused, result.Outcome);
        Assert.Equal("manager", Assert.Single(scenario.Calls));
    }

    [Theory]
    [InlineData("settle", true)]
    [InlineData("activate", true)]
    [InlineData("activate", false)]
    public async Task ApplyFailureReportsRetainedStateAndDoesNotReplay(string step, bool pending)
    {
        var scenario = new Scenario { FailAt = step, PendingOnFailure = step == "activate" && pending };
        var result = await scenario.Coordinator.ApplyAsync(1, Scenario.Candidate, TestContext.Current.CancellationToken);

        Assert.Equal(pending ? UpgradeActivationOutcome.Pending : UpgradeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(step == "settle" ? UpgradePhase.Draining : UpgradePhase.Installed, result.Progress!.Phase);
        var evidence = Assert.Single(result.Progress.Evidence);
        Assert.Equal(UpgradeEvidenceKind.Failure, evidence.Kind);
        Assert.Equal(pending ? "activation-pending" : "activation-failed", evidence.Code);
        Assert.Equal(step, scenario.Calls[^2]);
        Assert.Equal("save", scenario.Calls[^1]);
        Assert.Equal(1, scenario.Calls.Count(call => call == step));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedActivationRetainsProgressWithoutPublishingFailure(bool resume)
    {
        var step = resume ? "resume-activation" : "activate";
        var scenario = new Scenario(resume ? UpgradePhase.Verifying : UpgradePhase.Prepared)
        {
            FailAt = step,
            Failure = new OperationCanceledException(),
            PendingOnFailure = true
        };
        var token = TestContext.Current.CancellationToken;
        var result = resume
            ? await scenario.Coordinator.ResumeAsync(1, Scenario.Candidate, token)
            : await scenario.Coordinator.ApplyAsync(1, Scenario.Candidate, token);

        Assert.Equal(UpgradeActivationOutcome.Pending, result.Outcome);
        Assert.Equal("reservation", result.Progress!.PendingOperation);
        Assert.Empty(result.Progress.Evidence);
        Assert.Equal(step, scenario.Calls[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservedLaunchesReleaseAdmissionBeforeStartAndCommandsBeforeProbing(bool reconciling)
    {
        var events = new List<string>();
        UpgradeLaunch[] launches = [new("first", "run-1"), new("second", "run-2")];
        await UpgradeLifecycle.StartReservedOwnersAsync(reconciling, launches,
            new RecordingLease(() => events.Add("release-admission")),
            new RecordingLease(() => events.Add("release-commands")), launch =>
            {
                events.Add(launch.RunId);
                return Task.CompletedTask;
            });
        events.Add("probe");

        Assert.Equal((reconciling ? "release-admission release-commands probe"
            : "release-admission run-1 run-2 release-commands probe").Split(' '), events);
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("digest", false)]
    [InlineData("missing", false)]
    [InlineData("foreign", false)]
    [InlineData("duplicate", false)]
    public void RetainedLaunchGrantMustMatchThePackageAndStartRequests(string fault, bool expected)
    {
        var (progress, _, _) = LiveIdentity(DateTimeOffset.UnixEpoch, "demo");
        progress = progress with
        {
            Maintenance = [new("demo", null, 0, 0) { StartRequested = true }, new("stopped", null, 0, 0)]
        };
        var grant = new UpgradeActivation(1, progress.OperationId, progress.NewPackageDigest, [new("demo", "run")]);
        grant = fault switch
        {
            "digest" => grant with { PackageDigest = progress.OldPackageDigest },
            "missing" => grant with { Launches = [] },
            "foreign" => grant with { Launches = [new("stopped", "run")] },
            "duplicate" => grant with { Launches = [.. grant.Launches, new("demo", "other")] },
            _ => grant
        };
        Assert.Equal(expected, UpgradeAcceptance.MatchesLaunchGrant(progress, grant));
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("manual", true)]
    [InlineData("missing", false)]
    [InlineData("unavailable", false)]
    [InlineData("generation", false)]
    [InlineData("revision", false)]
    [InlineData("token", false)]
    [InlineData("stopped", false)]
    [InlineData("disabled", false)]
    public void FinalIntentCheckRequiresTheOriginalAuthority(string fault, bool expected)
    {
        var item = new UpgradeMaintenance("demo", "maintenance", 5, 6) { StartRequested = true };
        var state = SupervisionState.Manual(new("root", "config", "git", "sid"))
            with { Revision = item.Revision, Generation = item.Generation, MaintenanceToken = item.Token, Enabled = true, DesiredRunning = true };
        state = fault switch
        {
            "generation" => state with { Generation = 7 },
            "revision" => state with { Revision = 7 },
            "token" => state with { MaintenanceToken = "foreign" },
            "stopped" => state with { DesiredRunning = false },
            "disabled" => state with { Enabled = false },
            _ => state
        };
        SupervisionRead? read = fault switch
        {
            "manual" or "missing" => null,
            "unavailable" => new(SupervisionAvailability.Unavailable, null, "store", "read failed"),
            _ => new(SupervisionAvailability.Available, state, "store", null)
        };
        if (fault == "manual") item = item with { Revision = 0 };
        Assert.Equal(expected, UpgradeLifecycle.ActivationIntentMatches(item, read));
    }

    [Theory]
    [InlineData("healthy", true)]
    [InlineData("state", false)]
    [InlineData("failure", false)]
    [InlineData("stop-reason", false)]
    [InlineData("web", false)]
    [InlineData("runtime", false)]
    public void FinalInventoryRejectsAnOwnerThatBecameUnhealthy(string fault, bool expected)
    {
        var (_, _, owner) = LiveIdentity(DateTimeOffset.UnixEpoch, "demo");
        owner = fault switch
        {
            "state" => owner with { State = "degraded" },
            "failure" => owner with { Failure = new(DateTimeOffset.UnixEpoch, "WebFailure", owner.RunId,
                owner.RuntimeId, owner.ProcessId, owner.ProcessStartedAtUtc) },
            "stop-reason" => owner with { StopReason = "RuntimeFailure" },
            "web" => owner with { WebHealth = "unavailable" },
            "runtime" => owner with { RuntimeHealth = "failed" },
            _ => owner
        };
        Assert.Equal(expected, UpgradeAcceptance.HasHealthyFinalOwner(owner));
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("reused")]
    [InlineData("unavailable")]
    [InlineData("descendants")]
    [InlineData("owner-lock")]
    public void StoppedAcceptanceRequiresFreshSettlementForTheExactOriginalOwner(string fault)
    {
        var (progress, project, original) = LiveIdentity(DateTimeOffset.UnixEpoch, "demo");
        progress = progress with { Owners = [UpgradeLifecycle.OwnerEvidence(original)] };
        BackgroundOwnerRecord[] owners = fault switch
        {
            "missing" => [],
            "foreign" => [original with { RunId = new('e', 32) }],
            _ => [original]
        };
        var inventory = new UpgradeLifecycleInventory(progress.Scope, owners, true);
        var processState = fault switch
        {
            "reused" => OwnerProcessState.Reused,
            "unavailable" => OwnerProcessState.Unavailable,
            _ => OwnerProcessState.Exited
        };

        void Verify() => UpgradeAcceptance.RequireStoppedSettlement(progress, project.Name, inventory,
            _ => fault == "owner-lock" ? null : false, observed =>
            {
                Assert.Equal(original, observed);
                return new(processState, fault != "descendants");
            });

        if (fault == "settled") Verify();
        else Assert.Throws<InvalidOperationException>(Verify);
    }

    [Theory]
    [InlineData("observation")]
    [InlineData("control")]
    [InlineData("runtime")]
    public void FinalAcceptanceRechecksEarlierProofAfterAnotherProjectIsProbed(string clock)
    {
        var firstSeen = DateTimeOffset.UnixEpoch.AddHours(1);
        var (progress, first, firstOwner) = LiveIdentity(firstSeen, "demo");
        var (_, second, secondOwner) = LiveIdentity(firstSeen.AddSeconds(2), "other");
        progress = progress with { Scope = progress.Scope with { Projects = [first, second] } };
        var identities = new[] { new UpgradeLaunch(first.Name, firstOwner.RunId), new UpgradeLaunch(second.Name, secondOwner.RunId) };
        var current = firstSeen.AddSeconds(11);
        var firstProof = Observe(first, firstOwner, current);
        firstProof = clock switch
        {
            "observation" => firstProof with { Utc = firstSeen },
            "control" => firstProof with { ControlObservedUtc = firstSeen },
            _ => firstProof with { RuntimeObservedUtc = firstSeen }
        };
        var proofs = new Dictionary<string, DeploymentObservation>(StringComparer.Ordinal)
        {
            [first.Name] = firstProof,
            [second.Name] = Observe(second, secondOwner, firstSeen.AddSeconds(2))
        };

        UpgradeAcceptance.ConfirmCurrentPairs(progress, secondOwner.CliRelease!.Build, [identities[1]], proofs, new FixedClock(current));
        Assert.Throws<InvalidOperationException>(() =>
            UpgradeAcceptance.ConfirmCurrentPairs(progress, secondOwner.CliRelease.Build, identities, proofs, new FixedClock(current)));
    }

    [Theory]
    [InlineData(1234, 0, 1, true)]
    [InlineData(1235, 0, 1, false)]
    [InlineData(1234, 1, 1, false)]
    [InlineData(1234, 0, 2, false)]
    public void WebAttestationMustMatchLaunchedNativeChild(int pid, int seconds, int version, bool expected)
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var release = new LiveRelease(version, new('a', 64), new('b', 40), 1234, now, "root", "config", "demo");
        Assert.Equal(expected, LiveReleaseIdentity.MatchesProcess(release, pid, now.AddSeconds(seconds)));
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
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var (progress, project, owner) = LiveIdentity(now, "demo");
        var root = progress.Scope.Root;
        var cli = owner.CliRelease!;
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

    private static (UpgradeProgress Progress, AffectedUpgradeProject Project, BackgroundOwnerRecord Owner)
        LiveIdentity(DateTimeOffset now, string name)
    {
        var root = Path.GetFullPath(".");
        var project = new AffectedUpgradeProject(name, Path.Combine(root, name + ".json"), new('0', 64), root, root, "physical");
        var scope = new UpgradeScope(root, "sid", [project]);
        var progress = new UpgradeProgress(1, new('a', 32), 1, scope, new('0', 64), new('1', 64), new('2', 64),
            UpgradePhase.Verifying, null, null, new('b', 32), [], [], [], []);
        var cli = new LiveRelease(1, progress.NewPackageDigest, new('3', 40), 1234, now, root, project.Configuration, name);
        var owner = new BackgroundOwnerRecord(1, "running", new('c', 32), cli.ProcessId, now, root, project.Configuration,
            name, root, new('d', 32), "http://127.0.0.1:5000", "healthy", "live", "", "", null)
            { CliRelease = cli, WebRelease = cli with { ProcessId = 1235 }, GitCommonDirectory = root, ContainmentVersion = 1 };
        return (progress, project, owner);
    }

    private static DeploymentObservation Observe(AffectedUpgradeProject project, BackgroundOwnerRecord owner, DateTimeOffset utc) =>
        new(1, utc, "test", 1, new(owner.Root, project.Configuration, project.Name, project.Repository, project.GitCommonDirectory),
            owner, DeploymentHealth.Healthy, "test", true, new(OwnerProcessState.Alive, false), utc, utc);

    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class RecordingLease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class Scenario
    {
        internal const string Candidate = "candidate";
        private readonly CancellationToken token = TestContext.Current.CancellationToken;
        private UpgradeProgress progress;
        internal List<string> Calls { get; } = [];
        internal UpgradePreflightReport Report { get; set; }
        internal UpgradeCoordinator Coordinator { get; }
        internal UpgradeJournalAvailability Availability { get; init; } = UpgradeJournalAvailability.Available;
        internal string? FailAt { get; init; }
        internal Exception Failure { get; init; } = new IOException("operation interrupted");
        internal bool PendingOnFailure { get; init; }
        internal bool Live { get; init; }

        internal Scenario(UpgradePhase phase = UpgradePhase.Prepared)
        {
            var projects = new AffectedUpgradeProject[] { new("demo", "config", "config-digest", "repo", "git", "physical") };
            progress = new(1, "operation", 1, new("installed", "sid", projects), "old", "new", "manager",
                phase, null, null, "backup", [], [], [], []);
            Report = new("test", "installed", Candidate, "old", "new", projects, true, true, []);
            Coordinator = new(new(
                ReadProgress: Read,
                SaveProgress: Save,
                VerifyRecoveryManager: _ => Record("manager"),
                Preflight: actual => { Assert.Equal(token, actual); Record("preflight"); return Task.FromResult(Report); },
                BeginDrain: (revision, actual) => Advance(revision, "drain", UpgradePhase.Draining, actual),
                ConfirmSettlement: (revision, actual) => Advance(revision, "settle", UpgradePhase.Settled, actual),
                ReplacePayload: (revision, root, actual) => Replace(revision, root, "replace", actual),
                ResumeReplacement: (revision, root, actual) => Replace(revision, root, "resume-replacement", actual),
                Activate: (revision, resume, actual) => Accept(revision, resume ? "resume-activation" : "activate", actual),
                ConfirmAccepted: (revision, actual) => Accept(revision, "confirm-accepted", actual)));
        }

        private UpgradeJournalRead Read() => new(Availability, progress, "journal", null);

        private Task<UpgradeJournalRead> Save(long revision, UpgradeProgress next, CancellationToken actual)
        {
            Assert.Equal(token, actual);
            Assert.Equal(progress.Revision, revision);
            Record("save");
            progress = next with { Revision = revision + 1 };
            return Task.FromResult(Read());
        }

        private Task<UpgradeProgress> Replace(long revision, string root, string operation, CancellationToken actual)
        {
            Assert.Equal(Candidate, root);
            return Advance(revision, operation, UpgradePhase.Installed, actual);
        }

        private async Task<UpgradeProgress> Accept(long revision, string operation, CancellationToken actual)
        {
            await Advance(revision, operation, UpgradePhase.Accepted, actual);
            if (Live)
            {
                var cli = new LiveRelease(1, "new", "build", 1, DateTimeOffset.UnixEpoch, "installed", "config", "demo");
                var owner = new UpgradeOwner("demo", 1, cli.CreatedUtc, "run", "owner", "runtime", "job");
                progress = progress with { AcceptedPairs = [new(owner, cli, cli with { ProcessId = 2 })] };
            }
            return progress;
        }

        private Task<UpgradeProgress> Advance(long revision, string operation, UpgradePhase phase, CancellationToken actual)
        {
            Assert.Equal(token, actual);
            Assert.Equal(progress.Revision, revision);
            Record(operation);
            progress = progress with { Phase = phase, Revision = revision + 1 };
            return Task.FromResult(progress);
        }

        private void Record(string operation)
        {
            Calls.Add(operation);
            if (operation != FailAt) return;
            if (PendingOnFailure) progress = progress with { PendingOperation = "reservation" };
            throw Failure;
        }
    }
}
