namespace Nightweaver.Cli;

internal static class HelpText
{
    internal const string Usage =
        "Usage: nightweaver (--project <project-slug> | --config <path-to-project-json>) --dry-run\n" +
        "       nightweaver dispatch <bead-id> [--role <role>] (--project <project-slug> | --config <path-to-project-json>)\n" +
        "       nightweaver --help\n\n" +
        "Options:\n" +
        "  --project <project-slug>       Load <nightweaver-root>/projects/<project-slug>.json.\n" +
        "  --config <path-to-project-json> Load a Project configuration file.\n" +
        "  --dry-run                      Validate and print a hypothetical startup snapshot.\n" +
        "  dispatch <bead-id>             Run exactly one eligible lifecycle stage.\n" +
        "  --role <role>                  Override role inference (planner, plan_challenger, implementer, reviewer, qa, merger).\n" +
        "  --help, -h                     Print this help.\n\n" +
        "<nightweaver-root> is the directory containing the nightweaver executable.\n\n" +
        "Exit codes: 0 success, 1 dispatch/inspection failure, 2 usage or configuration error.\n";

    internal const string DispatchUsage =
        "Usage: nightweaver dispatch <bead-id> [--role <role>] (--project <project-slug> | --config <path-to-project-json>)\n\n" +
        "Role inference: task absent/stage:implement -> implementer; stage:review -> reviewer; stage:qa -> qa; stage:merge -> merger.\n" +
        "              feature/bug absent/stage:plan/stage:plan-approved -> planner; stage:plan-challenge -> plan_challenger.\n" +
        "Lifecycle roles: planner, plan_challenger, implementer, reviewer, qa, merger.\n" +
        "This command runs one lifecycle stage, including configured operational retries; it never schedules the next stage.\n" +
        "Exit codes: 0 succeeded, 1 failed/escalated/cancelled, 2 usage or configuration error.\n";
}
