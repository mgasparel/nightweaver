namespace Nightweaver.Reporting;

internal sealed record ProjectHeader(
    string Name,
    string ConfigFilePath,
    string NightweaverRoot,
    string TargetRepositoryPath,
    string DefaultBranch,
    string RecoveryActor,
    string WorktreesDirectory,
    string TmpDirectory,
    string LogsDirectory);
