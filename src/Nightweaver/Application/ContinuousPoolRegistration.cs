using System.Threading.Channels;
using Nightweaver.Execution;

namespace Nightweaver.Application;

internal enum ContinuousPoolKind
{
    Planning,
    Delivery,
    Merger
}

internal sealed record ContinuousPoolDiagnostic(string BeadId, string Code, string Message);

internal sealed record ContinuousPoolCompletion(
    string BeadId,
    string? Result,
    string? Diagnostic,
    Exception? Exception,
    bool IsCanceled,
    bool LocalCapacityReleased,
    bool ClaimHandoffCompleted,
    bool IsDrainFailure,
    TaskCompletionSource<bool>? AccountingBarrier = null)
{
    internal bool IsAccountingBarrier => AccountingBarrier is not null;
}

/// <summary>Pool-neutral runtime wiring at the continuous discovery boundary.</summary>
internal sealed record ContinuousPoolRegistration
{
    internal ContinuousPoolKind Kind { get; }
    internal Func<CancellationToken, Task<IReadOnlyList<ContinuousPoolDiagnostic>>> EvaluateAsync { get; }
    internal Func<CancellationToken, IAsyncEnumerable<ContinuousPoolCompletion>> ReadCompletions { get; }
    internal Action Cancel { get; }
    internal Func<CancellationToken, Task> Drain { get; }
    internal Func<CancellationToken, Task>? WaitForCompletionAccountingAsync { get; }

    internal ContinuousPoolRegistration(
        ContinuousPoolKind kind,
        Func<CancellationToken, Task<IReadOnlyList<PlanningPoolDiagnostic>>> evaluateAsync,
        ChannelReader<PlanningPoolCompletion> completions,
        Action cancel,
        Func<CancellationToken, Task> drain,
        Func<CancellationToken, Task>? waitForCompletionAccountingAsync = null)
        : this(kind,
            async token => (await evaluateAsync(token).ConfigureAwait(false))
                .Select(item => new ContinuousPoolDiagnostic(item.BeadId, item.Code.ToString(), item.Message)).ToArray(),
            token => ReadPlanningCompletionsAsync(completions, token),
            cancel, drain, waitForCompletionAccountingAsync)
    {
    }

    internal ContinuousPoolRegistration(
        ContinuousPoolKind kind,
        Func<CancellationToken, Task<IReadOnlyList<DeliveryPoolDiagnostic>>> evaluateAsync,
        ChannelReader<DeliveryPoolCompletion> completions,
        Action cancel,
        Func<CancellationToken, Task> drain,
        Func<CancellationToken, Task>? waitForCompletionAccountingAsync = null)
        : this(kind,
            async token => (await evaluateAsync(token).ConfigureAwait(false))
                .Select(item => new ContinuousPoolDiagnostic(item.BeadId, item.Code.ToString(), item.Message)).ToArray(),
            token => ReadDeliveryCompletionsAsync(completions, token),
            cancel, drain, waitForCompletionAccountingAsync)
    {
    }

    internal ContinuousPoolRegistration(
        ContinuousPoolKind kind,
        Func<CancellationToken, Task<IReadOnlyList<MergerPoolDiagnostic>>> evaluateAsync,
        ChannelReader<MergerPoolCompletion> completions,
        Action cancel,
        Func<CancellationToken, Task> drain,
        Func<CancellationToken, Task>? waitForCompletionAccountingAsync = null)
        : this(kind,
            async token => (await evaluateAsync(token).ConfigureAwait(false))
                .Select(item => new ContinuousPoolDiagnostic(item.BeadId, item.Code.ToString(), item.Message)).ToArray(),
            token => ReadMergerCompletionsAsync(completions, token),
            cancel, drain, waitForCompletionAccountingAsync)
    {
    }

