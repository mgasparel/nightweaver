using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class PlanningSlotCoordinatorTests
{
    [Theory]
    [InlineData("backlog")]
    [InlineData("paused")]
    [InlineData("planned")]
    [InlineData("foreign")]
    [InlineData("blocked")]
    [InlineData("stage")]
    public async Task AdmissionRequiresExactOptInNativeReadinessAndUnownedWorkflow(string scenario)
    {
        using var fixture = new Fixture();
        fixture.State = scenario switch
        {
            "backlog" => fixture.State with { ReadyForPlanning = false },
            "paused" => fixture.State with { NeedsHuman = true },
            "planned" => fixture.State with { Planned = true },
            "stage" => fixture.State with { Stage = "stage:implement" },
            _ => fixture.State
        };
        if (scenario == "foreign") fixture.Owner = "someone";
        if (scenario == "blocked") fixture.Ready = false;
        var result = await fixture.RunAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(result.Diagnostic ?? "Confirmed handoff");
        Assert.Equal(PlanningSlotDisposition.Refused, result.Disposition);
        Assert.Equal(0, fixture.Acquisitions);
        Assert.Equal(0, fixture.Turns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardConflictNeverAdoptsOrReleasesSameActorCompetitor(bool uncertain)
    {
        using var fixture = new Fixture { Conflict = !uncertain, UncertainAcquire = uncertain };
        var result = await fixture.RunAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(result.Diagnostic ?? "Confirmed handoff");
        if (!uncertain)
        {
            Assert.Equal(PlanningSlotDisposition.Refused, result.Disposition);
            Assert.Equal(0, fixture.Turns);
            Assert.Equal(0, fixture.Releases);
        }
        else
        {
            Assert.Equal(PlanningSlotDisposition.Published, result.Disposition);
            Assert.Equal(3, fixture.Turns);
        }
        Assert.Equal(1, fixture.Acquisitions);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("readiness")]
    [InlineData("owner")]
    public async Task ClaimVerificationRefusesLateChangesAndPreservesWorkflow(string change)
    {
        using var fixture = new Fixture { AfterAcquire = change };
        var result = await fixture.RunAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(result.Diagnostic ?? "Confirmed handoff");
        Assert.Equal(0, fixture.Turns);
        Assert.Equal(change == "owner" ? 0 : 1, fixture.Releases);
        Assert.Equal(change == "owner" ? PlanningSlotDisposition.Failed : PlanningSlotDisposition.Refused, result.Disposition);
        if (change == "pause") Assert.True(fixture.State.NeedsHuman);
    }

    [Theory]
    [InlineData("stage:plan")]
    [InlineData("stage:plan-challenge")]
    [InlineData("stage:plan-approved")]
    public async Task VerifiedPublicationEndsAllOutgoingOperations(string stage)
    {
        using var fixture = new Fixture();
        fixture.State = fixture.State with { Stage = stage };
        var result = await fixture.RunAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(result.Diagnostic ?? "Confirmed handoff");
        Assert.Equal(PlanningSlotDisposition.Published, result.Disposition);
        Assert.True(result.ClaimHandoffCompleted);
        Assert.Equal(stage == "stage:plan" ? 3 : stage == "stage:plan-challenge" ? 2 : 1, fixture.Turns);
        Assert.Equal(0, fixture.Releases);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task StopBetweenTurnsRetainsStageAndNeverStartsNextTurn(int stopAfter, bool force)
    {
        using var fixture = new Fixture { StopAfter = stopAfter, Force = force };
        using var context = ContinuousAgentLaunchContext.Enter(fixture.Gate);
        var result = await fixture.RunAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(result.Diagnostic ?? "Confirmed handoff");
        Assert.Equal(stopAfter, fixture.Turns);
        Assert.Equal(stopAfter == 1 ? "stage:plan-challenge" : "stage:plan-approved", fixture.State.Stage);
        Assert.Equal(force ? 0 : 1, fixture.Releases);
        Assert.Equal(force ? PlanningSlotDisposition.Failed : PlanningSlotDisposition.Interrupted, result.Disposition);
        Assert.Equal(2, fixture.State.RepairCycles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainReleaseReconcilesOnceAndFailedFinalizationRetainsClaim(bool fail)
    {
        using var fixture = new Fixture { StopAfter = 1, LostReleaseResponse = true, FailRelease = fail };
        using var context = ContinuousAgentLaunchContext.Enter(fixture.Gate);
        var result = await fixture.RunAsync();
        TestContext.Current.TestOutputHelper?.WriteLine(result.Diagnostic ?? "Confirmed handoff");
        Assert.Equal(1, fixture.Releases);
        Assert.Equal(!fail, result.ClaimHandoffCompleted);
        Assert.Equal(fail ? PlanningSlotDisposition.Failed : PlanningSlotDisposition.Interrupted, result.Disposition);
        Assert.Equal(fail ? fixture.Project.RecoveryActor : null, fixture.Owner);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal ProjectConfig Project { get; }
        private readonly HostPaths paths;
        private readonly ValidatedTargetRepository repository;
        private readonly RecordingProcessRunner runner;
        internal ContinuousStopGate Gate { get; } = new();
        internal BeadWorkflowState State { get; set; } = new("product", "feature", "open", "stage:plan", false, false, false, 2)
            { ReadyForPlanning = true };
        internal string? Owner { get; set; }
        internal bool Ready { get; set; } = true;
        internal bool Conflict { get; init; }
        internal bool UncertainAcquire { get; init; }
        internal string? AfterAcquire { get; init; }
        internal int StopAfter { get; init; }
        internal bool Force { get; init; }
        internal bool LostReleaseResponse { get; init; }
        internal bool FailRelease { get; init; }
        internal int Acquisitions { get; private set; }
        internal int Releases { get; private set; }
        internal int Turns { get; private set; }
        private bool handedOff;

        internal Fixture()
        {
            paths = new(temp.CreateDirectory("host"));
            repository = new(temp.CreateDirectory("target"), "main");
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            Project = new(ProjectSlug.Parse("planning"), "config.json", new(repository.Path, "main"), new(2, 1, 10),
                new(3, 0), settings, new(AgentRoles.All.ToDictionary(role => role, _ => settings)));
            runner = new((request, _) =>
            {
                Assert.False(handedOff, "No outgoing operation is allowed after the receipt.");
                var args = request.Arguments.ToList();
                if (args.Contains("update"))
                {
                    var acquire = args[args.IndexOf("--if-status") + 1] == "open";
                    if (acquire)
                    {
                        Acquisitions++;
                        State = State with { Status = "in_progress" };
                        Owner = Project.RecoveryActor;
                        if (AfterAcquire == "pause") State = State with { NeedsHuman = true };
                        if (AfterAcquire == "readiness") Ready = false;
                        if (AfterAcquire == "owner") Owner = "foreign";
                        if (Conflict) return Task.FromResult(new ProcessResult(13, "", "guard mismatch"));
                        if (UncertainAcquire) return Task.FromResult(new ProcessResult(1, "", "lost response"));
                    }
                    else
                    {
                        Releases++;
                        if (!FailRelease) { State = State with { Status = "open" }; Owner = null; }
                        if (LostReleaseResponse) return Task.FromResult(new ProcessResult(1, "", "lost response"));
                    }
                    return Task.FromResult(new ProcessResult(0, Inventory(), ""));
                }
                var selected = true;
                if (args.Contains("ready") || args.Contains("--ready")) selected = Ready && (args.Contains("--unassigned") ? Owner is null : Owner == Project.RecoveryActor);
                return Task.FromResult(new ProcessResult(0, selected ? Inventory() : "[]", ""));
            });
        }

        private string Inventory() => JsonSerializer.Serialize(new[] { new
        {
            id = State.Id, issue_type = State.IssueType, status = State.Status, assignee = Owner,
            priority = 1, created_at = "2026-01-01T00:00:00Z",
            labels = new[] { State.Stage }.Where(s => s is not null)
                .Concat(State.ReadyForPlanning ? ["ready-for-planning"] : [])
                .Concat(State.NeedsHuman ? ["needs-human"] : []).Concat(State.Planned ? ["planned"] : []),
            metadata = new { repair_cycles = State.RepairCycles }
        } });

        internal Task<PlanningSlotOutcome> RunAsync() => new PlanningSlotCoordinator(runner, (host, project, target, id, role, actor, state, token, progress) =>
            RunTurn(host, project, target, id, role, actor, state, progress, token))
            .RunAsync(paths, Project, repository, State.Id, TestContext.Current.CancellationToken);

        private Task<OperationalRetryOutcome> RunTurn(HostPaths host, ProjectConfig project,
            ValidatedTargetRepository target, string id, AgentRole role, string actor, BeadWorkflowState expected,
            Action<OperationalRetryProgress>? progress, CancellationToken token)
        {
            Assert.Equal(State, expected);
            Turns++;
            var published = State.Stage == "stage:plan-approved";
            State = published ? State with { Stage = null, Planned = true, Status = "open" }
                : State with { Stage = role == AgentRole.Planner ? "stage:plan-challenge" : "stage:plan-approved" };
            if (published) { Owner = null; handedOff = true; }
            if (Turns == StopAfter) { if (Force) Gate.RequestForceStop(); else Gate.RequestStop(); }
            var receipt = published ? new DeliveryOutcomePublicationReceipt(AgentRole.Planner, AgentOutcomeChoice.Approved,
                State, null, true, false) : null;
            return Task.FromResult(new OperationalRetryOutcome(OperationalRetryStatus.Succeeded,
                new AgentStageResult(role, id, "turn", "log", null, [], VerifiedState: State, PublicationReceipt: receipt),
                1, null, null, null));
        }
        public void Dispose() => temp.Dispose();
    }
}
