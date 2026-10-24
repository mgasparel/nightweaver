namespace Nightweaver;

internal sealed record AppEnvironment(
    string NightweaverRoot,
    string CurrentDirectory,
    TextWriter Out,
    TextWriter Err);
