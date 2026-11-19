namespace Nightweaver.Tests;

internal static class FixtureReadinessFile
{
    internal static string Read(string path, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return Read(() => File.ReadAllText(path), timeout, () => clock.Elapsed, Thread.Sleep);
    }

    internal static string Read(Func<string> read, TimeSpan timeout,
        Func<TimeSpan> elapsed, Action<TimeSpan> wait)
    {
        IOException? lastFailure = null;
        while (elapsed() < timeout)
        {
            try { return read(); }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 2 or 32 or 33)
            {
                // Existence does not guarantee an openable file, even after atomic publication.
                // Only missing-file and sharing/lock violations consume the readiness budget.
                lastFailure = exception;
            }

            var remaining = timeout - elapsed();
            if (remaining > TimeSpan.Zero)
                wait(remaining < TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException("Native child readiness file could not be read within the existing readiness deadline.",
            lastFailure);
    }
}
