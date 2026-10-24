using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DeliverySlotCoordinatorTests
{
    [Fact]
    public async Task UnlabeledPlanRunsAllThreeRolesAndReleasesOnceAtMerge()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();

        var outcome = await fixture.RunAsync(bead, (role, token) =>
        {
            roles.Add(role);
            fixture.Advance(bead, role);
            return Task.FromResult(Success(role, bead, fixture.ReadState(bead)));
        });

        Assert.True(outcome.MergeReady, outcome.Diagnostic);
        Assert.Equal([AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Qa], roles);
        Assert.Equal("stage:merge", outcome.FinalState!.Stage);
        Assert.Equal("open", outcome.FinalState.Status);
        Assert.True(outcome.ClaimHandoffCompleted);
        Assert.Equal(2, fixture.ClaimWrites(bead).Length);
        Assert.Equal("open", fixture.ReadBead(bead).Status);
        Assert.Null(fixture.ReadBead(bead).Assignee);
    }

    [Fact]
    public async Task ReviewAndQaEntrySelectOnlyTheRemainingRoles()
    {
        using var reviewFixture = new SlotFixture();
        var reviewBead = reviewFixture.CreateTask("stage:review");
        var reviewRoles = new List<AgentRole>();
        var review = await reviewFixture.RunAsync(reviewBead, (role, _) =>
        {
            reviewRoles.Add(role);
            reviewFixture.Advance(reviewBead, role);
            return Task.FromResult(Success(role, reviewBead, reviewFixture.ReadState(reviewBead)));
        });

        Assert.True(review.MergeReady, review.Diagnostic);
        Assert.Equal([AgentRole.Reviewer, AgentRole.Qa], reviewRoles);

        using var qaFixture = new SlotFixture();
        var qaBead = qaFixture.CreateTask("stage:qa");
        var qaRoles = new List<AgentRole>();
        var qa = await qaFixture.RunAsync(qaBead, (role, _) =>
        {
            qaRoles.Add(role);
            qaFixture.Advance(qaBead, role);
            return Task.FromResult(Success(role, qaBead, qaFixture.ReadState(qaBead)));
        });

        Assert.True(qa.MergeReady, qa.Diagnostic);
        Assert.Equal([AgentRole.Qa], qaRoles);
    }

    [Fact]
    public async Task WorkflowChangeAfterClaimRefusesBeforeLaunchingAndSafelyReleasesOwnedClaim()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();
        var runner = new MutatingProcessRunner(request => SlotFixture.IsAcquire(bead, request), () =>
        {
            _ = fixture.Target.Run("bd", ["update", bead, "--add-label", "stage:qa"]);
            return Task.CompletedTask;
        });

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            throw new InvalidOperationException("Agent must not launch after workflow race.");
        }, runner);

        Assert.True(outcome.Disposition == DeliverySlotDisposition.Refused, outcome.Diagnostic);
        Assert.Empty(roles);
        Assert.True(outcome.ClaimHandoffCompleted);
        Assert.Equal(2, SlotFixture.ClaimWrites(runner.Requests, bead).Length);
        Assert.Equal("open", fixture.ReadBead(bead).Status);
        Assert.Null(fixture.ReadBead(bead).Assignee);
        Assert.Equal("stage:qa", fixture.ReadState(bead).Stage);
    }

    [Fact]
    public async Task CompetingOwnerAfterClaimIsNeverReleasedOrDispatched()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();
        var runner = new MutatingProcessRunner(request => SlotFixture.IsAcquire(bead, request),
            () => fixture.SimulateCompetingClaimAsync(bead));

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            throw new InvalidOperationException("Agent must not launch after ownership race.");
        }, runner);

        Assert.True(outcome.Disposition == DeliverySlotDisposition.Refused, outcome.Diagnostic);
        Assert.Empty(roles);
        Assert.False(outcome.ClaimHandoffCompleted);
        Assert.Single(SlotFixture.ClaimWrites(runner.Requests, bead));
        Assert.Equal("in_progress", fixture.ReadBead(bead).Status);
        Assert.Equal("other:worker", fixture.ReadBead(bead).Assignee);
    }

    [Fact]
    public async Task InterstageOwnerChangePreventsNextRoleAndPreservesForeignClaim()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();

        var outcome = await fixture.RunAsync(bead, async (role, _) =>
        {
            roles.Add(role);
            fixture.Advance(bead, role);
            await fixture.SimulateCompetingClaimAsync(bead);
            return Success(role, bead, fixture.ReadState(bead));
        });

        Assert.Equal(DeliverySlotDisposition.Failed, outcome.Disposition);
        Assert.Single(roles);
        Assert.False(outcome.ClaimHandoffCompleted);
        Assert.Single(fixture.ClaimWrites(bead));
        Assert.Equal("other:worker", fixture.ReadBead(bead).Assignee);
        Assert.Equal("in_progress", fixture.ReadBead(bead).Status);
    }

    [Fact]
    public async Task ReviewerRepairStopsWithoutFollowOnRoleOrClaimRelease()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask("stage:review");
        var roles = new List<AgentRole>();

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            fixture.Target.Run("bd", ["update", bead, "--remove-label", "stage:review", "--add-label", "stage:implement",
                "--set-metadata", "repair_cycles=1"]);
            return Task.FromResult(Success(role, bead, fixture.ReadState(bead)));
        });

        Assert.Equal(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, outcome.Disposition);
        Assert.Single(roles);
        Assert.False(outcome.ClaimHandoffCompleted);
        Assert.Single(fixture.ClaimWrites(bead));
        Assert.Equal("in_progress", fixture.ReadBead(bead).Status);
        Assert.Equal("nightweaver:demo", fixture.ReadBead(bead).Assignee);
        Assert.Equal("stage:implement", outcome.FinalState!.Stage);
        Assert.Equal(1, outcome.FinalState.RepairCycles);
    }

    [Fact]
    public async Task MissingVerifiedStateStopsWithoutReleasingTheClaim()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            var result = new AgentStageResult(role, bead, "unverified", string.Empty, null, []);
            return Task.FromResult(new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, result, 1, null, null, null));
        });

        Assert.Equal(DeliverySlotDisposition.Failed, outcome.Disposition);
        Assert.Single(roles);
        Assert.False(outcome.ClaimHandoffCompleted);
        Assert.Single(fixture.ClaimWrites(bead));
        Assert.Equal("in_progress", fixture.ReadBead(bead).Status);
    }

    [Fact]
    public async Task CancellationAfterRoleExecutionStopsWithClaimAndNoFollowOnRole()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.CancellationTokenOverride = cancellation.Token;
        var roles = new List<AgentRole>();

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        Assert.Equal(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, outcome.Disposition);
        Assert.Single(roles);
        Assert.False(outcome.ClaimHandoffCompleted);
        Assert.Single(fixture.ClaimWrites(bead));
        Assert.Equal("in_progress", fixture.ReadBead(bead).Status);
        Assert.Equal("nightweaver:demo", fixture.ReadBead(bead).Assignee);
    }

    [Fact]
    public async Task StaleStateAfterGuardedMergeReleaseIsNotReportedAsMergeReady()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask("stage:qa");
        var runner = new MutatingProcessRunner(request => SlotFixture.IsRelease(bead, request), () =>
        {
            _ = fixture.Target.Run("bd", ["update", bead, "--remove-label", "stage:merge", "--add-label", "stage:implement"]);
            return Task.CompletedTask;
        });

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            fixture.Advance(bead, role);
            return Task.FromResult(Success(role, bead, fixture.ReadState(bead)));
        }, runner);

        Assert.Equal(DeliverySlotDisposition.Failed, outcome.Disposition);
        Assert.False(outcome.MergeReady);
        Assert.False(outcome.ClaimHandoffCompleted);
        Assert.Equal(2, SlotFixture.ClaimWrites(runner.Requests, bead).Length);
        Assert.Equal("open", fixture.ReadBead(bead).Status);
        Assert.Null(fixture.ReadBead(bead).Assignee);
        Assert.Equal("stage:implement", fixture.ReadState(bead).Stage);
    }

    [Fact]
    public async Task RetryEscalationStopsSlotWithoutDuplicateClaimRelease()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var runner = new RecordingProcessRunner((request, token) => new SystemProcessRunner().RunAsync(request, token));

        var outcome = await fixture.RunAsync(bead, async (role, _) =>
        {
            await fixture.SimulateEscalationAsync(bead);
            var failure = new AgentOperationalFailure(AgentOperationalFailureKind.NonZeroExit, "fixture failure");
            var result = new AgentStageResult(role, bead, "failed", "failure.log", failure, []);
            var escalation = new NeedsHumanEscalationResult(true, true, true, true, []);
            return new OperationalRetryOutcome(OperationalRetryStatus.Escalated, result, 2,
                NeedsHumanCause.OperationalRetriesExhausted, escalation, null);
        }, runner);

        Assert.Equal(DeliverySlotDisposition.Escalated, outcome.Disposition);
        Assert.True(outcome.ClaimHandoffCompleted);
        Assert.Single(SlotFixture.ClaimWrites(runner.Requests, bead));
        Assert.Equal("open", fixture.ReadBead(bead).Status);
        Assert.Null(fixture.ReadBead(bead).Assignee);
        Assert.True(fixture.ReadState(bead).NeedsHuman);
    }

    [Fact]
    public async Task BlockedPlanBeadIsRejectedBeforeClaimOrLaunch()
    {
        using var fixture = new SlotFixture();
        var blocker = fixture.CreateTask();
        var bead = fixture.CreateTask();
        fixture.Target.Run("bd", ["dep", "add", bead, blocker]);
        var roles = new List<AgentRole>();

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            throw new InvalidOperationException("Blocked Bead must not launch.");
        });

        Assert.Equal(DeliverySlotDisposition.Refused, outcome.Disposition);
        Assert.Empty(roles);
        Assert.Empty(fixture.ClaimWrites(bead));
        Assert.Equal("open", fixture.ReadBead(bead).Status);
        Assert.Null(fixture.ReadBead(bead).Assignee);
    }

    [Fact]
    public async Task ProductPausedMergeAndMalformedPlanBeadsAreRejectedBeforeClaim()
    {
        using var fixture = new SlotFixture();
        var ineligible = new[]
        {
            fixture.CreateBead("feature"),
            fixture.CreateTask("planned"),
            fixture.CreateTask("product-decision-required"),
            fixture.CreateTask("needs-human"),
            fixture.CreateTask("stage:merge"),
            fixture.CreateTask("stage:review", "stage:qa")
        };

        foreach (var bead in ineligible)
        {
            var launched = false;
            var outcome = await fixture.RunAsync(bead, (role, _) =>
            {
                launched = true;
                throw new InvalidOperationException("Ineligible Bead must not launch.");
            });

            Assert.Equal(DeliverySlotDisposition.Refused, outcome.Disposition);
            Assert.False(launched);
            Assert.Empty(fixture.ClaimWrites(bead));
        }

        var missing = await fixture.RunAsync("missing-plan-bead", (_, _) =>
            throw new InvalidOperationException("Missing Bead must not launch."));
        Assert.Equal(DeliverySlotDisposition.Refused, missing.Disposition);
        Assert.Empty(fixture.ClaimWrites("missing-plan-bead"));
    }

    private static OperationalRetryOutcome Success(AgentRole role, string beadId, BeadWorkflowState verifiedState)
    {
        var result = new AgentStageResult(role, beadId, $"fixture-{role}", string.Empty, null, [], verifiedState);
        return new(OperationalRetryStatus.Succeeded, result, 1, null, null, null);
    }

    private sealed class SlotFixture : IDisposable
    {
        private readonly TempDirectory hostTemp = new();
        private readonly ProjectConfig project;

        internal SlotFixture()
        {
            Target = new BeadsFixture();
            Host = new HostPaths(hostTemp.Path);
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            var roles = AgentRoles.All.ToDictionary(role => role, _ => settings);
            project = new(ProjectSlug.Parse("demo"), Path.Combine(Host.Root, "demo.json"),
                new(Target.Path, "main"), new(1, 1, 1), new(3, 2), settings, new RoleSettings(roles));
        }

        internal BeadsFixture Target { get; }
        internal HostPaths Host { get; }
        internal CancellationToken? CancellationTokenOverride { get; set; }
        private IReadOnlyList<ProcessRequest> LastRequests { get; set; } = [];
        private ValidatedTargetRepository Repository => new(Target.Path, "main");

        internal string CreateTask(params string[] labels)
            => CreateBead("task", labels);

        internal string CreateBead(string issueType, params string[] labels)
        {
            var bead = Target.Create($"Delivery {Guid.NewGuid():N}", type: issueType);
            if (labels.Length > 0)
            {
                var args = new List<string> { "update", bead };
                foreach (var label in labels) { args.Add("--add-label"); args.Add(label); }
                Target.Run("bd", args);
            }
            return bead;
        }

        internal BeadSnapshot ReadBead(string beadId) => BeadsReader.Parse(
            Target.Run("bd", BeadsCommand.Create(Target.Path).Arguments)).ById[beadId];

        internal BeadWorkflowState ReadState(string beadId) => BeadWorkflowState.Parse(ReadBead(beadId));

        internal void Advance(string beadId, AgentRole role)
        {
            var next = role switch
            {
                AgentRole.Implementer => "stage:review",
                AgentRole.Reviewer => "stage:qa",
                AgentRole.Qa => "stage:merge",
                _ => throw new InvalidOperationException($"Unexpected role {role}.")
            };
            var current = ReadState(beadId).Stage;
            var args = new List<string> { "update", beadId };
            if (current is not null) { args.Add("--remove-label"); args.Add(current); }
            args.Add("--add-label");
            args.Add(next);
            Target.Run("bd", args);
        }

        internal async Task SimulateEscalationAsync(string beadId)
        {
            var escalationWriter = new BeadsWriter(new SystemProcessRunner());
            await escalationWriter.ApplyNeedsHumanAsync(Repository, beadId, project.RecoveryActor, "in_progress", CancellationToken.None);
            await escalationWriter.AddCommentAsync(Repository, beadId, "fixture needs-human diagnostic", CancellationToken.None);
            await escalationWriter.ReleaseClaimAsync(Repository, beadId, project.RecoveryActor, CancellationToken.None);
        }

        internal async Task SimulateCompetingClaimAsync(string beadId)
        {
            var systemRunner = new SystemProcessRunner();
            var beadsWriter = new BeadsWriter(systemRunner);
            await beadsWriter.ReleaseDispatchClaimAsync(Repository, beadId, project.RecoveryActor, "in_progress", CancellationToken.None);
            var request = BeadsCommand.CreateWrite(Target.Path, ["update", beadId, "--claim"], "other:worker");
            var result = await systemRunner.RunAsync(request, CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Fixture competing claim failed ({result.ExitCode}): {result.StandardError}{result.StandardOutput}");
        }

        internal async Task<DeliverySlotOutcome> RunAsync(string beadId,
            Func<AgentRole, CancellationToken, Task<OperationalRetryOutcome>> roleRunner,
            IProcessRunner? processRunner = null)
        {
            var runner = processRunner ?? new RecordingProcessRunner((request, token) =>
                new SystemProcessRunner().RunAsync(request, token));
            LastRequests = runner switch
            {
                RecordingProcessRunner recording => recording.Requests,
                MutatingProcessRunner mutating => mutating.Requests,
                _ => []
            };
            var stageRunner = new DeliveryRoleRunner((_, _, _, requestedBead, role, _, token, _) =>
            {
                Assert.Equal(beadId, requestedBead);
                return roleRunner(role, token);
            });
            return await new DeliverySlotCoordinator(runner, stageRunner)
                .RunAsync(Host, project, Repository, beadId,
                    CancellationTokenOverride ?? TestContext.Current.CancellationToken);
        }

        internal static bool IsAcquire(string bead, ProcessRequest request) => IsClaimWrite(bead, request, "open");
        internal static bool IsRelease(string bead, ProcessRequest request) => IsClaimWrite(bead, request, "in_progress");

        internal ProcessRequest[] ClaimWrites(string bead) => ClaimWrites(LastRequests, bead);

        internal static ProcessRequest[] ClaimWrites(IReadOnlyList<ProcessRequest> requests, string bead) =>
            requests.Where(request => IsClaimWrite(bead, request, null)).ToArray();

        private static bool IsClaimWrite(string bead, ProcessRequest request, string? expectedStatus)
        {
            var updateIndex = request.Arguments.ToList().IndexOf("update");
            if (request.FileName != "bd" || updateIndex < 0 || updateIndex + 1 >= request.Arguments.Count
                || request.Arguments[updateIndex + 1] != bead) return false;
            var statusIndex = request.Arguments.ToList().IndexOf("--if-status");
            var ownerIndex = request.Arguments.ToList().IndexOf("--if-assignee");
            return statusIndex >= 0 && ownerIndex >= 0 && (expectedStatus is null
                || statusIndex + 1 < request.Arguments.Count && request.Arguments[statusIndex + 1] == expectedStatus);
        }

        public void Dispose()
        {
            Target.Dispose();
            hostTemp.Dispose();
        }
    }

    private sealed class MutatingProcessRunner(
        Func<ProcessRequest, bool> shouldMutate,
        Func<Task> mutate) : IProcessRunner
    {
        private readonly SystemProcessRunner inner = new();
        private int didMutate;
        private readonly List<ProcessRequest> requests = [];
        internal IReadOnlyList<ProcessRequest> Requests => requests;

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            var result = await inner.RunAsync(request, cancellationToken);
            if (shouldMutate(request) && Interlocked.Exchange(ref didMutate, 1) == 0)
                await mutate();
            return result;
        }
    }
}
