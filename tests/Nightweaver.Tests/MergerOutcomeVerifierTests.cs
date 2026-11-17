using System.Diagnostics;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class MergerOutcomeVerifierTests
{
    [Theory]
    [InlineData("absent-terminal")]
    [InlineData("failed-terminal")]
    [InlineData("incomplete-terminal")]
    [InlineData("absent-result")]
    [InlineData("foreign-invocation")]
    [InlineData("foreign-evidence")]
    [InlineData("foreign-head")]
    [InlineData("filtered-command")]
    [InlineData("failed-result")]
    [InlineData("incomplete-result")]
    [InlineData("wrong-elapsed")]
    [InlineData("dirty-result")]
    [InlineData("duplicate-result")]
    [InlineData("oversized-result")]
    [InlineData("invalid-utf8-result")]
    [InlineData("absent-suite-log")]
    [InlineData("failed-suite-log")]
    [InlineData("incomplete-suite-log")]
    [InlineData("absent-xml")]
    [InlineData("failed-xml")]
    [InlineData("incomplete-xml")]
    [InlineData("invalid-xml")]
    public async Task MatchingClaimsCannotReplaceActualCompleteMeasurement(string mode)
    {
        using var fixture = new VerifierFixture();
        fixture.SetPerformanceFinding();
        var resultPath = Path.Combine(fixture.MeasurementDirectory, "result.json");
        var result = File.ReadAllText(resultPath);
        var originalResult = result;
        var log = File.ReadAllText(fixture.Invocation.LogFilePath);
        var terminalEnd = log.IndexOf("NIGHTWEAVER_PERFORMANCE_CHECK_V1", StringComparison.Ordinal);
        switch (mode)
        {
            case "absent-terminal": File.WriteAllText(fixture.Invocation.LogFilePath, log[terminalEnd..]); break;
            case "failed-terminal": File.WriteAllText(fixture.Invocation.LogFilePath, "Test run summary: Failed!\ntotal: 1325\nfailed: 1\n" + log[terminalEnd..]); break;
            case "incomplete-terminal": File.WriteAllText(fixture.Invocation.LogFilePath, "Test run summary: Passed!\ntotal: 1325\n" + log[terminalEnd..]); break;
            case "absent-result": File.Delete(resultPath); break;
            case "foreign-invocation": result = result.Replace(fixture.Invocation.InvocationId, "old-invocation", StringComparison.Ordinal); break;
            case "foreign-evidence": result = result.Replace("0123456789abcdef0123456789abcdef", new string('d', 32), StringComparison.Ordinal); break;
            case "foreign-head": result = result.Replace(fixture.Head, new string('d', 40), StringComparison.Ordinal); break;
            case "filtered-command": result = result.Replace("--show-slowest-tests 20", "--filter-class X", StringComparison.Ordinal); break;
            case "failed-result": result = result.Replace("\"testExitCode\":0", "\"testExitCode\":1", StringComparison.Ordinal); break;
            case "incomplete-result": result = result.Replace("\"passed\":1325", "\"passed\":1324", StringComparison.Ordinal); break;
            case "wrong-elapsed": result = result.Replace("300.5", "301.5", StringComparison.Ordinal); break;
            case "dirty-result": result = result.Replace("\"dirty\":false", "\"dirty\":true", StringComparison.Ordinal); break;
            case "duplicate-result": result = result.Replace("\"failed\":0", "\"failed\":1,\"failed\":0", StringComparison.Ordinal); break;
            case "oversized-result": result += new string(' ', MergerLocalCheckReader.MaximumEvidenceBytes); break;
            case "invalid-utf8-result": File.WriteAllBytes(resultPath, [0xff]); break;
            case "absent-suite-log": File.Delete(Path.Combine(fixture.MeasurementDirectory, "suite.log")); break;
            case "failed-suite-log": File.WriteAllText(Path.Combine(fixture.MeasurementDirectory, "suite.log"), "Test run summary: Failed!\n"); break;
            case "incomplete-suite-log": File.WriteAllText(Path.Combine(fixture.MeasurementDirectory, "suite.log"), "Test run summary: Passed!\ntotal: 1325\n"); break;
            case "absent-xml": File.Delete(Path.Combine(fixture.MeasurementDirectory, "report-1.xml")); break;
            case "failed-xml":
            case "incomplete-xml":
            case "invalid-xml":
                var xmlPath = Path.Combine(fixture.MeasurementDirectory, "report-1.xml");
                var xml = File.ReadAllText(xmlPath);
                File.WriteAllText(xmlPath, mode == "invalid-xml" ? "<broken" : xml.Replace(
                    mode == "failed-xml" ? "failed=\"0\"" : "not-run=\"0\"",
                    mode == "failed-xml" ? "failed=\"1\"" : "not-run=\"1\"", StringComparison.Ordinal));
                break;
        }
        if (result != originalResult) File.WriteAllText(resultPath, result);
        Assert.False((await fixture.VerifyAsync()).Succeeded);
    }

    [Fact]
    public async Task AllPassingHardLimitUsesOrdinaryGuardedRepairWithoutInventingFailures()
    {
        using var fixture = new VerifierFixture();
        fixture.SetPerformanceFinding();
        var verified = await fixture.VerifyAsync();
        Assert.True(verified.Succeeded, verified.Diagnostic);
        Assert.Equal(0, verified.Finding!.LocalExitCode);
        Assert.Equal(1325, verified.Finding.Performance!.Passed);
        Assert.Equal(0, fixture.Runner.NativeGitCommandCount);
        fixture.Runner.Status = "open";
        fixture.Runner.Assignee = null;
        var result = await fixture.RunSlotAsync();
        Assert.Equal(MergerSlotDisposition.RepairHandedOff, result.Disposition);
        Assert.True(result.ClaimHandoffCompleted, result.Diagnostic);
        Assert.Equal(1, fixture.Runner.RepairCycles);
        Assert.Single(fixture.Runner.PublicationWrites);
        var duplicate = await fixture.RunSlotAsync();
        Assert.False(duplicate.RepairHandoffCompleted);
        Assert.Equal(1, fixture.Runner.RepairCycles);
        Assert.Single(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData("elapsed_seconds", "299.999")]
    [InlineData("elapsed_seconds", "-1")]
    [InlineData("elapsed_seconds", "1e999")]
    [InlineData("failed", "1")]
    [InlineData("skipped", "1")]
    [InlineData("total", "0")]
    [InlineData("passed", "1324")]
    [InlineData("test_exit_code", "1")]
    [InlineData("hard_limit_seconds", "240")]
    [InlineData("command", "\"dotnet test --filter-class X\"")]
    [InlineData("evidence_id", "\"old\"")]
    public async Task RejectsInvalidPerformancePolicyEvenWhenFrameAndCommentAgree(string key, string json)
    {
        using var fixture = new VerifierFixture();
        fixture.SetPerformanceFinding(key, json);
        Assert.False((await fixture.VerifyAsync()).Succeeded);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("quoted")]
    [InlineData("foreign")]
    [InlineData("head")]
    [InlineData("pr")]
    [InlineData("mismatch")]
    [InlineData("later")]
    [InlineData("oversized")]
    [InlineData("oversized-frame")]
    [InlineData("owner")]
    [InlineData("dirty")]
    [InlineData("incomplete")]
    [InlineData("utf8")]
    [InlineData("duplicate-json")]
    [InlineData("historical-comment")]
    public async Task RejectsAmbiguousForeignOrContradictoryPerformanceEvidence(string mode)
    {
        using var fixture = new VerifierFixture();
        fixture.SetPerformanceFinding();
        var path = fixture.Invocation.LogFilePath;
        var log = File.ReadAllText(path);
        switch (mode)
        {
            case "duplicate": log += log; break;
            case "quoted": log = log.Replace("\nNIGHTWEAVER_PERFORMANCE_CHECK_V1", "\n> NIGHTWEAVER_PERFORMANCE_CHECK_V1", StringComparison.Ordinal); break;
            case "foreign": log = log.Replace(fixture.Invocation.InvocationId, "old-invocation", StringComparison.Ordinal); break;
            case "head": log = log.Replace(fixture.Head, new string('d', 40), StringComparison.Ordinal); break;
            case "pr": fixture.Runner.ReportedPullRequestUrl = "https://github.com/attacker/other/pull/123"; break;
            case "mismatch": log = log.Replace("300.5", "301.5", StringComparison.Ordinal); break;
            case "later": log += "Test run summary: Passed!\n"; break;
            case "oversized": log = new string('x', MergerLocalCheckReader.MaximumLogBytes) + log; break;
            case "oversized-frame": log = log.Replace("All tests passed;", new string('x', MergerLocalCheckReader.MaximumEvidenceBytes), StringComparison.Ordinal); break;
            case "owner": fixture.Runner.Assignee = "human:competitor"; break;
            case "dirty": fixture.Invocation = fixture.Invocation with { Observations = fixture.Invocation.Observations! with { WorktreeClean = false } }; break;
            case "incomplete": log = log[..log.IndexOf("END_", StringComparison.Ordinal)]; break;
            case "utf8":
                File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(log).SelectMany(b => b == (byte)'{' ? new byte[] { b, 0xff } : new[] { b }).ToArray());
                Assert.False((await fixture.VerifyAsync()).Succeeded);
                return;
            case "duplicate-json": log = log.Replace("\"total\":1325", "\"total\":1325,\"total\":1325", StringComparison.Ordinal); break;
            case "historical-comment": fixture.Invocation = fixture.Invocation with { PreInvocationCommentIds = new HashSet<string>(["current"]) }; break;
        }
        File.WriteAllText(path, log);
        Assert.False((await fixture.VerifyAsync()).Succeeded);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WorktreeReadsOverlapAndAllSettleBeforeEvidenceQueries(bool approval, bool failBranch)
    {
        using var fixture = new VerifierFixture();
        if (approval)
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = false;
            fixture.SetMergeEvidence();
        }
        var started = 0;
        var expected = approval ? 6 : 5;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downstream = false;
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.FileName == "git" && request.WorkingDirectory == fixture.Worktree
                && request.Arguments[0] is "branch" or "rev-parse" or "status" or "ls-files")
            {
                if (Interlocked.Increment(ref started) == expected) ready.TrySetResult();
                if (failBranch && request.Arguments[0] == "branch")
                    throw new IOException("controlled branch read failure");
                await release.Task.WaitAsync(token);
            }
            if (request.FileName == "gh") downstream = true;
            return await fixture.Runner.RunAsync(request, token);
        });
        var verification = approval
            ? CheckApprovalAsync()
            : CheckRejectionAsync();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(verification.IsCompleted);
            Assert.False(downstream);
            release.TrySetResult();
            Assert.Equal(!failBranch, await verification);
            if (failBranch) Assert.False(downstream);
        }
        finally
        {
            release.TrySetResult();
            await verification;
        }

        async Task<bool> CheckApprovalAsync() => (await fixture.VerifyApprovalAsync(runner)).Succeeded;
        async Task<bool> CheckRejectionAsync() => (await fixture.VerifyAsync(runner)).Succeeded;
    }

    [Fact]
    public void PullRequestQueryUsesFieldsSupportedByTheInstalledGhCli()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh", UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("pr");
        startInfo.ArgumentList.Add("view");
        startInfo.ArgumentList.Add("--help");
        // This is an offline CLI-contract check. Do not let a first-use update
        // notification add a network round trip to the complete-suite budget.
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        using var process = FixtureProcess.Start(startInfo);
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        var help = output + error;
        Assert.Contains("JSON FIELDS", help, StringComparison.Ordinal);
        Assert.Contains("--repo [HOST/]OWNER/REPO", help, StringComparison.Ordinal);
        foreach (var field in new[] { "state", "baseRefName", "headRefName", "headRefOid", "url", "mergedAt", "mergeCommit" })
            Assert.Contains(field, help, StringComparison.Ordinal);
        Assert.DoesNotContain("baseRepository", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsOnlyANewStructuredFindingConfirmedByTheCurrentRequiredCheck()
    {
        using var fixture = new VerifierFixture();

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("fixture checks", result.Finding?.Subject);
        Assert.Equal(fixture.Head, result.Finding?.CheckedHead);
        Assert.Equal(2, fixture.Runner.PullRequestViewCount);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
        Assert.Equal(0, fixture.Runner.NativeGitCommandCount);
    }

    [Fact]
    public async Task ScriptedGitFixtureNeverFallsThroughForAnUnknownCommand()
    {
        using var fixture = new VerifierFixture();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Runner.RunAsync(GitCommand.Create(["status", "--ignored"], fixture.Worktree),
                CancellationToken.None));

        Assert.Contains("Unexpected scripted Git command", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Runner.NativeGitCommandCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptsPinnedFailedReadinessJobAfterDraftConversionMasksTheRequiredCheck(bool masked)
    {
        using var fixture = new VerifierFixture();
        fixture.PinRemoteFinding();
        fixture.Runner.CheckSkipped = masked;
        fixture.Runner.RequiredLinkIsPinned = !masked;

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(2, fixture.Runner.PullRequestViewCount);
    }

    [Fact]
    public async Task PinnedFailureWithSkippedRollupReachesOneGuardedRepairHandoff()
    {
        using var fixture = new VerifierFixture();
        fixture.PinRemoteFinding();
        fixture.Runner.CheckSkipped = true;
        fixture.Runner.Status = "open";
        fixture.Runner.Assignee = null;
        var result = await fixture.RunSlotAsync();
        Assert.True(result.RepairHandoffCompleted, result.Diagnostic);
        Assert.Equal("stage:implement", fixture.Runner.Stage);
        Assert.Equal(1, fixture.Runner.RepairCycles);
        Assert.Null(fixture.Runner.Assignee);
        var write = Assert.Single(fixture.Runner.PublicationWrites);
        Assert.Contains("--if-status", write);
        Assert.Contains("--if-assignee", write);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("repository")]
    [InlineData("head-repository")]
    [InlineData("branch")]
    [InlineData("pr")]
    [InlineData("event")]
    [InlineData("attempt")]
    [InlineData("job")]
    [InlineData("job-head")]
    [InlineData("job-name")]
    [InlineData("workflow")]
    [InlineData("skipped")]
    [InlineData("passed")]
    [InlineData("cancelled")]
    [InlineData("pending")]
    [InlineData("link")]
    [InlineData("old-run")]
    [InlineData("rerun")]
    [InlineData("newer-passed")]
    [InlineData("hidden-passed")]
    [InlineData("history-overflow")]
    [InlineData("jobs-overflow")]
    public async Task RejectsUnrelatedOrNonFailingPinnedHostedEvidence(string mutation)
    {
        using var fixture = new VerifierFixture();
        fixture.PinRemoteFinding();
        fixture.Runner.CheckSkipped = true;
        fixture.Runner.HostedMutation = mutation;

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Runner.PublicationWrites);
    }

    [Fact]
    public async Task RejectsChangedHeadAfterPinnedFailureVerification()
    {
        using var fixture = new VerifierFixture();
        fixture.PinRemoteFinding();
        fixture.Runner.CheckSkipped = true;
        fixture.Runner.HeadAfterChecks = new string('d', 40);
        var result = await fixture.VerifyAsync();
        Assert.False(result.Succeeded);
        Assert.Contains("changed while required checks", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ReadsRequiredCheckJsonDespiteGhCheckStatusExitCode(int exitCode)
    {
        using var fixture = new VerifierFixture();
        fixture.PinRemoteFinding();
        fixture.Runner.CheckSkipped = true;
        fixture.Runner.RequiredChecksExitCode = exitCode;
        var result = await fixture.VerifyAsync();
        Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Fact]
    public async Task AcceptsLocalBuildFailureWithoutAFailedRemoteCheck()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.CheckFailed = false;
        fixture.SetLocalCheckFinding("dotnet build", 1);

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("local", result.Finding?.CheckSource);
        Assert.Equal(1, result.Finding?.LocalExitCode);
        Assert.Equal(1, fixture.Runner.PullRequestViewCount);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task AcceptsLocalTestFailureWithACompletedNonzeroTranscript()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 1);

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("dotnet test", result.Finding?.Subject);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task AcceptsCompleteLocalEvidenceAfterLongUnrelatedPrefix()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        File.WriteAllText(fixture.Invocation.LogFilePath,
            new string('x', 400_000) + "\n" + File.ReadAllText(fixture.Invocation.LogFilePath));
        var result = await fixture.VerifyAsync();
        Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Theory]
    [InlineData("total: -1")]
    [InlineData("total: unknown")]
    [InlineData("failed: -1")]
    [InlineData("failed: unknown")]
    [InlineData("succeeded: -1")]
    [InlineData("succeeded: unknown")]
    [InlineData("skipped: -1")]
    [InlineData("skipped: unknown")]
    [InlineData("succeeded: 0\nsucceeded: -1")]
    [InlineData("succeeded: 0\nsucceeded: unknown")]
    [InlineData("skipped: 0\nskipped: -1")]
    [InlineData("skipped: 0\nskipped: unknown")]
    [InlineData("succeeded:")]
    [InlineData("succeeded: 2147483648")]
    [InlineData("succeeded: +1")]
    [InlineData("succeeded: 0 extra")]
    public async Task RejectsMalformedSuppliedTestCountsWithoutRepairPublication(string counts)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2, trailingOutput: counts);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("failure summary", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal("stage:merge", fixture.Runner.Stage);
        Assert.Equal(0, fixture.Runner.RepairCycles);
        Assert.Empty(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData("")]
    [InlineData("succeeded: 0\nskipped: 0")]
    public async Task AcceptsMinimalAndConsistentFullTestCounts(string counts)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2,
            trailingOutput: counts + "\nDuration: 1 second\nUnrelated diagnostic output");

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Fact]
    public async Task ShippedLocalTestExampleReachesGuardedRepairHandoffAfterLongOutput()
    {
        using var fixture = new VerifierFixture();
        fixture.SetPromptExample();
        fixture.Runner.Status = "open";
        fixture.Runner.Assignee = null;
        var result = await fixture.RunSlotAsync();
        Assert.True(result.RepairHandoffCompleted, result.Diagnostic);
        Assert.Equal("stage:implement", fixture.Runner.Stage);
        Assert.Equal("open", fixture.Runner.Status);
        Assert.Null(fixture.Runner.Assignee);
        Assert.Equal(1, fixture.Runner.RepairCycles);
        var args = Assert.Single(fixture.Runner.PublicationWrites);
        Assert.Contains("--if-status", args);
        Assert.Contains("--if-assignee", args);
        Assert.Contains(VerifierFixture.Owner, args);
        Assert.Contains("repair_cycles=1", args);
        Assert.Contains("keep:label", fixture.Runner.Labels);
    }

    [Fact]
    public async Task FullInvocationWithPromptAndDocumentationEchoesReachesGuardedRepairHandoff()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        var frame = File.ReadAllText(fixture.Invocation.LogFilePath);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Nightweaver.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var prompt = File.ReadAllText(Path.Combine(root.FullName, "prompts", "merger.md"));
        var specification = File.ReadAllText(Path.Combine(root.FullName, "SPEC.md"));
        File.WriteAllText(fixture.Invocation.LogFilePath,
            "OpenAI Codex\nuser\n" + prompt +
            "\nexec\npwsh -Command Get-Content SPEC.md\nsucceeded in 10ms:\n" +
            specification + "\nDocumentation mentions NIGHTWEAVER_LOCAL_CHECK_V1 and END_NIGHTWEAVER_LOCAL_CHECK_V1.\n" +
            "exec\npwsh -Command \"Write-Output (\\\"NIGHTWEAVER_LOCAL_CHECK_V2 \\\" + $env:NIGHTWEAVER_INVOCATION_ID)\"\nsucceeded in 10ms:\n" + frame +
            "codex\nRecorded the settled local test failure in the Bead comment.\n");
        fixture.Runner.Status = "open";
        fixture.Runner.Assignee = null;
        var result = await fixture.RunSlotAsync();
        Assert.True(result.RepairHandoffCompleted, result.Diagnostic);
        Assert.Equal("stage:implement", fixture.Runner.Stage);
        Assert.Equal(1, fixture.Runner.RepairCycles);
        Assert.Null(fixture.Runner.Assignee);
        Assert.Single(fixture.Runner.PublicationWrites);
    }

    [Fact]
    public async Task PromptExampleWithoutActualCurrentInvocationEvidenceCannotReject()
    {
        using var fixture = new VerifierFixture();
        fixture.SetPromptExample();
        var frame = File.ReadAllText(fixture.Invocation.LogFilePath);
        File.WriteAllText(fixture.Invocation.LogFilePath,
            frame.Replace(fixture.Invocation.InvocationId, "20000101T0000000000000Z-merger", StringComparison.Ordinal));
        var result = await fixture.VerifyAsync();
        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Runner.PublicationWrites);
        Assert.Equal(0, fixture.Runner.RepairCycles);
    }

    [Theory]
    [InlineData("dotnet test", "Test run summary: Failed!\ntotal: 1\nfailed: 1", "Test run summary: Passed!\ntotal: 1\nfailed: 0")]
    [InlineData("dotnet build", "Build FAILED.\n1 Error(s)", "Build succeeded.\n0 Error(s)")]
    [InlineData("dotnet test", "Test run summary: Failed!\ntotal: 1\nfailed: 1", "  test RUN summary: PASSED!\r\ntotal: 1\nfailed: 0\nNIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger {}\nTest run summary: Failed!\nEND_NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger")]
    public async Task PassingRerunBeforeStaleFrameCannotAuthorizeRejection(string command, string failed, string passed)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding(command, 2);
        var frame = File.ReadAllText(fixture.Invocation.LogFilePath);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Nightweaver.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        File.WriteAllText(fixture.Invocation.LogFilePath,
            "OpenAI Codex\nuser\n" + File.ReadAllText(Path.Combine(root.FullName, "prompts", "merger.md")) +
            "\nexec\npwsh -Command Get-Content SPEC.md\nsucceeded in 10ms:\n" +
            File.ReadAllText(Path.Combine(root.FullName, "SPEC.md")) +
            $"\nexec\npwsh -Command git rev-parse HEAD; {command}\nexited 2 in 10ms:\n{fixture.Head}\n{failed}\n" +
            $"exec\npwsh -Command git rev-parse HEAD; {command}\nsucceeded in 10ms:\n{fixture.Head}\n{passed}\n" +
            "exec\npwsh -Command Write-Output $frame\nsucceeded in 10ms:\n" + frame);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("passing result", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("stage:merge", fixture.Runner.Stage);
        Assert.Equal(0, fixture.Runner.RepairCycles);
        Assert.Equal(VerifierFixture.Owner, fixture.Runner.Assignee);
        Assert.Empty(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("dotnet test", false)]
    [InlineData("dotnet build", false)]
    public async Task EchoedFailureCannotErasePassingRerun(string command, bool legacyPrompt)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding(command, 2);
        var frame = File.ReadAllText(fixture.Invocation.LogFilePath);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Nightweaver.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        // Snapshot of main's shipped prompt at 8fd3402: preserve its real V1 example
        // without making this regression depend on a mutable Git ref at test runtime.
        var echo = legacyPrompt
            ? File.ReadAllText(Path.Combine(root.FullName, "tests", "Nightweaver.Tests", "Fixtures", "merger-v1-prompt.txt"))
            : "Build FAILED.\n1 Error(s)\nTest run summary: Failed!\ntotal: 1\nfailed: 1\n";
        var passed = command == "dotnet test" ? "Test run summary: Passed!\ntotal: 1\nfailed: 0" : "Build succeeded.\n0 Error(s)";
        File.WriteAllText(fixture.Invocation.LogFilePath,
            "OpenAI Codex\nuser\n" + File.ReadAllText(Path.Combine(root.FullName, "prompts", "merger.md")) +
            $"\nexec\npwsh -Command git rev-parse HEAD; {command}\nexited 2 in 10ms:\n{fixture.Head}\n" +
            "Build FAILED.\n1 Error(s)\nTest run summary: Failed!\ntotal: 1\nfailed: 1\n" +
            $"exec\npwsh -Command git rev-parse HEAD; {command}\nsucceeded in 10ms:\n{fixture.Head}\n{passed}\n" +
            "exec\npwsh -Command git show origin/main:prompts/merger.md\nsucceeded in 10ms:\n" + echo +
            "\nexec\npwsh -Command Write-Output $frame\nsucceeded in 10ms:\n" + frame);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("stage:merge", fixture.Runner.Stage);
        Assert.Equal(0, fixture.Runner.RepairCycles);
        Assert.Equal(VerifierFixture.Owner, fixture.Runner.Assignee);
        Assert.Empty(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData("dotnet test", "Build succeeded.\n0 Error(s)\n")]
    [InlineData("dotnet test", "Documentation: a Test run summary: Passed! supersedes failure.\n")]
    [InlineData("dotnet test", "NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger {}\nTest run summary: Passed!\nEND_NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger\n")]
    public async Task PrefixWithoutSupersedingSameCommandPassStillAcceptsFailure(string command, string prefix)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding(command, 2);
        File.WriteAllText(fixture.Invocation.LogFilePath, prefix + File.ReadAllText(fixture.Invocation.LogFilePath));

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet build")]
    public async Task AttributableFailedRerunAfterPassReachesGuardedRepairHandoff(string command)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding(command, 2);
        var frame = File.ReadAllText(fixture.Invocation.LogFilePath);
        var rerun = frame.Replace("NIGHTWEAVER_LOCAL_CHECK_V2", "NIGHTWEAVER_LOCAL_RERUN_V1", StringComparison.Ordinal);
        var passed = command == "dotnet test" ? "Test run summary: Passed!\ntotal: 1\nfailed: 0" : "Build succeeded.\n0 Error(s)";
        File.WriteAllText(fixture.Invocation.LogFilePath,
            $"OpenAI Codex\nexec\npwsh -Command git rev-parse HEAD; {command}\nsucceeded in 10ms:\n{fixture.Head}\n{passed}\n" +
            $"exec\npwsh -Command git rev-parse HEAD; {command}; Write-Output $rerun\nexited 2 in 10ms:\n{fixture.Head}\n" +
            rerun + "exec\npwsh -Command Write-Output $frame\nsucceeded in 10ms:\n" + frame);
        fixture.Runner.Status = "open";
        fixture.Runner.Assignee = null;

        var result = await fixture.RunSlotAsync();

        Assert.True(result.RepairHandoffCompleted, result.Diagnostic);
        Assert.Equal("stage:implement", fixture.Runner.Stage);
        Assert.Equal(1, fixture.Runner.RepairCycles);
        Assert.Null(fixture.Runner.Assignee);
        Assert.Single(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("head")]
    [InlineData("pr")]
    [InlineData("command")]
    [InlineData("exit")]
    [InlineData("counts")]
    [InlineData("summary")]
    [InlineData("details")]
    [InlineData("duplicate")]
    [InlineData("quoted")]
    [InlineData("incomplete")]
    [InlineData("oversized")]
    [InlineData("utf8")]
    [InlineData("later-pass")]
    [InlineData("after-frame")]
    [InlineData("foreign")]
    public async Task UnverifiableRerunCannotClearAnObservedPass(string mutation)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        var frame = File.ReadAllText(fixture.Invocation.LogFilePath);
        var rerun = frame.Replace("NIGHTWEAVER_LOCAL_CHECK_V2", "NIGHTWEAVER_LOCAL_RERUN_V1", StringComparison.Ordinal);
        rerun = mutation switch
        {
            "absent" => "Test run summary: Failed!\ntotal: 1\nfailed: 1\n",
            "head" => rerun.Replace(fixture.Head, new string('f', 40), StringComparison.Ordinal),
            "pr" => rerun.Replace(fixture.PullRequest, "https://github.com/example/project/pull/999", StringComparison.Ordinal),
            "command" => rerun.Replace("dotnet test", "dotnet build", StringComparison.Ordinal),
            "exit" => rerun.Replace("\"exit_code\":2", "\"exit_code\":0", StringComparison.Ordinal),
            "counts" => rerun.Replace("failed: 1", "failed: 0", StringComparison.Ordinal),
            "summary" => rerun.Replace("Local dotnet test failed", "Other summary", StringComparison.Ordinal),
            "details" => rerun.Replace("RejectsInvalidInput", "OtherTest", StringComparison.Ordinal),
            "duplicate" => rerun + rerun,
            "quoted" => "> " + rerun,
            "incomplete" => rerun[..rerun.IndexOf("END_NIGHTWEAVER", StringComparison.Ordinal)],
            "oversized" => rerun.Replace("total:", new string('x', 33000) + "\ntotal:", StringComparison.Ordinal),
            "foreign" => rerun.Replace(fixture.Invocation.InvocationId, "20000101T0000000000000Z-merger", StringComparison.Ordinal),
            _ => rerun
        };
        const string pass = "Test run summary: Passed!\ntotal: 1\nfailed: 0\n";
        var transcript = pass + rerun + (mutation == "later-pass" ? pass : "") + frame;
        if (mutation == "after-frame") transcript = pass + frame + rerun;
        var bytes = System.Text.Encoding.UTF8.GetBytes(transcript);
        if (mutation == "utf8") bytes[Array.IndexOf(bytes, (byte)'[')] = 0xff;
        File.WriteAllBytes(fixture.Invocation.LogFilePath, bytes);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("stage:merge", fixture.Runner.Stage);
        Assert.Equal(0, fixture.Runner.RepairCycles);
        Assert.Equal(VerifierFixture.Owner, fixture.Runner.Assignee);
        Assert.Empty(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData(3780)]
    [InlineData(4095)]
    public async Task PaddedPassingPrefixAcrossScanChunksCannotAuthorizeRejection(int padding)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        File.WriteAllText(fixture.Invocation.LogFilePath,
            new string('x', padding) + "\n" + new string(' ', 300) + "Test run summary: Passed!" +
            new string(' ', 300) + "\r\n" + File.ReadAllText(fixture.Invocation.LogFilePath));

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Runner.PublicationWrites);
        Assert.Equal(0, fixture.Runner.RepairCycles);
    }

    [Fact]
    public async Task UnclosedHistoricalFrameCannotHidePassingRerun()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        File.WriteAllText(fixture.Invocation.LogFilePath,
            "NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger {}\nTest run summary: Passed!\n" +
            File.ReadAllText(fixture.Invocation.LogFilePath));

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Runner.PublicationWrites);
        Assert.Equal(0, fixture.Runner.RepairCycles);
    }

    [Theory]
    [InlineData("Build succeeded.")]
    [InlineData("Test run summary: Passed!")]
    [InlineData("  build SUCCEEDED.\r\n")]
    [InlineData("test RUN summary: PASSED!")]
    public async Task LaterPassingToolOutputOutsideFrameCannotAuthorizeRejection(string passed)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        File.AppendAllText(fixture.Invocation.LogFilePath,
            "exec\npwsh -Command dotnet test\nsucceeded in 10ms:\n" + passed + "\n");
        var result = await fixture.VerifyAsync();
        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData(4075, false)]
    [InlineData(4095, true)]
    [InlineData(32767, false)]
    public async Task AcceptsFrameAcrossScanChunksAndUtf8PrefixBoundaries(int prefixBytes, bool crlf)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        var frame = File.ReadAllText(fixture.Invocation.LogFilePath);
        if (crlf) frame = frame.Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(fixture.Invocation.LogFilePath,
            new string('x', prefixBytes) + "\U0001f600\n" + frame + new string('z', 40000));
        var result = await fixture.VerifyAsync();
        Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Theory]
    [InlineData("cut-start", "complete local check transcript")]
    [InlineData("cut-end", "complete local check transcript")]
    [InlineData("oversized-frame", "32 KiB")]
    [InlineData("oversized-log", "8 MiB")]
    [InlineData("duplicate", "ambiguous")]
    [InlineData("quoted", "unquoted lines")]
    [InlineData("quoted-end", "complete local check transcript")]
    [InlineData("malformed", "metadata is malformed")]
    [InlineData("duplicate-field", "allowlisted failing command")]
    [InlineData("invocation", "complete local check transcript")]
    [InlineData("pr", "allowlisted failing command")]
    [InlineData("head", "retained HEAD")]
    [InlineData("command", "allowlisted failing command")]
    [InlineData("summary", "summary and diagnostics")]
    [InlineData("details", "summary and diagnostics")]
    [InlineData("missing-total", "failure summary")]
    [InlineData("contradictory-total", "failure summary")]
    [InlineData("passed", "failure summary")]
    public async Task RejectsIncompleteAmbiguousOrMismatchedLocalEvidence(string mutation, string diagnostic)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        var text = File.ReadAllText(fixture.Invocation.LogFilePath);
        text = mutation switch
        {
            "cut-start" => text[(text.IndexOf('\n') + 1)..],
            "cut-end" => text[..text.IndexOf("END_NIGHTWEAVER", StringComparison.Ordinal)],
            "oversized-frame" => text.Replace("total: 1", new string('x', 32768) + "\ntotal: 1", StringComparison.Ordinal),
            "oversized-log" => new string('x', MergerLocalCheckReader.MaximumLogBytes) + "\n" + text,
            "duplicate" => text + new string('x', 40000) + "\n" + text,
            "quoted" => "> " + text,
            "quoted-end" => text.Replace("END_NIGHTWEAVER", "> END_NIGHTWEAVER", StringComparison.Ordinal),
            "malformed" => text.Replace("{", "{broken", StringComparison.Ordinal),
            "duplicate-field" => text.Replace("{", "{\"command\":\"dotnet test\",", StringComparison.Ordinal),
            "invocation" => text.Replace("20261104T1200000000000Z-merger", "20261103T1200000000000Z-merger", StringComparison.Ordinal),
            "pr" => text.Replace(fixture.PullRequest, "https://github.com/example/project/pull/999", StringComparison.Ordinal),
            "head" => text.Replace(fixture.Head, new string('f', 40), StringComparison.Ordinal),
            "command" => text.Replace("\"command\":\"dotnet test\"", "\"command\":\"dotnet run\"", StringComparison.Ordinal),
            "summary" => text.Replace("Local dotnet test failed for the checked HEAD.", "Other summary", StringComparison.Ordinal),
            "details" => text.Replace("RejectsInvalidInput", "OtherTest", StringComparison.Ordinal),
            "missing-total" => text.Replace("total: 1\n", "", StringComparison.Ordinal),
            "contradictory-total" => text.Replace("total: 1", "total: 0", StringComparison.Ordinal),
            "passed" => text.Replace("END_NIGHTWEAVER", "Test run summary: Passed!\nEND_NIGHTWEAVER", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(mutation)
        };
        File.WriteAllText(fixture.Invocation.LogFilePath, new string('x', 40000) + "\n" + text);
        var result = await fixture.VerifyAsync();
        Assert.False(result.Succeeded);
        Assert.Contains(diagnostic, result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal("stage:merge", fixture.Runner.Stage);
        Assert.Equal(0, fixture.Runner.RepairCycles);
        Assert.Empty(fixture.Runner.PublicationWrites);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidatesUtf8OnlyWithinConsumedFrame(bool corruptEvidence)
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet test", 2);
        var frame = File.ReadAllBytes(fixture.Invocation.LogFilePath);
        if (corruptEvidence) frame[Array.IndexOf(frame, (byte)'[')] = 0xff;
        File.WriteAllBytes(fixture.Invocation.LogFilePath,
            new byte[] { 0xff, 0xf0, 0x9f, 0x98, 0x80, 10 }.Concat(frame).ToArray());
        var result = await fixture.VerifyAsync();
        Assert.Equal(!corruptEvidence, result.Succeeded);
        if (corruptEvidence) Assert.Contains("DecoderFallbackException", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsLocalCheckMarkerWithoutFramedNonzeroTranscript()
    {
        using var fixture = new VerifierFixture();
        const string details = "error CS1002: expected ';' in Fixture.cs";
        const string summary = "Local build failed for the checked HEAD.";
        fixture.SetFinding("checks", "dotnet build", summary, details, "local", 1,
            $"A prior report mentioned Build FAILED. and 1 Error(s). {summary} {details}");

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("complete local check transcript", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task RejectsLocalTranscriptThatContradictsStructuredExitCode()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet build", 1, transcriptExitCode: 0);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("nonzero exit", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsLocalTranscriptWithLaterSuccessfulTerminalResult()
    {
        using var fixture = new VerifierFixture();
        fixture.SetLocalCheckFinding("dotnet build", 1, trailingOutput: "Build succeeded.");

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("no unambiguous nonzero build/test failure summary", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/Nightweaver.cs")]
    public async Task AcceptsRootLevelAndNestedConflictPaths(string subject)
    {
        using var fixture = new VerifierFixture();
        fixture.SetFinding("conflict", subject, "A repository conflict requires implementation repair.",
            $"CONFLICT: both branches changed {subject} and the rebase was aborted.",
            transcript: $"CONFLICT: both branches changed {subject} and the rebase was aborted.");

        var result = await fixture.VerifyAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Theory]
    [InlineData("../README.md")]
    [InlineData("src/../README.md")]
    [InlineData("/README.md")]
    [InlineData("C:\\README.md")]
    public async Task RejectsAbsoluteOrTraversalConflictPaths(string subject)
    {
        using var fixture = new VerifierFixture();
        fixture.SetFinding("conflict", subject, "A repository conflict requires implementation repair.",
            $"CONFLICT: both branches changed {subject} and the rebase was aborted.",
            transcript: $"CONFLICT: both branches changed {subject} and the rebase was aborted.");

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No structured actionable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsRemoteCheckFailureWhenPullRequestHeadChangesDuringObservation()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.HeadAfterChecks = new string('d', 40);

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("identity or head changed", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Runner.PullRequestViewCount);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task RejectsAValidFindingThatPredatesTheCurrentInvocation()
    {
        using var fixture = new VerifierFixture();
        fixture.Invocation = fixture.Invocation with
        {
            PreInvocationCommentIds = new HashSet<string>(["prior", "current"], StringComparer.Ordinal)
        };

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("from this invocation", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMalformedCurrentFinding()
    {
        using var fixture = new VerifierFixture();
        fixture.Comments = [new("current", "{ malformed", DateTimeOffset.UtcNow)];

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No structured actionable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("workflow")]
    public async Task RejectsChangedBeadOwnerOrStage(string change)
    {
        using var fixture = new VerifierFixture();
        if (change == "owner") fixture.Runner.Assignee = "human:receiver";
        else fixture.Runner.Stage = "stage:review";

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("current Bead state or owner", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAReportedCheckThatPassedForTheCurrentHead()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.CheckFailed = false;

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No required check", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsPullRequestHeadMismatchAndActiveRebase()
    {
        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.AdvertisedHead = new string('d', 40);
            var result = await fixture.VerifyAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("open retained branch and current checked HEAD", result.Diagnostic, StringComparison.Ordinal);
        }

        using (var fixture = new VerifierFixture(nativeGit: true))
        {
            Directory.CreateDirectory(Path.Combine(fixture.Worktree, ".git", "rebase-merge"));
            var result = await fixture.VerifyAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("active rebase", result.Diagnostic, StringComparison.Ordinal);
            Assert.True(fixture.Runner.NativeGitCommandCount > 0);
        }
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("head")]
    public async Task RejectsChangedNativeWorktreeBranchOrHead(string change)
    {
        using var fixture = new VerifierFixture(nativeGit: true);
        if (change == "branch")
            VerifierFixture.RunGit(fixture.Worktree, "checkout", "-b", "nightweaver/other");
        else
        {
            File.WriteAllText(Path.Combine(fixture.Worktree, "README.md"), "changed\n");
            VerifierFixture.RunGit(fixture.Worktree, "add", "README.md");
            VerifierFixture.RunGit(fixture.Worktree, "commit", "-m", "changed head");
        }

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("retained Merger branch or HEAD changed", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(fixture.Runner.NativeGitCommandCount > 0);
    }

    [Fact]
    public async Task RejectsNativeUnmergedIndexBeforeRejectionHandoff()
    {
        using var fixture = new VerifierFixture(nativeGit: true);
        var baseBlob = VerifierFixture.RunGit(fixture.Worktree, "rev-parse", "HEAD:README.md");
        var oursPath = Path.Combine(fixture.Worktree, "ours.txt");
        var theirsPath = Path.Combine(fixture.Worktree, "theirs.txt");
        File.WriteAllText(oursPath, "ours\n");
        File.WriteAllText(theirsPath, "theirs\n");
        var oursBlob = VerifierFixture.RunGit(fixture.Worktree, "hash-object", "-w", oursPath);
        var theirsBlob = VerifierFixture.RunGit(fixture.Worktree, "hash-object", "-w", theirsPath);
        var indexInfo = $"100644 {baseBlob} 1\tREADME.md\n100644 {oursBlob} 2\tREADME.md\n100644 {theirsBlob} 3\tREADME.md\n";
        VerifierFixture.RunGitWithInput(fixture.Worktree, indexInfo, "update-index", "--index-info");
        Assert.NotEmpty(VerifierFixture.RunGit(fixture.Worktree, "ls-files", "-u"));

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("unresolved index entries", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(fixture.Runner.NativeGitCommandCount > 0);
    }

    [Fact]
    public async Task RejectsObservationForDifferentRetainedBranch()
    {
        using var fixture = new VerifierFixture();
        fixture.Invocation = fixture.Invocation with
        {
            Observations = fixture.Invocation.Observations! with { BranchName = "nightweaver/other" }
        };

        var result = await fixture.VerifyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("retained branch", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptsApprovalOnlyAfterCurrentMergeEvidencePassingChecksAndDefaultBranchReachability()
    {
        using var fixture = new VerifierFixture(nativeGit: true);
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.SetMergeEvidence(mergeCommit: fixture.ReachableMergeCommit);

        var result = await fixture.VerifyApprovalAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(fixture.PullRequest, result.Evidence?.PullRequest);
        Assert.Equal(fixture.Head, result.Evidence?.CheckedHead);
        Assert.Equal(fixture.ReachableMergeCommit, result.Evidence?.MergeCommit);
        Assert.Equal("current", result.Evidence?.SourceCommentId);
        Assert.Equal(2, fixture.Runner.PullRequestViewCount);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
        Assert.True(fixture.Runner.FetchedDefaultBranch);
        Assert.True(fixture.Runner.NativeGitCommandCount > 0);
        Assert.Equal("github.com/example/project", fixture.Runner.PullRequestRepository);
        Assert.Equal("github.com/example/project", fixture.Runner.ChecksRepository);
    }

    [Fact]
    public async Task RejectsApprovalWhenReturnedPRUrlIdentifiesAForeignRepository()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.Runner.ReportedPullRequestUrl = "https://github.com/attacker/other/pull/123";
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("does not match", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Runner.PullRequestViewCount);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
        Assert.Equal("github.com/example/project", fixture.Runner.PullRequestRepository);
    }

    [Fact]
    public async Task AcceptsReturnedPRUrlForTheCapturedSameRepository()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("https://github.com/example/project/pull/123", result.Evidence?.PullRequest);
        Assert.Equal("github.com/example/project", fixture.Runner.PullRequestRepository);
    }

    [Fact]
    public async Task RejectsEvidenceForAForeignRepositoryPRUrlBeforeQueryingGitHub()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.SetMergeEvidence(pullRequest: "https://github.com/attacker/other/pull/123");

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("does not identify the validated Target Repository", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Runner.PullRequestViewCount);
        Assert.Equal(0, fixture.Runner.RequiredChecksCount);
    }

    [Fact]
    public async Task RejectsNativeDirtyWorktreeBeforeApprovingMerge()
    {
        using var fixture = new VerifierFixture(nativeGit: true);
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.SetMergeEvidence(mergeCommit: fixture.ReachableMergeCommit);
        File.AppendAllText(Path.Combine(fixture.Worktree, "README.md"), "local modification\n");

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("branch, HEAD, index, or worktree changed", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(fixture.Runner.NativeGitCommandCount > 0);
        Assert.False(fixture.Runner.FetchedDefaultBranch);
    }

    [Fact]
    public async Task RejectsTargetOriginChangedDuringRequiredCheckVerification()
    {
        using var fixture = new VerifierFixture(nativeGit: true);
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.Runner.OriginUrlAfterChecks = "https://github.com/attacker/other.git";
        fixture.SetMergeEvidence();

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("origin changed while PR checks", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Runner.RequiredChecksCount);
        Assert.False(fixture.Runner.FetchedDefaultBranch);
        Assert.True(fixture.Runner.NativeGitCommandCount > 0);
    }

    [Fact]
    public async Task RejectsApprovalFromHistoricalEvidenceOrChangedClaim()
    {
        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.SetMergeEvidence();
            fixture.Invocation = fixture.Invocation with
            {
                PreInvocationCommentIds = new HashSet<string>(["prior", "current"], StringComparer.Ordinal)
            };
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("current invocation", result.Diagnostic, StringComparison.Ordinal);
            Assert.Equal(0, fixture.Runner.PullRequestViewCount);
        }

        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = false;
            fixture.Runner.Assignee = "human:receiver";
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("state or claim owner", result.Diagnostic, StringComparison.Ordinal);
            Assert.Equal(0, fixture.Runner.PullRequestViewCount);
        }
    }

    [Fact]
    public async Task RejectsApprovalWhenPullRequestOrCurrentHeadChecksAreNotVerified()
    {
        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = false;
            fixture.Runner.BaseBranch = "release";
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("identity, configured base", result.Diagnostic, StringComparison.Ordinal);
        }

        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = false;
            fixture.Runner.HeadAfterChecks = new string('d', 40);
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("changed while required checks", result.Diagnostic, StringComparison.Ordinal);
        }

        using (var fixture = new VerifierFixture())
        {
            fixture.Runner.ApprovalMode = true;
            fixture.Runner.CheckFailed = true;
            fixture.SetMergeEvidence();
            var result = await fixture.VerifyApprovalAsync();
            Assert.False(result.Succeeded);
            Assert.Contains("Required pull request checks", result.Diagnostic, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DraftSkippedChecksCannotAuthorizeMerge()
    {
        using var fixture = new VerifierFixture();
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.Runner.CheckSkipped = true;
        fixture.SetMergeEvidence();
        var result = await fixture.VerifyApprovalAsync();
        Assert.False(result.Succeeded);
        Assert.Contains("Required pull request checks", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMergeCommitThatIsNotReachableFromConfiguredDefaultBranch()
    {
        using var fixture = new VerifierFixture(nativeGit: true);
        fixture.Runner.ApprovalMode = true;
        fixture.Runner.CheckFailed = false;
        fixture.SetMergeEvidence(mergeCommit: fixture.UnreachableMergeCommit);

        var result = await fixture.VerifyApprovalAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("not reachable", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(fixture.Runner.NativeGitCommandCount > 0);
    }

    private sealed class VerifierFixture : IDisposable
    {
        internal const string BeadId = "ns-ver.1";
        internal const string Owner = "nightweaver:demo";
        internal const string OriginUrl = "https://github.com/example/project.git";
        private static readonly Lazy<(TempDirectory Directory, string Head, string ReachableMergeCommit, string UnreachableMergeCommit)> Template = new(() =>
        {
            var directory = new TempDirectory();
            RunGit(directory.Path, "init", "--initial-branch=main");
            RunGit(directory.Path, "config", "user.name", "Verifier Fixture");
            RunGit(directory.Path, "config", "user.email", "verifier@example.invalid");
            File.WriteAllText(Path.Combine(directory.Path, "README.md"), "fixture\n");
            RunGit(directory.Path, "add", "README.md");
            RunGit(directory.Path, "commit", "-m", "fixture base");
            var head = RunGit(directory.Path, "rev-parse", "HEAD");
            RunGit(directory.Path, "checkout", "-b", $"nightweaver/{BeadId}");
            RunGit(directory.Path, "checkout", "main");
            File.WriteAllText(Path.Combine(directory.Path, "README.md"), "reachable default branch\n");
            RunGit(directory.Path, "add", "README.md");
            RunGit(directory.Path, "commit", "-m", "reachable merged result");
            var reachableMergeCommit = RunGit(directory.Path, "rev-parse", "HEAD");
            RunGit(directory.Path, "checkout", "-b", "fixture/unreachable", head);
            File.WriteAllText(Path.Combine(directory.Path, "unreachable.txt"), "not on main\n");
            RunGit(directory.Path, "add", "unreachable.txt");
            RunGit(directory.Path, "commit", "-m", "unreachable merge result");
            var unreachableMergeCommit = RunGit(directory.Path, "rev-parse", "HEAD");
            RunGit(directory.Path, "checkout", $"nightweaver/{BeadId}");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => directory.Dispose();
            return (directory, head, reachableMergeCommit, unreachableMergeCommit);
        });
        private readonly TempDirectory temp = new();
        private readonly ProjectConfig project;
        private readonly ValidatedTargetRepository repository;
        private readonly AgentStageSession session;
        private readonly AgentOutcomeClassificationResult classification =
            new(AgentOutcomeChoice.ChangesRequired, "fixture", "The current required check failed.");
        private readonly string invocationId = "20261104T1200000000000Z-merger";

        internal VerifierFixture(bool nativeGit = false)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Settled Merger log verification requires Windows file handles.");
            NativeGit = nativeGit;
            var host = new HostPaths(temp.CreateDirectory("host"));
            var worktrees = host.WorktreesRoot(ProjectSlug.Parse("demo"));
            Worktree = Directory.CreateDirectory(Path.Combine(worktrees, BeadId)).FullName;
            if (NativeGit)
            {
                // Native cases share only a completed immutable template. Every
                // mutable .git database is copied into this fixture's worktree.
                var template = Template.Value;
                TestDirectoryTemplate.Copy(template.Directory.Path, Worktree);
                Head = template.Head;
                ReachableMergeCommit = template.ReachableMergeCommit;
                UnreachableMergeCommit = template.UnreachableMergeCommit;
            }
            else
            {
                // Metadata and evidence cases use valid stable identities and do
                // not create/copy a repository or launch a Git process.
                Head = new string('a', 40);
                ReachableMergeCommit = new string('b', 40);
                UnreachableMergeCommit = new string('c', 40);
            }

            project = new ProjectConfig(ProjectSlug.Parse("demo"), "fixture.json", new("target", "main"),
                new(1, 1, 1), new(3, 1), new(AgentCli.Codex, null, "low", 1),
                new RoleSettings(AgentRoles.All.ToDictionary(role => role,
                    _ => new AgentSettings(AgentCli.Codex, null, "low", 1))));
            var targetRepositoryPath = temp.CreateDirectory("target");
            if (NativeGit)
            {
                RunGit(targetRepositoryPath, "init", "--initial-branch=main");
                RunGit(targetRepositoryPath, "remote", "add", "origin", OriginUrl);
            }
            repository = new ValidatedTargetRepository(targetRepositoryPath, "main")
            {
                OriginUrl = OriginUrl,
                OriginRepository = new("github.com", "example", "project")
            };
            var state = new BeadWorkflowState(BeadId, "task", "in_progress", "stage:merge", false, false, false, 0);
            session = new AgentStageSession(host, project, repository, BeadId, AgentRole.Merger,
                WorktreePurpose.Delivery, new ProvisionedWorktree(Worktree, Head), state, Owner,
                new AgentDispatchContext(BeadId, "task", "stage:merge", 0), PreserveInheritedChanges: true);

            var branch = $"nightweaver/{BeadId}";
            PullRequest = "https://github.com/example/project/pull/123";
            var logPath = Path.Combine(host.LogsRoot(project.Name), BeadId, invocationId + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var observations = new AgentStageObservations(state, state, Owner, Owner, AgentExecutionStatus.Exited,
                0, branch, Head, string.Empty, true);
            Invocation = new AgentStageResult(AgentRole.Merger, BeadId, invocationId, logPath, null, [], state,
                observations, classification, new HashSet<string>(["prior"], StringComparer.Ordinal));
            Runner = new VerifierProcessRunner(Head, branch, PullRequest, Worktree, targetRepositoryPath,
                NativeGit, NativeGit ? Template.Value.Directory.Path : null);
            SetFinding("checks", "fixture checks", "The required fixture check failed.",
                $"Required check 'fixture checks' failed for synchronized HEAD {Head}; inspect the fixture failure.", "remote",
                transcript: $"Merger rejection evidence: fixture checks; Required check 'fixture checks' failed for synchronized HEAD {Head}; inspect the fixture failure.");
        }

        internal string Worktree { get; }
        internal string Head { get; }
        internal string ReachableMergeCommit { get; }
        internal string UnreachableMergeCommit { get; }
        internal bool NativeGit { get; }
        internal string PullRequest { get; }
        internal VerifierProcessRunner Runner { get; }
        internal AgentStageResult Invocation { get; set; }
        internal IReadOnlyList<BeadCommentSnapshot> Comments { get; set; } = [];
        internal string MeasurementDirectory => Path.Combine(session.HostPaths.TmpRoot(project.Name), BeadId,
            invocationId, "0123456789abcdef0123456789abcdef");
        private string LogText { get; set; } = string.Empty;

        internal void SetPromptExample()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Nightweaver.slnx"))) root = root.Parent;
            Assert.NotNull(root);
            var prompt = File.ReadAllText(Path.Combine(root.FullName, "prompts", "merger.md"))
                .Replace("0123456789abcdef0123456789abcdef01234567", Head, StringComparison.Ordinal)
                .Replace("https://github.com/example/repository/pull/123", PullRequest, StringComparison.Ordinal);
            var frameStart = prompt.IndexOf("NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger {", StringComparison.Ordinal);
            var frameEnd = prompt.IndexOf("END_NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger", frameStart, StringComparison.Ordinal)
                + "END_NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger".Length;
            var jsonStart = prompt.IndexOf("```json", frameEnd, StringComparison.Ordinal) + "```json".Length;
            var jsonEnd = prompt.IndexOf("```", jsonStart, StringComparison.Ordinal);
            Comments = [new("current", prompt[jsonStart..jsonEnd].Trim(), DateTimeOffset.UtcNow)];
            File.WriteAllText(Invocation.LogFilePath, new string('x', 400_000) + "\n" + prompt[frameStart..frameEnd].Replace("20000101T0000000000000Z-merger", invocationId, StringComparison.Ordinal));
        }

        internal Task<MergerSlotOutcome> RunSlotAsync()
        {
            var slot = new MergerSlotCoordinator(Runner, async (_, _, _, _, _, _, _, token, _) =>
            {
                var verified = await VerifyAsync();
                Assert.True(verified.Succeeded, verified.Diagnostic);
                var published = await new DeliveryOutcomePublisher(Runner).PublishAsync(session, Invocation,
                    classification, releaseClaim: true, token);
                return new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, published, 1, null, null, null);
            });
            return slot.RunAsync(session.HostPaths, project, repository, BeadId, TestContext.Current.CancellationToken);
        }

        internal void SetLocalCheckFinding(string command, int exitCode, int? transcriptExitCode = null,
            string trailingOutput = "")
        {
            var summary = $"Local {command} failed for the checked HEAD.";
            var details = command == "dotnet test"
                ? "Failed: LocalCheckFailureTests.RejectsInvalidInput [1 ms]"
                : "error CS1002: expected ';' in Fixture.cs";
            var reportedExitCode = transcriptExitCode ?? exitCode;
            var envelope = JsonSerializer.Serialize(new { command, checked_head = Head, exit_code = reportedExitCode, pull_request = PullRequest });
            var terminal = command == "dotnet test"
                ? "Test run summary: Failed!\ntotal: 1\nfailed: 1"
                : "Build FAILED.\n1 Error(s)";
            var transcript = $"NIGHTWEAVER_LOCAL_CHECK_V2 {invocationId} {envelope}\n{summary}\n{details}\n{terminal}\n{trailingOutput}\nEND_NIGHTWEAVER_LOCAL_CHECK_V2 {invocationId}\n";
            SetFinding("checks", command, summary, details, "local", exitCode, transcript);
        }

        internal void SetPerformanceFinding(string? field = null, string? value = null)
        {
            const string id = "0123456789abcdef0123456789abcdef";
            var payload = new Dictionary<string, object?>
            {
                ["reason"] = "hard-limit", ["evidence_id"] = id, ["command"] = PerformanceCheckEvidence.CommandFor(id),
                ["total"] = 1325, ["passed"] = 1325, ["failed"] = 0, ["skipped"] = 0,
                ["elapsed_seconds"] = 300.5, ["hard_limit_seconds"] = 300, ["test_exit_code"] = 0
            };
            if (field is not null) payload[field] = JsonSerializer.Deserialize<JsonElement>(value!);
            var json = JsonSerializer.Serialize(new
            {
                schema = "nightweaver-merger-rejection-v1", kind = "performance", pull_request = PullRequest,
                checked_head = Head, subject = PerformanceCheckEvidence.Subject,
                summary = "Complete suite exceeded the local hard limit.",
                details = "All tests passed; repair the measured suite cost in the originating Bead.",
                check_source = "local", local_exit_code = 0, performance = payload
            });
            Comments = [new("current", json, DateTimeOffset.UtcNow)];
            const string terminal = "Test run summary: Passed!\ntotal: 1325\nfailed: 0\nsucceeded: 1325\nskipped: 0\n";
            File.WriteAllText(Invocation.LogFilePath, $"{terminal}NIGHTWEAVER_PERFORMANCE_CHECK_V1 {invocationId} {json}\nEND_NIGHTWEAVER_PERFORMANCE_CHECK_V1 {invocationId}\n");
            Directory.CreateDirectory(MeasurementDirectory);
            File.WriteAllText(Path.Combine(MeasurementDirectory, "suite.log"), terminal);
            File.WriteAllText(Path.Combine(MeasurementDirectory, "report-1.xml"),
                "<assemblies><assembly total=\"1325\" passed=\"1325\" failed=\"0\" skipped=\"0\" errors=\"0\" not-run=\"0\" environment=\".NET 10.0.8, 6 threads\" /></assemblies>");
            File.WriteAllText(Path.Combine(MeasurementDirectory, "result.json"), JsonSerializer.Serialize(new
            {
                schema = "nightweaver-suite-measurement-v1", invocationId, evidenceId = id,
                candidateRevision = Head, candidateTree = new string('b', 40), dirty = false,
                commandPolicy = PerformanceCheckEvidence.Subject, command = PerformanceCheckEvidence.CommandFor(id),
                threads = 6, testExitCode = 0, total = 1325, passed = 1325, failed = 0, skipped = 0,
                elapsedSeconds = 300.5, measurementErrors = Array.Empty<string>()
            }));
        }

        internal void PinRemoteFinding()
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(Comments[^1].Text)!;
            fields["workflow_run_id"] = 100L;
            fields["workflow_run_attempt"] = 1L;
            fields["workflow_job_id"] = 200L;
            Comments = [new("current", JsonSerializer.Serialize(fields), DateTimeOffset.UtcNow)];
        }

        internal void SetFinding(string kind, string subject, string summary, string details,
            string? checkSource = null, int? localExitCode = null, string? transcript = null)
        {
            var fields = new Dictionary<string, object?>
            {
                ["schema"] = "nightweaver-merger-rejection-v1",
                ["kind"] = kind,
                ["pull_request"] = PullRequest,
                ["checked_head"] = Head,
                ["subject"] = subject,
                ["summary"] = summary,
                ["details"] = details
            };
            if (checkSource is not null) fields["check_source"] = checkSource;
            if (localExitCode is not null) fields["local_exit_code"] = localExitCode.Value;
            var finding = JsonSerializer.Serialize(fields);
            Comments = [new("prior", finding, DateTimeOffset.UnixEpoch), new("current", finding, DateTimeOffset.UnixEpoch.AddSeconds(1))];
            LogText = transcript ?? $"Merger rejection evidence: {subject}; {details}\n";
            File.WriteAllText(Invocation.LogFilePath, LogText);
        }

        internal void SetMergeEvidence(string? pullRequest = null, string? checkedHead = null,
            string mergeMethod = "squash", string? mergeCommit = null)
        {
            var commit = mergeCommit ?? new string('b', 40);
            Runner.ReportedMergeCommit = commit;
            var comment = $"nightweaver-merge-v1 pull_request={pullRequest ?? PullRequest} checked_head={checkedHead ?? Head} required_checks=passed merge_method={mergeMethod} merge_commit={commit}";
            Comments = [new("prior", comment, DateTimeOffset.UnixEpoch), new("current", comment, DateTimeOffset.UnixEpoch.AddSeconds(1))];
        }

        internal Task<MergerRejectionVerification> VerifyAsync(IProcessRunner? runner = null) =>
            new MergerOutcomeVerifier(runner ?? Runner).VerifyRejectionAsync(
                new HostPaths(Path.Combine(temp.Path, "host")), project, session, Invocation,
                classification, Comments, Owner, CancellationToken.None);

        internal Task<MergerApprovalVerification> VerifyApprovalAsync(IProcessRunner? runner = null) =>
            new MergerOutcomeVerifier(runner ?? Runner).VerifyApprovalAsync(session, Invocation,
                new(AgentOutcomeChoice.Approved, "fixture", "The merged result is confirmed."),
                Comments, Owner, CancellationToken.None);

        public void Dispose() => temp.Dispose();

        internal static string RunGit(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = workingDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(workingDirectory, "missing-global-config");
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var process = FixtureProcess.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}{output}");
            return output.Trim();
        }

        internal static void RunGitWithInput(string workingDirectory, string input, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = workingDirectory, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(workingDirectory, "missing-global-config");
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var process = FixtureProcess.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}{output}");
        }
    }

    private sealed class VerifierProcessRunner(string head, string branch, string pullRequest,
        string worktreePath, string targetRepositoryPath, bool nativeGit, string? nativeFetchSource) : IProcessRunner
    {
        private readonly SystemProcessRunner system = new();
        private readonly string originalHead = head;
        private readonly string expectedWorktreePath = Path.GetFullPath(worktreePath);
        private readonly string expectedTargetRepositoryPath = Path.GetFullPath(targetRepositoryPath);
        private readonly bool useNativeGit = nativeGit;
        private readonly string? fetchSource = nativeFetchSource;
        internal int NativeGitCommandCount { get; private set; }
        internal string OriginUrl { get; set; } = "https://github.com/example/project.git";
        internal string? OriginUrlAfterChecks { get; set; }
        internal string ReportedPullRequestUrl { get; set; } = pullRequest;
        internal string? PullRequestRepository { get; private set; }
        internal string? ChecksRepository { get; private set; }
        internal string Status { get; set; } = "in_progress";
        internal string? Assignee { get; set; } = VerifierFixture.Owner;
        internal string Stage { get; set; } = "stage:merge";
        internal int RepairCycles { get; private set; }
        internal string[] Labels => [Stage, "keep:label"];
        internal List<IReadOnlyList<string>> PublicationWrites { get; } = [];
        private readonly List<string> publishedComments = [];
        internal bool CheckFailed { get; set; } = true;
        internal bool CheckSkipped { get; set; }
        internal string? HostedMutation { get; set; }
        internal int RequiredChecksExitCode { get; set; }
        internal bool RequiredLinkIsPinned { get; set; }
        internal string AdvertisedHead { get; set; } = head;
        internal string? HeadAfterChecks { get; set; }
        internal string BaseBranch { get; set; } = "main";
        internal bool ApprovalMode { get; set; }
        internal string ReportedMergeCommit { get; set; } = new('b', 40);
        internal bool FetchedDefaultBranch { get; private set; }
        internal int PullRequestViewCount { get; private set; }
        internal int RequiredChecksCount { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FileName == "git")
                return RunGitAsync(request, cancellationToken);
            if (request.FileName == "gh" && request.Arguments[0] == "api")
            {
                Assert.Equal("github.com", request.Arguments[2]);
                var path = request.Arguments[3];
                Assert.StartsWith("repos/example/project/actions/", path, StringComparison.Ordinal);
                if (path.Contains("/jobs/", StringComparison.Ordinal))
                {
                    var current = path.EndsWith("/201", StringComparison.Ordinal);
                    return Task.FromResult(Json(new
                    {
                        id = current ? 201L : 200L,
                        run_id = current ? 101L : HostedMutation == "job" ? 999L : 100L,
                        run_attempt = 1,
                        head_sha = HostedMutation == "job-head" ? new string('e', 40) : originalHead,
                        name = HostedMutation == "job-name" ? "unrelated" : "fixture checks",
                        status = HostedMutation == "pending" ? "in_progress" : "completed",
                        conclusion = current ? HostedMutation == "newer-passed" ? "success" : "skipped" : HostedMutation switch
                        {
                            "skipped" => "skipped", "passed" => "success", "cancelled" => "cancelled", _ => "failure"
                        }
                    }));
                }
                object RunRow(long runId, bool rerun = false) => new
                {
                    id = runId,
                    run_attempt = HostedMutation == "attempt" || rerun ? 2 : 1,
                    run_started_at = HostedMutation == "old-run" ? "2026-11-03T12:00:00Z" : "2026-11-04T12:01:00Z",
                    workflow_id = runId == 101 && HostedMutation == "workflow" ? 888 : 300,
                    head_sha = HostedMutation == "head" ? new string('e', 40) : originalHead,
                    head_branch = HostedMutation == "branch" ? "unrelated" : branch,
                    @event = HostedMutation == "event" ? "workflow_dispatch" : "pull_request",
                    repository = new { full_name = HostedMutation == "repository" ? "unrelated/repo" : "example/project" },
                    head_repository = new { full_name = HostedMutation == "head-repository" ? "fork/project" : "example/project" },
                    pull_requests = new[] { new { number = HostedMutation == "pr" ? 999 : 123 } },
                    status = "completed", conclusion = runId == 100 ? "failure" : "success"
                };
                if (path.Contains("/workflows/", StringComparison.Ordinal))
                {
                    var rows = new List<object> { RunRow(100), RunRow(101) };
                    if (HostedMutation == "hidden-passed") rows.Add(RunRow(102));
                    return Task.FromResult(Json(new { total_count = HostedMutation == "history-overflow" ? 11 : rows.Count, workflow_runs = rows }));
                }
                if (path.Contains("/jobs?", StringComparison.Ordinal))
                {
                    var hidden = path.Contains("/runs/102/", StringComparison.Ordinal);
                    return Task.FromResult(Json(new
                    {
                        total_count = HostedMutation == "jobs-overflow" ? 101 : 1,
                        jobs = new[] { new { id = hidden ? 202 : 201, run_id = hidden ? 102 : 101, run_attempt = 1,
                            name = "fixture checks", status = "completed", conclusion = hidden ? "success" : "skipped" } }
                    }));
                }
                var latest = path.EndsWith("/101", StringComparison.Ordinal);
                var rerun = path.EndsWith("/100", StringComparison.Ordinal) && HostedMutation == "rerun";
                return Task.FromResult(Json(RunRow(latest ? 101 : 100, rerun)));
            }
            if (request.FileName == "bd")
            {
                var args = request.Arguments;
                string? Value(string key)
                {
                    var index = args.ToList().IndexOf(key);
                    return index < 0 ? null : args[index + 1];
                }
                object Row() => new
                {
                    id = VerifierFixture.BeadId, status = Status, issue_type = "task", priority = 2,
                    created_at = "2026-01-01T00:00:00Z", assignee = Assignee,
                    labels = Labels, metadata = new { repair_cycles = RepairCycles }
                };
                if (args.Contains("comments", StringComparer.Ordinal))
                {
                    if (args.Contains("add", StringComparer.Ordinal)) publishedComments.Add(args[^1]);
                    return Task.FromResult(Json(publishedComments.Select((text, index) => new
                    {
                        id = $"published-{index}", issue_id = VerifierFixture.BeadId, text,
                        author = VerifierFixture.Owner, created_at = "2026-01-01T00:00:00Z"
                    }).ToArray()));
                }
                if (args.Contains("update", StringComparer.Ordinal))
                {
                    if (!args.Contains("--add-label", StringComparer.Ordinal))
                    {
                        Assert.Equal("open", Status);
                        Assert.Null(Assignee);
                        Assert.Equal(Status, Value("--if-status"));
                        Assert.Equal("", Value("--if-assignee"));
                        Status = "in_progress";
                        Assignee = VerifierFixture.Owner;
                    }
                    else
                    {
                        Assert.Equal(Status, Value("--if-status"));
                        Assert.Equal(Assignee, Value("--if-assignee"));
                        PublicationWrites.Add(args.ToArray());
                        Stage = Value("--add-label")!;
                        RepairCycles = int.Parse(Value("--set-metadata")!["repair_cycles=".Length..],
                            System.Globalization.CultureInfo.InvariantCulture);
                        Status = Value("--status") ?? Status;
                        if (Value("--assignee") == "") Assignee = null;
                    }
                }
                if (args.Contains("ready", StringComparer.Ordinal) || args.Contains("--ready", StringComparer.Ordinal))
                {
                    var ready = Status is "open" or "in_progress";
                    return Task.FromResult(Json(ready ? new[] { Row() } : Array.Empty<object>()));
                }
                return Task.FromResult(Json(new[] { Row() }));
            }
            if (request.FileName == "gh" && request.Arguments.Count >= 2
                && request.Arguments[0] == "pr" && request.Arguments[1] == "view")
            {
                PullRequestViewCount++;
                var jsonIndex = request.Arguments.ToList().IndexOf("--json");
                if (jsonIndex < 0 || jsonIndex + 1 >= request.Arguments.Count)
                    return Task.FromResult(new ProcessResult(1, string.Empty, "gh pr view requires --json fields in this fixture."));
                var requestedFields = request.Arguments[jsonIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var supportedFields = new HashSet<string>(
                    ["state", "baseRefName", "headRefName", "headRefOid", "url", "mergedAt", "mergeCommit"],
                    StringComparer.Ordinal);
                var unsupportedField = requestedFields.FirstOrDefault(field => !supportedFields.Contains(field));
                if (unsupportedField is not null)
                    return Task.FromResult(new ProcessResult(1, string.Empty, $"Unknown JSON field: {unsupportedField}"));
                var repoIndex = request.Arguments.ToList().IndexOf("--repo");
                PullRequestRepository = repoIndex >= 0 && repoIndex + 1 < request.Arguments.Count
                    ? request.Arguments[repoIndex + 1] : null;
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    state = ApprovalMode ? "MERGED" : "OPEN",
                    baseRefName = BaseBranch,
                    headRefName = branch,
                    headRefOid = AdvertisedHead,
                    url = ReportedPullRequestUrl,
                    mergedAt = ApprovalMode ? "2026-11-04T20:00:00Z" : null,
                    mergeCommit = ApprovalMode ? new { oid = ReportedMergeCommit } : null
                }), string.Empty));
            }
            if (request.FileName == "gh" && request.Arguments.Count >= 2
                && request.Arguments[0] == "pr" && request.Arguments[1] == "checks")
            {
                var repoIndex = request.Arguments.ToList().IndexOf("--repo");
                ChecksRepository = repoIndex >= 0 && repoIndex + 1 < request.Arguments.Count
                    ? request.Arguments[repoIndex + 1] : null;
                RequiredChecksCount++;
                if (HeadAfterChecks is not null) AdvertisedHead = HeadAfterChecks;
                if (useNativeGit && OriginUrlAfterChecks is not null)
                {
                    VerifierFixture.RunGit(expectedTargetRepositoryPath,
                        "remote", "set-url", "origin", OriginUrlAfterChecks);
                    NativeGitCommandCount++;
                }
                return Task.FromResult(new ProcessResult(RequiredChecksExitCode, JsonSerializer.Serialize(new[]
                {
                    new { name = "fixture checks", state = CheckSkipped ? "SKIPPED" : CheckFailed ? "FAILURE" : "SUCCESS", bucket = CheckSkipped ? "skipping" : CheckFailed ? "fail" : "pass",
                        link = HostedMutation == "link" ? "https://github.com/unrelated/repo/actions/runs/101/job/201" : RequiredLinkIsPinned ? "https://github.com/example/project/actions/runs/100/job/200" : "https://github.com/example/project/actions/runs/101/job/201" }
                }), string.Empty));
            }
            throw new InvalidOperationException($"Unexpected verifier command: {request.FileName} {string.Join(' ', request.Arguments)}");
        }

        private Task<ProcessResult> RunGitAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            var args = request.Arguments;
            var worktreeRequest = Path.GetFullPath(request.WorkingDirectory) == expectedWorktreePath;
            var targetRequest = Path.GetFullPath(request.WorkingDirectory) == expectedTargetRepositoryPath;

            if (!useNativeGit)
                return Task.FromResult(RunScriptedGit(args, worktreeRequest, targetRequest));

            if (!worktreeRequest && !targetRequest)
                throw new InvalidOperationException($"Unexpected Git working directory: {request.WorkingDirectory}");

            if (args.SequenceEqual(["fetch", OriginUrl, "main"], StringComparer.Ordinal))
            {
                Assert.True(worktreeRequest, "Default-branch fetch must run in the retained worktree.");
                Assert.False(string.IsNullOrWhiteSpace(fetchSource));
                FetchedDefaultBranch = true;
                return RunNativeGitAsync(["fetch", fetchSource!, "main"], expectedWorktreePath, cancellationToken);
            }

            if (args.SequenceEqual(["branch", "--show-current"], StringComparer.Ordinal)
                || args.SequenceEqual(["rev-parse", "HEAD"], StringComparer.Ordinal)
                || args.SequenceEqual(["status", "--porcelain"], StringComparer.Ordinal)
                || args.SequenceEqual(["ls-files", "-u"], StringComparer.Ordinal)
                || args.SequenceEqual(["rev-parse", "--git-path", "rebase-merge"], StringComparer.Ordinal)
                || args.SequenceEqual(["rev-parse", "--git-path", "rebase-apply"], StringComparer.Ordinal)
                || args.SequenceEqual(["config", "--get", "remote.origin.url"], StringComparer.Ordinal)
                || args.SequenceEqual(["check-ref-format", "refs/heads/main"], StringComparer.Ordinal)
                || (args.Count == 4 && args[0] == "merge-base" && args[1] == "--is-ancestor"
                    && args[2] == ReportedMergeCommit && args[3] == "FETCH_HEAD"))
            {
                if (args[0] == "config")
                    Assert.True(targetRequest, "Validated origin must be read from the target repository.");
                else
                    Assert.True(worktreeRequest, "Worktree observations must use the retained worktree.");
                NativeGitCommandCount++;
                return system.RunAsync(request, cancellationToken);
            }

            throw new InvalidOperationException($"Unexpected native Git command: {string.Join(' ', args)}");
        }

        private ProcessResult RunScriptedGit(IReadOnlyList<string> args, bool worktreeRequest, bool targetRequest)
        {
            if (args.SequenceEqual(["branch", "--show-current"], StringComparer.Ordinal))
            {
                Assert.True(worktreeRequest);
                return new(0, branch, string.Empty);
            }
            if (args.SequenceEqual(["rev-parse", "HEAD"], StringComparer.Ordinal))
            {
                Assert.True(worktreeRequest);
                return new(0, originalHead, string.Empty);
            }
            if (args.SequenceEqual(["status", "--porcelain"], StringComparer.Ordinal))
            {
                Assert.True(worktreeRequest);
                return new(0, string.Empty, string.Empty);
            }
            if (args.SequenceEqual(["ls-files", "-u"], StringComparer.Ordinal))
            {
                Assert.True(worktreeRequest);
                return new(0, string.Empty, string.Empty);
            }
            if (args.Count == 3 && args[0] == "rev-parse" && args[1] == "--git-path"
                && args[2] is "rebase-merge" or "rebase-apply")
            {
                Assert.True(worktreeRequest);
                return new(0, Path.Combine(expectedWorktreePath, ".git", args[2]), string.Empty);
            }
            if (args.SequenceEqual(["config", "--get", "remote.origin.url"], StringComparer.Ordinal))
            {
                Assert.True(targetRequest);
                var origin = OriginUrlAfterChecks is not null && RequiredChecksCount > 0
                    ? OriginUrlAfterChecks : OriginUrl;
                return new(0, origin, string.Empty);
            }
            if (args.SequenceEqual(["check-ref-format", "refs/heads/main"], StringComparer.Ordinal))
            {
                Assert.True(worktreeRequest);
                return new(0, string.Empty, string.Empty);
            }
            if (args.SequenceEqual(["fetch", OriginUrl, "main"], StringComparer.Ordinal))
            {
                Assert.True(worktreeRequest);
                FetchedDefaultBranch = true;
                return new(0, string.Empty, string.Empty);
            }
            if (args.Count == 4 && args[0] == "merge-base" && args[1] == "--is-ancestor"
                && args[2] == ReportedMergeCommit && args[3] == "FETCH_HEAD")
            {
                Assert.True(worktreeRequest);
                return new(0, string.Empty, string.Empty);
            }

            throw new InvalidOperationException($"Unexpected scripted Git command: {string.Join(' ', args)}");
        }

        private async Task<ProcessResult> RunNativeGitAsync(IReadOnlyList<string> args,
            string workingDirectory, CancellationToken cancellationToken)
        {
            NativeGitCommandCount++;
            return await system.RunAsync(GitCommand.Create(args, workingDirectory), cancellationToken);
        }

        private static ProcessResult Json(object value) => new(0, JsonSerializer.Serialize(value), string.Empty);
    }
}
