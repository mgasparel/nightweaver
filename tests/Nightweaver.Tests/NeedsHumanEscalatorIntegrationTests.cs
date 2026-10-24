using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class NeedsHumanEscalatorIntegrationTests
{
    [Theory]
    [InlineData("feature", "Planner", "stage:plan", "false", "false")]
    [InlineData("task", "Implementer", "stage:implement", "true", "false")]
    [InlineData("feature", "Planner", "stage:plan", "false", "true")]
    public async Task RealBdAndGitEscalationPreservesEvidenceAndOnlyRemovesOwnedResources(
        string issueType, string roleName, string stage, string deliveryValue, string alreadyPausedValue)
    {
        using var fixture = new ProvisioningGitFixture();
        using var foreignTracker = new BeadsFixture("foreign tracker with spaces");
        var foreignBead = foreignTracker.Create("Foreign tracker sentinel");
        var foreignTrackerBefore = Snapshot.FileTree(foreignTracker.Path);
        var foreignInventoryBefore = foreignTracker.Run("bd", BeadsCommand.Create(foreignTracker.Path).Arguments);
        var project = fixture.Project;
        var role = Enum.Parse<AgentRole>(roleName);
        var isDelivery = bool.Parse(deliveryValue);
        var alreadyPaused = bool.Parse(alreadyPausedValue);
        var actor = "nightweaver:demo";
        var userHome = Directory.CreateDirectory(Path.Combine(fixture.TempRoot, "isolated bd home")).FullName;
        var environment = new Dictionary<string, string?>(BeadsCommand.Create(fixture.Target).Environment)
        {
            ["HOME"] = userHome,
            ["USERPROFILE"] = userHome,
            ["APPDATA"] = userHome,
            ["XDG_CONFIG_HOME"] = userHome,
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(userHome, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "Nightweaver Integration",
            ["GIT_AUTHOR_EMAIL"] = "nightweaver@example.invalid",
            ["GIT_COMMITTER_NAME"] = "Nightweaver Integration",
            ["GIT_COMMITTER_EMAIL"] = "nightweaver@example.invalid",
            ["BD_ACTOR"] = actor,
            ["BD_DOLT_AUTO_COMMIT"] = "on",
            ["BD_DISABLE_EVENT_FLUSH"] = null
        };
        var runner = new EnvironmentProcessRunner(environment);
        var init = await runner.RunAsync(new ProcessRequest("bd",
            ["init", "--prefix", "fixture", "--non-interactive", "--skip-agents", "--skip-hooks"],
            fixture.Target, environment, TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal(0, init.ExitCode);
        using var control = await RunBdJsonAsync(runner, fixture.Target, ["create", "--title", "Tracker control", "--type", "task", "--json"], environment);
        Assert.False(string.IsNullOrWhiteSpace(control.RootElement.GetProperty("id").GetString()));

        using var beadJson = await RunBdJsonAsync(runner, fixture.Target,
            ["create", "--title", "Pause cleanup integration", "--type", issueType, "--json"], environment);
        var beadId = beadJson.RootElement.GetProperty("id").GetString()!;
        await RunBdAsync(runner, fixture.Target, ["update", beadId, "--claim"], environment);
        await RunBdAsync(runner, fixture.Target,
            ["update", beadId, "--add-label", stage, "--add-label", "product-decision-required", "--add-label", "retain-label",
                "--set-metadata", "repair_cycles=2", "--set-metadata", "retain=metadata"], environment);
        if (alreadyPaused)
            await RunBdAsync(runner, fixture.Target,
                ["update", beadId, "--status", "open", "--assignee", "", "--add-label", "needs-human"], environment);

        var localBranch = $"nightweaver/{beadId}";
        string? remoteBranchSha = null;
        if (isDelivery)
        {
            remoteBranchSha = fixture.CreateAndPushRemoteBeadBranch(beadId);
        }
        else
        {
            fixture.Git(fixture.Target, ["branch", localBranch]);
        }

        var provisioned = await fixture.Provisioner().ProvisionAsync(fixture.Paths, project, fixture.Repository,
            beadId, isDelivery ? WorktreePurpose.Delivery : WorktreePurpose.Planning, CancellationToken.None);
        File.AppendAllText(Path.Combine(provisioned.Path, "README.md"), "dirty worktree data\n");
        File.WriteAllText(Path.Combine(provisioned.Path, "untracked-pause-sentinel.txt"), "remove me");
        Directory.CreateDirectory(Path.Combine(provisioned.Path, "ignored-cache"));
        File.WriteAllText(Path.Combine(provisioned.Path, "ignored-cache", "pause-cache.bin"), "remove ignored cache too");

        var worktreeSibling = Path.Combine(fixture.Paths.WorktreesRoot(project), "sibling-environment");
        var tmpSibling = Path.Combine(fixture.Paths.TmpRoot(project), "sibling-environment");
        Directory.CreateDirectory(worktreeSibling);
        Directory.CreateDirectory(tmpSibling);
        File.WriteAllText(Path.Combine(worktreeSibling, "keep.txt"), "sibling worktree sentinel");
        File.WriteAllText(Path.Combine(tmpSibling, "keep.txt"), "sibling tmp sentinel");

        var temporaryPath = Path.Combine(fixture.Paths.TmpRoot(project), beadId, "20261024T142233123456Z-" + (isDelivery ? "implementer" : "planner"));
        Directory.CreateDirectory(temporaryPath);
        File.WriteAllText(Path.Combine(temporaryPath, "temp-data.txt"), "remove tmp data");
        var logDirectory = Path.Combine(fixture.Paths.LogsRoot(project), beadId);
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "20261024T142233123456Z-" + (isDelivery ? "implementer" : "planner") + ".log");
        File.WriteAllText(logPath, "settled invocation log contents");
        var logSnapshot = Snapshot.FileTree(logDirectory);
        var siblingWorktreeSnapshot = Snapshot.FileTree(worktreeSibling);
        var siblingTmpSnapshot = Snapshot.FileTree(tmpSibling);
        var rootBefore = CaptureTargetCheckout(fixture);
        var remoteTrackingSha = isDelivery
            ? fixture.Git(fixture.Target, ["rev-parse", $"refs/remotes/origin/{localBranch}"]).Output.Trim()
            : null;

        // Simulate a host process configured to route Beads to a different tracker.
        // The escalation writer must scrub these selectors and keep using Target Repository cwd.
        environment["BEADS_DIR"] = Path.Combine(foreignTracker.Path, ".beads");
        environment["BEADS_DB"] = Path.Combine(foreignTracker.Path, ".beads", "beads.db");
        environment["BD_DB"] = Path.Combine(foreignTracker.Path, ".beads", "beads.db");
        environment["BEADS_DOLT_SERVER_DATABASE"] = "foreign-database";
        environment["BEADS_DOLT_DATA_DIR"] = Path.Combine(foreignTracker.Path, ".beads", "embeddeddolt");
        var bareRemoteSha = isDelivery
            ? fixture.Git(fixture.BareOrigin, ["rev-parse", $"refs/heads/{localBranch}"]).Output.Trim()
            : null;

        var request = new NeedsHumanRequest(beadId, actor, NeedsHumanCause.OperationalRetriesExhausted, role,
            "Agent retries were exhausted after the worktree remained dirty.",
            ["latest verification found a dirty tracked file", "quotes \" and command-looking text `git clean -fd` remain data"],
            "20261024T142233123456Z-" + (isDelivery ? "implementer" : "planner"), [logPath]);
        var result = await new NeedsHumanEscalator(runner).EscalateAsync(fixture.Paths, project, fixture.Repository,
            request, CancellationToken.None);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures));
        Assert.False(Directory.Exists(provisioned.Path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.TmpRoot(project), beadId)));
        Assert.Equal(logSnapshot, Snapshot.FileTree(logDirectory));
        Assert.Equal(siblingWorktreeSnapshot, Snapshot.FileTree(worktreeSibling));
        Assert.Equal(siblingTmpSnapshot, Snapshot.FileTree(tmpSibling));
        Assert.Equal(rootBefore, CaptureTargetCheckout(fixture));

        var inventory = await new BeadsReader(runner).ReadSnapshotAsync(fixture.Repository, CancellationToken.None);
        var bead = inventory.ById[beadId];
        Assert.Equal("open", bead.Status);
        Assert.Null(bead.Assignee);
        Assert.Contains("needs-human", bead.Labels);
        Assert.Contains(stage, bead.Labels);
        Assert.Contains("retain-label", bead.Labels);
        Assert.DoesNotContain("product-decision-required", bead.Labels);
        Assert.Equal(2, bead.Payload.GetProperty("metadata").GetProperty("repair_cycles").GetInt32());
        Assert.Equal("metadata", bead.Payload.GetProperty("metadata").GetProperty("retain").GetString());

        var comments = await runner.RunAsync(new ProcessRequest("bd", ["--readonly", "--sandbox", "comments", beadId, "--json"],
            fixture.Target, BeadsCommand.Create(fixture.Target).Environment, TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(0, comments.ExitCode);
        using var commentsJson = JsonDocument.Parse(comments.StandardOutput);
        var diagnosticText = StringValues(commentsJson.RootElement)
            .Single(value => value.Contains("nightweaver-needs-human-v1", StringComparison.Ordinal));
        using var diagnostic = JsonDocument.Parse(diagnosticText);
        Assert.Equal("nightweaver-needs-human-v1", diagnostic.RootElement.GetProperty("schema").GetString());
        Assert.Equal($"latest verification found a dirty tracked file{Environment.NewLine}quotes \" and command-looking text `git clean -fd` remain data",
            diagnostic.RootElement.GetProperty("latest_findings").GetString());
        Assert.Equal(logPath, diagnostic.RootElement.GetProperty("log_files")[0].GetString());
        Assert.Equal(logDirectory, diagnostic.RootElement.GetProperty("log_directory").GetString());

        var localRef = fixture.Git(fixture.Target, ["show-ref", "--verify", "--quiet", $"refs/heads/{localBranch}"], allowFailure: true);
        if (isDelivery)
        {
            Assert.Equal(1, localRef.ExitCode);
            Assert.Equal(remoteBranchSha, fixture.Git(fixture.Target, ["rev-parse", $"refs/remotes/origin/{localBranch}"]).Output.Trim());
            Assert.Equal(bareRemoteSha, fixture.Git(fixture.BareOrigin, ["rev-parse", $"refs/heads/{localBranch}"]).Output.Trim());
        }
        else
        {
            Assert.Equal(0, localRef.ExitCode);
        }

        Assert.DoesNotContain(runner.Requests, process => process.FileName == "gh");
        Assert.DoesNotContain(runner.Requests, process => process.FileName == "git"
            && process.Arguments.Any(argument => argument is "push" or "fetch"));
        var gitArgs = runner.Requests.Where(process => process.FileName == "git").Select(process => process.Arguments).ToArray();
        Assert.True(Array.FindIndex(gitArgs, args => args.Count > 1 && args[0] == "worktree" && args[1] == "remove")
            < Array.FindIndex(gitArgs, args => args.Count > 1 && args[0] == "worktree" && args[1] == "prune"));
        if (isDelivery)
            Assert.True(Array.FindIndex(gitArgs, args => args.Count > 1 && args[0] == "branch" && args[1] == "-D")
                < Array.FindIndex(gitArgs, args => args.Count > 1 && args[0] == "worktree" && args[1] == "prune"));
        else
            Assert.DoesNotContain(gitArgs, args => args.Count > 1 && args[0] == "branch" && args[1] == "-D");
        Assert.Equal(foreignTrackerBefore, Snapshot.FileTree(foreignTracker.Path));
        Assert.Equal(foreignInventoryBefore, foreignTracker.Run("bd", BeadsCommand.Create(foreignTracker.Path).Arguments));
        Assert.Contains(foreignBead, foreignTracker.Run("bd", BeadsCommand.Create(foreignTracker.Path).Arguments), StringComparison.Ordinal);
    }

    private static async Task<JsonDocument> RunBdJsonAsync(IProcessRunner runner, string cwd, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var result = await RunBdAsync(runner, cwd, arguments, environment);
        return JsonDocument.Parse(result);
    }

    private static async Task<string> RunBdAsync(IProcessRunner runner, string cwd, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var result = await runner.RunAsync(new ProcessRequest("bd", arguments, cwd, environment, TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput;
    }

    private static RootCheckoutSnapshot CaptureTargetCheckout(ProvisioningGitFixture fixture)
    {
        var checkout = fixture.TargetCheckout();
        var files = string.Join("\n", checkout.WorkingFiles.Split('\n').Where(line => !line.Contains(".beads", StringComparison.Ordinal)));
        return new(checkout.IndexSha, checkout.Head, checkout.Branch, files, checkout.Status);
    }

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString()!;
            yield break;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var value in StringValues(item)) yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var value in StringValues(property.Value)) yield return value;
        }
    }

    private sealed record RootCheckoutSnapshot(string IndexSha, string Head, string Branch, string WorkingFiles, string Status);

    private sealed class EnvironmentProcessRunner(IReadOnlyDictionary<string, string?> baseEnvironment) : IProcessRunner
    {
        private readonly SystemProcessRunner runner = new();
        internal List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var environment = new Dictionary<string, string?>(baseEnvironment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            return runner.RunAsync(request with { Environment = environment }, cancellationToken);
        }
    }
}
