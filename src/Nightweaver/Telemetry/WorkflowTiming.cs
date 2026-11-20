namespace Nightweaver.Telemetry;

internal enum WorkflowPhase { Preparation, Agent, Classification, Verification, Publication, Finalization, Other }
internal enum WorkflowOperation
{
    Preparation, LifecyclePrimary, LifecycleFallback, DuplicateFailure,
    PostExitVerification, OutcomeVerification, Publication, PlanningPublication, Finalization
}
internal enum WorkflowResult { Success, Unavailable, Invalid, Cancelled, Refused, Uncertain }

/// <summary>A reservation owns one monotonic clock and seven disjoint accumulators. Child
/// operation durations are diagnostic; they must never be added to the phase allocation.</summary>
internal sealed class WorkflowTiming : IDisposable
{
    private static readonly AsyncLocal<WorkflowTiming?> Current = new();
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Action<WorkflowPhase, double> recordPhase;
    private readonly Action<double> recordOccupied;
    private readonly double[] seconds = new double[7];
    private readonly long started;
    private long changed;
    private WorkflowPhase phase = WorkflowPhase.Other;
    private bool closed;
    private string? beadId;
    private string? invocationId;

    internal WorkflowPhase ActivePhase { get { lock (gate) return phase; } }

    internal WorkflowTiming(TimeProvider clock, Action<WorkflowPhase, double> recordPhase, Action<double> recordOccupied)
    {
        this.clock = clock;
        this.recordPhase = recordPhase;
        this.recordOccupied = recordOccupied;
        started = changed = clock.GetTimestamp();
    }

    internal static WorkflowTiming? Reserve(string pool, string beadId)
    {
        var runtime = OperationalTelemetry.Current;
        return runtime is null ? null : new(TimeProvider.System,
            (phase, seconds) => runtime.Telemetry.RecordPhase(pool, phase, seconds),
            seconds => runtime.Telemetry.RecordOccupied(pool, seconds)) { beadId = beadId };
    }

    internal IDisposable Enter()
    {
        var previous = Current.Value;
        Current.Value = this;
        return new Restore(() => Current.Value = previous);
    }

    internal static IDisposable? EnterPhase(WorkflowPhase next) => Current.Value?.Change(next);

    internal static void ObserveInvocation(string invocationId)
    {
        if (Current.Value is { } timing) timing.invocationId = invocationId;
    }

    private Restore Change(WorkflowPhase next)
    {
        lock (gate)
        {
            var previous = phase;
            Switch(next);
            return new Restore(() => { lock (gate) Switch(previous); });
        }
    }

    private void Switch(WorkflowPhase next)
    {
        if (closed) return;
        var now = clock.GetTimestamp();
        seconds[(int)phase] += clock.GetElapsedTime(changed, now).TotalSeconds;
        changed = now;
        phase = next;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            Switch(WorkflowPhase.Other);
            closed = true;
            for (var index = 0; index < seconds.Length; index++)
                recordPhase((WorkflowPhase)index, seconds[index]);
            recordOccupied(clock.GetElapsedTime(started, changed).TotalSeconds);
        }
    }

    internal static async Task<T> MeasureAsync<T>(WorkflowOperation operation, WorkflowPhase phase,
        Func<Task<T>> action, Func<T, WorkflowResult> result)
    {
        using var allocation = EnterPhase(phase);
        var runtime = OperationalTelemetry.Current;
        if (runtime is null) return await action().ConfigureAwait(false);
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
        var outcome = WorkflowResult.Uncertain;
        try
        {
            var value = await action().ConfigureAwait(false);
            outcome = result(value);
            return value;
        }
        catch (OperationCanceledException) { outcome = WorkflowResult.Cancelled; throw; }
        catch (AgentStageInputException) { outcome = WorkflowResult.Refused; throw; }
        finally
        {
            runtime?.Telemetry.RecordWorkflow(operation, outcome, clock.GetElapsedTime(started).TotalSeconds,
                runtime.RuntimeId, Current.Value?.beadId, Current.Value?.invocationId);
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? pending = restore;
        public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
    }
}
