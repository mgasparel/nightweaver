using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class LocalMutationRequestPolicyTests
{
    [Fact]
    public void AllowsSameOriginBrowserRequestOnAspireAllocatedProxyPort()
    {
        using var server = new AddressServer("http://localhost:15618");
        var policy = new LocalMutationRequestPolicy(new ConfigurationBuilder().Build(), server);
        var request = CreateRequest("http://localhost:52347", includeOrigin: true);

        Assert.True(policy.Allows(request));
    }

    [Fact]
    public void AllowsMarkedLocalClientOnAspireAllocatedProxyPortWithoutOrigin()
    {
        using var server = new AddressServer("http://localhost:15618");
        var policy = new LocalMutationRequestPolicy(new ConfigurationBuilder().Build(), server);
        var request = CreateRequest("http://localhost:52347", includeOrigin: false);

        Assert.True(policy.Allows(request));
    }

    [Fact]
    public void RejectsOriginThatDoesNotMatchTheBrowserVisibleAuthority()
    {
        using var server = new AddressServer("http://localhost:15618");
        var policy = new LocalMutationRequestPolicy(new ConfigurationBuilder().Build(), server);
        var request = CreateRequest("http://localhost:52347", includeOrigin: true);
        request.Headers.Origin = "http://localhost:52348";

        Assert.False(policy.Allows(request));
    }

    [Fact]
    public void RejectsNonLocalRequestHostEvenWhenOriginMatches()
    {
        using var server = new AddressServer("http://localhost:15618");
        var policy = new LocalMutationRequestPolicy(new ConfigurationBuilder().Build(), server);
        var request = CreateRequest("http://attacker.invalid:52347", includeOrigin: true);

        Assert.False(policy.Allows(request));
    }

    private static HttpRequest CreateRequest(string authority, bool includeOrigin)
    {
        var uri = new Uri(authority);
        var request = new DefaultHttpContext().Request;
        request.Scheme = uri.Scheme;
        request.Host = new HostString(uri.Host, uri.Port);
        request.Headers[LocalMutationRequestPolicy.RequiredHeader] = LocalMutationRequestPolicy.RequiredHeaderValue;
        request.Headers["Sec-Fetch-Site"] = "same-origin";
        request.Headers["Sec-Fetch-Mode"] = "cors";
        if (includeOrigin) request.Headers.Origin = authority;
        return request;
    }

    private sealed class AddressServer(params string[] addresses) : IServer
    {
        private readonly FeatureCollection features = CreateFeatures(addresses);

        public IFeatureCollection Features => features;

        public Task StartAsync<TContext>(IHttpApplication<TContext> application,
            CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }

        private static FeatureCollection CreateFeatures(IEnumerable<string> addresses)
        {
            var features = new FeatureCollection();
            features.Set<IServerAddressesFeature>(new AddressFeature(addresses));
            return features;
        }
    }

    private sealed class AddressFeature(IEnumerable<string> addresses) : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; set; } = addresses.ToArray();

        public bool PreferHostingUrls { get; set; }
    }
}
