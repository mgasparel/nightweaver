using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using System.Text.Json;

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
        Assert.Equal(new string?[] { null, "stage:review", "stage:qa" },
            fixture.ExpectedStates.Select(state => state.Stage));
        Assert.All(fixture.ExpectedStates, state =>
        {
            Assert.Equal("in_progress", state.Status);
            Assert.Equal(0, state.RepairCycles);
        });
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
        using var fixture = new FastSlotFixture();
        var reviewBead = fixture.AddBead("task", "stage:review");
        var reviewRoles = new List<AgentRole>();
        var review = await fixture.RunAsync(reviewBead, (role, _) =>
        {
            reviewRoles.Add(role);
            fixture.Advance(reviewBead, role);
            return Task.FromResult(Success(role, reviewBead, fixture.ReadState(reviewBead)));
        });

        Assert.True(review.MergeReady, review.Diagnostic);
        Assert.Equal([AgentRole.Reviewer, AgentRole.Qa], reviewRoles);
        var reviewStates = fixture.ExpectedStatesFor(reviewBead);
        Assert.Equal(2, reviewStates.Length);
        Assert.Equal("stage:review", reviewStates[0].Stage);
        Assert.Equal("stage:qa", reviewStates[1].Stage);
        Assert.Equal("stage:merge", review.FinalState!.Stage);
        Assert.Equal("open", fixture.ReadBead(reviewBead).Status);
        Assert.Null(fixture.ReadBead(reviewBead).Assignee);
        var reviewClaims = fixture.Runner.ClaimWrites(reviewBead);
        Assert.Equal(2, reviewClaims.Length);
        Assert.Equal("open", FastBeadsProcessRunner.GuardedStatus(reviewClaims[0]));
        Assert.Equal("in_progress", FastBeadsProcessRunner.GuardedStatus(reviewClaims[1]));

        var qaBead = fixture.AddBead("task", "stage:qa");
        var qaRoles = new List<AgentRole>();
        var qa = await fixture.RunAsync(qaBead, (role, _) =>
        {
            qaRoles.Add(role);
            fixture.Advance(qaBead, role);
            return Task.FromResult(Success(role, qaBead, fixture.ReadState(qaBead)));
        });

        Assert.True(qa.MergeReady, qa.Diagnostic);
        Assert.Equal([AgentRole.Qa], qaRoles);
        var qaStates = fixture.ExpectedStatesFor(qaBead);
        Assert.Single(qaStates);
        Assert.Equal("stage:qa", qaStates[0].Stage);
        Assert.Equal("stage:merge", qa.FinalState!.Stage);
        Assert.Equal("open", fixture.ReadBead(qaBead).Status);
        Assert.Null(fixture.ReadBead(qaBead).Assignee);
        var qaClaims = fixture.Runner.ClaimWrites(qaBead);
        Assert.Equal(2, qaClaims.Length);
        Assert.Equal("open", FastBeadsProcessRunner.GuardedStatus(qaClaims[0]));
        Assert.Equal("in_progress", FastBeadsProcessRunner.GuardedStatus(qaClaims[1]));
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
    public async Task SameActorClaimWinnerIsNotReleasedWhenGuardedAcquireReturnsExitThirteen()
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();
        var runner = new MutatingProcessRunner(SlotFixture.IsRecoverableReadiness,
            () => fixture.SimulateClaimAsync(bead, fixture.RecoveryActor));

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            throw new InvalidOperationException("A losing same-actor claim must not launch an Agent.");
        }, runner);

        Assert.Equal(DeliverySlotDisposition.Refused, outcome.Disposition);
        Assert.Contains("exited 13", outcome.Diagnostic!, StringComparison.Ordinal);
        Assert.Empty(roles);
        Assert.False(outcome.ClaimHandoffCompleted);
        Assert.Single(SlotFixture.ClaimWrites(runner.Requests, bead));
        Assert.Equal("in_progress", fixture.ReadBead(bead).Status);
        Assert.Equal(fixture.RecoveryActor, fixture.ReadBead(bead).Assignee);
    }

    [Theory]
    [InlineData(3, "Reviewer")]
    [InlineData(4, "Qa")]
    public async Task RepairCounterChangeDuringInterstageReadinessStopsBeforeTheNextRole(
        int readinessQueryToMutate, string roleThatMustNotRun)
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();
        var recoverableReadinessCount = 0;
        var runner = new MutatingProcessRunner(request =>
                SlotFixture.IsRecoverableReadiness(request)
                && Interlocked.Increment(ref recoverableReadinessCount) == readinessQueryToMutate,
            () =>
            {
                fixture.Target.Run("bd", ["update", bead, "--set-metadata", "repair_cycles=1"]);
                return Task.CompletedTask;
            });

        var outcome = await fixture.RunAsync(bead, (role, _) =>
        {
            roles.Add(role);
            fixture.Advance(bead, role);
            return Task.FromResult(Success(role, bead, fixture.ReadState(bead)));
        }, runner);

        Assert.Equal(DeliverySlotDisposition.Failed, outcome.Disposition);
        Assert.Contains("workflow identity changed", outcome.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Enum.Parse<AgentRole>(roleThatMustNotRun), roles);
        AgentRole[] expectedRoles = roleThatMustNotRun == "Reviewer"
            ? [AgentRole.Implementer]
            : [AgentRole.Implementer, AgentRole.Reviewer];
        Assert.Equal(expectedRoles, roles);
        Assert.Equal(1, outcome.FinalState!.RepairCycles);
        Assert.Single(SlotFixture.ClaimWrites(runner.Requests, bead));
        Assert.Equal("in_progress", fixture.ReadBead(bead).Status);
        Assert.Equal(fixture.RecoveryActor, fixture.ReadBead(bead).Assignee);
    }

    [Theory]
    [InlineData("operational")]
    [InlineData("operational-cancelled")]
    [InlineData("needs-human")]
    [InlineData("cancelled")]
    public async Task IncompleteEscalationProgressSurvivesAndIsNotReleasedAgain(string failureKind)
    {
        using var fixture = new SlotFixture();
        var bead = fixture.CreateTask();
        var roles = new List<AgentRole>();
        var progress = new NeedsHumanEscalationResult(true, true, false, true, ["worktree cleanup failed"]);

        var outcome = await fixture.RunAsync(bead, async (role, _) =>
        {
            roles.Add(role);
            if (role == AgentRole.Implementer)
            {
                fixture.Advance(bead, role);
                return Success(role, bead, fixture.ReadState(bead));
            }

            await fixture.SimulateEscalationAsync(bead);
            var knownState = fixture.ReadState(bead);
            switch (failureKind)
            {
                case "operational":
                    throw new OperationalRetryEscalationIncompleteException(
                        "operational escalation cleanup failed", FailureOutcome(role, bead, knownState, progress));
                case "operational-cancelled":
                    throw new OperationalRetryEscalationIncompleteException(
                        "operational escalation cancelled", FailureOutcome(role, bead, knownState, progress),
                        new OperationCanceledException("escalation cancelled"));
                case "needs-human":
                    throw new NeedsHumanEscalationException("needs-human escalation cleanup failed", progress,
                        new InvalidOperationException("cleanup failed"));
                case "cancelled":
                    var cancellation = new OperationCanceledException("escalation cancelled");
                    cancellation.Data[nameof(NeedsHumanEscalationResult)] = progress;
                    throw cancellation;
                default:
                    throw new InvalidOperationException($"Unknown fixture failure kind '{failureKind}'.");
            }
        });

        Assert.Equal(failureKind is "cancelled" or "operational-cancelled"
            ? DeliverySlotDisposition.StoppedAtUnsupportedOutcome
            : DeliverySlotDisposition.Failed, outcome.Disposition);
        Assert.Equal([AgentRole.Implementer, AgentRole.Reviewer], roles);
        Assert.Equal(AgentRole.Reviewer, outcome.LastRole);
        Assert.Equal(progress, outcome.EscalationProgress);
        Assert.True(outcome.ClaimReleased);
        Assert.False(outcome.ClaimHandoffCompleted);
        if (failureKind is "operational" or "operational-cancelled")
        {
            Assert.Equal("review-invocation", outcome.LastStage!.StageResult.InvocationId);
            Assert.Equal("review.log", outcome.LastStage.StageResult.LogFilePath);
        }
        else
        {
            Assert.Null(outcome.LastStage);
        }
        Assert.Equal("stage:review", outcome.FinalState!.Stage);
        Assert.Single(fixture.ClaimWrites(bead));
        Assert.Equal("open", fixture.ReadBead(bead).Status);
        Assert.Null(fixture.ReadBead(bead).Assignee);
        Assert.True(fixture.ReadState(bead).NeedsHuman);
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
        using var fixture = new FastSlotFixture();
        var ineligible = new[]
        {
            (fixture.AddBead("feature"), "accepts Plan Beads of type task"),
            (fixture.AddBead("task", "planned"), "planned or paused"),
            (fixture.AddBead("task", "product-decision-required"), "planned or paused"),
            (fixture.AddBead("task", "needs-human"), "planned or paused"),
            (fixture.AddBead("task", "stage:merge"), "cannot accept stage 'stage:merge'"),
            (fixture.AddBead("task", "stage:review", "stage:qa"), "invalid workflow state")
        };

        foreach (var (bead, diagnostic) in ineligible)
        {
            var launched = false;
            var outcome = await fixture.RunAsync(bead, (role, _) =>
            {
                launched = true;
                throw new InvalidOperationException("Ineligible Bead must not launch.");
            });

            Assert.Equal(DeliverySlotDisposition.Refused, outcome.Disposition);
            Assert.False(launched);
            Assert.Contains(diagnostic, outcome.Diagnostic!, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fixture.Runner.ClaimWrites(bead));
        }

        var missing = await fixture.RunAsync("missing-plan-bead", (_, _) =>
            throw new InvalidOperationException("Missing Bead must not launch."));
        Assert.Equal(DeliverySlotDisposition.Refused, missing.Disposition);
        Assert.Contains("was not found", missing.Diagnostic!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Runner.ClaimWrites("missing-plan-bead"));
        Assert.Empty(fixture.Runner.WriteRequests);
    }

    private static OperationalRetryOutcome Success(AgentRole role, string beadId, BeadWorkflowState verifiedState)
    {
        var result = new AgentStageResult(role, beadId, $"fixture-{role}", string.Empty, null, [], verifiedState);
        return new(OperationalRetryStatus.Succeeded, result, 1, null, null, null);
    }

    private static OperationalRetryOutcome FailureOutcome(AgentRole role, string beadId,
        BeadWorkflowState verifiedState, NeedsHumanEscalationResult progress)
    {
        var failure = new AgentOperationalFailure(AgentOperationalFailureKind.NonZeroExit, "fixture stage failed");
        var result = new AgentStageResult(role, beadId, "review-invocation", "review.log", failure,
            ["latest review invocation failed"], verifiedState);
        return new(OperationalRetryStatus.Escalated, result, 2, NeedsHumanCause.OperationalRetriesExhausted,
            progress, null);
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
        internal string RecoveryActor => project.RecoveryActor;
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

        internal async Task SimulateClaimAsync(string beadId, string actor)
        {
            var request = BeadsCommand.CreateWrite(Target.Path, ["update", beadId, "--claim"], actor);
            var result = await new SystemProcessRunner().RunAsync(request, CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Fixture claim failed ({result.ExitCode}): {result.StandardError}{result.StandardOutput}");
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
            var stageRunner = new DeliveryRoleRunner((_, _, _, requestedBead, role, _, expectedState, token, _) =>
            {
                Assert.Equal(beadId, requestedBead);
                ExpectedStates.Add(expectedState);
                return roleRunner(role, token);
            });
            return await new DeliverySlotCoordinator(runner, stageRunner)
                .RunAsync(Host, project, Repository, beadId,
                    CancellationTokenOverride ?? TestContext.Current.CancellationToken);
        }

        internal static bool IsAcquire(string bead, ProcessRequest request) => IsClaimWrite(bead, request, "open");
        internal static bool IsRelease(string bead, ProcessRequest request) => IsClaimWrite(bead, request, "in_progress");
        internal static bool IsRecoverableReadiness(ProcessRequest request) => request.FileName == "bd"
            && request.Arguments.Contains("--ready", StringComparer.Ordinal)
            && request.Arguments.Contains("--assignee", StringComparer.Ordinal)
            && request.Arguments.Contains("in_progress", StringComparer.Ordinal);

        internal List<BeadWorkflowState> ExpectedStates { get; } = [];

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

    private sealed class FastSlotFixture : IDisposable
    {
        private readonly BeadsApplicationFixture target = new();
        private readonly TempDirectory hostTemp = new();
        private readonly ProjectConfig project;

        internal FastSlotFixture()
        {
            Host = new HostPaths(hostTemp.Path);
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            var roles = AgentRoles.All.ToDictionary(role => role, _ => settings);
            project = new(ProjectSlug.Parse("demo"), Path.Combine(Host.Root, "demo.json"),
                new(target.Path, "main"), new(1, 1, 1), new(3, 2), settings, new RoleSettings(roles));
            Runner = new FastBeadsProcessRunner(target.Path, project.RecoveryActor);
        }

        internal HostPaths Host { get; }
        internal FastBeadsProcessRunner Runner { get; }
        private ValidatedTargetRepository Repository => new(target.Path, "main");
        private List<(string BeadId, BeadWorkflowState State)> ExpectedStates { get; } = [];

        internal string AddBead(string issueType, params string[] labels) => Runner.AddBead(issueType, labels);

        internal BeadSnapshot ReadBead(string beadId) => Runner.ReadBead(beadId);

        internal BeadWorkflowState ReadState(string beadId) => BeadWorkflowState.Parse(ReadBead(beadId));

        internal BeadWorkflowState[] ExpectedStatesFor(string beadId) => ExpectedStates
            .Where(item => item.BeadId == beadId).Select(item => item.State).ToArray();

        internal void Advance(string beadId, AgentRole role) => Runner.Advance(beadId, role);

        internal async Task<DeliverySlotOutcome> RunAsync(string beadId,
            Func<AgentRole, CancellationToken, Task<OperationalRetryOutcome>> roleRunner)
        {
            var stageRunner = new DeliveryRoleRunner((_, _, _, requestedBead, role, _, expectedState, token, _) =>
            {
                Assert.Equal(beadId, requestedBead);
                ExpectedStates.Add((beadId, expectedState));
                return roleRunner(role, token);
            });
            return await new DeliverySlotCoordinator(Runner, stageRunner)
                .RunAsync(Host, project, Repository, beadId, TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            target.Dispose();
            hostTemp.Dispose();
        }
    }

    private sealed class FastBeadsProcessRunner(string repositoryPath, string recoveryActor) : IProcessRunner
    {
        private sealed class FakeBead(string id, string issueType, IReadOnlyList<string> labels)
        {
            internal string Id { get; } = id;
            internal string IssueType { get; } = issueType;
            internal List<string> Labels { get; } = [.. labels];
            internal string Status { get; set; } = "open";
            internal string? Assignee { get; set; }
            internal int RepairCycles { get; set; }
        }

        private readonly List<FakeBead> beads = [];
        private readonly List<ProcessRequest> requests = [];

        internal IEnumerable<ProcessRequest> WriteRequests => requests.Where(IsWrite);

        internal string AddBead(string issueType, IReadOnlyList<string> labels)
        {
            var id = $"fast-{beads.Count + 1:D3}";
            beads.Add(new FakeBead(id, issueType, labels));
            return id;
        }

        internal BeadSnapshot ReadBead(string beadId)
        {
            var bead = beads.Single(item => item.Id == beadId);
            return BeadsReader.Parse(JsonSerializer.Serialize(new[] { ToRow(bead) })).ById[beadId];
        }

        internal void Advance(string beadId, AgentRole role)
        {
            var bead = beads.Single(item => item.Id == beadId);
            var currentStage = bead.Labels.SingleOrDefault(label => label.StartsWith("stage:", StringComparison.Ordinal));
            if (currentStage is not null) bead.Labels.Remove(currentStage);
            bead.Labels.Add(role switch
            {
                AgentRole.Implementer => "stage:review",
                AgentRole.Reviewer => "stage:qa",
                AgentRole.Qa => "stage:merge",
                _ => throw new InvalidOperationException($"Unexpected role {role}.")
            });
        }

        internal ProcessRequest[] ClaimWrites(string beadId) => requests
            .Where(request => IsWrite(request) && IsClaimWrite(request, beadId)).ToArray();

        internal static string GuardedStatus(ProcessRequest request)
        {
            var args = request.Arguments;
            var index = args.ToList().IndexOf("--if-status");
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : "";
        }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            if (request.FileName != "bd")
                throw new InvalidOperationException($"Unexpected executable '{request.FileName}' in fast coordinator fixture.");
            if (!string.Equals(request.WorkingDirectory, repositoryPath, StringComparison.Ordinal))
                throw new InvalidOperationException($"bd request used unexpected working directory '{request.WorkingDirectory}'.");

            if (request.Arguments.SequenceEqual(BeadsCommand.Create(repositoryPath).Arguments))
                return Task.FromResult(Json(beads.Select(ToRow)));
            if (request.Arguments.SequenceEqual(BeadsCommand.CreateUnassignedReadiness(repositoryPath).Arguments))
                return Task.FromResult(Json(beads.Where(bead => bead.Status == "open" && bead.Assignee is null).Select(ToRow)));
            if (request.Arguments.SequenceEqual(BeadsCommand.CreateRecoverableReadiness(repositoryPath, recoveryActor).Arguments))
                return Task.FromResult(Json(beads.Where(bead => bead.Status == "in_progress" && bead.Assignee == recoveryActor).Select(ToRow)));

            return Task.FromResult(Update(request));
        }

        private ProcessResult Update(ProcessRequest request)
        {
            var args = request.Arguments;
            var updateIndex = args.ToList().IndexOf("update");
            if (updateIndex < 0 || updateIndex + 1 >= args.Count)
                throw new InvalidOperationException($"Unexpected bd request: {string.Join(' ', args)}");
            var updateArgs = args.Skip(updateIndex).ToArray();
            var bead = beads.SingleOrDefault(item => item.Id == updateArgs[1]);
            if (bead is null) return new ProcessResult(13, "", "guarded update target was not found");

            var acquire = new[] { "update", bead.Id, "--if-status", "open", "--if-assignee", "", "--status", "in_progress",
                "--assignee", recoveryActor, "--json" };
            if (updateArgs.SequenceEqual(acquire))
            {
                if (bead.Status != "open" || bead.Assignee is not null)
                    return new ProcessResult(13, "", "guarded claim state changed");
                bead.Status = "in_progress";
                bead.Assignee = recoveryActor;
                return new ProcessResult(0, "{}", "");
            }

            var release = new[] { "update", bead.Id, "--status", "open", "--assignee", "", "--if-assignee", recoveryActor,
                "--if-status", "in_progress", "--json" };
            if (updateArgs.SequenceEqual(release))
            {
                if (bead.Status != "in_progress" || bead.Assignee != recoveryActor)
                    return new ProcessResult(13, "", "guarded release state changed");
                bead.Status = "open";
                bead.Assignee = null;
                return new ProcessResult(0, "{}", "");
            }

            throw new InvalidOperationException($"Unexpected bd write in fast coordinator fixture: {string.Join(' ', args)}");
        }

        private static ProcessResult Json(IEnumerable<object> rows) =>
            new(0, JsonSerializer.Serialize(rows), "");

        private static object ToRow(FakeBead bead) => new
        {
            id = bead.Id,
            status = bead.Status,
            issue_type = bead.IssueType,
            priority = 2,
            created_at = "2026-01-01T00:00:00Z",
            assignee = bead.Assignee,
            labels = bead.Labels,
            metadata = new { repair_cycles = bead.RepairCycles }
        };

        private static bool IsWrite(ProcessRequest request) => request.FileName == "bd"
            && request.Arguments.Contains("update", StringComparer.Ordinal)
            && request.Arguments.Contains("--if-status", StringComparer.Ordinal);

        private static bool IsClaimWrite(ProcessRequest request, string beadId)
        {
            var args = request.Arguments;
            var updateIndex = args.ToList().IndexOf("update");
            return updateIndex >= 0 && updateIndex + 1 < args.Count && args[updateIndex + 1] == beadId
                && args.Contains("--if-status", StringComparer.Ordinal)
                && args.Contains("--if-assignee", StringComparer.Ordinal);
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
