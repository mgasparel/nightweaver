namespace Nightweaver.Execution;

/// <summary>Serializes first-stop with new slot reservations and native Agent launches.</summary>
internal sealed class ContinuousStopGate
{
    private readonly object sync = new();
    private bool stopRequested;

    internal bool IsStopRequested
    {
        get { lock (sync) return stopRequested; }
    }

    internal void RequestStop()
    {
        lock (sync) stopRequested = true;
    }

    internal bool TryReserve<T>(Func<T> reserve, out T result)
    {
        ArgumentNullException.ThrowIfNull(reserve);
        lock (sync)
        {
            if (stopRequested)
            {
                result = default!;
                return false;
            }

            result = reserve();
            return true;
        }
    }

    internal bool TryStart<T>(Func<T> start, out T result)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (sync)
        {
            if (stopRequested)
            {
                result = default!;
                return false;
            }

            result = start();
            return true;
        }
    }

    internal bool TryLaunch(ContinuousAgentLaunchContext slot, AgentLaunchPurpose purpose, Action launch)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(launch);
        lock (sync)
        {
            if (stopRequested && !(purpose == AgentLaunchPurpose.OutcomeClassifier && slot.HasLaunchedAgent))
                return false;

            launch();
            if (purpose == AgentLaunchPurpose.WorkingAgent)
                slot.MarkAgentLaunched();
            return true;
        }
    }
}

internal enum AgentLaunchPurpose
{
    WorkingAgent,
    OutcomeClassifier
}

internal sealed class AgentLaunchDeniedException()
    : NightweaverException("The continuous runtime entered graceful shutdown before this Agent could launch.");

/// <summary>Ambient per-slot launch authorization, flowed through async execution to the native boundary.</summary>
internal sealed class ContinuousAgentLaunchContext : IDisposable
{
    private static readonly AsyncLocal<ContinuousAgentLaunchContext?> CurrentSlot = new();
    private static readonly AsyncLocal<AgentLaunchPurpose> CurrentPurpose = new();
    private readonly ContinuousAgentLaunchContext? previousSlot;
    private readonly AgentLaunchPurpose previousPurpose;
    private readonly ContinuousStopGate? gate;
    private int hasLaunchedAgent;
    private int disposed;

    private ContinuousAgentLaunchContext(ContinuousStopGate? gate)
    {
        this.gate = gate;
        previousSlot = CurrentSlot.Value;
        previousPurpose = CurrentPurpose.Value;
        CurrentSlot.Value = this;
        CurrentPurpose.Value = AgentLaunchPurpose.WorkingAgent;
    }

    internal static ContinuousAgentLaunchContext Enter(ContinuousStopGate? gate) => new(gate);

    internal bool HasLaunchedAgent => Volatile.Read(ref hasLaunchedAgent) != 0;

    internal static bool StopRequested => CurrentSlot.Value?.gate?.IsStopRequested ?? false;

    internal static bool TryBeginExternalOperation<T>(Func<T> start, out T operation)
    {
        ArgumentNullException.ThrowIfNull(start);
        var slot = CurrentSlot.Value;
        if (slot?.gate is null)
        {
            operation = start();
            return true;
        }

        return slot.gate.TryStart(start, out operation);
    }

    internal static IDisposable PermitOutcomeClassifier()
    {
        var previous = CurrentPurpose.Value;
        CurrentPurpose.Value = AgentLaunchPurpose.OutcomeClassifier;
        return new PurposeRestorer(previous);
    }

    internal static bool TryLaunch(Action launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var slot = CurrentSlot.Value;
        if (slot?.gate is null)
        {
            launch();
            if (CurrentPurpose.Value == AgentLaunchPurpose.WorkingAgent)
                slot?.MarkAgentLaunched();
            return true;
        }

        return slot.gate.TryLaunch(slot, CurrentPurpose.Value, launch);
    }

    internal static bool HasCurrentSlotLaunchedAgent => CurrentSlot.Value?.HasLaunchedAgent ?? false;

    internal static bool IsActive => CurrentSlot.Value is not null;

    internal void MarkAgentLaunched() => Interlocked.Exchange(ref hasLaunchedAgent, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CurrentSlot.Value = previousSlot;
        CurrentPurpose.Value = previousPurpose;
    }

    private sealed class PurposeRestorer(AgentLaunchPurpose previous) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                CurrentPurpose.Value = previous;
        }
    }
}
