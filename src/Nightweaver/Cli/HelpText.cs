namespace Nightweaver.Cli;

internal static class HelpText
{
    internal const string Usage =
        "Usage: nightweaver (--project <project-slug> | --config <path-to-project-json>) --dry-run\n" +
        "       nightweaver --help\n\n" +
        "Options:\n" +
        "  --project <project-slug>       Load <nightweaver-root>/projects/<project-slug>.json.\n" +
        "  --config <path-to-project-json> Load a Project configuration file.\n" +
        "  --dry-run                      Validate and print a hypothetical startup snapshot.\n" +
        "  --help, -h                     Print this help.\n\n" +
        "<nightweaver-root> is the directory containing the nightweaver executable.\n\n" +
        "Exit codes: 0 success, 1 inspection failure, 2 usage or configuration error.\n";
}
