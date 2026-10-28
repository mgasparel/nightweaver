using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum NeedsHumanCause
{
    OperationalRetriesExhausted,
    DuplicateFailure,
    RepairBudgetExhausted,
    EnvironmentBlocked
}

internal sealed record NeedsHumanRequest(
    string BeadId,
    string ExpectedAssignee,
    NeedsHumanCause Cause,
    AgentRole Role,
    string Reason,
    IReadOnlyList<string> LatestFindings,
    string? InvocationId = null,
    IReadOnlyList<string>? LogFilePaths = null,
    WorktreePurpose? CleanupPurpose = null,
    BeadWorkflowState? ExpectedWorkflowState = null);

internal sealed record NeedsHumanEscalationResult(
    bool PauseRecorded,
    bool DiagnosticRecorded,
    bool CleanupCompleted,
    bool ClaimReleased,
    IReadOnlyList<string> Failures)
{
    internal bool Succeeded => PauseRecorded && DiagnosticRecorded && CleanupCompleted && ClaimReleased && Failures.Count == 0;
}

internal sealed class NeedsHumanEscalationException : NightweaverException
{
    internal NeedsHumanEscalationException(string message, NeedsHumanEscalationResult progress, Exception inner)
        : base(message, inner) => Progress = progress;

    internal NeedsHumanEscalationResult Progress { get; }
}

/// <summary>
/// Records needs-human diagnostics, safely disposes one owned pause environment, and releases its claim.
/// Callers must serialize escalation per Bead and wait for all Agent/classifier processes and log writers to settle.
/// </summary>
internal sealed class NeedsHumanEscalator
{
    private const int MaxDiagnosticBytes = 32 * 1024;
    private static readonly TimeSpan ClaimReleaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly BeadsReader beadsReader;
    private readonly BeadsWriter beadsWriter;
    private readonly PauseEnvironmentCleaner cleaner;

    internal NeedsHumanEscalator(IProcessRunner processRunner, PauseEnvironmentCleaner? cleaner = null)
    {
        beadsReader = new BeadsReader(processRunner);
        beadsWriter = new BeadsWriter(processRunner);
        this.cleaner = cleaner ?? new PauseEnvironmentCleaner(processRunner);
    }

