using System.Collections.Concurrent;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Tests.ManualDeliveryPilotFixture;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;

namespace Nightweaver.Tests;

public sealed class PoolConcurrencyIntegrationTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task DeliveryCompletesWhileMergerPreparationIsHeldThenBothTasksMerge()
    {
        await using var fixture = await ManualPilot.CreateAsync(seedProfile: ManualPilotSeedProfile.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        var paths = new HostPaths(fixture.NightweaverRoot);
        var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), paths);
        var repository = new ValidatedTargetRepository(fixture.Repository, "main");
        var heldBead = await fixture.CreateMergeGateTaskAsync("Held singleton Merger", TestContext.Current.CancellationToken);
        fixture.MergerDiagnosticsBeadId = heldBead;
        var environment = fixture.CreateEnvironment();
        var system = new SystemProcessRunner();
        var runner = new RecordingProcessRunner((request, token) =>
        {
            var merged = new Dictionary<string, string?>(request.Environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in environment) merged[pair.Key] = pair.Value;
            return system.RunAsync(request with
            {
                FileName = request.FileName == "gh" ? Path.Combine(fixture.FakeCliRoot, "gh.exe") : request.FileName,
                Environment = merged
            }, token);
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var preparing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var roles = new ConcurrentQueue<(string Bead, AgentRole Role)>();
        var mergerStarts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var preparer = new AgentInvocationPreparer();
        var launcher = new AgentStageTargetFixture.FixtureEnvironmentOverlayProcessLauncher(environment);
        async Task<PreparedAgentInvocation> Prepare(HostPaths host, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktree, CancellationToken token)
        {
            roles.Enqueue((context.BeadId, role));
            if (context.BeadId == heldBead && role == AgentRole.Merger)
            {
                // Hold the singleton reservation before launching an Agent. A
                // test-owned wait must not consume another role's invocation clock.
                preparing.TrySetResult(true);
                await release.Task.WaitAsync(token);
            }
            return await preparer.PrepareAsync(host, config, role, context, worktree, token);
        }
        Task<AgentExecutionResult> Execute(HostPaths host, ProjectSlug slug,
            PreparedAgentInvocation invocation, CancellationToken token)
        {
            if (invocation.Purpose == "merger")
            {
                var bead = invocation.EnvironmentOverrides["NIGHTWEAVER_BEAD_ID"];
                mergerStarts.AddOrUpdate(bead, 1, (_, count) => count + 1);
            }
            return AgentInvocationExecutor.ExecuteAsync(host, slug, invocation, launcher, token);
        }
        var stages = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
        var retries = new OperationalRetryCoordinator(runner, stages: stages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        DeliveryRoleRunner deliveryRole = (host, config, target, bead, role, actor, state, token, progress) =>
            retries.RunAsync(host, config, target, bead, role, actor, token, progress, state);
        MergerRoleRunner mergerRole = (host, config, target, bead, role, actor, state, token, progress) =>
            retries.RunAsync(host, config, target, bead, role, actor, token, progress, state);
        var deliverySlot = new DeliverySlotCoordinator(runner, deliveryRole);
        var mergerSlot = new MergerSlotCoordinator(runner, mergerRole);
        var delivery = new DeliveryPoolCoordinator(paths, project, repository, runner,
            runSlot: (host, config, target, bead, token) => deliverySlot.RunAsync(host, config, target, bead, token),
            slotLifetimeToken: lifetime.Token);
        var merger = new MergerPoolCoordinator(paths, project, repository, runner,
            runSlot: mergerSlot.RunAsync, slotLifetimeToken: lifetime.Token);
        Exception? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            var initial = await merger.EvaluateAsync(lifetime.Token);
            var held = Assert.Single(initial.Admitted);
            Assert.Equal(heldBead, held.BeadId);
            await preparing.Task.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.Empty(mergerStarts);
            var heldState = await fixture.ReadBeadAsync(heldBead, lifetime.Token);
            Assert.Equal("in_progress", heldState.GetProperty("status").GetString());
            Assert.Equal(project.RecoveryActor, heldState.GetProperty("assignee").GetString());

            var lateBead = await fixture.CreateTaskAsync("Independent Delivery", lifetime.Token);
            var evaluation = await delivery.EvaluateAsync(lifetime.Token);
            var admitted = Assert.Single(evaluation.Admitted);
            Assert.Equal(lateBead, admitted.BeadId);
            var completed = await admitted.Completion.WaitAsync(TimeSpan.FromMinutes(5), lifetime.Token);
            Assert.Equal(DeliverySlotDisposition.MergeReady, completed.Outcome?.Disposition);
            Assert.True(completed.ClaimHandoffCompleted);
            Assert.Equal(new[] { AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Qa },
                roles.Where(item => item.Bead == lateBead).Select(item => item.Role));
            Assert.Empty(mergerStarts);
            Assert.False(held.Completion.IsCompleted);
            Assert.Equal(heldBead, Assert.Single(merger.ActiveReservations).BeadId);
            var queued = await merger.EvaluateAsync(lifetime.Token);
            Assert.Empty(queued.Admitted);
            Assert.Contains(queued.Diagnostics, diagnostic => diagnostic.BeadId == lateBead
                && diagnostic.Code == MergerPoolDiagnosticCode.CapacityReached);

            release.TrySetResult(true);
            var firstMerge = await held.Completion.WaitAsync(TimeSpan.FromMinutes(3), lifetime.Token);
            Assert.True(firstMerge.MergeCompleted, firstMerge.Diagnostic);
            var next = Assert.Single((await merger.EvaluateAsync(lifetime.Token)).Admitted);
            Assert.Equal(lateBead, next.BeadId);
            var secondMerge = await next.Completion.WaitAsync(TimeSpan.FromMinutes(3), lifetime.Token);
            Assert.True(secondMerge.MergeCompleted, secondMerge.Diagnostic);
            Assert.Equal(1, mergerStarts[heldBead]);
            Assert.Equal(1, mergerStarts[lateBead]);
            foreach (var bead in new[] { heldBead, lateBead })
            {
                Assert.Equal("closed", (await fixture.ReadBeadAsync(bead, lifetime.Token)).GetProperty("status").GetString());
                Assert.Contains(bead, await fixture.ReadRemoteMainFileAsync($"merger-verified-{bead}.txt", lifetime.Token), StringComparison.Ordinal);
            }
        }
        catch (Exception exception)
        {
            primary = exception;
            fixture.PreserveForFailure();
            TestContext.Current.TestOutputHelper?.WriteLine($"Pool concurrency fixture preserved at {fixture.Root}");
            throw;
        }
        finally
        {
            release.TrySetResult(true);
            try
            {
                lifetime.Cancel();
                await Task.WhenAll(delivery.WaitForActiveSlotsAsync(CancellationToken.None),
                    merger.WaitForActiveSlotsAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
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
