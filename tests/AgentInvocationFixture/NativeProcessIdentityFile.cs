using System.Diagnostics;
using System.Text;

namespace Nightweaver.Tests.AgentInvocationFixture;

public static class NativeProcessIdentityFile
{
    public static void Write(string path, Action? beforePublication = null)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        using var process = Process.GetCurrentProcess();
        try
        {
            File.WriteAllText(temporary, $"{Environment.ProcessId}|{process.StartTime.ToUniversalTime().Ticks}", new UTF8Encoding(false));
            beforePublication?.Invoke();
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
