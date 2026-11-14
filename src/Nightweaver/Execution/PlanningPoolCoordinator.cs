using System.Threading.Channels;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;
using Nightweaver.Telemetry;

namespace Nightweaver.Execution;

internal sealed record PlanningPoolSnapshot(BeadsInventory Inventory, BeadsReadiness Readiness);

internal delegate Task<PlanningPoolSnapshot> PlanningPoolSnapshotReader(
    ValidatedTargetRepository repository,
    string recoveryActor,
    CancellationToken cancellationToken);

// Runs the complete admitted Planning workflow, including preparation and verified publication.
internal delegate Task<PlanningSlotOutcome> PlanningPoolSlotRunner(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    string beadId,
    CancellationToken cancellationToken);

internal enum PlanningPoolDiagnosticCode
{
    NotProductBead,
    NotEligible,
    InvalidWorkflowState,
    AlreadyReserved,
    CapacityReached,
    Backpressure,
    Stopping
}

internal sealed record PlanningPoolDiagnostic(
    string BeadId,
    PlanningPoolDiagnosticCode Code,
    string Message);

internal sealed record PlanningPoolCompletion(
    string BeadId,
    PlanningSlotOutcome? Outcome,
    Exception? Exception,
    bool IsCanceled,
    bool LocalCapacityReleased,
    TaskCompletionSource<bool>? AccountingBarrier = null)
{
    internal bool ClaimHandoffCompleted => Outcome?.ClaimHandoffCompleted ?? false;
    internal string? Diagnostic => Outcome?.Diagnostic ?? Exception?.Message;
    internal bool IsAccountingBarrier => AccountingBarrier is not null;
}

internal sealed record PlanningPoolAdmission(string BeadId, Task<PlanningPoolCompletion> Completion);

internal sealed record PlanningPoolReservation(string BeadId, Task<PlanningPoolCompletion> Completion);

internal sealed record PlanningPoolEvaluation(
    int CapacityLimit,
    IReadOnlyList<PlanningPoolAdmission> Admitted,
    IReadOnlyList<PlanningPoolDiagnostic> Diagnostics,
    IReadOnlyList<PlanningPoolReservation> ActiveReservations);

/// <summary>
/// Bounded Planning admission for one long-lived Project instance. The caller evaluates on startup,
/// timer ticks, and slot completions; this pool does not poll or own runtime shutdown policy.
/// </summary>
/// <remarks>
/// EvaluateAsync serializes only fresh-read/admission work. Reservations consume capacity before
/// preparation starts; a full pool returns immediately and later triggers reread eligibility.
/// The evaluation token controls only that pass. Each admitted slot receives the separate lifetime
/// token supplied at construction. CompletionReader is lossless and supports one consumer; callers
/// can also await an individual admission or snapshot the currently active completions with
/// WaitForActiveSlotsAsync.
/// </remarks>
internal sealed class PlanningPoolCoordinator
{
    private sealed class ReservationState(string beadId, string slotId)
    {
        internal string BeadId { get; } = beadId;
        internal string SlotId { get; } = slotId;
        internal IDisposable? EnvironmentLease { get; set; }
        internal TaskCompletionSource<PlanningPoolCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AdmissionReleaser(TaskCompletionSource<bool> turn) : IDisposable
    {
        private TaskCompletionSource<bool>? pendingTurn = turn;

        public void Dispose() => Interlocked.Exchange(ref pendingTurn, null)?.TrySetResult(true);
    }

    private readonly Action<string>? onPublished;
    private readonly HostPaths paths;
    private readonly ProjectConfig project;
    private readonly ValidatedTargetRepository repository;
    private readonly int capacityLimit;
    private readonly CancellationToken slotLifetimeToken;
    private readonly ContinuousStopGate? stopGate;
    private readonly EnvironmentReservations? environments;
    private readonly FactoryTelemetryPublisher? telemetry;
    private readonly PlanningPoolSnapshotReader readSnapshot;
    private readonly PlanningPoolSlotRunner runSlot;
    private readonly object admissionSync = new();
    private Task admissionTail = Task.CompletedTask;
    private readonly object stateGate = new();
    private readonly Dictionary<string, ReservationState> reservations = new(StringComparer.Ordinal);
    private readonly Channel<PlanningPoolCompletion> completions = Channel.CreateUnbounded<PlanningPoolCompletion>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    internal PlanningPoolCoordinator(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        IProcessRunner processRunner,
        PlanningPoolSnapshotReader? readSnapshot = null,
        PlanningPoolSlotRunner? runSlot = null,
        ContinuousStopGate? stopGate = null,
        FactoryTelemetryPublisher? telemetry = null,
        EnvironmentReservations? environments = null,
        Action<string>? onPublished = null,
        CancellationToken slotLifetimeToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(processRunner);

        this.onPublished = onPublished;
        this.paths = paths;
        this.project = project;
        this.repository = repository;
        this.slotLifetimeToken = slotLifetimeToken;
        this.stopGate = stopGate;
        this.environments = environments;
        this.telemetry = telemetry;
        capacityLimit = project.Concurrency.MaxConcurrentPlanning;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityLimit);

