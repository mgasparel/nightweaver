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

        foreach (var entry in Snapshot.SafeEntries(Path))
        {
            try
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
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

        DeleteTree(Path);
    }

    private static void DeleteTree(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry);
                else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0) DeleteTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(root);
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
    internal static IEnumerable<string> SafeEntries(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            yield return path;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in SafeEntries(path)) yield return child;
        }
    }

    internal static string FileTree(string root)
    {
        var rootAttributes = File.GetAttributes(root);
        var rootTimestamp = File.GetLastWriteTimeUtc(root).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var entries = SafeEntries(root)
            .Select(path =>
            {
                var relative = System.IO.Path.GetRelativePath(root, path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return $"L {relative} {new DirectoryInfo(path).LinkTarget}";
                // bd 1.3.1 --readonly closes its chunk journal by rewriting
                // identical bytes. The user explicitly accepts these backend
                // timestamps; still compare paths, attributes, length and hashes.
                var portable = "/" + relative.Replace('\\', '/');
                var backendTimestamp = portable.Contains("/.beads/embeddeddolt/", StringComparison.Ordinal)
                    && (portable.EndsWith("/.dolt/noms", StringComparison.Ordinal)
                        || (portable.Contains("/.dolt/noms/", StringComparison.Ordinal)
                            && System.IO.Path.GetFileName(path) is "manifest" or "journal.idx" or "vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv"));
                var timestamp = backendTimestamp ? "(accepted bd backend timestamp)" : File.GetLastWriteTimeUtc(path).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                if (Directory.Exists(path))
                {
                    return $"D {relative} {attributes} {timestamp}";
                }

                var info = new FileInfo(path);
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                return $"F {relative} {info.Length} {hash} {attributes} {timestamp}";
            })
            .Order(StringComparer.Ordinal);
        return string.Join("\n", new[] { $"R . {rootAttributes} {rootTimestamp}" }.Concat(entries));
    }

    internal static string Location(string path)
    {
        if (Directory.Exists(path)) return FileTree(path);
        if (!File.Exists(path)) return "(missing)";
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var timestamp = File.GetLastWriteTimeUtc(path).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return $"F . {info.Length} {hash} {File.GetAttributes(path)} {timestamp}";
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

// A real, isolated embedded Dolt tracker. Setup writes are outside measured runs.
internal sealed class BeadsFixture : IDisposable
{
    private readonly TempDirectory temp = new();
    internal string Path { get; }
    internal string Root => temp.Path;
    internal string UserHome { get; }
    internal IReadOnlyDictionary<string, string?> Environment { get; }
    internal string ControlBeadId { get; }

    internal BeadsFixture(string relativePath = "repo")
    {
        Path = temp.CreateDirectory(relativePath);
        UserHome = temp.CreateDirectory("isolated user");
        var environment = new Dictionary<string, string?>(BeadsCommand.Create(Path).Environment)
        {
            ["HOME"] = UserHome, ["USERPROFILE"] = UserHome, ["APPDATA"] = UserHome, ["XDG_CONFIG_HOME"] = UserHome,
            ["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(UserHome, "gitconfig"), ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "Fixture", ["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid",
            ["GIT_COMMITTER_NAME"] = "Fixture", ["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid"
        };
        Environment = environment;
        Run("git", ["init", "-b", "main"]);
        Run("bd", ["init", "--prefix", "fixture", "--non-interactive", "--skip-agents", "--skip-hooks"]);
        // A genuine fixture write establishes bd's physical-root gate, which init
        // alone omits. Production refuses such unprepared trackers without writes.
        ControlBeadId = Create("Fixture control");
    }

    internal string Run(string tool, IReadOnlyList<string> args)
    {
        var result = new SystemProcessRunner().RunAsync(new ProcessRequest(tool, args, Path, Environment, TimeSpan.FromSeconds(30)), CancellationToken.None).GetAwaiter().GetResult();
        if (result.ExitCode != 0) throw new InvalidOperationException($"Required real-tool fixture prerequisite {tool} failed (exit {result.ExitCode}): {result.StandardError}{result.StandardOutput}");
        return result.StandardOutput;
    }

    internal string Create(string title, string? actor = null, string status = "open", string type = "task")
    {
        var args = new List<string> { "create", "--title", title, "--type", type, "--json" };
        if (actor is not null) { args.Add("--assignee"); args.Add(actor); }
        using var document = System.Text.Json.JsonDocument.Parse(Run("bd", args));
        var id = document.RootElement.GetProperty("id").GetString()!;
        if (status != "open") Run("bd", ["update", id, "--status", status]);
        return id;
    }

    public void Dispose() => temp.Dispose();
}
