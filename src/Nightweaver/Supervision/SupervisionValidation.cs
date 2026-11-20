namespace Nightweaver.Supervision;

internal static class SupervisionValidation
{
    internal static void Validate(SupervisionState state)
    {
        Validate(state.Identity);
        if (state.Version != 1 || state.Revision < 0 || state.Generation < 0
            || state.Attempts is < 0 or > SupervisionPolicy.MaximumAttempts
            || state.MatchingFailures is < 0 or > 2 || state.DiscardedHistory < 0
            || state.History is null || state.History.Length > SupervisionPolicy.MaximumHistory
            || state.Exhausted && state.Attempts != SupervisionPolicy.MaximumAttempts
            || state.Attempts == SupervisionPolicy.MaximumAttempts && !state.Exhausted && state.PendingAttempt is null
            || state.NextDueUtc is not null && (state.Exhausted || state.PendingAttempt is not null)
            || state.Attention is not (null or "clock-discontinuity" or "unresolved-attempt")
            || state.MatchingFailures > 0 && state.LastObservation?.Health != SupervisionHealth.Failed
            || state.UnresolvedIncident is not null && state.PendingAttempt is null
            || state.Attention == "unresolved-attempt" && state.PendingAttempt is null)
            throw new InvalidDataException("Invalid supervision v1 state or unsupported policy.");
        if (state.MaintenanceToken is not null) ValidateId(state.MaintenanceToken);
        if (state.LastObservation is not null) Validate(state.LastObservation);
        if (state.PendingAttempt is { } pending)
        {
            ValidateId(pending.Id);
            ValidateId(pending.Epoch);
            ValidateUtc(pending.ReservedUtc);
            if (pending.Number != state.Attempts || pending.Number < 1
                || pending.Generation < 0 || pending.Generation > state.Generation)
                throw new InvalidDataException("Invalid recovery reservation identity.");
            if (pending.PreviousOwner is not null) Validate(pending.PreviousOwner);
        }
        if (state.NextDueUtc is { } due) ValidateUtc(due);
        if (state.LastIncident is not null) Validate(state.LastIncident);
        if (state.UnresolvedIncident is not null) Validate(state.UnresolvedIncident);
        foreach (var incident in state.History) Validate(incident);
    }

    internal static void Validate(SupervisionIdentity identity)
    {
        if (identity is null) throw new InvalidDataException("Missing installation identity.");
        ValidateCanonicalPath(identity.Root);
        ValidateCanonicalPath(identity.ConfigPath);
        ValidateCanonicalPath(identity.GitCommonDirectory);
        // SID is obtained by the future current-user installer, never account-name substitution.
        if (string.IsNullOrEmpty(identity.UserSid) || identity.UserSid.Length > 184
            || !identity.UserSid.StartsWith("S-1-", StringComparison.Ordinal)
            || identity.UserSid[4..].Split('-').Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
            throw new InvalidDataException("Invalid Windows user SID.");
    }

    private static void ValidateCanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length > 4096
            || path.Any(char.IsControl)
            || !string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.Ordinal)
            || path[Path.GetPathRoot(path)!.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ')
                    || part.Contains(':', StringComparison.Ordinal)))
            throw new InvalidDataException("Installation identity must use canonical local paths.");
    }

    internal static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Expected a 32-hex operational ID.");
    }

    internal static void Validate(SupervisionObservation observation)
    {
        ValidateId(observation.Epoch);
        ValidateUtc(observation.Utc);
        if (observation.MonotonicTicks < 0 || !Enum.IsDefined(observation.Health))
            throw new InvalidDataException("Invalid health observation.");
        if (observation.Owner is not null) Validate(observation.Owner);
        if (observation.Health is SupervisionHealth.Healthy or SupervisionHealth.Draining && observation.Owner is null)
            throw new InvalidDataException("Live health requires exact owner identity.");
    }

    private static void Validate(SupervisionOwner owner)
    {
        if (owner.ProcessId <= 0) throw new InvalidDataException("Invalid owner PID.");
        ValidateUtc(owner.StartedUtc);
        ValidateId(owner.OwnerId);
        if (owner.RuntimeId is not null) ValidateId(owner.RuntimeId);
    }

    internal static void Validate(SupervisionIncident incident)
    {
        if (incident is null) throw new InvalidDataException("Missing incident.");
        ValidateId(incident.Id);
        ValidateUtc(incident.Utc);
        if (incident.EvidenceId is not null) ValidateId(incident.EvidenceId);
        if (incident.Reason is not ("start-failed" or "recovered" or "settlement-unknown" or "capture-failed"))
            throw new InvalidDataException("Unknown incident reason; free text belongs in bounded local diagnostics.");
    }

    private static void ValidateUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero || value.Year > 9998)
            throw new InvalidDataException("Expected a bounded UTC instant.");
    }
}
