using Nightweaver.Application;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class ContinuousApplicationTests
{
    [Fact]
    public void RuntimeValidationDoesNotRecreateALogRootRemovedAfterStartup()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(Path.Combine(temp.Path, "host"));
        var project = new ProjectSlug("demo");
        ContinuousApplication.EnsureInitialRoots(paths, project);

        var logsRoot = paths.LogsRoot(project);
        Directory.Delete(logsRoot);

        var exception = Assert.Throws<ToolExecutionException>(() =>
            ContinuousApplication.ValidateAdmissionRoots(paths, project));

        Assert.Contains(logsRoot, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(logsRoot));
    }
}
