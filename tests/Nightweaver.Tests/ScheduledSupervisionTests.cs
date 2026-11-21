using System.Xml.Linq;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Supervision;

namespace Nightweaver.Tests;

public sealed class ScheduledSupervisionTests
{
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
        Assert.Equal(1, fixture.Scheduler.Validations);
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
        internal RecordingScheduler Scheduler { get; } = new();
        internal Fixture()
        {
            Identity = SupervisionPolicyTests.Identity(Temp.Path);
            Store = new(new HostPaths(Temp.Path), ProjectSlug.Parse("demo"), Identity);
            Definition = ScheduledTaskDefinition.Build(Identity, Path.Combine(Temp.Path, "cli.exe"), ["a path\\"], Temp.Path);
        }
        internal Task<ScheduledSupervisionStatus> Apply(ScheduledSupervisionAction action) =>
            new ScheduledSupervision(Store, Identity, Definition, Scheduler).ApplyAsync(action, TestContext.Current.CancellationToken);
        public void Dispose() => Temp.Dispose();
    }

    private sealed class RecordingScheduler : ISupervisionScheduler
    {
        internal ScheduledTaskSnapshot? Snapshot { get; set; }
        internal Action? BeforeEffect { get; set; }
        internal bool LoseResponse { get; set; }
        internal bool Deny { get; set; }
        internal int Runs { get; private set; }
        internal int Validations { get; private set; }
        public ScheduledTaskSnapshot? Read(string name) => Snapshot;
        public void Validate(string xml, string sid) => Validations++;
        public void Create(string name, string xml, string sid) => Effect(() => Snapshot = new(xml, false));
        public void SetEnabled(string name, bool enabled) => Effect(() => Snapshot = Snapshot! with { Enabled = enabled });
        public void Run(string name) => Runs++;
        public void Delete(string name) => Effect(() => Snapshot = null);
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
