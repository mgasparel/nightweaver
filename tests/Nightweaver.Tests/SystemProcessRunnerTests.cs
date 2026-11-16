using System.Globalization;
using Nightweaver.Inspection;
using Nightweaver.Tests.AgentInvocationFixture;

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
        var result = await runner.RunAsync(
            FixtureRequest(temp.Path, ["flood", size.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(30)),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string('O', size), result.StandardOutput);
        Assert.Equal(new string('E', size), result.StandardError);
    }

    [Fact]
    public async Task OutputLimitKillsAndSettlesBeforeReturningAnUnusablePrefix()
    {
        using var temp = new TempDirectory();
        var request = FixtureRequest(temp.Path, ["flood-hold", "1100000"], TimeSpan.FromSeconds(10))
            with { MaximumOutputBytes = 2048 };
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => runner.RunAsync(request, TestContext.Current.CancellationToken));
        Assert.Contains("exceeded read limit", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.StandardError);
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
            var request = FixtureRequest(temp.Path, ["environment", variable], TimeSpan.FromSeconds(10));
            var inherited = await runner.RunAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(0, inherited.ExitCode);
            Assert.Equal("parent-value", inherited.StandardOutput);
            var environment = new Dictionary<string, string?>(request.Environment) { [variable] = null };
            var result = await runner.RunAsync(request with { Environment = environment }, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static ProcessRequest FixtureRequest(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout) =>
        Request("dotnet", [typeof(FixtureMarker).Assembly.Location, .. arguments], workingDirectory,
            new Dictionary<string, string?>
            {
                ["NIGHTWEAVER_FIXTURE_ACTION"] = null,
                ["NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION"] = null,
                ["NIGHTWEAVER_INVOCATION_PURPOSE"] = null
            }, timeout);

    private static ProcessRequest Request(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null) =>
        new(fileName, arguments, workingDirectory, environment ?? new Dictionary<string, string?>(), timeout ?? TimeSpan.FromSeconds(10));
}
