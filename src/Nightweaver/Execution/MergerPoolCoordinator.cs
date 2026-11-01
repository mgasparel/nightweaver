using System.Threading.Channels;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Execution;

internal sealed record MergerPoolSnapshot(BeadsInventory Inventory, BeadsReadiness Readiness);

internal delegate Task<MergerPoolSnapshot> MergerPoolSnapshotReader(
    ValidatedTargetRepository repository,
    string recoveryActor,
    CancellationToken cancellationToken);

internal delegate Task<MergerSlotOutcome> MergerPoolSlotRunner(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    string beadId,
    Action agentAttemptStarted,
    CancellationToken cancellationToken);

internal enum MergerPoolDiagnosticCode
{
    NotPlanBead,
    NotEligible,
    InvalidWorkflowState,
    AlreadyReserved,
    CapacityReached,
    SlotRefused,
    SlotEnded
}

internal sealed record MergerPoolDiagnostic(string BeadId, MergerPoolDiagnosticCode Code, string Message);

internal sealed record MergerPoolCompletion(
    string BeadId,
    MergerSlotOutcome? Outcome,
    Exception? Exception,
    bool IsCanceled,
    bool LocalCapacityReleased)
{
    internal bool MergeCompleted => Outcome?.MergeCompleted ?? false;
    internal string? Diagnostic => Outcome?.Diagnostic ?? Exception?.Message;
}

internal sealed record MergerPoolAdmission(string BeadId, Task<MergerPoolCompletion> Completion);
internal sealed record MergerPoolReservation(string BeadId, Task<MergerPoolCompletion> Completion);
internal sealed record MergerPoolEvaluation(
    int CapacityLimit,
    IReadOnlyList<MergerPoolAdmission> Admitted,
    IReadOnlyList<MergerPoolDiagnostic> Diagnostics,
    IReadOnlyList<MergerPoolReservation> ActiveReservations);

/// <summary>Serializes merge work for one Project while keeping its reservation separate from Delivery.</summary>
internal sealed class MergerPoolCoordinator
{
    private sealed class ReservationState(string beadId)
    {
        internal string BeadId { get; } = beadId;
        internal TaskCompletionSource<MergerPoolCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<AdmissionResolution> AdmissionResolved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private abstract record AdmissionResolution
    {
        internal sealed record AgentAttemptStarted : AdmissionResolution;
        internal sealed record CompletedBeforeAgentAttempt(MergerPoolCompletion Completion) : AdmissionResolution;
    }

    private sealed record PendingAdmissionWait(ReservationState[] Reservations, Task CapacityChanged);

    private sealed class AdmissionReleaser(TaskCompletionSource<bool> turn) : IDisposable
    {
        private TaskCompletionSource<bool>? pendingTurn = turn;
        public void Dispose() => Interlocked.Exchange(ref pendingTurn, null)?.TrySetResult(true);
    }

    private readonly HostPaths paths;
    private readonly ProjectConfig project;
    private readonly ValidatedTargetRepository repository;
    private readonly CancellationToken slotLifetimeToken;
    private readonly MergerPoolSnapshotReader readSnapshot;
    private readonly MergerPoolSlotRunner runSlot;
    private readonly object admissionSync = new();
    private Task admissionTail = Task.CompletedTask;
    private readonly object stateGate = new();
    private readonly Dictionary<string, ReservationState> reservations = new(StringComparer.Ordinal);
    private TaskCompletionSource<bool> capacityChanged = NewSignal();
    private readonly Channel<MergerPoolCompletion> completions = Channel.CreateUnbounded<MergerPoolCompletion>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly int capacityLimit = ConcurrencyConfig.MergerSlots;

    internal MergerPoolCoordinator(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        IProcessRunner processRunner,
        MergerPoolSnapshotReader? readSnapshot = null,
        MergerPoolSlotRunner? runSlot = null,
        CancellationToken slotLifetimeToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(processRunner);
        this.paths = paths;
        this.project = project;
        this.repository = repository;
        this.slotLifetimeToken = slotLifetimeToken;
        if (readSnapshot is null)
        {
            var reader = new BeadsReader(processRunner);
            this.readSnapshot = async (target, actor, cancellationToken) =>
            {
                var snapshot = await reader.ReadSnapshotWithReadinessAsync(target, actor, cancellationToken)
                    .ConfigureAwait(false);
                return new MergerPoolSnapshot(snapshot.Inventory, snapshot.Readiness);
            };
        }
        else
        {
            this.readSnapshot = readSnapshot;
        }

        if (runSlot is null)
        {
            var slot = new MergerSlotCoordinator(processRunner);
            this.runSlot = (host, config, target, beadId, agentAttemptStarted, cancellationToken) =>
                slot.RunAsync(host, config, target, beadId, agentAttemptStarted, cancellationToken);
        }
        else
        {
            this.runSlot = runSlot;
        }
    }

