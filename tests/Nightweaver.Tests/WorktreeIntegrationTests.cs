using System.Reflection;
using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

// Reusable real-tool fixture for downstream dry-run/readiness integration gates.
// All setup mutations occur before callers take their measurement snapshots.
internal sealed class StartupSweepFixture : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly Dictionary<string, string> seededIds;
    internal BeadsFixture Target { get; } = new("target repo with spaces");
    internal string Host { get; }
    internal string Caller { get; }
    internal string Config { get; }
    internal string External { get; }
    internal IReadOnlyList<string> StaleIds { get; }
    internal string PlannedBeadId => seededIds["planned"];
    internal IReadOnlyList<string> ActiveIds { get; }
    internal IReadOnlyList<string> PausedIds { get; }
    internal IReadOnlyList<string> OrphanIds { get; }

    internal StartupSweepFixture(string? publishedArtifactDirectory = null)
    {
        Host = temp.CreateDirectory("nightweaver host");
        Caller = temp.CreateDirectory("unrelated caller");
        External = temp.CreateDirectory("external");
        temp.WriteFile("external/sentinel", "never inspect or modify through a link");
        var appDirectory = publishedArtifactDirectory ?? Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "NightweaverAppDir").Value!;
        foreach (var directory in Directory.EnumerateDirectories(appDirectory, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(Host, Path.GetRelativePath(appDirectory, directory)));
        foreach (var file in Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(Host, Path.GetRelativePath(appDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(destination, File.GetUnixFileMode(file)); }
                catch (PlatformNotSupportedException) { }
            }
        }
        Config = temp.WriteFile("nightweaver host/projects/demo.json", JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = Target.Path } }));
        temp.WriteFile("nightweaver host/logs/demo/retained.log", "preserve logs");
        Target.Run("git", ["commit", "--allow-empty", "-m", "fixture base"]);
        // These rows describe a scenario, not create/update command contracts.
        // Import them in one transaction before constructing real Git worktrees.
        var seeds = new[]
        {
            new { title = "closed", status = "closed", labels = Array.Empty<string>() },
            // Completed Planning has passed verification and guarded claim handoff.
            // A still-claimed planned Product instead requires explicit recovery.
            new { title = "planned", status = "open", labels = new[] { "planned" } },
            new { title = "paused", status = "in_progress", labels = new[] { "product-decision-required" } },
            new { title = "needs human", status = "in_progress", labels = new[] { "needs-human" } },
            new { title = "active", status = "in_progress", labels = Array.Empty<string>() },
            new { title = "merge", status = "open", labels = new[] { "stage:merge" } },
            new { title = "locked closed", status = "closed", labels = Array.Empty<string>() },
            new { title = "missing path", status = "closed", labels = Array.Empty<string>() },
            new { title = "orphan tmp", status = "closed", labels = Array.Empty<string>() }
        };
        var seedFile = Path.Combine(temp.Path, "startup-seed.jsonl");
        File.WriteAllText(seedFile, string.Join('\n', seeds.Select(seed => JsonSerializer.Serialize(new
        {
            seed.title, seed.status, seed.labels,
            issue_type = seed.title == "planned" ? "feature" : "task",
            priority = 2,
            assignee = seed.status == "closed" || seed.title == "planned" ? null : "nightweaver:demo"
        }))));
        Target.Run("bd", ["import", seedFile, "--json"]);
        seededIds = BeadsReader.Parse(Target.Run("bd", BeadsCommand.Create(Target.Path).Arguments)).ById.Values
            .ToDictionary(bead => bead.Payload.GetProperty("title").GetString()!, bead => bead.Id, StringComparer.Ordinal);
        File.Delete(seedFile);
        var closed = Worktree("closed", named: true);
        var planned = Worktree("planned");
        var paused = Worktree("paused");
        var needsHuman = Worktree("needs human");
        var active = Worktree("active");
        var merge = Worktree("merge", named: true);
        Target.Run("bd", ["dep", "add", active, merge]);
        var locked = Worktree("locked closed");
        Target.Run("git", ["worktree", "lock", "--reason", "human inspection\nkeep this worktree", WorktreePath(locked)]);
        var missing = Worktree("missing path");
        Directory.Delete(WorktreePath(missing), recursive: true);
        Target.Run("git", ["worktree", "add", "--detach", WorktreePath("fixture.absent"), "HEAD"]);
        temp.CreateDirectory("nightweaver host/worktrees/demo/unregistered");
        Target.Run("git", ["worktree", "add", "--detach", Path.Combine(Host, "worktrees", "elsewhere", closed), "HEAD"]);
        Target.Run("git", ["worktree", "add", "-b", "nightweaver/external", Path.Combine(External, "foreign checkout"), "HEAD"]);
        WorktreeInspectionTests.CreateDirectoryLink(WorktreePath("linked-worktree"), External);
        temp.CreateDirectory("nightweaver host/tmp/demo");
        WorktreeInspectionTests.CreateDirectoryLink(Path.Combine(Host, "tmp", "demo", "linked-tmp"), External);
        StaleIds = Array.AsReadOnly(new[] { closed, planned });
        ActiveIds = Array.AsReadOnly(new[] { active, merge });
        PausedIds = Array.AsReadOnly(new[] { paused, needsHuman });
        OrphanIds = Array.AsReadOnly(new[] { closed, planned, seededIds["orphan tmp"] });
        foreach (var id in new[] { closed, planned, paused, needsHuman, active, merge, locked, missing, seededIds["orphan tmp"], "fixture.absent", "unregistered", "linked-worktree" })
            temp.WriteFile($"nightweaver host/tmp/demo/{id}/invocation/output", "preserve invocation data");
    }

    internal string WorktreePath(string id) => Path.Combine(Host, "worktrees", "demo", id);
    private string Worktree(string title, bool named = false)
    {
        var id = seededIds[title];
        if (named) Target.Run("git", ["worktree", "add", "-b", "nightweaver/" + id, WorktreePath(id), "HEAD"]);
        else Target.Run("git", ["worktree", "add", "--detach", WorktreePath(id), "HEAD"]);
        return id;
    }

    public void Dispose()
    {
        temp.Dispose();
        Target.Dispose();
    }
}
