using System.Text.Json;
using Nightweaver.ActivityFeed;
using Nightweaver.Application;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class ContinuousApplicationTests
{
    [Theory]
    [InlineData("https://github.com/example/factory.git")]
    [InlineData("git@github.com:example/factory.git")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("failure")]
    public async Task StartupConnectsActivityToValidatedOriginWithoutChangingAdmission(string origin)
    {
        using var temp = new TempDirectory();
        var target = temp.CreateDirectory("target");
        var common = Path.GetFullPath(temp.CreateDirectory("target/.git"));
        temp.WriteFile("target/.beads/metadata.json", "{\"backend\":\"dolt\",\"dolt_mode\":\"embedded\"}");
        temp.CreateDirectory("target/.beads/embeddeddolt");
        temp.WriteFile("target/.beads.gate.lock", "");
        temp.WriteFile("target/.beads/embeddeddolt.gate.lock", "");
        var paths = new HostPaths(temp.CreateDirectory("host"));
        var slug = new ProjectSlug("demo");
        var config = temp.WriteFile("project.json", JsonSerializer.Serialize(new
        {
            name = slug.Value, target_repository = new { path = target },
            concurrency = new { max_active_plan_beads = 1 }
        }));
        // One task outside native readiness closes Planning's backlog gate without admitting a slot.
        const string inventory = """
            [{"id":"task-1","status":"open","issue_type":"task","priority":2,
              "created_at":"2026-01-01T00:00:00Z","labels":[]}]
            """;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var completedTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessReads = 0;
        var runner = new RecordingProcessRunner((request, _) =>
        {
            string output;
            if (request.FileName == "git")
            {
                output = request.Arguments[0] switch
                {
                    "check-ref-format" => "main\n",
                    "config" when origin == "failure" => throw new ToolExecutionException("origin unavailable"),
                    "config" when origin == "missing" => "",
                    "config" => origin,
                    "worktree" => $"worktree {target}\0HEAD {new string('a', 40)}\0branch refs/heads/main\0\0",
                    "rev-parse" when request.Arguments.Contains("--is-inside-work-tree") => $"true\n\n{common}\n{common}\n",
                    "rev-parse" => common + "\n",
                    _ => throw new InvalidOperationException(string.Join(' ', request.Arguments))
                };
                return Task.FromResult(new ProcessResult(origin == "missing" && request.Arguments[0] == "config" ? 1 : 0, output, ""));
            }
            Assert.Equal("bd", request.FileName);
            if (request.Arguments.SequenceEqual(BeadsCommand.Create(target).Arguments)) output = inventory;
            else if (request.Arguments.Contains("branch")) output = "\"main\"";
            // Refuse watcher setup so this controlled test cannot launch the native journal reader.
            else if (request.Arguments.Contains("config")) return Task.FromResult(new ProcessResult(1, "", "watcher disabled"));
            else output = "[]";
            if (request.Arguments.Contains("--ready") && Interlocked.Increment(ref readinessReads) == 3)
                completedTurn.TrySetResult();
            return Task.FromResult(new ProcessResult(0, output, ""));
        });
        var error = new StringWriter();
        var acquired = false;
        var run = ContinuousApplication.RunAsync(new(new ProjectSelector.ByConfigPath(config), false, false),
            new(paths.Root, temp.Path, new StringWriter(), error), runner, null, stop.Token,
            // Process-lifetime exclusion has separate native coverage. Do not retain a lease in this test process.
            acquireRuntime: (_, _, repository) => { Assert.Equal(common, repository.GitCommonDirectory); acquired = true; });
        try
        {
            await await Task.WhenAny(run, completedTurn.Task)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(ExitCodes.Success, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        Assert.True(acquired);
        Assert.Single(runner.Requests, r => r.FileName == "git" && r.Arguments[0] == "config");
        Assert.Equal(3, readinessReads);
        Assert.DoesNotContain(runner.Requests, r => r.FileName == "bd" && r.Arguments.Contains("update"));
        if (origin is "missing" or "invalid" or "failure")
        {
            Assert.False(Directory.Exists(paths.ActivityRoot(slug)));
            Assert.Contains("Activity journal unavailable", error.ToString(), StringComparison.Ordinal);
            return;
        }
        using var reader = new ActivityJournalFiles(paths, slug, target, new("github.com", "example", "factory"));
        var page = reader.ReadPage(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ActivityPageState.Ready, page.State);
        Assert.Contains(page.Records, r => r.Reason == ActivityReason.RuntimeStarting);
        Assert.Contains(page.Records, r => r.Reason == ActivityReason.RuntimeRunning);
        var gate = Assert.Single(page.Records, r => r.Kind == ActivityKind.WaitStarted && r.Phase == ActivityPhase.Planning);
        Assert.Equal(ActivityReason.Backpressure, gate.Reason);
        Assert.Equal(1, gate.Count);
        Assert.Equal(1, gate.Limit);
        Assert.Contains(page.Records, r => r.Reason == ActivityReason.RuntimeStopped);
        Assert.Single(page.Records.Select(r => r.Position.Runtime).Distinct());
    }

    [Fact]
    public void RuntimeValidationDoesNotRecreateALogRootRemovedAfterStartup()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(Path.Combine(temp.Path, "host"));
        var project = new ProjectSlug("demo");
        ContinuousApplication.EnsureInitialRoots(paths, project);

        var logsRoot = paths.LogsRoot(project);
        Directory.Delete(logsRoot);

        var exception = Assert.Throws<ToolExecutionException>(() =>
            ContinuousApplication.ValidateAdmissionRoots(paths, project));

        Assert.Contains(logsRoot, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(logsRoot));
    }
}
