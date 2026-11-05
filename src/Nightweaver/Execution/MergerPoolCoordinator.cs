using System.Threading.Channels;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;
using Nightweaver.Telemetry;

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
    SlotEnded,
    Stopping
}

internal sealed record MergerPoolDiagnostic(string BeadId, MergerPoolDiagnosticCode Code, string Message);

internal sealed record MergerPoolCompletion(
    string BeadId,
    MergerSlotOutcome? Outcome,
    Exception? Exception,
    bool IsCanceled,
    bool LocalCapacityReleased,
    TaskCompletionSource<bool>? AccountingBarrier = null)
{
    internal bool MergeCompleted => Outcome?.MergeCompleted ?? false;
    internal string? Diagnostic => Outcome?.Diagnostic ?? Exception?.Message;
    internal bool IsAccountingBarrier => AccountingBarrier is not null;
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
    private sealed class ReservationState(string beadId, string slotId)
    {
        internal string BeadId { get; } = beadId;
        internal string SlotId { get; } = slotId;
        internal TaskCompletionSource<MergerPoolCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AdmissionReleaser(TaskCompletionSource<bool> turn) : IDisposable
    {
        private TaskCompletionSource<bool>? pendingTurn = turn;
        public void Dispose() => Interlocked.Exchange(ref pendingTurn, null)?.TrySetResult(true);
    }

    private readonly HostPaths paths;
    private readonly ProjectConfig project;
    private readonly ValidatedTargetRepository repository;
    private readonly CancellationToken slotLifetimeToken;
    private readonly ContinuousStopGate? stopGate;
    private readonly FactoryTelemetryPublisher? telemetry;
    private readonly MergerPoolSnapshotReader readSnapshot;
    private readonly MergerPoolSlotRunner runSlot;
    private readonly object admissionSync = new();
    private Task admissionTail = Task.CompletedTask;
    private readonly object stateGate = new();
    private readonly Dictionary<string, ReservationState> reservations = new(StringComparer.Ordinal);
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
        ContinuousStopGate? stopGate = null,
        FactoryTelemetryPublisher? telemetry = null,
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
        this.stopGate = stopGate;
        this.telemetry = telemetry;
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

        for (var index = 0; index < eligible.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = eligible[index];
            var reservation = TryReserve(bead.Id, out var reservationDiagnostic);
            if (reservation is null)
            {
                diagnostics.Add(reservationDiagnostic!);
                if (reservationDiagnostic!.Code is MergerPoolDiagnosticCode.CapacityReached or MergerPoolDiagnosticCode.Stopping)
                {
                    var code = reservationDiagnostic.Code;
                    var message = code == MergerPoolDiagnosticCode.CapacityReached
                        ? $"The Project's single Merger slot is reserved; this candidate remains queued for a later evaluation."
                        : "The continuous runtime is stopping; this candidate remains unclaimed.";
                    for (var waiting = index + 1; waiting < eligible.Count; waiting++)
                        diagnostics.Add(new MergerPoolDiagnostic(eligible[waiting].Id, code, message));
                    break;
                }
                continue;
            }

            admitted.Add(new MergerPoolAdmission(bead.Id, reservation.Completion.Task));
            // The reservation is visible before execution starts. Preparation and execution run
            // independently so a slow Merger slot never holds the shared discovery pass.
            _ = Task.Run(() => RunReservedSlotAsync(reservation), CancellationToken.None);
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

    /// <summary>Waits until the completion reader has processed every slot result published before this call.</summary>
    internal async Task WaitForCompletionAccountingAsync(CancellationToken cancellationToken)
    {
        var accounted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new MergerPoolCompletion(string.Empty, null, null, false,
            LocalCapacityReleased: true, AccountingBarrier: accounted);
        if (!completions.Writer.TryWrite(barrier))
            throw new InvalidOperationException("Merger completion channel closed before its drain accounting barrier was published.");
        await accounted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        using var launchContext = ContinuousAgentLaunchContext.Enter(stopGate, telemetry, "merger",
            reservation.SlotId, reservation.BeadId);
        telemetry?.UpdateSlot("merger", reservation.SlotId, "preparing", reservation.BeadId);
        try
        {
            outcome = stopGate?.IsStopRequested == true
                ? new MergerSlotOutcome(MergerSlotDisposition.Refused, null, null, false,
                    "The continuous runtime stopped before this reserved Merger slot began claim acquisition.")
                : await runSlot(paths, project, repository, reservation.BeadId, static () => { }, slotLifetimeToken)
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

        var completion = new MergerPoolCompletion(reservation.BeadId, outcome, failure, canceled, LocalCapacityReleased: true);
        lock (stateGate)
        {
            telemetry?.Release("merger", reservation.SlotId, reservation.BeadId);
            reservations.Remove(reservation.BeadId);
            // Publish the result in the same critical section that releases capacity. A drain
            // which observes no reservations can then safely place its accounting barrier next.
            if (!completions.Writer.TryWrite(completion))
                throw new InvalidOperationException("Merger completion channel closed before slot completion publication.");
            reservation.Completion.TrySetResult(completion);
        }
    }

    private ReservationState? TryReserve(string beadId, out MergerPoolDiagnostic? diagnostic)
    {
        ReservationState? reservation = null;
        MergerPoolDiagnostic? refusal = null;
        bool TryReserveLocal()
        {
            lock (stateGate)
            {
                if (reservations.ContainsKey(beadId))
                {
                    refusal = new MergerPoolDiagnostic(beadId, MergerPoolDiagnosticCode.AlreadyReserved,
                        "This task Bead already has a reservation in this Project's singleton Merger slot.");
                    return false;
                }
                if (reservations.Count >= CapacityLimit)
                {
                    refusal = new MergerPoolDiagnostic(beadId, MergerPoolDiagnosticCode.CapacityReached,
                        "The Project's singleton Merger slot is reserved; this candidate remains queued for a later evaluation.");
                    return false;
                }

                var slotId = telemetry?.SlotForReservation("merger", beadId) ?? "merger-1";
                reservation = new ReservationState(beadId, slotId);
                reservations.Add(beadId, reservation);
                telemetry?.Reserve("merger", slotId, beadId);
                return true;
            }
        }

        if (stopGate is null)
            _ = TryReserveLocal();
        else if (!stopGate.TryReserve(TryReserveLocal, out _))
            refusal = new MergerPoolDiagnostic(beadId, MergerPoolDiagnosticCode.Stopping,
                "The continuous runtime is stopping; this candidate remains unclaimed.");
        diagnostic = refusal;
        return reservation;
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
