using System.Threading.Channels;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Execution;

internal sealed record DeliveryPoolSnapshot(BeadsInventory Inventory, BeadsReadiness Readiness);

internal delegate Task<DeliveryPoolSnapshot> DeliveryPoolSnapshotReader(
    ValidatedTargetRepository repository,
    string recoveryActor,
    CancellationToken cancellationToken);

internal delegate Task<DeliverySlotOutcome> DeliveryPoolSlotRunner(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    string beadId,
    CancellationToken cancellationToken);

internal enum DeliveryPoolDiagnosticCode
{
    NotPlanBead,
    NotEligible,
    InvalidWorkflowState,
    AlreadyReserved,
    CapacityReached
}

internal sealed record DeliveryPoolDiagnostic(
    string BeadId,
    DeliveryPoolDiagnosticCode Code,
    string Message);

internal sealed record DeliveryPoolCompletion(
    string BeadId,
    DeliverySlotOutcome? Outcome,
    Exception? Exception,
    bool IsCanceled,
    bool LocalCapacityReleased)
{
    internal bool ClaimHandoffCompleted => Outcome?.ClaimHandoffCompleted ?? false;
    internal string? Diagnostic => Outcome?.Diagnostic ?? Exception?.Message;
}

internal sealed record DeliveryPoolAdmission(string BeadId, Task<DeliveryPoolCompletion> Completion);

internal sealed record DeliveryPoolReservation(string BeadId, Task<DeliveryPoolCompletion> Completion);

internal sealed record DeliveryPoolEvaluation(
    int CapacityLimit,
    IReadOnlyList<DeliveryPoolAdmission> Admitted,
    IReadOnlyList<DeliveryPoolDiagnostic> Diagnostics,
    IReadOnlyList<DeliveryPoolReservation> ActiveReservations);

/// <summary>
/// Bounded Delivery admission for one long-lived Project instance. The caller evaluates on startup,
/// tracker changes, and slot completions; this pool does not poll or own the runtime shutdown policy.
/// </summary>
/// <remarks>
/// EvaluateAsync serializes only fresh-read/admission work and never waits for a slot to finish.
/// The evaluation token controls only that pass. Each admitted slot receives the separate lifetime
/// token supplied at construction. CompletionReader is lossless and supports one consumer; callers
/// can also await an individual admission or snapshot the currently active completions with
/// WaitForActiveSlotsAsync.
/// </remarks>
internal sealed class DeliveryPoolCoordinator
{
    private sealed class ReservationState(string beadId)
    {
        internal string BeadId { get; } = beadId;
        internal TaskCompletionSource<DeliveryPoolCompletion> Completion { get; } =
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
    private readonly int capacityLimit;
    private readonly CancellationToken slotLifetimeToken;
    private readonly DeliveryPoolSnapshotReader readSnapshot;
    private readonly DeliveryPoolSlotRunner runSlot;
    private readonly object admissionSync = new();
    private Task admissionTail = Task.CompletedTask;
    private readonly object stateGate = new();
    private readonly Dictionary<string, ReservationState> reservations = new(StringComparer.Ordinal);
    private readonly Channel<DeliveryPoolCompletion> completions = Channel.CreateUnbounded<DeliveryPoolCompletion>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    internal DeliveryPoolCoordinator(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        IProcessRunner processRunner,
        DeliveryPoolSnapshotReader? readSnapshot = null,
        DeliveryPoolSlotRunner? runSlot = null,
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
        capacityLimit = project.Concurrency.MaxConcurrentDelivery;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityLimit);

        if (readSnapshot is null)
        {
            var reader = new BeadsReader(processRunner);
            this.readSnapshot = async (target, actor, cancellationToken) =>
            {
                var inventory = await reader.ReadSnapshotAsync(target, cancellationToken).ConfigureAwait(false);
                var readiness = await reader.ReadReadinessAsync(target, inventory, actor, cancellationToken).ConfigureAwait(false);
                return new DeliveryPoolSnapshot(inventory, readiness);
            };
        }
        else
        {
            this.readSnapshot = readSnapshot;
        }

        if (runSlot is null)
        {
            var slot = new DeliverySlotCoordinator(processRunner);
            this.runSlot = (host, config, target, beadId, cancellationToken) =>
                slot.RunAsync(host, config, target, beadId, cancellationToken);
        }
        else
        {
            this.runSlot = runSlot;
        }
    }

    internal int CapacityLimit => capacityLimit;

    internal ChannelReader<DeliveryPoolCompletion> CompletionReader => completions.Reader;

    internal IReadOnlyList<DeliveryPoolReservation> ActiveReservations
    {
        get
        {
            lock (stateGate)
                return reservations.Values
                    .OrderBy(reservation => reservation.BeadId, StringComparer.Ordinal)
                    .Select(reservation => new DeliveryPoolReservation(reservation.BeadId, reservation.Completion.Task))
                    .ToArray();
        }
    }

