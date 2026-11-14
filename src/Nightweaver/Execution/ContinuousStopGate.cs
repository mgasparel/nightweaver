using Nightweaver.Telemetry;

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
    private readonly HashSet<EventWaitHandle> stopSignals = [];
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
        lock (sync)
        {
            stopRequested = true;
            foreach (var signal in stopSignals) signal.Reset();
        }
    }

    // OS signals expose cleanup admission across processes without relying on telemetry
    // freshness or filesystem writes during graceful/force stop.
    internal void RegisterStopSignal(EventWaitHandle signal)
    {
        lock (sync)
        {
            stopSignals.Add(signal);
            if (stopRequested) signal.Reset();
            else signal.Set();
        }
    }

    internal void UnregisterStopSignal(EventWaitHandle signal)
    {
        lock (sync)
        {
            signal.Reset();
            stopSignals.Remove(signal);
        }
    }

    /// <summary>Closes every active Agent Job without entering executor cleanup or waiting for it.</summary>
    internal IReadOnlyList<Exception> RequestForceStop()
    {
        IForceCloseAgentJob[] agents;
        lock (sync)
        {
            forceStopRequested = true;
            stopRequested = true;
            foreach (var signal in stopSignals) signal.Reset();
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

    internal bool TryFinalize<T>(Func<T> start, out T result)
    {
        lock (sync)
        {
            if (forceStopRequested) { result = default!; return false; }
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
    private readonly FactoryTelemetryPublisher? telemetry;
    private readonly string? telemetryPool;
    private readonly string? telemetrySlot;
    private readonly string? telemetryBead;
    private int hasLaunchedAgent;
    private int disposed;

    private ContinuousAgentLaunchContext(ContinuousStopGate? gate, FactoryTelemetryPublisher? telemetry = null,
        string? telemetryPool = null, string? telemetrySlot = null, string? telemetryBead = null)
    {
        this.gate = gate;
        this.telemetry = telemetry;
        this.telemetryPool = telemetryPool;
        this.telemetrySlot = telemetrySlot;
        this.telemetryBead = telemetryBead;
        previousSlot = CurrentSlot.Value;
        previousPurpose = CurrentPurpose.Value;
        CurrentSlot.Value = this;
        CurrentPurpose.Value = AgentLaunchPurpose.WorkingAgent;
    }

    internal static ContinuousAgentLaunchContext Enter(ContinuousStopGate? gate, FactoryTelemetryPublisher? telemetry = null,
        string? telemetryPool = null, string? telemetrySlot = null, string? telemetryBead = null)
        => new(gate, telemetry, telemetryPool, telemetrySlot, telemetryBead);

    internal static void ObservePrepared(PreparedAgentInvocation invocation)
    {
        var slot = CurrentSlot.Value;
        if (slot?.telemetry is null || slot.telemetryPool is null || slot.telemetrySlot is null) return;
        slot.telemetry.ObservePreparedInvocation(slot.telemetryPool, slot.telemetrySlot,
            slot.telemetryBead ?? "unknown", invocation);
    }

    internal static void ObserveLaunched(PreparedAgentInvocation invocation, int? processId, DateTimeOffset startedAt)
    {
        var slot = CurrentSlot.Value;
        if (slot?.telemetry is null || slot.telemetryPool is null || slot.telemetrySlot is null) return;
        slot.telemetry.SetInvocation(slot.telemetryPool, slot.telemetrySlot, new(
            slot.telemetryBead ?? "unknown", invocation.InvocationId, invocation.Purpose, invocation.Executable, invocation.ConfiguredModel,
            invocation.Effort ?? "unknown", "running", startedAt, startedAt + invocation.Timeout,
            processId, null));
    }

    internal static void ObserveSettled(PreparedAgentInvocation invocation, string status, int? exitCode)
    {
        var slot = CurrentSlot.Value;
        if (slot?.telemetry is null || slot.telemetryPool is null || slot.telemetrySlot is null) return;
        slot.telemetry.FinishInvocation(slot.telemetryPool, slot.telemetrySlot, invocation.InvocationId, status, exitCode);
    }

    internal static void ObserveLaunchFailure(PreparedAgentInvocation invocation, string status)
    {
        var slot = CurrentSlot.Value;
        if (slot?.telemetry is null || slot.telemetryPool is null || slot.telemetrySlot is null) return;
        slot.telemetry.SetInvocation(slot.telemetryPool, slot.telemetrySlot, new(
            slot.telemetryBead ?? "unknown", invocation.InvocationId, invocation.Purpose, invocation.Executable,
            invocation.ConfiguredModel, invocation.Effort ?? "unknown", status, null, null, null, null));
    }

    internal bool HasLaunchedAgent => Volatile.Read(ref hasLaunchedAgent) != 0;

    internal static bool ForceStopRequested => CurrentSlot.Value?.gate?.IsForceStopRequested ?? false;

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

    internal static bool TryBeginFinalization<T>(Func<T> start, out T operation)
    {
        var slot = CurrentSlot.Value;
        if (slot?.gate is null) { operation = start(); return true; }
        return slot.gate.TryFinalize(start, out operation);
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
