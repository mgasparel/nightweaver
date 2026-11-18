using System.Net;
using System.Net.Http.Headers;

namespace Nightweaver.Telemetry;

internal sealed class OperationalTelemetryConfiguration(Uri endpoint, string headers, double samplingRatio)
{
    internal Uri Endpoint { get; } = endpoint;
    internal string Headers { get; } = headers;
    internal double SamplingRatio { get; } = samplingRatio;
    public override string ToString() => "Operational telemetry configuration (private).";

    internal const string DefaultEndpoint = "https://otlp-gateway-prod-ca-east-0.grafana.net/otlp";

    // Read environment directly: configuration providers and command-line arguments may not supply secrets.
    internal static OperationalTelemetryConfiguration? Read(Func<string, string?> read, bool dryRun,
        Action<string> diagnostic)
    {
        if (dryRun || read("NIGHTWEAVER_TELEMETRY_ENABLED") != "true") return null;
        try
        {
            var endpoint = read("OTEL_EXPORTER_OTLP_ENDPOINT") ?? DefaultEndpoint;
            var headers = read("OTEL_EXPORTER_OTLP_HEADERS") ?? "";
            var protocol = read("OTEL_EXPORTER_OTLP_PROTOCOL") ?? "http/protobuf";
            if (protocol != "http/protobuf" || endpoint.Length > 2048 || headers.Length > 4096
                || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || (uri.Scheme != "https" && !(uri.Scheme == "http"
                    && read("NIGHTWEAVER_TELEMETRY_ISOLATED_QA") == "true"
                    && IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)))
                || uri.AbsolutePath.Contains("/v1/", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                || read("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY") is not null)
                throw new FormatException();
            if (headers.Length == 0 && uri.Scheme == "https") throw new FormatException();
            // Only Authorization is approved. Reject ambiguous duplicates and invalid HTTP values.
            if (headers.Length > 0)
            {
                var parts = headers.Split('=', 2);
                if (parts.Length != 2 || !parts[0].Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || parts[1].Contains(',') || Uri.UnescapeDataString(parts[1]).Any(char.IsControl)
                    || !AuthenticationHeaderValue.TryParse(Uri.UnescapeDataString(parts[1]), out var authorization)
                    || authorization.Parameter is null)
                    throw new FormatException();
            }
            return new(uri, headers,
                read("NIGHTWEAVER_TELEMETRY_ISOLATED_QA") == "true" ? 1 : 0.1);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            diagnostic("Operational telemetry disabled: invalid configuration.");
            return null;
        }
    }

    internal Uri SignalEndpoint(string signal) => new(Endpoint.AbsoluteUri.TrimEnd('/') + "/v1/" + signal);
}
