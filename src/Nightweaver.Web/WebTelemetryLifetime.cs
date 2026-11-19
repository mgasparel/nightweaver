using System.Diagnostics;
using Nightweaver.Telemetry;

namespace Nightweaver.Web;

/// <summary>The standalone and background executable share the same telemetry lifetime.</summary>
internal sealed class WebTelemetryLifetime : IDisposable
{
    private readonly OperationalTelemetry? telemetry;
    internal OperationalTelemetry? Telemetry => telemetry;
    private readonly CancellationTokenRegistration started;
    private readonly CancellationTokenRegistration stopped;
    private readonly CancellationTokenRegistration stopping;
    private long shutdownTimestamp;

    internal WebTelemetryLifetime(IHostApplicationLifetime lifetime, long startupTimestamp)
    {
        telemetry = OperationalTelemetry.Start(OperationalHost.Web, dryRun: false,
            message => Console.Error.WriteLine("nightweaver: " + message));
        started = lifetime.ApplicationStarted.Register(() =>
            telemetry?.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success,
                Stopwatch.GetElapsedTime(startupTimestamp).TotalSeconds));
        stopping = lifetime.ApplicationStopping.Register(() => shutdownTimestamp = Stopwatch.GetTimestamp());
        stopped = lifetime.ApplicationStopped.Register(() =>
            telemetry?.RecordOperation(OperationalOperation.HostStop, OperationalOutcome.Success,
                shutdownTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(shutdownTimestamp).TotalSeconds));
    }

    public void Dispose()
    {
        started.Dispose();
        stopping.Dispose();
        stopped.Dispose();
        telemetry?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