        if (readSnapshot is null)
        {
            var reader = new BeadsReader(processRunner);
            this.readSnapshot = async (target, actor, cancellationToken) =>
            {
                var snapshot = await reader.ReadSnapshotWithReadinessAsync(target, actor, cancellationToken).ConfigureAwait(false);
                return new PlanningPoolSnapshot(snapshot.Inventory, snapshot.Readiness);
            };
        }
        else
        {
            this.readSnapshot = readSnapshot;
        }

        if (runSlot is null)
        {
            var slot = new PlanningSlotCoordinator(processRunner);
            this.runSlot = (host, config, target, beadId, cancellationToken) =>
                slot.RunAsync(host, config, target, beadId, cancellationToken);
        }
        else
        {
            this.runSlot = runSlot;
        }
    }

    internal int CapacityLimit => capacityLimit;

    internal ChannelReader<PlanningPoolCompletion> CompletionReader => completions.Reader;

    internal IReadOnlyList<PlanningPoolReservation> ActiveReservations
    {
        get
        {
            lock (stateGate)
                return reservations.Values
                    .OrderBy(reservation => reservation.BeadId, StringComparer.Ordinal)
                    .Select(reservation => new PlanningPoolReservation(reservation.BeadId, reservation.Completion.Task))
                    .ToArray();
        }
    }

