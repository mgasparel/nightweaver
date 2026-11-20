using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Nightweaver.Evidence.Capture
{
    public interface IClock
    {
        long Milliseconds { get; }
        DateTimeOffset UtcNow { get; }
        Task Delay(int milliseconds, CancellationToken token);
    }
    public sealed class RealClock : IClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public long Milliseconds => watch.ElapsedMilliseconds;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public Task Delay(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token);
    }
    public sealed class StopSignal : IDisposable
    {
        private readonly CancellationTokenSource source = new CancellationTokenSource();
        public CancellationToken Token => source.Token;
        public StopSignal() { Console.CancelKeyPress += Cancel; }
        private void Cancel(object sender, ConsoleCancelEventArgs args) { args.Cancel = true; source.Cancel(); }
        public void Dispose() { Console.CancelKeyPress -= Cancel; source.Dispose(); }
    }
    public sealed class Request
    {
        public string Kind, Id;
        public int Maximum = 1048576, Budget = 2000;
        public long Offset;
        public Request(string kind, string id) { Kind = kind; Id = id; }
    }
    public sealed class ReadResult
    {
        public byte[] Bytes;
        public string Outcome = "complete", Code;
        public string ObservedUtc, InvocationId, Generation;
        public string CollectionStartUtc;
        public long? CollectionStartElapsedMs;
        public bool NativeAccountingAvailable;
        public double NativeCpuMs;
        public ulong NativeReadBytes, NativeWriteBytes;
        public Dictionary<string, object> Epoch;
        public long Offset;
        public static ReadResult Failure(string outcome, string code) => new ReadResult { Outcome = outcome, Code = code };
        public static ReadResult Json(object value) => new ReadResult { Bytes = JsonSerializer.SerializeToUtf8Bytes(value) };
    }
    public interface ISources : IDisposable
    {
        Task<ReadResult> Read(Request request, CancellationToken token);
    }
    public sealed class Options
    {
        public string Installation, Configuration, Repository, Project, Contract, ContractRevision, EvidenceRoot, WebUrl;
        public string[] WorkloadIds;
        public DateTimeOffset StartUtc, CutoffUtc;
        public int StopAfterSeconds;
        public bool CaptureBackgroundLogs = true;
        // All are explicit local input files. Invocation keys are selected bead-id/invocation-id.
        public Dictionary<string, string> InvocationLogs = new Dictionary<string, string>();
        public Dictionary<string, string> References = new Dictionary<string, string>();
    }
    public sealed class Limits
    {
        // Test seams may only tighten the delivered archive bounds.
        public long ArchiveBytes = 268435456, StreamBytes = 33554432;
        public int Entries = 16384, Records = 32768, PayloadBytes = 1048576, RecordBytes = 2097152;
        public void Validate()
        {
            if (ArchiveBytes < 65536 || ArchiveBytes > 268435456 || StreamBytes < 4096 || StreamBytes > 33554432 ||
                Entries < 8 || Entries > 16384 || Records < 4 || Records > 32768 ||
                PayloadBytes < 1 || PayloadBytes > 1048576 || RecordBytes < 1024 || RecordBytes > 2097152)
                throw new InvalidDataException("invalid-limits");
        }
    }
    public static class Data
    {
        public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        public static bool Id(string value) => value != null && value.Length <= 128 && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z") && !value.EndsWith(".");
        public static string PathOf(string path)
        {
            if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || path.StartsWith(@"\\") || path.IndexOf(':', 2) >= 0)
                throw new InvalidDataException("unsafe-path");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        public static Dictionary<string, object> Map(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
    }

    // Pin each ancestor against rename, reject links (including hard links), and
    // identify an open file by volume/file-index rather than its mutable name.
    public sealed class SafeFile : IDisposable
    {
        private readonly List<SafeFileHandle> pins = new List<SafeFileHandle>();
        public FileStream Stream { get; private set; }
        public string Generation { get; private set; }
        public string PhysicalPath { get; private set; }
        public SafeFile(string requested, bool writable = false, bool create = false, bool directory = false)
        {
            var path = Data.PathOf(requested);
            try
            {
                if (directory) { PinChain(path); PhysicalPath = Resolve(pins[pins.Count - 1]); return; }
                PinChain(Path.GetDirectoryName(path));
                var handle = Open(path, writable ? 0x40000000u : 0x80000000u, writable ? 1u : 7u, false, create ? 1u : 3u);
                try
                {
                    Info info;
                    if (!GetFileInformationByHandle(handle, out info)) throw new IOException("file-identity-unavailable");
                    Generation = info.Volume.ToString("x8") + "-" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
                    PhysicalPath = Resolve(handle);
                    Stream = new FileStream(handle, writable ? FileAccess.Write : FileAccess.Read, 4096, true);
                }
                catch { handle.Dispose(); throw; }
            }
            catch { Dispose(); throw; }
        }
        public void PinChain(string path)
        {
            var current = Path.GetPathRoot(path);
            pins.Add(Open(current, 0x80, 3, true, 3));
            foreach (var segment in path.Substring(current.Length).Split(Path.DirectorySeparatorChar))
            {
                if (segment.Length == 0) continue;
                if (pins.Count >= 128) throw new InvalidDataException("directory-limit");
                current = Path.Combine(current, segment);
                pins.Add(Open(current, 0x80, 3, true, 3));
            }
        }
        private static string Resolve(SafeFileHandle handle)
        {
            var physical = new StringBuilder(4096);
            uint length = GetFinalPathNameByHandleW(handle, physical, 4096, 1);
            if (length == 0 || length >= 4096) throw new IOException("physical-identity-unavailable");
            return physical.ToString();
        }
        private static SafeFileHandle Open(string path, uint access, uint share, bool directory, uint disposition)
        {
            var handle = CreateFileW(@"\\?\" + path, access, share, IntPtr.Zero, disposition,
                0x00200000u | (directory ? 0x02000000u : 0x40000000u), IntPtr.Zero);
            Info info;
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info) || GetFileType(handle) != 1 ||
                (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            { handle.Dispose(); throw new IOException("unsafe-or-unavailable-file"); }
            return handle;
        }
        public async Task<byte[]> Read(int maximum, long offset, CancellationToken token, bool incremental = false)
        {
            if (offset < 0 || offset > Stream.Length) throw new InvalidDataException("source-truncated");
            Stream.Position = offset;
            var bytes = new byte[Math.Min(maximum + (incremental ? 0L : 1L), Stream.Length - offset)];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = await Stream.ReadAsync(bytes.AsMemory(total), token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return total == bytes.Length ? bytes : bytes.Take(total).ToArray();
        }
        public void Dispose()
        {
            Stream?.Dispose();
            for (int i = pins.Count - 1; i >= 0; i--) pins[i].Dispose();
            pins.Clear();
        }
        [StructLayout(LayoutKind.Sequential)] private struct Info
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    }

    // A suspended launch is assigned to a kill-on-close Job before any code runs.
    // The two pipe readers retain one aggregate MiB, never unrestricted stderr.
    public static class NativeRead
    {
        public static Dictionary<string, object> CurrentIo()
        {
            Io value;
            if (!GetProcessIoCounters(new IntPtr(-1), out value)) return Data.Map("available", false);
            return Data.Map("available", true, "readBytes", value.ReadBytes, "writeBytes", value.WriteBytes, "otherBytes", value.OtherBytes,
                "readOperations", value.ReadOps, "writeOperations", value.WriteOps, "otherOperations", value.OtherOps);
        }
        public static async Task<ReadResult> Run(string executable, string[] arguments, string directory,
            Dictionary<string, string> overrides, int maximum, CancellationToken token)
        {
            using (var stdout = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable))
            using (var stderr = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable))
            using (var job = CreateJobObject(IntPtr.Zero, null))
            {
                var limit = new JobLimit(); limit.Basic.Flags = 0x2000;
                if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<JobLimit>())) throw new IOException("job-unavailable");
                var start = new Startup(); start.Size = Marshal.SizeOf<Startup>(); start.Flags = 0x100;
                start.Output = stdout.ClientSafePipeHandle.DangerousGetHandle(); start.Error = stderr.ClientSafePipeHandle.DangerousGetHandle();
                var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables()) env[(string)item.Key] = (string)item.Value;
                foreach (var pair in overrides) { if (pair.Value == null) env.Remove(pair.Key); else env[pair.Key] = pair.Value; }
                var block = Marshal.StringToHGlobalUni(string.Join("\0", env.Select(p => p.Key + "=" + p.Value)) + "\0\0");
                ProcessInfo process;
                try
                {
                    var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
                    if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000404, block, directory, ref start, out process))
                        throw new IOException("process-unavailable");
                }
                finally { Marshal.FreeHGlobal(block); }
                using (var root = new SafeFileHandle(process.Process, true))
                using (var thread = new SafeFileHandle(process.Thread, true))
                {
                    if (!AssignProcessToJobObject(job, root))
                    {
                        TerminateProcess(root, 2);
                        return Account(ReadResult.Failure("error", WaitForSingleObject(root, 200) == 0 ? "job-assignment-failed" : "settlement-uncertain"), job);
                    }
                    stdout.DisposeLocalCopyOfClientHandle(); stderr.DisposeLocalCopyOfClientHandle();
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                    using (var output = new MemoryStream())
                    {
                        int total = 0; bool oversized = false;
                        Func<Stream, bool, Task> drain = async (stream, retain) =>
                        {
                            var buffer = new byte[4096];
                            for (;;)
                            {
                                int count = await stream.ReadAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
                                if (count == 0) break;
                                if (Interlocked.Add(ref total, count) > maximum) { oversized = true; linked.Cancel(); break; }
                                if (retain) output.Write(buffer, 0, count);
                            }
                        };
                        var outTask = drain(stdout, true); var errTask = drain(stderr, false);
                        ResumeThread(thread);
                        try
                        {
                            while (WaitForSingleObject(root, 0) == 258 || Active(job) != 0)
                                await Task.Delay(10, linked.Token).ConfigureAwait(false);
                            await Task.WhenAll(outTask, errTask).WaitAsync(linked.Token).ConfigureAwait(false);
                            uint exit; GetExitCodeProcess(root, out exit);
                            return Account(exit == 0 ? new ReadResult { Bytes = output.ToArray() } : ReadResult.Failure("error", "command-failed"), job);
                        }
                        catch (Exception)
                        {
                            TerminateJobObject(job, 2);
                            // Job ownership prevents PID-name based termination of foreign processes.
                            var settle = Stopwatch.StartNew();
                            while (Active(job) != 0 && settle.ElapsedMilliseconds < 200) await Task.Delay(5).ConfigureAwait(false);
                            try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false); } catch { }
                            return Account(ReadResult.Failure(oversized ? "limit" : "timeout", Active(job) == 0 ? (oversized ? "output-limit" : "command-timeout") : "settlement-uncertain"), job);
                        }
                    }
                }
            }
        }
        private static uint Active(SafeFileHandle job) { Accounting value; if (!QueryInformationJobObject(job, 1, out value, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero)) return uint.MaxValue; return value.Active; }
        private static ReadResult Account(ReadResult result, SafeFileHandle job)
        {
            AccountingIo value;
            if (QueryAccountingIo(job, 8, out value, (uint)Marshal.SizeOf<AccountingIo>(), IntPtr.Zero))
            {
                result.NativeAccountingAvailable = true;
                result.NativeCpuMs = (value.Basic.User + value.Basic.Kernel) / 10000.0;
                result.NativeReadBytes = value.Io.ReadBytes; result.NativeWriteBytes = value.Io.WriteBytes;
            }
            return result;
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct JobLimit { public BasicLimit Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [StructLayout(LayoutKind.Sequential)] private struct Accounting { public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, Total, Active, Terminated; }
        [StructLayout(LayoutKind.Sequential)] private struct AccountingIo { public Accounting Basic; public Io Io; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public int Size; public string Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCount, YCount, Fill; public int Flags; public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref JobLimit value, uint size);
        [DllImport("kernel32.dll")] private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting value, uint size, IntPtr returned);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject")] private static extern bool QueryAccountingIo(SafeFileHandle job, int type, out AccountingIo value, uint size, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll")] private static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
        [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out Io counters);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInfo process);
    }

    public sealed class LocalSources : ISources
    {
        private readonly Options options;
        private readonly string bd, git, pwsh, resourceScript;
        private readonly HttpClient http;
        public int Commands { get; private set; }
        public LocalSources(Options options, string bd, string git, string pwsh, string resourceScript)
        {
            this.options = options; this.bd = bd; this.git = git; this.pwsh = pwsh; this.resourceScript = resourceScript;
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, UseDefaultCredentials = false });
            http.Timeout = Timeout.InfiniteTimeSpan;
        }
        public static Dictionary<string, string> ReadEnvironment()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "BEADS_DIR", "BEADS_DB", "BD_DB", "BEADS_DOLT_SERVER_DATABASE", "BEADS_DOLT_DATA_DIR", "BEADS_DOLT_SERVER_MODE", "BEADS_DOLT_SHARED_SERVER", "BEADS_MEM_PROFILE", "BEADS_MEM_STATS", "BD_CPU_PROFILE", "BD_MEM_PROFILE", "BD_TRACE", "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS" }) env[key] = null;
            foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
                if (((string)item.Key).StartsWith("GIT_CONFIG_KEY_") || ((string)item.Key).StartsWith("GIT_CONFIG_VALUE_")) env[(string)item.Key] = null;
            foreach (var key in new[] { "BD_IMPORT_AUTO", "BD_EXPORT_AUTO", "BD_BACKUP_ENABLED", "BD_DOLT_AUTO_PUSH", "BD_EVENTS_EXPORT", "BD_OTEL_STDOUT", "BD_NO_DB", "BD_DOLT_AUTO_START" }) env[key] = "false";
            foreach (var key in new[] { "BD_DISABLE_METRICS", "BD_DISABLE_EVENT_FLUSH", "GIT_OPTIONAL_LOCKS" }) env[key] = key == "GIT_OPTIONAL_LOCKS" ? "0" : "1";
            env["OTEL_SDK_DISABLED"] = "true"; env["BD_DOLT_AUTO_COMMIT"] = "off"; env["BD_NO_HOOKS"] = "true";
            env["GIT_TERMINAL_PROMPT"] = "0";
            env["GOMAXPROCS"] = Environment.GetEnvironmentVariable("GOMAXPROCS") ?? "2";
            return env;
        }
        public async Task<ReadResult> Read(Request request, CancellationToken token)
        {
            try
            {
                switch (request.Kind)
                {
                    case "directory":
                        string directory = request.Id == "evidence" ? options.EvidenceRoot : request.Id == "repository" ? options.Repository :
                            request.Id == "installation" ? options.Installation : throw new InvalidDataException();
                        using (var pin = new SafeFile(directory, directory: true)) return new ReadResult { Bytes = Encoding.UTF8.GetBytes(pin.PhysicalPath) };
                    case "git":
                        Commands++;
                        string[] arguments = request.Id == "common" ? new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" } :
                            request.Id == "head" ? new[] { "rev-parse", "HEAD" } : throw new InvalidDataException();
                        var gitResult = await NativeRead.Run(git, arguments, options.Repository, ReadEnvironment(), request.Maximum, token).ConfigureAwait(false);
                        if (request.Id == "common" && gitResult.Bytes != null)
                        {
                            string common = Data.PathOf(Encoding.UTF8.GetString(gitResult.Bytes).TrimEnd('\r', '\n'));
                            using (var head = new SafeFile(Path.Combine(common, "HEAD")))
                                gitResult.Bytes = Encoding.UTF8.GetBytes(Path.GetDirectoryName(head.PhysicalPath));
                        }
                        return gitResult;
                    case "tracker":
                        Commands++;
                        var args = new List<string> { "--readonly", "--sandbox" };
                        if (request.Id == "inventory") args.AddRange(new[] { "list", "--all", "--limit", "0", "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json" });
                        else if (request.Id == "ready") args.AddRange(new[] { "ready", "--unassigned", "--limit", "0", "--json" });
                        else if (options.WorkloadIds.Contains(request.Id)) args.AddRange(new[] { "show", request.Id, "--include-comments", "--json" });
                        else throw new InvalidDataException();
                        return await NativeRead.Run(bd, args.ToArray(), options.Repository, ReadEnvironment(), request.Maximum, token).ConfigureAwait(false);
                    case "http":
                        var url = new Uri(new Uri(options.WebUrl), "/api/operations/v1/projects/" + Uri.EscapeDataString(options.Project) + "/resources?key=" + Uri.EscapeDataString(request.Id));
                        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                        using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                        using (var output = new MemoryStream())
                        {
                            if (!response.IsSuccessStatusCode) return ReadResult.Failure("missing", "http-unavailable");
                            var buffer = new byte[4096];
                            while (output.Length <= request.Maximum)
                            {
                                int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, request.Maximum + 1L - output.Length)), token).ConfigureAwait(false);
                                if (count == 0) break;
                                output.Write(buffer, 0, count);
                            }
                            return new ReadResult { Bytes = output.ToArray() };
                        }
                    case "resource":
                        Commands++;
                        var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                        return await NativeRead.Run(windowsPowerShell, new[] { "-NoProfile", "-File", resourceScript }, options.Repository, ReadEnvironment(), request.Maximum, token).ConfigureAwait(false);
                    case "file":
                    case "invocation":
                    case "hostLog":
                        string path = FilePath(request);
                        using (var file = new SafeFile(path))
                        {
                            if (file.Stream.Length < request.Offset) return new ReadResult { Outcome = "invalid", Code = "source-truncated", Generation = file.Generation, Offset = request.Offset };
                            var bytes = await file.Read(request.Maximum, request.Offset, token, request.Kind != "file").ConfigureAwait(false);
                            return new ReadResult { Bytes = bytes, Generation = file.Generation, Offset = request.Offset,
                                InvocationId = request.Kind == "invocation" ? request.Id.Split('/')[1] : null };
                        }
                    case "configuration":
                    case "fileHash":
                        using (var file = new SafeFile(request.Kind == "configuration" ? options.Configuration : FilePath(new Request("file", request.Id))))
                        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                        {
                            // Large configuration is streamed and time-bounded, never retained.
                            var buffer = new byte[65536]; int count;
                            while ((count = await file.Stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) hash.AppendData(buffer, 0, count);
                            string digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                            return request.Kind == "configuration" ? new ReadResult { Bytes = Encoding.ASCII.GetBytes(digest) } :
                                ReadResult.Json(Data.Map("file", request.Id, "sha256", digest, "bytes", file.Stream.Length));
                        }
                    case "process":
                        using (var process = Process.GetProcessById(int.Parse(request.Id)))
                            return ReadResult.Json(Data.Map("processId", process.Id, "processStartedUtc", Data.Utc(process.StartTime.ToUniversalTime())));
                    default: return ReadResult.Failure("invalid", "unsupported-source");
                }
            }
            catch (OperationCanceledException) { return ReadResult.Failure("timeout", "source-timeout"); }
            catch { return ReadResult.Failure("missing", "source-unavailable"); }
        }
        private string FilePath(Request request)
        {
            if (request.Kind == "hostLog")
            {
                if (request.Id != "background-host" && request.Id != "background-web") throw new InvalidDataException();
                return Path.Combine(options.Installation, "logs", options.Project, request.Id + ".log");
            }
            if (request.Kind == "invocation")
            {
                string path;
                if (!options.InvocationLogs.TryGetValue(request.Id, out path)) throw new InvalidDataException();
                return path;
            }
            switch (request.Id)
            {
                case "manifest": return Path.Combine(options.Installation, "nightweaver-deployment.json");
                case "cli": return Path.Combine(options.Installation, "nightweaver.exe");
                case "web": return Path.Combine(options.Installation, "web", "Nightweaver.Web.exe");
                case "contract": return options.Contract;
                case "owner": return Path.Combine(options.Installation, "run", options.Project, "background", "owner.json");
                case "factory": return Path.Combine(options.Installation, "telemetry", options.Project, "factory.json");
                default:
                    if (request.Id.StartsWith("reference-") && options.References.ContainsKey(request.Id.Substring(10))) return options.References[request.Id.Substring(10)];
                    throw new InvalidDataException();
            }
        }
        public void Dispose() => http.Dispose();
    }

    public interface IWriter : IDisposable
    {
        void Start(Dictionary<string, object> manifest);
        void Append(Dictionary<string, object> record, byte[] payload, bool terminal);
        void Close(Dictionary<string, object> manifest);
        int Count { get; }
    }
    public sealed class ArchiveWriter : IWriter
    {
        private readonly string root, staging;
        private readonly Limits limits;
        private readonly Action beforeRecordCommit;
        private readonly List<SafeFile> pins = new List<SafeFile>();
        private SafeFile reservation;
        private FileStream samples;
        private long bytes, committed;
        private int entries = 3;
        public int Count { get; private set; }
        public ArchiveWriter(string root, Limits limits, Action beforeRecordCommit = null)
        {
            this.root = Data.PathOf(root); this.limits = limits; limits.Validate();
            this.beforeRecordCommit = beforeRecordCommit;
            staging = root + "-incomplete";
        }
        private void Reserve()
        {
            pins.Add(new SafeFile(Path.GetDirectoryName(root), directory: true));
            // Native CREATE_NEW directory reservation, never reuse an existing directory.
            SecureDirectory(root); pins.Add(new SafeFile(root, directory: true));
            SecureDirectory(staging); pins.Add(new SafeFile(staging, directory: true));
            Directory.CreateDirectory(Path.Combine(root, "payloads"));
            pins.Add(new SafeFile(Path.Combine(root, "payloads"), directory: true));
            reservation = new SafeFile(Path.Combine(staging, "terminal-reserve"), true, true);
            var reservedBytes = new byte[65536]; RandomNumberGenerator.Fill(reservedBytes);
            reservation.Stream.Write(reservedBytes); reservation.Stream.Flush(true);
        }
        public static ArchiveWriter CreateBounded(string root, Limits limits) =>
            Task.Run(() => new ArchiveWriter(root, limits)).WaitAsync(TimeSpan.FromMilliseconds(1000)).GetAwaiter().GetResult();
        public void Start(Dictionary<string, object> manifest)
        {
            Reserve();
            var sample = new SafeFile(Path.Combine(root, "samples.jsonl"), true, true); pins.Add(sample); samples = sample.Stream;
            PublishManifest(manifest, false);
        }
        private void PublishManifest(Dictionary<string, object> manifest, bool replace)
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (content.Length > 1048576) throw new IOException("manifest-limit");
            var path = Path.Combine(staging, "manifest-" + Guid.NewGuid().ToString("N"));
            using (var file = new SafeFile(path, true, true)) { file.Stream.Write(content); file.Stream.Flush(true); }
            File.Move(path, Path.Combine(root, "manifest.json"), replace);
            bytes = committed + content.Length + payloadBytes;
        }
        private long payloadBytes;
        public void Append(Dictionary<string, object> record, byte[] payload, bool terminal)
        {
            if (terminal && reservation != null) { reservation.Dispose(); reservation = null; File.Delete(Path.Combine(staging, "terminal-reserve")); }
            var lineMaximum = terminal ? limits.RecordBytes : Math.Min(limits.RecordBytes, 65536);
            int maximum = terminal ? 4096 : (string)record["sourceKind"] == "invocation" ? Math.Min(limits.PayloadBytes, 65536) : limits.PayloadBytes;
            if (payload != null && payload.Length > maximum) throw new IOException("payload-limit");
            string name = null, path = null;
            if (payload != null)
            {
                name = "payloads/" + (Count + 1).ToString("D5") + "-" + Guid.NewGuid().ToString("N") + ".bin";
                record["payload"] = Data.Map("path", name, "bytes", payload.LongLength, "sha256", Data.Hash(payload));
            }
            var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
            // Reserve closure metadata, one failure record and one directory entry.
            if (Count + 1 > limits.Records - (terminal ? 0 : 1) || content.Length > lineMaximum ||
                committed + content.Length > limits.StreamBytes - (terminal ? 0 : 4096) ||
                entries + (payload == null ? 0 : 1) > limits.Entries - (terminal ? 0 : 1) ||
                bytes + content.Length + (payload?.Length ?? 0) > limits.ArchiveBytes - (terminal ? 0 : 16384))
                throw new IOException("archive-limit");
            // Only collector-owned incomplete files live outside the archive namespace.
            var staged = Path.Combine(staging, "record-" + Guid.NewGuid().ToString("N"));
            using (var file = new SafeFile(staged, true, true)) { file.Stream.Write(content); file.Stream.Flush(true); }
            if (payload != null)
            {
                path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                var pendingPayload = Path.Combine(staging, "payload-" + Guid.NewGuid().ToString("N"));
                using (var file = new SafeFile(pendingPayload, true, true)) { file.Stream.Write(payload); file.Stream.Flush(true); }
                File.Move(pendingPayload, path);
                payloadBytes += payload.Length; bytes += payload.Length; entries++;
            }
            try { beforeRecordCommit?.Invoke(); samples.Write(content); samples.Flush(true); }
            catch { samples.SetLength(committed); samples.Flush(true); throw; }
            committed += content.Length; bytes += content.Length; Count++;
            File.Delete(staged); // own completed staging bytes only
        }
        public void Close(Dictionary<string, object> manifest) { samples?.Dispose(); PublishManifest(manifest, true); }
        public void Dispose() { reservation?.Dispose(); foreach (var pin in pins) pin.Dispose(); pins.Clear(); }
        public bool DisposeWithinDeadline()
        {
            try { Task.Run(Dispose).WaitAsync(TimeSpan.FromMilliseconds(1000)).GetAwaiter().GetResult(); return true; }
            catch { return false; }
        }
        private static void SecureDirectory(string path)
        {
            // Raw logs never inherit a broader evidence-root access list.
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            IntPtr descriptor;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;" + sid + ")", 1, out descriptor, IntPtr.Zero)) throw new IOException("archive-acl-unavailable");
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                if (!CreateDirectoryW(path, ref attributes)) throw new IOException("archive-reservation-failed");
            }
            finally { LocalFree(descriptor); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr length);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }

    public sealed class Collector
    {
        private readonly Options options;
        private readonly IClock clock;
        private readonly ISources sources;
        private readonly IWriter writer;
        private readonly CancellationToken cancellation;
        private readonly List<Task> pending = new List<Task>();
        private Dictionary<string, object> manifest, epoch;
        private long origin, end, shutdownDeadline, shutdownAnchor, lastRuntime = -1;
        private DateTimeOffset actualStart, lastUtc;
        private bool uncertain, clockUncertain, stopped, writerUnsettled;
        private string stopReason;
        private readonly Dictionary<string, (string Generation, long Offset, long Bytes)> cursors = new Dictionary<string, (string, long, long)>();
        private readonly Dictionary<string, long> trackerAccepted = new Dictionary<string, long>();
        public int Reads { get; private set; }
        public long ReadMilliseconds { get; private set; }
        public bool WriterSettled => !writerUnsettled;
        public int ShutdownRemaining(int maximum, int reserve = 0) => (int)Math.Min(maximum, Math.Max(0, shutdownDeadline - clock.Milliseconds - reserve));
        private void BeginShutdown()
        {
            shutdownAnchor = Math.Min(shutdownAnchor, clock.Milliseconds);
            shutdownDeadline = Math.Min(shutdownDeadline, shutdownAnchor + 10000);
        }
        private void ObserveSettlement(ReadResult value)
        {
            if (value.Code == "settlement-uncertain") { uncertain = true; stopped = true; BeginShutdown(); }
        }
        public Collector(Options options, IClock clock, ISources sources, IWriter writer, CancellationToken cancellation)
        { this.options = options; this.clock = clock; this.sources = sources; this.writer = writer; this.cancellation = cancellation; }
        public static void Validate(Options o)
        {
            if (!Data.Id(o.Project) || o.WorkloadIds == null || o.WorkloadIds.Length != 3 || o.WorkloadIds.Distinct().Count() != 3 || o.WorkloadIds.Any(id => !Data.Id(id)) ||
                !Regex.IsMatch(o.ContractRevision ?? "", "\\A[0-9a-f]{40}\\z") || o.CutoffUtc - o.StartUtc != TimeSpan.FromHours(8) || o.StopAfterSeconds < 0)
                throw new InvalidDataException("invalid-input-identity");
            o.Repository = Data.PathOf(o.Repository); o.Installation = Data.PathOf(o.Installation); o.EvidenceRoot = Data.PathOf(o.EvidenceRoot);
            o.Configuration = Data.PathOf(o.Configuration); o.Contract = Data.PathOf(o.Contract);
            if (Data.Within(o.EvidenceRoot, o.Repository) || Data.Within(o.EvidenceRoot, o.Installation)) throw new InvalidDataException("archive-must-be-external");
            Uri web;
            if (!Uri.TryCreate(o.WebUrl, UriKind.Absolute, out web) || web.Scheme != "http" || !web.IsLoopback || web.UserInfo.Length != 0 || web.AbsolutePath != "/" || web.Query.Length != 0 || web.Fragment.Length != 0)
                throw new InvalidDataException("invalid-web-origin");
            if (o.InvocationLogs.Count > 128) throw new InvalidDataException("invocation-count-limit");
            foreach (var pair in o.InvocationLogs)
            {
                var parts = pair.Key.Split('/');
                if (parts.Length != 2 || !o.WorkloadIds.Contains(parts[0]) || !Regex.IsMatch(parts[1], @"\A[0-9]{8}T[0-9]{13}Z-(planner|plan-challenger|implementer|reviewer|qa|merger|classifier|outcome-classifier)\z")) throw new InvalidDataException("invalid-log-identity");
                var canonical = Path.Combine(o.Installation, "logs", o.Project, parts[0], parts[1] + ".log");
                if (!Data.PathOf(pair.Value).Equals(canonical, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("noncanonical-log");
            }
            foreach (var pair in o.References)
                if (!new[] { "interventionJournal", "operatorAttestation", "budgetWorksheet", "runAuthorization", "qualification", "checks" }.Contains(pair.Key)) throw new InvalidDataException("invalid-reference");
        }
        private long Elapsed => Math.Max(0, clock.Milliseconds - origin);
        private async Task<ReadResult> Read(Request request, long deadline)
        {
            long start = clock.Milliseconds;
            string startedUtc = Data.Utc(clock.UtcNow);
            ReadResult Stamp(ReadResult value) { value.CollectionStartUtc = startedUtc; value.CollectionStartElapsedMs = Math.Max(0, start - origin); return value; }
            int budget = (int)Math.Min(request.Budget, Math.Max(0, Math.Min(end, deadline) - start));
            if (budget <= 0 || stopped || cancellation.IsCancellationRequested) return Stamp(ReadResult.Failure("interrupted", "collection-cutoff"));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(budget);
                // Contain synchronous adapter entry as well as its asynchronous operation.
                var task = Task.Run(() => sources.Read(request, timeout.Token)); Reads++;
                try
                {
                    var value = await task.WaitAsync(TimeSpan.FromMilliseconds(budget), cancellation).ConfigureAwait(false);
                    ObserveSettlement(value);
                    if (uncertain) return Stamp(ReadResult.Failure("timeout", "settlement-uncertain"));
                    if (clock.Milliseconds > start + budget) return Stamp(ReadResult.Failure("timeout", "source-deadline"));
                    if (value.Bytes != null && value.Bytes.Length > request.Maximum) return Stamp(ReadResult.Failure("limit", "output-limit"));
                    return Stamp(value);
                }
                catch
                {
                    if (cancellation.IsCancellationRequested) BeginShutdown();
                    timeout.Cancel();
                    try
                    {
                        int grace = ShutdownRemaining(300);
                        if (grace <= 0 && !task.IsCompleted) throw new TimeoutException();
                        ObserveSettlement(await task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, grace))).ConfigureAwait(false));
                    }
                    catch { if (!task.IsCompleted) { pending.Add(task); uncertain = true; stopped = true; BeginShutdown(); } }
                    return Stamp(ReadResult.Failure(cancellation.IsCancellationRequested ? "interrupted" : "timeout", uncertain ? "settlement-uncertain" : "source-timeout"));
                }
                finally { ReadMilliseconds += Math.Max(0, clock.Milliseconds - start); }
            }
        }
        private async Task<ReadResult> Essential(Request request)
        {
            var result = await Read(request, clock.Milliseconds + 2000).ConfigureAwait(false);
            if (result.Outcome != "complete" || result.Bytes == null) throw new InvalidDataException("essential-identity-unavailable");
            return result;
        }
        private static JsonElement Json(ReadResult result) => JsonDocument.Parse(result.Bytes, new JsonDocumentOptions { MaxDepth = 32 }).RootElement.Clone();
        private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString();
        public async Task<Dictionary<string, object>> Run()
        {
            Validate(options);
            origin = clock.Milliseconds; actualStart = clock.UtcNow; lastUtc = actualStart;
            long remaining = (long)(options.CutoffUtc - actualStart).TotalMilliseconds;
            if (remaining <= 0 || remaining > 28920000) throw new InvalidDataException("invalid-collection-window");
            end = origin + remaining;
            if (options.StopAfterSeconds > 0) end = Math.Min(end, origin + options.StopAfterSeconds * 1000L);
            shutdownAnchor = end; shutdownDeadline = end + 10000;
            var common = Encoding.UTF8.GetString((await Essential(new Request("git", "common")).ConfigureAwait(false)).Bytes).Trim();
            string evidencePhysical = Encoding.UTF8.GetString((await Essential(new Request("directory", "evidence")).ConfigureAwait(false)).Bytes);
            string repositoryPhysical = Encoding.UTF8.GetString((await Essential(new Request("directory", "repository")).ConfigureAwait(false)).Bytes);
            string installationPhysical = Encoding.UTF8.GetString((await Essential(new Request("directory", "installation")).ConfigureAwait(false)).Bytes);
            string primaryCheckout = Path.GetFileName(common).Equals(".git", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(common) : common;
            if (Data.Within(evidencePhysical, repositoryPhysical) || Data.Within(evidencePhysical, installationPhysical) || Data.Within(evidencePhysical, primaryCheckout))
                throw new InvalidDataException("archive-must-be-physically-external");
            // Native adapter resolves a no-link, pinned HEAD handle with a volume-GUID
            // path, eliminating drive-letter/subst aliases from physical identity.
            if (!common.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) common = Data.PathOf(common);
            common = common.TrimEnd('\\', '/').Replace('\\', '/').ToUpperInvariant();
            var head = Encoding.ASCII.GetString((await Essential(new Request("git", "head")).ConfigureAwait(false)).Bytes).Trim();
            if (!Regex.IsMatch(head, "\\A[0-9a-f]{40}\\z")) throw new InvalidDataException("source-revision-unavailable");
            var deployment = await Essential(new Request("file", "manifest")).ConfigureAwait(false);
            var installed = Json(deployment);
            var revision = Text(installed, "build_version");
            if (installed.GetProperty("schema_version").GetInt32() != 1 || !Regex.IsMatch(revision ?? "", "\\A[0-9a-f]{40}\\z")) throw new InvalidDataException("installed-revision-unavailable");
            var config = Encoding.ASCII.GetString((await Essential(new Request("configuration", "hash")).ConfigureAwait(false)).Bytes);
            if (!Regex.IsMatch(config, "\\A[0-9a-f]{64}\\z")) throw new InvalidDataException("configuration-hash-unavailable");
            var contract = await Essential(new Request("file", "contract")).ConfigureAwait(false);
            if (!Encoding.UTF8.GetString(contract.Bytes).Contains("Contract version: **1**.")) throw new InvalidDataException("unsupported-contract-document");
            var cliIdentity = await Essential(new Request("fileHash", "cli")).ConfigureAwait(false);
            var webIdentity = await Essential(new Request("fileHash", "web")).ConfigureAwait(false);
            var references = Data.Map("interventionJournal", null, "operatorAttestation", null, "budgetWorksheet", null, "runAuthorization", null, "qualification", null, "checks", null);
            manifest = Data.Map("schema", "nightweaver-unattended-archive-v1", "runId", Guid.NewGuid().ToString("N"), "projectId", options.Project,
                "repositoryId", Data.Hash(Encoding.UTF8.GetBytes(common)), "repositoryPath", options.Repository,
                "contract", Data.Map("version", 1, "revision", options.ContractRevision, "sha256", Data.Hash(contract.Bytes)),
                "installation", Data.Map("revision", revision, "manifestSha256", Data.Hash(deployment.Bytes), "configurationSha256", config),
                "workloadIds", options.WorkloadIds, "requestedWindow", Data.Map("startUtc", Data.Utc(options.StartUtc), "endUtc", Data.Utc(options.CutoffUtc)),
                "actualWindow", Data.Map("startUtc", Data.Utc(actualStart), "endUtc", null, "elapsedMs", null), "state", "open", "sampleCount", null, "clockUncertain", false, "references", references);
            Storage(() => writer.Start(manifest));
            string terminal = "collection-cutoff";
            try
            {
                Emit("installation", "baseline", ReadResult.Json(Data.Map("sourceRevision", head, "physicalCommonDirectorySha256", manifest["repositoryId"],
                    "installedManifestSha256", Data.Hash(deployment.Bytes), "configurationSha256", config, "collectorVersion", 1)), Elapsed);
                Emit("contract", "contract", contract, Elapsed);
                Emit("installation", "baseline-cli", cliIdentity, Elapsed);
                Emit("installation", "baseline-web", webIdentity, Elapsed);
                foreach (var key in options.References.Keys)
                {
                    var result = await Read(new Request("file", "reference-" + key), end).ConfigureAwait(false);
                    var record = Emit("authority", "reference-" + key, result, Elapsed);
                    if (record != null) references[key] = record["payload"];
                }
                await Installation("baseline").ConfigureAwait(false);
                await Resources("baseline").ConfigureAwait(false);
                long nextRuntime = clock.Milliseconds, nextTracker = clock.Milliseconds, nextResource = clock.Milliseconds + 900000;
                int rotation = 0, webRotation = 0, logRotation = 0;
                var trackerIds = new[] { "inventory", "ready" }.Concat(options.WorkloadIds).ToArray();
                while (clock.Milliseconds < end - 5000 && !stopped && !cancellation.IsCancellationRequested)
                {
                    if (clock.Milliseconds >= nextRuntime)
                    {
                        long due = nextRuntime; long turnStart = clock.Milliseconds;
                        if (turnStart - due >= 15000) Emit("runtime", "schedule", ReadResult.Failure("missing", "skipped-runtime-turns"), Elapsed);
                        await Runtime(turnStart + 5000, webRotation++).ConfigureAwait(false);
                        // Strict no catch-up: an overrun schedules a future turn.
                        nextRuntime = Math.Max(turnStart + 15000, clock.Milliseconds + 1);
                    }
                    if (clock.Milliseconds >= nextTracker && clock.Milliseconds < end - 5000 && !stopped)
                    {
                        long start = clock.Milliseconds;
                        for (int i = 0; i < 2 && clock.Milliseconds < start + 4000; i++)
                        {
                            string id = trackerIds[rotation++ % trackerIds.Length]; long before = Elapsed;
                            var result = await Read(new Request("tracker", id), Math.Min(start + 4000, end - 5000)).ConfigureAwait(false);
                            if (result.Outcome == "complete") trackerAccepted[id] = clock.Milliseconds;
                            Emit("tracker", id, result, before);
                        }
                        var ages = trackerIds.Select(id => Data.Map("sourceId", id, "ageMs", trackerAccepted.ContainsKey(id) ? (object)Math.Max(0, clock.Milliseconds - trackerAccepted[id]) : null)).ToArray();
                        var ageResult = ReadResult.Json(ages);
                        if (trackerIds.Any(id => trackerAccepted.ContainsKey(id) ? clock.Milliseconds - trackerAccepted[id] > 180000 : Elapsed > 180000))
                        { ageResult.Outcome = "invalid"; ageResult.Code = "tracker-age-over-180s"; }
                        Emit("tracker", "source-ages", ageResult, Elapsed);
                        nextTracker = Math.Max(start + 60000, clock.Milliseconds + 1);
                    }
                    var logs = options.InvocationLogs.Keys.Select(id => (Id: id, Host: false)).ToList();
                    if (options.CaptureBackgroundLogs) logs.AddRange(new[] { ("background-host", true), ("background-web", true) });
                    long logDeadline = Math.Min(end - 5000, clock.Milliseconds + 2000);
                    for (int i = 0; i < Math.Min(4, logs.Count); i++)
                    {
                        if (clock.Milliseconds >= logDeadline || stopped) break;
                        var selection = logs[logRotation++ % logs.Count];
                        await Invocation(selection.Id, selection.Host, logDeadline).ConfigureAwait(false);
                    }
                    if (clock.Milliseconds >= nextResource && !stopped)
                    { await Resources("periodic").ConfigureAwait(false); nextResource = clock.Milliseconds + 900000; }
                    long next = Math.Min(end - 5000, Math.Min(nextRuntime, Math.Min(nextTracker, nextResource)));
                    if (next > clock.Milliseconds) await clock.Delay((int)Math.Min(next - clock.Milliseconds, 15000), cancellation).ConfigureAwait(false);
                }
                if (!stopped && !cancellation.IsCancellationRequested)
                {
                    await Installation("endpoint").ConfigureAwait(false);
                    await Resources("endpoint").ConfigureAwait(false);
                    if (end > clock.Milliseconds) await clock.Delay((int)(end - clock.Milliseconds), cancellation).ConfigureAwait(false);
                }
                if (cancellation.IsCancellationRequested) terminal = "caller-cancelled";
                if (uncertain) terminal = "settlement-uncertain";
                else if (stopReason != null) terminal = stopReason;
            }
            catch (OperationCanceledException) { terminal = "caller-cancelled"; }
            catch (IOException) { terminal = "storage-or-archive-limit"; stopped = true; }
            catch { terminal = "collector-failure"; stopped = true; }
            // Endpoint bookkeeping may already have crossed cutoff. It consumes the
            // same absolute allowance as closure, disposal and independent inspection.
            BeginShutdown();
            try
            {
                if (writerUnsettled) throw new IOException("writer-settlement-uncertain");
                Emit("installation", "collector-stop", ReadResult.Json(Data.Map("reason", terminal, "reads", Reads, "readElapsedMs", ReadMilliseconds,
                    "unsettledOperations", pending.Count, "settlementUncertain", uncertain, "shutdownBudgetMs", 10000)), Elapsed, true);
                manifest["state"] = !stopped && !cancellation.IsCancellationRequested && options.StopAfterSeconds == 0 && clock.UtcNow >= options.CutoffUtc ? "complete" : "interrupted";
                manifest["actualWindow"] = Data.Map("startUtc", Data.Utc(actualStart), "endUtc", Data.Utc(clock.UtcNow), "elapsedMs", Elapsed);
                manifest["sampleCount"] = writer.Count; manifest["clockUncertain"] = clockUncertain;
                Storage(() => writer.Close(manifest));
            }
            catch { terminal = "closure-unavailable"; }
            return Data.Map("schema", "nightweaver-unattended-collector-result-v1", "state", manifest["state"], "records", writer.Count,
                "reads", Reads, "readElapsedMs", ReadMilliseconds, "elapsedMs", Elapsed, "shutdownMs", Math.Max(0, clock.Milliseconds - shutdownAnchor),
                "settlementUncertain", uncertain, "writerSettled", !writerUnsettled, "reason", terminal, "unattendedVerdict", "not-evaluated");
        }
        private void Storage(Action action)
        {
            if (writerUnsettled) throw new IOException("writer-settlement-uncertain");
            int budget = ShutdownRemaining(1000);
            if (budget <= 0) throw new IOException("shutdown-deadline");
            var task = Task.Run(action);
            try { task.WaitAsync(TimeSpan.FromMilliseconds(budget)).GetAwaiter().GetResult(); }
            catch (TimeoutException) when (!task.IsCompleted)
            {
                // Never race an outstanding append with rollback, closure, validation
                // or another writer. A stopped snapshot is unavailable in this case.
                writerUnsettled = true; uncertain = true; stopped = true;
                throw new IOException("writer-settlement-uncertain");
            }
        }
        public Task<Dictionary<string, object>> Finish(Dictionary<string, object> result, string executable, string script, string archive)
            => Finish(result, token => NativeRead.Run(executable, new[] { "-NoProfile", "-File", script, "-ArchiveDirectory", archive },
                options.EvidenceRoot, LocalSources.ReadEnvironment(), 1048576, token));
        private static object InspectionValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: return value.EnumerateObject().ToDictionary(p => p.Name, p => InspectionValue(p.Value));
                case JsonValueKind.Array: return value.EnumerateArray().Select(InspectionValue).ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number: return value.TryGetInt64(out long integer) ? (object)integer : value.GetDouble();
                default: return null;
            }
        }
        public async Task<Dictionary<string, object>> Finish(Dictionary<string, object> result, Func<CancellationToken, Task<ReadResult>> inspect)
        {
            // Also used after preflight failure. Never start a fresh allowance in
            // the wrapper, and never inspect concurrently with an unsettled writer.
            BeginShutdown();
            async Task<bool> DisposeOwned(IDisposable owned)
            {
                int budget = ShutdownRemaining(1000);
                if (budget <= 0) return false;
                try { await Task.Run(owned.Dispose).WaitAsync(TimeSpan.FromMilliseconds(budget)).ConfigureAwait(false); return true; }
                catch { return false; }
            }
            bool sourcesDisposed = await DisposeOwned(sources).ConfigureAwait(false);
            if (!sourcesDisposed) uncertain = true;
            if (!writerUnsettled && !await DisposeOwned(writer).ConfigureAwait(false)) writerUnsettled = true;
            if (result == null) return null;
            result["writerSettled"] = !writerUnsettled;
            result["settlementUncertain"] = uncertain;
            result["validation"] = Data.Map("integrityValid", false, "unattendedVerdict", "not-evaluated",
                "errors", new[] { Data.Map("code", writerUnsettled ? "writer-settlement-uncertain" : "validation-unavailable") });
            // Native cancellation itself needs settlement time. Reserve that time
            // inside the same absolute deadline rather than after its expiration.
            int inspectionBudget = ShutdownRemaining(5000, 300);
            if (!writerUnsettled && sourcesDisposed && inspectionBudget > 0)
            {
                using (var timeout = new CancellationTokenSource(inspectionBudget))
                {
                    var task = Task.Run(() => inspect(timeout.Token));
                    try
                    {
                        var value = await task.WaitAsync(TimeSpan.FromMilliseconds(inspectionBudget)).ConfigureAwait(false);
                        ObserveSettlement(value);
                        if (value.Outcome == "complete" && ShutdownRemaining(1) > 0) result["validation"] = InspectionValue(Json(value));
                    }
                    catch
                    {
                        timeout.Cancel();
                        int grace = ShutdownRemaining(300);
                        try
                        {
                            if (grace <= 0 && !task.IsCompleted) throw new TimeoutException();
                            ObserveSettlement(await task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, grace))).ConfigureAwait(false));
                        }
                        catch { if (!task.IsCompleted) uncertain = true; }
                    }
                }
            }
            result["settlementUncertain"] = uncertain;
            result["finalizationMs"] = Math.Max(0, clock.Milliseconds - shutdownAnchor);
            return result;
        }
        private Dictionary<string, object> Emit(string kind, string id, ReadResult result, long start, bool terminal = false)
        {
            var now = clock.UtcNow;
            if (now < lastUtc || Math.Abs((now - actualStart).TotalMilliseconds - Elapsed) > 2000) clockUncertain = true;
            bool backwards = now < lastUtc || (result.CollectionStartUtc != null && now < DateTimeOffset.Parse(result.CollectionStartUtc));
            if (backwards) clockUncertain = true;
            lastUtc = now;
            var record = Data.Map("schema", "nightweaver-unattended-sample-v1", "sequence", writer.Count + 1,
                "runId", manifest["runId"], "projectId", options.Project, "repositoryId", manifest["repositoryId"], "sourceKind", kind, "sourceId", id.Length <= 128 ? id : id.Substring(0, 32) + "-" + Data.Hash(Encoding.UTF8.GetBytes(id)),
                "collection", Data.Map("startedUtc", result.CollectionStartUtc ?? Data.Utc(backwards ? now : now.AddMilliseconds(-(Elapsed - start))), "endedUtc", Data.Utc(now), "startedElapsedMs", result.CollectionStartElapsedMs ?? start, "endedElapsedMs", Elapsed),
                "sourceObservedUtc", result.ObservedUtc, "outcome", backwards ? "clock-uncertain" : result.Outcome, "epoch", result.Epoch,
                "invocationId", result.InvocationId, "sourcePosition", result.Generation == null ? null : Data.Map("generation", result.Generation, "offset", result.Offset),
                "payload", null, "failureCode", backwards ? "utc-discontinuity" : result.Code);
            Storage(() => writer.Append(record, result.Bytes, terminal));
            return record;
        }
        private async Task Installation(string phase)
        {
            foreach (var id in new[] { "manifest", "cli", "web" })
            {
                long before = Elapsed;
                var result = await Read(new Request(id == "manifest" ? "file" : "fileHash", id), end).ConfigureAwait(false);
                if (id == "manifest" && result.Bytes != null) result.Bytes = JsonSerializer.SerializeToUtf8Bytes(Data.Map("file", id, "sha256", Data.Hash(result.Bytes), "bytes", result.Bytes.LongLength));
                Emit("installation", phase + "-" + id, result, before);
            }
            long started = Elapsed;
            var configuration = await Read(new Request("configuration", "hash"), end).ConfigureAwait(false);
            if (configuration.Bytes != null) configuration.Bytes = JsonSerializer.SerializeToUtf8Bytes(Data.Map("sha256", Encoding.ASCII.GetString(configuration.Bytes)));
            Emit("installation", phase + "-configuration", configuration, started);
            if (phase == "endpoint")
            {
                started = Elapsed;
                Emit("installation", "endpoint-source-head", await Read(new Request("git", "head"), end).ConfigureAwait(false), started);
            }
        }
        private async Task Runtime(long deadline, int rotation)
        {
            long before = Elapsed;
            var owner = await Read(new Request("file", "owner") { Maximum = 65536 }, deadline).ConfigureAwait(false);
            epoch = null;
            try
            {
                var value = Json(owner);
                if (value.GetProperty("schema_version").GetInt32() != 1 || Text(value, "project") != options.Project ||
                    !Data.PathOf(Text(value, "root")).Equals(options.Installation, StringComparison.OrdinalIgnoreCase) ||
                    !Data.PathOf(Text(value, "target_repository")).Equals(options.Repository, StringComparison.OrdinalIgnoreCase) ||
                    !Data.PathOf(Text(value, "config_path")).Equals(options.Configuration, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                string run = Text(value, "run_id"), runtime = Text(value, "runtime_id");
                if (!Regex.IsMatch(run, "\\A[0-9a-f]{32}\\z") || !Regex.IsMatch(runtime, "\\A[0-9a-f]{32}\\z")) throw new InvalidDataException();
                int pid = value.GetProperty("process_id").GetInt32();
                var native = await Read(new Request("process", pid.ToString()), deadline).ConfigureAwait(false);
                var created = DateTimeOffset.Parse(Text(Json(native), "processStartedUtc"));
                if (Math.Abs((created - DateTimeOffset.Parse(Text(value, "process_started_at_utc"))).TotalMilliseconds) > 1) throw new InvalidDataException();
                epoch = Data.Map("ownerRunId", run, "runtimeId", runtime, "processId", pid, "processStartedUtc", Data.Utc(created));
                owner.Epoch = epoch;
                if (lastRuntime >= 0 && Elapsed - lastRuntime > 45000) Emit("runtime", "coverage", ReadResult.Failure("missing", "runtime-gap-over-45s"), before);
                lastRuntime = Elapsed;
            }
            catch { if (owner.Outcome == "complete") { owner.Outcome = "invalid"; owner.Code = "owner-identity-unverified"; } }
            Emit("runtime", "owner", owner, before);
            var keys = new[] { "factory", "cleanup:1:all:", "workspace:" + options.WorkloadIds[0], "workspace:" + options.WorkloadIds[1], "workspace:" + options.WorkloadIds[2] };
            bool webBound = false;
            foreach (var key in new[] { "factory", keys[1 + rotation % 4] })
            {
                before = Elapsed;
                var response = await Read(new Request("http", key), deadline).ConfigureAwait(false);
                response.Epoch = epoch;
                try
                {
                    var envelope = Json(response);
                    if (Text(envelope, "schema") != "nightweaver-observation-v1" || Text(envelope, "projectId") != options.Project || Text(envelope, "key") != key) throw new InvalidDataException();
                    if (envelope.TryGetProperty("observedAt", out var observed) && observed.ValueKind == JsonValueKind.String) response.ObservedUtc = Data.Utc(observed.GetDateTimeOffset());
                    else throw new InvalidDataException();
                    if (epoch == null || Text(envelope, "health") != "fresh") throw new InvalidDataException();
                    if (key == "factory")
                    {
                        var data = envelope.GetProperty("data"); var snapshot = data.GetProperty("snapshot");
                        if (Text(data, "availability") != "live" || Text(snapshot, "runtime_id") != (string)epoch["runtimeId"] || Text(snapshot, "project") != options.Project ||
                            !Data.PathOf(Text(snapshot, "target_repository")).Equals(options.Repository, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        webBound = true;
                    }
                    else if (!webBound) throw new InvalidDataException();
                    // Preserve all supplied health/policy fields; HTTP receipt cannot
                    // refresh a retained body or erase its pending replacement age.
                    if (envelope.TryGetProperty("policy", out var policy) && policy.ValueKind == JsonValueKind.Object)
                    {
                        if (!envelope.TryGetProperty("validatedAgeMs", out var age) || age.ValueKind != JsonValueKind.Number ||
                            age.GetDouble() < 0 || age.GetDouble() > policy.GetProperty("maxSnapshotAgeMs").GetDouble()) throw new InvalidDataException();
                        if (envelope.TryGetProperty("pendingAgeMs", out var pendingAge) && pendingAge.ValueKind == JsonValueKind.Number &&
                            pendingAge.GetDouble() > policy.GetProperty("maxPendingAgeMs").GetDouble()) throw new InvalidDataException();
                    }
                }
                catch { if (response.Outcome == "complete") { response.Outcome = "invalid"; response.Code = "unverified-or-unhealthy-envelope"; } }
                Emit(key.StartsWith("cleanup") ? "cleanup" : "runtime", key, response, before);
            }
            before = Elapsed;
            var factory = await Read(new Request("file", "factory"), deadline).ConfigureAwait(false);
            factory.Epoch = epoch;
            try
            {
                var value = Json(factory);
                if (epoch == null || !new[] { 1, 2 }.Contains(value.GetProperty("schema_version").GetInt32()) || Text(value, "project") != options.Project ||
                    Text(value, "runtime_id") != (string)epoch["runtimeId"] || !Data.PathOf(Text(value, "target_repository")).Equals(options.Repository, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                factory.ObservedUtc = Data.Utc(value.GetProperty("observed_at").GetDateTimeOffset());
                double age = (clock.UtcNow - DateTimeOffset.Parse(factory.ObservedUtc)).TotalSeconds;
                if (age > 10 || age < -60) throw new InvalidDataException();
                foreach (var poolName in new[] { "planning", "delivery", "merger" })
                {
                    if (!value.TryGetProperty(poolName, out var pool) || pool.ValueKind != JsonValueKind.Object || !pool.TryGetProperty("slots", out var slots)) continue;
                    foreach (var slot in slots.EnumerateArray())
                    {
                        if (!slot.TryGetProperty("bead_id", out var beadValue) || beadValue.ValueKind != JsonValueKind.String || !options.WorkloadIds.Contains(beadValue.GetString()) ||
                            !slot.TryGetProperty("invocation", out var invocation) || invocation.ValueKind != JsonValueKind.Object) continue;
                        string bead = beadValue.GetString(), id = Text(invocation, "invocation_id");
                        if (Text(invocation, "bead_id") != bead || !Regex.IsMatch(id ?? "", @"\A[0-9]{8}T[0-9]{13}Z-(planner|plan-challenger|implementer|reviewer|qa|merger|classifier|outcome-classifier)\z")) continue;
                        string key = bead + "/" + id;
                        if (!options.InvocationLogs.ContainsKey(key))
                        {
                            if (options.InvocationLogs.Count >= 128) { Emit("invocation", "discovery", ReadResult.Failure("limit", "invocation-count-limit"), before); stopped = true; stopReason = "invocation-count-limit"; break; }
                            options.InvocationLogs.Add(key, Path.Combine(options.Installation, "logs", options.Project, bead, id + ".log"));
                        }
                    }
                }
            }
            catch { if (factory.Outcome == "complete") { factory.Outcome = "invalid"; factory.Code = "factory-identity-or-freshness"; } }
            Emit("runtime", "factory-local", factory, before);
        }
        private async Task Resources(string phase)
        {
            long deadline = Math.Min(end, clock.Milliseconds + 5000);
            for (int i = 0; i < 2; i++)
            {
                long before = Elapsed;
                Emit("resource", phase + "-" + i, await Read(new Request("resource", "counters"), deadline).ConfigureAwait(false), before);
                if (i == 0 && deadline - clock.Milliseconds >= 1000) await clock.Delay(1000, cancellation).ConfigureAwait(false);
            }
        }
        private async Task Invocation(string id, bool host, long deadline)
        {
            if (!cursors.TryGetValue(id, out var cursor)) cursor = (null, 0, 0);
            if (cursor.Bytes >= 8388608)
            {
                if (cursor.Bytes == 8388608) { Emit(host ? "runtime" : "invocation", host ? id : "log-" + Array.IndexOf(options.InvocationLogs.Keys.ToArray(), id), ReadResult.Failure("limit", "invocation-source-limit"), Elapsed); cursors[id] = (cursor.Generation, cursor.Offset, cursor.Bytes + 1); }
                return;
            }
            long before = Elapsed;
            var request = new Request(host ? "hostLog" : "invocation", id) { Maximum = (int)Math.Min(65536, 8388608 - cursor.Bytes), Offset = cursor.Offset };
            var value = await Read(request, Math.Min(deadline, clock.Milliseconds + 1000)).ConfigureAwait(false);
            if (value.Bytes != null)
            {
                if (cursor.Generation != null && cursor.Generation != value.Generation)
                {
                    value.Outcome = "invalid"; value.Code = "log-generation-replaced";
                    // The already-read bytes remain attributable to the new generation at
                    // the real offset; restart from zero only on a later scheduled turn.
                    cursors[id] = (value.Generation, 0, cursor.Bytes + value.Bytes.Length);
                }
                else cursors[id] = (value.Generation, cursor.Offset + value.Bytes.Length, cursor.Bytes + value.Bytes.Length);
                if (value.Bytes.Length == 0) return;
            }
            else if (value.Outcome != "complete") cursors[id] = (cursor.Generation, 0, cursor.Bytes);
            if (host)
            {
                value.Epoch = epoch;
                if (value.Outcome == "complete" && epoch == null) { value.Outcome = "invalid"; value.Code = "host-log-epoch-unverified"; }
            }
            Emit(host ? "runtime" : "invocation", host ? id : "log-" + Array.IndexOf(options.InvocationLogs.Keys.ToArray(), id), value, before);
        }
    }
}
