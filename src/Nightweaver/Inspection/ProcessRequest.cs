namespace Nightweaver.Inspection;

internal sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan Timeout);
