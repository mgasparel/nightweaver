using System.Diagnostics;
using System.Text;

namespace Nightweaver.Tests;

internal sealed record RawProcessResult(int ExitCode, byte[] StandardOutput, byte[] StandardError)
{
    internal string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
    internal string StandardErrorText => Encoding.UTF8.GetString(StandardError);
}

public sealed class PublishedCliArtifactFixture : IDisposable
{
    private readonly TempDirectory temp = new();

    public string RepositoryRoot { get; }
    public string PublishDirectory { get; }
    public string TrapDirectory { get; }
    public string TrapRecordPath { get; }
    public string PublishCommand { get; }
    public string AppHostPath => Path.Combine(PublishDirectory, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");

    public void CopyPublishedAppTo(string destination) => CopyDirectory(PublishDirectory, destination);

    public PublishedCliArtifactFixture()
    {
        RepositoryRoot = FindRepositoryRoot();
        PublishDirectory = temp.CreateDirectory("published nightweaver");
        var helperPublishDirectory = temp.CreateDirectory("published agent trap");
        TrapDirectory = temp.CreateDirectory("agent traps");
        TrapRecordPath = Path.Combine(temp.Path, "agent invocations.jsonl");

        var projectPath = Path.GetFullPath(Path.Combine(RepositoryRoot, "src", "Nightweaver", "Nightweaver.csproj"));
        PublishCommand = $"dotnet publish {projectPath} -c Release --self-contained false -p:UseAppHost=true -o {PublishDirectory}";
        RunDotnetPublish(projectPath, PublishDirectory);

        var trapProjectPath = Path.GetFullPath(Path.Combine(RepositoryRoot, "tests", "AgentCliTrap", "AgentCliTrap.csproj"));
        RunDotnetPublish(trapProjectPath, helperPublishDirectory);
        CopyDirectory(helperPublishDirectory, TrapDirectory);

        var helperAppHost = Path.Combine(helperPublishDirectory, OperatingSystem.IsWindows() ? "AgentCliTrap.exe" : "AgentCliTrap");
        foreach (var name in new[] { "claude", "codex" })
        {
            var trapPath = Path.Combine(TrapDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            File.Copy(helperAppHost, trapPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(trapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        SelfTestTrapsAndPrerequisites();
    }

    public Dictionary<string, string?> EnvironmentWithAgentTraps(IReadOnlyDictionary<string, string?>? inherited = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (inherited is not null)
            foreach (var (key, value) in inherited) environment[key] = value;

        var inheritedPath = environment.TryGetValue("PATH", out var pathOverride) && pathOverride is not null
            ? pathOverride
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        environment["PATH"] = TrapDirectory + Path.PathSeparator + inheritedPath;
        environment["NIGHTWEAVER_AGENT_TRAP_RECORD"] = TrapRecordPath;
        return environment;
    }

    public void AssertNoAgentInvocations()
    {
        if (File.Exists(TrapRecordPath))
            throw new InvalidOperationException($"Agent CLI trap recorded an unexpected invocation: {File.ReadAllText(TrapRecordPath)}");
    }

    private void SelfTestTrapsAndPrerequisites()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalRecordPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_AGENT_TRAP_RECORD");
        var environment = EnvironmentWithAgentTraps();
        try
        {
            // Windows resolves a bare ProcessStartInfo.FileName using the parent's PATH.
            // Give the test process the same trap-first PATH that the published child receives.
            Environment.SetEnvironmentVariable("PATH", environment["PATH"]);
            Environment.SetEnvironmentVariable("NIGHTWEAVER_AGENT_TRAP_RECORD", TrapRecordPath);
            foreach (var tool in new[] { "git", "bd" })
            {
                var result = ProcessTool.RunAsync(tool, ["--version"], RepositoryRoot, environment, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Required real-tool prerequisite '{tool}' is not reachable with the process-guard PATH: {result.StandardErrorText}");
            }

            var trapResults = new List<(string Tool, RawProcessResult Result)>();
            foreach (var tool in new[] { "claude", "codex" })
            {
                var result = ProcessTool.RunAsync(tool, ["--nightweaver-trap-self-test"], RepositoryRoot, environment, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                if (result.ExitCode == 0)
                    throw new InvalidOperationException($"The {tool} process trap must exit nonzero during its self-test");
                trapResults.Add((tool, result));
            }

            if (!File.Exists(TrapRecordPath))
                throw new InvalidOperationException("Agent CLI trap self-test did not create its invocation record: "
                    + string.Join(" | ", trapResults.Select(item => $"{item.Tool} exit={item.Result.ExitCode}; stdout={item.Result.StandardOutputText}; stderr={item.Result.StandardErrorText}")));
            var records = File.ReadAllLines(TrapRecordPath);
            if (records.Length != 2 || !records.Any(record => record.Contains("\"executable\":\"claude\"", StringComparison.Ordinal))
                || !records.Any(record => record.Contains("\"executable\":\"codex\"", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Agent CLI trap self-test did not record both executable names: {string.Join(" | ", records)}");
            File.Delete(TrapRecordPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("NIGHTWEAVER_AGENT_TRAP_RECORD", originalRecordPath);
        }
    }

    private void RunDotnetPublish(string projectPath, string outputPath)
    {
        var arguments = new[]
        {
            "publish", projectPath, "-c", "Release", "--self-contained", "false", "-p:UseAppHost=true", "-o", outputPath
        };
        var result = ProcessTool.RunAsync("dotnet", arguments, RepositoryRoot, null, TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish failed for '{projectPath}' (exit {result.ExitCode})\n{result.StandardOutputText}\n{result.StandardErrorText}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(target, File.GetUnixFileMode(file)); }
                catch (PlatformNotSupportedException) { }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Nightweaver.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException($"Could not locate Nightweaver.slnx above '{AppContext.BaseDirectory}'");
    }

    public void Dispose() => temp.Dispose();
}

internal static class ProcessTool
{
    internal static async Task<RawProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}");
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(standardOutput);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(standardError);
        using var timeoutSource = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        if (timedOut) throw new TimeoutException($"Process '{fileName}' exceeded its {timeout} timeout");
        return new(process.ExitCode, standardOutput.ToArray(), standardError.ToArray());
    }
}
