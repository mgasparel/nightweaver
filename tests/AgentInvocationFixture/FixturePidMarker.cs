using System.Globalization;
using System.Text;

namespace Nightweaver.Tests.AgentInvocationFixture;

public static class FixturePidMarker
{
    public static void Publish(string path, int pid, Action? beforeWrite = null)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temporaryPath, append: false, new UTF8Encoding(false)))
            {
                beforeWrite?.Invoke();
                writer.Write(pid.ToString(CultureInfo.InvariantCulture));
            }
            // Existence is the consumer's readiness handshake. Publish only
            // after the PID is complete and its writer is closed.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
