using System.Reflection;
using System.Text.Json;
using Nightweaver;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class NightweaverAppTests
{
    [Fact]
    public async Task HelpAndCliErrorsDoNotProbeToolsOrWriteStdoutOnFailure()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var runner = new RecordingProcessRunner((_, _) => throw new InvalidOperationException("unexpected process launch"));

        var helpOutput = new StringWriter();
        var helpError = new StringWriter();
        var helpCode = await NightweaverApp.RunAsync(["--help", "--project", "demo", "--config", "elsewhere.json"], CreateAppEnvironment(host, caller, helpOutput, helpError), runner, CancellationToken.None);
        Assert.Equal(0, helpCode);
        Assert.Equal(Nightweaver.Cli.HelpText.Usage, helpOutput.ToString());
        Assert.Equal(string.Empty, helpError.ToString());

        var badOutput = new StringWriter();
        var badError = new StringWriter();
        var badCode = await NightweaverApp.RunAsync(["--help", "--unknown"], CreateAppEnvironment(host, caller, badOutput, badError), runner, CancellationToken.None);
        Assert.Equal(2, badCode);
        Assert.Equal(string.Empty, badOutput.ToString());
        Assert.Contains("unknown option '--unknown'", badError.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task UnsupportedLiveModeFailsBeforeConfigurationOrToolAccess()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var runner = new RecordingProcessRunner((_, _) => throw new InvalidOperationException("unexpected process launch"));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await NightweaverApp.RunAsync(
            ["--config", Path.Combine(host, "missing.json")],
            CreateAppEnvironment(host, caller, output, error),
            runner,
            CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("only '--dry-run' is supported", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task InvalidConfigurationFailsWithoutLaunchingGit()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var config = temp.WriteFile("broken.json", "{\"name\":\"demo\",\"target_repository\":{}}");
        var runner = new RecordingProcessRunner((_, _) => throw new InvalidOperationException("unexpected process launch"));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await NightweaverApp.RunAsync(["--config", config, "--dry-run"], CreateAppEnvironment(host, caller, output, error), runner, CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("$.target_repository.path", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task InvalidSchemaReportsEveryErrorInDocumentOrderBeforeGitRuns()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var config = temp.WriteFile("broken.json", """
            {"name":"demo","concurrency":{"unknown":1,"max_concurrent_planning":0},"agent_defaults":{"cli":"gemini","timeout_minutes":0},"target_repository":{"path":"missing","default_branch":"main"}}
            """);
        var runner = new RecordingProcessRunner((_, _) => throw new InvalidOperationException("invalid config must not launch a tool"));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await NightweaverApp.RunAsync(
            ["--config", config, "--dry-run"],
            CreateAppEnvironment(host, caller, output, error),
            runner,
            CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output.ToString());
        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal(
            ["$.concurrency.unknown", "$.concurrency.max_concurrent_planning", "$.agent_defaults.cli", "$.agent_defaults.timeout_minutes"],
            lines.Select(line => line.Split(": ", StringSplitOptions.None)[3]));
        Assert.All(lines, line => Assert.StartsWith($"nightweaver: error: {Path.GetFullPath(config)}: ", line, StringComparison.Ordinal));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task InvalidDefaultBranchLaunchesGitAndReportsAConfigurationError()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var config = temp.WriteFile("project.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = Path.Combine(temp.Path, "missing repository"), default_branch = "bad..branch" }
        }));
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(1, string.Empty, "invalid branch name")));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await NightweaverApp.RunAsync(
            ["--config", config, "--dry-run"],
            CreateAppEnvironment(host, caller, output, error),
            runner,
            CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains($"{Path.GetFullPath(config)}: $.target_repository.default_branch: 'bad..branch' is not a valid Git branch name", error.ToString(), StringComparison.Ordinal);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(["check-ref-format", "--branch", "bad..branch"], request.Arguments);
        Assert.Equal(Path.GetTempPath(), request.WorkingDirectory);
    }

    [Fact]
    public async Task TargetRepositoryGitFailureMapsToInspectionExitCodeAndKeepsStdoutEmpty()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var plainDirectory = temp.CreateDirectory("not a repository");
        var config = temp.WriteFile("project.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = plainDirectory }
        }));
        var runner = new RecordingProcessRunner((request, _) => Task.FromResult(
            request.Arguments.Contains("check-ref-format")
                ? new ProcessResult(0, string.Empty, string.Empty)
                : new ProcessResult(128, string.Empty, "fatal: not a git repository\n")));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await NightweaverApp.RunAsync(["--config", config, "--dry-run"], CreateAppEnvironment(host, caller, output, error), runner, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("is not a Git working tree", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains("check-ref-format", runner.Requests[0].Arguments);
        Assert.Contains("rev-parse", runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task DryRunWritesTheExactHeaderAndIsByteStable()
    {
        using var temp = new TempDirectory();
        using var repository = new BeadsFixture("repo with spaces");
        var host = temp.CreateDirectory("nightweaver host");
        var caller = temp.CreateDirectory("unrelated caller");
        var configPath = WriteProjectConfig(host, "demo", repository.Path);
        var runner = new RecordingProcessRunner((request, token) => new SystemProcessRunner().RunAsync(request, token));

        var firstOutput = new StringWriter();
        var firstError = new StringWriter();
        var firstCode = await NightweaverApp.RunAsync(["--project", "demo", "--dry-run"], CreateAppEnvironment(host, caller, firstOutput, firstError), runner, CancellationToken.None);
        var secondOutput = new StringWriter();
        var secondError = new StringWriter();
        var secondCode = await NightweaverApp.RunAsync(["--project", "demo", "--dry-run"], CreateAppEnvironment(host, caller, secondOutput, secondError), runner, CancellationToken.None);

        var expected =
            "Nightweaver dry run: hypothetical startup snapshot; no actions are performed.\n\n" +
            "Project\n" +
            $"  name: \"demo\"\n" +
            $"  config: {JsonSerializer.Serialize(configPath)}\n" +
            $"  nightweaver_root: {JsonSerializer.Serialize(host)}\n" +
            $"  target_repository: {JsonSerializer.Serialize(repository.Path)}\n" +
            "  default_branch: \"main\"\n" +
            "  recovery_actor: \"nightweaver:demo\"\n" +
            $"  worktrees_dir: {JsonSerializer.Serialize(Path.Combine(host, "worktrees", "demo"))}\n" +
            $"  tmp_dir: {JsonSerializer.Serialize(Path.Combine(host, "tmp", "demo"))}\n" +
            $"  logs_dir: {JsonSerializer.Serialize(Path.Combine(host, "logs", "demo"))}\n" +
            "\nStartup claim releases (hypothetical)\n  (none)\n\nWorktree removals (hypothetical)\n  (none)\n\nRetained worktrees\n  (none)\n\nOrphan tmp deletions (hypothetical)\n  (none)\n\nStartup diagnostics\n  (none)\n" +
            "\nConfiguration\n" +
            "  max_concurrent_planning: 2\n" +
            "  max_concurrent_delivery: 3\n" +
            "  merger_slots: 1\n" +
            "  max_active_plan_beads: 5\n" +
            "  max_repair_cycles: 3\n" +
            "  max_operational_retries: 2\n" +
            "\nAgent roles\n" +
            "  agent_defaults: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n" +
            "  planner: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n" +
            "  plan_challenger: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n" +
            "  implementer: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n" +
            "  reviewer: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n" +
            "  qa: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n" +
            "  merger: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n" +
            "  classifier: cli=claude model=(agent-cli-default) effort=high timeout_minutes=30\n";

        Assert.Equal(0, firstCode);
        Assert.Equal(0, secondCode);
        Assert.Equal(expected, firstOutput.ToString());
        Assert.Equal(expected, secondOutput.ToString());
        Assert.Equal(string.Empty, firstError.ToString());
        Assert.Equal(string.Empty, secondError.ToString());
        Assert.Equal(8, runner.Requests.Count);
        Assert.Equal(2, runner.Requests.Count(request => request.FileName == "bd"));
        Assert.Equal(2, runner.Requests.Count(request => request.Arguments.Contains("check-ref-format")));
        Assert.Equal(2, runner.Requests.Count(request => request.Arguments.Contains("rev-parse")));
        Assert.All(runner.Requests, request => Assert.Equal("0", request.Environment["GIT_OPTIONAL_LOCKS"]));
    }

    [Fact]
    public async Task DryRunPrintsResolvedRoleSettingsInCanonicalOrder()
    {
        using var temp = new TempDirectory();
        using var repository = new BeadsFixture();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        var configPath = Path.Combine(host, "projects", "demo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = repository.Path },
            agent_defaults = new { cli = "codex", model = "base-model", effort = "low", timeout_minutes = 12 },
            roles = new
            {
                plan_challenger = new { cli = "claude", model = (string?)null, timeout_minutes = 20 },
                classifier = new { effort = "high" }
            }
        }));
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new RecordingProcessRunner((request, token) => new SystemProcessRunner().RunAsync(request, token));

        var code = await NightweaverApp.RunAsync(
            ["--project", "demo", "--dry-run"],
            CreateAppEnvironment(host, caller, output, error),
            runner,
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error.ToString());
        var marker = output.ToString().IndexOf("\nAgent roles\n", StringComparison.Ordinal);
        Assert.True(marker >= 0);
        Assert.Equal(
            "\nAgent roles\n" +
            "  agent_defaults: cli=codex model=base-model effort=low timeout_minutes=12\n" +
            "  planner: cli=codex model=base-model effort=low timeout_minutes=12\n" +
            "  plan_challenger: cli=claude model=(agent-cli-default) effort=low timeout_minutes=20\n" +
            "  implementer: cli=codex model=base-model effort=low timeout_minutes=12\n" +
            "  reviewer: cli=codex model=base-model effort=low timeout_minutes=12\n" +
            "  qa: cli=codex model=base-model effort=low timeout_minutes=12\n" +
            "  merger: cli=codex model=base-model effort=low timeout_minutes=12\n" +
            "  classifier: cli=codex model=base-model effort=high timeout_minutes=12\n",
            output.ToString()[marker..]);
    }

    [Fact]
    public async Task DryRunAndFailureLeaveHostAndTargetFilesystemsUnchanged()
    {
        using var temp = new TempDirectory();
        using var repository = new BeadsFixture();
        var host = temp.CreateDirectory("host");
        var caller = temp.CreateDirectory("caller");
        WriteProjectConfig(host, "demo", repository.Path);
        var invalidConfig = temp.WriteFile("invalid.json", "{\"name\":\"demo\",\"target_repository\":{}}");
        var runner = new SystemProcessRunner();

        var hostBeforeSuccess = Snapshot.FileTree(host);
        var repositoryBeforeSuccess = Snapshot.FileTree(repository.Path);
        var statusBeforeSuccess = Snapshot.GitStatus(repository.Path);
        var successCode = await NightweaverApp.RunAsync(
            ["--project", "demo", "--dry-run"],
            CreateAppEnvironment(host, caller, new StringWriter(), new StringWriter()),
            runner,
            CancellationToken.None);
        Assert.Equal(0, successCode);
        Assert.Equal(hostBeforeSuccess, Snapshot.FileTree(host));
        Assert.Equal(repositoryBeforeSuccess, Snapshot.FileTree(repository.Path));
        Assert.Equal(statusBeforeSuccess, Snapshot.GitStatus(repository.Path));

        var hostBeforeFailure = Snapshot.FileTree(host);
        var repositoryBeforeFailure = Snapshot.FileTree(repository.Path);
        var statusBeforeFailure = Snapshot.GitStatus(repository.Path);
        var failureOutput = new StringWriter();
        var failureError = new StringWriter();
        var failureCode = await NightweaverApp.RunAsync(
            ["--config", invalidConfig, "--dry-run"],
            CreateAppEnvironment(host, caller, failureOutput, failureError),
            runner,
            CancellationToken.None);

        Assert.Equal(2, failureCode);
        Assert.Equal(string.Empty, failureOutput.ToString());
        Assert.NotEmpty(failureError.ToString());
        Assert.Equal(hostBeforeFailure, Snapshot.FileTree(host));
        Assert.Equal(repositoryBeforeFailure, Snapshot.FileTree(repository.Path));
        Assert.Equal(statusBeforeFailure, Snapshot.GitStatus(repository.Path));
        Assert.False(Directory.Exists(Path.Combine(host, "worktrees")));
        Assert.False(Directory.Exists(Path.Combine(host, "tmp")));
        Assert.False(Directory.Exists(Path.Combine(host, "logs")));
    }

    [Fact]
    public async Task ApphostResolvesProjectRootIndependentlyFromCallerDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        using var repository = new BeadsFixture("repo with spaces");
        var host = temp.CreateDirectory("copied apphost");
        var caller = temp.CreateDirectory("unrelated caller");
        var appDirectory = GetNightweaverAppDirectory();
        CopyDirectory(appDirectory, host);
        WriteProjectConfig(host, "demo", repository.Path);
        var configFromCaller = temp.WriteFile(
            "unrelated caller/settings/demo.json",
            JsonSerializer.Serialize(new
            {
                name = "demo",
                target_repository = new { path = repository.Path }
            }));

        var projectRun = await RunApphost(host, caller, ["--project", "demo", "--dry-run"]);
        var relativeConfigPath = Path.GetRelativePath(caller, configFromCaller);
        var configRun = await RunApphost(host, caller, ["--config", relativeConfigPath, "--dry-run"]);
        var helpRun = await RunApphost(host, caller, ["--help"]);
        var missingArgumentsRun = await RunApphost(host, caller, []);

        Assert.Equal(0, projectRun.ExitCode);
        Assert.Contains($"  nightweaver_root: {JsonSerializer.Serialize(host)}\n", projectRun.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"  config: {JsonSerializer.Serialize(Path.Combine(host, "projects", "demo.json"))}\n", projectRun.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, projectRun.StandardError);
        Assert.Equal(0, configRun.ExitCode);
        Assert.Contains($"  config: {JsonSerializer.Serialize(Path.GetFullPath(configFromCaller))}\n", configRun.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, helpRun.ExitCode);
        Assert.Contains("Usage: nightweaver", helpRun.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(2, missingArgumentsRun.ExitCode);
        Assert.Equal(string.Empty, missingArgumentsRun.StandardOutput);
    }

    private static AppEnvironment CreateAppEnvironment(string root, string currentDirectory, TextWriter output, TextWriter error) =>
        new(root, currentDirectory, output, error);

    private static string WriteProjectConfig(string host, string name, string targetRepositoryPath)
    {
        var configPath = Path.Combine(host, "projects", name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            name,
            target_repository = new { path = targetRepositoryPath }
        }));
        return Path.GetFullPath(configPath);
    }

    private static async Task<ProcessResult> RunApphost(string host, string workingDirectory, IReadOnlyList<string> args)
    {
        var request = new ProcessRequest(
            Path.Combine(host, "nightweaver.exe"),
            args,
            workingDirectory,
            new Dictionary<string, string?>
            {
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = Path.Combine(workingDirectory, "missing-global-config")
            },
            TimeSpan.FromSeconds(60));
        return await new SystemProcessRunner().RunAsync(request, CancellationToken.None);
    }

    private static string GetNightweaverAppDirectory()
    {
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NightweaverAppDir");
        return metadata.Value ?? throw new InvalidOperationException("NightweaverAppDir metadata is empty");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
