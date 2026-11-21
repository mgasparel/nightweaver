using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Cli;
using Nightweaver.Supervision;

namespace Nightweaver.Tests;

public sealed class SupervisionTickTests
{
    [Fact]
    public void FiniteInvocationEpochSurvivesProcessReconstructionButNotBootOrLogonChange()
    {
        var epoch = BackgroundManagerApplication.TickEpoch(123, "S-1-5-5-1-2");
        SupervisionValidation.ValidateId(epoch);
        Assert.Equal(epoch, BackgroundManagerApplication.TickEpoch(123, "S-1-5-5-1-2"));
        Assert.NotEqual(epoch, BackgroundManagerApplication.TickEpoch(124, "S-1-5-5-1-2"));
        Assert.NotEqual(epoch, BackgroundManagerApplication.TickEpoch(123, "S-1-5-5-1-3"));
    }

    [Fact]
    public async Task ReservesBeforeLaunchAndReconstructedTickRequiresExactHealthyRun()
    {
        var fixture = new Fixture();
        await fixture.Qualify();
        Assert.Equal("attempt-pending", (await fixture.Run()).Outcome);
        Assert.Equal(1, fixture.Launches);
        var pending = fixture.State.PendingAttempt!;
        Assert.Equal(pending.Id, fixture.Launched);
        fixture.Observation = fixture.Observation with { Health = DeploymentHealth.Healthy,
            Owner = fixture.Observation.Owner! with { RunId = pending.Id }, OwnerLockHeld = true,
            Process = new(OwnerProcessState.Alive, false) };
        Assert.Equal("recovered", (await fixture.Run()).Outcome);
        Assert.Null(fixture.State.PendingAttempt);
        Assert.Equal(1, fixture.State.Attempts);
        for (var i = 0; i < 12; i++) { fixture.Advance(); await fixture.Run(); }
        Assert.Equal(1, fixture.State.Attempts); // stable health never refunds a retry
        Assert.Equal(1, fixture.Launches);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("confirm")]
    [InlineData("reserve")]
    [InlineData("launch")]
    public async Task InterveningStopAtEveryAsyncEffectBoundaryPreventsLaunch(string boundary)
    {
        var fixture = new Fixture();
        await fixture.Qualify();
        fixture.Boundary = name => { if (name == boundary) fixture.Stop(); };
        if (boundary == "reserve") await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Run());
        else await fixture.Run();
        Assert.Equal(0, fixture.Launches);
        Assert.False(fixture.State.DesiredRunning);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stopped")]
    [InlineData("maintenance")]
    [InlineData("exhausted")]
    [InlineData("attention")]
    [InlineData("unknown")]
    [InlineData("draining")]
    [InlineData("starting")]
    public async Task ProtectedStatesNeverLaunch(string scenario)
    {
        var fixture = new Fixture();
        await fixture.Qualify();
        fixture.State = scenario switch
        {
            "disabled" => fixture.State with { Enabled = false },
            "stopped" => fixture.State with { DesiredRunning = false },
            "maintenance" => fixture.State with { MaintenanceToken = Guid.NewGuid().ToString("N") },
            "exhausted" => fixture.State with { Attempts = 3, Exhausted = true, NextDueUtc = null },
            "attention" => fixture.State with { Attention = "clock-discontinuity" },
            _ => fixture.State
        };
        fixture.Observation = fixture.Observation with { Health = scenario switch
        {
            "unknown" => DeploymentHealth.Unknown,
            "draining" => DeploymentHealth.Draining,
            "starting" => DeploymentHealth.Starting,
            _ => DeploymentHealth.Failed
        }};
        await fixture.Run();
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(0, fixture.Drains);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebAndRuntimeFailureDrainWithoutChangingIntentOrForcing(bool web)
    {
        var fixture = new Fixture();
        fixture.Observation = fixture.Observation with { Reason = web ? "WebFailure" : "RuntimeFailure",
            Process = new(OwnerProcessState.Alive, false), OwnerLockHeld = true };
        await fixture.Qualify();
        Assert.Equal("failure-draining", (await fixture.Run()).Outcome);
        Assert.Equal(1, fixture.Drains);
        Assert.Equal(0, fixture.Launches);
        Assert.True(fixture.State.DesiredRunning);
        Assert.Equal(0, fixture.State.Attempts);
    }

    [Theory]
    [InlineData("capture-failed")]
    [InlineData("descendants")]
    [InlineData("owner-changed")]
    [InlineData("write-failed")]
    [InlineData("write-unconfirmed")]
    public async Task UncertainEvidenceOrPublicationNeverGrantsLaunch(string scenario)
    {
        var fixture = new Fixture();
        await fixture.Qualify();
        if (scenario == "capture-failed") fixture.Capture = IncidentCaptureState.Failed;
        if (scenario == "descendants") fixture.Observation = fixture.Observation with { Process = new(OwnerProcessState.Exited, null) };
        fixture.Boundary = name =>
        {
            if (name == "capture" && scenario == "owner-changed")
                fixture.Observation = fixture.Observation with { Owner = fixture.Observation.Owner! with { RunId = Guid.NewGuid().ToString("N") } };
            if (name == "reserve" && scenario == "write-failed") throw new IOException("denied");
            if (name == "reserve" && scenario == "write-unconfirmed") fixture.State = fixture.State with { Revision = 0 };
        };
        if (scenario == "write-failed") await Assert.ThrowsAsync<IOException>(() => fixture.Run());
        else if (scenario == "write-unconfirmed") await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Run());
        else await fixture.Run();
        Assert.Equal(0, fixture.Launches);
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("launched")]
    public async Task CrashedAttemptIsReconciledWithoutReplay(string crash)
    {
        var fixture = new Fixture();
        await fixture.Qualify();
        fixture.Boundary = name => { if (name == crash) throw new IOException("interrupted"); };
        await Assert.ThrowsAsync<IOException>(() => fixture.Run());
        Assert.NotNull(fixture.State.PendingAttempt);
        var launches = fixture.Launches;
        fixture.Boundary = null;
        Assert.Equal("needs-attention", (await fixture.Run()).Outcome);
        Assert.Equal(launches, fixture.Launches);
        Assert.NotNull(fixture.State.PendingAttempt);
        Assert.Equal(1, fixture.State.Attempts);
        Assert.Null(fixture.State.NextDueUtc);
    }

