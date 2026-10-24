namespace Nightweaver.Execution;

internal sealed record AgentDispatchContext(
    string BeadId,
    string BeadType,
    string? StageLabel,
    int RepairCycles);