    internal int CapacityLimit => capacityLimit;
    internal ChannelReader<MergerPoolCompletion> CompletionReader => completions.Reader;

    internal IReadOnlyList<MergerPoolReservation> ActiveReservations
    {
        get
        {
            lock (stateGate)
                return reservations.Values.OrderBy(reservation => reservation.BeadId, StringComparer.Ordinal)
                    .Select(reservation => new MergerPoolReservation(reservation.BeadId, reservation.Completion.Task))
                    .ToArray();
        }
    }

    internal async Task<MergerPoolEvaluation> EvaluateAsync(CancellationToken cancellationToken)
    {
        using var admission = await EnterAdmissionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await readSnapshot(repository, project.RecoveryActor, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<MergerPoolDiagnostic>();
        var eligible = SelectEligible(snapshot, diagnostics);
        var admitted = new List<MergerPoolAdmission>();
        var passReservations = new List<ReservationState>();

        for (var index = 0; index < eligible.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = eligible[index];
            ReservationState? reservation;
            lock (stateGate)
            {
                if (reservations.ContainsKey(bead.Id))
                {
                    diagnostics.Add(new MergerPoolDiagnostic(bead.Id, MergerPoolDiagnosticCode.AlreadyReserved,
                        "This Plan Bead already has a reservation in this Project's Merger slot."));
                    index++;
                    continue;
                }
                if (reservations.Count >= CapacityLimit)
                {
                    reservation = null;
                }
                else
                {
                    reservation = new ReservationState(bead.Id);
                    reservations.Add(bead.Id, reservation);
                }
            }

            if (reservation is null)
            {
                var pendingAdmissionWait = GetPendingAdmissionsIfCapacityIsFull();
                if (pendingAdmissionWait is null)
                    continue;
                if (pendingAdmissionWait.Reservations.Length == 0)
                {
                    for (var waiting = index; waiting < eligible.Count; waiting++)
                    {
                        diagnostics.Add(new MergerPoolDiagnostic(eligible[waiting].Id,
                            MergerPoolDiagnosticCode.CapacityReached,
                            "The Project's single Merger slot is reserved; this candidate remains queued for a later evaluation."));
                    }
                    break;
                }

                var admissionAndCapacitySignals = pendingAdmissionWait.Reservations
                    .Select(pending => (Task)pending.AdmissionResolved.Task)
                    .Append(pendingAdmissionWait.CapacityChanged);
                var signaled = await Task.WhenAny(admissionAndCapacitySignals)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (signaled != pendingAdmissionWait.CapacityChanged)
                {
                    var resolution = await ((Task<AdmissionResolution>)signaled).ConfigureAwait(false);
                    if (resolution is AdmissionResolution.CompletedBeforeAgentAttempt endedBeforeLaunch)
                        diagnostics.Add(CreatePreLaunchDiagnostic(endedBeforeLaunch.Completion));
                }
                continue;
            }

            admitted.Add(new MergerPoolAdmission(bead.Id, reservation.Completion.Task));
            passReservations.Add(reservation);
            // The reservation is visible before execution starts. Once admitted, the pass token
            // no longer controls the slot; project shutdown is represented by slotLifetimeToken.
            _ = Task.Run(() => RunReservedSlotAsync(reservation), CancellationToken.None);
            index++;
        }

        foreach (var reservation in passReservations)
        {
            if (!reservation.AdmissionResolved.Task.IsCompletedSuccessfully
                || reservation.AdmissionResolved.Task.Result is not AdmissionResolution.CompletedBeforeAgentAttempt endedBeforeLaunch
                || diagnostics.Any(diagnostic => diagnostic.BeadId == endedBeforeLaunch.Completion.BeadId
                    && diagnostic.Code is MergerPoolDiagnosticCode.SlotRefused or MergerPoolDiagnosticCode.SlotEnded))
                continue;

            diagnostics.Add(CreatePreLaunchDiagnostic(endedBeforeLaunch.Completion));
        }

        return new MergerPoolEvaluation(CapacityLimit, admitted, diagnostics.ToArray(), ActiveReservations);
    }

    internal async Task<IReadOnlyList<MergerPoolCompletion>> WaitForActiveSlotsAsync(CancellationToken cancellationToken)
    {
        Task<MergerPoolCompletion>[] active;
        lock (stateGate)
            active = reservations.Values.Select(reservation => reservation.Completion.Task).ToArray();
        if (active.Length == 0) return [];
        return await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<BeadSnapshot> SelectEligible(MergerPoolSnapshot snapshot,
        List<MergerPoolDiagnostic> diagnostics)
    {
        var eligible = new List<BeadSnapshot>();
        foreach (var bead in snapshot.Inventory.ById.Values)
        {
            if (bead.IssueType != "task")
            {
                if (StageLabelParser.Parse(bead.Labels).Contains("stage:merge", StringComparer.Ordinal))
                    diagnostics.Add(new MergerPoolDiagnostic(bead.Id, MergerPoolDiagnosticCode.NotPlanBead,
                        $"Merger admits Plan Beads of type task; observed type '{bead.IssueType}'."));
                continue;
            }

            BeadWorkflowState state;
            try { state = BeadWorkflowState.Parse(bead); }
            catch (FormatException exception)
            {
                if (StageLabelParser.Parse(bead.Labels).Contains("stage:merge", StringComparer.Ordinal))
                    diagnostics.Add(new MergerPoolDiagnostic(bead.Id, MergerPoolDiagnosticCode.InvalidWorkflowState,
                        $"Candidate has invalid workflow state: {exception.Message}"));
                continue;
            }

            var refusal = MergerSlotCoordinator.ValidateAdmission(bead, state, snapshot.Readiness);
            if (refusal is not null)
            {
                if (state.Stage == "stage:merge")
                    diagnostics.Add(new MergerPoolDiagnostic(bead.Id, MergerPoolDiagnosticCode.NotEligible, refusal));
                continue;
            }
            eligible.Add(bead);
        }

        eligible.Sort(BeadCandidateOrder.Compare);
        return eligible;
    }

    private async Task RunReservedSlotAsync(ReservationState reservation)
    {
        MergerSlotOutcome? outcome = null;
        Exception? failure = null;
        var canceled = false;
        try
        {
            outcome = await runSlot(paths, project, repository, reservation.BeadId,
                () => reservation.AdmissionResolved.TrySetResult(new AdmissionResolution.AgentAttemptStarted()), slotLifetimeToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
            canceled = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        TaskCompletionSource<bool> capacityReleased;
        lock (stateGate)
        {
            reservations.Remove(reservation.BeadId);
            capacityReleased = capacityChanged;
            capacityChanged = NewSignal();
        }
        var completion = new MergerPoolCompletion(reservation.BeadId, outcome, failure, canceled, LocalCapacityReleased: true);
        reservation.Completion.TrySetResult(completion);
        reservation.AdmissionResolved.TrySetResult(new AdmissionResolution.CompletedBeforeAgentAttempt(completion));
        capacityReleased.TrySetResult(true);
        if (!completions.Writer.TryWrite(completion))
            throw new InvalidOperationException("Merger completion channel closed before slot completion publication.");
    }

    private PendingAdmissionWait? GetPendingAdmissionsIfCapacityIsFull()
    {
        lock (stateGate)
        {
            if (reservations.Count < CapacityLimit)
                return null;

            return new PendingAdmissionWait(reservations.Values
                .Where(reservation => !reservation.AdmissionResolved.Task.IsCompleted)
                .ToArray(), capacityChanged.Task);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static MergerPoolDiagnostic CreatePreLaunchDiagnostic(MergerPoolCompletion completion)
    {
        var refused = completion.Outcome?.Disposition == MergerSlotDisposition.Refused;
        var message = completion.Diagnostic
            ?? (completion.IsCanceled
                ? "Merger slot was canceled before an Agent attempt started."
                : $"Merger slot ended before an Agent attempt started{(completion.Outcome is null ? "." : $" with disposition '{completion.Outcome.Disposition}'.")}");
        return new MergerPoolDiagnostic(completion.BeadId,
            refused ? MergerPoolDiagnosticCode.SlotRefused : MergerPoolDiagnosticCode.SlotEnded,
            message);
    }

    private async Task<IDisposable> EnterAdmissionAsync(CancellationToken cancellationToken)
    {
        var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task precedingTurn;
        lock (admissionSync)
        {
            precedingTurn = admissionTail;
            admissionTail = turn.Task;
        }

        try
        {
            await precedingTurn.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AdmissionReleaser(turn);
        }
        catch
        {
            _ = ReleaseAfterAsync(precedingTurn, turn);
            throw;
        }
    }

    private static async Task ReleaseAfterAsync(Task precedingTurn, TaskCompletionSource<bool> turn)
    {
        await precedingTurn.ConfigureAwait(false);
        turn.TrySetResult(true);
    }
}
