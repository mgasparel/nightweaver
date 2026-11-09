using System.Security.Cryptography;
using System.Text;

namespace Nightweaver.Tests.AgentInvocationFixture;

/// <summary>
/// Coordinates short evidence-log file operations made by independent manual-pilot processes.
/// The native manual-pilot fixture runs on Windows and uses a Local named mutex shared by its child processes.
/// </summary>
public static class ManualPilotEvidenceLog
{
    private static readonly TimeSpan MutexWaitTimeout = TimeSpan.FromSeconds(30);

    public static void AppendLine(string path, string record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Contains('\r') || record.Contains('\n'))
            throw new ArgumentException("An evidence record must occupy exactly one line.", nameof(record));

        WithMutex(path, () =>
        {
            var bytes = new UTF8Encoding(false).GetBytes(record + Environment.NewLine);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        });
    }

    public static string[] ReadAllLines(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return WithMutex(path, () => File.ReadAllLines(path, Encoding.UTF8));
    }

    internal static void HoldMutexForProbe(string path, string readyPath, string releasePath)
    {
        WithMutex(path, () =>
        {
            File.WriteAllText(readyPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WaitForProbeRelease(releasePath);
        });
    }

    private static TResult WithMutex<TResult>(string path, Func<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var mutex = new Mutex(initiallyOwned: false, GetMutexName(path));
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(MutexWaitTimeout);
            }
            catch (AbandonedMutexException exception)
            {
                acquired = true;
                throw new IOException(
                    $"The manual-pilot evidence mutex for '{Path.GetFullPath(path)}' was abandoned. " +
                    "The previous operation may have left a partial JSONL record; inspect the evidence file before retrying.",
                    exception);
            }

            if (!acquired)
                throw new TimeoutException(
                    $"Timed out after {MutexWaitTimeout.TotalSeconds:0} seconds waiting for the manual-pilot evidence log '{Path.GetFullPath(path)}'.");

            return operation();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void WithMutex(string path, Action operation)
        => WithMutex(path, () =>
        {
            operation();
            return true;
        });

    private static string GetMutexName(string path)
    {
        var canonicalPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) canonicalPath = canonicalPath.ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
        var name = "Nightweaver.ManualPilotEvidence." + digest;
        return OperatingSystem.IsWindows() ? @"Local\" + name : name;
    }

    private static void WaitForProbeRelease(string releasePath)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(releasePath)) return;
            Thread.Sleep(10);
        }

        throw new TimeoutException($"Evidence-lock probe did not receive release file '{releasePath}'.");
    }
}
