namespace Nightweaver.Tests;

// Static checks intentionally bind the actual workflow, not a second policy implementation.
public sealed class HostedWorkflowContractTests
{
    [Fact]
    public void DraftEventsSkipTheOnlyExpensiveJobAndReadinessRetriggersIt()
    {
        var workflow = Read(".github", "workflows", "build-test.yml");
        Assert.Contains("types: [opened, synchronize, reopened, ready_for_review, converted_to_draft]", workflow, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name != 'pull_request' || github.event.pull_request.draft == false", workflow, StringComparison.Ordinal);
        Assert.Contains("branches: [main]", workflow, StringComparison.Ordinal);
        Assert.Equal(1, workflow.Split("runs-on:", StringSplitOptions.None).Length - 1);
        Assert.True(workflow.IndexOf("    if:", StringComparison.Ordinal) < workflow.IndexOf("    runs-on:", StringComparison.Ordinal));
        Assert.Contains("group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: ${{ github.event_name == 'pull_request' }}", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchRequiresAttributionAndRecordsActualCheckoutWithoutShellInterpolation()
    {
        var workflow = Read(".github", "workflows", "build-test.yml");
        Assert.Contains("workflow_dispatch:\n    inputs:", workflow, StringComparison.Ordinal);
        foreach (var input in new[] { "bead", "requirement" })
        {
            var block = workflow.Split($"      {input}:\n", StringSplitOptions.None)[1].Split("type: string", StringSplitOptions.None)[0];
            Assert.Contains("required: true", block, StringComparison.Ordinal);
            Assert.Contains("${{ inputs." + input + " }}", workflow, StringComparison.Ordinal);
        }
        Assert.Contains("$revision = git rev-parse HEAD", workflow, StringComparison.Ordinal);
        Assert.Contains("Tested revision: $revision", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/runs/$env:GITHUB_RUN_ID", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.", workflow[(workflow.IndexOf("        run: |", StringComparison.Ordinal))..], StringComparison.Ordinal);
    }

    [Fact]
    public void HostedTimingIsDiagnosticWhileLocalFullSuiteKeepsItsBudget()
    {
        var workflow = Read(".github", "workflows", "build-test.yml");
        Assert.Contains("name: Windows build and test", workflow, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 15", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build Nightweaver.slnx -warnaserror", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($testExit -ne 0) { exit $testExit }", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("TotalSeconds -ge 300", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--filter", workflow, StringComparison.Ordinal);
        Assert.Contains("$timer.Elapsed", workflow, StringComparison.Ordinal);
        var local = Read("tests", "README.md");
        Assert.Contains("TotalSeconds -ge 300", local, StringComparison.Ordinal);
        Assert.Contains("local latest-main performance requirement", local, StringComparison.Ordinal);
        Assert.Contains("six-thread parallelism", local, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Nightweaver.slnx")))
                return File.ReadAllText(Path.Combine([directory.FullName, .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
