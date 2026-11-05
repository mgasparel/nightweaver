using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

public sealed class FactoryTelemetryIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task PublisherPersistsStableActualSlotsAndReaderReportsStoppedRuntime()
    {
        using var directory = new TempDirectory();
        var host = new HostPaths(directory.CreateDirectory("host"));
        var targetPath = directory.CreateDirectory("target");
        var project = CreateProject(host, targetPath, "telemetry-test", capacity: 2);
        await using var publisher = new FactoryTelemetryPublisher(host, project,
            new ValidatedTargetRepository(targetPath, "main"), 2, 1);
        await publisher.StartAsync(TestContext.Current.CancellationToken);

        var reader = new FactoryTelemetryReader(host, project);
        var initial = Assert.IsType<FactoryTelemetrySnapshot>(reader.Read().Snapshot);
        Assert.Equal(2, initial.Delivery.Capacity);
        Assert.Equal(["delivery-1", "delivery-2"], initial.Delivery.Slots.Select(slot => slot.SlotId));
        Assert.Equal("live", initial.RuntimeStatus);

        var slotId = publisher.SlotForReservation("delivery", "ns-test.1");
        Assert.Equal("delivery-1", slotId);
        publisher.Reserve("delivery", slotId, "ns-test.1");
        publisher.UpdateSlot("delivery", slotId, "preparing", "ns-test.1",
            "stage:implement", "implementer", worktreePath: Path.Combine(host.WorktreesRoot(project.Name), "ns-test.1"),
            repairCycles: 1, maxRepairCycles: 3);

        var observed = await WaitForSnapshotAsync(reader,
            snapshot => snapshot.Delivery.Slots[0].Status == "preparing",
            TestContext.Current.CancellationToken);
        Assert.Equal("implementer", observed.Delivery.Slots[0].Role);
        Assert.Equal(1, observed.Delivery.Slots[0].RepairCycles);

        await publisher.DisposeAsync();
        Assert.Equal(FactoryTelemetryAvailability.Stopped, reader.Read().Availability);

        var stoppedSnapshot = Assert.IsType<FactoryTelemetrySnapshot>(reader.Read().Snapshot);
        var staleSnapshot = stoppedSnapshot with
        {
            RuntimeStatus = "live",
            ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-20)
        };
        await File.WriteAllBytesAsync(host.FactoryTelemetryPath(project.Name),
            JsonSerializer.SerializeToUtf8Bytes(staleSnapshot, JsonOptions), TestContext.Current.CancellationToken);
        var stale = reader.Read();
        Assert.Equal(FactoryTelemetryAvailability.Stale, stale.Availability);
        Assert.Equal("heartbeat-expired", stale.Reason);
    }

    [Fact]
    public async Task WebStateAndSseConsumeThePersistedPublisherContract()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        await using var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);

        using var initialResponse = await client.GetAsync("/api/factory/state", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        Assert.Equal("no-store", initialResponse.Headers.CacheControl?.ToString());
        using var initialJson = JsonDocument.Parse(await initialResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var initialSnapshot = initialJson.RootElement.GetProperty("snapshot");
        Assert.Equal("live", initialJson.RootElement.GetProperty("availability").GetString());
        var runtimeId = initialSnapshot.GetProperty("runtime_id").GetString()!;
        Assert.Equal("web-test", initialSnapshot.GetProperty("project").GetString());
        Assert.Equal(3, initialSnapshot.GetProperty("delivery").GetProperty("capacity").GetInt32());

        var badState = await client.GetAsync("/api/factory/state?unexpected=1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
        badState.Dispose();

        const string beadId = "ns-test.1";
        const string invocationId = "20260301T0102030000000Z-implementer";
        publisher.Reserve("delivery", "delivery-1", beadId);
        publisher.UpdateSlot("delivery", "delivery-1", "preparing", beadId,
            "stage:implement", "implementer");
        publisher.SetInvocation("delivery", "delivery-1", new FactoryInvocationObservation(
            beadId, invocationId, "implementer", "codex", "gpt-test", "high", "running",
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(5), 1234, null));
        var logPath = Path.Combine(host.LogsRoot(project.Name), beadId, invocationId + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, "marker-before-exit\n", new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        publisher.FinishInvocation("delivery", "delivery-1", invocationId, "completed", 0);
        await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
            snapshot => snapshot.Delivery.Slots[0].Invocation?.Status == "completed",
            TestContext.Current.CancellationToken);

        var query = $"runtime_id={runtimeId}&slot_id=delivery-1&invocation_id={invocationId}";
        using var response = await client.GetAsync("/api/telemetry/stream?" + query,
            HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var lines = new StreamReader(body, Encoding.UTF8);
        var captured = new StringBuilder();
        var sawEnd = false;
        while (captured.Length < 32 * 1024)
        {
            var line = await lines.ReadLineAsync(deadline.Token);
            if (line is null) break;
            captured.AppendLine(line);
            if (line == "event: end") sawEnd = true;
            if (sawEnd && line.Length == 0) break;
        }
        Assert.True(sawEnd, captured.ToString());
        Assert.Contains("event: log", captured.ToString(), StringComparison.Ordinal);
        Assert.Contains("marker-before-exit", captured.ToString(), StringComparison.Ordinal);

        var invalid = await client.GetAsync("/api/telemetry/stream?runtime_id=bad&slot_id=delivery-1&invocation_id=bad",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        invalid.Dispose();
    }

    [Fact]
    public async Task TwoNativeSlotsStreamOnlyTheirOwnOutputBeforeExit()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        var promptRoot = Directory.CreateDirectory(Path.Combine(host.Root, "prompts"));
        await File.WriteAllTextAsync(Path.Combine(promptRoot.FullName, "implementer.md"), "Fixture prompt.",
            TestContext.Current.CancellationToken);
        await using var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        var reader = factory.Services.GetRequiredService<FactoryTelemetryReader>();

        const string firstBead = "ns-test.1";
        const string secondBead = "ns-test.2";
        const string firstSlot = "delivery-1";
        const string secondSlot = "delivery-2";
        publisher.Reserve("delivery", firstSlot, firstBead);
        publisher.Reserve("delivery", secondSlot, secondBead);
        var preparer = new AgentInvocationPreparer();
        var firstPrepared = await PrepareFixtureInvocationAsync(preparer, host, project, publisher,
            firstSlot, firstBead);
        var secondPrepared = await PrepareFixtureInvocationAsync(preparer, host, project, publisher,
            secondSlot, secondBead);

        var firstReady = Path.Combine(factory.TargetRoot, "first-ready.txt");
        var secondReady = Path.Combine(factory.TargetRoot, "second-ready.txt");
        var firstRelease = Path.Combine(factory.TargetRoot, "first-release.txt");
        var secondRelease = Path.Combine(factory.TargetRoot, "second-release.txt");
        firstPrepared = WithFixtureArguments(firstPrepared, firstReady, firstRelease, "first-marker");
        secondPrepared = WithFixtureArguments(secondPrepared, secondReady, secondRelease, "second-marker");

        async Task<AgentExecutionResult> RunAsync(string slot, string bead, PreparedAgentInvocation invocation)
        {
            using var context = ContinuousAgentLaunchContext.Enter(null, publisher, "delivery", slot, bead);
            return await AgentInvocationExecutor.ExecuteAsync(host, project.Name, invocation,
                TestContext.Current.CancellationToken);
        }

        var firstExecution = Task.Run(() => RunAsync(firstSlot, firstBead, firstPrepared),
            TestContext.Current.CancellationToken);
        var secondExecution = Task.Run(() => RunAsync(secondSlot, secondBead, secondPrepared),
            TestContext.Current.CancellationToken);
        try
        {
            await WaitForFileAsync(firstReady, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await WaitForFileAsync(secondReady, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var snapshot = await WaitForSnapshotAsync(reader, value =>
                value.Delivery.Slots.Count(slot => slot.Invocation?.Status == "running") == 2,
                TestContext.Current.CancellationToken);
            var firstInvocation = snapshot.Delivery.Slots.Single(slot => slot.SlotId == firstSlot).Invocation!;
            var secondInvocation = snapshot.Delivery.Slots.Single(slot => slot.SlotId == secondSlot).Invocation!;

            using var firstResponse = await client.GetAsync(StreamUrl(snapshot.RuntimeId, firstSlot, firstInvocation.InvocationId),
                HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            using var secondResponse = await client.GetAsync(StreamUrl(snapshot.RuntimeId, secondSlot, secondInvocation.InvocationId),
                HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            using var firstReader = new StreamReader(await firstResponse.Content.ReadAsStreamAsync(deadline.Token), Encoding.UTF8);
            using var secondReader = new StreamReader(await secondResponse.Content.ReadAsStreamAsync(deadline.Token), Encoding.UTF8);
            var firstMarker = await ReadLogMarkerAsync(firstReader, "first-marker", "second-marker", deadline.Token);
            var secondMarker = await ReadLogMarkerAsync(secondReader, "second-marker", "first-marker", deadline.Token);
            Assert.Contains("first-marker", firstMarker, StringComparison.Ordinal);
            Assert.Contains("second-marker", secondMarker, StringComparison.Ordinal);
            Assert.False(firstExecution.IsCompleted);
            Assert.False(secondExecution.IsCompleted);

            await File.WriteAllTextAsync(firstRelease, "release", deadline.Token);
            await File.WriteAllTextAsync(secondRelease, "release", deadline.Token);
            await ReadEndAsync(firstReader, deadline.Token);
            await ReadEndAsync(secondReader, deadline.Token);
            var firstResult = await firstExecution.WaitAsync(deadline.Token);
            var secondResult = await secondExecution.WaitAsync(deadline.Token);
            Assert.Equal(AgentExecutionStatus.Exited, firstResult.Status);
            Assert.Equal(AgentExecutionStatus.Exited, secondResult.Status);
        }
        finally
        {
            await File.WriteAllTextAsync(firstRelease, "release", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(secondRelease, "release", TestContext.Current.CancellationToken);
            await ObserveExecutionAsync(firstExecution);
            await ObserveExecutionAsync(secondExecution);
            await publisher.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReconnectFromLastSafeCursorReplaysSplitUtf8Codepoint()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        var promptRoot = Directory.CreateDirectory(Path.Combine(host.Root, "prompts"));
        await File.WriteAllTextAsync(Path.Combine(promptRoot.FullName, "implementer.md"), "Fixture prompt.",
            TestContext.Current.CancellationToken);
        await using var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);

        const string beadId = "ns-utf8.1";
        const string slot = "delivery-1";
        publisher.Reserve("delivery", slot, beadId);
        var prepared = await PrepareFixtureInvocationAsync(new AgentInvocationPreparer(), host, project,
            publisher, slot, beadId);
        var ready = Path.Combine(factory.TargetRoot, "split-ready.txt");
        var release = Path.Combine(factory.TargetRoot, "split-release.txt");
        prepared = WithSplitUtf8Arguments(prepared, ready, release);
        var logPath = prepared.LogFilePath;

        async Task<AgentExecutionResult> RunAsync()
        {
            using var context = ContinuousAgentLaunchContext.Enter(null, publisher, "delivery", slot, beadId);
            return await AgentInvocationExecutor.ExecuteAsync(host, project.Name, prepared,
                TestContext.Current.CancellationToken);
        }

        var execution = Task.Run(RunAsync, TestContext.Current.CancellationToken);
        try
        {
            await WaitForFileAsync(ready, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var snapshot = await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
                value => value.Delivery.Slots.Single(item => item.SlotId == slot).Invocation?.Status == "running",
                TestContext.Current.CancellationToken);
            var invocation = snapshot.Delivery.Slots.Single(item => item.SlotId == slot).Invocation!;
            var query = StreamUrl(snapshot.RuntimeId, slot, invocation.InvocationId);

            string lastEventId;
            using (var firstRequest = new HttpRequestMessage(HttpMethod.Get, query))
            using (var firstResponse = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead,
                       TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
                await using var firstBody = await firstResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
                using var firstReader = new StreamReader(firstBody, Encoding.UTF8);
                var firstLog = await ReadLogEventAsync(firstReader, TestContext.Current.CancellationToken);
                Assert.Equal("prefix-", firstLog.Text);
                Assert.DoesNotContain("\uFFFD", firstLog.Text, StringComparison.Ordinal);
                lastEventId = firstLog.Id;
                Assert.Equal(Encoding.UTF8.GetByteCount("prefix-"), ParseCursor(lastEventId));
            }

            var initialLog = await WaitForLogLengthAsync(logPath, Encoding.UTF8.GetByteCount("prefix-") + 2,
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(Encoding.UTF8.GetByteCount("prefix-") + 2, initialLog);
            Assert.False(execution.IsCompleted, "The native Agent must remain alive while its UTF-8 sequence is split across output writes.");

            using var reconnectRequest = new HttpRequestMessage(HttpMethod.Get, query);
            reconnectRequest.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
            using var reconnectResponse = await client.SendAsync(reconnectRequest, HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, reconnectResponse.StatusCode);
            await using var reconnectBody = await reconnectResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            using var reconnectReader = new StreamReader(reconnectBody, Encoding.UTF8);
            await File.WriteAllTextAsync(release, "release", TestContext.Current.CancellationToken);
            var resumedLog = await ReadLogEventAsync(reconnectReader, TestContext.Current.CancellationToken);
            Assert.Equal("€-suffix\n", resumedLog.Text);
            Assert.DoesNotContain("prefix-", resumedLog.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\uFFFD", resumedLog.Text, StringComparison.Ordinal);
            Assert.Equal(Encoding.UTF8.GetByteCount("prefix-€-suffix\n"), ParseCursor(resumedLog.Id));
            await ReadEndAsync(reconnectReader, TestContext.Current.CancellationToken);
            Assert.Equal(AgentExecutionStatus.Exited, (await execution.WaitAsync(TestContext.Current.CancellationToken)).Status);
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            await ObserveExecutionAsync(execution);
            await publisher.DisposeAsync();
        }
    }

    private static async Task<PreparedAgentInvocation> PrepareFixtureInvocationAsync(AgentInvocationPreparer preparer,
        HostPaths host, ProjectConfig project, FactoryTelemetryPublisher publisher,
        string slot, string beadId)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(host.WorktreesRoot(project.Name), beadId)).FullName;
        publisher.UpdateSlot("delivery", slot, "preparing", beadId,
            "stage:implement", "implementer", worktreePath: worktree, repairCycles: 0,
            maxRepairCycles: project.Limits.MaxRepairCycles);
        using var context = ContinuousAgentLaunchContext.Enter(null, publisher, "delivery", slot, beadId);
        return await preparer.PrepareAsync(host, project, AgentRole.Implementer,
            new AgentDispatchContext(beadId, "task", "stage:implement", 0), worktree,
            TestContext.Current.CancellationToken);
    }

    private static PreparedAgentInvocation WithFixtureArguments(PreparedAgentInvocation prepared,
        string ready, string release, string marker)
        => new("dotnet", [typeof(FixtureMarker).Assembly.Location, "live-marker", ready, release, marker],
            prepared.WorkingDirectory, prepared.StandardInput, prepared.EnvironmentOverrides,
            TimeSpan.FromSeconds(25), prepared.InvocationId, prepared.TemporaryDirectory,
            prepared.LogFilePath, prepared.Purpose, prepared.Stage, prepared.RepairCycles,
            prepared.MaxRepairCycles, prepared.ConfiguredModel, prepared.Effort);

    private static PreparedAgentInvocation WithSplitUtf8Arguments(PreparedAgentInvocation prepared,
        string ready, string release)
        => new("dotnet", [typeof(FixtureMarker).Assembly.Location, "live-split-utf8", ready, release],
            prepared.WorkingDirectory, prepared.StandardInput, prepared.EnvironmentOverrides,
            TimeSpan.FromSeconds(25), prepared.InvocationId, prepared.TemporaryDirectory,
            prepared.LogFilePath, prepared.Purpose, prepared.Stage, prepared.RepairCycles,
            prepared.MaxRepairCycles, prepared.ConfiguredModel, prepared.Effort);

    private static string StreamUrl(string runtimeId, string slotId, string invocationId)
        => "/api/telemetry/stream?runtime_id=" + runtimeId + "&slot_id=" + slotId + "&invocation_id=" + invocationId;

    private static async Task<string> ReadLogMarkerAsync(StreamReader reader, string expectedMarker,
        string forbiddenMarker, CancellationToken cancellationToken)
    {
        var eventType = string.Empty;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("The telemetry stream ended before its expected log marker.");
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line[7..];
                continue;
            }
            if (eventType != "log" || !line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var json = JsonDocument.Parse(line[6..]);
            var text = json.RootElement.GetProperty("text").GetString() ?? string.Empty;
            Assert.DoesNotContain(forbiddenMarker, text, StringComparison.Ordinal);
            if (text.Contains(expectedMarker, StringComparison.Ordinal)) return text;
        }
    }

    private static async Task ReadEndAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var sawEnd = false;
        // Read through clean EOF: receiving the terminal event alone can hide a
        // request-handler exception that aborts the response immediately afterward.
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line == "event: end") sawEnd = true;
        }
        Assert.True(sawEnd, "The telemetry stream closed before its terminal event.");
    }

    private static async Task<(string Id, string Text)> ReadLogEventAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        var eventName = string.Empty;
        var eventId = string.Empty;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("The telemetry stream ended before its expected log event.");
            if (line.Length == 0)
            {
                if (eventName != "log")
                {
                    eventName = string.Empty;
                    eventId = string.Empty;
                    data.Clear();
                    continue;
                }
                using var json = JsonDocument.Parse(data.ToString());
                return (eventId, json.RootElement.GetProperty("text").GetString() ?? string.Empty);
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];
            else if (line.StartsWith("id: ", StringComparison.Ordinal)) eventId = line[4..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data.Append(line[6..]);
        }
    }

    private static long ParseCursor(string eventId)
    {
        var separator = eventId.LastIndexOf('.');
        long cursor = -1;
        Assert.True(separator >= 0 && long.TryParse(eventId.AsSpan(separator + 1), out cursor),
            $"SSE event ID '{eventId}' does not contain a numeric cursor.");
        return cursor;
    }

    private static async Task<long> WaitForLogLengthAsync(string path, long minimumLength,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (length >= minimumLength) return length;
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException("Native Agent log did not expose the expected split UTF-8 prefix.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Native fixture did not reach its output gate.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task ObserveExecutionAsync(Task<AgentExecutionResult> task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken); }
        catch (Exception) { }
    }

    private static ProjectConfig CreateProject(HostPaths paths, string targetPath, string projectName, int capacity)
    {
        var slug = ProjectSlug.Parse(projectName);
        var roles = AgentRoles.All.ToDictionary(role => role,
            _ => new AgentSettings(AgentCli.Codex, null, "low", 5));
        return new ProjectConfig(slug, paths.ProjectConfigPath(slug),
            new TargetRepositoryConfig(targetPath, "main"),
            new ConcurrencyConfig(1, capacity, 1), new LimitsConfig(3, 2),
            new AgentSettings(AgentCli.Codex, null, "low", 5), new RoleSettings(roles));
    }

    private static async Task<FactoryTelemetrySnapshot> WaitForSnapshotAsync(FactoryTelemetryReader reader,
        Func<FactoryTelemetrySnapshot, bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Read().Snapshot is { } snapshot && condition(snapshot)) return snapshot;
            await Task.Delay(25, cancellationToken);
        }
        throw new TimeoutException("Factory telemetry did not publish the expected lifecycle update.");
    }
}
