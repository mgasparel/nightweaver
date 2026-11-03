namespace Nightweaver.Execution;

internal interface IForceCloseAgentJob
{
    void CloseForForceStop();
}

/// <summary>Serializes first-stop with new slot reservations and native Agent launches.</summary>
internal sealed class ContinuousStopGate
{
    private readonly object sync = new();
    private readonly HashSet<IForceCloseAgentJob> activeAgents = [];
    private bool stopRequested;
    private bool forceStopRequested;

    internal bool IsStopRequested
    {
        get { lock (sync) return stopRequested; }
    }

    internal bool IsForceStopRequested
    {
        get { lock (sync) return forceStopRequested; }
    }

    internal void RequestStop()
    {
        lock (sync) stopRequested = true;
    }

    /// <summary>Closes every active Agent Job without entering executor cleanup or waiting for it.</summary>
    internal IReadOnlyList<Exception> RequestForceStop()
    {
        IForceCloseAgentJob[] agents;
        lock (sync)
        {
            forceStopRequested = true;
            stopRequested = true;
            agents = activeAgents.ToArray();
        }

        List<Exception>? failures = null;
        foreach (var agent in agents)
        {
            try { agent.CloseForForceStop(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        return failures ?? [];
    }

    internal void RegisterActiveJob(IForceCloseAgentJob agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (sync)
        {
            if (forceStopRequested)
                throw new InvalidOperationException("Cannot register an Agent Job after force stop has started.");
            activeAgents.Add(agent);
        }
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
            if (forceStopRequested || (stopRequested && !(purpose == AgentLaunchPurpose.OutcomeClassifier && slot.HasLaunchedAgent)))
                return false;

            launch();
            if (purpose == AgentLaunchPurpose.WorkingAgent)
                slot.MarkAgentLaunched();
            return true;
        }
    }

    internal bool TryLaunchAgent(
        ContinuousAgentLaunchContext slot,
        AgentLaunchPurpose purpose,
        Func<WindowsProcessLauncher.WindowsAgentProcess> launch,
        out WindowsProcessLauncher.WindowsAgentProcess? process)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(launch);
        lock (sync)
        {
            if (forceStopRequested || (stopRequested && !(purpose == AgentLaunchPurpose.OutcomeClassifier && slot.HasLaunchedAgent)))
            {
                process = null;
                return false;
            }

            process = launch();
            activeAgents.Add(process);
            process.SetJobClosedCallback(ForgetAgent);
            if (purpose == AgentLaunchPurpose.WorkingAgent)
                slot.MarkAgentLaunched();
            return true;
        }
    }

    private void ForgetAgent(IForceCloseAgentJob process)
    {
        lock (sync) activeAgents.Remove(process);
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

    internal static bool TryLaunchAgent(
        Func<WindowsProcessLauncher.WindowsAgentProcess> launch,
        out WindowsProcessLauncher.WindowsAgentProcess? process)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var slot = CurrentSlot.Value;
        if (slot?.gate is null)
        {
            process = launch();
            if (CurrentPurpose.Value == AgentLaunchPurpose.WorkingAgent)
                slot?.MarkAgentLaunched();
            return true;
        }

        return slot.gate.TryLaunchAgent(slot, CurrentPurpose.Value, launch, out process);
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