    internal async Task<DeliveryPoolEvaluation> EvaluateAsync(CancellationToken cancellationToken)
    {
        using var admission = await EnterAdmissionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Never carry a preceding evaluation's tracker state across this gate.
        var snapshot = await readSnapshot(repository, project.RecoveryActor, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<DeliveryPoolDiagnostic>();
        var eligible = SelectEligible(snapshot, diagnostics);
        var admitted = new List<DeliveryPoolAdmission>();

        for (var index = 0; index < eligible.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = eligible[index];
            var reservation = TryReserve(bead.Id, out var reservationDiagnostic);
            if (reservation is null)
            {
                diagnostics.Add(reservationDiagnostic!);
                if (reservationDiagnostic!.Code == DeliveryPoolDiagnosticCode.CapacityReached)
                {
                    for (var waiting = index + 1; waiting < eligible.Count; waiting++)
                    {
                        diagnostics.Add(new DeliveryPoolDiagnostic(eligible[waiting].Id,
                            DeliveryPoolDiagnosticCode.CapacityReached,
                            $"Delivery capacity ({capacityLimit}) is reserved; this candidate remains queued for a later evaluation."));
                    }
                    break;
                }
                continue;
            }

            admitted.Add(new DeliveryPoolAdmission(bead.Id, reservation.Completion.Task));
            // The reservation is visible before execution is queued, and the evaluation does not
            // inherit cancellation once a whole slot has been admitted.
            _ = Task.Run(() => RunReservedSlotAsync(reservation), CancellationToken.None);
        }

        return new DeliveryPoolEvaluation(capacityLimit, admitted, diagnostics.ToArray(), ActiveReservations);
    }

    /// <summary>Waits for the reservations active when called; later evaluations may admit new slots.</summary>
    internal async Task<IReadOnlyList<DeliveryPoolCompletion>> WaitForActiveSlotsAsync(CancellationToken cancellationToken)
    {
        Task<DeliveryPoolCompletion>[] active;
        lock (stateGate)
            active = reservations.Values.Select(reservation => reservation.Completion.Task).ToArray();

        if (active.Length == 0) return [];
        return await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<BeadSnapshot> SelectEligible(DeliveryPoolSnapshot snapshot, List<DeliveryPoolDiagnostic> diagnostics)
    {
        var eligible = new List<BeadSnapshot>();
        foreach (var bead in snapshot.Inventory.ById.Values)
        {
            if (bead.IssueType != "task")
            {
                diagnostics.Add(new DeliveryPoolDiagnostic(bead.Id, DeliveryPoolDiagnosticCode.NotPlanBead,
                    $"Delivery admits Plan Beads of type task; observed type '{bead.IssueType}'."));
                continue;
            }

            BeadWorkflowState state;
            try
            {
                state = BeadWorkflowState.Parse(bead);
            }
            catch (FormatException exception)
            {
                diagnostics.Add(new DeliveryPoolDiagnostic(bead.Id, DeliveryPoolDiagnosticCode.InvalidWorkflowState,
                    $"Candidate has invalid workflow state: {exception.Message}"));
                continue;
            }

            var refusal = DeliverySlotCoordinator.ValidateAdmission(bead, state, snapshot.Readiness);
            if (refusal is not null || !DeliverySlotCoordinator.TrySelectRole(state, out _))
            {
                diagnostics.Add(new DeliveryPoolDiagnostic(bead.Id, DeliveryPoolDiagnosticCode.NotEligible,
                    refusal ?? $"Workflow stage '{state.Stage ?? "(none)"}' is not valid input for a Delivery role."));
                continue;
            }

            eligible.Add(bead);
        }

        eligible.Sort(BeadCandidateOrder.Compare);
        return eligible;
    }

    private ReservationState? TryReserve(string beadId, out DeliveryPoolDiagnostic? diagnostic)
    {
        lock (stateGate)
        {
            if (reservations.ContainsKey(beadId))
            {
                diagnostic = new DeliveryPoolDiagnostic(beadId, DeliveryPoolDiagnosticCode.AlreadyReserved,
                    "This Plan Bead already has a reservation in this Delivery Pool instance.");
                return null;
            }

            if (reservations.Count >= capacityLimit)
            {
                diagnostic = new DeliveryPoolDiagnostic(beadId, DeliveryPoolDiagnosticCode.CapacityReached,
                    $"Delivery capacity ({capacityLimit}) is reserved; this candidate remains queued for a later evaluation.");
                return null;
            }

            var reservation = new ReservationState(beadId);
            reservations.Add(beadId, reservation);
            diagnostic = null;
            return reservation;
        }
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
        DeliverySlotOutcome? outcome = null;
        Exception? failure = null;
        var canceled = false;
        try
        {
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

        lock (stateGate)
            reservations.Remove(reservation.BeadId);

        var completion = new DeliveryPoolCompletion(reservation.BeadId, outcome, failure, canceled, LocalCapacityReleased: true);
        reservation.Completion.TrySetResult(completion);
        // The channel is intentionally unbounded and remains open for the pool lifetime. A failed
        // write would violate the single-runtime-consumer completion contract.
        if (!completions.Writer.TryWrite(completion))
            throw new InvalidOperationException("Delivery completion channel closed before slot completion publication.");
    }
}
