using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nightweaver.Tests;

namespace Nightweaver.Tests.ManualDeliveryPilotFixture;

/// <summary>Owns the disposable Git, Beads, runtime and gate files used by the manual Delivery pilot.</summary>
public sealed class ManualDeliveryPilotFixture : IAsyncDisposable
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private readonly string inheritedPath;
    private readonly HashSet<int> ownedProcesses = [];
    private bool disposed;

    private ManualDeliveryPilotFixture(string root)
    {
        Root = Path.GetFullPath(root);
        Repository = Path.Combine(Root, "repository");
        Origin = Path.Combine(Root, "origin.git");
        UserHome = Path.Combine(Root, "home");
        NightweaverRoot = Path.Combine(Root, "nightweaver");
        FakeCliRoot = Path.Combine(Root, "fake-cli");
        GateDirectory = Path.Combine(Root, "gates");
        EvidencePath = Path.Combine(Root, "agent-evidence.jsonl");
        RuntimeLogPath = Path.Combine(Root, "runtime.log");
        ManifestPath = Path.Combine(Root, "manifest.json");
        ConfigPath = Path.Combine(Root, "project.json");
        RuntimeRestartRequestPath = Path.Combine(GateDirectory, "runtime.restart.request");
        RuntimeRestartAckPath = Path.Combine(GateDirectory, "runtime.restart.ack");
        RuntimeProcessIdPath = Path.Combine(GateDirectory, "runtime.pid");
        inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }

    public string Root { get; }
    public string Repository { get; }
    public string Origin { get; }
    public string UserHome { get; }
    public string NightweaverRoot { get; }
    public string FakeCliRoot { get; }
    public string GateDirectory { get; }
    public string EvidencePath { get; }
    public string RuntimeLogPath { get; }
    public string ManifestPath { get; }
    public string ConfigPath { get; }
    public string RuntimeRestartRequestPath { get; }
    public string RuntimeRestartAckPath { get; }
    public string RuntimeProcessIdPath { get; }
    public string ParentlessTaskId { get; private set; } = string.Empty;
    public string ProductId { get; private set; } = string.Empty;
    public string ProductTaskId { get; private set; } = string.Empty;
    public string DecisionId { get; private set; } = string.Empty;
    public string PausedTaskId { get; private set; } = string.Empty;
    public string ExhaustionTaskId { get; private set; } = string.Empty;
    public string ForeignClaimId { get; private set; } = string.Empty;
    public string? MergerGateBeadId { get; set; }
    public string? MergerInterruptBeadId { get; set; }
    public string? MergerRetryGateBeadId { get; set; }
    public string? MergerDiagnosticsBeadId { get; set; }
    public string? RetainSentinelBeadId { get; set; }
    internal WindowsConsoleProcess? RuntimeProcess { get; private set; }
    public bool PreserveEvidenceOnDispose { get; private set; }

    public void PreserveForFailure() => PreserveEvidenceOnDispose = true;

    public static async Task<ManualDeliveryPilotFixture> CreateAsync(string? root = null,
        string? agentInvocationFixturePath = null, bool includeExhaustionTask = false,
        bool includeForeignClaim = false, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native manual Delivery pilot requires Windows process and console controls.");

        var fixture = new ManualDeliveryPilotFixture(root ?? Path.Combine(Path.GetTempPath(), "nightweaver-manual-delivery-" + Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(fixture.Root);
            Directory.CreateDirectory(fixture.Repository);
            Directory.CreateDirectory(fixture.UserHome);
            Directory.CreateDirectory(fixture.NightweaverRoot);
            Directory.CreateDirectory(Path.Combine(fixture.NightweaverRoot, "prompts"));
            Directory.CreateDirectory(fixture.FakeCliRoot);
            Directory.CreateDirectory(fixture.GateDirectory);

            foreach (var role in new[] { "implementer", "reviewer", "qa", "merger" })
                await File.WriteAllTextAsync(Path.Combine(fixture.NightweaverRoot, "prompts", role + ".md"),
                    $"Disposable manual Delivery pilot {role} prompt. Follow the task and report evidence.\n",
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            await fixture.InitializeGitAsync(cancellationToken).ConfigureAwait(false);
            await fixture.InitializeBeadsAsync(includeExhaustionTask, includeForeignClaim, cancellationToken).ConfigureAwait(false);
            fixture.CopyAgentAliases(agentInvocationFixturePath ?? Path.Combine(AppContext.BaseDirectory, "AgentInvocationFixture.exe"));
            await fixture.WriteConfigurationAsync(cancellationToken).ConfigureAwait(false);
            await fixture.WriteManifestAsync(cancellationToken).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyDictionary<string, string?> CreateEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = FakeCliRoot + Path.PathSeparator + inheritedPath,
            ["HOME"] = UserHome,
            ["USERPROFILE"] = UserHome,
            ["APPDATA"] = UserHome,
            ["XDG_CONFIG_HOME"] = UserHome,
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(UserHome, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "Nightweaver Manual Delivery Fixture",
            ["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid",
            ["GIT_COMMITTER_NAME"] = "Nightweaver Manual Delivery Fixture",
            ["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid",
            ["BD_IMPORT_AUTO"] = "false",
            ["BD_EXPORT_AUTO"] = "false",
            ["BD_BACKUP_ENABLED"] = "false",
            ["BD_DOLT_AUTO_PUSH"] = "false",
            ["BD_EVENTS_EXPORT"] = "false",
            ["BD_DISABLE_METRICS"] = "1",
            ["BD_NO_HOOKS"] = "true",
            ["BD_DOLT_AUTO_START"] = "false",
            ["BEADS_DOLT_SERVER_HOST"] = "127.0.0.1",
            ["NIGHTWEAVER__Root"] = NightweaverRoot,
            ["NIGHTWEAVER__ConfigPath"] = ConfigPath,
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "manual-delivery-pilot",
            ["NIGHTWEAVER_FIXTURE_PILOT_GATE_BEAD"] = ParentlessTaskId,
            ["NIGHTWEAVER_FIXTURE_PILOT_GATE_BEAD2"] = PausedTaskId,
            ["NIGHTWEAVER_FIXTURE_PILOT_GATE_DIRECTORY"] = GateDirectory,
            ["NIGHTWEAVER_FIXTURE_PILOT_EVIDENCE"] = EvidencePath,
            ["NIGHTWEAVER_FIXTURE_PILOT_RETAIN_SENTINEL_BEAD"] = RetainSentinelBeadId,
            ["NIGHTWEAVER_FIXTURE_PILOT_CONFIG"] = ConfigPath,
            ["NIGHTWEAVER_FIXTURE_PILOT_EXHAUST_BEAD"] = string.IsNullOrEmpty(ExhaustionTaskId) ? null : ExhaustionTaskId,
            ["NIGHTWEAVER_FIXTURE_PILOT_MERGER_GATE_BEAD"] = MergerGateBeadId,
            ["NIGHTWEAVER_FIXTURE_PILOT_MERGER_GATE_DIRECTORY"] = GateDirectory,
            ["NIGHTWEAVER_FIXTURE_PILOT_MERGER_INTERRUPT_BEAD"] = MergerInterruptBeadId,
            ["NIGHTWEAVER_FIXTURE_PILOT_MERGER_INTERRUPT_DIRECTORY"] = GateDirectory,
            ["NIGHTWEAVER_FIXTURE_PILOT_MERGER_RETRY_GATE_BEAD"] = MergerRetryGateBeadId,
            ["NIGHTWEAVER_FIXTURE_PILOT_MERGER_RETRY_GATE_DIRECTORY"] = GateDirectory,
            ["NIGHTWEAVER_FIXTURE_PILOT_MERGER_DIAGNOSTICS_PATH"] = string.IsNullOrWhiteSpace(MergerDiagnosticsBeadId)
                ? null : MergerDiagnosticsPath(MergerDiagnosticsBeadId),
            ["NIGHTWEAVER_FIXTURE_PILOT_REJECT_BEAD"] = ProductTaskId,
            ["NIGHTWEAVER_FIXTURE_DEFAULT_BRANCH"] = "main"
        };

        foreach (var key in ScrubbedEnvironmentVariables)
            environment[key] = null;
        return environment;
    }

    internal WindowsConsoleProcess StartRuntime(string nightweaverAssemblyPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RuntimeProcess is { HasExited: true } exitedRuntime)
        {
            ownedProcesses.Remove(exitedRuntime.Id);
            exitedRuntime.Dispose();
            RuntimeProcess = null;
        }
        if (RuntimeProcess is { HasExited: false })
            throw new InvalidOperationException("The manual Delivery pilot runtime is already running.");

        CopyRuntimePayload(nightweaverAssemblyPath);
        var runtimeAssembly = Path.Combine(NightweaverRoot, Path.GetFileName(nightweaverAssemblyPath));
        var process = WindowsConsoleProcess.Start("dotnet", [runtimeAssembly, "--config", ConfigPath],
            Repository, CreateEnvironment(), RuntimeLogPath);
        ownedProcesses.Add(process.Id);
        RuntimeProcess = process;
        return process;
    }

    public async Task RunRuntimeRestartControlLoopAsync(string nightweaverAssemblyPath, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(RuntimeRestartRequestPath) && RuntimeProcess is { HasExited: true })
            {
                File.Delete(RuntimeRestartRequestPath);
                var runtime = StartRuntime(nightweaverAssemblyPath);
                await File.WriteAllTextAsync(RuntimeProcessIdPath, runtime.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(RuntimeRestartAckPath, runtime.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendRuntimeConsoleBreakAsync(CancellationToken cancellationToken = default)
    {
        var process = RuntimeProcess ?? throw new InvalidOperationException("The manual Delivery pilot runtime has not been started.");
        if (process.HasExited) throw new InvalidOperationException("The manual Delivery pilot runtime has already exited.");
        var helper = Path.Combine(FakeCliRoot, "AgentInvocationFixture.exe");
        await RunToolAsync(helper,
            ["--nightweaver-send-control-break", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            Repository, CreateEnvironment(), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    public async Task CrashRuntimeAsync(TimeSpan timeout)
    {
        if (RuntimeProcess is not { } process) return;
        try
        {
            if (!process.HasExited) process.KillEntireProcessTree();
            await process.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            if (process.HasExited)
            {
                ownedProcesses.Remove(process.Id);
                process.Dispose();
                RuntimeProcess = null;
            }
        }
    }

    private void CopyRuntimePayload(string runtimeAssemblyPath)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(runtimeAssemblyPath))
            ?? throw new InvalidOperationException("The runtime assembly has no containing directory.");
        var requiredAssembly = Path.GetFileName(runtimeAssemblyPath);
        var requiredFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals(Path.GetFileNameWithoutExtension(requiredAssembly) + ".deps.json", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals(Path.GetFileNameWithoutExtension(requiredAssembly) + ".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!requiredFiles.Any(path => Path.GetFileName(path).Equals(requiredAssembly, StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException("The runtime assembly was not found in its output directory.", runtimeAssemblyPath);

        foreach (var source in requiredFiles)
            File.Copy(source, Path.Combine(NightweaverRoot, Path.GetFileName(source)), overwrite: true);
    }

    public async Task WaitForGateAsync(string beadId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var gate = Path.Combine(GateDirectory, beadId + ".started");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(gate)) return;
            if (RuntimeProcess is { HasExited: true } process)
                throw new InvalidOperationException($"Pilot runtime exited ({process.ExitCode}) before the native gate opened: {ReadRuntimeLog()}");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Pilot task '{beadId}' did not reach its native gate within {timeout}. Runtime output: {ReadRuntimeLog()}");
    }

    public void ReleaseGate(string beadId) =>
        File.WriteAllText(Path.Combine(GateDirectory, beadId + ".release"), "release\n", new UTF8Encoding(false));

    public void ReleaseMergerGate(string beadId) =>
        File.WriteAllText(Path.Combine(GateDirectory, beadId + ".merger.release"), "release\n", new UTF8Encoding(false));

    public void ReleaseMergerRetryGate(string beadId) =>
        File.WriteAllText(Path.Combine(GateDirectory, beadId + ".merger.retry.release"), "release\n", new UTF8Encoding(false));

    public async Task WaitForMergerGateAsync(string beadId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var gate = Path.Combine(GateDirectory, beadId + ".merger.started");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(gate)) return;
            if (RuntimeProcess is { HasExited: true } process)
                throw new InvalidOperationException($"Pilot runtime exited ({process.ExitCode}) before the Merger gate opened: {ReadRuntimeLog()}");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Pilot Merger '{beadId}' did not reach its gate within {timeout}. Runtime output: {ReadRuntimeLog()}");
    }

    public async Task WaitForMergerRetryGateAsync(string beadId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var gate = Path.Combine(GateDirectory, beadId + ".merger.retry.started");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(gate)) return;
            if (RuntimeProcess is { HasExited: true } process)
                throw new InvalidOperationException($"Pilot runtime exited ({process.ExitCode}) before the Merger retry gate opened: {ReadRuntimeLog()}");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Pilot Merger retry '{beadId}' did not reach its gate within {timeout}. Runtime output: {ReadRuntimeLog()}");
    }

    public Task<string> CreateTaskAsync(string title, CancellationToken cancellationToken = default) =>
        CreateBeadAsync(title, "task", ["stage:implement"], cancellationToken);

    public Task<string> CreateMergeGateTaskAsync(string title, CancellationToken cancellationToken = default) =>
        CreateBeadAsync(title, "task", ["stage:merge"], cancellationToken);

    public string ReadRuntimeLog()
    {
        if (!File.Exists(RuntimeLogPath)) return string.Empty;
        using var stream = new FileStream(RuntimeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public async Task<JsonElement> ReadBeadAsync(string beadId, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await RunToolAsync("bd", ["show", beadId, "--json"], Repository,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        return (root.ValueKind == JsonValueKind.Array ? root[0] : root).Clone();
    }

    public async Task<string> CreateForeignClaimAsync(CancellationToken cancellationToken = default)
    {
        var beadId = await CreateBeadAsync("QA manual pilot foreign-owned recovery sentinel", "task",
            ["stage:implement"], cancellationToken).ConfigureAwait(false);
        await RunToolAsync("bd", ["update", beadId, "--status", "in_progress", "--assignee", "foreign:operator"],
            Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return beadId;
    }

    public Task<string> ReadWorktreeBranchAsync(string beadId, CancellationToken cancellationToken = default) =>
        RunToolAsync("git", ["branch", "--show-current"], WorktreePath(beadId), CreateEnvironment(),
            TimeSpan.FromSeconds(15), cancellationToken);

    public async Task<string> ReadRemoteMainFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
        await RunToolAsync("git", ["--git-dir", Origin, "show", $"refs/heads/main:{relativePath}"], Root,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

    public Task<string> ReadOriginRefAsync(string reference, CancellationToken cancellationToken = default) =>
        RunToolAsync("git", ["--git-dir", Origin, "rev-parse", reference], Root,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken);

    public Task<string> ReadOriginFileCommitHistoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
        RunToolAsync("git", ["--git-dir", Origin, "log", "--format=%H", "--all", "--", relativePath], Root,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken);

    public Task<string> ReadBeadCommentsAsync(string beadId, CancellationToken cancellationToken = default) =>
        RunToolAsync("bd", ["comments", beadId, "--json"], Repository, CreateEnvironment(),
            TimeSpan.FromSeconds(30), cancellationToken);

    public Task<string> ReadPullRequestAsync(string beadId, CancellationToken cancellationToken = default) =>
        RunToolAsync(Path.Combine(FakeCliRoot, "gh.exe"),
            ["pr", "view", "https://github.com/nightweaver-fixture/target/pull/123", "--json", "state,headRefOid"],
            WorktreePath(beadId), CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken);

    public string MergerDiagnosticsPath(string beadId) => Path.Combine(GateDirectory, beadId + ".merger-diagnostics.jsonl");

    public string WorktreePath(string beadId) => Path.Combine(NightweaverRoot, "worktrees", "manualqa", beadId);

    public async Task StopRuntimeAsync(bool force, TimeSpan timeout)
    {
        if (RuntimeProcess is not { } process) return;
        try
        {
            if (!process.HasExited)
            {
                if (force)
                {
                    await CrashRuntimeAsync(timeout).ConfigureAwait(false);
                    return;
                }
                else
                {
                    var helper = Path.Combine(FakeCliRoot, "AgentInvocationFixture.exe");
                    await RunToolAsync(helper, ["--nightweaver-send-control-break", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                        Repository, CreateEnvironment(), timeout).ConfigureAwait(false);
                }
                try { await process.WaitAsync(timeout).ConfigureAwait(false); }
                catch (TimeoutException) when (!process.HasExited)
                {
                    process.Dispose();
                    throw new TimeoutException($"The manual Delivery pilot runtime did not stop within {timeout}; it was force-killed after the graceful bound.");
                }
            }
        }
        finally
        {
            if (process.HasExited)
            {
                ownedProcesses.Remove(process.Id);
                process.Dispose();
                RuntimeProcess = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Exception? cleanupFailure = null;
        try { await StopRuntimeAsync(force: true, TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailure = exception; }

        foreach (var processId in ownedProcesses.ToArray())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            ownedProcesses.Remove(processId);
        }

        if (!PreserveEvidenceOnDispose && Directory.Exists(Root))
        {
            var fullRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                cleanupFailure ??= new InvalidOperationException($"Refusing to remove pilot fixture outside the temporary root: '{fullRoot}'.");
            else
            {
                for (var attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                            File.SetAttributes(file, FileAttributes.Normal);
                        Directory.Delete(Root, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        cleanupFailure = exception;
                        if (attempt < 9) await Task.Delay(250).ConfigureAwait(false);
                    }
                }
            }
        }

        if (Directory.Exists(Root) && cleanupFailure is not null)
            throw new InvalidOperationException("Manual Delivery pilot cleanup was incomplete.", cleanupFailure);
    }

    private async Task InitializeGitAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Origin);
        await RunToolAsync("git", ["init", "--bare", Origin], Root, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["init", "-b", "main"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["config", "core.autocrlf", "false"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["config", "user.name", "Nightweaver Manual Delivery Fixture"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["config", "user.email", "fixture@example.invalid"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(Repository, "README.md"), "Disposable native Delivery pilot.\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(Repository, ".gitignore"), ".nightweaver-ignored/\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["add", "README.md", ".gitignore"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["commit", "-m", "seed manual Delivery pilot"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["remote", "add", "origin", Origin], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        const string hostedOriginUrl = "https://github.com/nightweaver-fixture/target.git";
        var localOriginUrl = Origin.Replace('\\', '/');
        await RunToolAsync("git", ["config", "remote.origin.url", hostedOriginUrl], Repository,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["config", "remote.origin.pushurl", localOriginUrl], Repository,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["config", $"url.{localOriginUrl}.insteadOf", hostedOriginUrl], Repository,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("git", ["push", "--set-upstream", "origin", "main"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeBeadsAsync(bool includeExhaustionTask, bool includeForeignClaim, CancellationToken cancellationToken)
    {
        await RunToolAsync("bd", ["init", "--prefix", "qa", "--non-interactive", "--skip-agents", "--skip-hooks"],
            Repository, CreateEnvironment(), TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        await RunToolAsync("bd", ["config", "set", "events-journal", "true"], Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        ParentlessTaskId = await CreateBeadAsync("QA manual pilot parentless task", "task", ["stage:implement"], cancellationToken).ConfigureAwait(false);
        ProductId = await CreateBeadAsync("QA manual pilot Product", "feature", ["planned"], cancellationToken).ConfigureAwait(false);
        ProductTaskId = await CreateBeadAsync("QA manual pilot linked task", "task", ["stage:implement"], cancellationToken).ConfigureAwait(false);
        await RunToolAsync("bd", ["dep", "add", ProductTaskId, ProductId, "--type", "discovered-from"],
            Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        DecisionId = await CreateBeadAsync("QA manual pilot product decision", "feature", ["product-decision-required"], cancellationToken).ConfigureAwait(false);
        await RunToolAsync("bd", ["comments", "add", DecisionId,
            "Which compatibility path should we choose? Keep the current API contract or migrate the consumer."],
            Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        PausedTaskId = await CreateBeadAsync("QA manual pilot needs-human retry", "task",
            ["stage:implement", "needs-human"], cancellationToken).ConfigureAwait(false);
        await RunToolAsync("bd", ["update", PausedTaskId, "--set-metadata", "repair_cycles=2"],
            Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var diagnostic = JsonSerializer.Serialize(new
        {
            schema = "nightweaver-needs-human-v1",
            bead_id = PausedTaskId,
            bead_type = "task",
            cause = "operational-retries-exhausted",
            failed_role = "implementer",
            reason = "The disposable pilot requires the browser operator to retry this held task.",
            latest_findings = "The fixture pause proves guarded local Resume/Retry.",
            cleanup_preflight = "",
            stage = "stage:implement",
            invocation_id = "manual-delivery-pilot",
            log_directory = "logs/manual-delivery-pilot",
            log_files = Array.Empty<string>()
        });
        await RunToolAsync("bd", ["comments", "add", PausedTaskId, diagnostic], Repository,
            CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (includeExhaustionTask)
            ExhaustionTaskId = await CreateBeadAsync("QA manual pilot automatic rejection exhaustion", "task",
                ["stage:implement"], cancellationToken).ConfigureAwait(false);
        if (includeForeignClaim)
        {
            ForeignClaimId = await CreateBeadAsync("QA manual pilot foreign-owned preservation sentinel", "task",
                ["stage:implement"], cancellationToken).ConfigureAwait(false);
            await RunToolAsync("bd", ["update", ForeignClaimId, "--status", "in_progress", "--assignee", "foreign:operator"],
                Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> CreateBeadAsync(string title, string type, IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "create", "--title", title, "--type", type, "--json" };
        if (labels.Count > 0) { args.Add("--labels"); args.Add(string.Join(',', labels)); }
        var output = await RunToolAsync("bd", args, Repository, CreateEnvironment(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidDataException($"Beads did not return an ID for '{title}'.");
    }

    private async Task WriteConfigurationAsync(CancellationToken cancellationToken)
    {
        var config = new
        {
            name = "manualqa",
            target_repository = new { path = Repository, default_branch = "main" },
            concurrency = new { max_concurrent_planning = 1, max_concurrent_delivery = 2, max_active_plan_beads = 1 },
            limits = new { max_repair_cycles = 2, max_operational_retries = 1 },
            agent_defaults = new { cli = "codex", effort = "low", timeout_minutes = 2 }
        };
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(config, IndentedJson),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteManifestAsync(CancellationToken cancellationToken)
    {
        var manifest = new
        {
            schema = "nightweaver-manual-delivery-pilot-v1",
            root = Root,
            repository = Repository,
            origin = Origin,
            userHome = UserHome,
            nightweaverRoot = NightweaverRoot,
            configPath = ConfigPath,
            gateDirectory = GateDirectory,
            evidencePath = EvidencePath,
            runtimeLogPath = RuntimeLogPath,
            runtimeRestartRequest = RuntimeRestartRequestPath,
            runtimeRestartAck = RuntimeRestartAckPath,
            runtimeProcessId = RuntimeProcessIdPath,
            foreignClaim = string.IsNullOrEmpty(ForeignClaimId) ? null : ForeignClaimId,
            tasks = new
            {
                parentless = ParentlessTaskId,
                product = ProductId,
                productTask = ProductTaskId,
                decision = DecisionId,
                paused = PausedTaskId,
                exhaustion = string.IsNullOrEmpty(ExhaustionTaskId) ? null : ExhaustionTaskId
            }
        };
        await File.WriteAllTextAsync(ManifestPath, JsonSerializer.Serialize(manifest, IndentedJson),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private void CopyAgentAliases(string executablePath)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new InvalidOperationException("AgentInvocationFixture has no directory.");
        var sourceExe = Path.Combine(sourceDirectory, "AgentInvocationFixture.exe");
        if (!File.Exists(sourceExe)) sourceExe = executablePath;
        if (!File.Exists(sourceExe)) throw new FileNotFoundException("AgentInvocationFixture executable is missing.", sourceExe);
        foreach (var file in new[] { "AgentInvocationFixture.exe", "AgentInvocationFixture.dll", "AgentInvocationFixture.deps.json", "AgentInvocationFixture.runtimeconfig.json" })
        {
            var path = Path.Combine(sourceDirectory, file);
            if (File.Exists(path)) File.Copy(path, Path.Combine(FakeCliRoot, file), overwrite: true);
        }
        foreach (var alias in new[] { "claude.exe", "codex.exe", "gh.exe" })
            File.Copy(sourceExe, Path.Combine(FakeCliRoot, alias), overwrite: true);
    }

    private static readonly string[] ScrubbedEnvironmentVariables =
    [
        "BEADS_DIR", "BEADS_DB", "BD_DB", "BEADS_DOLT_SERVER_DATABASE", "BEADS_DOLT_DATA_DIR",
        "BEADS_DOLT_SERVER_MODE", "BEADS_DOLT_SHARED_SERVER", "BEADS_MEM_PROFILE", "BEADS_MEM_STATS",
        "BD_CPU_PROFILE", "BD_MEM_PROFILE", "BD_TRACE", "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE",
        "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE",
        "GIT_CEILING_DIRECTORIES", "OPENAI_API_KEY", "ANTHROPIC_API_KEY"
    ];

    private static async Task<string> RunToolAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException($"'{executable} {string.Join(' ', arguments)}' exceeded {timeout}.");
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{executable} {string.Join(' ', arguments)}' exited {process.ExitCode}. stdout: {output}\nstderr: {error}");
        return output;
    }

}
