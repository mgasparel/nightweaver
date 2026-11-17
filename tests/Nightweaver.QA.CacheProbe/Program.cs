using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nightweaver;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;
using Nightweaver.Web;

// Deliberately absent from Nightweaver.slnx and test discovery. This exercises
// the actual Web module with recording adapters, without a production endpoint.
var output = Path.GetFullPath(args.Single(arg => arg.StartsWith("--output=", StringComparison.Ordinal))[9..]);
var scale = args.Contains("--scale");
var revision = args.SingleOrDefault(arg => arg.StartsWith("--revision=", StringComparison.Ordinal))?[11..];
if (revision is null || revision.Length != 40 || !revision.All(Uri.IsHexDigit))
    throw new ArgumentException("Supply the exact tested --revision=<40-character Git commit>.");
var sourceIdentity = new
{
    webAssembly = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Nightweaver.Web.Program).Assembly.Location))),
    cliAssembly = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ProjectConfigLoader).Assembly.Location)))
};
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var root = Path.Combine(Path.GetDirectoryName(output)!, "cache-probe-" + Guid.NewGuid().ToString("N"));
var repo = Path.Combine(root, "repo");
var home = Path.Combine(root, "home");
Directory.CreateDirectory(repo); Directory.CreateDirectory(home);
foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "XDG_CONFIG_HOME" }) Environment.SetEnvironmentVariable(name, home);
Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(home, "gitconfig"));
Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
Environment.SetEnvironmentVariable("BD_DISABLE_METRICS", "1");
Environment.SetEnvironmentVariable("BD_NO_HOOKS", "true");
Environment.SetEnvironmentVariable("BD_DOLT_AUTO_START", "false");
var runner = new MeasuredRunner();
var token = CancellationToken.None;
async Task WriteAsync(params string[] arguments)
{
    var result = await runner.RunAsync(BeadsCommand.CreateWrite(repo, arguments) with { Timeout = TimeSpan.FromSeconds(60) }, token);
    if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
}
var git = await runner.RunAsync(GitCommand.Create(["init", "-b", "main"], repo), token);
if (git.ExitCode != 0) throw new InvalidOperationException(git.StandardError);
await WriteAsync("init", "--prefix", "probe", "--non-interactive", "--skip-agents", "--skip-hooks");
await WriteAsync("config", "set", "events-journal", "true");
var pauseLabels = new[] { "needs-human" };
var rows = Enumerable.Range(0, scale ? 2000 : 50).Select(index => new
{
    id = $"probe-{index:D5}", title = $"Cache probe Bead {index}", description = string.Concat(Enumerable.Repeat("Representative specification. ", 32)),
    issue_type = "task", status = "open", priority = 2,
    created_at = "2026-11-14T00:00:00Z", updated_at = "2026-11-14T00:00:00Z",
    labels = index < (scale ? 40 : 5) ? pauseLabels : [],
    comments = index < (scale ? 40 : 5) ? Enumerable.Range(0, scale ? 20 : 2).Select(comment => new
    { id = index * 20 + comment + 1, author = "probe", text = $"History {comment}. " + string.Concat(Enumerable.Repeat("Question context. ", 32)), created_at = "2026-11-14T00:00:00Z" }).ToArray() : []
});
var seed = Path.Combine(root, "seed.jsonl");
await File.WriteAllLinesAsync(seed, rows.Select(row => JsonSerializer.Serialize(row)), token);
await WriteAsync("import", seed);
var configPath = Path.Combine(root, "project.json");
await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { name = "probe", target_repository = new { path = repo } }), token);
var paths = new HostPaths(root);
var project = ProjectConfigLoader.Load(configPath, expectedName: null, paths);
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton(paths); services.AddSingleton(project); services.AddSingleton(TimeProvider.System);
services.AddSingleton<IHostApplicationLifetime, ProbeLifetime>();
services.AddSingleton<IProcessRunner>(runner);
services.AddSingleton<TargetRepositoryValidator>(); services.AddSingleton<BeadsReader>(); services.AddSingleton<BeadsWriter>();
services.AddSingleton<GitWorktreeReader>();
services.AddSingleton<IBeadWorkspaceRuntimeObserver, UnavailableBeadWorkspaceRuntimeObserver>();
services.AddSingleton<BeadWorkspaceEnvironmentObserver>(); services.AddSingleton<TrackerBridge>(); services.AddSingleton<CatchUpCheckpointStore>();
services.AddSingleton<FactoryTelemetryReader>();
services.AddOperationalObservations();
MeasuredJournal? journal = null;
services.AddSingleton<IConsoleJournalReader>(provider => journal = new MeasuredJournal(
    new ConsoleJournalReader(project, runner, TimeProvider.System, provider.GetRequiredService<TrackerBridge>())));
