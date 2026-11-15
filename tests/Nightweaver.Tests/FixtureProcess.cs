using System.Diagnostics;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

// Managed CreateProcess inherits all inheritable handles, including another
// launcher's temporary pipe writers. Share the native launcher's gate even in
// fixtures: HANDLE_LIST restricts its child, not an unrelated managed child.
internal static class FixtureProcess
{
    internal static Process? Start(ProcessStartInfo startInfo)
    {
        lock (WindowsProcessLauncher.ProcessCreationGate) return Process.Start(startInfo);
    }

    internal static bool Start(Process process)
    {
        lock (WindowsProcessLauncher.ProcessCreationGate) return process.Start();
    }
}