    internal Task<NeedsHumanEscalationResult> EscalateAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        NeedsHumanRequest request,
        CancellationToken cancellationToken) =>
        EscalateAsync(paths, project.Name, repository, request, cancellationToken);

    internal async Task<NeedsHumanEscalationResult> EscalateAsync(
        HostPaths paths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        NeedsHumanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(paths, project, request);
        cancellationToken.ThrowIfCancellationRequested();

        var initialBead = await ReadBeadAsync(repository, request.BeadId, cancellationToken);
        var initialState = ParseState(initialBead);
        if (request.ExpectedWorkflowState is { } expectedWorkflowState && initialState != expectedWorkflowState)
            throw new ToolExecutionException($"Bead '{request.BeadId}' workflow state changed before needs-human escalation; refusing to adopt a new baseline.");
        var purpose = ResolvePurpose(initialBead, initialState, request);
        var initialClaimOwned = ValidateInitialClaim(initialBead, initialState, request);
        var initialUnclaimedPause = initialState.NeedsHuman && initialState.Status == "open" && initialBead.Assignee is null;
        var expectedStage = initialState.Stage;
        var expectedRepairCycles = initialState.RepairCycles;

        var preflight = await cleaner.InspectAsync(paths, project, repository, request.BeadId, purpose, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Prove required fields fit before the first tracker mutation. Only findings may be truncated.
        _ = CreateDiagnostic(request, initialBead.IssueType, expectedStage, paths, project, preflight);

        var pauseRecorded = initialState.NeedsHuman;
        var mayReleaseClaim = false;
        var diagnosticRecorded = false;
        var cleanupCompleted = false;
        var claimReleased = initialUnclaimedPause;
        var failures = new List<string>();
        Exception? primaryFailure = null;
        Exception? releaseFailure = null;
        OperationCanceledException? cancellationFailure = null;

        try
        {
            if (!pauseRecorded || initialState.ProductDecisionRequired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var guardedAssignee = initialClaimOwned ? request.ExpectedAssignee : string.Empty;
                var guardedStatus = initialClaimOwned ? "in_progress" : "open";
                await beadsWriter.ApplyNeedsHumanAsync(repository, request.BeadId, guardedAssignee, guardedStatus, cancellationToken);
                pauseRecorded = true;
                mayReleaseClaim = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var paused = await ReadPausedBeadAsync(repository, request, purpose, initialClaimOwned, initialUnclaimedPause,
                expectedStage, expectedRepairCycles, cancellationToken);
            var diagnostic = CreateDiagnostic(request, paused.Bead.IssueType, paused.State.Stage, paths, project, preflight);
            cancellationToken.ThrowIfCancellationRequested();
            await beadsWriter.AddCommentAsync(repository, request.BeadId, diagnostic, cancellationToken);
            diagnosticRecorded = true;
            if (initialClaimOwned) mayReleaseClaim = true;

            cancellationToken.ThrowIfCancellationRequested();
            _ = await ReadPausedBeadAsync(repository, request, purpose, initialClaimOwned, initialUnclaimedPause,
                expectedStage, expectedRepairCycles, cancellationToken);
            if (preflight.Safe)
            {
                await cleaner.CleanAsync(paths, project, repository, request.BeadId, preflight, cancellationToken);
                cleanupCompleted = true;
            }
            else
            {
                failures.Add(preflight.Refusal ?? "Pause cleanup was refused during preflight.");
            }
        }
        catch (OperationCanceledException exception)
        {
            cancellationFailure = exception;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            if (mayReleaseClaim)
            {
                try
                {
                    using var releaseTimeout = new CancellationTokenSource(ClaimReleaseTimeout);
                    var current = await ReadBeadAsync(repository, request.BeadId, releaseTimeout.Token);
                    var currentState = ParseState(current);
                    if (currentState.IssueType != initialBead.IssueType || !currentState.NeedsHuman
                        || currentState.Stage != expectedStage || currentState.RepairCycles != expectedRepairCycles)
                        releaseFailure = new ToolExecutionException($"release needs-human claim for Bead '{request.BeadId}' was refused because its workflow identity changed");
                    else if (currentState.Status == "open" && current.Assignee is null)
                    {
                        claimReleased = true;
                    }
                    else if (initialClaimOwned && currentState.NeedsHuman && currentState.Status == "in_progress"
                        && string.Equals(current.Assignee, request.ExpectedAssignee, StringComparison.Ordinal))
                    {
                        await beadsWriter.ReleaseClaimAsync(repository, request.BeadId, request.ExpectedAssignee, releaseTimeout.Token);
                        claimReleased = true;
                    }
                    else
                    {
                        releaseFailure = new ToolExecutionException($"release needs-human claim for Bead '{request.BeadId}' was refused because its current status or assignee no longer matches the observed owner");
                    }
                    if (releaseFailure is not null) failures.Add($"Claim release failed: {releaseFailure.Message}");
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;
                    failures.Add($"Claim release failed: {exception.Message}");
                }
            }
        }

        if (cancellationFailure is not null)
        {
            var progress = new NeedsHumanEscalationResult(pauseRecorded, diagnosticRecorded, cleanupCompleted, claimReleased,
                failures.AsReadOnly());
            cancellationFailure.Data[nameof(NeedsHumanEscalationResult)] = progress;
            throw cancellationFailure;
        }

        if (primaryFailure is not null || releaseFailure is not null)
        {
            if (primaryFailure is not null && diagnosticRecorded && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var supplemental = CreateSupplementalComment(request.BeadId, primaryFailure.Message);
                    await beadsWriter.AddCommentAsync(repository, request.BeadId, supplemental, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failures.Add($"Supplemental diagnostic comment failed: {exception.Message}");
                }
            }

            var failure = primaryFailure ?? releaseFailure!;
            failures.Insert(0, failure.Message);
            var progress = new NeedsHumanEscalationResult(pauseRecorded, diagnosticRecorded, cleanupCompleted, claimReleased,
                failures.AsReadOnly());
            throw new NeedsHumanEscalationException($"Needs-human escalation for Bead '{request.BeadId}' did not complete: {failure.Message}",
                progress, failure);
        }

        return new NeedsHumanEscalationResult(pauseRecorded, diagnosticRecorded, cleanupCompleted, claimReleased,
            failures.AsReadOnly());
    }

    private async Task<BeadSnapshot> ReadBeadAsync(ValidatedTargetRepository repository, string beadId, CancellationToken cancellationToken)
    {
        BeadsInventory inventory;
        try { inventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken); }
        catch (ToolExecutionException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        if (!inventory.ById.TryGetValue(beadId, out var bead))
            throw new AgentStageInputException($"Bead '{beadId}' was not found in the Target Repository tracker.");
        return bead;
    }

    private async Task<(BeadSnapshot Bead, BeadWorkflowState State)> ReadPausedBeadAsync(
        ValidatedTargetRepository repository,
        NeedsHumanRequest request,
        WorktreePurpose purpose,
        bool initialClaimOwned,
        bool initialUnclaimedPause,
        string? expectedStage,
        int expectedRepairCycles,
        CancellationToken cancellationToken)
    {
        var bead = await ReadBeadAsync(repository, request.BeadId, cancellationToken);
        var state = ParseState(bead);
        _ = ResolvePurpose(bead, state, request);
        if (state.IssueType != (purpose == WorktreePurpose.Planning ? "feature" : "task")
            && !(purpose == WorktreePurpose.Planning && state.IssueType == "bug"))
            throw new ToolExecutionException($"Bead '{request.BeadId}' changed type during needs-human escalation.");
        if (!state.NeedsHuman || state.Planned || state.Stage != expectedStage || state.RepairCycles != expectedRepairCycles)
            throw new ToolExecutionException($"Bead '{request.BeadId}' workflow state changed during needs-human escalation; refusing cleanup.");
        if (initialClaimOwned)
        {
            if (state.Status != "in_progress" || !string.Equals(bead.Assignee, request.ExpectedAssignee, StringComparison.Ordinal))
                throw new ToolExecutionException($"Bead '{request.BeadId}' claim changed during needs-human escalation; refusing cleanup.");
        }
        else if (!initialUnclaimedPause || state.Status != "open" || bead.Assignee is not null)
        {
            throw new ToolExecutionException($"Bead '{request.BeadId}' acquired a claim during needs-human cleanup recovery; refusing to touch its environment.");
        }
        return (bead, state);
    }

    private static BeadWorkflowState ParseState(BeadSnapshot bead)
    {
        try { return BeadWorkflowState.Parse(bead); }
        catch (FormatException) when (bead.Labels.Contains("product-decision-required", StringComparer.Ordinal)
            && bead.Labels.Contains("needs-human", StringComparer.Ordinal))
        {
            // A retry after an interrupted label write must be able to remove the conflicting pause label.
            var normalized = bead with { Labels = bead.Labels.Where(label => label != "product-decision-required").ToArray() };
            try { return BeadWorkflowState.Parse(normalized) with { ProductDecisionRequired = true }; }
            catch (FormatException exception) { throw new AgentStageInputException(exception.Message); }
        }
        catch (FormatException exception) { throw new AgentStageInputException(exception.Message); }
    }

    private static WorktreePurpose ValidateRoleAndType(BeadSnapshot bead, BeadWorkflowState state, AgentRole role)
    {
        var purpose = role switch
        {
            AgentRole.Planner or AgentRole.PlanChallenger => WorktreePurpose.Planning,
            AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa or AgentRole.Merger => WorktreePurpose.Delivery,
            AgentRole.Classifier => throw new AgentStageInputException("Classifier is not a lifecycle role for needs-human escalation."),
            _ => throw new AgentStageInputException($"Unknown Agent role '{role}'.")
        };
        if (state.Status == "closed" || state.Status is not ("open" or "in_progress") || state.Planned)
            throw new AgentStageInputException($"Bead '{bead.Id}' is terminal or planned and cannot be escalated to needs-human.");
        if (purpose == WorktreePurpose.Planning && bead.IssueType is not ("feature" or "bug"))
            throw new AgentStageInputException($"Planning role '{role}' requires a Product Bead of type feature or bug; '{bead.Id}' is '{bead.IssueType}'.");
        if (purpose == WorktreePurpose.Delivery && bead.IssueType != "task")
            throw new AgentStageInputException($"Delivery role '{role}' requires a Plan Bead of type task; '{bead.Id}' is '{bead.IssueType}'.");
        return purpose;
    }

    private static WorktreePurpose ResolvePurpose(BeadSnapshot bead, BeadWorkflowState state, NeedsHumanRequest request)
    {
        if (request.CleanupPurpose is not { } purpose)
            return ValidateRoleAndType(bead, state, request.Role);
        if (!IsLifecycleRole(request.Role))
            throw new AgentStageInputException($"Unknown or non-lifecycle failed role '{request.Role}'.");
        if (state.Status == "closed" || state.Status is not ("open" or "in_progress") || state.Planned)
            throw new AgentStageInputException($"Bead '{bead.Id}' is terminal or planned and cannot be escalated to needs-human.");
        if (purpose == WorktreePurpose.Planning && bead.IssueType is not ("feature" or "bug"))
            throw new AgentStageInputException($"Planning cleanup requires a Product Bead; '{bead.Id}' is '{bead.IssueType}'.");
        if (purpose == WorktreePurpose.Delivery && bead.IssueType != "task")
            throw new AgentStageInputException($"Delivery cleanup requires a Plan Bead; '{bead.Id}' is '{bead.IssueType}'.");
        return purpose;
    }

    private static bool IsLifecycleRole(AgentRole role) => role is AgentRole.Planner or AgentRole.PlanChallenger
        or AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa or AgentRole.Merger;

    private static bool ValidateInitialClaim(BeadSnapshot bead, BeadWorkflowState state, NeedsHumanRequest request)
    {
        if (state.NeedsHuman && state.Status == "open" && bead.Assignee is null) return false;
        if (state.Status == "in_progress" && string.Equals(bead.Assignee, request.ExpectedAssignee, StringComparison.Ordinal)) return true;
        if (bead.Assignee is not null && !string.Equals(bead.Assignee, request.ExpectedAssignee, StringComparison.Ordinal))
            throw new AgentStageInputException($"Bead '{bead.Id}' is claimed by '{bead.Assignee}', not expected owner '{request.ExpectedAssignee}'.");
        throw new AgentStageInputException($"Bead '{bead.Id}' must have the expected live in_progress claim before needs-human escalation.");
    }

    private static void ValidateRequest(HostPaths paths, ProjectSlug project, NeedsHumanRequest request)
    {
        if (!WorktreeProvisioner.IsSafeBeadId(request.BeadId) || !char.IsAsciiLetterOrDigit(request.BeadId[0]))
            throw new AgentStageInputException($"Bead ID '{request.BeadId}' is not a safe managed runtime path segment.");
        if (string.IsNullOrWhiteSpace(request.ExpectedAssignee))
            throw new AgentStageInputException("Expected claim assignee must be nonblank.");
        if (!Enum.IsDefined(request.Cause)) throw new AgentStageInputException($"Unknown needs-human cause '{request.Cause}'.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new AgentStageInputException("Needs-human escalation reason must be nonblank.");
        if (request.LatestFindings is null) throw new AgentStageInputException("Latest findings must be supplied as a list (which may be empty).");
        if (request.LatestFindings.Any(finding => finding is null))
            throw new AgentStageInputException("Latest findings cannot contain null entries.");
        if (request.InvocationId is { } invocation && (string.IsNullOrWhiteSpace(invocation) || invocation.Length > 256))
            throw new AgentStageInputException("Invocation identity must be nonblank and at most 256 characters.");

        var logDirectory = RuntimePaths.Normalize(Path.Combine(paths.LogsRoot(project), request.BeadId));
        var logPaths = request.LogFilePaths ?? Array.Empty<string>();
        if (logPaths.Count > 8 || logPaths.Distinct(StringComparer.Ordinal).Count() != logPaths.Count)
            throw new AgentStageInputException("Supply at most eight distinct diagnostic log file paths.");
        foreach (var logPath in logPaths) ValidateLogFile(logDirectory, logPath);
    }

    private static void ValidateLogFile(string logDirectory, string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !Path.IsPathFullyQualified(logPath))
            throw new AgentStageInputException("Supplied diagnostic log paths must be absolute files under the canonical Bead log directory.");
        string fullPath;
        try { fullPath = Path.GetFullPath(logPath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new AgentStageInputException($"Supplied diagnostic log path '{logPath}' is invalid: {exception.Message}"); }
        var fileName = Path.GetFileName(fullPath);
        var expected = Path.Combine(logDirectory, fileName);
        if (Path.GetDirectoryName(fullPath) is not { } parent || !string.Equals(parent, logDirectory, StringComparison.Ordinal)
            || !string.Equals(fullPath, expected, StringComparison.Ordinal) || !fileName.EndsWith(".log", StringComparison.Ordinal)
            || fileName.Any(character => character <= '\u001f' || "<>:\"/\\|?*".Contains(character))
            || fileName.EndsWith(' ') || fileName.EndsWith('.'))
            throw new AgentStageInputException($"Supplied diagnostic log path '{logPath}' is not a canonical direct .log file under '{logDirectory}'.");

        try
        {
            EnsureNormalDirectoryChain(logDirectory);
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new AgentStageInputException($"Supplied diagnostic log path '{fullPath}' is not a normal file.");
        }
        catch (AgentStageInputException) { throw; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        { throw new AgentStageInputException($"Supplied diagnostic log file '{fullPath}' could not be validated: {exception.Message}"); }
    }

    private static void EnsureNormalDirectoryChain(string directory)
    {
        var chain = new Stack<string>();
        for (string? current = RuntimePaths.Normalize(directory); current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
        while (chain.TryPop(out var path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new AgentStageInputException($"Diagnostic log directory chain contains a reparse point or non-directory at '{path}'.");
        }
    }

    private static string CreateDiagnostic(NeedsHumanRequest request, string issueType, string? stage,
        HostPaths paths, ProjectSlug project, PauseCleanupPlan preflight)
    {
        var logDirectory = RuntimePaths.Normalize(Path.Combine(paths.LogsRoot(project), request.BeadId));
        var logs = request.LogFilePaths is { Count: > 0 } ? request.LogFilePaths.ToArray() : ["unavailable"];
        var body = new NeedsHumanDiagnostic("nightweaver-needs-human-v1", request.BeadId, issueType,
            stage ?? "(none)", CauseName(request.Cause), AgentRoles.ToConfigKey(request.Role), request.Reason,
            "", request.InvocationId ?? "unavailable", logDirectory, logs,
            preflight.Safe ? "passed" : preflight.Refusal ?? "refused");
        var emptyLength = SerializeDiagnostic(body).Length;
        if (emptyLength > MaxDiagnosticBytes)
            throw new AgentStageInputException("Required needs-human diagnostic fields exceed the 32 KiB comment limit.");

        long rawFindingsLength = (long)Math.Max(0, request.LatestFindings.Count - 1) * Environment.NewLine.Length;
        foreach (var finding in request.LatestFindings)
        {
            rawFindingsLength += finding.Length;
            if (rawFindingsLength > MaxDiagnosticBytes) break;
        }
        if (request.LatestFindings.Count == 0 || rawFindingsLength == 0)
        {
            body = body with { LatestFindings = "(none supplied)" };
            var noFindings = SerializeDiagnostic(body);
            if (noFindings.Length <= MaxDiagnosticBytes) return Encoding.UTF8.GetString(noFindings);
            body = body with { LatestFindings = "" };
        }
        else if (rawFindingsLength <= MaxDiagnosticBytes)
        {
            body = body with { LatestFindings = string.Join(Environment.NewLine, request.LatestFindings) };
            var complete = SerializeDiagnostic(body);
            if (complete.Length <= MaxDiagnosticBytes) return Encoding.UTF8.GetString(complete);
            body = body with { LatestFindings = "" };
        }

        var availableForFindings = MaxDiagnosticBytes - emptyLength;
        var marker = "\n[findings truncated]";
        var markerContentLength = JsonEncodedText.Encode(marker, DiagnosticJsonOptions.Encoder).EncodedUtf8Bytes.Length;
        if (markerContentLength > availableForFindings)
        {
            marker = "[truncated]";
            markerContentLength = JsonEncodedText.Encode(marker, DiagnosticJsonOptions.Encoder).EncodedUtf8Bytes.Length;
        }
        if (markerContentLength > availableForFindings)
            throw new AgentStageInputException("Required needs-human diagnostic fields leave no room for a truncation marker.");
        var available = availableForFindings - markerContentLength;

        var builder = new StringBuilder();
        var used = 0;
        var truncatedFindings = false;
        var hasFinding = false;
        var findings = request.LatestFindings.Count == 0 ? new[] { "(none supplied)" } : request.LatestFindings;
        foreach (var finding in findings)
        {
            if (hasFinding && !TryAppendFinding(builder, Environment.NewLine, ref used, available))
            {
                truncatedFindings = true;
                break;
            }
            if (!TryAppendFinding(builder, finding, ref used, available))
            {
                truncatedFindings = true;
                break;
            }
            hasFinding = true;
        }
        body = body with { LatestFindings = builder.ToString() + (truncatedFindings ? marker : string.Empty) };
        var truncated = SerializeDiagnostic(body);
        if (truncated.Length > MaxDiagnosticBytes)
            throw new AgentStageInputException("Could not fit the bounded needs-human diagnostic comment.");
        return Encoding.UTF8.GetString(truncated);
    }

    private static bool TryAppendFinding(StringBuilder builder, string value, ref int used, int available)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var encodedLength = JsonEncodedText.Encode(rune.ToString(), DiagnosticJsonOptions.Encoder).EncodedUtf8Bytes.Length;
            if (encodedLength > available - used) return false;
            builder.Append(rune.ToString());
            used += encodedLength;
        }
        return true;
    }

    private static byte[] SerializeDiagnostic(NeedsHumanDiagnostic diagnostic) =>
        JsonSerializer.SerializeToUtf8Bytes(diagnostic, DiagnosticJsonOptions);

    private static string CauseName(NeedsHumanCause cause) => cause switch
    {
        NeedsHumanCause.OperationalRetriesExhausted => "operational-retries-exhausted",
        NeedsHumanCause.DuplicateFailure => "duplicate-failure",
        NeedsHumanCause.RepairBudgetExhausted => "repair-budget-exhausted",
        NeedsHumanCause.EnvironmentBlocked => "environment-blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown needs-human cause")
    };

    private static string CreateSupplementalComment(string beadId, string failure) =>
        JsonSerializer.Serialize(new { schema = "nightweaver-needs-human-cleanup-failure-v1", bead_id = beadId, failure }, DiagnosticJsonOptions);

    private sealed record NeedsHumanDiagnostic(
        string Schema,
        string BeadId,
        string BeadType,
        string Stage,
        string Cause,
        string FailedRole,
        string Reason,
        string LatestFindings,
        string InvocationId,
        string LogDirectory,
        IReadOnlyList<string> LogFiles,
        string CleanupPreflight);
}
