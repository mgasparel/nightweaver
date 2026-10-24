using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

[CollectionDefinition("Agent stage native integration", DisableParallelization = true)]
public sealed class AgentStageNativeIntegration { }

[Collection("Agent stage native integration")]
public sealed class AgentStageIntegrationTests
{
    [Fact]
    public async Task RealGitBdAndWindowsJobsRunPlanningDeliveryAndAllOperationalClassifications()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        using var foreign = new BeadsFixture("foreign tracker");
        var foreignBefore = Snapshot.FileTree(foreign.Path);
        target.SeedRootSentinels();
        var rootBefore = target.CaptureRootState();

        var planner = target.CreateBead("Planning transition", "feature", "stage:plan");
        var planning = await target.RunAgentAsync(planner, AgentRole.Planner, "plan-challenge", AgentCli.Claude);
        AssertSuccess(planning, "plan-challenge");
        Assert.Equal("stage:plan-challenge", target.ReadState(planner).Stage);

        var challenger = target.CreateBead("Plan challenge transition", "feature", "stage:plan-challenge");
        var challenged = await target.RunAgentAsync(challenger, AgentRole.PlanChallenger, "challenge", AgentCli.Codex);
        AssertSuccess(challenged, "challenge");
        Assert.Equal("stage:plan-approved", target.ReadState(challenger).Stage);

        var approved = target.CreateBead("Materialized plan", "bug", "stage:plan-approved");
        var materialized = await target.RunAgentAsync(approved, AgentRole.Planner, "planned-materialization", AgentCli.Claude);
        AssertSuccess(materialized, "planned-materialization");
        Assert.True(target.ReadState(approved).Planned);
        var links = await new BeadsReader(new SystemProcessRunner()).ReadUpDependenciesAsync(
            new(target.Path, "main"), approved, TestContext.Current.CancellationToken);
        Assert.Contains(links, link => link.DependsOnId == approved && link.Type == "discovered-from"
            && target.ReadBead(link.IssueId) is { IssueType: "task", Status: "open" });

        var delivery = target.CreateBead("Delivery transition", "task", "stage:implement");
        var delivered = await target.RunAgentAsync(delivery, AgentRole.Implementer, "review-commit", AgentCli.Codex);
        AssertSuccess(delivered, "review-commit");
        Assert.Equal("stage:review", target.ReadState(delivery).Stage);

        var reviewId = target.CreateBead("Reviewer transition", "task", "stage:review");
        var reviewed = await target.RunAgentAsync(reviewId, AgentRole.Reviewer, "review-qa", AgentCli.Claude);
        AssertSuccess(reviewed, "review-qa");
        Assert.Equal("stage:qa", target.ReadState(reviewId).Stage);

        var qaId = target.CreateBead("QA transition", "task", "stage:qa");
        var qa = await target.RunAgentAsync(qaId, AgentRole.Qa, "qa-merge", AgentCli.Codex);
        AssertSuccess(qa, "qa-merge");
        Assert.Equal("stage:merge", target.ReadState(qaId).Stage);

        var merger = target.CreateBead("Merger closure", "task", "stage:merge");
        var merged = await target.RunAgentAsync(merger, AgentRole.Merger, "close-commit", AgentCli.Codex);
        AssertSuccess(merged, "close-commit");
        Assert.Equal("closed", target.ReadState(merger).Status);

        var rejectionId = target.CreateBead("Correct repair increment", "task", "stage:review");
        var rejection = await target.RunAgentAsync(rejectionId, AgentRole.Reviewer, "reject-with-increment", AgentCli.Claude);
        AssertSuccess(rejection, "reject-with-increment");
        Assert.Equal(1, target.ReadState(rejectionId).RepairCycles);

        var nonzeroId = target.CreateBead("Nonzero exit", "task", "stage:implement");
        var nonzero = await target.RunAgentAsync(nonzeroId, AgentRole.Implementer, "review-nonzero", AgentCli.Codex);
        AssertFailure(nonzero, AgentOperationalFailureKind.NonZeroExit);
        Assert.Contains("19", nonzero.Failure!.Reason, StringComparison.Ordinal);

