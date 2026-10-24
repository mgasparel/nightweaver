using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests.ExecutionCrashHost;

public sealed class CrashHostMarker { }

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6) return 64;
        var paths = new HostPaths(args[0]);
        var project = ProjectSlug.Parse(args[1]);
        var beadId = args[2];
        var invocationId = args[3];
        var fixtureAssembly = args[4];
        var readyPath = args[5];
        var tmp = Path.GetFullPath(Path.Combine(paths.TmpRoot(project), beadId, invocationId));
        var log = Path.GetFullPath(Path.Combine(paths.LogsRoot(project), beadId, invocationId + ".log"));
        var invocation = new PreparedAgentInvocation("dotnet", [fixtureAssembly, "never-read", readyPath],
            Path.GetFullPath(Path.Combine(paths.WorktreesRoot(project), beadId)), "hold",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NIGHTWEAVER_BEAD_ID"] = beadId,
                ["TMP"] = tmp,
                ["TEMP"] = tmp,
                ["TMPDIR"] = tmp
            }, TimeSpan.FromMinutes(5), invocationId, tmp, log);

        await AgentInvocationExecutor.ExecuteAsync(paths, project, invocation, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }
}
