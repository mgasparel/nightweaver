using System.Collections;
using System.Net;
using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Telemetry;

namespace Nightweaver.Background;

internal sealed class BackgroundWebHost : IAsyncDisposable
{
    private readonly WindowsProcessLauncher.WindowsAgentProcess process;
    private readonly Task<uint> exitTask;
    private readonly Task stdoutPump;
    private readonly Task stderrPump;
    private readonly InvocationRuntimeFiles.PinnedRuntimeFile log;
    private readonly SemaphoreSlim logGate;
    private readonly string url;
    private bool disposed;

    private BackgroundWebHost(WindowsProcessLauncher.WindowsAgentProcess process, string url,
        InvocationRuntimeFiles.PinnedRuntimeFile log, SemaphoreSlim logGate, Task stdoutPump, Task stderrPump)
    {
        this.process = process;
        exitTask = process.WaitForRootExitAsync();
        this.url = url;
        this.log = log;
        this.logGate = logGate;
        this.stdoutPump = stdoutPump;
        this.stderrPump = stderrPump;
    }

    internal bool HasExited
    {
        get
        {
            return exitTask.IsCompleted;
        }
    }

    internal Task<uint> WaitForExitAsync() => exitTask;

    internal static BackgroundWebHost Start(string executable, string installationRoot,
        string configPath, string urls, string logPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("background deployment currently requires Windows");
        if (!File.Exists(executable)) throw new FileNotFoundException("published Web Console executable is missing", executable);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(item => item.Key is string && item.Value is string)
            .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.OrdinalIgnoreCase);
        var args = new[]
        {
            "--urls", urls,
            "--Nightweaver:Root", installationRoot,
            "--Nightweaver:ConfigPath", configPath
        };
        var log = InvocationRuntimeFiles.OpenAppendRuntimeLog(logPath);
        try
        {
            var process = WindowsProcessLauncher.Launch(executable, args, Path.GetDirectoryName(executable)!, environment);
            var gate = new SemaphoreSlim(1, 1);
            var stdout = PumpAsync(process.StandardOutput, log.Stream, "stdout", gate);
            var stderr = PumpAsync(process.StandardError, log.Stream, "stderr", gate);
            return new BackgroundWebHost(process, urls, log, gate, stdout, stderr);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    internal async Task CheckReadyAsync(FactoryTelemetryPublisher telemetry, string project,
        string targetRepository, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var baseUri = new Uri(url.EndsWith('/') ? url : url + "/", UriKind.Absolute);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exitTask.IsCompleted)
                throw new IOException("published Web Console exited before readiness was confirmed");
            try
            {
                using var health = await client.GetAsync(new Uri(baseUri, "health"), cancellationToken).ConfigureAwait(false);
                if (health.StatusCode != HttpStatusCode.OK)
                    throw new IOException($"Web Console health endpoint returned {(int)health.StatusCode}");
                using var stateResponse = await client.GetAsync(new Uri(baseUri, "api/factory/state"), cancellationToken).ConfigureAwait(false);
                if (stateResponse.StatusCode != HttpStatusCode.OK)
                    throw new IOException($"Web Console runtime-state endpoint returned {(int)stateResponse.StatusCode}");
                await using var body = await stateResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (!root.TryGetProperty("availability", out var availability) || availability.GetString() != "live"
                    || !root.TryGetProperty("snapshot", out var snapshot)
                    || !snapshot.TryGetProperty("runtime_id", out var runtimeId)
                    || !string.Equals(runtimeId.GetString(), telemetry.RuntimeId, StringComparison.Ordinal)
                    || !snapshot.TryGetProperty("project", out var observedProject)
                    || !string.Equals(observedProject.GetString(), project, StringComparison.Ordinal)
                    || !snapshot.TryGetProperty("target_repository", out var observedRepository)
                    || !PathEquals(observedRepository.GetString(), targetRepository))
                    throw new IOException("Web Console does not report the expected live Project runtime identity");
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                or IOException or JsonException)
            {
                last = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TimeoutException("Web Console did not confirm the actual runtime identity within 30 seconds", last);
    }

    internal async Task<bool> CheckHttpHealthAsync(string expectedRuntimeId, string project,
        string targetRepository, CancellationToken cancellationToken)
    {
        if (exitTask.IsCompleted) return false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var baseUri = new Uri(url.EndsWith('/') ? url : url + "/", UriKind.Absolute);
            using var health = await client.GetAsync(new Uri(baseUri, "health"), cancellationToken).ConfigureAwait(false);
            if (health.StatusCode != HttpStatusCode.OK) return false;
            using var state = await client.GetAsync(new Uri(baseUri, "api/factory/state"), cancellationToken).ConfigureAwait(false);
            if (state.StatusCode != HttpStatusCode.OK) return false;
            await using var body = await state.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var snapshot = root.GetProperty("snapshot");
            return root.GetProperty("availability").GetString() == "live"
                && snapshot.GetProperty("runtime_id").GetString() == expectedRuntimeId
                && snapshot.GetProperty("project").GetString() == project
                && PathEquals(snapshot.GetProperty("target_repository").GetString(), targetRepository);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        process.CloseForForceStop();
        try { await exitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or ObjectDisposedException or IOException) { }
        process.Dispose();
        try { await Task.WhenAll(stdoutPump, stderrPump).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or ObjectDisposedException or IOException) { }
        logGate.Dispose();
        log.Dispose();
    }

    private static async Task PumpAsync(Stream source, Stream output, string name, SemaphoreSlim gate)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var prefix = System.Text.Encoding.UTF8.GetBytes($"[{DateTimeOffset.UtcNow:O}] {name}: ");
                    await output.WriteAsync(prefix).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
    }

    private static bool PathEquals(string? left, string right) =>
        left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
