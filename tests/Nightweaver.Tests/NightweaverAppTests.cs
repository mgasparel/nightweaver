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
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(128, string.Empty, "fatal: not a git repository\n")));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await NightweaverApp.RunAsync(["--config", config, "--dry-run"], CreateAppEnvironment(host, caller, output, error), runner, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("is not a Git working tree", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", error.ToString(), StringComparison.Ordinal);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task DryRunWritesTheExactHeaderAndIsByteStable()
    {
        using var temp = new TempDirectory();
        using var repository = new GitFixture("repo with spaces");
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
            $"  name: demo\n" +
            $"  config: {configPath}\n" +
            $"  nightweaver_root: {host}\n" +
            $"  target_repository: {repository.Path}\n" +
            "  default_branch: main\n" +
            "  recovery_actor: nightweaver:demo\n" +
            $"  worktrees_dir: {Path.Combine(host, "worktrees", "demo")}\n" +
            $"  tmp_dir: {Path.Combine(host, "tmp", "demo")}\n" +
            $"  logs_dir: {Path.Combine(host, "logs", "demo")}\n";

        Assert.Equal(0, firstCode);
        Assert.Equal(0, secondCode);
        Assert.Equal(expected, firstOutput.ToString());
        Assert.Equal(expected, secondOutput.ToString());
        Assert.Equal(string.Empty, firstError.ToString());
        Assert.Equal(string.Empty, secondError.ToString());
        Assert.Equal(2, runner.Requests.Count);
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal("git", request.FileName);
            Assert.Contains("rev-parse", request.Arguments);
            Assert.Equal("0", request.Environment["GIT_OPTIONAL_LOCKS"]);
        });
    }

    [Fact]
    public async Task DryRunAndFailureLeaveHostAndTargetFilesystemsUnchanged()
    {
        using var temp = new TempDirectory();
        using var repository = new GitFixture();
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
        using var repository = new GitFixture("repo with spaces");
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
        Assert.Contains($"  nightweaver_root: {host}\n", projectRun.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"  config: {Path.Combine(host, "projects", "demo.json")}\n", projectRun.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, projectRun.StandardError);
        Assert.Equal(0, configRun.ExitCode);
        Assert.Contains($"  config: {Path.GetFullPath(configFromCaller)}\n", configRun.StandardOutput, StringComparison.Ordinal);
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