using var provider = services.BuildServiceProvider();
var observer = provider.GetRequiredService<OperationalObservationService>();
var source = (OperationalObservationSource)provider.GetRequiredService<IOperationalObservationSource>();
var keys = new[] { "attention", "explorer:ready:", "explorer:blocked:", "explorer:all:", "workspace:probe-00000" };
var samples = new List<object>();
var succeeded = false;
var causes = new ConcurrentQueue<SourceCause>();
source.ReadStarted += (id, reason) => causes.Enqueue(new(id, reason, DateTimeOffset.UtcNow));
var timer = Stopwatch.StartNew();
var started = false;
var referenceStart = runner.Reads.Count;
var referenceTimer = Stopwatch.StartNew();
var referenceKeys = keys.Select(OperationKey.Parse).ToArray();
var referenceAttention = false; var referenceWorkspace = false;
for (var turn = 0; turn < 40 && (!referenceAttention || !referenceWorkspace); turn++)
{
    var reference = await source.ReadTrackerAsync(referenceKeys, token);
    referenceAttention |= reference.TryGetValue("attention", out var attention) && attention.Data is not null;
    referenceWorkspace |= reference.TryGetValue("workspace:probe-00000", out var workspace) && workspace.Data is not null;
}
if (!referenceAttention || !referenceWorkspace) throw new InvalidOperationException("Reference read set did not complete.");
var referenceSeconds = referenceTimer.Elapsed.TotalSeconds;
Console.WriteLine($"Reference cohort: {referenceSeconds:F3}s");
var referenceReads = runner.Reads.Skip(referenceStart).ToArray();
await source.ReadTrackerAsync([], token);
try
{
    using var pin = observer.Pin(keys);
    await observer.StartAsync(token); started = true;
    async Task UntilAsync(Func<bool> condition, int seconds = 90)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Actual cache probe did not converge.");
            await Task.Delay(50, token);
        }
    }
    await UntilAsync(() => observer.Read("attention").Data is { } data && data.GetArrayLength() == (scale ? 40 : 5));
    var bootstrapSeconds = timer.Elapsed.TotalSeconds - referenceSeconds;
    Console.WriteLine($"Actual cache Attention bootstrap: {bootstrapSeconds:F3}s");
    await UntilAsync(() => keys.All(key => observer.Read(key) is { Data: not null, Synchronization: { Journal: "current", Pending: false } }));
    var settledSeconds = timer.Elapsed.TotalSeconds - referenceSeconds;
    Console.WriteLine($"Actual cache settled: {settledSeconds:F3}s");
    var idleStart = runner.Reads.Count;
    var idleCauseStart = causes.Count;
    var pageStart = journal!.Pages;
    var idle = Stopwatch.StartNew();
    while (idle.Elapsed.TotalSeconds < 130)
    {
        using var process = Process.GetCurrentProcess();
        samples.Add(new { seconds = timer.Elapsed.TotalSeconds, process.TotalProcessorTime, process.PrivateMemorySize64,
            process.HandleCount, threads = process.Threads.Count, logicalIo = ProbeMetrics.ReadIo(process),
            resources = keys.Select(key => { var value = observer.Read(key); return new { key, value.ValidatedAgeMs, value.PendingAgeMs, value.Health, value.Synchronization }; }).ToArray() });
        await Task.Delay(1000, token);
    }
    var idleSeconds = idle.Elapsed.TotalSeconds;
    var idleReads = runner.Reads.Skip(idleStart).ToArray();
    var idleCauses = causes.Skip(idleCauseStart).ToArray();
    if (idleCauses.Any(read => read.Reason != "audit"))
        throw new InvalidOperationException("Settled idle issued a source read outside the audit schedule.");
    var idlePages = journal.Pages - pageStart;
    if (idlePages > 27) throw new InvalidOperationException("Journal exceeded one page per five seconds while settled.");
    // A supported local comment must reach the actual Workspace body.
    var beforeComment = runner.Reads.Count;
    var beforeCauses = causes.Count;
    timer.Restart();
    await WriteAsync("comments", "add", "probe-00000", "Actual cache event marker");
    var writeSeconds = timer.Elapsed.TotalSeconds;
    await UntilAsync(() => observer.Read("workspace:probe-00000").Data?.GetRawText().Contains("Actual cache event marker", StringComparison.Ordinal) == true);
    var visibleSeconds = timer.Elapsed.TotalSeconds;
    var commentReads = runner.Reads.Skip(beforeComment).ToArray();
    var commentCauses = causes.Skip(beforeCauses).ToArray();
    if (commentCauses.Any(read => read.Id.StartsWith("comments:", StringComparison.Ordinal) && read.Id != "comments:probe-00000" && read.Reason != "audit"))
        throw new InvalidOperationException("Unrelated history was invalidated for comment.");
    var burstStart = runner.Reads.Count;
    var burstCauseStart = causes.Count;
    var burst = Stopwatch.StartNew();
    for (var index = 0; index < 3; index++)
        await WriteAsync("comments", "add", "probe-00000", $"Coalesced burst marker {index}");
    var burstWriteSeconds = burst.Elapsed.TotalSeconds;
    await UntilAsync(() => observer.Read("workspace:probe-00000").Data?.GetRawText().Contains("Coalesced burst marker 2", StringComparison.Ordinal) == true);
    var burstVisibleSeconds = burst.Elapsed.TotalSeconds;
    var burstReads = runner.Reads.Skip(burstStart).ToArray();
    var burstCauses = causes.Skip(burstCauseStart).ToArray();
    if (burstCauses.Any(read => read.Id.StartsWith("comments:", StringComparison.Ordinal) && read.Id != "comments:probe-00000" && read.Reason != "audit"))
        throw new InvalidOperationException("Burst invalidated unrelated histories.");
    await WriteAsync("dep", "add", "probe-00001", "probe-00002");
    await UntilAsync(() => observer.Read("explorer:blocked:").Data?.GetRawText().Contains("probe-00001", StringComparison.Ordinal) == true);
    await WriteAsync("close", "probe-00002");
    await UntilAsync(() => observer.Read("explorer:ready:").Data?.GetRawText().Contains("probe-00001", StringComparison.Ordinal) == true);
    // Disabled-journal writes are outside the event contract; prove audit fallback.
    await WriteAsync("config", "set", "events-journal", "false");
    await WriteAsync("update", "probe-00003", "--title", "Unjournaled audit marker");
    var audit = Stopwatch.StartNew();
    observer.Read("explorer:all:");
    await UntilAsync(() => observer.Read("explorer:all:").Data?.GetRawText().Contains("Unjournaled audit marker", StringComparison.Ordinal) == true);
    await WriteAsync("config", "set", "events-journal", "true");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
    {
        schema = "nightweaver-cache-probe-v1", revision, scale, fixture = root, sourceIdentity,
        fixtureConfigHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath))),
        referenceSeconds, referenceReads, bootstrapSeconds, settledSeconds, idleSeconds, idlePages, idleReads, idleCauses,
        comment = new { writeSeconds, visibleSeconds, commentReads, commentCauses }, auditFallbackSeconds = audit.Elapsed.TotalSeconds,
        coalescedBurst = new { burstWriteSeconds, burstVisibleSeconds, burstReads, burstCauses },
        journalPages = journal.Pages, journal.Hints, samples,
        source.CommandCount, source.NativeMilliseconds, causes,
        processMetricsScope = "host samples; short-lived child CPU and logical IO are not included", productionTouched = false
    }, new JsonSerializerOptions { WriteIndented = true }), token);
    succeeded = true;
    Console.WriteLine(output);
}
finally
{
    if (started) await observer.StopAsync(token);
    if (!succeeded) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
    { completed = false, fixture = root, samples, causes, reads = runner.Reads.ToArray(), journalPages = journal?.Pages }), token);
    // Retain the isolated fixture and evidence for inspection. No production data.
}

internal sealed class ProbeLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
internal sealed record SourceCause(string Id, string Reason, DateTimeOffset At);
internal sealed record ReadCost(string Command, string? Id, double Milliseconds, int ExitCode);
internal sealed class MeasuredRunner : IProcessRunner
{
    private readonly SystemProcessRunner inner = new();
    internal ConcurrentQueue<ReadCost> Reads { get; } = new();
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var result = await inner.RunAsync(request, cancellationToken);
        if (request.FileName == "bd" && request.Arguments.Contains("--readonly"))
            Reads.Enqueue(new(request.Arguments[2], request.Arguments.Count > 3 ? request.Arguments[3] : null, timer.Elapsed.TotalMilliseconds, result.ExitCode));
        return result;
    }
}
internal sealed class MeasuredJournal(IConsoleJournalReader inner) : IConsoleJournalReader
{
    internal int Pages { get; private set; }
    internal int Hints { get; private set; }
    public async Task<ConsoleJournalPage> ReadAsync(long cursor, CancellationToken token)
    {
        var page = await inner.ReadAsync(cursor, token);
        Pages++; Hints += page.Hints.Count;
        return page;
    }
}
