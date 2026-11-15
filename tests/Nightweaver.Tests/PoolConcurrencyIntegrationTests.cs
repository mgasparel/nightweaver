using System.Collections.Concurrent;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Tests.ManualDeliveryPilotFixture;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;

namespace Nightweaver.Tests;

public sealed class PoolConcurrencyIntegrationTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task DeliveryCompletesWhileMergerPreparationIsHeld()
    {
        await using var fixture = await ManualPilot.CreateAsync(seedProfile: ManualPilotSeedProfile.ParentlessOnly,
            cancellationToken: TestContext.Current.CancellationToken);
        var paths = new HostPaths(fixture.NightweaverRoot);
        var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), paths);
        var repository = new ValidatedTargetRepository(fixture.Repository, "main");
        var heldBead = await fixture.CreateMergeGateTaskAsync("Held singleton Merger", TestContext.Current.CancellationToken);
        fixture.MergerDiagnosticsBeadId = heldBead;
        var environment = fixture.CreateEnvironment();
        var system = new SystemProcessRunner();
        var processFailures = new ConcurrentQueue<string>();
        var roleOutcomes = new ConcurrentQueue<string>();
        var roles = new ConcurrentQueue<(string Bead, AgentRole Role)>();
        var implementationInvocations = new ConcurrentQueue<string>();
        var mergerStarts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var mergerPreparing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdMergerPreparation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryWorktree = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var implementationOutcome = new TaskCompletionSource<OperationalRetryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryStop = new ContinuousStopGate();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            var merged = new Dictionary<string, string?>(request.Environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in environment) merged[pair.Key] = pair.Value;
            var result = await system.RunAsync(request with
            {
                FileName = request.FileName == "gh" ? Path.Combine(fixture.FakeCliRoot, "gh.exe") : request.FileName,
                Environment = merged
            }, token);
            if (result.ExitCode != 0)
                processFailures.Enqueue($"{request.FileName} {string.Join(' ', request.Arguments)}: {result.ExitCode}: {result.StandardError}");
            return result;
        });

        var preparer = new AgentInvocationPreparer();
        var launcher = new AgentStageTargetFixture.FixtureEnvironmentOverlayProcessLauncher(environment);
        async Task<PreparedAgentInvocation> Prepare(HostPaths host, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktree, CancellationToken token)
        {
            roles.Enqueue((context.BeadId, role));
            if (context.BeadId == heldBead && role == AgentRole.Merger)
            {
                mergerPreparing.TrySetResult(worktree);
                // Keep the singleton reservation held without starting a native Merger Agent.
                await holdMergerPreparation.Task.WaitAsync(token);
            }
            if (context.BeadId == fixture.ParentlessTaskId && role == AgentRole.Implementer)
                deliveryWorktree.TrySetResult(worktree);
            return await preparer.PrepareAsync(host, config, role, context, worktree, token);
        }

        Task<AgentExecutionResult> Execute(HostPaths host, ProjectSlug slug,
            PreparedAgentInvocation invocation, CancellationToken token)
        {
            var bead = invocation.EnvironmentOverrides["NIGHTWEAVER_BEAD_ID"];
            if (invocation.Purpose == "merger")
                mergerStarts.AddOrUpdate(bead, 1, (_, count) => count + 1);
            if (bead == fixture.ParentlessTaskId && invocation.Purpose == "implementer")
                implementationInvocations.Enqueue(invocation.InvocationId);
            return AgentInvocationExecutor.ExecuteAsync(host, slug, invocation, launcher, token);
        }

        var stages = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
        var retries = new OperationalRetryCoordinator(runner, stages: stages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        DeliveryRoleRunner deliveryRole = async (host, config, target, bead, role, actor, state, token, progress) =>
        {
            var outcome = await retries.RunAsync(host, config, target, bead, role, actor, token, progress, state);
            roleOutcomes.Enqueue($"{bead} {role}: {JsonSerializer.Serialize(outcome)}");
            if (bead == fixture.ParentlessTaskId && role == AgentRole.Implementer)
            {
                implementationOutcome.TrySetResult(outcome);
                deliveryStop.RequestStop();
            }
            return outcome;
        };
        MergerRoleRunner mergerRole = (host, config, target, bead, role, actor, state, token, progress) =>
            retries.RunAsync(host, config, target, bead, role, actor, token, progress, state);
        var deliverySlot = new DeliverySlotCoordinator(runner, deliveryRole);
        var mergerSlot = new MergerSlotCoordinator(runner, mergerRole);
        var delivery = new DeliveryPoolCoordinator(paths, project, repository, runner,
            runSlot: (host, config, target, bead, token) => deliverySlot.RunAsync(host, config, target, bead, token),
            stopGate: deliveryStop, slotLifetimeToken: lifetime.Token);
        var merger = new MergerPoolCoordinator(paths, project, repository, runner,
            runSlot: (host, config, target, bead, attemptStarted, token) =>
                mergerSlot.RunAsync(host, config, target, bead, attemptStarted, token),
            slotLifetimeToken: lifetime.Token);
        Exception? primary = null;
        Exception? cleanupFailure = null;

        try
        {
            var mergerEvaluation = await merger.EvaluateAsync(lifetime.Token);
            var held = Assert.Single(mergerEvaluation.Admitted);
            Assert.Equal(heldBead, held.BeadId);
            Assert.Equal(1, merger.CapacityLimit);
            var mergerWorktree = await mergerPreparing.Task.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.Equal(Path.GetFullPath(fixture.WorktreePath(heldBead)), Path.GetFullPath(mergerWorktree));
            Assert.True(Directory.Exists(mergerWorktree));

            var heldState = await fixture.ReadBeadAsync(heldBead, lifetime.Token);
            Assert.Equal("in_progress", heldState.GetProperty("status").GetString());
            Assert.Equal(project.RecoveryActor, heldState.GetProperty("assignee").GetString());
            Assert.False(held.Completion.IsCompleted);
            Assert.Equal(heldBead, Assert.Single(merger.ActiveReservations).BeadId);
            Assert.Empty(mergerStarts);
            Assert.Empty((await merger.EvaluateAsync(lifetime.Token)).Admitted);

            var deliveryBead = fixture.ParentlessTaskId;
            fixture.ReleaseGate(deliveryBead);
            var deliveryEvaluation = await delivery.EvaluateAsync(lifetime.Token);
            var admitted = Assert.Single(deliveryEvaluation.Admitted);
            Assert.Equal(deliveryBead, admitted.BeadId);

            var implementation = await implementationOutcome.Task.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.True(implementation.Succeeded, implementation.StageResult.Failure?.Reason);
            var implementationPath = await deliveryWorktree.Task.WaitAsync(TimeSpan.FromMinutes(1), lifetime.Token);
            Assert.Equal(Path.GetFullPath(fixture.WorktreePath(deliveryBead)), Path.GetFullPath(implementationPath));
            Assert.False(held.Completion.IsCompleted);
            Assert.Equal(heldBead, Assert.Single(merger.ActiveReservations).BeadId);
            Assert.Empty(mergerStarts);

            var completed = await admitted.Completion.WaitAsync(TimeSpan.FromMinutes(3), lifetime.Token);
            Assert.True(completed.ClaimHandoffCompleted, completed.Diagnostic);
            Assert.Equal("stage:review", completed.Outcome?.FinalState?.Stage);
            Assert.Empty(delivery.ActiveReservations);
            Assert.Equal(new[] { AgentRole.Implementer },
                roles.Where(item => item.Bead == deliveryBead).Select(item => item.Role));
            Assert.Single(implementationInvocations);

            var finalBead = await fixture.ReadBeadAsync(deliveryBead, lifetime.Token);
            Assert.Equal("open", finalBead.GetProperty("status").GetString());
            Assert.True(!finalBead.TryGetProperty("assignee", out var assignee) || assignee.ValueKind == JsonValueKind.Null);
            Assert.Contains(finalBead.GetProperty("labels").EnumerateArray(),
                label => label.GetString() == "stage:review");
            Assert.True(Directory.Exists(implementationPath));
            Assert.Equal($"nightweaver/{deliveryBead}",
                (await fixture.ReadWorktreeBranchAsync(deliveryBead, lifetime.Token)).Trim());
            Assert.True(File.Exists(Path.Combine(implementationPath, $"manual-delivery-{deliveryBead}.txt")));

            Assert.False(held.Completion.IsCompleted);
            Assert.Equal(heldBead, Assert.Single(merger.ActiveReservations).BeadId);
            Assert.Empty((await merger.EvaluateAsync(lifetime.Token)).Admitted);

            // Cancel while the Merger still waits in preparation; never release its gate.
            lifetime.Cancel();
            await Task.WhenAll(
                (Task)delivery.WaitForActiveSlotsAsync(CancellationToken.None),
                (Task)merger.WaitForActiveSlotsAsync(CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

            var mergerCompletion = await held.Completion.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(MergerSlotDisposition.Canceled, mergerCompletion.Outcome?.Disposition);
            Assert.True(mergerCompletion.LocalCapacityReleased);
            Assert.Empty(delivery.ActiveReservations);
            Assert.Empty(merger.ActiveReservations);
            Assert.Empty(mergerStarts);
            Assert.Equal(new[] { AgentRole.Merger }, roles.Where(item => item.Bead == heldBead).Select(item => item.Role));
            Assert.Equal($"nightweaver/{heldBead}",
                (await fixture.ReadWorktreeBranchAsync(heldBead, CancellationToken.None)).Trim());
        }
        catch (Exception exception)
        {
            primary = exception;
            fixture.PreserveForFailure();
            TestContext.Current.TestOutputHelper?.WriteLine($"Pool concurrency fixture preserved at {fixture.Root}");
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join('\n', roleOutcomes));
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join('\n', processFailures));
            throw;
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await Task.WhenAll(
                    (Task)delivery.WaitForActiveSlotsAsync(CancellationToken.None),
                    (Task)merger.WaitForActiveSlotsAsync(CancellationToken.None))
                    .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                fixture.PreserveForFailure();
                cleanupFailure = cleanup;
                TestContext.Current.TestOutputHelper?.WriteLine($"Pool cleanup also failed: {cleanup}");
            }
        }

        if (primary is null && cleanupFailure is not null)
            throw new InvalidOperationException("Pool cleanup failed after successful concurrency assertions.", cleanupFailure);
    }
}
