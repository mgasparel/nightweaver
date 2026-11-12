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
    public async Task NativeJournalEnablementStreamsAnOrchestratorAuthoredWriteWhileTheTailRemainsOpen()
    {
        using var fixture = new BeadsFixture();
        using var lifetime = new CancellationTokenSource();
        var repository = new ValidatedTargetRepository(fixture.Path, "main");
        var processRunner = new SystemProcessRunner();
        var client = new BeadsJournalClient(processRunner);

        await client.EnableAsync(repository, TestContext.Current.CancellationToken);

        var tailEnvironment = BeadsCommand.CreateJournalTail(fixture.Path, 0).Environment;
        var config = await processRunner.RunAsync(new ProcessRequest("bd",
            ["--readonly", "--sandbox", "config", "get", "events-journal"], fixture.Path,
            tailEnvironment, TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
        Assert.Equal(0, config.ExitCode);
        Assert.Equal("true", config.StandardOutput.Trim());

        var sequences = Channel.CreateUnbounded<long>();
        var follow = client.FollowAsync(repository, 0,
            sequence => sequences.Writer.TryWrite(sequence), lifetime.Token);
        try
        {
            var title = "native write while journal tail is open";
            var write = await processRunner.RunAsync(BeadsCommand.CreateWrite(fixture.Path,
                ["create", "--title", title, "--type", "task", "--priority", "2", "--json"],
                "nightweaver:orchestrator"), TestContext.Current.CancellationToken);

            Assert.Equal(0, write.ExitCode);
            var sequence = await sequences.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.True(sequence > 0);
            Assert.False(follow.IsCompleted, "The native follow process must remain open after a write.");

            var records = await processRunner.RunAsync(new ProcessRequest("bd",
                ["--readonly", "--sandbox", "events", "tail", "--json"], fixture.Path,
                tailEnvironment, TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
            Assert.Equal(0, records.ExitCode);
            var eventRows = records.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            var authoredRecord = Assert.Single(eventRows, row => row.GetProperty("seq").GetInt64() == sequence);
            Assert.Equal("nightweaver:orchestrator", authoredRecord.GetProperty("actor").GetString());
            Assert.Equal(title, authoredRecord.GetProperty("issue").GetProperty("title").GetString());
        }
        finally
        {
            lifetime.Cancel();
            try { await follow; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
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

        var readEnvironment = BeadsCommand.CreateJournalTail(fixture.Path, 0).Environment;
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
            client.FollowAsync(repository, 0, static _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal(head, truncated.Head);
    }

    [Fact]
    public async Task NativeReconnectKeepsTimerDiscoveryActiveAndResumesTargetTrackerWakeups()
    {
        using var target = new BeadsFixture("target");
        using var decoy = new BeadsFixture("decoy");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var repository = new ValidatedTargetRepository(target.Path, "main");
        var processRunner = new SystemProcessRunner();
        var enableClient = new BeadsJournalClient(processRunner);
        await enableClient.EnableAsync(repository, TestContext.Current.CancellationToken);
        await enableClient.EnableAsync(
            new ValidatedTargetRepository(decoy.Path, "main"), TestContext.Current.CancellationToken);

        var seedId = target.Create("target tracker seed");
        var decoyOnlyId = decoy.Create("decoy tracker seed");
        var firstHead = await ReadJournalHeadAsync(processRunner, target.Path, TestContext.Current.CancellationToken);
        Assert.True(firstHead > 0);

        var tailProcesses = Channel.CreateUnbounded<Process>();
        var tailRequests = Channel.CreateUnbounded<ProcessRequest>();
        var client = new BeadsJournalClient(processRunner, request =>
        {
            tailRequests.Writer.TryWrite(request);
            var process = StartTailWithDecoySelectors(request, decoy.Path);
            tailProcesses.Writer.TryWrite(process);
            return process;
        });
        var recordingClient = new RecordingJournalClient(client);

        var reconnectDelayEntered = NewSignal();
        var allowReconnect = NewSignal();
        var discoveryTicks = Channel.CreateUnbounded<bool>();
        var inventories = Channel.CreateUnbounded<BeadsInventory>();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var error = new StringWriter();
        var reader = new BeadsReader(processRunner);
        var runtime = new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(
                ContinuousPoolKind.Delivery,
                async cancellationToken =>
                {
                    var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken);
                    inventories.Writer.TryWrite(inventory);
                    return Array.Empty<DeliveryPoolDiagnostic>();
                },
                completions.Reader,
                static () => { },
                static _ => Task.CompletedTask)],
            static _ => Task.CompletedTask,
            TextWriter.Null,
            error,
            token => ReadTicksAsync(discoveryTicks.Reader, token),
            (requestDiscovery, token) => new BeadsJournalWatcher(
                recordingClient,
                requestDiscovery,
                error,
                (_, delayToken) =>
                {
                    reconnectDelayEntered.TrySetResult(true);
                    return allowReconnect.Task.WaitAsync(delayToken);
                }).RunAsync(repository, token));

        var runtimeTask = runtime.RunAsync(lifetime.Token);
        try
        {
            var initial = await WaitForInventoryAsync(inventories.Reader,
                inventory => inventory.ById.ContainsKey(seedId), lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.DoesNotContain(decoyOnlyId, initial.ById.Keys);

            var firstTail = await tailProcesses.Reader.ReadAsync(lifetime.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            var firstRequest = await tailRequests.Reader.ReadAsync(lifetime.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            var deliveredCursor = await recordingClient.FirstDeliveredSequence
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.Equal(target.Path, firstRequest.WorkingDirectory);
            Assert.Null(firstRequest.Environment["BEADS_DIR"]);
            Assert.Null(firstRequest.Environment["BEADS_DB"]);
            Assert.Null(firstRequest.Environment["BD_DB"]);
            Assert.Equal("0", ReadSinceArgument(firstRequest));
            Assert.True(deliveredCursor > 0, "The native tail must deliver an event before it is interrupted.");
            Assert.False(firstTail.HasExited, "The initial native journal tail should remain open.");
            var firstTailProcessId = firstTail.Id;
            using (var processToStop = Process.GetProcessById(firstTailProcessId))
                processToStop.Kill(entireProcessTree: true);

            await reconnectDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);

            var unjournaledId = await InsertUnjournaledBeadAsync(processRunner, target.Path, lifetime.Token);
            Assert.Equal(firstHead,
                await ReadJournalHeadAsync(processRunner, target.Path, lifetime.Token));

            discoveryTicks.Writer.TryWrite(true);
            var timerInventory = await WaitForInventoryAsync(inventories.Reader,
                inventory => inventory.ById.ContainsKey(unjournaledId), lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.DoesNotContain(decoyOnlyId, timerInventory.ById.Keys);
            Assert.False(allowReconnect.Task.IsCompleted,
                "The timer must discover the unjournaled write while the journal client is reconnecting.");

            allowReconnect.TrySetResult(true);
            var secondTail = await tailProcesses.Reader.ReadAsync(lifetime.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.False(secondTail.HasExited, "The reconnected native tail should remain open for new writes.");
            var secondRequest = await tailRequests.Reader.ReadAsync(lifetime.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            Assert.Equal(target.Path, secondRequest.WorkingDirectory);
            Assert.True(long.Parse(ReadSinceArgument(secondRequest),
                System.Globalization.CultureInfo.InvariantCulture) >= deliveredCursor,
                "Reconnect should resume at or after the event cursor observed before termination.");

            var burstIds = new[]
            {
                await CreateBeadAsync(processRunner, target.Path, "orchestrator burst one", "nightweaver:orchestrator",
                    lifetime.Token),
                await CreateBeadAsync(processRunner, target.Path, "orchestrator burst two", "nightweaver:orchestrator",
                    lifetime.Token)
            };
            var burstInventory = await WaitForInventoryAsync(inventories.Reader,
                inventory => burstIds.All(inventory.ById.ContainsKey), lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Assert.All(burstIds, id => Assert.Contains(id, burstInventory.ById.Keys));
            Assert.DoesNotContain(decoyOnlyId, burstInventory.ById.Keys);
            Assert.Contains("Target Repository events tail failed; reconnecting", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            allowReconnect.TrySetResult(true);
            lifetime.Cancel();
            await runtimeTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
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
        if (!process.Start())
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
        var environment = BeadsCommand.CreateJournalTail(repositoryPath, 0).Environment;
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
        var environment = BeadsCommand.CreateJournalTail(repositoryPath, 0).Environment;
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

    private sealed class RecordingJournalClient(IBeadsJournalClient inner) : IBeadsJournalClient
    {
        private readonly TaskCompletionSource<long> firstDeliveredSequence =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<long> FirstDeliveredSequence => firstDeliveredSequence.Task;

        public Task EnableAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken) =>
            inner.EnableAsync(repository, cancellationToken);

        public Task FollowAsync(ValidatedTargetRepository repository, long afterSequence,
            Action<long> onEvent, CancellationToken cancellationToken) =>
            inner.FollowAsync(repository, afterSequence, sequence =>
            {
                onEvent(sequence);
                firstDeliveredSequence.TrySetResult(sequence);
            }, cancellationToken);
    }
}
