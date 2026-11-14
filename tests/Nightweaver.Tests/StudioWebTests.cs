using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nightweaver.Tests;

public sealed partial class StudioWebTests
{
    [Fact]
    public async Task BootstrapExposesOnlyConfiguredProjectWithoutTrackerOrRuntimeClaims()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/studio/context", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("web-test", json.RootElement.GetProperty("projectId").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Empty(Directory.EnumerateFileSystemEntries(factory.HostRoot));
        Assert.False(Directory.Exists(Path.Combine(factory.TargetRoot, ".beads")));
    }

    [Theory]
    [InlineData("/studio")]
    [InlineData("/studio/")]
    [InlineData("/projects/web-test/overview")]
    [InlineData("/projects/web-test/delivery")]
    [InlineData("/projects/web-test/attention")]
    [InlineData("/projects/web-test/catch-up")]
    [InlineData("/projects/web-test/beads?q=retained&view=all&page=3")]
    [InlineData("/projects/web-test/beads/ns-123?tab=history")]
    public async Task DirectRoutesServeBuiltSpaAndResolvableVersionedAssets(string route)
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Nightweaver · Studio", html, StringComparison.Ordinal);
        var assets = AssetPath().Matches(html);
        Assert.Equal(2, assets.Count);
        foreach (Match asset in assets)
        {
            using var builtAsset = await client.GetAsync(asset.Value, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, builtAsset.StatusCode);
            Assert.True(builtAsset.Content.Headers.ContentLength > 0);
        }
    }

    [Theory]
    [InlineData("/api/missing")]
    [InlineData("/studio/assets/missing.js")]
    [InlineData("/projects/missing.js")]
    [InlineData("/missing")]
    public async Task MissingAssetsAndApisNeverReturnSpa(string route)
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [GeneratedRegex("/studio/assets/[^\" ]+\\.(?:js|css)")]
    private static partial Regex AssetPath();
}
