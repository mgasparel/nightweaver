using System.Xml.Linq;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Supervision;

namespace Nightweaver.Tests;

public sealed class ScheduledSupervisionTests
{
    [Theory]
    [InlineData("requested")]
    [InlineData("lost-response")]
    [InlineData("stop")]
    [InlineData("disabled-task")]
    [InlineData("foreign-task")]
    public async Task OwnerRequestRetainsReservationAndChecksFreshIntent(string scenario)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        await fixture.Apply(ScheduledSupervisionAction.Install);
        await fixture.Apply(ScheduledSupervisionAction.Enable);
        var state = fixture.Store.Read().State!;
        var id = Guid.NewGuid().ToString("N");
        state = await fixture.Store.SaveAsync(state.Revision, state with { Attempts = 1,
            PendingAttempt = new(id, state.Generation, 1, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), null) }, token);
        if (scenario == "disabled-task") fixture.Scheduler.OwnerSnapshot = fixture.Scheduler.OwnerSnapshot! with { Enabled = false };
        if (scenario == "foreign-task") fixture.Scheduler.OwnerSnapshot = fixture.Scheduler.OwnerSnapshot! with
            { Xml = fixture.Scheduler.OwnerSnapshot!.Xml.Replace("IgnoreNew", "Parallel", StringComparison.Ordinal) };
        fixture.Scheduler.LoseRunResponse = scenario == "lost-response";
        var beforeRuns = fixture.Scheduler.Runs;
        async Task<bool> Permit(CancellationToken ct)
        {
            if (scenario == "stop")
                await fixture.Store.SaveAsync(state.Revision, SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.Stop), ct);
            return true;
        }
        Task Request() => ScheduledOwnerLaunch.RequestAsync(fixture.Store, fixture.OwnerDefinition, fixture.Scheduler, id, Permit, token);
        if (scenario == "requested") await Request();
        else await Assert.ThrowsAnyAsync<Exception>(Request);
        var requested = scenario is "requested" or "lost-response";
        Assert.Equal(beforeRuns + (requested ? 1 : 0), fixture.Scheduler.Runs);
        Assert.Equal(id, fixture.Store.Read().State!.PendingAttempt!.Id);
        Assert.False(fixture.Store.Read().State!.PendingAttempt!.LaunchConsumed);
        if (requested) Assert.Equal(new[] { id, state.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), "http://127.0.0.1:5000" }, fixture.Scheduler.LastParameters);
    }

    [Fact]
    public async Task ScheduledOwnerWaitsForTickLockThenReadsLaterStop()
    {
        using var fixture = new Fixture();
        var paths = new HostPaths(fixture.Temp.Path);
        var slug = ProjectSlug.Parse("demo");
        var token = TestContext.Current.CancellationToken;
        await fixture.Apply(ScheduledSupervisionAction.Install);
        using var command = Nightweaver.Background.BackgroundFileLock.AcquireStart(paths, slug);
        var waiting = Nightweaver.Background.BackgroundOwnerApplication.AcquireScheduledCommandAsync(paths, slug, token);
        Assert.False(waiting.IsCompleted);
        var state = fixture.Store.Read().State!;
        await fixture.Store.SaveAsync(state.Revision, SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.Stop), token);
        command.Dispose();
        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.True(fixture.Store.Read().State!.Suppressed);
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate")]
    [InlineData("stop")]
    [InlineData("disable")]
    [InlineData("maintenance")]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("unreserved")]
    public async Task OwnerConsumesExactReservationOnceAndRejectsRevokedRequests(string scenario)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        await fixture.Apply(ScheduledSupervisionAction.Install);
        await fixture.Apply(ScheduledSupervisionAction.Enable);
        var state = fixture.Store.Read().State!;
        var id = Guid.NewGuid().ToString("N");
        var generation = state.Generation;
        state = state with { Attempts = 1, PendingAttempt = new(id, generation, 1, DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"), null) };
        state = scenario switch
        {
            "duplicate" => state with { PendingAttempt = state.PendingAttempt! with { LaunchConsumed = true } },
            "stop" => SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.Stop),
            "disable" => SupervisionPolicy.SetIntent(state, false, false, null),
            "maintenance" => SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.EnterMaintenance),
            "unreserved" => state with { PendingAttempt = null },
            _ => state
        };
        await fixture.Store.SaveAsync(state.Revision, state, token);
        var before = File.ReadAllBytes(fixture.Store.Path);
        if (scenario != "valid")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ScheduledOwnerLaunch.ConsumeAsync(fixture.Store,
                scenario == "foreign" ? Guid.NewGuid().ToString("N") : id, scenario == "stale" ? generation - 1 : generation, token));
            Assert.Equal(before, File.ReadAllBytes(fixture.Store.Path));
            return;
        }
        await ScheduledOwnerLaunch.ConsumeAsync(fixture.Store, id, generation, token);
        Assert.True(fixture.Store.Read().State!.PendingAttempt!.LaunchConsumed);
        var fresh = new SupervisionStore(new HostPaths(fixture.Temp.Path), ProjectSlug.Parse("demo"), fixture.Identity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScheduledOwnerLaunch.ConsumeAsync(fresh, id, generation, token));
        Assert.Equal(1, fresh.Read().State!.Attempts);
    }

    [Fact]
    public void OwnerDefinitionHasNoTriggersAndNoRuntimeDeadline()
    {
        using var fixture = new Fixture();
        var xml = XDocument.Parse(fixture.OwnerDefinition.Xml);
        var ns = ScheduledTaskDefinition.Namespace;
        Assert.Empty(xml.Root!.Element(ns + "Triggers")!.Elements());
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("IgnoreNew", xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("true", xml.Descendants(ns + "AllowStartOnDemand").Single().Value);
        Assert.Contains("$(Arg0)", xml.Descendants(ns + "Arguments").Single().Value, StringComparison.Ordinal);
        Assert.Contains("$(Arg1)", xml.Descendants(ns + "Arguments").Single().Value, StringComparison.Ordinal);
        Assert.Contains("$(Arg2)", xml.Descendants(ns + "Arguments").Single().Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledOwnerParserRequiresValidGenerationAndLoopbackUrl()
    {
        var id = Guid.NewGuid().ToString("N");
        string[] Arguments(string generation, string urls) => ["background", "owner", "--root", Path.GetFullPath("."),
            "--config", Path.GetFullPath("probe.json"), "--project", "demo", "--run-id", id,
            "--scheduled-generation", generation, "--urls", urls];
        var options = BackgroundCommandParser.Parse(Arguments("12", "http://127.0.0.1:51234"), out _);
        Assert.Equal(12, options.Owner!.ScheduledGeneration);
        Assert.Equal(id, options.Owner.RunId);
        Assert.Equal("http://127.0.0.1:51234", options.Owner.Urls);
        Assert.Throws<UsageException>(() => BackgroundCommandParser.Parse(Arguments("-1", "http://127.0.0.1:51234"), out _));
        Assert.Throws<UsageException>(() => BackgroundCommandParser.Parse(Arguments("$(Arg1)", "http://127.0.0.1:51234"), out _));
        Assert.Throws<UsageException>(() => BackgroundCommandParser.Parse(Arguments("12", "http://example.com:51234"), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialRegistrationCanBeRepairedWithoutRunningOwner(bool missingOwner)
    {
        using var fixture = new Fixture();
        await fixture.Apply(ScheduledSupervisionAction.Install);
        if (missingOwner) fixture.Scheduler.OwnerSnapshot = null;
        else fixture.Scheduler.Snapshot = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Apply(ScheduledSupervisionAction.Enable));
        var status = await fixture.Apply(ScheduledSupervisionAction.Install);
        Assert.False(status.Registration!.Enabled);
        Assert.False(status.OwnerRegistration!.Enabled);
        Assert.Equal(0, fixture.Scheduler.Runs);
    }

    [Theory]
    [InlineData("status", BackgroundAction.SupervisionStatus)]
    [InlineData("install", BackgroundAction.SupervisionInstall)]
    [InlineData("enable", BackgroundAction.SupervisionEnable)]
    [InlineData("disable", BackgroundAction.SupervisionDisable)]
    [InlineData("uninstall", BackgroundAction.SupervisionUninstall)]
    internal void CommandsRequireExplicitSupervisionVerb(string verb, BackgroundAction expected)
    {
        var options = BackgroundCommandParser.Parse(["background", "supervision", verb, "--project", "demo"], out var help);
        Assert.False(help);
        Assert.Equal(expected, options.Action);
        Assert.Throws<UsageException>(() => BackgroundCommandParser.Parse(
            ["background", "supervision", verb, "--project", "demo", "--force"], out _));
    }

    [Fact]
    public void DefinitionBindsIdentityQuotesArgumentsAndKeepsOneRetryAuthority()
    {
        using var fixture = new Fixture();
        var xml = XDocument.Parse(fixture.Definition.Xml);
        var ns = ScheduledTaskDefinition.Namespace;
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.All(xml.Descendants(ns + "UserId"), e => Assert.Equal(fixture.Identity.UserSid, e.Value));
        Assert.Equal("IgnoreNew", xml.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("PT1M", xml.Descendants(ns + "Interval").Single().Value);
        Assert.Equal("PT4M", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Empty(xml.Descendants(ns + "RestartOnFailure"));
        Assert.Equal("false", xml.Root!.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value);
        Assert.Contains("\"a path\\\\\"", xml.Descendants(ns + "Arguments").Single().Value, StringComparison.Ordinal);
        var other = ScheduledTaskDefinition.Build(fixture.Identity with { ConfigPath = Path.Combine(fixture.Temp.Path, "other.json") },
            Path.Combine(fixture.Temp.Path, "cli.exe"), [], fixture.Temp.Path);
        Assert.NotEqual(fixture.Definition.Name, other.Name);
        Assert.Throws<InvalidDataException>(() => ScheduledTaskDefinition.Build(fixture.Identity, "relative.exe", [], fixture.Temp.Path));
    }

    [Fact]
    public async Task ManualStatusDoesNotWriteAndInstallStartsDisabledBeforeSchedulerCreate()
    {
        using var fixture = new Fixture();
        var status = await fixture.Apply(ScheduledSupervisionAction.Status);
        Assert.Equal(SupervisionAvailability.Manual, status.Supervision.Availability);
        Assert.False(File.Exists(fixture.Store.Path));
        fixture.Scheduler.BeforeEffect = () =>
        {
            Assert.False(fixture.Store.Read().State!.Enabled);
            Assert.False(fixture.Store.Read().State!.DesiredRunning);
        };
        status = await fixture.Apply(ScheduledSupervisionAction.Install);
        Assert.False(status.Registration!.Enabled);
        Assert.Equal(0, fixture.Scheduler.Runs);
        Assert.Equal(2, fixture.Scheduler.Validations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnableRequestsFirstTickAndPreservesSpentBudget(bool exhausted)
    {
        using var fixture = new Fixture();
        await fixture.Apply(ScheduledSupervisionAction.Install);
        var state = fixture.Store.Read().State!;
        await fixture.Store.SaveAsync(state.Revision, state with { Attempts = exhausted ? 3 : 1, Exhausted = exhausted }, TestContext.Current.CancellationToken);
        fixture.Scheduler.BeforeEffect = () => Assert.True(fixture.Store.Read().State!.DesiredRunning);
        var status = await fixture.Apply(ScheduledSupervisionAction.Enable);
        Assert.True(status.Registration!.Enabled);
        Assert.True(status.Supervision.State!.Enabled);
        Assert.Equal(exhausted ? 3 : 1, status.Supervision.State.Attempts);
        Assert.Equal(exhausted, status.Supervision.State.Exhausted);
        Assert.Equal(1, fixture.Scheduler.Runs);
    }

    [Theory]
    [InlineData(ScheduledSupervisionAction.Disable)]
    [InlineData(ScheduledSupervisionAction.Uninstall)]
    internal async Task SuppressionPrecedesSchedulerMutationAndSurvivesFreshStore(ScheduledSupervisionAction action)
    {
        using var fixture = new Fixture();
        await fixture.Apply(ScheduledSupervisionAction.Install);
        await fixture.Apply(ScheduledSupervisionAction.Enable);
        fixture.Scheduler.BeforeEffect = () => Assert.True(fixture.Store.Read().State!.Suppressed);
        var status = await fixture.Apply(action);
        Assert.True(status.Supervision.State!.Suppressed);
        Assert.Null(status.Supervision.State.PendingAttempt);
        if (action == ScheduledSupervisionAction.Disable) Assert.False(status.Registration!.Enabled);
        else Assert.Null(status.Registration);
        var fresh = new SupervisionStore(new HostPaths(fixture.Temp.Path), ProjectSlug.Parse("demo"), fixture.Identity);
        Assert.True(fresh.Read().State!.Suppressed);
        Assert.Equal(1, fixture.Scheduler.Runs);
    }

    [Theory]
    [InlineData(ScheduledSupervisionAction.Install)]
    [InlineData(ScheduledSupervisionAction.Enable)]
    [InlineData(ScheduledSupervisionAction.Disable)]
    [InlineData(ScheduledSupervisionAction.Uninstall)]
    internal async Task LostMutationResponseReconcilesFromActualDefinition(ScheduledSupervisionAction action)
    {
        using var fixture = new Fixture();
        if (action != ScheduledSupervisionAction.Install) await fixture.Apply(ScheduledSupervisionAction.Install);
        fixture.Scheduler.LoseResponse = true;
        var status = await fixture.Apply(action);
        Assert.Equal(action == ScheduledSupervisionAction.Uninstall, status.Registration is null);
        Assert.Equal(action == ScheduledSupervisionAction.Enable, status.Supervision.State!.DesiredRunning);
        Assert.Equal(action == ScheduledSupervisionAction.Enable ? 1 : 0, fixture.Scheduler.Runs);
    }

    [Theory]
    [InlineData(ScheduledSupervisionAction.Install)]
    [InlineData(ScheduledSupervisionAction.Enable)]
    [InlineData(ScheduledSupervisionAction.Disable)]
    [InlineData(ScheduledSupervisionAction.Uninstall)]
    internal async Task DenialIsVisibleWithTruthfulIntent(ScheduledSupervisionAction action)
    {
        using var fixture = new Fixture();
        if (action != ScheduledSupervisionAction.Install)
        {
            await fixture.Apply(ScheduledSupervisionAction.Install);
            if (action != ScheduledSupervisionAction.Enable) await fixture.Apply(ScheduledSupervisionAction.Enable);
        }
        fixture.Scheduler.Deny = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Apply(action));
        Assert.Equal(action == ScheduledSupervisionAction.Enable, fixture.Store.Read().State!.DesiredRunning);
        Assert.Equal(action == ScheduledSupervisionAction.Enable ? 0 : action == ScheduledSupervisionAction.Install ? 0 : 1,
            fixture.Scheduler.Runs);
    }

    [Theory]
    [InlineData("Command")]
    [InlineData("UserId")]
    [InlineData("LogonType")]
    [InlineData("ExecutionTimeLimit")]
    [InlineData("Description")]
    public async Task ForeignDefinitionsRejectAllActionsWithoutMutation(string element)
    {
        using var fixture = new Fixture();
        await fixture.Apply(ScheduledSupervisionAction.Install);
        var xml = XDocument.Parse(fixture.Scheduler.Snapshot!.Xml);
        xml.Descendants(ScheduledTaskDefinition.Namespace + element).First().Value = "foreign";
        fixture.Scheduler.Snapshot = new(xml.ToString(), false);
        var revision = fixture.Store.Read().State!.Revision;
        foreach (var action in Enum.GetValues<ScheduledSupervisionAction>())
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Apply(action));
        Assert.Equal(revision, fixture.Store.Read().State!.Revision);
        Assert.Equal(0, fixture.Scheduler.Runs);
    }

    [Fact]
    public async Task StopAndMaintenanceCannotBeReversedByTickAndEnableRejectsPendingReservation()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Apply(ScheduledSupervisionAction.Enable));
        await fixture.Apply(ScheduledSupervisionAction.Install);
        var state = fixture.Store.Read().State!;
        state = await fixture.Store.SaveAsync(state.Revision, state with { MaintenanceToken = Guid.NewGuid().ToString("N") }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Apply(ScheduledSupervisionAction.Enable));
        state = await fixture.Store.SaveAsync(state.Revision, state with { MaintenanceToken = null,
            Attempts = 1, PendingAttempt = new(Guid.NewGuid().ToString("N"), state.Generation, 1, DateTimeOffset.UtcNow,
                Guid.NewGuid().ToString("N"), null) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Apply(ScheduledSupervisionAction.Enable));
        var disabled = await fixture.Apply(ScheduledSupervisionAction.Disable);
        Assert.Equal(state.PendingAttempt, disabled.Supervision.State!.PendingAttempt);
        Assert.True(disabled.Supervision.State.Generation > disabled.Supervision.State.PendingAttempt!.Generation);
        Assert.Equal(1, disabled.Supervision.State.Attempts);
        Assert.True(disabled.Supervision.State.Suppressed);
    }

    private sealed class Fixture : IDisposable
    {
        internal TempDirectory Temp { get; } = new();
        internal SupervisionIdentity Identity { get; }
        internal SupervisionStore Store { get; }
        internal ScheduledTaskDefinition Definition { get; }
        internal ScheduledTaskDefinition OwnerDefinition { get; }
        internal RecordingScheduler Scheduler { get; } = new();
        internal Fixture()
        {
            Identity = SupervisionPolicyTests.Identity(Temp.Path);
            Store = new(new HostPaths(Temp.Path), ProjectSlug.Parse("demo"), Identity);
            Definition = ScheduledTaskDefinition.Build(Identity, Path.Combine(Temp.Path, "cli.exe"), ["a path\\"], Temp.Path);
            OwnerDefinition = ScheduledTaskDefinition.Owner(Identity, Path.Combine(Temp.Path, "cli.exe"), "demo");
        }
        internal Task<ScheduledSupervisionStatus> Apply(ScheduledSupervisionAction action) =>
            new ScheduledSupervision(Store, Identity, Definition, OwnerDefinition, Scheduler).ApplyAsync(action, TestContext.Current.CancellationToken);
        public void Dispose() => Temp.Dispose();
    }

    private sealed class RecordingScheduler : ISupervisionScheduler
    {
        internal ScheduledTaskSnapshot? Snapshot { get; set; }
        internal ScheduledTaskSnapshot? OwnerSnapshot { get; set; }
        internal Action? BeforeEffect { get; set; }
        internal bool LoseResponse { get; set; }
        internal bool Deny { get; set; }
        internal int Runs { get; private set; }
        internal int Validations { get; private set; }
        internal bool LoseRunResponse { get; set; }
        internal string[]? LastParameters { get; private set; }
        public ScheduledTaskSnapshot? Read(string name) => name.EndsWith("-Owner", StringComparison.Ordinal) ? OwnerSnapshot : Snapshot;
        public void Validate(string xml, string sid) => Validations++;
        private void Set(string name, ScheduledTaskSnapshot? value)
        {
            if (name.EndsWith("-Owner", StringComparison.Ordinal)) OwnerSnapshot = value;
            else Snapshot = value;
        }
        public void Create(string name, string xml, string sid) => Effect(() => Set(name, new(xml, false)));
        public void SetEnabled(string name, bool enabled) => Effect(() => Set(name, Read(name)! with { Enabled = enabled }));
        public void Run(string name, string[]? parameters = null)
        {
            Runs++;
            LastParameters = parameters;
            if (LoseRunResponse) throw new IOException("Run reply lost after request.");
        }
        public void Delete(string name) => Effect(() => Set(name, null));
        public string Canonicalize(string xml) => XDocument.Parse(xml).ToString(SaveOptions.DisableFormatting);
        private void Effect(Action action)
        {
            BeforeEffect?.Invoke();
            if (Deny) throw new UnauthorizedAccessException("fixture policy denial");
            action();
            if (LoseResponse) throw new IOException("fixture lost response");
        }
    }
}
