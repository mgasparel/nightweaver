using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Nightweaver.Web;

internal sealed class LocalMutationRequestPolicy(IConfiguration configuration, IServer server)
{
    internal const string RequiredHeader = "X-Nightweaver-Request";
    internal const string RequiredHeaderValue = "console-v1";

    internal bool Allows(HttpRequest request)
    {
        var marker = request.Headers[RequiredHeader];
        if (marker.Count != 1 || !string.Equals(marker[0], RequiredHeaderValue, StringComparison.Ordinal))
            return false;

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (fetchSite.Count > 0 && (fetchSite.Count != 1
            || !string.Equals(fetchSite[0], "same-origin", StringComparison.OrdinalIgnoreCase)))
            return false;
        var fetchMode = request.Headers["Sec-Fetch-Mode"];
        if (fetchMode.Count > 0 && (fetchMode.Count != 1
            || fetchMode[0] is not null && !fetchMode[0]!.Equals("cors", StringComparison.OrdinalIgnoreCase)
                && !fetchMode[0]!.Equals("same-origin", StringComparison.OrdinalIgnoreCase)))
            return false;

        var bindings = GetLocalBindings();
        if (bindings.Length == 0 || !bindings.Any(binding => MatchesRequest(binding, request)))
            return false;

        if (!request.Headers.TryGetValue("Origin", out var origins))
            return true;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)
            || !IsLocalHost(origin.Host))
            return false;

        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == EffectivePort(request.Scheme, request.Host.Port)
            && bindings.Any(binding => MatchesOrigin(binding, origin));
    }

    private Uri[] GetLocalBindings()
    {
        // TestServer has no network binding to advertise. Its default HttpClient
        // origin is localhost, so accept loopback authorities only in that host.
        if (server.GetType().FullName == "Microsoft.AspNetCore.TestHost.TestServer")
            return [new Uri("http://localhost"), new Uri("https://localhost"),
                new Uri("http://127.0.0.1"), new Uri("https://127.0.0.1"),
                new Uri("http://[::1]"), new Uri("https://[::1]")];

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var configured = addresses is { Count: > 0 }
            ? addresses.ToArray()
            : (configuration["urls"] ?? configuration["ASPNETCORE_URLS"]
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return configured.Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsLocalHost(uri.Host) && uri.Scheme is "http" or "https")
            .Select(uri => uri!)
            .ToArray();
    }

    private static bool MatchesRequest(Uri binding, HttpRequest request)
        => string.Equals(binding.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && binding.Port == EffectivePort(request.Scheme, request.Host.Port);

    private static bool MatchesOrigin(Uri binding, Uri origin)
        => string.Equals(binding.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            && binding.Port == origin.Port;

    private static int EffectivePort(string scheme, int? port)
        => port ?? (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);

    private static bool IsLocalHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
