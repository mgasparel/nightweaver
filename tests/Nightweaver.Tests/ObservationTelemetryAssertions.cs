using System.Text;
using static Nightweaver.Tests.OperationalTelemetryTests;

namespace Nightweaver.Tests;

internal static class ObservationTelemetryAssertions
{
    private static readonly string[] ApprovedKeys = ["source", "health", "outcome"];
    private static readonly string[] DetailKeys = ["source", "operation", "outcome"];
    private static readonly string[] Bodies = ["Observation collection completed.", "Operational operation completed."];
    internal static async Task<double> WaitForMetricAsync(Receiver receiver, string name, string family, string? health,
        Func<double, bool> expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            foreach (var capture in receiver.Captures.Where(capture => capture.Signal == "metrics").TakeLast(1))
            {
                foreach (var metric in Metrics(capture))
                {
                    if (metric.Only(1).String != name) continue;
                    var gauge = name is "nightweaver.observation.health" or "nightweaver.observation.age";
                    var data = Message.Parse(metric.Only(gauge ? 5 : 7).Bytes);
                    foreach (var point in data.Fields.Where(field => field.Number == 1).Select(field => Message.Parse(field.Bytes)))
                    {
                        var tags = Attributes(point, 7);
                        if (tags.GetValueOrDefault("source") != family
                            || health is not null && tags.GetValueOrDefault("health") != health) continue;
                        var value = gauge ? BitConverter.ToDouble(point.Only(4).Bytes) : BitConverter.ToInt64(point.Only(6).Bytes);
                        if (expected(value)) return value;
                    }
                }
            }
            await Task.Delay(20, timeout.Token);
        }
    }

    internal static Message[] Metrics(Capture capture) => Message.Parse(Message.Parse(Message.Parse(capture.Bytes)
        .Only(1).Bytes).Only(2).Bytes).Fields.Where(field => field.Number == 2).Select(field => Message.Parse(field.Bytes)).ToArray();

    internal static void AssertSanitized(Receiver receiver, string canary)
    {
        Assert.NotEmpty(receiver.Captures);
        foreach (var capture in receiver.Captures)
        {
            Assert.DoesNotContain(canary, Encoding.UTF8.GetString(capture.Bytes));
            var group = Message.Parse(Message.Parse(capture.Bytes).Only(1).Bytes);
            Assert.Equal("nightweaver-web", Attributes(Message.Parse(group.Only(1).Bytes), 1)["service.name"]);
            if (capture.Signal != "metrics")
            {
                var scope = Message.Parse(group.Only(2).Bytes);
                foreach (var record in scope.Fields.Where(field => field.Number == 2).Select(field => Message.Parse(field.Bytes)))
                {
                    Assert.All(Attributes(record, capture.Signal == "traces" ? 9 : 6).Keys, key => Assert.Contains(key, DetailKeys));
                    if (capture.Signal == "logs")
                        Assert.Contains(Message.Parse(record.Only(5).Bytes).Only(1).String,
                            Bodies);
                }
                continue;
            }
            foreach (var metric in Metrics(capture).Where(metric => metric.Only(1).String.StartsWith("nightweaver.observation.", StringComparison.Ordinal)))
            {
                var dataField = metric.Fields.Single(field => field.Number is 5 or 7 or 9);
                foreach (var point in Message.Parse(dataField.Bytes).Fields.Where(field => field.Number == 1))
                    Assert.All(Attributes(Message.Parse(point.Bytes), dataField.Number == 9 ? 9 : 7).Keys,
                        key => Assert.Contains(key, ApprovedKeys));
            }
        }
    }
}
