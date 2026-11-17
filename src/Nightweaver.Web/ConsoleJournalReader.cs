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

// Owns only identity and the Web deadline. Process, UTF8, limits, parsing and
// settlement remain in the shared reader used by the Orchestrator.
internal sealed class ConsoleJournalReader(ProjectConfig project, IProcessRunner runner, TimeProvider clock, TrackerBridge? tracker = null) : IConsoleJournalReader
{
    private readonly TargetRepositoryValidator validator = new(runner);
    private readonly BeadsJournalClient client = new(runner, timeProvider: clock);
    private string? enabledIdentity;
    public async Task<ConsoleJournalPage> ReadAsync(long cursor, CancellationToken token)
    {
        var gate = tracker?.ObservationGate;
        if (gate is not null) await gate.WaitObserverAsync(token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var repository = await validator.ValidateAsync(project.TargetRepository, project.ConfigFilePath, linked.Token);
            var branch = await runner.RunAsync(GitCommand.Create(["symbolic-ref", "--quiet", "HEAD"], repository.Path)
                with { Timeout = TimeSpan.FromSeconds(2) }, linked.Token);
            if (branch.ExitCode != 0) throw new InvalidDataException("Cannot establish console journal branch identity.");
            var trackerBranch = await runner.RunAsync(BeadsCommand.CreateJournalBranch(repository.Path), linked.Token);
            if (trackerBranch.ExitCode != 0) throw new InvalidDataException("Cannot establish tracker branch identity.");
            using var branchDocument = System.Text.Json.JsonDocument.Parse(trackerBranch.StandardOutput);
            if (!branchDocument.RootElement.TryGetProperty("current", out var currentBranch)
                || currentBranch.ValueKind != System.Text.Json.JsonValueKind.String || currentBranch.GetString() is not { Length: > 0 and <= 1024 })
                throw new InvalidDataException("Cannot establish tracker branch identity.");
            var metadata = await File.ReadAllBytesAsync(Path.Combine(repository.Path, ".beads", "metadata.json"), linked.Token);
            var identity = repository.GitCommonDirectory + "|" + branch.StandardOutput.Trim() + "|"
                + Convert.ToHexString(SHA256.HashData(metadata)) + "|" + currentBranch.GetString()
                + "|" + File.GetCreationTimeUtc(Path.Combine(repository.Path, ".beads", "metadata.json")).Ticks
                + "|" + Directory.GetCreationTimeUtc(Path.Combine(repository.Path, ".beads")).Ticks;
            if (enabledIdentity != identity)
            {
                await client.EnableAsync(repository, linked.Token);
                enabledIdentity = identity;
                // The caller must discard a cursor from a different session before
                // applying this page; always start the changed session at zero.
                cursor = 0;
            }
            var hints = await client.ReadInvalidationsAsync(repository, cursor, 256, linked.Token);
            return new(identity, hints);
        }
        finally { gate?.Release(); }
    }
}
