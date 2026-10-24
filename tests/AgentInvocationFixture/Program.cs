using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace Nightweaver.Tests.AgentInvocationFixture;

public sealed class FixtureMarker { }

internal static class Program
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;

    private static int Main(string[] args)
    {
        var stageAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_ACTION");
        if (stageAction is not null) return RunStageAction(stageAction);
        if (args.Length == 0) return 64;
        switch (args[0])
        {
            case "inspect":
            {
                using var input = Console.OpenStandardInput();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                var report = new
                {
                    stdinBase64 = Convert.ToBase64String(bytes.ToArray()),
                    cwd = Environment.CurrentDirectory,
                    beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID"),
                    tmp = Environment.GetEnvironmentVariable("TMP"),
                    temp = Environment.GetEnvironmentVariable("TEMP"),
                    tmpdir = Environment.GetEnvironmentVariable("TMPDIR"),
                    slotId = Environment.GetEnvironmentVariable("NIGHTWEAVER_SLOT_ID"),
                    ambient = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_AMBIENT"),
                    auth = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_AUTH"),
                    args = args.Skip(1).ToArray()
                };
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report)));
                Console.OpenStandardOutput().Flush();
                return args.Length > 1 && int.TryParse(args[^1], out var exitCode) ? exitCode : 0;
            }
            case "flood":
            {
                using var input = Console.OpenStandardInput();
                input.CopyTo(Stream.Null);
                var size = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1_200_000;
                var output = Console.OpenStandardOutput();
                var error = Console.OpenStandardError();
                var outBuffer = Enumerable.Repeat((byte)'O', size).ToArray();
                var errBuffer = Enumerable.Repeat((byte)'E', size).ToArray();
                output.Write(outBuffer);
                output.Flush();
                error.Write(errBuffer);
                error.Flush();
                return 0;
            }
            case "exit":
                Console.OpenStandardInput().CopyTo(Stream.Null);
                return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            case "close-stdin":
                Console.OpenStandardInput().Dispose();
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes("stdin-closed\n"));
                return 0;
            case "never-read":
                if (args.Length > 1) File.WriteAllText(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                while (true) Thread.Sleep(1000);
            case "spawn-hold":
            {
                if (args.Length < 3) return 65;
                File.WriteAllText(args[1] + ".root", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SpawnProcess("relay", args[1], args[2]);
                WaitForFile(args[1]);
                WaitForFile(args[1] + ".child");
                WaitForFile(args[2]);
                var exitCode = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
                Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes("root-exit\n"));
                return exitCode;
            }
            case "relay":
                if (args.Length < 3) return 65;
                File.WriteAllText(args[1] + ".child", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SpawnProcess("hold", args[1]);
                WaitForFile(args[1]);
                WaitForFile(args[2]);
                return 0;
            case "hold":
                File.WriteAllText(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                while (true) Thread.Sleep(1000);
            default:
                return 66;
        }
    }

    private static int RunStageAction(string action)
    {
        Console.OpenStandardInput().CopyTo(Stream.Null);
        var beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID")
            ?? throw new InvalidOperationException("NIGHTWEAVER_BEAD_ID is missing.");
        var pidPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE");
        if (!string.IsNullOrWhiteSpace(pidPath)) File.WriteAllText(pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        switch (action)
        {
            case "plan-challenge":
                UpdateLabels(beadId, "stage:plan", "stage:plan-challenge");
                break;
            case "plan-commit":
                UpdateLabels(beadId, "stage:plan", "stage:plan-challenge");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "challenge-commit":
                UpdateLabels(beadId, "stage:plan-challenge", "stage:plan-approved");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "challenge":
                UpdateLabels(beadId, "stage:plan-challenge", "stage:plan-approved");
                break;
            case "planned-materialization":
            {
                using var created = JsonDocument.Parse(Run("bd", ["create", "--title", "Fixture plan bead", "--type", "task", "--json"]));
                var childId = created.RootElement.GetProperty("id").GetString()!;
                Run("bd", ["dep", "add", childId, beadId, "--type", "discovered-from"]);
                UpdateLabels(beadId, "stage:plan-approved", "planned");
                break;
            }
            case "review":
                UpdateLabels(beadId, "stage:implement", "stage:review");
                break;
            case "review-nonzero":
                UpdateLabels(beadId, "stage:implement", "stage:review");
                Console.WriteLine("fixture returned a nonzero exit after a legal Bead transition");
                return 19;
            case "review-dirty":
                UpdateLabels(beadId, "stage:implement", "stage:review");
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fixture-dirty.txt"), "dirty\n");
                break;
            case "review-commit":
                UpdateLabels(beadId, "stage:implement", "stage:review");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "review-qa-commit":
                UpdateLabels(beadId, "stage:review", "stage:qa");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "review-qa":
                UpdateLabels(beadId, "stage:review", "stage:qa");
                break;
            case "qa-merge-commit":
                UpdateLabels(beadId, "stage:qa", "stage:merge");
                Run("git", ["commit", "--allow-empty", "-m", "fixture read-only mutation"]);
                break;
            case "qa-merge":
                UpdateLabels(beadId, "stage:qa", "stage:merge");
                break;
            case "reject-without-increment":
                UpdateLabels(beadId, "stage:review", "stage:implement");
                break;
            case "reject-with-increment":
                Run("bd", ["update", beadId, "--remove-label", "stage:review", "--add-label", "stage:implement", "--set-metadata", "repair_cycles=1"]);
                break;
            case "noop":
                break;
            case "timeout":
                Console.WriteLine("fixture waiting for executor timeout");
                Console.Out.Flush();
                while (true) Thread.Sleep(1000);
            case "close":
                Run("bd", ["close", beadId]);
                break;
            case "close-commit":
                Run("git", ["commit", "--allow-empty", "-m", "fixture Merger commit"]);
                Run("bd", ["close", beadId]);
                break;
            default:
                throw new InvalidOperationException($"Unknown fixture Agent action '{action}'.");
        }

        Console.WriteLine($"fixture action completed: {action}");
        return 0;
    }

    private static void UpdateLabels(string beadId, string oldLabel, string newLabel) =>
        Run("bd", ["update", beadId, "--remove-label", oldLabel, "--add-label", newLabel]);

    private static string Run(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}: {stderrTask.Result}{stdoutTask.Result}");
        return stdoutTask.Result;
    }

    private static void SpawnProcess(string mode, params string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture process path is unavailable.");
        var fixtureDll = Assembly.GetExecutingAssembly().Location;
        var commandLineText = string.Join(' ', new[] { executable, fixtureDll, mode }.Concat(arguments).Select(QuoteArgument));
        var commandLine = Marshal.StringToHGlobalUni(commandLineText);
        try
        {
            var startup = new StartupInfo
            {
                Cb = (uint)Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardInput = GetStdHandle(-10),
                StandardOutput = GetStdHandle(-11),
                StandardError = GetStdHandle(-12)
            };
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                    CreateNoWindow, IntPtr.Zero, Environment.CurrentDirectory, ref startup, out var info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create descendant fixture.");
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var result = new StringBuilder().Append('"');
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void WaitForFile(string path)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException($"Fixture handshake timed out: {path}");
            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Cb;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2;
        internal IntPtr Reserved2Pointer;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
