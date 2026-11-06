using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Nightweaver.Background;

namespace Nightweaver.Tests;

public sealed class BackgroundDeploymentIntegrationTests
{
    [Fact]
    public async Task PublishedPairOwnsRuntimeAndConsoleAcrossCallerAndBrowserClosureAndManualRestart()
    {
        if (!OperatingSystem.IsWindows()) return;

        var temp = new TempDirectory();
        BeadsFixture? target = null;
        var ownerMayBeActive = false;
        var repositoryRoot = FindRepositoryRoot();
        var host = Path.Combine(temp.Path, "Nightweaver Host");
        var secondHost = Path.Combine(temp.Path, "Nightweaver Host Repeat");
        var caller = temp.CreateDirectory("Unrelated Caller");
        var traps = temp.CreateDirectory("Agent Fixture Binaries");
        var markers = temp.CreateDirectory("Agent Markers");
        var cli = Path.Combine(host, "nightweaver.exe");
        var urls = ReserveLoopbackUrl();
        var url = new Uri(urls);
        var firstStarted = Path.Combine(markers, "first.started");
        var firstRelease = Path.Combine(markers, "first.release");
        var secondStarted = Path.Combine(markers, "second.started");
        var secondRelease = Path.Combine(markers, "second.release");
        var pidDirectory = Path.Combine(markers, "pids");
        var worktreeMarkerDirectory = Path.Combine(markers, "worktrees");
        Directory.CreateDirectory(pidDirectory);
        Directory.CreateDirectory(worktreeMarkerDirectory);
        var liveMarker = "background-pair-live-marker-" + Guid.NewGuid().ToString("N");

        try
        {
            target = new BeadsFixture("Target Repository");
            target.Run("bd", ["close", target.ControlBeadId, "--reason", "background fixture has no work"]);
            ConfigureLocalDeliveryOrigin(target);

            CopyAgentFixture(traps);
            var publish = await PublishPairAsync(repositoryRoot, host, target.Path);
            Assert.True(publish.ExitCode == 0, publish.StandardErrorText + Environment.NewLine + publish.StandardOutputText);
            Assert.True(File.Exists(Path.Combine(host, "nightweaver-deployment.json")));
            Assert.True(File.Exists(Path.Combine(host, "prompts", "implementer.md")));
            Assert.True(File.Exists(Path.Combine(host, "web", "wwwroot", "index.html")));

            var repeatPublish = await PublishPairAsync(repositoryRoot, secondHost, target.Path);
            Assert.Equal(0, repeatPublish.ExitCode);
            Assert.True(File.Exists(Path.Combine(secondHost, "nightweaver-deployment.json")));
            var sentinel = Path.Combine(secondHost, "preserve sentinel.txt");
            File.WriteAllText(sentinel, "keep this existing destination untouched");
            var refusedPublish = await PublishPairAsync(repositoryRoot, secondHost, target.Path);
            Assert.NotEqual(0, refusedPublish.ExitCode);
            Assert.Equal("keep this existing destination untouched", File.ReadAllText(sentinel));

            var projectDirectory = Directory.CreateDirectory(Path.Combine(host, "projects")).FullName;
            var configPath = Path.Combine(projectDirectory, "demo.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                name = "demo",
                target_repository = new { path = target.Path, default_branch = "main" },
                concurrency = new { max_concurrent_delivery = 1 },
                agent_defaults = new { timeout_minutes = 30 }
            }));

            var env = BuildEnvironment(target, traps, firstStarted, firstRelease, pidDirectory,
                worktreeMarkerDirectory, liveMarker, "pool-implement-gated");
            using (var occupied = new TcpListener(IPAddress.Loopback, url.Port))
            {
                occupied.Start();
                var refusedStart = await RunCliAsync(cli, caller, env,
                    ["background", "start", "--project", "demo", "--urls", urls], TimeSpan.FromSeconds(90));
                Assert.NotEqual(0, refusedStart.ExitCode);
                Assert.False(BackgroundFileLock.IsOwnerActive(new Nightweaver.Configuration.HostPaths(host),
                    Nightweaver.Configuration.ProjectSlug.Parse("demo")));
            }

