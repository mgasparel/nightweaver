using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class SystemProcessRunnerTests
{
    private readonly SystemProcessRunner runner = new();

    [Fact]
    public async Task ProcessArgumentsPreserveSpacesAndEmbeddedQuotes()
    {
        using var fixture = new GitFixture();
        var configPath = Path.Combine(fixture.Path, "config file with spaces.ini");
        const string value = "a value with spaces and \"double quotes\"";
        var write = await runner.RunAsync(Request("git", ["config", "--file", configPath, "nightweaver.probe", value], fixture.Path), CancellationToken.None);
        var read = await runner.RunAsync(Request("git", ["config", "--file", configPath, "nightweaver.probe"], fixture.Path), CancellationToken.None);

        Assert.Equal(0, write.ExitCode);
        Assert.Equal(value + "\n", read.StandardOutput);
        Assert.Equal(string.Empty, read.StandardError);
    }

    [Fact]
    public async Task ProcessRunnerDrainsLargeStdoutAndStderrConcurrently()
    {
        using var temp = new TempDirectory();
        const int size = 1_100_000;
        var command = $"$stdout = 'x' * {size}; $stderr = 'y' * {size}; [Console]::Out.Write($stdout); [Console]::Error.Write($stderr)";
        var result = await runner.RunAsync(
            Request("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command], temp.Path, timeout: TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(size, result.StandardOutput.Length);
        Assert.Equal(size, result.StandardError.Length);
    }

    [Fact]
    public async Task ProcessTimeoutKillsTheProcessTree()
    {
        using var temp = new TempDirectory();
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => runner.RunAsync(
            Request("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], temp.Path, timeout: TimeSpan.FromMilliseconds(300)),
            CancellationToken.None));

        Assert.Contains("timed out after", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingExecutableHasAPathDiagnostic()
    {
        using var temp = new TempDirectory();
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => runner.RunAsync(
            Request("nightweaver-missing-executable-7f3a91.exe", [], temp.Path),
            CancellationToken.None));

        Assert.Contains("was not found on PATH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsCommandLineLaunchFailureUsesToolExecutionException()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var oversizedArgument = new string('x', 40_000);
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => runner.RunAsync(
            Request("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", oversizedArgument], temp.Path),
            CancellationToken.None));

        Assert.Contains("could not be started", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullEnvironmentOverrideRemovesAParentValue()
    {
        using var temp = new TempDirectory();
        const string variable = "NIGHTWEAVER_TEST_REMOVE_ME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "parent-value");
        try
        {
            var request = Request(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Write([Environment]::GetEnvironmentVariable('NIGHTWEAVER_TEST_REMOVE_ME'))"],
                temp.Path,
                new Dictionary<string, string?> { [variable] = null });
            var result = await runner.RunAsync(request, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static ProcessRequest Request(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null) =>
        new(fileName, arguments, workingDirectory, environment ?? new Dictionary<string, string?>(), timeout ?? TimeSpan.FromSeconds(10));
}
