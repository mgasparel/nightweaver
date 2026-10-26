using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class TemporaryTreeDeleterTests
{
    [Fact]
    public void DeleteRemovesTreeWithReadonlyFilesAndDirectories()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("managed-tree");
        var subDir = Directory.CreateDirectory(Path.Combine(root, "subdir")).FullName;
        var readOnlyFile1 = Path.Combine(root, "readonly1.txt");
        var readOnlyFile2 = Path.Combine(subDir, "readonly2.txt");

        File.WriteAllText(readOnlyFile1, "content1");
        File.SetAttributes(readOnlyFile1, FileAttributes.ReadOnly);

        File.WriteAllText(readOnlyFile2, "content2");
        File.SetAttributes(readOnlyFile2, FileAttributes.ReadOnly);

        TemporaryTreeDeleter.Delete(root);

        Assert.False(Directory.Exists(root));
    }


    [Fact]
    public void DeleteInvokesBeforeDeleteCallbackAndDeletesTree()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("managed-callback-tree");
        var child1 = Path.Combine(root, "file1.txt");
        var subDir = Directory.CreateDirectory(Path.Combine(root, "subdir")).FullName;
        var child2 = Path.Combine(subDir, "file2.txt");

        File.WriteAllText(child1, "content1");
        File.WriteAllText(child2, "content2");
        File.SetAttributes(child2, FileAttributes.ReadOnly);

        var visited = new List<string>();
        TemporaryTreeDeleter.Delete(root, visited.Add);

        Assert.False(Directory.Exists(root));
        Assert.Contains(child1, visited);
        Assert.Contains(child2, visited);
        Assert.Contains(subDir, visited);
        Assert.Contains(root, visited);
    }

    [Fact]
    public void DeleteRefusesReparsePointEntry()
    {
        using var temp = new TempDirectory();
        var external = temp.CreateDirectory("external-target");
        var root = temp.CreateDirectory("managed-link-tree");
        var link = Path.Combine(root, "linked-folder");
        WorktreeInspectionTests.CreateDirectoryLink(link, external);

        var ex = Assert.Throws<IOException>(() => TemporaryTreeDeleter.Delete(root));
        Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(external));
    }

    [Fact]
    public void DeleteRemovesTreeWithGitRepositoryContainingReadonlyObjects()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("managed-repo-tree");
        var repoDir = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;

        RunGit(repoDir, ["init", "--initial-branch=main"]);
        File.WriteAllText(Path.Combine(repoDir, "file.txt"), "hello git");
        RunGit(repoDir, ["add", "file.txt"]);
        RunGit(repoDir, [
            "-c", "user.name=Test",
            "-c", "user.email=test@example.com",
            "commit", "-m", "initial commit"
        ]);

        var objectsDir = Path.Combine(repoDir, ".git", "objects");
        var objectFiles = Directory.GetFiles(objectsDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("pack", StringComparison.Ordinal) && !f.EndsWith("info", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(objectFiles);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(objectFiles, f => (File.GetAttributes(f) & FileAttributes.ReadOnly) != 0);
        }

        TemporaryTreeDeleter.Delete(root);

        Assert.False(Directory.Exists(root));
    }


    private static void RunGit(string workingDirectory, string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git command failed: {process.StandardError.ReadToEnd()}");
        }
    }
}
