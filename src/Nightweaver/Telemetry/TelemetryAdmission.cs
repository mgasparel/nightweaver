namespace Nightweaver.Telemetry;

/// <summary>Nonblocking fixed-memory admission, with at most one minute of burst credit.</summary>
internal sealed class TelemetryAdmission(TimeProvider clock)
{
    internal const int RecordBytes = 2048;
    internal const int BytesPerMinute = 32 * 1024;
    private readonly object gate = new();
    private long previous = clock.GetTimestamp();
    private double credit = BytesPerMinute;

    internal bool TryTake()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            credit = Math.Min(BytesPerMinute, credit + clock.GetElapsedTime(previous, now).TotalMinutes * BytesPerMinute);
            previous = now;
            if (credit < RecordBytes) return false;
            credit -= RecordBytes;
            return true;
        }
    }
}
