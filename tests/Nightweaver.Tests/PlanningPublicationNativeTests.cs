using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

[SupportedOSPlatform("windows")]
public sealed class PlanningPublicationNativeTests(PublishedCliArtifactFixture published)
{
    [Fact]
    public Task CompetingProjectsAndHostRootsCannotWriteUntilTheOwnerProcessExits() =>
        DispatchScenarioSupport.PublishedDispatchExcludesCompetingProjectsAcrossHostRootsAndRecoversAfterProcessDeath(published);

    [Fact]
    public async Task NativePublicationRecoversLostCreatePreservesConflictsAndResumesLegacyGraph()
    {
        using var fixture = new Fixture();
        try
        {
            var recovered = await fixture.Approve(withInput: true);
            fixture.StopAfterCreate = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Publish(recovered));
            var partial = fixture.Read();
            var retained = Assert.Single(Fixture.Tasks(partial, recovered.BeadId));
            AssertHeld(partial, recovered.BeadId);
            Assert.False(PlanningPublisher.IsLegacyTaskId(retained.Id, recovered.BeadId));
            var creates = fixture.CreateCount;
            Assert.True((await fixture.Publish(recovered)).Succeeded);
            var complete = fixture.Read();
            Assert.Contains(retained.Id, Fixture.Tasks(complete, recovered.BeadId).Select(t => t.Id));
            Assert.Equal(creates + 1, fixture.CreateCount);
            fixture.AssertPublished(complete, recovered.BeadId);

            var conflict = await fixture.Approve();
            fixture.StopAfterCreate = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Publish(conflict));
            var original = Assert.Single(Fixture.Tasks(fixture.Read(), conflict.BeadId));
            var duplicate = fixture.Seed(original, explicitId: null, link: false);
            var before = fixture.Read();
            var beforeWrites = fixture.WriteCount;
            var failure = await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.Publish(conflict));
            Assert.Contains(original.Id, failure.Message);
            Assert.Contains(duplicate, failure.Message);
            Assert.Contains(conflict.BeadId, failure.Message);
            Assert.Contains("Maintainer:", failure.Message);
            fixture.Record("conflict", failure.Message);
            Assert.Equal(beforeWrites, fixture.WriteCount);
            var after = fixture.Read();
            Assert.Equal(before.ById.Keys.Order(), after.ById.Keys.Order());
            Assert.Equal(before.ById[original.Id].Payload.GetRawText(), after.ById[original.Id].Payload.GetRawText());
            Assert.Equal(before.ById[duplicate].Payload.GetRawText(), after.ById[duplicate].Payload.GetRawText());
            AssertHeld(after, conflict.BeadId);
            Assert.DoesNotContain(fixture.Comments(conflict.BeadId), c => c.Text.Contains(PlanningPublisher.ReleaseSchema, StringComparison.Ordinal));

            var legacy = await fixture.Approve();
            var candidate = PlanningEvidence.LatestCandidate(fixture.Comments(legacy.BeadId))!.Value;
            var task = candidate.Document.Tasks![0];
            var revision = PlanningEvidence.Hash(candidate.Comment.Text);
            var legacyId = PlanningPublisher.TaskId(legacy.BeadId, revision, task.Id);
            fixture.SeedLegacy(legacy.BeadId, legacyId, revision, task, candidate.Document);
            AssertHeld(fixture.Read(), legacy.BeadId);
            fixture.StopAfterReceipt = true;
            creates = fixture.CreateCount;
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Publish(legacy));
            var membership = PlanningPublisher.Metadata(fixture.Read().ById[legacy.BeadId], "publication_graph");
            var receipt = Assert.Single(fixture.Comments(legacy.BeadId), c => c.Text.Contains(PlanningPublisher.ReleaseSchema, StringComparison.Ordinal));
            AssertHeld(fixture.Read(), legacy.BeadId);
            Assert.True((await fixture.Publish(legacy)).Succeeded);
            var legacyComplete = fixture.Read();
            Assert.Equal(creates + 1, fixture.CreateCount);
            Assert.Contains(legacyId, Fixture.Tasks(legacyComplete, legacy.BeadId).Select(t => t.Id));
            Assert.Equal(membership, PlanningPublisher.Metadata(legacyComplete.ById[legacy.BeadId], "publication_graph"));
            Assert.Equal(receipt, Assert.Single(fixture.Comments(legacy.BeadId), c => c.Text.Contains(PlanningPublisher.ReleaseSchema, StringComparison.Ordinal)));
            fixture.AssertPublished(legacyComplete, legacy.BeadId);
            await fixture.ProveInputAfterCleanup(recovered);
        }
        catch { fixture.PreserveForFailure(); throw; }
        finally { fixture.SaveEvidence(); }
    }

    private static void AssertHeld(BeadsInventory inventory, string product)
    {
        Assert.DoesNotContain("planned", inventory.ById[product].Labels);
        foreach (var task in inventory.ById.Values.Where(t => PlanningPublisher.Metadata(t, "publication_product") == product))
        {
            Assert.Contains("plan-unpublished", task.Labels);
            Assert.False(PlanningPublicationGate.Allows(task, inventory));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string Actor = "nightweaver:demo";
        private static readonly JsonSerializerOptions HostJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        private readonly BeadsFixture target = new("native publication tracker");
        private readonly TempDirectory host = new();
        private readonly ConcurrentQueue<object> evidence = new();
        private readonly RecordingProcessRunner runner;
        private string checkout = "";
        private readonly string head;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private bool preserve;
        private static readonly byte[] InputBytes = Encoding.UTF8.GetBytes("Disposable synthetic input\r\n\u00e9\n");
        private PlanningLocalInput? input;
        internal bool StopAfterCreate { get; set; }
        internal bool StopAfterReceipt { get; set; }
        internal int CreateCount { get; private set; }
        internal int WriteCount { get; private set; }

        internal Fixture()
        {
            // One immutable prepared tracker; all three Products use independent provenance.
            target.Run("git", ["commit", "--allow-empty", "-m", "Native publication fixture"]);
            head = target.Run("git", ["rev-parse", "HEAD"]).Trim();
            ProcessLifetimeTargetRepositoryLock.Acquire(new(target.Path, "main") { GitCommonDirectory = Path.Combine(target.Path, ".git") });
            runner = new RecordingProcessRunner(async (request, token) =>
            {
                var environment = new Dictionary<string, string?>(target.Environment);
                foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
                var watch = Stopwatch.StartNew();
                var result = await new SystemProcessRunner().RunAsync(request with { Environment = environment }, token);
                evidence.Enqueue(new { request.FileName, request.Arguments, result.ExitCode, result.StandardOutput, result.StandardError,
                    seconds = watch.Elapsed.TotalSeconds });
                if (request.FileName == "bd" && !request.Arguments.Contains("--readonly")) WriteCount++;
                if (request.FileName == "bd" && request.Arguments.Contains("create"))
                {
                    Assert.DoesNotContain("--id", request.Arguments);
                    Assert.Equal(0, result.ExitCode);
                    CreateCount++;
                    if (StopAfterCreate)
                    {
                        StopAfterCreate = false;
                        // The real command has settled. The publisher never receives its response.
                        throw new OperationCanceledException("Discard successful native create response and stop the publisher");
                    }
                }
                if (StopAfterReceipt && request.Arguments.Contains("comments") && request.Arguments.Contains("add")
                    && request.Arguments.Any(a => a.Contains(PlanningPublisher.ReleaseSchema, StringComparison.Ordinal)))
                {
                    Assert.Equal(0, result.ExitCode);
                    StopAfterReceipt = false;
                    throw new OperationCanceledException("Stop after the native release receipt, before hold removal");
                }
                if (request.Arguments.Contains("--remove-label") && request.Arguments.Contains("plan-unpublished"))
                {
                    Assert.Equal(0, result.ExitCode);
                    var partial = Read();
                    var id = request.Arguments[request.Arguments.ToList().IndexOf("update") + 1];
                    var product = PlanningPublisher.Metadata(partial.ById[id], "publication_product")!;
                    Assert.DoesNotContain("planned", partial.ById[product].Labels);
                    Assert.All(Tasks(partial, product), task => Assert.False(PlanningPublicationGate.Allows(task, partial)));
                    Record("partial-release", new { product, releasedTask = id, admission = false });
                }
                return result;
            });
        }

        internal async Task<AgentStageSession> Approve(bool withInput = false)
        {
            var id = target.Create("Native publication behavior", Actor, "in_progress", "feature",
                labels: ["ready-for-planning", "stage:plan-approved"]);
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            var project = new ProjectConfig(ProjectSlug.Parse("demo"), "config.json", new(target.Path, "main"), new(1, 1, 1), new(3, 2),
                settings, new(AgentRoles.All.ToDictionary(r => r, _ => settings)));
            if (checkout.Length == 0)
            {
                checkout = Path.Combine(new HostPaths(host.Path).WorktreesRoot(project.Name), id);
                target.Run("git", ["worktree", "add", "--detach", checkout, "HEAD"]);
            }
            var parent = Read().ById[id];
            var session = new AgentStageSession(new(host.Path), project,
                new(target.Path, "main") { GitCommonDirectory = Path.Combine(target.Path, ".git") }, id, AgentRole.Planner,
                WorktreePurpose.Planning, new(checkout, head), BeadWorkflowState.Parse(parent), Actor,
                new(id, "feature", "stage:plan-approved", 0), false);
            if (withInput)
            {
                var tmp = Directory.CreateDirectory(Path.Combine(session.HostPaths.TmpRoot(project.Name), id)).FullName;
                var source = Path.Combine(tmp, "synthetic.json");
                File.WriteAllBytes(source, InputBytes);
                using var identity = WindowsIdentity.GetCurrent();
                var acl = new FileInfo(source).GetAccessControl();
                acl.SetAccessRuleProtection(true, false);
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                    acl.RemoveAccessRuleSpecific(rule);
                acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(source).SetAccessControl(acl);
                var hash = Convert.ToHexStringLower(SHA256.HashData(InputBytes));
                input = new("export", source, new("disposable synthetic source", "reported synthetic revision", "do not execute"),
                    hash, PlanningInputHandoff.Binding(session), "sha256/" + hash);
                File.WriteAllText(Path.Combine(tmp, "ordinary.tmp"), "Disposable temporary file");
            }
            var grounding = await new PlanningEvidence(runner).CaptureAsync(session, TestContext.Current.CancellationToken);
            var plan = new PlanningDocument(PlanningEvidence.CandidateSchema, grounding,
                Tasks: [new("first", "First behavior", "First works", "Contract", "Implement first", ["First passes"], "Check first", [],
                    LocalInputIds: withInput ? [input!.Id] : null),
                    new("second", "Second behavior", "Second works", "Contract", "Implement second", ["Second passes"], "Check second", ["first"])],
                Constraints: [new("Preserve retained work", "Product contract")], Coverage: [new("Behavior", ["first", "second"], null, null)],
                LocalInputs: withInput ? [input!] : null);
            AddComment(id, JsonSerializer.Serialize(plan, PlanningEvidence.JsonOptions));
            var candidate = PlanningEvidence.LatestCandidate(Comments(id))!.Value.Comment;
            var review = new PlanningDocument(PlanningEvidence.ReviewSchema, grounding, PlanCommentId: candidate.Id,
                PlanRevision: PlanningEvidence.Hash(candidate.Text), Assessment: "Complete", Findings: [], Questions: []);
            var reviewText = JsonSerializer.Serialize(review, PlanningEvidence.JsonOptions);
            AddComment(id, reviewText);
            var reviewComment = Comments(id).Single(c => c.Text == reviewText);
            var approval = new PlanningApproval(candidate.Id, PlanningEvidence.Hash(candidate.Text), reviewComment.Id,
                PlanningEvidence.Hash(reviewText), grounding);
            AddComment(id, JsonSerializer.Serialize(new { schema = "nightweaver-planning-outcome-v1", outcome = "Approved", planning_approval = approval },
                HostJsonOptions));
            return session;
        }

        // Each invocation reconstructs the publisher and its observation from the same native tracker.
        internal Task<AgentStageResult> Publish(AgentStageSession session) => new PlanningPublisher(runner).PublishAsync(
            session with { PublicationVersion = PlanningPublisher.Version(Read().ById[session.BeadId]) }, TestContext.Current.CancellationToken);

        internal async Task ProveInputAfterCleanup(AgentStageSession planning)
        {
            var watch = Stopwatch.StartNew();
            var beforeCommands = runner.Requests.Count;
            var token = TestContext.Current.CancellationToken;
            var declaration = Assert.IsType<PlanningLocalInput>(input);
            var comments = Comments(planning.BeadId);
            var candidate = PlanningEvidence.LatestCandidate(comments)!.Value.Comment;
            var revision = PlanningEvidence.Hash(candidate.Text);
            var handoffComment = Assert.Single(comments, c => c.Text.Contains(PlanningInputHandoff.Schema, StringComparison.Ordinal));
            var handoff = PlanningInputHandoff.Read(comments, planning.BeadId, revision)!;
            var store = PlanningInputHandoff.Open(planning);
            var manifest = store.Verify(declaration);
            var location = store.Location(declaration);
            var objectDirectory = Path.GetDirectoryName(location)!;
            var access = new[] { objectDirectory, location, Path.Combine(objectDirectory, "manifest.json") }.Select(path => new
            {
                path,
                sddl = (Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl()
                    : (FileSystemSecurity)new FileInfo(path).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.All)
            }).ToArray();
            Assert.Equal(declaration.Source, manifest.Source);
            Assert.Equal(declaration.SourcePath, manifest.SourcePath);
            Assert.Equal(declaration.ExpectedSha256, manifest.Sha256);
            Assert.Equal(PlanningInputHandoff.Binding(planning), manifest.Store);
            Assert.Equal(candidate.Id, handoff.PlanCommentId);
            Assert.Equal(revision, handoff.PlanRevision);
            Assert.Equal(declaration.Source, Assert.Single(handoff.Inputs).Source);
            Assert.Equal("", target.Run("git", ["ls-files", "--others", "--exclude-standard"]).Trim());

            var tmp = Path.Combine(planning.HostPaths.TmpRoot(planning.Project.Name), planning.BeadId);
            var ordinary = Path.Combine(tmp, "ordinary.tmp");
            var log = Path.Combine(planning.HostPaths.LogsRoot(planning.Project.Name), planning.BeadId, "proof.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, "Retained synthetic cleanup evidence");
            Assert.True(File.Exists(declaration.SourcePath) && File.Exists(ordinary));
            Assert.True(Directory.Exists(planning.Worktree.Path));
            var diagnostics = await new CompletedEnvironmentCleanup(new(runner), runner).ReconcileAsync(
                planning.HostPaths, planning.Project.Name, planning.Repository, Actor, token, onlyBeadId: planning.BeadId);
            var obligation = Assert.Single(new CompletedEnvironmentStore(planning.HostPaths, planning.Project.Name, planning.Repository)
                .Read().Obligations);
            var registrations = await new GitWorktreeReader(runner).ReadAsync(planning.Repository, token);
            var cleanup = new
            {
                worktreeAbsent = !Directory.Exists(planning.Worktree.Path),
                registrationAbsent = !registrations.Any(r => RuntimePaths.Comparer.Equals(r.Path, planning.Worktree.Path)),
                tmpAbsent = !Directory.Exists(tmp), sourceAbsent = !File.Exists(declaration.SourcePath),
                ordinaryAbsent = !File.Exists(ordinary), logRetained = File.Exists(log), obligation, diagnostics
            };
            Record("input-cleanup", cleanup);
            Assert.Empty(diagnostics);
            Assert.Equal(CleanupStatus.Resolved, obligation.Status);
            Assert.True(obligation.Worktree.ConfirmedAbsent && obligation.Tmp.ConfirmedAbsent);
            Assert.True(cleanup.worktreeAbsent && cleanup.registrationAbsent && cleanup.tmpAbsent
                && cleanup.sourceAbsent && cleanup.ordinaryAbsent && cleanup.logRetained);

            // Reconstruct Delivery from native task state and a different, newly created checkout.
            var task = Tasks(Read(), planning.BeadId).Single(t => PlanningPublisher.Metadata(t, "publication_entry") == "first");
            var deliveryCheckout = Path.Combine(planning.HostPaths.WorktreesRoot(planning.Project.Name), task.Id);
            target.Run("git", ["worktree", "add", "-b", "nightweaver/" + task.Id, deliveryCheckout, "HEAD"]);
            var delivery = new AgentStageSession(planning.HostPaths, planning.Project, planning.Repository, task.Id,
                AgentRole.Implementer, WorktreePurpose.Delivery, new(deliveryCheckout, head), BeadWorkflowState.Parse(task),
                Actor, new(task.Id, "task", "stage:implement", 0), false);
            var resolved = Assert.Single(await PlanningInputHandoff.ResolveAsync(delivery, new(runner), PlanningInputHandoff.Open, token));
            var prompts = Directory.CreateDirectory(Path.Combine(host.Path, "prompts")).FullName;
            File.WriteAllText(Path.Combine(prompts, "implementer.md"), "Read the verified synthetic input.");
            var prepared = AgentInvocationPreparer.WithPlanningInputs(await new AgentInvocationPreparer().PrepareAsync(
                delivery.HostPaths, delivery.Project, delivery.Role, delivery.DispatchContext, delivery.Worktree.Path, token), [resolved]);
            var supplied = Assert.Single(JsonSerializer.Deserialize<ResolvedPlanningInput[]>(
                prepared.StandardInput[(prepared.StandardInput.LastIndexOf('\n') + 1)..], PlanningEvidence.JsonOptions)!);
            Assert.Equal(deliveryCheckout, prepared.WorkingDirectory);
            Assert.Equal(resolved, supplied);
            Assert.Equal(location, supplied.Path);
            var bytes = File.ReadAllBytes(supplied.Path);
            var deliveryHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Assert.Equal(InputBytes, bytes);
            Assert.Equal(declaration.ExpectedSha256, deliveryHash);
            Assert.Equal(manifest.Sha256, deliveryHash);
            Assert.Equal(bytes.Length, supplied.SizeBytes);
            Record("input-delivery", new
            {
                candidateRevision = Environment.GetEnvironmentVariable("NIGHTWEAVER_TEST_CANDIDATE_REVISION"),
                product = planning.BeadId, task = task.Id, candidateComment = candidate.Id, candidateHash = revision,
                handoffComment = handoffComment.Id, handoffHash = PlanningEvidence.Hash(handoffComment.Text),
                source = declaration.Source, sourcePath = declaration.SourcePath, approvedHash = declaration.ExpectedSha256,
                durableManifest = manifest, access, deliveryCheckout, resolved = supplied, deliveryHash,
                bytesMatch = bytes.SequenceEqual(InputBytes), seconds = watch.Elapsed.TotalSeconds,
                commands = runner.Requests.Count - beforeCommands
            });
        }

        internal BeadsInventory Read()
        {
            var json = target.Run("bd", BeadsCommand.Create(target.Path).Arguments);
            Record("inventory", json);
            return BeadsReader.Parse(json);
        }

        internal static BeadSnapshot[] Tasks(BeadsInventory inventory, string product) => inventory.ById.Values
            .Where(t => PlanningPublisher.Metadata(t, "publication_product") == product).ToArray();

        internal IReadOnlyList<BeadCommentSnapshot> Comments(string product) => new BeadsReader(runner)
            .ReadCommentsAsync(new(target.Path, "main"), product, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        private void AddComment(string id, string text) => target.Run("bd", ["comments", "add", id, text]);

        internal string Seed(BeadSnapshot source, string? explicitId, bool link) => SeedTask(explicitId,
            source.Payload.GetProperty("title").GetString()!, source.Payload.GetProperty("description").GetString()!,
            source.Payload.GetProperty("acceptance_criteria").GetString()!, source.Payload.GetProperty("metadata").GetRawText(),
            link ? PlanningPublisher.Metadata(source, "publication_product") : null);

        internal void SeedLegacy(string product, string id, string revision, PlanningTask task, PlanningDocument plan) =>
            SeedTask(id, task.Title, PlanningPublisher.Description(task, plan), string.Join('\n', task.AcceptanceCriteria),
                JsonSerializer.Serialize(new { publication_product = product, publication_revision = revision, publication_entry = task.Id }), product);

        private string SeedTask(string? id, string title, string description, string acceptance, string metadata, string? product)
        {
            var args = new List<string> { "create", "--type", "task", "--title", title, "--description", description,
                "--acceptance", acceptance, "--labels", "plan-unpublished", "--metadata", metadata, "--json" };
            if (id is not null) args.AddRange(["--id", id]);
            if (product is not null) args.AddRange(["--deps", "discovered-from:" + product]);
            var raw = target.Run("bd", args);
            Record("seed", new { args, raw });
            using var json = JsonDocument.Parse(raw);
            return json.RootElement.GetProperty("id").GetString()!;
        }

        internal void AssertPublished(BeadsInventory inventory, string product)
        {
            var tasks = Tasks(inventory, product);
            Assert.Equal(2, tasks.Length);
            Assert.Equal(2, tasks.Select(t => PlanningPublisher.Metadata(t, "publication_entry")).Distinct().Count());
            Assert.Contains("planned", inventory.ById[product].Labels);
            Assert.Null(inventory.ById[product].Assignee);
            Assert.Equal("open", inventory.ById[product].Status);
            using var graph = JsonDocument.Parse(PlanningPublisher.Metadata(inventory.ById[product], "publication_graph")!);
            Assert.Equal(tasks.Select(t => t.Id).Order(), graph.RootElement.EnumerateArray().Select(t => t.GetProperty("id").GetString()).Order());
            using var receipt = JsonDocument.Parse(Assert.Single(Comments(product), c => c.Text.Contains(PlanningPublisher.ReleaseSchema, StringComparison.Ordinal)).Text);
            foreach (var task in tasks)
            {
                Assert.DoesNotContain("plan-unpublished", task.Labels);
                Assert.True(PlanningPublicationGate.Allows(task, inventory));
                var entry = PlanningPublisher.Metadata(task, "publication_entry")!;
                Assert.Equal(task.Id, receipt.RootElement.GetProperty("tasks").GetProperty(entry).GetString());
                var edges = BeadsReader.ParseDownDependencies(task.Payload.GetProperty("dependencies"), task.Id);
                Assert.Contains(new(task.Id, product, "discovered-from"), edges);
                if (entry == "second") Assert.Contains(new(task.Id, tasks.Single(t => PlanningPublisher.Metadata(t, "publication_entry") == "first").Id, "blocks"), edges);
            }
            var readyJson = target.Run("bd", BeadsCommand.CreateUnassignedReadiness(target.Path).Arguments);
            var ready = BeadsReader.Parse(readyJson);
            Assert.Contains(tasks.Single(t => PlanningPublisher.Metadata(t, "publication_entry") == "first").Id, ready.ById.Keys);
            Assert.DoesNotContain(tasks.Single(t => PlanningPublisher.Metadata(t, "publication_entry") == "second").Id, ready.ById.Keys);
            Record("native-readiness", readyJson);
            Record("published", new { product, tasks = tasks.Select(t => t.Id), graph = graph.RootElement.Clone(), receipt = receipt.RootElement.Clone(), admission = true });
        }

        internal void Record(string kind, object value) => evidence.Enqueue(new { kind, value });

        internal void SaveEvidence()
        {
            var text = JsonSerializer.Serialize(new { seconds = elapsed.Elapsed.TotalSeconds, CreateCount, WriteCount,
                commands = runner.Requests.Count, fixtureCommands = target.ToolRunCounts, evidence }, PlanningEvidence.JsonOptions);
            var path = Environment.GetEnvironmentVariable("NIGHTWEAVER_PUBLICATION_EVIDENCE")
                ?? (preserve ? Path.Combine(host.Path, "publication-native-proof.json") : null);
            if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, text);
            TestContext.Current.TestOutputHelper?.WriteLine($"Native publication proof: {elapsed.Elapsed.TotalSeconds:F3}s; commands={runner.Requests.Count}; creates={CreateCount}; evidence={path ?? "not requested"}");
        }

        internal void PreserveForFailure()
        {
            preserve = true;
            host.PreserveForFailure();
            target.PreserveForFailure();
            TestContext.Current.TestOutputHelper?.WriteLine($"Preserved native tracker: {target.Path}; host: {host.Path}");
        }

        public void Dispose() { host.Dispose(); target.Dispose(); }
    }
}
