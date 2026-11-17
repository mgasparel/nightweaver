using System.Security.Cryptography;
using Nightweaver.Application;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record ConsoleJournalPage(string Identity, IReadOnlyList<BeadsInvalidation> Hints);
internal interface IConsoleJournalReader
{
    Task<ConsoleJournalPage> ReadAsync(long cursor, CancellationToken token);
}

// Owns only identity and Web command deadlines. Process, UTF8, limits, parsing and
// settlement remain in the shared reader used by the Orchestrator.
internal sealed class ConsoleJournalReader(ProjectConfig project, IProcessRunner runner, TimeProvider clock, TrackerBridge? tracker = null,
    IBeadsInvalidationClient? journalClient = null) : IConsoleJournalReader
{
    private readonly CommandRunner commands = new(runner, clock, tracker);
    private TargetRepositoryValidator? validator;
    private readonly IBeadsInvalidationClient client = journalClient ?? new BeadsJournalClient(runner, timeProvider: clock);
    private string? enabledIdentity;
    private string? deliveredIdentity;
    public async Task<ConsoleJournalPage> ReadAsync(long cursor, CancellationToken token)
    {
        commands.BeginTurn();
        var repository = await (validator ??= new(commands)).ValidateAsync(project.TargetRepository, project.ConfigFilePath, token);
        var branch = await commands.RunAsync(GitCommand.Create(["symbolic-ref", "--quiet", "HEAD"], repository.Path), token);
        if (branch.ExitCode != 0) throw new InvalidDataException("Cannot establish console journal branch identity.");
        var trackerBranch = await commands.RunAsync(BeadsCommand.CreateJournalBranch(repository.Path), token);
        if (trackerBranch.ExitCode != 0) throw new InvalidDataException("Cannot establish tracker branch identity.");
        using var branchDocument = System.Text.Json.JsonDocument.Parse(trackerBranch.StandardOutput);
        if (!branchDocument.RootElement.TryGetProperty("current", out var currentBranch)
            || currentBranch.ValueKind != System.Text.Json.JsonValueKind.String || currentBranch.GetString() is not { Length: > 0 and <= 1024 })
            throw new InvalidDataException("Cannot establish tracker branch identity.");
        var metadata = await File.ReadAllBytesAsync(Path.Combine(repository.Path, ".beads", "metadata.json"), token);
        var identity = repository.GitCommonDirectory + "|" + branch.StandardOutput.Trim() + "|"
            + Convert.ToHexString(SHA256.HashData(metadata)) + "|" + currentBranch.GetString()
            + "|" + File.GetCreationTimeUtc(Path.Combine(repository.Path, ".beads", "metadata.json")).Ticks
            + "|" + Directory.GetCreationTimeUtc(Path.Combine(repository.Path, ".beads")).Ticks;
        if (enabledIdentity != identity)
        {
            await commands.ObserveAsync(async ct => { await client.EnableAsync(repository, ct); return true; }, token);
            enabledIdentity = identity;
            // The caller must discard a cursor from a different session before
            // applying this page; always start the changed session at zero.
        }
        if (deliveredIdentity != identity) cursor = 0;
        // Preparation must not consume the finite page's observation deadline.
        // In particular two individually successful bd commands can exceed two
        // seconds together. Each command settles before releasing admission.
        var hints = await commands.ObserveAsync(ct => client.ReadInvalidationsAsync(repository, cursor, 256, ct), token);
        deliveredIdentity = identity;
        return new(identity, hints);
    }

    private sealed class CommandRunner(IProcessRunner inner, TimeProvider clock, TrackerBridge? tracker) : IProcessRunner
    {
        private TimeSpan elapsed;
        internal void BeginTurn() => elapsed = TimeSpan.Zero;
        internal async Task<T> ObserveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
        {
            var remaining = TimeSpan.FromSeconds(4) - elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Console journal native work exceeded its four-second turn budget.");
            var gate = tracker?.ObservationGate;
            if (gate is not null) await gate.WaitObserverAsync(token);
            var started = clock.GetTimestamp();
            try
            {
                using var deadline = new CancellationTokenSource(
                    remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                var result = await operation(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally { elapsed += clock.GetElapsedTime(started); gate?.Release(); }
        }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
            => ObserveAsync(ct => inner.RunAsync(request with { Timeout = TimeSpan.FromSeconds(2) }, ct), token);
    }
}
