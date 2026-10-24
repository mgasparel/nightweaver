namespace Nightweaver.Inspection;

internal static class GitCommand
{
    private static readonly string[] ScrubbedVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_COMMON_DIR",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE",
        "GIT_CEILING_DIRECTORIES"
    ];

    internal static ProcessRequest Create(IReadOnlyList<string> arguments, string workingDirectory)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        foreach (var variable in ScrubbedVariables)
        {
            environment[variable] = null;
        }

        return new ProcessRequest("git", arguments, workingDirectory, environment, TimeSpan.FromSeconds(30));
    }
}
