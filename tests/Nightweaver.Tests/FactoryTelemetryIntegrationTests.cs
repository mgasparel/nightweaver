using System.Net;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
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
        Assert.Equal(2, initial.SchemaVersion);
        Assert.Equal(project.Concurrency.MaxConcurrentPlanning, initial.Planning?.Capacity);
        Assert.All(initial.Planning!.Slots, slot => Assert.Equal("idle", slot.Status));
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
    public async Task LegacySnapshotsKeepPlanningUnavailableAndVersionTwoRequiresIt()
    {
        using var factory = new NightweaverWebApplicationFactory();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var publisher = new FactoryTelemetryPublisher(host, project,
            new(project.TargetRepository.Path, "main"), project.Concurrency.MaxConcurrentDelivery, 1);
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        var reader = factory.Services.GetRequiredService<FactoryTelemetryReader>();
        var current = reader.Read().Snapshot!;
        await publisher.DisposeAsync();
        foreach (var version in new[] { 1, 2 })
        {
            await File.WriteAllBytesAsync(host.FactoryTelemetryPath(project.Name),
                JsonSerializer.SerializeToUtf8Bytes(current with { SchemaVersion = version, Planning = null }, JsonOptions),
                TestContext.Current.CancellationToken);
            var result = reader.Read();
            Assert.Equal(version == 1 ? FactoryTelemetryAvailability.Live : FactoryTelemetryAvailability.Unavailable, result.Availability);
            Assert.Null(result.Snapshot?.Planning);
        }
    }

    [Fact]
    public async Task MalformedPersistedSnapshotsAreUnavailableToReaderAndHttp()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        const string beadId = "ns-null-check.1";
        const string invocationId = "20260301T0102030000000Z-malformed";
        publisher.Reserve("delivery", "delivery-1", beadId);
        publisher.SetInvocation("delivery", "delivery-1", new FactoryInvocationObservation(
            beadId, invocationId, "implementer", "codex", "gpt-test", "high", "running",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), 1234, null));
        var snapshot = await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
            value => value.Delivery.Slots[0].Invocation?.InvocationId == invocationId,
            TestContext.Current.CancellationToken);
        var baseline = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        await publisher.DisposeAsync();

        var mutations = new (string Name, Action<JsonObject> Apply)[]
        {
            ("null delivery", root => root["delivery"] = null),
            ("missing delivery", root => root.Remove("delivery")),
            ("null merger", root => root["merger"] = null),
            ("null slots", root => root["delivery"]!["slots"] = null),
            ("missing slots", root => root["delivery"]!.AsObject().Remove("slots")),
            ("null slot element", root => root["delivery"]!["slots"]![0] = null),
            ("null slot identity", root => root["delivery"]!["slots"]![0]!["slot_id"] = null),
            ("null invocation bead", root => root["delivery"]!["slots"]![0]!["invocation"]!["bead_id"] = null),
            ("missing invocation id", root => root["delivery"]!["slots"]![0]!["invocation"]!.AsObject().Remove("invocation_id")),
            ("null invocation purpose", root => root["delivery"]!["slots"]![0]!["invocation"]!["purpose"] = null),
            ("null invocation cli", root => root["delivery"]!["slots"]![0]!["invocation"]!["cli"] = null),
            ("null invocation effort", root => root["delivery"]!["slots"]![0]!["invocation"]!["effort"] = null),
            ("null invocation status", root => root["delivery"]!["slots"]![0]!["invocation"]!["status"] = null)
        };

        var reader = factory.Services.GetRequiredService<FactoryTelemetryReader>();
        foreach (var (name, apply) in mutations)
        {
            var malformed = baseline.DeepClone().AsObject();
            apply(malformed);
            await File.WriteAllTextAsync(host.FactoryTelemetryPath(project.Name), malformed.ToJsonString(),
                TestContext.Current.CancellationToken);
            var direct = reader.Read();
            Assert.True(direct.Availability == FactoryTelemetryAvailability.Unavailable && direct.Snapshot is null,
                $"The reader accepted {name}.");
            using var response = await client.GetAsync("/api/factory/state", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
            Assert.Equal("unavailable", json.RootElement.GetProperty("availability").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("snapshot").ValueKind);
        }
    }

    [Fact]
    public async Task PublisherDoesNotWriteThroughTelemetryDirectoryJunction()
    {
        Assert.True(OperatingSystem.IsWindows(), "Telemetry publication path pinning uses Windows no-follow handles.");
        using var directory = new TempDirectory();
        var hostRoot = directory.CreateDirectory("host");
        var host = new HostPaths(hostRoot);
        var targetPath = directory.CreateDirectory("target");
        var project = CreateProject(host, targetPath, "telemetry-junction", capacity: 1);
        var outside = directory.CreateDirectory("outside");
        var sentinel = Path.Combine(outside, "factory.json");
        await File.WriteAllTextAsync(sentinel, "outside-sentinel", TestContext.Current.CancellationToken);
        var telemetryParent = Directory.CreateDirectory(Path.Combine(hostRoot, "telemetry")).FullName;
        var telemetryRoot = host.TelemetryRoot(project.Name);
        CreateDirectoryJunction(telemetryRoot, outside);
        var failures = new List<string>();

        await using var publisher = new FactoryTelemetryPublisher(host, project,
            new ValidatedTargetRepository(targetPath, "main"), 1, 1, message => { failures.Add(message); });
        await publisher.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("outside-sentinel", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.NotEmpty(failures);
        Assert.Contains("could not publish its snapshot", failures[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(telemetryParent));
    }

    [Fact]
    public async Task PublisherDoesNotWriteThroughPreexistingTemporarySnapshotHardLink()
    {
        Assert.True(OperatingSystem.IsWindows(), "Telemetry publication path pinning uses Windows no-follow handles.");
        using var directory = new TempDirectory();
        var hostRoot = directory.CreateDirectory("host");
        var host = new HostPaths(hostRoot);
        var targetPath = directory.CreateDirectory("target");
        var project = CreateProject(host, targetPath, "telemetry-file-link", capacity: 1);
        Directory.CreateDirectory(host.TelemetryRoot(project.Name));
        var outside = directory.CreateDirectory("outside");
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "outside-sentinel", TestContext.Current.CancellationToken);
        var snapshotPath = host.FactoryTelemetryPath(project.Name);
        var failures = new List<string>();
        await using var publisher = new FactoryTelemetryPublisher(host, project,
            new ValidatedTargetRepository(targetPath, "main"), 1, 1, message => { failures.Add(message); });
        var temporaryPath = snapshotPath + "." + publisher.RuntimeId + ".tmp";
        CreateHardLink(temporaryPath, sentinel);

        await publisher.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("outside-sentinel", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.NotEmpty(failures);
        Assert.Contains("could not publish its snapshot", failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafeSnapshotDirectoryJunctionIsUnavailableToReaderAndHttp()
    {
        Assert.True(OperatingSystem.IsWindows(), "Factory snapshot paths are validated with Windows no-follow handles.");
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        await using var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        publisher.Reserve("delivery", "delivery-1", "sentinel-secret.1");
        await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
            snapshot => snapshot.Delivery.Slots[0].BeadId == "sentinel-secret.1",
            TestContext.Current.CancellationToken);
        await publisher.DisposeAsync();

        var telemetryRoot = host.TelemetryRoot(project.Name);
        var snapshotPath = host.FactoryTelemetryPath(project.Name);
        var outside = Directory.CreateDirectory(Path.Combine(factory.TargetRoot, "outside-snapshot")).FullName;
        var outsideSnapshot = Path.Combine(outside, "factory.json");
        File.Copy(snapshotPath, outsideSnapshot);
        DeleteTelemetryFilesAndDirectoryBeforeJunction(telemetryRoot);
        CreateDirectoryJunction(telemetryRoot, outside);

        await AssertUnsafeSnapshotUnavailableAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
            client, "sentinel-secret.1");
        Assert.True(File.Exists(outsideSnapshot));
        Assert.Contains("sentinel-secret.1", await File.ReadAllTextAsync(outsideSnapshot, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingTelemetryStreamEndsExplicitlyWhenSnapshotDirectoryBecomesUnsafe()
    {
        Assert.True(OperatingSystem.IsWindows(), "Factory snapshot paths are validated with Windows no-follow handles.");
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        await using var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        const string beadId = "unsafe-stream.1";
        const string invocationId = "20260301T0102030000000Z-implementer";
        publisher.Reserve("delivery", "delivery-1", beadId);
        publisher.SetInvocation("delivery", "delivery-1", new FactoryInvocationObservation(
            beadId, invocationId, "implementer", "codex", "gpt-test", "high", "running",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), 4321, null));
        var logPath = Path.Combine(host.LogsRoot(project.Name), beadId, invocationId + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, "unsafe-refresh-marker\n", new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var snapshot = await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
            value => value.Delivery.Slots[0].Invocation?.InvocationId == invocationId,
            TestContext.Current.CancellationToken);
        using var response = await client.GetAsync(StreamUrl(snapshot.RuntimeId, "delivery-1", invocationId),
            HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var firstEvent = await ReadNextSseEventAsync(reader, deadline.Token);
        Assert.Equal("status", firstEvent.Name);

        await publisher.DisposeAsync();
        var telemetryRoot = host.TelemetryRoot(project.Name);
        var outside = Directory.CreateDirectory(Path.Combine(factory.TargetRoot, "unsafe-refresh-target")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "factory.json"), "private-sentinel", deadline.Token);
        DeleteTelemetryFilesAndDirectoryBeforeJunction(telemetryRoot);
        CreateDirectoryJunction(telemetryRoot, outside);

        var observed = new List<CapturedSseEvent>();
        while (true)
        {
            var item = await ReadNextSseEventAsync(reader, deadline.Token);
            observed.Add(item);
            if (item.Name == "end") break;
        }
        var error = Assert.Single(observed, item => item.Name == "error");
        var end = Assert.Single(observed, item => item.Name == "end");
        Assert.Equal("runtime_lost", error.Data.GetProperty("reason").GetString());
        Assert.Equal("runtime_lost", end.Data.GetProperty("reason").GetString());
        Assert.DoesNotContain("private-sentinel", string.Join("\n", observed.Select(item => item.Data.ToString())),
            StringComparison.Ordinal);
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

        publisher.Reserve("planning", "planning-1", beadId);
        publisher.SetInvocation("planning", "planning-1", new FactoryInvocationObservation(beadId, invocationId,
            "planner", "codex", null, "high", "completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), 5678, 0));
        await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
            value => value.Planning?.Slots[0].Invocation?.ProcessId == 5678, TestContext.Current.CancellationToken);
        using var planningResponse = await client.GetAsync(StreamUrl(runtimeId, "planning-1", invocationId),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, planningResponse.StatusCode);
        Assert.Contains("marker-before-exit", await planningResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

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

    [Fact]
    public async Task ExplicitCursorsAreClampedToTheBoundedReplayWindow()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        await using var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);

        const string beadId = "ns-replay.1";
        const string slot = "delivery-1";
        const string invocationId = "20260301T0102030000000Z-implementer";
        const int logLength = 256 * 1024;
        const int replayLength = 64 * 1024;
        publisher.Reserve("delivery", slot, beadId);
        publisher.SetInvocation("delivery", slot, new FactoryInvocationObservation(
            beadId, invocationId, "implementer", "codex", "gpt-test", "high", "running",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), 1234, null));
        var logPath = Path.Combine(host.LogsRoot(project.Name), beadId, invocationId + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, new string('x', logLength), new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        publisher.FinishInvocation("delivery", slot, invocationId, "completed", 0);
        var snapshot = await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
            value => value.Delivery.Slots[0].Invocation?.Status == "completed",
            TestContext.Current.CancellationToken);
        var url = StreamUrl(snapshot.RuntimeId, slot, invocationId) + $"&cursor=0";
        var requests = new[] { new HttpRequestMessage(HttpMethod.Get, url) };
        var headerRequest = new HttpRequestMessage(HttpMethod.Get, url + $"&cursor={logLength}");
        headerRequest.Headers.TryAddWithoutValidation("Last-Event-ID", $"{snapshot.RuntimeId}.{invocationId}.0");
        requests = [requests[0], headerRequest];
        foreach (var request in requests)
        {
            using (request)
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                       TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var events = await ReadAllEventsAsync(response, deadline.Token);
                var reset = Assert.Single(events, item => item.Name == "reset");
                Assert.Equal(logLength - replayLength, reset.Data.GetProperty("cursor").GetInt64());
                var logEvents = events.Where(item => item.Name == "log").ToArray();
                Assert.Equal(replayLength, logEvents.Sum(item => Encoding.UTF8.GetByteCount(item.Data.GetProperty("text").GetString()!)));
                Assert.Equal(logLength, ParseCursor(logEvents[^1].Id));
                Assert.Equal("end", events[^1].Name);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalUtf8BacklogDrainsAcrossChunkBoundary(bool stoppedRuntime)
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var repository = new ValidatedTargetRepository(project.TargetRepository.Path, project.TargetRepository.DefaultBranch);
        var publisher = new FactoryTelemetryPublisher(host, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots);
        await publisher.StartAsync(TestContext.Current.CancellationToken);

        const string beadId = "ns-terminal-utf8.1";
        const string slot = "delivery-1";
        const string invocationId = "20260301T0102030000000Z-implementer";
        var expected = new string('x', 16383) + "€-suffix\n";
        publisher.Reserve("delivery", slot, beadId);
        publisher.SetInvocation("delivery", slot, new FactoryInvocationObservation(
            beadId, invocationId, "implementer", "codex", "gpt-test", "high", "running",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), 1234, null));
        var logPath = Path.Combine(host.LogsRoot(project.Name), beadId, invocationId + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, expected, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        if (stoppedRuntime) await publisher.DisposeAsync();
        else
        {
            publisher.FinishInvocation("delivery", slot, invocationId, "completed", 0);
            await WaitForSnapshotAsync(factory.Services.GetRequiredService<FactoryTelemetryReader>(),
                value => value.Delivery.Slots[0].Invocation?.Status == "completed",
                TestContext.Current.CancellationToken);
        }

        try
        {
            var snapshot = Assert.IsType<FactoryTelemetrySnapshot>(
                factory.Services.GetRequiredService<FactoryTelemetryReader>().Read().Snapshot);
            using var response = await client.GetAsync(StreamUrl(snapshot.RuntimeId, slot, invocationId),
                HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var events = await ReadAllEventsAsync(response, deadline.Token);
            var logEvents = events.Where(item => item.Name == "log").ToArray();
            var actual = string.Concat(logEvents.Select(item => item.Data.GetProperty("text").GetString()));
            Assert.Equal(expected, actual);
            Assert.DoesNotContain("\uFFFD", actual, StringComparison.Ordinal);
            Assert.Equal(Encoding.UTF8.GetByteCount(expected), ParseCursor(logEvents[^1].Id));
            Assert.Equal("end", events[^1].Name);
        }
        finally { await publisher.DisposeAsync(); }
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

    private static async Task AssertUnsafeSnapshotUnavailableAsync(FactoryTelemetryReader reader, HttpClient client,
        string secretValue)
    {
        var direct = reader.Read();
        Assert.Equal(FactoryTelemetryAvailability.Unavailable, direct.Availability);
        Assert.Null(direct.Snapshot);

        using var response = await client.GetAsync("/api/factory/state", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("unavailable", json.RootElement.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("snapshot").ValueKind);
        Assert.DoesNotContain(secretValue, body, StringComparison.Ordinal);
    }

    private static async Task<CapturedSseEvent> ReadNextSseEventAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        var name = string.Empty;
        var id = string.Empty;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (name.Length == 0) continue;
                using var document = JsonDocument.Parse(data.ToString());
                return new CapturedSseEvent(name, id, document.RootElement.Clone());
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data.Append(line[6..]);
        }
        throw new EndOfStreamException("The telemetry stream ended before the expected SSE event.");
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(linkPath);
        using var handle = CreateFileW(linkPath, 0x40000000, 0x00000007, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening telemetry test junction failed.");

        var printName = Path.GetFullPath(targetPath);
        var substituteName = @"\??\" + printName;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var dataLength = 8 + substituteBytes.Length + sizeof(char) + printBytes.Length + sizeof(char);
        var buffer = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + sizeof(char))));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer.AsSpan(16));
        printBytes.CopyTo(buffer.AsSpan(16 + substituteBytes.Length + sizeof(char)));
        if (!DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Creating telemetry test junction failed.");
    }

    private static void CreateHardLink(string linkPath, string targetPath)
    {
        if (!CreateHardLinkW(linkPath, targetPath, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Creating telemetry test hard link failed.");
    }

    private static void DeleteTelemetryFilesAndDirectoryBeforeJunction(string telemetryRoot)
    {
        Assert.True(Directory.Exists(telemetryRoot));
        Assert.False((File.GetAttributes(telemetryRoot) & FileAttributes.ReparsePoint) != 0,
            "The telemetry test root must be a real directory before replacing it with a junction.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(telemetryRoot))
        {
            var attributes = File.GetAttributes(entry);
            Assert.False((attributes & FileAttributes.ReparsePoint) != 0,
                $"Refusing to remove reparse entry '{entry}' while preparing a junction test.");
            Assert.False((attributes & FileAttributes.Directory) != 0,
                $"Unexpected nested telemetry directory '{entry}' while preparing a junction test.");
            File.Delete(entry);
        }
        Directory.Delete(telemetryRoot);
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

    private sealed record CapturedSseEvent(string Name, string Id, JsonElement Data);

    private static async Task<List<CapturedSseEvent>> ReadAllEventsAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var events = new List<CapturedSseEvent>();
        var name = string.Empty;
        var id = string.Empty;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (name.Length > 0)
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    events.Add(new(name, id, document.RootElement.Clone()));
                }
                name = string.Empty;
                id = string.Empty;
                data.Clear();
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data.Append(line[6..]);
        }
        return events;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[] inputBuffer,
        uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}