            ownerMayBeActive = true;
            var firstStart = await RunCliAsync(cli, caller, env,
                ["background", "start", "--project", "demo", "--urls", urls], TimeSpan.FromSeconds(90));
            Assert.True(firstStart.ExitCode == 0, firstStart.StandardErrorText);
            var ownerBefore = ParseOwner(firstStart.StandardOutputText);
            Assert.Equal("running", ownerBefore.GetProperty("state").GetString());
            Assert.Equal("healthy", ownerBefore.GetProperty("web_health").GetString());
            Assert.Equal("live", ownerBefore.GetProperty("runtime_health").GetString());
            ownerMayBeActive = true;

            // The initiating CLI process has exited. A separate Web connection still sees the same live runtime.
            using (var client = new HttpClient())
            {
                using var health = await client.GetAsync(new Uri(url, "/health"), TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                using var console = await client.GetAsync(url, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, console.StatusCode);
                Assert.Contains("Nightweaver", await console.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
            }
            var duplicateStart = await RunCliAsync(cli, caller, env,
                ["background", "start", "--project", "demo", "--urls", urls], TimeSpan.FromSeconds(30));
            Assert.True(duplicateStart.ExitCode == 0, duplicateStart.StandardErrorText);
            Assert.Equal(ownerBefore.GetProperty("run_id").GetString(), ParseOwner(duplicateStart.StandardOutputText).GetProperty("run_id").GetString());

            var firstTask = target.Create("Background graceful drain fixture", labels: ["stage:implement"]);
            await WaitForFileAsync(firstStarted, TimeSpan.FromSeconds(75));
            Assert.True(File.Exists(Path.Combine(worktreeMarkerDirectory, firstTask + ".path")));
            Assert.False(File.Exists(firstRelease), "the native Agent remains gated until the control test releases it");
            var firstSnapshot = await WaitForRunningInvocationAsync(host, firstTask, TimeSpan.FromSeconds(30));
            await AssertStreamContainsMarkerAsync(url, firstSnapshot, "delivery-1", liveMarker, TimeSpan.FromSeconds(20));

            // Closing the actual telemetry connection must not cancel the native Agent.
            Assert.True(File.Exists(firstStarted));
            var statusWhileRunning = await RunCliAsync(cli, caller, env,
                ["background", "status", "--project", "demo"], TimeSpan.FromSeconds(20));
            Assert.True(statusWhileRunning.ExitCode == 0, statusWhileRunning.StandardErrorText);
            Assert.Equal(ownerBefore.GetProperty("runtime_id").GetString(), ParseOwner(statusWhileRunning.StandardOutputText).GetProperty("runtime_id").GetString());

            using (var stop = ProcessTool.Start(cli, ["background", "stop", "--project", "demo"], caller, env))
            {
                await WaitForStateAsync(cli, caller, env, "stopping", TimeSpan.FromSeconds(15));
                Assert.False(stop.HasExited, "graceful stop waits for the active native Agent turn");
                var firstAgentIdentityPath = Path.Combine(pidDirectory, firstTask + ".pid");
                await WaitForFileAsync(firstAgentIdentityPath, TimeSpan.FromSeconds(10));
                var firstAgentIdentity = File.ReadAllText(firstAgentIdentityPath);
                var pipeName = BackgroundIdentity.PipeName(new Nightweaver.Configuration.HostPaths(host),
                    Nightweaver.Configuration.ProjectSlug.Parse("demo"));
                var repeatedStop = await BackgroundControlProtocol.SendAsync(pipeName,
                    new BackgroundControlRequest("stop", ownerBefore.GetProperty("run_id").GetString()!),
                    TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.True(repeatedStop.Accepted, repeatedStop.Error);
                AssertAgentIdentityAlive(firstAgentIdentity);
                Assert.False(File.Exists(firstRelease), "repeated graceful stop must leave the gated Agent alive");
                File.WriteAllText(firstRelease, "continue after graceful drain request");
                var stopResult = await stop.WaitAsync(TimeSpan.FromSeconds(90));
                Assert.True(stopResult.ExitCode == 0, stopResult.StandardErrorText);
            }
            var firstStopped = await RunCliAsync(cli, caller, env,
                ["background", "status", "--project", "demo"], TimeSpan.FromSeconds(15));
            Assert.True(firstStopped.ExitCode == 0, firstStopped.StandardErrorText);
            Assert.Equal("stopped", ParseOwner(firstStopped.StandardOutputText).GetProperty("state").GetString());
            ownerMayBeActive = false;
            await AssertNoDirectChildrenAsync(ownerBefore.GetProperty("process_id").GetInt32(), TimeSpan.FromSeconds(10));
            await Task.Delay(500, TestContext.Current.CancellationToken);
            var noRespawn = await RunCliAsync(cli, caller, env,
                ["background", "status", "--project", "demo"], TimeSpan.FromSeconds(15));
            Assert.Equal("stopped", ParseOwner(noRespawn.StandardOutputText).GetProperty("state").GetString());

            var secondTask = target.Create("Background force recovery fixture", labels: ["stage:implement"]);
            var secondEnv = BuildEnvironment(target, traps, secondStarted, secondRelease, pidDirectory,
                worktreeMarkerDirectory, liveMarker, "pool-implement-gated");
            ownerMayBeActive = true;
            var secondStart = await RunCliAsync(cli, caller, secondEnv,
                ["background", "start", "--project", "demo", "--urls", urls], TimeSpan.FromSeconds(60));
            Assert.True(secondStart.ExitCode == 0, secondStart.StandardErrorText);
            ownerMayBeActive = true;
            var beforeForce = ParseOwner(secondStart.StandardOutputText);
            await WaitForFileAsync(secondStarted, TimeSpan.FromSeconds(75));
            var worktreePath = File.ReadAllText(Path.Combine(worktreeMarkerDirectory, secondTask + ".path"));
            var retainedFile = Path.Combine(worktreePath, "force-stop-retained.txt");
            Assert.True(File.Exists(retainedFile));
            var pidIdentity = File.ReadAllText(Path.Combine(pidDirectory, secondTask + ".pid"));

            var forceStop = await RunCliAsync(cli, caller, secondEnv,
                ["background", "stop", "--project", "demo", "--force"], TimeSpan.FromSeconds(30));
            Assert.True(forceStop.ExitCode == 0, forceStop.StandardErrorText);
            ownerMayBeActive = false;
            AssertAgentIdentityExited(pidIdentity);
            await AssertNoDirectChildrenAsync(beforeForce.GetProperty("process_id").GetInt32(), TimeSpan.FromSeconds(10));
            var forceStopped = await RunCliAsync(cli, caller, secondEnv,
                ["background", "status", "--project", "demo"], TimeSpan.FromSeconds(15));
            Assert.Equal("stopped", ParseOwner(forceStopped.StandardOutputText).GetProperty("state").GetString());

            var restartReleaseDirectory = Directory.CreateDirectory(Path.Combine(markers, "restart releases")).FullName;
            var restartEnv = BuildEnvironment(target, traps, Path.Combine(markers, "restart.started"),
                Path.Combine(restartReleaseDirectory, "unused.release"), pidDirectory, worktreeMarkerDirectory,
                liveMarker, "pool-implement-restart-observe");
            restartEnv["NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY"] = restartReleaseDirectory;
            ownerMayBeActive = true;
            var restart = await RunCliAsync(cli, caller, restartEnv,
                ["background", "restart", "--project", "demo"], TimeSpan.FromSeconds(90));
            Assert.True(restart.ExitCode == 0, restart.StandardErrorText);
            ownerMayBeActive = true;
            var restartedOwner = ParseOwner(restart.StandardOutputText);
            Assert.NotEqual(beforeForce.GetProperty("runtime_id").GetString(), restartedOwner.GetProperty("runtime_id").GetString());
            var restartEvidencePath = Path.Combine(worktreeMarkerDirectory, secondTask + ".restart");
            await WaitForFileAsync(restartEvidencePath, TimeSpan.FromSeconds(75));
            var restartEvidence = File.ReadAllText(restartEvidencePath);
            Assert.Contains("retained=True", restartEvidence, StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(worktreePath), Path.GetFullPath(restartEvidence.Split('|')[0]));
            Assert.Equal(restartedOwner.GetProperty("runtime_id").GetString(),
                await ReadWebRuntimeIdAsync(url, TimeSpan.FromSeconds(10)));

            var restartStarted = Path.Combine(markers, "restart.started");
            using (var stop = ProcessTool.Start(cli, ["background", "stop", "--project", "demo"], caller, restartEnv))
            {
                await WaitForStateAsync(cli, caller, restartEnv, "stopping", TimeSpan.FromSeconds(15));
                File.WriteAllText(Path.Combine(restartReleaseDirectory, secondTask + ".release"), "finish cleanup");
                await WaitForFileAsync(restartStarted, TimeSpan.FromSeconds(5));
                var result = await stop.WaitAsync(TimeSpan.FromSeconds(90));
                Assert.True(result.ExitCode == 0, result.StandardErrorText);
            }
            ownerMayBeActive = false;
            var finalStatus = await RunCliAsync(cli, caller, restartEnv,
                ["background", "status", "--project", "demo"], TimeSpan.FromSeconds(15));
            Assert.True(finalStatus.ExitCode == 0, finalStatus.StandardErrorText);
            Assert.Equal("stopped", ParseOwner(finalStatus.StandardOutputText).GetProperty("state").GetString());
            await AssertNoDirectChildrenAsync(restartedOwner.GetProperty("process_id").GetInt32(), TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (ownerMayBeActive && target is not null && File.Exists(cli))
            {
                try
                {
                    var cleanupEnvironment = BuildEnvironment(target, traps, firstStarted, firstRelease,
                        pidDirectory, worktreeMarkerDirectory, liveMarker, "pool-implement-gated");
                    _ = await RunCliAsync(cli, caller, cleanupEnvironment,
                        ["background", "stop", "--project", "demo", "--force"], TimeSpan.FromSeconds(30));
                }
                catch (Exception) { }
                ownerMayBeActive = BackgroundFileLock.IsOwnerActive(new Nightweaver.Configuration.HostPaths(host),
                    Nightweaver.Configuration.ProjectSlug.Parse("demo"));
            }

            if (ownerMayBeActive)
            {
                // Retain disposable inputs while a verified deployment lock remains open.
                _ = temp.Path;
            }
            else
            {
                target?.Dispose();
                temp.Dispose();
            }
        }
    }

    private static async Task<RawProcessResult> PublishPairAsync(string repositoryRoot, string destination,
        string targetRepository)
    {
        var script = Path.Combine(repositoryRoot, "scripts", "Publish-Nightweaver.ps1");
        return await ProcessTool.RunAsync("powershell.exe",
            ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Destination", destination, "-TargetRepository", targetRepository],
            repositoryRoot, null, TimeSpan.FromMinutes(4));
    }

    private static async Task<RawProcessResult> RunCliAsync(string executable, string workingDirectory,
        IReadOnlyDictionary<string, string?> environment, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var run = ProcessTool.RunAsync(executable, arguments, workingDirectory, environment, timeout);
        try
        {
            // ProcessTool also drains redirected output after process exit. Bound that phase so an inherited
            // detached-owner pipe is reported as a lifecycle failure instead of hanging the test indefinitely.
            return await run.WaitAsync(timeout + TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"CLI action '{string.Join(' ', arguments)}' did not finish including output drain within {timeout + TimeSpan.FromSeconds(10)}.", exception);
        }
    }

    private static async Task AssertNoDirectChildrenAsync(int ownerProcessId, TimeSpan timeout)
    {
        var command = $"@(Get-CimInstance Win32_Process -Filter 'ParentProcessId={ownerProcessId}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessId) -join ','";
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await ProcessTool.RunAsync("powershell.exe",
                ["-NoLogo", "-NoProfile", "-Command", command], Environment.CurrentDirectory, null,
                TimeSpan.FromSeconds(5));
            Assert.True(result.ExitCode == 0, result.StandardErrorText);
            if (string.IsNullOrWhiteSpace(result.StandardOutputText)) return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        var remaining = await ProcessTool.RunAsync("powershell.exe",
            ["-NoLogo", "-NoProfile", "-Command", command], Environment.CurrentDirectory, null,
            TimeSpan.FromSeconds(5));
        Assert.True(string.IsNullOrWhiteSpace(remaining.StandardOutputText),
            $"background owner process {ownerProcessId} left direct helper process(es): {remaining.StandardOutputText}");
    }

    private static Dictionary<string, string?> BuildEnvironment(BeadsFixture target, string trapDirectory,
        string startedFile, string releaseFile, string pidDirectory, string markerDirectory,
        string liveMarker, string action)
    {
        var environment = new Dictionary<string, string?>(target.Environment, StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = trapDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty),
            ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = startedFile,
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = releaseFile,
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = pidDirectory,
            ["NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_DIRECTORY"] = markerDirectory,
            ["NIGHTWEAVER_FIXTURE_LIVE_MARKER"] = liveMarker
        };
        return environment;
    }

    private static void CopyAgentFixture(string destination)
    {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "AgentInvocationFixture.*"))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        File.Copy(Path.Combine(destination, "AgentInvocationFixture.exe"), Path.Combine(destination, "claude.exe"), overwrite: true);
    }

    private static async Task<JsonElement> WaitForRunningInvocationAsync(string host, string beadId, TimeSpan timeout)
    {
        var path = Path.Combine(host, "telemetry", "demo", "factory.json");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var snapshot = document.RootElement;
                foreach (var slot in snapshot.GetProperty("delivery").GetProperty("slots").EnumerateArray())
                {
                    if (slot.GetProperty("bead_id").GetString() == beadId
                        && slot.GetProperty("invocation") is { ValueKind: JsonValueKind.Object } invocation)
                    {
                        return JsonDocument.Parse(snapshot.GetRawText()).RootElement.Clone();
                    }
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"factory telemetry did not report an invocation for '{beadId}'");
    }

    private static async Task AssertStreamContainsMarkerAsync(Uri url, JsonElement snapshot,
        string slotId, string marker, TimeSpan timeout)
    {
        var runtimeId = snapshot.GetProperty("runtime_id").GetString()!;
        string invocationId = string.Empty;
        foreach (var slot in snapshot.GetProperty("delivery").GetProperty("slots").EnumerateArray())
        {
            if (slot.GetProperty("slot_id").GetString() != slotId) continue;
            invocationId = slot.GetProperty("invocation").GetProperty("invocation_id").GetString()!;
            break;
        }
        Assert.False(string.IsNullOrWhiteSpace(invocationId));
        var streamUrl = new Uri(url,
            $"/api/telemetry/stream?runtime_id={Uri.EscapeDataString(runtimeId)}&slot_id={Uri.EscapeDataString(slotId)}&invocation_id={Uri.EscapeDataString(invocationId)}");
        using var cancellation = new CancellationTokenSource(timeout);
        using var client = new HttpClient();
        using var response = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(cancellation.Token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var data = JsonDocument.Parse(line[6..]);
            if (data.RootElement.TryGetProperty("text", out var text)
                && text.GetString()!.Contains(marker, StringComparison.Ordinal)) return;
        }
        throw new TimeoutException($"published Web telemetry did not stream the active Agent marker '{marker}'");
    }

    private static async Task<string> ReadWebRuntimeIdAsync(Uri url, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(url, "/api/factory/state"), cancellation.Token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation.Token), cancellationToken: cancellation.Token);
        Assert.Equal("live", document.RootElement.GetProperty("availability").GetString());
        return document.RootElement.GetProperty("snapshot").GetProperty("runtime_id").GetString()!;
    }

    private static async Task WaitForStateAsync(string cli, string caller,
        IReadOnlyDictionary<string, string?> environment, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            var result = await RunCliAsync(cli, caller, environment,
                ["background", "status", "--project", "demo"], TimeSpan.FromSeconds(10));
            lastStatus = $"exit={result.ExitCode}; stdout={result.StandardOutputText}; stderr={result.StandardErrorText}";
            try
            {
                if (ParseOwner(result.StandardOutputText).GetProperty("state").GetString() == state) return;
            }
            catch (JsonException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"background status did not report '{state}'. Last response: {lastStatus}");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"native Agent fixture did not create '{path}'");
    }

    private static JsonElement ParseOwner(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private static void AssertAgentIdentityExited(string identity)
    {
        var parts = identity.Split('|');
        var processId = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var startTicks = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.NotEqual(startTicks, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (ArgumentException) { }
    }

    private static void AssertAgentIdentityAlive(string identity)
    {
        var parts = identity.Split('|');
        var processId = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var startTicks = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.GetProcessById(processId);
        Assert.Equal(startTicks, process.StartTime.ToUniversalTime().Ticks);
        Assert.False(process.HasExited);
    }

    private static void ConfigureLocalDeliveryOrigin(BeadsFixture target)
    {
        var originPath = Path.Combine(target.Root, "origin.git");
        target.Run("git", ["init", "--bare", originPath]);
        target.Run("git", ["remote", "add", "origin", originPath]);
        target.Run("git", ["push", "--set-upstream", "origin", "main"]);
    }

    private static string ReserveLoopbackUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Nightweaver.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Nightweaver.slnx above the test output.");
    }
}