    internal async Task<PlanningPoolEvaluation> EvaluateAsync(CancellationToken cancellationToken)
    {
        using var admission = await EnterAdmissionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Never carry a preceding evaluation's tracker state across this gate.
        var snapshot = await readSnapshot(repository, project.RecoveryActor, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<PlanningPoolDiagnostic>();
        var eligible = SelectEligible(snapshot, diagnostics);
        var admitted = new List<PlanningPoolAdmission>();
        for (var index = 0; index < eligible.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = eligible[index];
            var reservation = TryReserve(bead.Id, out var reservationDiagnostic);
            if (reservation is null)
            {
                diagnostics.Add(reservationDiagnostic!);
                if (reservationDiagnostic!.Code == PlanningPoolDiagnosticCode.CapacityReached)
                {
                    for (var waiting = index + 1; waiting < eligible.Count; waiting++)
                        diagnostics.Add(new PlanningPoolDiagnostic(eligible[waiting].Id,
                            PlanningPoolDiagnosticCode.CapacityReached,
                            $"Planning capacity ({capacityLimit}) is reserved; this candidate remains queued for a later evaluation."));
                    break;
                }
                continue;
            }

            admitted.Add(new PlanningPoolAdmission(bead.Id, reservation.Completion.Task));
            // The reservation is visible before execution is queued, and the evaluation does not
            // inherit cancellation once a whole slot has been admitted.
            _ = Task.Run(() => RunReservedSlotAsync(reservation), CancellationToken.None);
        }

        return new PlanningPoolEvaluation(capacityLimit, admitted, diagnostics.ToArray(), ActiveReservations);
    }

    /// <summary>Waits for the reservations active when called; later evaluations may admit new slots.</summary>
    internal async Task<IReadOnlyList<PlanningPoolCompletion>> WaitForActiveSlotsAsync(CancellationToken cancellationToken)
    {
        Task<PlanningPoolCompletion>[] active;
        lock (stateGate)
            active = reservations.Values.Select(reservation => reservation.Completion.Task).ToArray();

        if (active.Length == 0) return [];
        return await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until the single completion reader has processed every completion published before this call.</summary>
    internal async Task WaitForCompletionAccountingAsync(CancellationToken cancellationToken)
    {
        var accounted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new PlanningPoolCompletion(string.Empty, null, null, false,
            LocalCapacityReleased: true, AccountingBarrier: accounted);
        if (!completions.Writer.TryWrite(barrier))
            throw new InvalidOperationException("Planning completion channel closed before its drain accounting barrier was published.");
        await accounted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private List<BeadSnapshot> SelectEligible(PlanningPoolSnapshot snapshot, List<PlanningPoolDiagnostic> diagnostics)
    {
        var eligible = new List<BeadSnapshot>();
        var activeTasks = PlanningPoolPlanner.CountActiveTasks(snapshot.Inventory);
        foreach (var bead in snapshot.Inventory.ById.Values)
        {
            if (bead.IssueType is not ("feature" or "bug"))
            {
                diagnostics.Add(new PlanningPoolDiagnostic(bead.Id, PlanningPoolDiagnosticCode.NotProductBead,
                    $"Planning admits Product Beads of type feature or bug; observed type '{bead.IssueType}'."));
                continue;
            }

            BeadWorkflowState state;
            try
            {
                state = BeadWorkflowState.Parse(bead);
            }
            catch (FormatException exception)
            {
                diagnostics.Add(new PlanningPoolDiagnostic(bead.Id, PlanningPoolDiagnosticCode.InvalidWorkflowState,
                    $"Candidate has invalid workflow state: {exception.Message}"));
                continue;
            }

            var refusal = PlanningSlotCoordinator.ValidateAdmission(bead, state, snapshot.Readiness);
            if (refusal is not null)
            {
                diagnostics.Add(new PlanningPoolDiagnostic(bead.Id, PlanningPoolDiagnosticCode.NotEligible,
                    refusal ?? $"Workflow stage '{state.Stage ?? "(none)"}' is not valid input for a Planning role."));
                continue;
            }

            eligible.Add(bead);
        }

        if (activeTasks >= project.Concurrency.MaxActivePlanBeads)
        {
            diagnostics.AddRange(eligible.Select(bead => new PlanningPoolDiagnostic(bead.Id,
                PlanningPoolDiagnosticCode.Backpressure,
                $"{activeTasks} active Plan Beads meet the configured threshold ({project.Concurrency.MaxActivePlanBeads}); new Planning waits.")));
            return [];
        }
        eligible.Sort(BeadCandidateOrder.Compare);
        return eligible;
    }

    private ReservationState? TryReserve(string beadId, out PlanningPoolDiagnostic? diagnostic)
    {
        ReservationState? reservation = null;
        PlanningPoolDiagnostic? refusal = null;
        bool TryReserveLocal()
        {
            lock (stateGate)
            {
                if (reservations.ContainsKey(beadId))
                {
                    refusal = new PlanningPoolDiagnostic(beadId, PlanningPoolDiagnosticCode.AlreadyReserved,
                        "This Product Bead already has a reservation in this Planning Pool instance.");
                    return false;
                }

                if (reservations.Count >= capacityLimit)
                {
                    refusal = new PlanningPoolDiagnostic(beadId, PlanningPoolDiagnosticCode.CapacityReached,
                        $"Planning capacity ({capacityLimit}) is reserved; this candidate remains queued for a later evaluation.");
                    return false;
                }

                var environmentLease = environments?.TryAcquire(beadId);
                if (environments is not null && environmentLease is null)
                {
                    refusal = new PlanningPoolDiagnostic(beadId, PlanningPoolDiagnosticCode.AlreadyReserved,
                        "The environment is reserved by another pool or completed-environment cleanup.");
                    return false;
                }
                var slotId = telemetry?.SlotForReservation("planning", beadId)
                    ?? $"planning-{Enumerable.Range(1, capacityLimit).First(index => reservations.Values.All(active => active.SlotId != $"planning-{index}"))}";
                reservation = new ReservationState(beadId, slotId) { EnvironmentLease = environmentLease };
                reservations.Add(beadId, reservation);
                telemetry?.Reserve("planning", slotId, beadId);
                return true;
            }
        }

        var gateOpen = true;
        if (stopGate is null)
            _ = TryReserveLocal();
        else
            gateOpen = stopGate.TryReserve(TryReserveLocal, out _);
        if (!gateOpen)
        {
            diagnostic = new PlanningPoolDiagnostic(beadId, PlanningPoolDiagnosticCode.Stopping,
                "The continuous runtime is stopping; this candidate remains unclaimed.");
            return null;
        }

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

    private async Task RunReservedSlotAsync(ReservationState reservation)
    {
        PlanningSlotOutcome? outcome = null;
        Exception? failure = null;
        var canceled = false;
        using var launchContext = ContinuousAgentLaunchContext.Enter(stopGate, telemetry, "planning",
            reservation.SlotId, reservation.BeadId);
        telemetry?.UpdateSlot("planning", reservation.SlotId, "preparing", reservation.BeadId);
        try
        {
            if (stopGate?.IsStopRequested == true)
                outcome = new PlanningSlotOutcome(PlanningSlotDisposition.Refused, null, false,
                    "The continuous runtime stopped before this reserved slot began claim acquisition.");
            else
                outcome = await runSlot(paths, project, repository, reservation.BeadId, slotLifetimeToken)
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

        try
        {
            if (outcome?.Disposition == PlanningSlotDisposition.Published) onPublished?.Invoke(reservation.BeadId);
        }
        catch (Exception exception) { failure = exception; }

        var completion = new PlanningPoolCompletion(reservation.BeadId, outcome, failure, canceled, LocalCapacityReleased: true);
        lock (stateGate)
        {
            telemetry?.Release("planning", reservation.SlotId, reservation.BeadId);
            reservations.Remove(reservation.BeadId);
            reservation.EnvironmentLease?.Dispose();
            // Capacity is released before notification. Keep that transition and publication under
            // one lock so a drain cannot observe an empty reservation set before this result exists.
            // The channel is unbounded and remains open for the pool lifetime.
            if (!completions.Writer.TryWrite(completion))
                throw new InvalidOperationException("Planning completion channel closed before slot completion publication.");
            reservation.Completion.TrySetResult(completion);
        }
    }
}
