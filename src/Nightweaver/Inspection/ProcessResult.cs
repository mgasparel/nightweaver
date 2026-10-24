namespace Nightweaver.Inspection;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
