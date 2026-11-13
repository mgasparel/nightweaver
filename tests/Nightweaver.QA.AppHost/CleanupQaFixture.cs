using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

/// <summary>Disposable native cleanup failure, persisted restart and live cross-process inbox.
/// No production paths, workflow changes after closure, or Web disposal shortcuts.</summary>
internal sealed class CleanupQaFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly CompletedEnvironmentWorker worker;
    private readonly Task loop;
    private CleanupQaFixture(CompletedEnvironmentWorker worker)
    {
        this.worker = worker;
        loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await worker.RunBatchAsync(stop.Token);
                await Task.Delay(500, stop.Token);
            }
        });
    }

    internal static async Task<CleanupQaFixture> CreateAsync(HostPaths paths, ProjectConfig project, string runtimeId,
        Func<string, string, string[], CancellationToken, Task<string>> run, CancellationToken token)
    {
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, "main")
        { GitCommonDirectory = Path.Combine(project.TargetRepository.Path, ".git") };
        async Task<string> Native(string executable, params string[] args) => await run(executable, repository.Path, args, token);
        await Native("git", "-c", "user.name=QA", "-c", "user.email=qa@example.invalid", "commit", "--allow-empty", "-m", "Disposable QA baseline");
        using var created = JsonDocument.Parse(await Native("bd", "create", "--title", "QA completed cleanup failure", "--type", "task", "--json"));
        var beadId = created.RootElement.GetProperty("id").GetString()!;
        Directory.CreateDirectory(paths.WorktreesRoot(project.Name));
        var tmp = Directory.CreateDirectory(Path.Combine(paths.TmpRoot(project.Name), beadId)).FullName;
        var evidence = Path.Combine(tmp, "ignored-qa-report.xml");
        await File.WriteAllTextAsync(evidence, "QA passed; retained until verified completion", token);
        await Native("bd", "close", beadId, "--reason", "Disposable verified completion fixture");
        var clock = new FixtureClock();
        var runner = new NativeRunner(run);
        using (var locked = new FileStream(evidence, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var first = new CompletedEnvironmentWorker(paths, project, repository, runner, new(), new(),
                   runtimeId, Console.WriteLine, clock))
        {
            await first.InitializeAsync(token);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await first.RunBatchAsync(token);
                clock.Now = clock.Now.AddMinutes(11);
            }
            var record = new CompletedEnvironmentStore(paths, project.Name, repository).Read().Obligations.Single(item => item.BeadId == beadId);
            if (record.Status != CleanupStatus.NeedsAttention || record.Attempts != 4 || !File.Exists(evidence))
                throw new InvalidOperationException("Cleanup fixture failed to retain bounded failure evidence.");
        }
        // Reconstruct the owner from durable state; no attempt until real browser Retry.
        var restarted = new CompletedEnvironmentWorker(paths, project, repository, runner, new(), new(), runtimeId,
            Console.WriteLine, clock);
        await restarted.InitializeAsync(token);
        return new(restarted);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { worker.Dispose(); stop.Dispose(); }
    }
    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class NativeRunner(Func<string, string, string[], CancellationToken, Task<string>> run) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            new(0, await run(request.FileName, request.WorkingDirectory, request.Arguments.ToArray(), cancellationToken), "");
    }
}
