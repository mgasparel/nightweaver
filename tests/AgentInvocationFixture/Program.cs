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
    private const uint ControlBreakEvent = 1;

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--nightweaver-send-control-break")
            return SendConsoleBreak(args[1]);

        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "bd", StringComparison.OrdinalIgnoreCase))
            return RunBdShim(args);
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "gh", StringComparison.OrdinalIgnoreCase))
            return RunGhShim(args);

        if (Environment.GetEnvironmentVariable("NIGHTWEAVER_INVOCATION_PURPOSE") == "outcome-classifier")
            return RunOutcomeClassifier();

        var classifierAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION");
        if (classifierAction is not null) return RunClassifierAction(classifierAction);
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

    private static int RunOutcomeClassifier()
    {
        using var input = Console.OpenStandardInput();
        using var stream = new MemoryStream();
        input.CopyTo(stream);
        var prompt = Encoding.UTF8.GetString(stream.ToArray());
        var classifierPidPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_PID_FILE");
        if (!string.IsNullOrWhiteSpace(classifierPidPath))
        {
            var startTime = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(classifierPidPath, $"{Environment.ProcessId}|{startTime}", new UTF8Encoding(false));
        }
        var classifierStartedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_STARTED_FILE");
        if (!string.IsNullOrWhiteSpace(classifierStartedFile))
            File.WriteAllText(classifierStartedFile, Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID") ?? string.Empty,
                new UTF8Encoding(false));
        var classifierReleaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_RELEASE_FILE");
        if (!string.IsNullOrWhiteSpace(classifierReleaseFile))
            WaitForFile(classifierReleaseFile, TimeSpan.FromMinutes(2));
        const string evidenceMarker = "Evidence JSON:\n";
        var evidenceStart = prompt.IndexOf(evidenceMarker, StringComparison.Ordinal);
        if (evidenceStart < 0) return 65;
        evidenceStart += evidenceMarker.Length;
        var evidenceEnd = prompt.IndexOf("\nReturn only one JSON object", evidenceStart, StringComparison.Ordinal);
        if (evidenceEnd < 0) return 65;
        using var evidence = JsonDocument.Parse(prompt[evidenceStart..evidenceEnd]);
        var root = evidence.RootElement;
        var role = root.GetProperty("role").GetString();
        var sources = root.GetProperty("sources").ToString();
        var choice = sources.Contains("product decision:", StringComparison.OrdinalIgnoreCase)
            ? "product_decision_required"
            : role switch
            {
                "implementer" => "ready_for_review",
                "reviewer" when sources.Contains("blocking finding:", StringComparison.OrdinalIgnoreCase) => "changes_required",
                "reviewer" => "approved",
                "qa" when sources.Contains("qa failure:", StringComparison.OrdinalIgnoreCase) => "changes_required",
                "qa" => "approved",
                _ => "incomplete_or_unclear"
            };
        var schema = "nightweaver-" + role + "-outcome-v1";
        var question = role switch
        {
            "implementer" => "implementer_outcome",
            "reviewer" => "reviewer_outcome",
            "qa" => "qa_outcome",
            _ => "unknown_outcome"
        };
        var response = JsonSerializer.Serialize(new
        {
            schema,
            question,
            choice
        });
        Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(response));
        Console.OpenStandardOutput().Flush();
        return 0;
    }

    private static int RunGhShim(string[] args)
    {
        var logPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_GH_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
            File.AppendAllText(logPath, JsonSerializer.Serialize(args) + Environment.NewLine, new UTF8Encoding(false));

        var branch = Run("git", ["branch", "--show-current"]);
        var head = Run("git", ["rev-parse", "HEAD"]);
        if (args.Length >= 2 && args[0] == "pr" && args[1] == "checks")
        {
            Console.WriteLine($"All required fixture checks passed for {head.Trim()}.");
            return Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CI_RESULT") == "fail" ? 1 : 0;
        }

        if (args.Length >= 2 && args[0] == "pr" && args[1] == "merge")
        {
            if (!args.Contains("--squash", StringComparer.Ordinal)
                || !args.Contains("--delete-branch", StringComparer.Ordinal))
                throw new InvalidOperationException("Fixture merge requires --squash and --delete-branch.");
            Run("git", ["push", "origin", "HEAD:refs/heads/main"]);
            if (args.Contains("--delete-branch", StringComparer.Ordinal))
                Run("git", ["push", "origin", "--delete", branch.Trim()]);
            Console.WriteLine("Fixture pull request merged.");
            return 0;
        }

        var remoteMain = Run("git", ["ls-remote", "origin", "refs/heads/main"])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var state = string.Equals(remoteMain, head.Trim(), StringComparison.OrdinalIgnoreCase) ? "MERGED" : "OPEN";
        var response = JsonSerializer.Serialize(new
        {
            state,
            baseRefName = "main",
            headRefName = branch.Trim(),
            headRefOid = head.Trim(),
            url = "https://example.invalid/pull/fixture",
            mergeCommit = state == "MERGED" ? new { oid = head.Trim() } : null
        });
        Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes(response));
        Console.OpenStandardOutput().Flush();
        return 0;
    }

    private static int RunBdShim(string[] args)
    {
        var realBd = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_REAL_PATH")
            ?? throw new InvalidOperationException("The native bd shim real executable path is missing.");
        var mode = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_MODE");
        var targetId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_TARGET");
        var logPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_BD_SHIM_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            File.AppendAllText(logPath, JsonSerializer.Serialize(new { args, mode }) + Environment.NewLine, new UTF8Encoding(false));
        }

        var updateIndex = Array.FindIndex(args, argument => argument == "update");
        var isRecoveryRelease = updateIndex >= 0 && updateIndex + 1 < args.Length
            && (targetId is null || args[updateIndex + 1] == targetId)
            && HasOptionValue(args, "--if-status", "in_progress")
            && HasOptionValue(args, "--status", "open");
        var isStartupPauseWrite = updateIndex >= 0 && updateIndex + 1 < args.Length
            && (targetId is null || args[updateIndex + 1] == targetId)
            && HasOptionValue(args, "--add-label", "needs-human");
        var isStartupDiagnosticComment = Array.FindIndex(args, argument => argument == "comments") is var commentIndex
            && commentIndex >= 0 && commentIndex + 2 < args.Length
            && args[commentIndex + 1] == "add"
            && (targetId is null || args[commentIndex + 2] == targetId);

        if (mode == "startup-cleanup-pause-race" && targetId is not null && logPath is not null
            && args.Contains("--include-ephemeral", StringComparer.Ordinal)
            && File.ReadAllLines(logPath).Count(line => line.Contains("--include-ephemeral", StringComparison.Ordinal)
                ) == 3)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", targetId, "--add-label", "needs-human"]);
            if (mutation.ExitCode != 0)
            {
                WriteResult(mutation);
                return mutation.ExitCode;
            }
        }
        if (mode == "startup-cleanup-label-write-failure" && isStartupPauseWrite)
        {
            Console.Error.WriteLine("fixture startup cleanup label write failure");
            return 75;
        }
        if (mode == "startup-cleanup-comment-write-failure" && isStartupDiagnosticComment)
        {
            Console.Error.WriteLine("fixture startup cleanup comment write failure");
            return 76;
        }

        if (mode == "read-failure" && args.Contains("list", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("fixture tracker read failure");
            return 71;
        }
        if (mode == "recovery-write-failure" && isRecoveryRelease)
        {
            Console.Error.WriteLine("fixture recovery write failure");
            return 72;
        }
        if (mode == "recovery-guard-mismatch" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--status", "closed", "--force"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
        }
        if (mode == "recovery-assignee-mismatch" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--assignee", "person@example.invalid"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
        }
        if (mode == "recovery-label-race" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--add-label", "needs-human"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
        }
        if (mode == "recovery-uncertain-foreign" && isRecoveryRelease)
        {
            var mutation = RunRealBd(realBd, ["--sandbox", "update", args[updateIndex + 1], "--assignee", "person@example.invalid"]);
            WriteResult(mutation);
            if (mutation.ExitCode != 0) return mutation.ExitCode;
            Console.Error.WriteLine("fixture recovery write failed after a concurrent foreign claim");
            return 74;
        }

        var result = RunRealBd(realBd, args);
        WriteResult(result);
        if (mode == "recovery-uncertain-write" && isRecoveryRelease && result.ExitCode == 0) return 73;
        return result.ExitCode;
    }

    private static bool HasOptionValue(string[] args, string option, string value)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == option && args[index + 1] == value) return true;
        return false;
    }

    private static FixtureProcessResult RunRealBd(string executable, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the real bd executable.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new(process.ExitCode, stdout, stderr);
    }

    private static void WriteResult(FixtureProcessResult result)
    {
        Console.Out.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
    }

    private sealed record FixtureProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static int RunClassifierAction(string action)
    {
        using var input = Console.OpenStandardInput();
        using var inputBytes = new MemoryStream();
        input.CopyTo(inputBytes);
        var exactInput = inputBytes.ToArray();
        var capturedInputPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CAPTURE_STDIN");
        if (!string.IsNullOrWhiteSpace(capturedInputPath)) File.WriteAllBytes(capturedInputPath, exactInput);

        var reportPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                cwd = Environment.CurrentDirectory,
                beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID"),
                tmp = Environment.GetEnvironmentVariable("TMP"),
                temp = Environment.GetEnvironmentVariable("TEMP"),
                tmpdir = Environment.GetEnvironmentVariable("TMPDIR"),
                arguments = Environment.GetCommandLineArgs().Skip(1).ToArray()
            }), new UTF8Encoding(false));
        }

        var output = Console.OpenStandardOutput();
        var error = Console.OpenStandardError();
        switch (action)
        {
            case "duplicate":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}\n"));
                return 0;
            case "different":
                output.Write(Encoding.UTF8.GetBytes(" {\"verdict\":\"different\"} \n"));
                return 0;
            case "stderr-verdict":
                error.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"different\"}"));
                return 0;
            case "nonzero":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                return 17;
            case "invalid":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\",\"verdict\":\"different\"}"));
                return 0;
            case "oversized":
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                output.Write(Enumerable.Repeat((byte)' ', 20 * 1024).ToArray());
                return 0;
            case "timeout-descendant":
            {
                var handshake = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_HANDSHAKE")
                    ?? throw new InvalidOperationException("Classifier timeout handshake path is missing.");
                File.WriteAllText(handshake + ".root", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var classifierAction = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION");
                Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", null);
                try { SpawnProcess("hold", handshake + ".child"); }
                finally { Environment.SetEnvironmentVariable("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION", classifierAction); }
                WaitForFile(handshake + ".child");
                while (true) Thread.Sleep(1000);
            }
            case "flood":
                output.Write(Enumerable.Repeat((byte)'O', 600_000).ToArray());
                error.Write(Enumerable.Repeat((byte)'E', 600_000).ToArray());
                output.Write(Encoding.UTF8.GetBytes("{\"verdict\":\"duplicate\"}"));
                return 0;
            default:
                return 67;
        }
    }

    private static int RunStageAction(string action)
    {
        Console.OpenStandardInput().CopyTo(Stream.Null);
        var beadId = Environment.GetEnvironmentVariable("NIGHTWEAVER_BEAD_ID")
            ?? throw new InvalidOperationException("NIGHTWEAVER_BEAD_ID is missing.");
        var pidPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_PID_FILE");
        if (!string.IsNullOrWhiteSpace(pidPath))
        {
            var startTime = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(pidPath, $"{Environment.ProcessId}|{startTime}");
        }
        var launchLog = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG");
        if (!string.IsNullOrWhiteSpace(launchLog))
            File.AppendAllText(launchLog, $"{beadId}|{Environment.ProcessId}{Environment.NewLine}", new UTF8Encoding(false));

        switch (action)
        {
            case "slot-implement":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-implement-unlabeled":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-implement-push":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-implement-pause":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation before pause"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                Run("bd", ["comments", "add", beadId, "Implementer product decision: what database schema should be used?"]);
                break;
            case "slot-implement-pause-unlabeled":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation before pause"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                Run("bd", ["comments", "add", beadId, "Implementer product decision: should this feature be enabled by default?"]);
                break;
            case "slot-review-pause":
                Run("bd", ["comments", "add", beadId, "Reviewer product decision: does this error handling match product requirements?"]);
                break;
            case "slot-qa-pause":
                Run("bd", ["comments", "add", beadId, "QA product decision: is this edge case behavior intended?"]);
                break;
            case "pause-nonzero":
                Run("bd", ["comments", "add", beadId, "question"]);
                Run("bd", ["update", beadId, "--add-label", "product-decision-required"]);
                return 19;
            case "pause-dirty":
                Run("bd", ["comments", "add", beadId, "question"]);
                Run("bd", ["update", beadId, "--add-label", "product-decision-required"]);
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "dirty.txt"), "dirty\n");
                break;
            case "pause-stage-change":
                Run("bd", ["comments", "add", beadId, "question"]);
                Run("bd", ["update", beadId, "--remove-label", "stage:implement", "--add-label", "stage:review", "--add-label", "product-decision-required"]);
                break;
            case "pool-implement-gated":
            {
                var startedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_STARTED_FILE")
                    ?? throw new InvalidOperationException("Pool fixture started-file path is missing.");
                var releaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RELEASE_FILE")
                    ?? throw new InvalidOperationException("Pool fixture release-file path is missing.");
                File.WriteAllText(startedFile, beadId, new UTF8Encoding(false));
                WaitForFile(releaseFile, TimeSpan.FromMinutes(2));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "delivery-slot.txt"), "slot implementation\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "slot-sentinel.bin"), "keep across roles\n");
                Run("git", ["add", "delivery-slot.txt"]);
                Run("git", ["commit", "-m", "slot fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            }
            case "pool-implement-fail-gated":
            {
                var startedFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_STARTED_FILE")
                    ?? throw new InvalidOperationException("Pool fixture started-file path is missing.");
                var releaseFile = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIXTURE_RELEASE_FILE")
                    ?? throw new InvalidOperationException("Pool fixture release-file path is missing.");
                File.WriteAllText(startedFile, beadId, new UTF8Encoding(false));
                WaitForFile(releaseFile, TimeSpan.FromMinutes(2));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "unfinished-after-stop.txt"),
                    "preserve this failed current turn\n", new UTF8Encoding(false));
                return 23;
            }
            case "slot-implement-repair":
                File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "delivery-repair.txt"), "repair committed\n");
                Run("git", ["add", "delivery-repair.txt"]);
                Run("git", ["commit", "-m", "slot fixture repair"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "slot-review":
                Run("bd", ["comments", "add", beadId, "Reviewer evidence: no blocking findings remain; approval is supported."]);
                break;
            case var reviewRejection when action.StartsWith("slot-review-reject-", StringComparison.Ordinal):
            {
                var cycle = int.Parse(reviewRejection["slot-review-reject-".Length..], System.Globalization.CultureInfo.InvariantCulture);
                Run("bd", ["comments", "add", beadId,
                    $"Blocking finding: reviewer cycle {cycle}; the serialized response is malformed; correct it and add a reproducible regression check."]);
                break;
            }
            case "slot-qa":
                Run("bd", ["comments", "add", beadId, "QA evidence: required checks passed and observed behavior matches acceptance criteria."]);
                break;
            case var qaRejection when action.StartsWith("slot-qa-reject-", StringComparison.Ordinal):
            {
                var cycle = int.Parse(qaRejection["slot-qa-reject-".Length..], System.Globalization.CultureInfo.InvariantCulture);
                Run("bd", ["comments", "add", beadId,
                    $"QA failure: cycle {cycle}; POST /fixture returned 500; expected 200. Reproduce with the fixture command and fix the handler."]);
                break;
            }
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
                Run("git", ["commit", "--allow-empty", "-m", "fixture implementation"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "review-nonzero":
                Console.WriteLine("fixture returned a nonzero exit after a legal Bead transition");
                return 19;
            case "nonzero-dirty":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "README.md"), "retry must restore tracked content\n");
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "retry-untracked.txt"), "retry must remove this\n");
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored"));
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".nightweaver-ignored", "keep.bin"), "ignored sentinel\n");
                Console.WriteLine("fixture returned nonzero after creating retry cleanup sentinels");
                return 19;
            case "nonzero-retain":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "attempt-untracked.txt"), "retain failed attempt evidence\n");
                Console.WriteLine("fixture returned nonzero while inherited work was present");
                return 19;
            case "resume-inherited":
                Run("git", ["add", "-A"]);
                Run("git", ["commit", "-m", "resume inherited retained work"]);
                Run("git", ["push", "origin", $"HEAD:nightweaver/{beadId}"]);
                break;
            case "review-dirty":
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fixture-dirty.txt"), "dirty\n");
                break;
            case "review-commit":
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
            case "merger-verified-merge":
            {
                var defaultBranch = Environment.GetEnvironmentVariable("NIGHTWEAVER_DEFAULT_BRANCH") ?? "main";
                var branch = Run("git", ["branch", "--show-current"]).Trim();
                Run("git", ["fetch", "origin", defaultBranch]);
                var head = Run("git", ["rev-parse", "HEAD"]).Trim();
                var defaultHead = Run("git", ["rev-parse", $"origin/{defaultBranch}"]).Trim();
                if (!IsAncestor(defaultHead, head))
                    throw new InvalidOperationException("Fixture branch is not synchronized with its configured default branch.");

                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "merger-verified.txt"),
                    "fixture merge after synchronized current-head checks\n", new UTF8Encoding(false));
                Run("git", ["add", "merger-verified.txt"]);
                Run("git", ["commit", "-m", "fixture verified Merger change"]);
                head = Run("git", ["rev-parse", "HEAD"]).Trim();
                Run("git", ["push", "--force-with-lease", "origin", $"HEAD:refs/heads/{branch}"]);
                var pullRequest = "https://example.invalid/pull/fixture";
                var beforeChecks = ReadPullRequest(pullRequest);
                if (beforeChecks.GetProperty("state").GetString() != "OPEN"
                    || beforeChecks.GetProperty("headRefOid").GetString() != head)
                    throw new InvalidOperationException("Fixture pull request did not identify the synchronized current HEAD.");
                Run("gh", ["pr", "checks", pullRequest, "--watch"]);
                var afterChecks = ReadPullRequest(pullRequest);
                if (afterChecks.GetProperty("headRefOid").GetString() != head)
                    throw new InvalidOperationException("Fixture pull request head changed while checks were running.");
                Run("gh", ["pr", "merge", pullRequest, "--squash", "--delete-branch"]);
                var merged = ReadPullRequest(pullRequest);
                if (merged.GetProperty("state").GetString() != "MERGED")
                    throw new InvalidOperationException("Fixture pull request was not confirmed merged.");

                var mergeCommit = merged.GetProperty("mergeCommit").GetProperty("oid").GetString();
                Run("bd", ["comments", "add", beadId,
                    $"nightweaver-merge-v1 pull_request={pullRequest} checked_head={head} required_checks=passed merge_method=squash merge_commit={mergeCommit}"]);
                Run("bd", ["close", beadId]);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown fixture Agent action '{action}'.");
        }

        Console.WriteLine($"fixture action completed: {action}");
        return 0;
    }

    private static void UpdateLabels(string beadId, string oldLabel, string newLabel) =>
        Run("bd", ["update", beadId, "--remove-label", oldLabel, "--add-label", newLabel]);

    private static int SendConsoleBreak(string processIdText)
    {
        if (!OperatingSystem.IsWindows() || !uint.TryParse(processIdText, out var processId)) return 68;
        // A console helper started under a test host may inherit its own console despite
        // CREATE_NO_WINDOW. Detach first so it can attach to the isolated apphost console.
        _ = FreeConsole();
        if (!AttachConsole(processId)) return Marshal.GetLastWin32Error();
        try
        {
            return GenerateConsoleCtrlEvent(ControlBreakEvent, processId) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { _ = FreeConsole(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    private static bool IsAncestor(string ancestor, string descendant)
    {
        var result = RunAllowFailure("git", ["merge-base", "--is-ancestor", ancestor, descendant]);
        return result.ExitCode == 0;
    }

    private static JsonElement ReadPullRequest(string pullRequest)
    {
        var json = Run("gh", ["pr", "view", pullRequest, "--json", "state,baseRefName,headRefName,headRefOid,url"]);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunAllowFailure(
        string fileName, IReadOnlyList<string> arguments)
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
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static void RejectToImplement(string beadId, string stage, int repairCycles, string finding)
    {
        Run("bd", ["comments", "add", beadId, finding]);
        Run("bd", ["update", beadId, "--remove-label", stage, "--add-label", "stage:implement",
            "--set-metadata", $"repair_cycles={repairCycles}"]);
    }

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
        var launchArguments = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { fixtureDll, mode }.Concat(arguments)
            : new[] { mode }.Concat(arguments);
        var commandLineText = string.Join(' ', new[] { executable }.Concat(launchArguments).Select(QuoteArgument));
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
        => WaitForFile(path, TimeSpan.FromSeconds(15));

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed > timeout) throw new TimeoutException($"Fixture handshake timed out: {path}");
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
