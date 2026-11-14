using System.Collections.Concurrent;
using System.Text.Json;
using Nightweaver.Planning;
using Nightweaver.Telemetry;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Tests.ManualDeliveryPilotFixture;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;

namespace Nightweaver.Tests;

public sealed class PoolConcurrencyIntegrationTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task PlanningPublishesWhileDeliveryAndMergerProgressIndependently()
    {
        await using var fixture = await ManualPilot.CreateAsync(seedProfile: ManualPilotSeedProfile.ParentlessOnly,
            cancellationToken: TestContext.Current.CancellationToken);
        var paths = new HostPaths(fixture.NightweaverRoot);
        var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), paths);
        project = project with { Concurrency = project.Concurrency with { MaxActivePlanBeads = 10 } };
        foreach (var prompt in new[] { "planner.md", "plan-challenger.md" })
            File.WriteAllText(Path.Combine(fixture.NightweaverRoot, "prompts", prompt), "Disposable Planning fixture prompt.");
        var repository = new ValidatedTargetRepository(fixture.Repository, "main");
        var heldBead = await fixture.CreateMergeGateTaskAsync("Held singleton Merger", TestContext.Current.CancellationToken);
        fixture.MergerDiagnosticsBeadId = heldBead;
        var environment = fixture.CreateEnvironment();
        var system = new SystemProcessRunner();
        var processFailures = new ConcurrentQueue<string>();
        var roleOutcomes = new ConcurrentQueue<string>();
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
        var productCreate = await runner.RunAsync(BeadsCommand.CreateWrite(repository.Path,
            ["create", "--title", "Requested behavior", "--type", "feature", "--labels", "ready-for-planning", "--json"]),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, productCreate.ExitCode);
        using var productJson = JsonDocument.Parse(productCreate.StandardOutput);
        var product = productJson.RootElement.GetProperty("id").GetString()!;
        var planningPreparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryStop = new ContinuousStopGate();
        var planningStop = new ContinuousStopGate();
        await using var telemetry = new FactoryTelemetryPublisher(paths, project, repository,
            project.Concurrency.MaxConcurrentDelivery, 1);
        await telemetry.StartAsync(TestContext.Current.CancellationToken);
        var telemetryReader = new FactoryTelemetryReader(paths, project);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var preparing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var successorStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var roles = new ConcurrentQueue<(string Bead, AgentRole Role)>();
        var unrelatedImplementation = new TaskCompletionSource<OperationalRetryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationalRetryOutcome? generatedOutcome = null;
        var mergerStarts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var preparer = new AgentInvocationPreparer();
        var launcher = new AgentStageTargetFixture.FixtureEnvironmentOverlayProcessLauncher(environment);
        async Task<PreparedAgentInvocation> Prepare(HostPaths host, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktree, CancellationToken token)
        {
            roles.Enqueue((context.BeadId, role));
            if (context.BeadId == product && role == AgentRole.Planner)
            {
                planningPreparing.TrySetResult();
                await releasePlanning.Task.WaitAsync(token);
            }
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
            if (invocation.Purpose is "planner" or "plan-challenger"
                || invocation.EnvironmentOverrides["NIGHTWEAVER_BEAD_ID"].StartsWith(product + "-p", StringComparison.Ordinal))
            {
                var overlay = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase)
                {
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = invocation.Purpose switch
                    {
                        "planner" => "planning-draft", "plan-challenger" => "planning-approve", _ => "slot-implement-unlabeled"
                    },
                    ["NIGHTWEAVER_FIXTURE_PID_FILE"] = Path.Combine(fixture.Root, invocation.InvocationId + ".pid")
                };
                return AgentInvocationExecutor.ExecuteAsync(host, slug, invocation,
                    new AgentStageTargetFixture.FixtureEnvironmentOverlayProcessLauncher(overlay), token);
            }
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
                unrelatedImplementation.TrySetResult(outcome);
            if (bead.StartsWith(product + "-p", StringComparison.Ordinal))
            {
                generatedOutcome = outcome;
                deliveryStop.RequestStop();
            }
            if (bead == product && role == AgentRole.PlanChallenger) planningStop.RequestStop();
            return outcome;
        };
        MergerRoleRunner mergerRole = (host, config, target, bead, role, actor, state, token, progress) =>
            retries.RunAsync(host, config, target, bead, role, actor, token, progress, state);
        var planningSlot = new PlanningSlotCoordinator(runner, deliveryRole);
        var planning = new PlanningPoolCoordinator(paths, project, repository, runner,
            runSlot: (host, config, target, bead, token) => planningSlot.RunAsync(host, config, target, bead, token),
            telemetry: telemetry, stopGate: planningStop, slotLifetimeToken: lifetime.Token);
        var deliverySlot = new DeliverySlotCoordinator(runner, deliveryRole);
        var mergerSlot = new MergerSlotCoordinator(runner, mergerRole);
        var delivery = new DeliveryPoolCoordinator(paths, project, repository, runner,
            runSlot: (host, config, target, bead, token) => deliverySlot.RunAsync(host, config, target, bead, token),
            stopGate: deliveryStop, telemetry: telemetry, slotLifetimeToken: lifetime.Token);
        var merger = new MergerPoolCoordinator(paths, project, repository, runner,
            runSlot: async (host, config, target, bead, attemptStarted, token) =>
            {
                if (bead != heldBead)
                {
                    // Prove that the queued successor gets the singleton reservation.
                    // Native merge completion is already exercised by the first slot
                    // here and by MergerPoolIntegrationTests; do not repeat that full
                    // pipeline merely to observe the pool's next admission.
                    successorStarted.TrySetResult(bead);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return await mergerSlot.RunAsync(host, config, target, bead, attemptStarted, token);
            }, slotLifetimeToken: lifetime.Token);
        Exception? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            var admittedPlanning = Assert.Single((await planning.EvaluateAsync(lifetime.Token)).Admitted);
            Assert.Equal(product, admittedPlanning.BeadId);
            await planningPreparing.Task.WaitAsync(TimeSpan.FromMinutes(1), lifetime.Token);
            Assert.Empty((await planning.EvaluateAsync(lifetime.Token)).Admitted);
            var initial = await merger.EvaluateAsync(lifetime.Token);
            var held = Assert.Single(initial.Admitted);
            Assert.Equal(heldBead, held.BeadId);
            await preparing.Task.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.Empty(mergerStarts);
            var heldState = await fixture.ReadBeadAsync(heldBead, lifetime.Token);
            Assert.Equal("in_progress", heldState.GetProperty("status").GetString());
            Assert.Equal(project.RecoveryActor, heldState.GetProperty("assignee").GetString());

            // Reuse the native recovery smoke's prepared template. Admission still
            // happens only after Merger preparation is held; release this task's
            // fixture gate so the real Delivery roles can make independent progress.
            var lateBead = fixture.ParentlessTaskId;
            fixture.ReleaseGate(lateBead);
            var evaluation = await delivery.EvaluateAsync(lifetime.Token);
            var admitted = Assert.Single(evaluation.Admitted);
            Assert.Equal(lateBead, admitted.BeadId);
            var implementation = await unrelatedImplementation.Task.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.True(implementation.Succeeded, implementation.StageResult.Failure?.Reason);
            Assert.False(admittedPlanning.Completion.IsCompleted);
            Assert.False(held.Completion.IsCompleted);
            // One actual native turn proves held Planning preparation does not
            // block Delivery. Let Planning overlap the remaining Delivery
            // turns rather than serializing two independent pool journeys.
            releasePlanning.TrySetResult();
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

            var interrupted = await admittedPlanning.Completion.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.True(interrupted.Outcome?.Disposition == PlanningSlotDisposition.Interrupted,
                $"Expected Interrupted; outcome: {JsonSerializer.Serialize(interrupted.Outcome)}; exception: {interrupted.Exception}");
            Assert.True(interrupted.ClaimHandoffCompleted, interrupted.Diagnostic);
            var sentinel = Path.Combine(paths.TmpRoot(project.Name), product, "retained-sentinel");
            File.WriteAllText(sentinel, "preserve interruption and pause");
            var pause = await runner.RunAsync(BeadsCommand.CreateWrite(repository.Path,
                ["update", product, "--add-label", "needs-human"]), lifetime.Token);
            Assert.Equal(0, pause.ExitCode);
            var restarted = new PlanningPoolCoordinator(paths, project, repository, runner,
                runSlot: (host, config, target, bead, token) => planningSlot.RunAsync(host, config, target, bead, token),
                slotLifetimeToken: lifetime.Token);
            Assert.Empty((await restarted.EvaluateAsync(lifetime.Token)).Admitted);
            await new BeadsWriter(runner).RetryNeedsHumanAsync(repository, product, lifetime.Token);
            var recovered = Assert.Single((await restarted.EvaluateAsync(lifetime.Token)).Admitted);
            var planned = await recovered.Completion.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.Equal("preserve interruption and pause", File.ReadAllText(sentinel));
            Assert.Equal(new[] { AgentRole.Planner, AgentRole.PlanChallenger }, roles.Where(item => item.Bead == product).Select(item => item.Role));
            Assert.Equal(PlanningSlotDisposition.Published, planned.Outcome?.Disposition);
            Assert.True(planned.Outcome?.FinalState?.Planned);
            Assert.True(planned.ClaimHandoffCompleted, planned.Diagnostic);
            Assert.Empty(planning.ActiveReservations);
            var inventory = await new BeadsReader(runner).ReadSnapshotAsync(repository, lifetime.Token);
            var generated = inventory.ById.Values.Where(bead => PlanningPublisher.Metadata(bead, "publication_product") == product).ToArray();
            Assert.Equal(2, generated.Length);
            var task = Assert.Single(generated, bead => PlanningPublisher.Metadata(bead, "publication_entry") == "slice-1");
            var dependent = Assert.Single(generated, bead => PlanningPublisher.Metadata(bead, "publication_entry") == "slice-2");
            Assert.DoesNotContain("plan-unpublished", task.Labels);
            Assert.All(generated, bead => Assert.True(PlanningPublicationGate.Allows(bead, inventory)));
            var readiness = await runner.RunAsync(BeadsCommand.CreateUnassignedReadiness(repository.Path), lifetime.Token);
            Assert.Equal(0, readiness.ExitCode);
            var ready = BeadsReader.Parse(readiness.StandardOutput).ById.Keys;
            Assert.Contains(task.Id, ready);
            Assert.DoesNotContain(dependent.Id, ready);
            var generatedAdmission = Assert.Single((await delivery.EvaluateAsync(lifetime.Token)).Admitted);
            Assert.Equal(task.Id, generatedAdmission.BeadId);
            // Publication is complete: main may now advance without invalidating
            // Planning evidence. Run Merger alongside the generated Implementer.
            release.TrySetResult(true);
            var generatedTurn = await generatedAdmission.Completion.WaitAsync(TimeSpan.FromMinutes(2), lifetime.Token);
            Assert.True(generatedTurn.ClaimHandoffCompleted, generatedTurn.Diagnostic);
            Assert.NotNull(generatedOutcome);
            Assert.True(generatedOutcome.Succeeded, generatedOutcome.StageResult.Failure?.Reason);
            Assert.Contains((generatedAdmission.BeadId, AgentRole.Implementer), roles);
            Assert.Single(roles, item => item.Bead == task.Id);
            Assert.Equal("stage:review", (await new BeadsReader(runner).ReadSnapshotAsync(repository, lifetime.Token))
                .ById[generatedAdmission.BeadId].Labels.Single(label => label.StartsWith("stage:", StringComparison.Ordinal)));
            var generatedWorktree = Path.Combine(paths.WorktreesRoot(project.Name), task.Id);
            var branch = await runner.RunAsync(GitCommand.Create(["branch", "--show-current"], generatedWorktree), lifetime.Token);
            Assert.Equal(0, branch.ExitCode);
            Assert.Equal($"nightweaver/{task.Id}", branch.StandardOutput.Trim());
            Assert.True(File.Exists(Path.Combine(generatedWorktree, "delivery-slot.txt")));
            var observed = telemetryReader.Read().Snapshot;
            Assert.NotNull(observed?.Planning);
            Assert.Contains(observed.Planning.Slots, slot => slot.Invocation?.ProcessId is > 0
                && slot.Invocation.Purpose == "plan-challenger");

            var firstMerge = await held.Completion.WaitAsync(TimeSpan.FromMinutes(3), lifetime.Token);
            Assert.True(firstMerge.MergeCompleted, firstMerge.Diagnostic);
            var next = Assert.Single((await merger.EvaluateAsync(lifetime.Token)).Admitted);
            Assert.Equal(lateBead, next.BeadId);
            Assert.Equal(lateBead, await successorStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token));
            Assert.Equal(lateBead, Assert.Single(merger.ActiveReservations).BeadId);
            Assert.False(next.Completion.IsCompleted);
            Assert.Equal(1, mergerStarts[heldBead]);
            Assert.False(mergerStarts.ContainsKey(lateBead));
            Assert.Equal("closed", (await fixture.ReadBeadAsync(heldBead, lifetime.Token)).GetProperty("status").GetString());
            Assert.Contains(heldBead, await fixture.ReadRemoteMainFileAsync($"merger-verified-{heldBead}.txt", lifetime.Token), StringComparison.Ordinal);
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
            release.TrySetResult(true);
            releasePlanning.TrySetResult();
            try
            {
                lifetime.Cancel();
                await Task.WhenAll(delivery.WaitForActiveSlotsAsync(CancellationToken.None),
                    merger.WaitForActiveSlotsAsync(CancellationToken.None), planning.WaitForActiveSlotsAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
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
