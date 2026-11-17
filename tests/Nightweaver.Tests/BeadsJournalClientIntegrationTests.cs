using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nightweaver.Application;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class BeadsJournalClientIntegrationTests
{
    [Fact]
    public async Task NativeJournalBatchRequestsFreshDiscoveryAfterAnOrchestratorAuthoredWrite()
    {
        using var fixture = new BeadsFixture();
        var repository = new ValidatedTargetRepository(fixture.Path, "main");
        var runner = new SystemProcessRunner();
        var client = new BeadsJournalClient(runner);
        await client.EnableAsync(repository, TestContext.Current.CancellationToken);
        var id = await CreateBeadAsync(runner, fixture.Path, "native finite batch write",
            "nightweaver:orchestrator", TestContext.Current.CancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requests = 0;
        var watcher = new BeadsJournalWatcher(client, () => requests++, TextWriter.Null,
            (delay, _) => { Assert.Equal(BeadsJournalWatcher.PollInterval, delay); lifetime.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.RunAsync(repository, lifetime.Token));
        Assert.Equal(1, requests);
        var inventory = await new BeadsReader(runner).ReadSnapshotAsync(repository, TestContext.Current.CancellationToken);
        Assert.Contains(id, inventory.ById.Keys);
        var batch = await client.ReadBatchAsync(repository, 0, 256, TestContext.Current.CancellationToken);
        Assert.NotEmpty(batch);
        Assert.Empty(await client.ReadBatchAsync(repository, batch.Max(), 256, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NativeJournalTruncationIsReportedWithItsCurrentHead()
    {
        using var fixture = new BeadsFixture();
        var repository = new ValidatedTargetRepository(fixture.Path, "main");
        var processRunner = new SystemProcessRunner();
        var client = new BeadsJournalClient(processRunner);
        await client.EnableAsync(repository, TestContext.Current.CancellationToken);

        for (var index = 0; index < 3; index++)
        {
            var write = await processRunner.RunAsync(BeadsCommand.CreateWrite(fixture.Path,
                ["create", "--title", $"retained event {index}", "--type", "task", "--priority", "2", "--json"],
                "nightweaver:orchestrator"), TestContext.Current.CancellationToken);
            Assert.Equal(0, write.ExitCode);
        }

        var readEnvironment = BeadsCommand.CreateJournalBatch(fixture.Path, 0, 256).Environment;
        var existing = await processRunner.RunAsync(new ProcessRequest("bd",
            ["--readonly", "--sandbox", "events", "tail", "--json"], fixture.Path,
            readEnvironment, TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
        Assert.Equal(0, existing.ExitCode);
        var head = existing.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64())
            .Max();

        foreach (var arguments in new[]
        {
            new[] { "config", "set", "events-journal-retain-days", "0" },
            new[] { "config", "set", "events-journal-retain-rows", "0" },
            new[] { "events", "prune", "--before", (head + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }
        })
        {
            var write = await processRunner.RunAsync(BeadsCommand.CreateWrite(fixture.Path, arguments),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, write.ExitCode);
        }

        var truncated = await Assert.ThrowsAsync<BeadsJournalTruncatedException>(() =>
            client.ReadBatchAsync(repository, 0, 256, TestContext.Current.CancellationToken));
        Assert.Equal(head, truncated.Head);
    }

    [Fact]
    public async Task NativeBatchFailureKeepsTimerDiscoveryActiveAndResumesTheExactTargetCursor()
    {
        using var target = new BeadsFixture("target");
        using var decoy = new BeadsFixture("decoy");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var repository = new ValidatedTargetRepository(target.Path, "main");
        var runner = new SystemProcessRunner();
        // The fixture has one embedded store. Serialize its journal commands,
        // discovery and direct SQL writes just as the host serializes tracker
        // access; independent discovery does not require overlapping engines.
        using var trackerAccess = new SemaphoreSlim(1, 1);
        var requests = new List<ProcessRequest>();
        var client = new SerializedJournalClient(new BeadsJournalClient(runner, request =>
        {
            requests.Add(request);
            if (requests.Count == 2) throw new IOException("controlled batch launch failure");
            return StartTailWithDecoySelectors(request, decoy.Path);
        }), trackerAccess);
        await client.EnableAsync(repository, lifetime.Token);
        var seed = target.Create("target seed");
        var foreign = decoy.Create("foreign seed");
        var firstHead = await ReadJournalHeadAsync(runner, target.Path, lifetime.Token);
        var ticks = Channel.CreateUnbounded<bool>();
        var delays = Channel.CreateUnbounded<TimeSpan>();
        var releases = Channel.CreateUnbounded<bool>();
        var inventories = Channel.CreateUnbounded<BeadsInventory>();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var error = new StringWriter();
        var coordinator = new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(ContinuousPoolKind.Delivery, async token =>
            {
                await trackerAccess.WaitAsync(token);
                try { inventories.Writer.TryWrite(await new BeadsReader(runner).ReadSnapshotAsync(repository, token)); }
                finally { trackerAccess.Release(); }
                return Array.Empty<DeliveryPoolDiagnostic>();
            }, completions.Reader, static () => { }, static _ => Task.CompletedTask)],
            static _ => Task.CompletedTask, TextWriter.Null, error,
            token => ReadTicksAsync(ticks.Reader, token),
            (requestDiscovery, token) => new BeadsJournalWatcher(client, requestDiscovery, error,
                async (delay, ct) => { delays.Writer.TryWrite(delay); await releases.Reader.ReadAsync(ct); })
                .RunAsync(repository, token));
        var runtime = coordinator.RunAsync(lifetime.Token);
        try
        {
            await WaitForInventoryAsync(inventories.Reader, inventory => inventory.ById.ContainsKey(seed), lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.Equal(BeadsJournalWatcher.PollInterval, await delays.Reader.ReadAsync(lifetime.Token));
            releases.Writer.TryWrite(true);
            Assert.Equal(TimeSpan.FromSeconds(1), await delays.Reader.ReadAsync(lifetime.Token));
            string unjournaled;
            await trackerAccess.WaitAsync(lifetime.Token);
            try
            {
                unjournaled = await InsertUnjournaledBeadAsync(runner, target.Path, lifetime.Token);
                Assert.Equal(firstHead, await ReadJournalHeadAsync(runner, target.Path, lifetime.Token));
            }
            finally { trackerAccess.Release(); }
            ticks.Writer.TryWrite(true);
            var inventory = await WaitForInventoryAsync(inventories.Reader,
                snapshot => snapshot.ById.ContainsKey(unjournaled), lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.DoesNotContain(foreign, inventory.ById.Keys);
            string authored;
            await trackerAccess.WaitAsync(lifetime.Token);
            try { authored = await CreateBeadAsync(runner, target.Path, "later journal write", "nightweaver:orchestrator", lifetime.Token); }
            finally { trackerAccess.Release(); }
            releases.Writer.TryWrite(true);
            Assert.Equal(BeadsJournalWatcher.PollInterval, await delays.Reader.ReadAsync(lifetime.Token));
            await WaitForInventoryAsync(inventories.Reader, snapshot => snapshot.ById.ContainsKey(authored), lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.Equal(3, requests.Count);
            Assert.Equal("0", ReadSinceArgument(requests[0]));
            Assert.Equal(firstHead.ToString(System.Globalization.CultureInfo.InvariantCulture), ReadSinceArgument(requests[1]));
            Assert.Equal(ReadSinceArgument(requests[1]), ReadSinceArgument(requests[2]));
            Assert.All(requests, request =>
            {
                Assert.Equal(target.Path, request.WorkingDirectory);
                Assert.Null(request.Environment["BEADS_DIR"]);
                Assert.Null(request.Environment["BEADS_DB"]);
                Assert.Null(request.Environment["BD_DB"]);
                Assert.DoesNotContain("--follow", request.Arguments);
            });
            Assert.Contains("events batch failed; retrying", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            lifetime.Cancel();
            await runtime.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            TestContext.Current.TestOutputHelper?.WriteLine(error.ToString());
        }
    }

    private sealed class SerializedJournalClient(IBeadsJournalClient inner, SemaphoreSlim access) : IBeadsJournalClient
    {
        public async Task EnableAsync(ValidatedTargetRepository repository, CancellationToken token)
        {
            await access.WaitAsync(token);
            try { await inner.EnableAsync(repository, token); }
            finally { access.Release(); }
        }

        public async Task<IReadOnlyList<long>> ReadBatchAsync(ValidatedTargetRepository repository, long cursor, int count,
            CancellationToken token)
        {
            await access.WaitAsync(token);
            try { return await inner.ReadBatchAsync(repository, cursor, count, token); }
            finally { access.Release(); }
        }
    }

    private static Process StartTailWithDecoySelectors(ProcessRequest request, string decoyPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        // Simulate a host that has ambient selectors aimed at another prepared tracker.
        startInfo.Environment["BEADS_DIR"] = Path.Combine(decoyPath, ".beads");
        startInfo.Environment["BEADS_DB"] = Path.Combine(decoyPath, ".beads", "beads.db");
        startInfo.Environment["BD_DB"] = Path.Combine(decoyPath, ".beads", "other-db");
        foreach (var (name, value) in request.Environment)
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo };
        if (!FixtureProcess.Start(process))
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start native journal tail fixture.");
        }
        process.StandardInput.Close();
        return process;
    }

    private static async Task<string> CreateBeadAsync(
        SystemProcessRunner processRunner,
        string repositoryPath,
        string title,
        string actor,
        CancellationToken cancellationToken)
    {
        var request = BeadsCommand.CreateWrite(repositoryPath,
            ["create", "--title", title, "--type", "task", "--priority", "2", "--json"], actor);
        var result = await processRunner.RunAsync(request, cancellationToken);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> InsertUnjournaledBeadAsync(
        SystemProcessRunner processRunner,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var id = $"fixture-timer-{Guid.NewGuid():N}";
        var title = "target row without journal event";
        var query = $"INSERT INTO issues (id,title,description,design,acceptance_criteria,notes,status,priority,issue_type,created_by) "
            + $"VALUES ('{id}','{title}','','','','','open',2,'task','fixture')";
        var environment = BeadsCommand.CreateJournalBatch(repositoryPath, 0, 256).Environment;
        var request = new ProcessRequest("dolt",
            ["--data-dir", Path.Combine(repositoryPath, ".beads", "embeddeddolt"), "sql", "-q", query],
            repositoryPath, environment, TimeSpan.FromSeconds(30));
        var result = await processRunner.RunAsync(request, cancellationToken);
        Assert.True(result.ExitCode == 0,
            $"Unjournaled native SQL insert exited {result.ExitCode}. stdout={result.StandardOutput}; stderr={result.StandardError}");
        return id;
    }

    private static async Task<long> ReadJournalHeadAsync(
        SystemProcessRunner processRunner,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var environment = BeadsCommand.CreateJournalBatch(repositoryPath, 0, 256).Environment;
        var result = await processRunner.RunAsync(new ProcessRequest("bd",
            ["--readonly", "--sandbox", "events", "tail", "--json"],
            repositoryPath, environment, TimeSpan.FromSeconds(30)), cancellationToken);
        Assert.Equal(0, result.ExitCode);
        long head = 0;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            using var record = JsonDocument.Parse(line);
            head = Math.Max(head, record.RootElement.GetProperty("seq").GetInt64());
        }
        return head;
    }

    private static string ReadSinceArgument(ProcessRequest request)
    {
        for (var index = 0; index < request.Arguments.Count - 1; index++)
        {
            if (request.Arguments[index] == "--since") return request.Arguments[index + 1];
        }
        throw new InvalidDataException("The native journal tail request is missing its --since cursor.");
    }

    private static async Task<BeadsInventory> WaitForInventoryAsync(
        ChannelReader<BeadsInventory> inventories,
        Func<BeadsInventory, bool> matches,
        CancellationToken cancellationToken)
    {
        await foreach (var inventory in inventories.ReadAllAsync(cancellationToken))
        {
            if (matches(inventory)) return inventory;
        }
        throw new InvalidOperationException("The discovery inventory stream ended before the expected tracker state was observed.");
    }

    private static async IAsyncEnumerable<bool> ReadTicksAsync(
        ChannelReader<bool> ticks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var tick in ticks.ReadAllAsync(cancellationToken))
            yield return tick;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

}
