using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class NeedsHumanEscalatorTests
{
    [Theory]
    [InlineData("feature", "Planner", "OperationalRetriesExhausted", "stage:plan")]
    [InlineData("bug", "PlanChallenger", "DuplicateFailure", "stage:plan-challenge")]
    [InlineData("task", "Implementer", "RepairBudgetExhausted", "stage:implement")]
    [InlineData("feature", "Planner", "RepairBudgetExhausted", "")]
    [InlineData("task", "Implementer", "OperationalRetriesExhausted", "")]
    public async Task EscalationWritesPauseBeforeDiagnosticAndRetainsTheOwnedEnvironment(
        string issueType, string roleName, string causeName, string stage)
    {
        var role = Enum.Parse<AgentRole>(roleName);
        var cause = Enum.Parse<NeedsHumanCause>(causeName);
        var labels = new List<string> { "product-decision-required", "retain-me" };
        if (stage.Length > 0) labels.Add(stage);
        using var target = new FakeEscalationTarget(issueType, labels);
        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository,
            target.Request(cause, role), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("needs-human", target.Labels);
        if (stage.Length > 0) Assert.Contains(stage, target.Labels);
        Assert.Contains("retain-me", target.Labels);
        Assert.DoesNotContain("product-decision-required", target.Labels);
        Assert.Equal("open", target.Status);
        Assert.Null(target.Assignee);
        Assert.True(result.EnvironmentRetained);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.Equal("tmp payload", File.ReadAllText(Path.Combine(target.TemporaryPath, "invocation", "tmp.txt")));
        Assert.Equal("settled log stays", File.ReadAllText(target.LogPath));

        var writes = target.Runner.Requests.Where(IsWrite).ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Contains("--add-label", writes[0].Arguments);
        Assert.Contains("needs-human", writes[0].Arguments);
        Assert.Contains("--remove-label", writes[0].Arguments);
        Assert.Contains("product-decision-required", writes[0].Arguments);
        Assert.Equal("nightweaver:demo", writes[0].Arguments[Array.IndexOf(writes[0].Arguments.ToArray(), "--if-assignee") + 1]);
        Assert.Equal("in_progress", writes[0].Arguments[Array.IndexOf(writes[0].Arguments.ToArray(), "--if-status") + 1]);
        Assert.Equal("", writes[^1].Arguments[Array.IndexOf(writes[^1].Arguments.ToArray(), "--assignee") + 1]);
        Assert.DoesNotContain(writes.SelectMany(request => request.Arguments), argument => argument == "--force");
        foreach (var write in writes)
        {
            Assert.DoesNotContain("--readonly", write.Arguments);
            Assert.Equal("on", write.Environment["BD_DOLT_AUTO_COMMIT"]);
            Assert.Null(write.Environment["BD_DISABLE_EVENT_FLUSH"]);
            Assert.Null(write.Environment["BEADS_DIR"]);
            Assert.Contains("--sandbox", write.Arguments);
        }

        var args = target.Runner.Requests.Select(request => request.Arguments).ToArray();
        var pause = Array.FindIndex(args, list => list.Contains("--add-label") && list.Contains("needs-human"));
        var diagnostic = Array.FindIndex(args, list => list.Contains("comments") && list.Contains("add"));
        var release = Array.FindIndex(args, list => list.Contains("--status") && list.Contains("open"));
        Assert.True(pause < diagnostic && diagnostic < release);
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
        using var diagnosticJson = JsonDocument.Parse(target.Comments.Single());
        Assert.Contains(target.WorktreePath, diagnosticJson.RootElement.GetProperty("environment_retention").GetString(), StringComparison.Ordinal);
        Assert.Contains(target.TemporaryPath, diagnosticJson.RootElement.GetProperty("environment_retention").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticKeepsDataAsJsonAndTruncatesFindingsAtRuneBoundaries()
    {
        using var target = new FakeEscalationTarget("task", ["stage:implement"]);
        var raw = "quote \" and newline\n literal `; Remove-Item *;` " + string.Concat(Enumerable.Repeat("🧭", 9000));
        var request = target.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Implementer) with
        {
            Reason = "timeout said \"still working\"\nsecond line",
            LatestFindings = [raw],
            InvocationId = "20261024T142233123456Z-implementer",
            LogFilePaths = [target.LogPath]
        };

        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository, request, CancellationToken.None);
        Assert.True(result.Succeeded);
        var comment = target.Comments.Single();
        Assert.True(Encoding.UTF8.GetByteCount(comment) <= 32 * 1024);
        using var diagnostic = JsonDocument.Parse(comment);
        var root = diagnostic.RootElement;
        Assert.Equal("nightweaver-needs-human-v1", root.GetProperty("schema").GetString());
        Assert.Equal("task", root.GetProperty("bead_type").GetString());
        Assert.Equal("stage:implement", root.GetProperty("stage").GetString());
        Assert.Equal("duplicate-failure", root.GetProperty("cause").GetString());
        Assert.Equal("implementer", root.GetProperty("failed_role").GetString());
        Assert.Equal(request.Reason, root.GetProperty("reason").GetString());
        Assert.Equal(request.InvocationId, root.GetProperty("invocation_id").GetString());
        Assert.Equal(target.LogPath, root.GetProperty("log_files")[0].GetString());
        Assert.Equal(target.LogDirectory, root.GetProperty("log_directory").GetString());
        var findings = root.GetProperty("latest_findings").GetString()!;
        Assert.StartsWith("quote \" and newline\n literal `; Remove-Item *;`", findings, StringComparison.Ordinal);
        Assert.EndsWith("[findings truncated]", findings, StringComparison.Ordinal);
        Assert.Contains("🧭", findings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairBudgetCommentStatesWhenNoInvocationLogExists()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]);
        var request = target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner) with
        {
            LatestFindings = ["Successful rejection exhausted repair_cycles=3."],
            InvocationId = null,
            LogFilePaths = null
        };
        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository, request, CancellationToken.None);

        Assert.True(result.Succeeded);
        using var comment = JsonDocument.Parse(target.Comments.Single());
        var root = comment.RootElement;
        Assert.Equal("unavailable", root.GetProperty("invocation_id").GetString());
        Assert.Equal("unavailable", root.GetProperty("log_files")[0].GetString());
        Assert.Equal(target.LogDirectory, root.GetProperty("log_directory").GetString());
    }

    [Fact]
    public async Task UnsafeNestedTemporaryLinkIsLeftIntactWhenPauseIsRecorded()
    {
        using var target = new FakeEscalationTarget("task", ["stage:implement"]);
        using var external = new TempDirectory();
        var sentinel = external.WriteFile("sentinel.txt", "must remain");
        WorktreeInspectionTests.CreateDirectoryLink(Path.Combine(target.TemporaryPath, "linked"), external.Path);
        var before = Snapshot.FileTree(external.Path);

        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.OperationalRetriesExhausted, AgentRole.Implementer), CancellationToken.None);

        Assert.True(result.PauseRecorded);
        Assert.True(result.DiagnosticRecorded);
        Assert.True(result.EnvironmentRetained);
        Assert.True(result.ClaimReleased);
        Assert.Empty(result.Failures);
        Assert.Equal(before, Snapshot.FileTree(external.Path));
        Assert.Equal("must remain", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git"
            && request.Arguments.Any(argument => argument is "remove" or "prune" or "-D"));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
    }

    [Fact]
    public async Task PauseDoesNotPreflightOrTouchWorktreeBeforeRetainingIt()
    {
        using var target = new FakeEscalationTarget("task", ["stage:implement"]);
        var result = await target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Implementer), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.EnvironmentRetained);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
    }

    [Fact]
    public async Task ForeignClaimAndOversizedRequiredReasonCauseNoWrites()
    {
        using (var target = new FakeEscalationTarget("task", ["stage:implement"], assignee: "someone-else"))
        {
            await Assert.ThrowsAsync<AgentStageInputException>(() => target.Escalator().EscalateAsync(target.Paths, target.Project,
                target.Repository, target.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Implementer), CancellationToken.None));
            Assert.DoesNotContain(target.Runner.Requests, IsWrite);
        }

        using (var target = new FakeEscalationTarget("task", ["stage:implement"]))
        {
            var request = target.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Implementer) with { Reason = new string('r', 40_000) };
            await Assert.ThrowsAsync<AgentStageInputException>(() => target.Escalator().EscalateAsync(target.Paths, target.Project,
                target.Repository, request, CancellationToken.None));
            Assert.DoesNotContain(target.Runner.Requests, IsWrite);
        }
    }

    [Fact]
    public async Task ExistingOpenPauseWithConflictingPauseLabelIsNormalizedAndRetained()
    {
        using var target = new FakeEscalationTarget("feature",
            ["stage:plan", "needs-human", "product-decision-required", "retain-me"], assignee: null, status: "open");

        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("needs-human", target.Labels);
        Assert.DoesNotContain("product-decision-required", target.Labels);
        Assert.Equal("open", target.Status);
        Assert.Null(target.Assignee);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        var pauseWrite = Assert.Single(target.Runner.Requests, request => request.FileName == "bd"
            && request.Arguments.Contains("--add-label"));
        Assert.Equal(string.Empty, pauseWrite.Arguments[Array.IndexOf(pauseWrite.Arguments.ToArray(), "--if-assignee") + 1]);
        Assert.Equal("open", pauseWrite.Arguments[Array.IndexOf(pauseWrite.Arguments.ToArray(), "--if-status") + 1]);
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "bd"
            && request.Arguments.Contains("--status"));
    }

    [Fact]
    public async Task RepeatedEscalationCompletesFromFreshUnclaimedPausedState()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]);
        var request = target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner);
        Assert.True((await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository, request,
            CancellationToken.None)).Succeeded);
        var beforeRepeat = target.Runner.Requests.Count;

        var repeated = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository, request,
            CancellationToken.None);

        Assert.True(repeated.Succeeded);
        Assert.True(repeated.EnvironmentRetained);
        Assert.Equal(2, target.Comments.Count);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests.Skip(beforeRepeat), request => request.FileName == "git"
            && request.Arguments.Any(argument => argument is "remove" or "prune" or "-D"));
    }

    [Fact]
    public async Task TemporaryOnlyPausedRecoveryRetainsItsTree()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan", "needs-human"], assignee: null, status: "open");
        target.RemoveOwnedWorktreeBeforeCall();

        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.EnvironmentRetained);
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
    }

    [Fact]
    public async Task BranchDeletionFailureInjectionCannotDeletePausedEnvironment()
    {
        using var target = new FakeEscalationTarget("task", ["stage:implement"])
        {
            FailBranchDelete = true
        };

        var result = await target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Implementer), CancellationToken.None);

        Assert.True(result.PauseRecorded);
        Assert.True(result.DiagnosticRecorded);
        Assert.True(result.EnvironmentRetained);
        Assert.True(result.ClaimReleased);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.Equal("open", target.Status);
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("prune")]
    public async Task GitCleanupFailureInjectionDoesNotRunForPausedEnvironments(string operation)
    {
        using var target = new FakeEscalationTarget("task", ["stage:implement"]) { FailGitOperation = operation };

        var result = await target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Implementer), CancellationToken.None);

        Assert.True(result.EnvironmentRetained);
        Assert.True(result.ClaimReleased);
        Assert.Equal("open", target.Status);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
    }

    [Fact]
    public async Task RepeatedPauseDoesNotRemoveWorktreeOrTemporaryFiles()
    {
        using var target = new FakeEscalationTarget("task", ["stage:implement"]) { FailBranchDelete = true };
        var request = target.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Implementer);
        var requestCount = target.Runner.Requests.Count;

        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository,
            request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.EnvironmentRetained);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests.Skip(requestCount), request => request.FileName == "git");
    }

    [Fact]
    public async Task FailedClaimReleaseReportsIncompleteProgressAndRetainsEnvironment()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]) { FailRelease = true };

        var error = await Assert.ThrowsAsync<NeedsHumanEscalationException>(() => target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None));

        Assert.True(error.Progress.PauseRecorded);
        Assert.True(error.Progress.DiagnosticRecorded);
        Assert.True(error.Progress.EnvironmentRetained);
        Assert.False(error.Progress.ClaimReleased);
        Assert.Equal("in_progress", target.Status);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
    }

    [Fact]
    public async Task ClaimChangeAfterPausePreventsCommentAndCleanupWithoutReleasingForeignClaim()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]) { ChangeClaimAfterPause = true };

        var error = await Assert.ThrowsAsync<NeedsHumanEscalationException>(() => target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None));

        Assert.True(error.Progress.PauseRecorded);
        Assert.False(error.Progress.DiagnosticRecorded);
        Assert.True(error.Progress.EnvironmentRetained);
        Assert.False(error.Progress.ClaimReleased);
        Assert.Equal("someone-else", target.Assignee);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git"
            && request.Arguments.Any(argument => argument is "remove" or "prune" or "-D"));
    }

    [Fact]
    public async Task TemporaryDeleteFailureInjectionCannotDeletePausedEnvironment()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]);
        var result = await target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.OperationalRetriesExhausted, AgentRole.Planner), CancellationToken.None);

        Assert.True(result.EnvironmentRetained);
        Assert.True(result.ClaimReleased);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(File.Exists(Path.Combine(target.TemporaryPath, "invocation", "tmp.txt")));
        Assert.Equal("open", target.Status);
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
    }

    [Fact]
    public async Task CancellationAfterPauseSkipsCleanupButUsesBoundedGuardedClaimRelease()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]);
        using var cancellation = new CancellationTokenSource();
        target.CancelAfterPause = cancellation;

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), cancellation.Token));

        var progress = Assert.IsType<NeedsHumanEscalationResult>(error.Data[nameof(NeedsHumanEscalationResult)]);
        Assert.True(progress.PauseRecorded);
        Assert.False(progress.DiagnosticRecorded);
        Assert.True(progress.EnvironmentRetained);
        Assert.True(progress.ClaimReleased);
        Assert.Equal("open", target.Status);
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git"
            && request.Arguments.Any(argument => argument is "remove" or "prune" or "-D"));
    }

    [Fact]
    public async Task CancellationBeforeMutationPropagatesWithoutTrackerWrites()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), cancellation.Token));

        Assert.DoesNotContain(target.Runner.Requests, IsWrite);
        Assert.Equal("in_progress", target.Status);
        Assert.Equal("nightweaver:demo", target.Assignee);
    }

    [Fact]
    public async Task FailedLabelWriteDoesNotReleaseClaimOrTouchRuntimeResources()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]) { FailLabelWrite = true };

        var error = await Assert.ThrowsAsync<NeedsHumanEscalationException>(() => target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None));

        Assert.False(error.Progress.PauseRecorded);
        Assert.False(error.Progress.DiagnosticRecorded);
        Assert.True(error.Progress.EnvironmentRetained);
        Assert.False(error.Progress.ClaimReleased);
        Assert.Equal("in_progress", target.Status);
        Assert.Equal("nightweaver:demo", target.Assignee);
        Assert.Empty(target.Comments);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git"
            && request.Arguments.Any(argument => argument is "remove" or "prune" or "-D"));
    }

    [Fact]
    public async Task FailedDiagnosticCommentPreventsCleanupButStillReleasesThePausedClaim()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]) { FailDiagnosticComment = true };

        var error = await Assert.ThrowsAsync<NeedsHumanEscalationException>(() => target.Escalator().EscalateAsync(
            target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None));

        Assert.True(error.Progress.PauseRecorded);
        Assert.False(error.Progress.DiagnosticRecorded);
        Assert.True(error.Progress.EnvironmentRetained);
        Assert.True(error.Progress.ClaimReleased);
        Assert.Equal("open", target.Status);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git"
            && request.Arguments.Any(argument => argument is "remove" or "prune" or "-D"));
    }

    [Fact]
    public async Task MalformedGitInventoryDoesNotPreventPauseAndRetention()
    {
        using var target = new FakeEscalationTarget("feature", ["stage:plan"]) { MalformedWorktreeOutput = true };

        var result = await target.Escalator().EscalateAsync(target.Paths, target.Project, target.Repository,
            target.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None);

        Assert.True(result.PauseRecorded);
        Assert.True(result.DiagnosticRecorded);
        Assert.True(result.EnvironmentRetained);
        Assert.True(result.ClaimReleased);
        Assert.Empty(result.Failures);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, request => request.FileName == "git");
    }

    [Fact]
    public async Task InvalidTypeStageAndCrossBeadRequestsAreRejectedBeforeWrites()
    {
        using (var wrongType = new FakeEscalationTarget("task", ["stage:implement"]))
        {
            await Assert.ThrowsAsync<AgentStageInputException>(() => wrongType.Escalator().EscalateAsync(wrongType.Paths,
                wrongType.Project, wrongType.Repository, wrongType.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Planner), CancellationToken.None));
            Assert.DoesNotContain(wrongType.Runner.Requests, IsWrite);
        }
        using (var badStage = new FakeEscalationTarget("feature", ["stage:plan", "stage:qa"]))
        {
            await Assert.ThrowsAsync<AgentStageInputException>(() => badStage.Escalator().EscalateAsync(badStage.Paths,
                badStage.Project, badStage.Repository, badStage.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Planner), CancellationToken.None));
            Assert.DoesNotContain(badStage.Runner.Requests, IsWrite);
        }
        using (var closed = new FakeEscalationTarget("feature", ["stage:plan"], status: "closed"))
        {
            await Assert.ThrowsAsync<AgentStageInputException>(() => closed.Escalator().EscalateAsync(closed.Paths,
                closed.Project, closed.Repository, closed.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None));
            Assert.DoesNotContain(closed.Runner.Requests, IsWrite);
        }
        using (var planned = new FakeEscalationTarget("feature", ["stage:plan", "planned"]))
        {
            await Assert.ThrowsAsync<AgentStageInputException>(() => planned.Escalator().EscalateAsync(planned.Paths,
                planned.Project, planned.Repository, planned.Request(NeedsHumanCause.RepairBudgetExhausted, AgentRole.Planner), CancellationToken.None));
            Assert.DoesNotContain(planned.Runner.Requests, IsWrite);
        }
        using (var otherBead = new FakeEscalationTarget("feature", ["stage:plan"]))
        {
            var request = otherBead.Request(NeedsHumanCause.DuplicateFailure, AgentRole.Planner) with
            {
                BeadId = "other-bead",
                LogFilePaths = null
            };
            await Assert.ThrowsAsync<AgentStageInputException>(() => otherBead.Escalator().EscalateAsync(otherBead.Paths,
                otherBead.Project, otherBead.Repository, request, CancellationToken.None));
            Assert.DoesNotContain(otherBead.Runner.Requests, IsWrite);
        }
    }

    private static bool IsWrite(ProcessRequest request) => request.FileName == "bd"
        && request.Arguments.Count > 3 && request.Arguments[0] == "--sandbox";

    private sealed class FakeEscalationTarget : IDisposable
    {
        private readonly TempDirectory temp = new();
        private bool worktreePresent = true;
        private bool registered = true;
        private bool localBranchPresent;

        internal FakeEscalationTarget(string issueType, IReadOnlyList<string> labels, string? assignee = "nightweaver:demo",
            string status = "in_progress")
        {
            RepositoryRoot = temp.CreateDirectory("target repository with spaces");
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".git"));
            var beads = Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".beads")).FullName;
            File.WriteAllText(Path.Combine(beads, "metadata.json"), "{\"dolt_mode\":\"embedded\"}");
            Directory.CreateDirectory(Path.Combine(beads, "embeddeddolt"));
            File.WriteAllText(Path.Combine(RepositoryRoot, ".beads.gate.lock"), "");
            File.WriteAllText(Path.Combine(beads, "embeddeddolt.gate.lock"), "");
            Paths = new HostPaths(temp.CreateDirectory("nightweaver host with spaces"));
            Project = new ProjectSlug("demo");
            BeadId = "fixture-bead";
            IssueType = issueType;
            Labels = labels.ToList();
            Assignee = assignee;
            Status = status;
            WorktreePath = Path.Combine(Paths.WorktreesRoot(Project), BeadId);
            TemporaryPath = Path.Combine(Paths.TmpRoot(Project), BeadId);
            LogDirectory = Path.Combine(Paths.LogsRoot(Project), BeadId);
            Directory.CreateDirectory(WorktreePath);
            Directory.CreateDirectory(Path.Combine(TemporaryPath, "invocation"));
            LogPath = Path.Combine(LogDirectory, "20261024T142233123456Z-implementer.log");
            Directory.CreateDirectory(LogDirectory);
            File.WriteAllText(LogPath, "settled log stays");
            TempWrite("tmp payload", Path.Combine(TemporaryPath, "invocation", "tmp.txt"));
            if (issueType == "task") localBranchPresent = true;
            Repository = new ValidatedTargetRepository(RepositoryRoot, "main");
            Runner = new RecordingProcessRunner(RunRequestAsync);
        }

        internal string RepositoryRoot { get; }
        internal string BeadId { get; }
        internal string IssueType { get; }
        internal List<string> Labels { get; }
        internal string Status { get; private set; }
        internal string? Assignee { get; private set; }
        internal string WorktreePath { get; }
        internal string TemporaryPath { get; }
        internal string LogDirectory { get; }
        internal string LogPath { get; }
        internal HostPaths Paths { get; }
        internal ProjectSlug Project { get; }
        internal ValidatedTargetRepository Repository { get; }
        internal RecordingProcessRunner Runner { get; }
        internal List<string> Comments { get; } = [];
        internal bool FailBranchDelete { get; set; }
        internal bool FailLabelWrite { get; set; }
        internal bool FailDiagnosticComment { get; set; }
        internal string? FailGitOperation { get; set; }
        internal bool FailRelease { get; set; }
        internal bool ChangeClaimAfterPause { get; set; }
        internal bool MalformedWorktreeOutput { get; set; }
        internal CancellationTokenSource? CancelAfterPause { get; set; }

        internal NeedsHumanRequest Request(NeedsHumanCause cause, AgentRole role) =>
            new(BeadId, "nightweaver:demo", cause, role, "operational failure with detail", ["latest finding"],
                "20261024T142233123456Z-implementer", [LogPath]);

        internal NeedsHumanEscalator Escalator() => new(Runner);

        internal void RemoveOwnedWorktreeBeforeCall()
        {
            registered = false;
            worktreePresent = false;
            DeleteTree(WorktreePath);
        }

        private Task<ProcessResult> RunRequestAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.FileName == "bd") return Task.FromResult(RunBd(request));
            Assert.Equal("git", request.FileName);
            if (request.Arguments.SequenceEqual(["worktree", "list", "--porcelain", "-z"]))
                return Task.FromResult(new ProcessResult(0, MalformedWorktreeOutput ? "not a worktree" : WorktreeList(), ""));
            if (request.Arguments.SequenceEqual(["show-ref", "--verify", "--quiet", $"refs/heads/nightweaver/{BeadId}"]))
                return Task.FromResult(new ProcessResult(localBranchPresent ? 0 : 1, "", ""));
            if (request.Arguments.Count == 4 && request.Arguments[0] == "worktree" && request.Arguments[1] == "remove"
                && request.Arguments[2] == "--force" && request.Arguments[3] == WorktreePath)
            {
                if (FailGitOperation == "remove") return Task.FromResult(new ProcessResult(1, "", "controlled worktree removal failure"));
                registered = false;
                worktreePresent = false;
                DeleteTree(WorktreePath);
                return Task.FromResult(new ProcessResult(0, "", ""));
            }
            if (request.Arguments.SequenceEqual(["branch", "-D", $"nightweaver/{BeadId}"]))
            {
                if (FailBranchDelete) return Task.FromResult(new ProcessResult(1, "", "controlled branch deletion failure"));
                localBranchPresent = false;
                return Task.FromResult(new ProcessResult(0, "", ""));
            }
            if (request.Arguments.SequenceEqual(["worktree", "prune"]))
                return Task.FromResult(FailGitOperation == "prune"
                    ? new ProcessResult(1, "", "controlled prune failure")
                    : new ProcessResult(0, "", ""));
            throw new InvalidOperationException($"Unexpected fake process request: {request.FileName} {string.Join(' ', request.Arguments)}");
        }

        private ProcessResult RunBd(ProcessRequest request)
        {
            if (request.Arguments.SequenceEqual(BeadsCommand.Create(RepositoryRoot).Arguments))
            {
                var row = new
                {
                    id = BeadId,
                    status = Status,
                    issue_type = IssueType,
                    priority = 2,
                    created_at = "2026-10-24T14:22:33Z",
                    assignee = Assignee,
                    labels = Labels,
                    metadata = new { repair_cycles = 2 }
                };
                return new ProcessResult(0, JsonSerializer.Serialize(new[] { row }), "");
            }
            var args = request.Arguments;
            if (args.Count > 3 && args[3] == "update" && args.Contains("--add-label"))
            {
                if (FailLabelWrite) return new ProcessResult(13, "", "stale assignee guard");
                var assigneeIndex = Array.IndexOf(args.ToArray(), "--assignee");
                if (assigneeIndex >= 0) Assignee = args[assigneeIndex + 1] is { Length: > 0 } value ? value : null;
                Labels.Remove("product-decision-required");
                if (!Labels.Contains("needs-human", StringComparer.Ordinal)) Labels.Add("needs-human");
                if (ChangeClaimAfterPause) Assignee = "someone-else";
                CancelAfterPause?.Cancel();
                return new ProcessResult(0, "", "");
            }
            if (args.Count > 3 && args[3] == "comments" && args[4] == "add")
            {
                if (FailDiagnosticComment) return new ProcessResult(1, "", "controlled diagnostic comment failure");
                Comments.Add(args[6]);
                return new ProcessResult(0, "", "");
            }
            if (args.Count > 3 && args[3] == "update" && args.Contains("--status"))
            {
                if (FailRelease) return new ProcessResult(1, "", "controlled claim release failure");
                Status = "open";
                Assignee = null;
                return new ProcessResult(0, "", "");
            }
            throw new InvalidOperationException($"Unexpected fake bd request: {string.Join(' ', args)}");
        }

        private string WorktreeList()
        {
            var output = $"worktree {RepositoryRoot}\0HEAD {new string('a', 40)}\0branch refs/heads/main\0\0";
            if (!registered || !worktreePresent) return output;
            output += IssueType == "task"
                ? $"worktree {WorktreePath}\0HEAD {new string('b', 40)}\0branch refs/heads/nightweaver/{BeadId}\0\0"
                : $"worktree {WorktreePath}\0HEAD {new string('b', 40)}\0detached\0\0";
            return output;
        }

        private static void TempWrite(string contents, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        private static void DeleteTree(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) DeleteTree(child);
                else File.Delete(child);
            }
            Directory.Delete(path);
        }

        public void Dispose() => temp.Dispose();
    }
}