        var timeoutId = target.CreateBead("Timeout", "task", "stage:implement");
        var timedOut = await target.RunAgentAsync(timeoutId, AgentRole.Implementer, "timeout", AgentCli.Codex);
        AssertFailure(timedOut, AgentOperationalFailureKind.TimedOut);
        Assert.Contains(timedOut.Diagnostics, diagnostic => diagnostic.StartsWith("IllegalBeadTransition:", StringComparison.Ordinal));

        var dirtyId = target.CreateBead("Dirty worktree", "task", "stage:implement");
        var dirty = await target.RunAgentAsync(dirtyId, AgentRole.Implementer, "review-dirty", AgentCli.Codex);
        AssertFailure(dirty, AgentOperationalFailureKind.DirtyWorktree);
        Assert.Contains("fixture-dirty.txt", dirty.Failure!.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(System.IO.Path.Combine(target.WorktreePath(dirtyId), "fixture-dirty.txt")),
            "Post-exit failure must retain the dirty worktree for diagnosis.");

        var headId = target.CreateBead("Forbidden read-only commit", "feature", "stage:plan");
        var headChanged = await target.RunAgentAsync(headId, AgentRole.Planner, "plan-commit", AgentCli.Claude);
        AssertFailure(headChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var challengerHeadId = target.CreateBead("Forbidden challenger commit", "feature", "stage:plan-challenge");
        var challengerHeadChanged = await target.RunAgentAsync(challengerHeadId, AgentRole.PlanChallenger, "challenge-commit", AgentCli.Codex);
        AssertFailure(challengerHeadChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var reviewerHeadId = target.CreateBead("Forbidden reviewer commit", "task", "stage:review");
        var reviewerHeadChanged = await target.RunAgentAsync(reviewerHeadId, AgentRole.Reviewer, "review-qa-commit", AgentCli.Claude);
        AssertFailure(reviewerHeadChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var qaHeadId = target.CreateBead("Forbidden QA commit", "task", "stage:qa");
        var qaHeadChanged = await target.RunAgentAsync(qaHeadId, AgentRole.Qa, "qa-merge-commit", AgentCli.Codex);
        AssertFailure(qaHeadChanged, AgentOperationalFailureKind.ForbiddenHeadMutation);

        var noOpId = target.CreateBead("Illegal no-op", "task", "stage:implement");
        var noOp = await target.RunAgentAsync(noOpId, AgentRole.Implementer, "noop", AgentCli.Codex);
        AssertFailure(noOp, AgentOperationalFailureKind.IllegalBeadTransition);

        var missingCounterId = target.CreateBead("Missing repair increment", "task", "stage:review");
        var missingCounter = await target.RunAgentAsync(missingCounterId, AgentRole.Reviewer, "reject-without-increment", AgentCli.Claude);
        AssertFailure(missingCounter, AgentOperationalFailureKind.IllegalBeadTransition);
        Assert.Contains("repair_cycles", missingCounter.Failure!.Reason, StringComparison.Ordinal);

        Assert.Equal(rootBefore, target.CaptureRootState());
        Assert.Equal(foreignBefore, Snapshot.FileTree(foreign.Path));
    }

    private static void AssertSuccess(AgentStageResult result, string action)
    {
        Assert.True(result.Succeeded, result.Failure?.Reason);
        Assert.True(File.Exists(result.LogFilePath), result.LogFilePath);
        Assert.Contains($"fixture action completed: {action}", File.ReadAllText(result.LogFilePath), StringComparison.Ordinal);
    }

    private static void AssertFailure(AgentStageResult result, AgentOperationalFailureKind expected)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure!.Kind);
        Assert.True(File.Exists(result.LogFilePath), result.LogFilePath);
    }

    private sealed class AgentStageTargetFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private long nextRun;

