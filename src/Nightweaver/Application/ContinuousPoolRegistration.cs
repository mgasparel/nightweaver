using System.Threading.Channels;
using Nightweaver.Execution;

namespace Nightweaver.Application;

internal enum ContinuousPoolKind
{
    Planning,
    Delivery,
    Merger
}

/// <summary>Small runtime wiring for one statically composed lifecycle pool.</summary>
internal sealed record ContinuousPoolRegistration(
    ContinuousPoolKind Kind,
    Func<CancellationToken, Task<IReadOnlyList<DeliveryPoolDiagnostic>>> EvaluateAsync,
    ChannelReader<DeliveryPoolCompletion> Completions,
    Action Cancel,
    Func<CancellationToken, Task> Drain,
    Func<CancellationToken, Task>? WaitForCompletionAccountingAsync = null)
{
    internal static IReadOnlyList<ContinuousPoolRegistration> Order(
        IReadOnlyList<ContinuousPoolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Count == 0)
            throw new ArgumentException("At least one continuous pool must be configured.", nameof(registrations));

        var ordered = registrations.OrderBy(registration => registration.Kind).ToArray();
        if (ordered.Any(registration => !Enum.IsDefined(registration.Kind)))
            throw new ArgumentException("A continuous pool has an unknown lifecycle position.", nameof(registrations));
        if (ordered.Select(registration => registration.Kind).Distinct().Count() != ordered.Length)
            throw new ArgumentException("A continuous pool kind may be registered only once.", nameof(registrations));

        return ordered;
    }
}
