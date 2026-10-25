using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class BeadsApplicationTests
{
    [Theory]
    [InlineData("nonzero")]
    [InlineData("missing")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("malformed")]
    [InlineData("schema")]
    public async Task InspectionFailureNeverPrintsPartialSuccess(string failure)
    {
        using var repository = new BeadsApplicationFixture();
        using var temp = new TempDirectory();
        var config = temp.WriteFile("project.json", JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = repository.Path } }));
        var runner = new RecordingProcessRunner((request, token) =>
        {
            if (request.FileName == "git") return new SystemProcessRunner().RunAsync(request, token);
            return failure switch
            {
                "nonzero" => Task.FromResult(new ProcessResult(1, "[]", "fatal backend failure")),
                "missing" => throw new ToolExecutionException("required executable 'bd' was not found on PATH"),
                "timeout" => throw new ToolExecutionException("'bd' timed out after 30s"),
                "cancelled" => throw new OperationCanceledException(token),
                "malformed" => Task.FromResult(new ProcessResult(0, "[", "")),
                _ => Task.FromResult(new ProcessResult(0, "[{}]", ""))
            };
        });
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await NightweaverApp.RunAsync(["--config", config, "--dry-run"], new(temp.Path, temp.Path, output, error), runner, CancellationToken.None);
        Assert.Equal(1, code);
        Assert.Equal("", output.ToString());
        Assert.Contains(runner.Requests, request => request.FileName == "bd");
        Assert.Contains("bd --readonly --sandbox", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(repository.Path, error.ToString(), StringComparison.Ordinal);
        if (failure == "schema") Assert.Contains("$[0].id", error.ToString(), StringComparison.Ordinal);
        if (failure == "nonzero") Assert.Contains("fatal backend failure", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureOfRecoveryReadinessQueryPrintsNoPartialReport()
    {
        using var repository = new BeadsApplicationFixture();
        using var temp = new TempDirectory();
        var config = temp.WriteFile("project.json", JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = repository.Path } }));
        var inventoryArgs = BeadsCommand.Create(repository.Path).Arguments;
        var nativeArgs = BeadsCommand.CreateUnassignedReadiness(repository.Path).Arguments;
        var recoveryArgs = BeadsCommand.CreateRecoverableReadiness(repository.Path, "nightweaver:demo").Arguments;
        var runner = new RecordingProcessRunner((request, token) =>
        {
            if (request.FileName == "git") return new SystemProcessRunner().RunAsync(request, token);
            var response = request.Arguments.SequenceEqual(inventoryArgs) || request.Arguments.SequenceEqual(nativeArgs)
                ? new ProcessResult(0, "[]", "")
                : new ProcessResult(2, "", "recovery readiness unavailable");
            return Task.FromResult(response);
        });
        var output = new StringWriter();
        var error = new StringWriter();
        var before = Snapshot.FileTree(repository.Path);

        var code = await NightweaverApp.RunAsync(["--config", config, "--dry-run"], new(temp.Path, temp.Path, output, error), runner, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal("", output.ToString());
        Assert.Contains(runner.Requests, request => request.Arguments.SequenceEqual(recoveryArgs));
        Assert.Contains("bd --readonly --sandbox list --ready --status in_progress --assignee nightweaver:demo", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("recovery readiness unavailable", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot.FileTree(repository.Path));
    }

    [Fact]
    public async Task ReportsAreStableAcrossInventoryOrderAndEscapeToolText()
    {
        using var repository = new BeadsApplicationFixture();
        using var temp = new TempDirectory();
        var config = temp.WriteFile("project.json", JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = repository.Path } }));
        var rows = ((string[])["z\nInjected", "a"]).Select(id => JsonSerializer.Serialize(new
        {
            id, status = "in_progress", issue_type = "task", priority = 2, created_at = "2026-01-01T00:00:00Z", assignee = "other\nActor"
        })).ToArray();
        async Task<string> Render(IEnumerable<string> input)
        {
            var runner = new RecordingProcessRunner((request, token) => request.FileName == "git"
                ? new SystemProcessRunner().RunAsync(request, token)
                : request.Arguments.SequenceEqual(BeadsCommand.Create(repository.Path).Arguments)
                    ? Task.FromResult(new ProcessResult(0, "[" + string.Join(',', input) + "]", ""))
                    : Task.FromResult(new ProcessResult(0, "[]", "")));
            var output = new StringWriter();
            Assert.Equal(0, await NightweaverApp.RunAsync(["--config", config, "--dry-run"], new(temp.Path, temp.Path, output, new StringWriter()), runner, CancellationToken.None));
            return output.ToString();
        }
        var report = await Render(rows);
        Assert.Equal(report, await Render(rows.Reverse()));
        Assert.Contains("z\\nInjected", report, StringComparison.Ordinal);
        Assert.DoesNotContain("other\nActor", report, StringComparison.Ordinal);
    }
}