        internal AgentStageTargetFixture()
        {
            TargetPath = temp.CreateDirectory("target repository with spaces");
            OriginPath = System.IO.Path.Combine(temp.Path, "origin.git");
            Directory.CreateDirectory(OriginPath);
            Run("git", ["init", "--bare", OriginPath], temp.Path);
            Run("git", ["init", "-b", "main"], TargetPath);
            Run("git", ["config", "user.name", "Nightweaver Fixture"], TargetPath);
            Run("git", ["config", "user.email", "fixture@example.invalid"], TargetPath);
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "README.md"), "fixture base\n", new UTF8Encoding(false));
            Run("git", ["add", "README.md"], TargetPath);
            Run("git", ["commit", "-m", "fixture base"], TargetPath);
            Run("git", ["remote", "add", "origin", OriginPath], TargetPath);
            Run("git", ["push", "--set-upstream", "origin", "main"], TargetPath);

            Host = new HostPaths(temp.CreateDirectory("nightweaver host"));
            Directory.CreateDirectory(System.IO.Path.Combine(Host.Root, "prompts"));
            foreach (var file in new[] { "planner.md", "plan-challenger.md", "implementer.md", "reviewer.md", "qa.md", "merger.md" })
                File.WriteAllText(System.IO.Path.Combine(Host.Root, "prompts", file), $"fixture prompt for {file}\n", new UTF8Encoding(false));

            FakeCliRoot = temp.CreateDirectory("fake native agent cli");
            CopyFixtureCli("AgentInvocationFixture.exe");
            CopyFixtureCli("AgentInvocationFixture.dll");
            CopyFixtureCli("AgentInvocationFixture.deps.json");
            CopyFixtureCli("AgentInvocationFixture.runtimeconfig.json");

            UserHome = temp.CreateDirectory("isolated bd user");
            BeadsEnvironment = new Dictionary<string, string?>(BeadsCommand.Create(TargetPath).Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["HOME"] = UserHome,
                ["USERPROFILE"] = UserHome,
                ["APPDATA"] = UserHome,
                ["XDG_CONFIG_HOME"] = UserHome,
                ["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(UserHome, "gitconfig"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_AUTHOR_NAME"] = "Nightweaver Fixture",
                ["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid",
                ["GIT_COMMITTER_NAME"] = "Nightweaver Fixture",
                ["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid"
            };
            Run("bd", ["init", "--prefix", "stage", "--non-interactive", "--skip-agents", "--skip-hooks"], TargetPath, BeadsEnvironment);
            _ = CreateBead("Control", "task");
        }

        private string TargetPath { get; }
        private string OriginPath { get; }
        private HostPaths Host { get; }
        private string FakeCliRoot { get; }
        private string UserHome { get; }
        private IReadOnlyDictionary<string, string?> BeadsEnvironment { get; }

        internal string Path => TargetPath;
        internal string WorktreePath(string beadId) => System.IO.Path.Combine(Host.WorktreesRoot(ProjectSlug.Parse("demo")), beadId);

        internal void SeedRootSentinels()
        {
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "README.md"), "root checkout edit to preserve\n", new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(TargetPath, "root-untracked-sentinel.txt"), "preserve me\n", new UTF8Encoding(false));
        }

        internal string CreateBead(string title, string type, params string[] labels)
        {
            var created = Run("bd", ["create", "--title", title, "--type", type, "--json"], TargetPath, BeadsEnvironment);
            using var document = JsonDocument.Parse(created);
            var id = document.RootElement.GetProperty("id").GetString()!;
            if (labels.Length > 0)
            {
                var update = new List<string> { "update", id };
                foreach (var label in labels) { update.Add("--add-label"); update.Add(label); }
                Run("bd", update, TargetPath, BeadsEnvironment);
            }
            return id;
        }

        internal BeadWorkflowState ReadState(string beadId) => BeadWorkflowState.Parse(ReadBead(beadId));

        internal BeadSnapshot ReadBead(string beadId)
        {
            var inventory = BeadsReader.Parse(Run("bd", BeadsCommand.Create(TargetPath).Arguments, TargetPath, BeadsEnvironment));
            return inventory.ById[beadId];
        }

        internal async Task<AgentStageResult> RunAgentAsync(string beadId, AgentRole role, string action, AgentCli cli)
        {
            var runId = Interlocked.Increment(ref nextRun).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var pidFile = System.IO.Path.Combine(temp.Path, "pids", runId + ".txt");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pidFile)!);
            var project = CreateProject(cli);
            var runner = new SystemProcessRunner();
            var preparer = new AgentInvocationPreparer();
            async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig projectConfig, AgentRole invocationRole,
                AgentDispatchContext context, string worktreePath, CancellationToken token)
            {
                var invocation = await preparer.PrepareAsync(hostPaths, projectConfig, invocationRole, context, worktreePath, token);
                var executable = AgentCliNames.ToConfigValue(cli);
                Assert.Equal(executable, invocation.Executable);
                var canonical = System.IO.Path.GetFullPath(System.IO.Path.Combine(hostPaths.WorktreesRoot(projectConfig.Name), context.BeadId));
                Assert.Equal(canonical, worktreePath);
                Assert.Equal(canonical, invocation.WorkingDirectory);
                var branch = Run("git", ["branch", "--show-current"], worktreePath).Trim();
                if (invocationRole is AgentRole.Planner or AgentRole.PlanChallenger)
                    Assert.Equal("", branch);
                else
                    Assert.Equal($"nightweaver/{context.BeadId}", branch);
                return invocation;
            }

            Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
                PreparedAgentInvocation invocation, CancellationToken token)
            {
                var timeout = action == "timeout" ? TimeSpan.FromMilliseconds(350) : invocation.Timeout;
                var actual = new PreparedAgentInvocation(invocation.Executable, invocation.Arguments, invocation.WorkingDirectory,
                    invocation.StandardInput, invocation.EnvironmentOverrides, timeout, invocation.InvocationId,
                    invocation.TemporaryDirectory, invocation.LogFilePath);
                return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, actual, token);
            }

            var coordinator = new AgentStageCoordinator(runner, prepare: Prepare, execute: Execute);
            var oldAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION");
            var oldPidFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE");
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            AgentStageResult result;
            try
            {
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", action);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", pidFile);
                Environment.SetEnvironmentVariable("PATH", FakeCliRoot + System.IO.Path.PathSeparator + (oldPath ?? ""));
                result = await coordinator.RunAsync(Host, project, new(TargetPath, "main"), beadId, role,
                    TestContext.Current.CancellationToken);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION", oldAction);
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE", oldPidFile);
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
            AssertFixtureExited(pidFile);
            return result;
        }

        internal string CaptureRootState()
        {
            var indexPath = System.IO.Path.Combine(TargetPath, ".git", "index");
            using var index = File.OpenRead(indexPath);
            return string.Join("\n",
                Run("git", ["rev-parse", "HEAD"], TargetPath).Trim(),
                Run("git", ["symbolic-ref", "--short", "HEAD"], TargetPath).Trim(),
                Convert.ToHexString(SHA256.HashData(index)),
                Run("git", ["status", "--porcelain"], TargetPath),
                File.ReadAllText(System.IO.Path.Combine(TargetPath, "README.md")),
                File.ReadAllText(System.IO.Path.Combine(TargetPath, "root-untracked-sentinel.txt")));
        }

        private ProjectConfig CreateProject(AgentCli cli)
        {
            var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(cli, null, "low", 1));
            return new(ProjectSlug.Parse("demo"), "fixture.json", new(TargetPath, "main"), new(1, 1, 1), new(3, 2),
                new(cli, null, "low", 1), new RoleSettings(roles));
        }

        private void CopyFixtureCli(string name)
        {
            var source = System.IO.Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(source)) throw new FileNotFoundException($"Native Agent fixture component '{name}' was not built.", source);
            File.Copy(source, System.IO.Path.Combine(FakeCliRoot, name), overwrite: true);
            if (name == "AgentInvocationFixture.exe")
            {
                File.Copy(source, System.IO.Path.Combine(FakeCliRoot, "claude.exe"), overwrite: true);
                File.Copy(source, System.IO.Path.Combine(FakeCliRoot, "codex.exe"), overwrite: true);
            }
        }

        private static void AssertFixtureExited(string pidFile)
        {
            Assert.True(File.Exists(pidFile), $"The native fixture did not create its process handshake: {pidFile}");
            var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(process.WaitForExit(5000), $"Fixture process {pid} survived executor completion.");
            }
            catch (ArgumentException)
            {
                // The process exited before this check.
            }
        }

        private static string Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            if (environment is not null)
            {
                foreach (var pair in environment)
                {
                    if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
                    else startInfo.Environment[pair.Key] = pair.Value;
                }
            }
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(workingDirectory, "missing-global-config");
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdoutTask, stderrTask);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}: {stderrTask.Result}{stdoutTask.Result}");
            return stdoutTask.Result;
        }

        public void Dispose() => temp.Dispose();
    }
}
