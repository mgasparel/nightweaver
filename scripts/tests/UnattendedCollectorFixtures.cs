using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nightweaver.Evidence.Capture;

namespace Nightweaver.Evidence.Tests
{
    public sealed class FixtureClock : IClock
    {
        public long Now, Skew;
        public long Milliseconds => Now;
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMilliseconds(Now + Skew);
        public Task Delay(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Now += milliseconds; return Task.CompletedTask; }
    }
    public sealed class FixtureSources : ISources
    {
        public FixtureClock Clock;
        public Options Options;
        public List<(string Kind, string Id, long At, int Budget, long Offset)> Trace = new List<(string, string, long, int, long)>();
        public Func<Request, ReadResult> Override;
        public Func<Request, CancellationToken, Task<ReadResult>> AsyncOverride;
        public CancellationTokenSource Cancel;
        public int DelayMs;
        public Action OnDispose;
        public Task<ReadResult> Read(Request request, CancellationToken token)
        {
            Trace.Add((request.Kind, request.Id, Clock.Now, request.Budget, request.Offset));
            if (AsyncOverride != null && request.Kind == "http") return AsyncOverride(request, token);
            Clock.Now += DelayMs;
            if (Override != null) { var result = Override(request); if (result != null) return Task.FromResult(result); }
            if (Cancel != null && Clock.Now > 30000) Cancel.Cancel();
            object payload;
            switch (request.Kind)
            {
                case "directory": return Task.FromResult(new ReadResult { Bytes = Encoding.UTF8.GetBytes(request.Id == "evidence" ? Options.EvidenceRoot : request.Id == "installation" ? Options.Installation : Options.Repository) });
                case "git": return Task.FromResult(new ReadResult { Bytes = Encoding.ASCII.GetBytes(request.Id == "common" ? "C:/fixture/repository/.git" : new string('a', 40)) });
                case "configuration": return Task.FromResult(new ReadResult { Bytes = Encoding.ASCII.GetBytes(new string('b', 64)) });
                case "fileHash": payload = Data.Map("file", request.Id, "sha256", new string('a', 64), "bytes", 10); break;
                case "process": payload = Data.Map("processId", 42, "processStartedUtc", "2025-12-31T23:00:00Z"); break;
                case "http": payload = Data.Map("schema", "nightweaver-observation-v1", "projectId", "fixture", "key", request.Id,
                    "epoch", "web-epoch-1", "health", "fresh", "observedAt", Data.Utc(Clock.UtcNow),
                    "data", Data.Map("availability", "live", "snapshot", Data.Map("runtime_id", new string('d',32), "project", "fixture", "target_repository", Options.Repository)),
                    "policy", Data.Map("maxSnapshotAgeMs", 120000, "maxPendingAgeMs", 5000), "validatedAgeMs", 0, "pendingAgeMs", null); break;
                case "resource": payload = Data.Map("observedUtc", Data.Utc(Clock.UtcNow), "processes", new object[0]); break;
                case "tracker": payload = Data.Map("source", request.Id, "rows", new object[0]); break;
                case "hostLog": return Task.FromResult(ReadResult.Failure("missing", "fixture-host-log-missing"));
                case "invocation":
                    return Task.FromResult(new ReadResult { Bytes = Encoding.ASCII.GetBytes("partial\nbytes"), Generation = "file-1", Offset = request.Offset, InvocationId = request.Id.Split('/')[1] });
                case "file":
                    if (request.Id == "contract") return Task.FromResult(new ReadResult { Bytes = Encoding.UTF8.GetBytes("Contract version: **1**. Synthetic fixture.") });
                    if (request.Id == "owner") payload = Data.Map("schema_version", 1, "project", "fixture", "root", Options.Installation, "config_path", Options.Configuration,
                        "target_repository", Options.Repository, "run_id", new string('c', 32), "runtime_id", new string('d', 32), "process_id", 42, "process_started_at_utc", "2025-12-31T23:00:00Z");
                    else if (request.Id == "factory") payload = Data.Map("schema_version", 2, "project", "fixture", "target_repository", Options.Repository,
                        "runtime_id", new string('d', 32), "observed_at", Data.Utc(Clock.UtcNow));
                    else if (request.Id == "manifest") payload = Data.Map("schema_version", 1, "build_version", new string('e', 40));
                    else payload = Data.Map("fixture", true);
                    break;
                default: throw new Exception("Unexpected adapter request");
            }
            return Task.FromResult(ReadResult.Json(payload));
        }
        public void Dispose() { OnDispose?.Invoke(); }
    }
    public sealed class MemoryWriter : IWriter
    {
        public List<Dictionary<string, object>> Records = new List<Dictionary<string, object>>();
        public List<byte[]> Payloads = new List<byte[]>();
        public Dictionary<string, object> Manifest;
        public int FailAt = int.MaxValue;
        public ManualResetEventSlim WriteGate, WriteFinished;
        public bool Closed;
        public Action OnWrite, OnDispose;
        public int Count => Records.Count;
        public void Start(Dictionary<string, object> manifest) { Manifest = manifest; }
        public void Append(Dictionary<string, object> record, byte[] payload, bool terminal)
        {
            OnWrite?.Invoke();
            if (!terminal && Count >= FailAt) throw new IOException("credential-canary-must-not-escape");
            if (!terminal && Count == 5 && WriteGate != null) WriteGate.Wait();
            Records.Add(record); Payloads.Add(payload);
            if (WriteGate != null && Count == 6) WriteFinished.Set();
        }
        public void Close(Dictionary<string, object> manifest) { OnWrite?.Invoke(); Manifest = manifest; Closed = true; }
        public void Dispose() { OnDispose?.Invoke(); }
    }
    public static class CollectorFixtures
    {
        private static int cases;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); cases++; }
        public static Options Options(string root)
        {
            return new Options { Installation = Path.Combine(root, "installation"), Configuration = Path.Combine(root, "installation", "projects", "fixture.json"),
                Repository = Path.Combine(root, "repository"), EvidenceRoot = Path.Combine(root, "evidence"), Project = "fixture", WebUrl = "http://127.0.0.1:54321/",
                Contract = Path.Combine(root, "contract.md"), ContractRevision = new string('f', 40), WorkloadIds = new[] { "selected-1", "selected-2", "selected-3" },
                StartUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), CutoffUtc = DateTimeOffset.Parse("2026-01-01T08:00:00Z") };
        }
        private static async Task<(MemoryWriter Writer, FixtureSources Source, FixtureClock Clock, Dictionary<string, object> Result)> Run(string root, int seconds = 0, Action<FixtureSources, MemoryWriter> setup = null)
        {
            var options = Options(root); options.StopAfterSeconds = seconds;
            var clock = new FixtureClock(); var source = new FixtureSources { Clock = clock, Options = options }; var writer = new MemoryWriter();
            setup?.Invoke(source, writer);
            var result = await new Collector(options, clock, source, writer, source.Cancel?.Token ?? CancellationToken.None).Run().ConfigureAwait(false);
            return (writer, source, clock, result);
        }
        public static async Task<Dictionary<string, object>> RunAll(string root)
        {
            cases = 0;
            var full = await Run(root).ConfigureAwait(false);
            Check((string)full.Result["state"] == "complete" && full.Clock.Now == 28800000, "Full simulated window did not stop exactly at cutoff");
            Check((string)full.Result["unattendedVerdict"] == "not-evaluated", "Collector produced a verdict");
            var trace = full.Source.Trace;
            Check(trace.All(t => t.At < 28800000), "Source admitted after cutoff");
            Check(trace.All(t => new[] { "directory", "file", "fileHash", "configuration", "process", "git", "http", "tracker", "resource", "invocation", "hostLog" }.Contains(t.Kind)), "Read allowlist escaped");
            var tracker = trace.Where(t => t.Kind == "tracker").ToArray();
            Check(tracker.Length == 960 && tracker.Select(t => t.Id).Take(5).SequenceEqual(new[] { "inventory", "ready", "selected-1", "selected-2", "selected-3" }), "Tracker rotation or rate wrong");
            Check(tracker.GroupBy(t => t.At / 60000).All(g => g.Count() <= 2), "Tracker burst");
            Check(trace.Count(t => t.Kind == "http") <= 3840, "HTTP rate exceeds two per turn");
            var resources = trace.Where(t => t.Kind == "resource").ToArray();
            Check(resources.Length >= 64 && resources.Length <= 68, "Resource schedule wrong");
            Check(resources.Where((t, i) => i % 2 == 1).Select((t, i) => t.At - resources[2 * i].At).All(delta => delta == 1000), "Resource samples not one second apart");
            Check((string)((Dictionary<string, object>)full.Writer.Manifest["installation"])["revision"] == new string('e', 40), "Source HEAD substituted for installed revision");
            Check(full.Writer.Records.All(r => (int)r["sequence"] > 0), "Invalid record sequence");
            var shortRun = await Run(root, 35).ConfigureAwait(false);
            Check((string)shortRun.Result["state"] == "interrupted" && shortRun.Clock.Now == 35000, "Short fixture changed requested interval or extended capture");
            Check((string)((Dictionary<string, object>)shortRun.Writer.Manifest["requestedWindow"])["endUtc"] == "2026-01-01T08:00:00.0000000Z", "Short fixture rewrote requested eight hours");
            var failure = await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "http" ? ReadResult.Failure("missing", "unsupported-source") : null).ConfigureAwait(false);
            Check(failure.Writer.Records.Any(r => (string)r["failureCode"] == "unsupported-source"), "Missing source hidden");
            var large = await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "http" ? new ReadResult { Bytes = new byte[1048577] } : null).ConfigureAwait(false);
            Check(large.Writer.Records.Any(r => (string)r["outcome"] == "limit") && large.Writer.Payloads.All(p => p == null || p.Length <= 1048576), "Oversized output retained");
            var timeout = await Run(root, 35, (source, writer) => source.Override = r => { if (r.Kind == "http") { source.Clock.Now += 2100; return ReadResult.Json(new { late = true }); } return null; }).ConfigureAwait(false);
            Check(timeout.Writer.Records.Any(r => (string)r["outcome"] == "timeout"), "Adapter budget not enforced");
            var exhausted = await Run(root, 35, (source, writer) => writer.FailAt = 5).ConfigureAwait(false);
            Check((string)exhausted.Result["reason"] == "storage-or-archive-limit" && exhausted.Writer.Count == 6, "Storage failure lost terminal reserve or earlier records");
            using (var gate = new ManualResetEventSlim())
            using (var finished = new ManualResetEventSlim())
            {
                var blocked = await Run(root, 35, (source, writer) => { writer.WriteGate = gate; writer.WriteFinished = finished; }).ConfigureAwait(false);
                Check(!(bool)blocked.Result["writerSettled"] && !blocked.Writer.Closed && blocked.Writer.Count == 5, "Unsettled writer was closed or replaced");
                gate.Set();
                Check(finished.Wait(1000), "Fixture-owned writer did not settle after explicit release");
            }
            var uncertain = await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "http" ? ReadResult.Failure("timeout", "settlement-uncertain") : null).ConfigureAwait(false);
            Check((bool)uncertain.Result["settlementUncertain"] && uncertain.Source.Trace.Count(t => t.Kind == "http") == 1, "Unsettled source replaced");
            foreach (bool grace in new[] { false, true })
            {
                var deadlineUncertain = await Run(root, 35, (source, writer) => source.AsyncOverride = async (request, token) =>
                {
                    if (grace)
                    {
                        // Exercise the outer deadline catch, then complete during its
                        // settlement grace with explicit contrary ownership evidence.
                        try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        await Task.Delay(20).ConfigureAwait(false);
                    }
                    source.Clock.Now += 2100;
                    return ReadResult.Failure("timeout", "settlement-uncertain");
                }).ConfigureAwait(false);
                Check((bool)deadlineUncertain.Result["settlementUncertain"], "Deadline discarded settlement uncertainty: grace=" + grace);
                var firstHttp = deadlineUncertain.Source.Trace.FindIndex(t => t.Kind == "http");
                Check(firstHttp == deadlineUncertain.Source.Trace.Count - 1, "Source admitted after uncertain deadline settlement");
                Check(deadlineUncertain.Writer.Payloads.Where(p => p != null).Any(p => Encoding.UTF8.GetString(p).Contains("\"settlementUncertain\":true")), "Terminal evidence concealed uncertain deadline settlement");
            }
            // Reproduce the reviewer's slow-but-settled endpoint counterexample.
            // The same Finish method owns disposal/validation for the PS wrapper;
            // no separate wrapper stopwatch or renewed timeout participates.
            foreach (bool exhaustedDeadline in new[] { false, true })
            {
                var o = Options(root); o.StopAfterSeconds = 35;
                var c = new FixtureClock(); var s = new FixtureSources { Clock = c, Options = o };
                var w = new MemoryWriter();
                s.Override = r => { if (c.Now >= 30000 && c.Now < 35000) c.Now += 2000; return null; };
                w.OnWrite = () => { if (c.Now >= 30000) c.Now += 900; };
                s.OnDispose = () => c.Now += 900;
                w.OnDispose = () => c.Now += 900;
                var collector = new Collector(o, c, s, w, CancellationToken.None);
                var result = await collector.Run().ConfigureAwait(false);
                Check((long)result["shutdownMs"] == c.Now - 35000 && c.Now > 35000, "Endpoint overrun omitted from shutdown duration");
                if (exhaustedDeadline) c.Now = 45000;
                bool inspected = false;
                result = await collector.Finish(result, token =>
                {
                    inspected = true;
                    int remaining = collector.ShutdownRemaining(5000, 300);
                    Check(remaining > 0 && remaining < 5000, "Validator received a renewed five-second allowance");
                    c.Now += remaining;
                    return Task.FromResult(ReadResult.Json(Data.Map("integrityValid", true, "unattendedVerdict", "not-evaluated")));
                }).ConfigureAwait(false);
                Check(c.Now <= 45000 && (long)result["finalizationMs"] == c.Now - 35000, "Aggregate finalization exceeded cutoff plus ten seconds");
                Check(inspected != exhaustedDeadline, "Inspection admitted after shutdown deadline or omitted with available budget");
                if (exhaustedDeadline) Check(!(bool)result["writerSettled"], "Unavailable disposal reported as settled");
            }
            var cancelled = await Run(root, 120, (source, writer) => source.Cancel = new CancellationTokenSource()).ConfigureAwait(false);
            Check((string)cancelled.Result["state"] == "interrupted" && (string)cancelled.Result["reason"] == "caller-cancelled", "Cancellation not finite");
            var skew = await Run(root, 35, (source, writer) => source.Override = r => { if (r.Kind == "http") source.Clock.Skew -= 5000; return null; }).ConfigureAwait(false);
            Check((bool)skew.Writer.Manifest["clockUncertain"] && skew.Writer.Records.Any(r => (string)r["outcome"] == "clock-uncertain"), "Clock discontinuity hidden");
            var stale = await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "http" ? ReadResult.Json(Data.Map(
                "schema", "nightweaver-observation-v1", "projectId", "fixture", "key", r.Id, "health", "fresh", "observedAt", "2025-12-31T22:00:00Z",
                "epoch", "old-web", "policy", Data.Map("maxSnapshotAgeMs", 120000, "maxPendingAgeMs", 5000), "validatedAgeMs", 180001, "pendingAgeMs", null)) : null).ConfigureAwait(false);
            Check(stale.Writer.Records.Any(r => (string)r["failureCode"] == "unverified-or-unhealthy-envelope" && (string)r["sourceObservedUtc"] == "2025-12-31T22:00:00.0000000Z"), "HTTP receipt refreshed stale source time");
            var reuse = await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "process" && source.Clock.Now > 10000 ?
                ReadResult.Json(Data.Map("processId", 42, "processStartedUtc", "2026-01-01T00:00:10Z")) : null).ConfigureAwait(false);
            Check(reuse.Writer.Records.Any(r => (string)r["failureCode"] == "owner-identity-unverified"), "PID reuse accepted without creation match");
            string selectedInvocation = "20260101T0000000000000Z-implementer";
            var logs = await Run(root, 50, (source, writer) =>
            {
                string id = "selected-1/" + selectedInvocation;
                source.Options.InvocationLogs[id] = Path.Combine(source.Options.Installation, "logs", "fixture", "selected-1", selectedInvocation + ".log");
                source.Override = r => r.Kind == "invocation" ? new ReadResult { Bytes = new byte[13], InvocationId = selectedInvocation,
                    Offset = r.Offset, Generation = source.Clock.Now < 10000 ? "old-file" : "new-file" } : null;
            }).ConfigureAwait(false);
            Check(logs.Writer.Records.Any(r => (string)r["failureCode"] == "log-generation-replaced"), "Log replacement was flattened");
            Check(logs.Source.Trace.Where(t => t.Kind == "invocation").Select(t => t.Offset).Take(3).SequenceEqual(new long[] { 0, 13, 0 }), "Log cursor did not restart after replacement gap");
            var cappedLogs = await Run(root, 2100, (source, writer) =>
            {
                string id = "selected-1/" + selectedInvocation;
                source.Options.InvocationLogs[id] = Path.Combine(source.Options.Installation, "logs", "fixture", "selected-1", selectedInvocation + ".log");
                source.Override = r => r.Kind == "invocation" ? new ReadResult { Bytes = new byte[65536], InvocationId = selectedInvocation, Offset = r.Offset,
                    Generation = (source.Trace.Count % 2).ToString() } : null;
            }).ConfigureAwait(false);
            Check(cappedLogs.Source.Trace.Count(t => t.Kind == "invocation") == 128, "Invocation bytes escaped eight-MiB cross-generation cap");
            Check(cappedLogs.Writer.Records.Count(r => (string)r["failureCode"] == "invocation-source-limit") == 1, "Source cap exhausted without terminal source diagnostic");
            var discovery = await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "file" && r.Id == "factory" ?
                ReadResult.Json(Data.Map("schema_version", 2, "project", "fixture", "target_repository", source.Options.Repository, "runtime_id", new string('d', 32),
                    "observed_at", Data.Utc(source.Clock.UtcNow), "delivery", Data.Map("slots", new[] { Data.Map("bead_id", "selected-1", "invocation", Data.Map("bead_id", "selected-1", "invocation_id", selectedInvocation)) }))) : null).ConfigureAwait(false);
            Check(discovery.Source.Trace.Any(t => t.Kind == "invocation" && t.Id == "selected-1/" + selectedInvocation), "Corroborated selected invocation not discovered");
            var invalid = Options(root); invalid.WorkloadIds[1] = invalid.WorkloadIds[0];
            try { Collector.Validate(invalid); Check(false, "Duplicate workload accepted"); } catch (InvalidDataException) { cases++; }
            invalid = Options(root); invalid.EvidenceRoot = invalid.Installation;
            try { Collector.Validate(invalid); Check(false, "Internal archive accepted"); } catch (InvalidDataException) { cases++; }
            invalid = Options(root); invalid.WebUrl = "http://user:credential-canary@localhost/";
            try { Collector.Validate(invalid); Check(false, "Credential origin accepted"); } catch (InvalidDataException) { cases++; }
            invalid = Options(root); invalid.ContractRevision = "unknown";
            try { Collector.Validate(invalid); Check(false, "Invented contract identity accepted"); } catch (InvalidDataException) { cases++; }
            try
            {
                await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "directory" && r.Id == "evidence" ?
                    new ReadResult { Bytes = Encoding.UTF8.GetBytes(source.Options.Repository) } : null).ConfigureAwait(false);
                Check(false, "Physical repository alias accepted as external evidence");
            }
            catch (InvalidDataException) { cases++; }
            try
            {
                await Run(root, 35, (source, writer) => source.Override = r => r.Kind == "file" && r.Id == "manifest" ? ReadResult.Failure("missing", "fixture-missing") : null).ConfigureAwait(false);
                Check(false, "Missing installed identity was fabricated");
            }
            catch (InvalidDataException) { cases++; }
            var env = LocalSources.ReadEnvironment();
            Check(env["BEADS_DB"] == null && env["BD_DOLT_AUTO_COMMIT"] == "off" && env["BD_NO_HOOKS"] == "true" && env["GIT_OPTIONAL_LOCKS"] == "0", "Read-only environment escaped");
            // Real writer boundary with a simulated clock: stopped fixtures are inspected
            // by the delivered independent validator in the PowerShell harness.
            Directory.CreateDirectory(Path.Combine(root, "archives"));
            var archives = new List<string>();
            foreach (int seconds in new[] { 35, 0 })
            {
                var o = Options(root); o.StopAfterSeconds = seconds;
                var c = new FixtureClock(); var s = new FixtureSources { Clock = c, Options = o };
                // The memory fixture above checks the entire eight-hour schedule.
                // Exercise disk publication only across a short final interval.
                if (seconds == 0) c.Now = 28800000 - 35000;
                var path = Path.Combine(root, "archives", seconds == 0 ? "complete" : "interrupted");
                using (var w = new ArchiveWriter(path, new Limits())) await new Collector(o, c, s, w, CancellationToken.None).Run().ConfigureAwait(false);
                archives.Add(path);
            }
            foreach (var limits in new[] { new Limits { Records = 8 }, new Limits { Entries = 8 }, new Limits { PayloadBytes = 1 }, new Limits { ArchiveBytes = 65536 }, new Limits { StreamBytes = 4096 } })
            {
                var o = Options(root); o.StopAfterSeconds = 120;
                var c = new FixtureClock(); var s = new FixtureSources { Clock = c, Options = o };
                var path = Path.Combine(root, "archives", Guid.NewGuid().ToString("N"));
                using (var w = new ArchiveWriter(path, limits))
                {
                    var result = await new Collector(o, c, s, w, CancellationToken.None).Run().ConfigureAwait(false);
                    Check((string)result["state"] == "interrupted", "Tightened cap did not stop");
                }
                archives.Add(path);
            }
            {
                var o = Options(root); o.StopAfterSeconds = 35;
                var c = new FixtureClock(); var s = new FixtureSources { Clock = c, Options = o };
                var path = Path.Combine(root, "archives", "write-interruption"); int writes = 0;
                using (var w = new ArchiveWriter(path, new Limits(), () => { if (++writes == 6) throw new IOException("injected-append-interruption"); }))
                {
                    var result = await new Collector(o, c, s, w, CancellationToken.None).Run().ConfigureAwait(false);
                    Check((string)result["reason"] == "storage-or-archive-limit" && w.Count == 6, "Interrupted append collided with reserved terminal publication");
                }
                archives.Add(path);
            }
            Check(!JsonSerializer.Serialize(full.Result).Contains("credential-canary"), "Untrusted error escaped summary");
            return Data.Map("passed", cases, "failed", 0, "skipped", 0, "archives", archives);
        }
    }
}
