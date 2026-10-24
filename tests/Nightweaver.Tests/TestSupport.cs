using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nightweaver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string CreateDirectory(string relativePath)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    internal string WriteFile(string relativePath, string contents)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(entry, FileAttributes.Normal);
            }
            catch (IOException)
            {
                // Directories and files can disappear during recursive cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Clear attributes where the current account permits it; Directory.Delete reports the rest.
            }
        }

        Directory.Delete(Path, recursive: true);
    }
}

internal sealed class RecordingProcessRunner(Func<ProcessRequest, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
{
    private readonly List<ProcessRequest> requests = [];

    internal IReadOnlyList<ProcessRequest> Requests => requests;

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        requests.Add(request);
        return handler(request, cancellationToken);
    }
}

internal sealed class GitFixture : IDisposable
{
    private readonly TempDirectory tempDirectory = new();

    internal GitFixture(string relativePath = "repo")
    {
        Path = tempDirectory.CreateDirectory(relativePath);
        RunGit(Path, ["init", "-b", "main"]);
        Directory.CreateDirectory(System.IO.Path.Combine(Path, ".beads"));
        File.WriteAllText(System.IO.Path.Combine(Path, ".beads", "metadata.json"), "{\"backend\":\"dolt\"}");
    }

    internal string Path { get; }

    internal static int RunGit(string workingDirectory, IReadOnlyList<string> arguments, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(workingDirectory, "missing-global-config");
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {standardError}{standardOutput}");
        }

        return process.ExitCode;
    }

    internal void CommitEmpty()
    {
        RunGit(Path, ["-c", "user.name=Nightweaver Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "fixture"]);
    }

    public void Dispose() => tempDirectory.Dispose();
}

internal static class Snapshot
{
    internal static string FileTree(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = System.IO.Path.GetRelativePath(root, path);
                if (Directory.Exists(path))
                {
                    return $"D {relative}";
                }

                var info = new FileInfo(path);
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                return $"F {relative} {info.Length} {hash}";
            })
            .Order(StringComparer.Ordinal);
        return string.Join("\n", entries);
    }

    internal static string GitStatus(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--porcelain=v1");
        startInfo.ArgumentList.Add("--ignored");
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(path, "missing-global-config");
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git status failed: {stderr}");
        }

        return stdout;
    }
}