    private ContinuousPoolRegistration(
        ContinuousPoolKind kind,
        Func<CancellationToken, Task<IReadOnlyList<ContinuousPoolDiagnostic>>> evaluateAsync,
        Func<CancellationToken, IAsyncEnumerable<ContinuousPoolCompletion>> readCompletions,
        Action cancel,
        Func<CancellationToken, Task> drain,
        Func<CancellationToken, Task>? waitForCompletionAccountingAsync)
    {
        ArgumentNullException.ThrowIfNull(evaluateAsync);
        ArgumentNullException.ThrowIfNull(readCompletions);
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(drain);
        Kind = kind;
        EvaluateAsync = evaluateAsync;
        ReadCompletions = readCompletions;
        Cancel = cancel;
        Drain = drain;
        WaitForCompletionAccountingAsync = waitForCompletionAccountingAsync;
    }

    private static async IAsyncEnumerable<ContinuousPoolCompletion> ReadPlanningCompletionsAsync(
        ChannelReader<PlanningPoolCompletion> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var completion in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var disposition = completion.Outcome?.Disposition;
            var failed = completion.Exception is not null || completion.IsCanceled
                || disposition == PlanningSlotDisposition.Failed;
            yield return new ContinuousPoolCompletion(
                completion.BeadId,
                disposition?.ToString() ?? (completion.IsAccountingBarrier ? null : "ended without an outcome"),
                completion.Diagnostic,
                completion.Exception,
                completion.IsCanceled,
                completion.LocalCapacityReleased,
                completion.ClaimHandoffCompleted,
                failed,
                completion.AccountingBarrier);
        }
    }

    private static async IAsyncEnumerable<ContinuousPoolCompletion> ReadDeliveryCompletionsAsync(
        ChannelReader<DeliveryPoolCompletion> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var completion in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var disposition = completion.Outcome?.Disposition;
            var failed = completion.Exception is not null
                || completion.IsCanceled
                || disposition is DeliverySlotDisposition.Failed or DeliverySlotDisposition.IncompletePauseDisposition
                || disposition == DeliverySlotDisposition.Escalated && !completion.ClaimHandoffCompleted
                || disposition == DeliverySlotDisposition.PausedForProductDecision && !completion.ClaimHandoffCompleted;
            yield return new ContinuousPoolCompletion(
                completion.BeadId,
                disposition?.ToString() ?? (completion.IsAccountingBarrier ? null : "ended without an outcome"),
                completion.Diagnostic,
                completion.Exception,
                completion.IsCanceled,
                completion.LocalCapacityReleased,
                completion.ClaimHandoffCompleted,
                failed,
                completion.AccountingBarrier);
        }
    }

    private static async IAsyncEnumerable<ContinuousPoolCompletion> ReadMergerCompletionsAsync(
        ChannelReader<MergerPoolCompletion> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var completion in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var disposition = completion.Outcome?.Disposition;
            var receipt = completion.Outcome?.Stage?.StageResult.MergerCompletion;
            var requiredHousekeepingFailed = receipt is { } completedMerge
                && (!completedMerge.CleanupSucceeded && !completedMerge.CleanupQueued
                    || completedMerge.ProductDiagnostic is not null
                    || completedMerge.DiagnosticPersistenceDiagnostic is not null);
            var failed = completion.Exception is not null
                || completion.IsCanceled
                || disposition is MergerSlotDisposition.Failed or MergerSlotDisposition.Canceled
                || disposition == MergerSlotDisposition.Escalated && !completion.Outcome!.ClaimHandoffCompleted
                || disposition == MergerSlotDisposition.PausedForProductDecision && !completion.Outcome!.ClaimHandoffCompleted
                || disposition == MergerSlotDisposition.Merged && requiredHousekeepingFailed;
            yield return new ContinuousPoolCompletion(
                completion.BeadId,
                disposition?.ToString() ?? (completion.IsAccountingBarrier ? null : "ended without an outcome"),
                completion.Diagnostic,
                completion.Exception,
                completion.IsCanceled,
                completion.LocalCapacityReleased,
                completion.Outcome?.ClaimHandoffCompleted ?? false,
                failed,
                completion.AccountingBarrier);
        }
    }

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