    [Fact]
    public async Task UnacknowledgedDifferentOrStartingOwnerRetainsReservationAndAttention()
    {
        var fixture = new Fixture();
        await fixture.Qualify();
        await fixture.Run();
        fixture.Observation = fixture.Observation with { Health = DeploymentHealth.Starting,
            Owner = fixture.Observation.Owner! with { RunId = fixture.Launched! },
            OwnerLockHeld = true, Process = new(OwnerProcessState.Alive, false) };
        Assert.Equal("needs-attention", (await fixture.Run()).Outcome);
        Assert.NotNull(fixture.State.PendingAttempt);
        Assert.Equal("unresolved-attempt", fixture.State.Attention);
        fixture.Observation = fixture.Observation with { Health = DeploymentHealth.Healthy,
            Owner = fixture.Observation.Owner! with { RunId = Guid.NewGuid().ToString("N") } };
        await fixture.Run();
        Assert.NotNull(fixture.State.PendingAttempt);
        Assert.Equal(1, fixture.Launches);
    }

    [Fact]
    public async Task ShortLivedStartsExhaustAcrossReconstructedTicks()
    {
        var fixture = new Fixture();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await fixture.Qualify();
            // Due time survives all qualification; advance by minute ticks until eligible.
            while (fixture.State.NextDueUtc > fixture.Observation.Utc) { fixture.Advance(); await fixture.Run(); }
            if (fixture.State.PendingAttempt is null) await fixture.Run();
            Assert.NotNull(fixture.State.PendingAttempt);
            Assert.Equal(attempt, fixture.State.Attempts);
            fixture.Observation = fixture.Observation with { Owner = fixture.Observation.Owner! with { RunId = fixture.Launched! } };
            Assert.Equal("attempt-settled-absent", (await fixture.Run()).Outcome);
        }
        Assert.True(fixture.State.Exhausted);
        fixture.Advance();
        await fixture.Run();
        Assert.Equal(3, fixture.Launches);
    }

    [Fact]
    public void TickParserRequiresSelectorAndRejectsLaunchOptions()
    {
        Assert.Equal(Nightweaver.Cli.BackgroundAction.Tick,
            Nightweaver.Cli.BackgroundCommandParser.Parse(["background", "tick", "--project", "demo"], out _).Action);
        Assert.Throws<UsageException>(() =>
            Nightweaver.Cli.BackgroundCommandParser.Parse(["background", "tick", "--project", "demo", "--force"], out _));
    }

    [Fact]
    public async Task FiniteCommandHoldsManualLaunchLockAndPersistsBeforeFixtureLaunch()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.Path);
        var slug = ProjectSlug.Parse("demo");
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(identity.ConfigPath)!);
        await File.WriteAllTextAsync(identity.ConfigPath,
            System.Text.Json.JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = Path.Combine(temp.Path, "target") } }), token);
        var store = new SupervisionStore(paths, slug, identity);
        var fixture = new Fixture();
        await fixture.Qualify();
        await store.SaveAsync(null, fixture.State with { Identity = identity, Revision = 0 }, token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var options = new Nightweaver.Cli.BackgroundCommandOptions(Nightweaver.Cli.BackgroundAction.Tick,
            new ProjectSelector.ByConfigPath(identity.ConfigPath));
        var environment = new AppEnvironment(temp.Path, temp.Path, new StringWriter(), new StringWriter());
        var command = BackgroundManagerApplication.RunAsync(options, environment, token,
            (_, _, _) => Task.FromResult<SupervisionLifecycle?>(new(store)),
            (lifecycle, _) => Task.FromResult(new SupervisionTick(new(lifecycle.Read,
                (revision, next, saveToken) => store.SaveAsync(revision, next, saveToken),
                _ => Task.FromResult(fixture.Observation),
                async (observation, attempts, prepareToken) =>
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(prepareToken);
                    return new(new(Guid.NewGuid().ToString("N"), observation, attempts,
                        ResourceIncidentSignal.None, new(IncidentCaptureState.NotRequired, null, null), "", "", "fixture"), true);
                },
                (_, _) => throw new InvalidOperationException("Settled owner must not be drained."),
                async (id, permit, launchToken) =>
                {
                    Assert.Equal(id, new SupervisionStore(paths, slug, identity).Read().State!.PendingAttempt!.Id);
                    Assert.True(await permit(launchToken));
                    Assert.Throws<IOException>(() => BackgroundFileLock.AcquireStart(paths, slug));
                    launches++;
                }))));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        try
        {
            // Both another tick and manual stop fail before changing intent or launching.
            Assert.Equal(1, await BackgroundManagerApplication.RunAsync(options, environment, token,
                (_, _, _) => throw new InvalidOperationException("Must contend before opening lifecycle.")));
            Assert.Equal(1, await BackgroundManagerApplication.RunAsync(options with { Action = Nightweaver.Cli.BackgroundAction.Stop },
                environment, token, (_, _, _) => throw new InvalidOperationException("Must contend before intent mutation.")));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await command);
        Assert.Equal(1, launches);
        Assert.True(store.Read().State!.DesiredRunning);
        Assert.Equal(1, store.Read().State!.Attempts);
        Assert.NotNull(store.Read().State!.PendingAttempt);
        using var unlocked = BackgroundFileLock.AcquireStart(paths, slug);
    }

    [Theory]
    [InlineData("WebFailure")]
    [InlineData("RuntimeFailure")]
    [InlineData("OperatorDrain")]
    [InlineData("Maintenance")]
    [InlineData("ForcedStop")]
    [InlineData("healthy")]
    public async Task RecoveryControlPreservesIntentAndRefusesDeliberateStop(string reason)
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.Path);
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var slug = ProjectSlug.Parse("demo");
        var token = TestContext.Current.CancellationToken;
        var store = new SupervisionStore(paths, slug, identity);
        await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null), token);
        var deployment = DeploymentObservationTests.Identity(temp.Path);
        var owner = DeploymentObservationTests.Owner(deployment) with { StopReason = reason };
        if (reason != "healthy") owner = owner with { Failure = new(SupervisionPolicyTests.Start, "RuntimeFailure", owner.RunId,
            owner.RuntimeId, owner.ProcessId, owner.ProcessStartedAtUtc) };
        using var state = new BackgroundOwnerState(owner, (_, _, _) => Task.CompletedTask);
        var options = new Nightweaver.Cli.BackgroundOwnerOptions(temp.Path, identity.ConfigPath, "demo", owner.RunId, owner.WebUrl);
        var response = await BackgroundOwnerApplication.HandleRequestAsync(new("recover", owner.RunId), options,
            paths, null!, state, "fixture", () => null, new(store), token);
        Assert.Equal(reason != "healthy" && !DeploymentObserver.IsDeliberate(reason), response.Accepted);
        Assert.Equal(reason, state.Current.StopReason);
        Assert.Equal(1, store.Read().State!.Revision);
        Assert.True(store.Read().State!.DesiredRunning);
        Assert.Equal(owner.Failure, state.Current.Failure);
    }

    private sealed class Fixture
    {
        internal SupervisionState State;
        internal DeploymentObservation Observation;
        internal int Launches, Drains;
        internal string? Launched;
        internal Action<string>? Boundary;
        internal IncidentCaptureState Capture = IncidentCaptureState.NotRequired;
        private int probes;
        internal Fixture()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tick-fixture"));
            var identity = DeploymentObservationTests.Identity(root);
            State = SupervisionPolicy.SetIntent(SupervisionState.Manual(new(identity.Root, identity.ConfigPath,
                identity.GitCommonDirectory, "S-1-5-21-1")), true, true, null);
            Observation = new(1, SupervisionPolicyTests.Start, Guid.NewGuid().ToString("N"), 0,
                identity, DeploymentObservationTests.Owner(identity), DeploymentHealth.Failed, "owner-exited", false,
                new(OwnerProcessState.Exited, true), null, null);
        }
        internal void Advance() => Observation = Observation with { Utc = Observation.Utc.AddMinutes(1),
            MonotonicTicks = Observation.MonotonicTicks + TimeSpan.TicksPerMinute };
        internal void Stop() => State = SupervisionPolicy.SetIntent(State, State.Enabled, false, null) with { Revision = State.Revision + 1 };
        internal async Task Qualify() { await Run(); Advance(); await Run(); Advance(); }
        internal Task<SupervisionTickResult> Run()
        {
            probes = 0;
            return new SupervisionTick(new(
                () => new(SupervisionAvailability.Available, State, "fixture", null),
                (revision, next, _) =>
                {
                    Assert.Equal(State.Revision, revision);
                    SupervisionValidation.Validate(next);
                    var reserving = State.PendingAttempt is null && next.PendingAttempt is not null;
                    State = next with { Revision = revision + 1 };
                    if (reserving) Boundary?.Invoke("reserve");
                    return Task.FromResult(State);
                },
                _ => { if (++probes == 2) Boundary?.Invoke("confirm"); return Task.FromResult(Observation); },
                (observation, attempts, _) =>
                {
                    Boundary?.Invoke("capture");
                    var incident = new DeploymentIncident(Guid.NewGuid().ToString("N"), observation, attempts,
                        ResourceIncidentSignal.None, new(Capture, null, null), "", "", "fixture");
                    return Task.FromResult(new RecoveryEvidence(incident,
                        observation.Process?.DescendantsSettled == true));
                },
                (_, _) => { Drains++; return Task.FromResult(true); },
                async (id, permit, token) =>
                {
                    Assert.Equal(id, State.PendingAttempt!.Id);
                    Boundary?.Invoke("launch");
                    if (await permit(token)) { Launches++; Launched = id; Boundary?.Invoke("launched"); }
                })).RunAsync(TestContext.Current.CancellationToken);
        }
    }
}
