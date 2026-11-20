using Nightweaver.Background;
using Nightweaver.Supervision;
using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class DeploymentObservationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    internal static DeploymentIdentity Identity(string root) => new(root, Path.Combine(root, "demo.json"), "demo",
        Path.Combine(root, "target"), Path.Combine(root, "target", ".git"));
    internal static BackgroundOwnerRecord Owner(DeploymentIdentity identity) => new(1, "running", Guid.NewGuid().ToString("N"),
        1234, Now.AddMinutes(-2), identity.Root, identity.ConfigPath, identity.Project, identity.TargetRepository,
        Guid.NewGuid().ToString("N"), "http://127.0.0.1:5000", "healthy", "live", "", "", null)
        { GitCommonDirectory = identity.GitCommonDirectory, ContainmentVersion = 1, ObservedAtUtc = Now };

    [Theory]
    [InlineData("healthy", "Healthy")]
    [InlineData("stale-runtime", "Unknown")]
    [InlineData("foreign-runtime", "Unknown")]
    [InlineData("foreign-project", "Unknown")]
    [InlineData("foreign-repository", "Unknown")]
    [InlineData("stale-control", "Unknown")]
    [InlineData("future-control", "Unknown")]
    [InlineData("future-runtime", "Unknown")]
    [InlineData("web-liveness-only", "Unknown")]
    [InlineData("reused-pid", "Unknown")]
    [InlineData("native-unavailable", "Unknown")]
    [InlineData("lock-unavailable", "Unknown")]
    [InlineData("lock-absent", "Unknown")]
    [InlineData("pipe-unavailable", "Unknown")]
    [InlineData("pipe-deadline", "Unknown")]
    [InlineData("different-owner", "Unknown")]
    [InlineData("different-start", "Unknown")]
    [InlineData("different-root", "Unknown")]
    [InlineData("different-config", "Unknown")]
    [InlineData("different-common-dir", "Unknown")]
    [InlineData("intent-unavailable", "Unknown")]
    [InlineData("intent-write-failed", "Unknown")]
    [InlineData("starting", "Starting")]
    [InlineData("draining", "Draining")]
    [InlineData("maintenance", "Draining")]
    [InlineData("web-failed", "Failed")]
    [InlineData("runtime-failed", "Failed")]
    public async Task ClassifiesEvidenceWithoutTurningGapsIntoDeath(string scenario, string expected)
    {
        using var directory = new TempDirectory();
        var identity = Identity(directory.Path);
        var owner = Owner(identity);
        var snapshot = new FactoryTelemetrySnapshot(2, identity.Project, identity.TargetRepository, owner.RuntimeId!, 1,
            Now, "running", new(1, []), new(1, []));
        var runtime = new FactoryTelemetryReadResult(FactoryTelemetryAvailability.Live, null, snapshot);
        bool? held = true;
        var process = new OwnerProcessObservation(OwnerProcessState.Alive, false);
        var intent = new SupervisionRead(SupervisionAvailability.Manual, null, "", null);
        switch (scenario)
        {
            case "stale-runtime": runtime = runtime with { Availability = FactoryTelemetryAvailability.Stale }; break;
            case "foreign-runtime": runtime = runtime with { Snapshot = snapshot with { RuntimeId = "another-runtime" } }; break;
            case "foreign-project": runtime = runtime with { Snapshot = snapshot with { Project = "other" } }; break;
            case "foreign-repository": runtime = runtime with { Snapshot = snapshot with { TargetRepository = directory.Path } }; break;
            case "stale-control": owner = owner with { ObservedAtUtc = Now.AddSeconds(-11) }; break;
            case "future-control": owner = owner with { ObservedAtUtc = Now.AddSeconds(2) }; break;
            case "future-runtime": runtime = runtime with { Snapshot = snapshot with { ObservedAt = Now.AddSeconds(2) } }; break;
            case "web-liveness-only": owner = owner with { WebHealth = "unhealthy" }; break;
            case "reused-pid": process = new(OwnerProcessState.Reused, null); break;
            case "native-unavailable": process = new(OwnerProcessState.Unavailable, null); break;
            case "lock-unavailable": held = null; break;
            case "lock-absent": held = false; break;
            case "different-root": owner = owner with { Root = Path.Combine(directory.Path, "copied") }; break;
            case "different-config": owner = owner with { ConfigPath = Path.Combine(directory.Path, "other.json") }; break;
            case "different-common-dir": owner = owner with { GitCommonDirectory = Path.Combine(directory.Path, "other.git") }; break;
            case "intent-unavailable": intent = intent with { Availability = SupervisionAvailability.Unavailable }; break;
            case "intent-write-failed": owner = owner with { StopReason = "supervision-unavailable" }; break;
            case "starting": owner = owner with { State = "starting", RuntimeId = null }; break;
            case "draining": owner = owner with { State = "stopping", StopReason = "OperatorDrain" }; break;
            case "maintenance": owner = owner with { State = "stopping", StopReason = "Maintenance" }; break;
            case "web-failed": owner = owner with { StopReason = "WebFailure" }; break;
            case "runtime-failed": owner = owner with { StopReason = "RuntimeFailure" }; break;
        }
        var live = scenario switch
        {
            "different-owner" => owner with { RunId = Guid.NewGuid().ToString("N") },
            "different-start" => owner with { ProcessStartedAtUtc = owner.ProcessStartedAtUtc.AddTicks(1) },
            _ => owner
        };
        var observer = new DeploymentObserver(identity, new(() => owner, () => held, _ => process,
            _ => scenario switch
            {
                "pipe-unavailable" => throw new IOException("private canary"),
                "pipe-deadline" => throw new TimeoutException("private canary"),
                _ => Task.FromResult(new BackgroundControlResponse(true, live, null))
            }, () => runtime, () => intent), new Clock());
        var result = await observer.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Health.ToString());
        Assert.DoesNotContain("private canary", result.Reason);
        if (expected is "Unknown" or "Starting")
            Assert.Equal(SupervisionHealth.Unknown, result.ToPolicyObservation().Health);
    }

    [Theory]
    [InlineData(null, false, "Failed")]
    [InlineData("WebFailure", false, "Failed")]
    [InlineData("RuntimeFailure", false, "Failed")]
    [InlineData("OperatorDrain", false, "Stopped")]
    [InlineData("ForcedStop", false, "Stopped")]
    [InlineData("Maintenance", false, "Stopped")]
    [InlineData(null, true, "Unknown")]
    [InlineData("supervision-unavailable", false, "Unknown")]
    public async Task ExitedOwnerRetainsIntentAndDoesNotBorrowAnotherLock(string? reason, bool held, string expected)
    {
        using var directory = new TempDirectory();
        var identity = Identity(directory.Path);
        var owner = Owner(identity) with { StopReason = reason };
        var observer = new DeploymentObserver(identity, new(() => owner, () => held,
            _ => new(OwnerProcessState.Exited, true), _ => throw new InvalidOperationException("No dead-owner pipe probe"),
            () => throw new InvalidOperationException("Stale telemetry proves nothing"),
            () => new(SupervisionAvailability.Manual, null, "", null)), new Clock());
        Assert.Equal(expected, (await observer.ObserveAsync(TestContext.Current.CancellationToken)).Health.ToString());
    }

    [Fact]
    public async Task ChangedOwnerAfterControlCannotAuthorizeHealthyObservation()
    {
        using var directory = new TempDirectory();
        var identity = Identity(directory.Path);
        var owner = Owner(identity);
        var reads = 0;
        var observer = new DeploymentObserver(identity, new(() => ++reads == 1 ? owner : owner with { RuntimeId = "replacement" },
            () => true, _ => new(OwnerProcessState.Alive, false), _ => Task.FromResult(new BackgroundControlResponse(true, owner, null)),
            () => throw new InvalidOperationException(), () => new(SupervisionAvailability.Manual, null, "", null)), new Clock());
        var observation = await observer.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeploymentHealth.Unknown, observation.Health);
        Assert.Equal("owner-changed-during-probe", observation.Reason);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => TimeSpan.FromMinutes(5).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public async Task ProbeDeadlineUsesInjectedClockAndMissingRecordRemainsUnknown()
    {
        using var directory = new TempDirectory();
        var identity = Identity(directory.Path);
        var owner = Owner(identity);
        var clock = new InvocationDeadlineClock();
        var pending = new TaskCompletionSource<BackgroundControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new DeploymentObservationSources(() => owner, () => true, _ => new(OwnerProcessState.Alive, false),
            _ => pending.Task, () => throw new InvalidOperationException(), () => new(SupervisionAvailability.Manual, null, "", null));
        var observing = new DeploymentObserver(identity, sources, clock).ObserveAsync(TestContext.Current.CancellationToken);
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(SupervisionPolicy.ProbeBudget);
        var result = await observing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        pending.SetResult(new(false, null, null));
        Assert.Equal(DeploymentHealth.Unknown, result.Health);
        var absent = await new DeploymentObserver(identity, sources with { Owner = () => null }, clock)
            .ObserveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeploymentHealth.Unknown, absent.Health);
    }
}
