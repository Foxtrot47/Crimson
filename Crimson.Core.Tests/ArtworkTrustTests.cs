using Crimson.Providers;
using Crimson.Providers.Epic;
using Crimson.Providers.Ubisoft;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

/// <summary>Shared artwork plumbing must obey every registered store, not one hardcoded host list.</summary>
public class ArtworkTrustTests
{
    private const string UbisoftArtwork = "https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/abc.jpg";
    private const string EpicArtwork = "https://cdn1.epicgames.com/community/assets/box.png";
    private const string Untrusted = "https://example.test/tracker.png";

    [Fact]
    public async Task ArtworkCacheDownloadsFromAnyRegisteredStoreAndRejectsUnknownHosts()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-artwork-trust-" + Guid.NewGuid().ToString("N"));
        using var log = new LoggerConfiguration().CreateLogger();
        var handler = new PngHandler();
        using var http = new HttpClient(handler);
        try
        {
            var cache = new ArtworkCache(http, log, Path.Combine(root, "artwork"),
                [new EpicContentPolicy(), new UbisoftContentPolicy()]);

            Assert.NotNull(await cache.GetImageUriAsync(UbisoftArtwork));
            Assert.NotNull(await cache.GetImageUriAsync(EpicArtwork));
            Assert.Null(await cache.GetImageUriAsync(Untrusted));
            Assert.Equal(2, handler.Requests);

            // Without any registered provider nothing is trusted.
            var bare = new ArtworkCache(http, log, Path.Combine(root, "bare"));
            Assert.Null(await bare.GetImageUriAsync(UbisoftArtwork));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("application/octet-stream")] // Ubisoft's CDN
    [InlineData("image/jpeg")]
    [InlineData(null)]
    public async Task ArtworkIsAcceptedWhateverBinaryTypeTheCdnDeclares(string? contentType)
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-artwork-type-" + Guid.NewGuid().ToString("N"));
        using var log = new LoggerConfiguration().CreateLogger();
        var handler = new PngHandler(contentType);
        using var http = new HttpClient(handler);
        try
        {
            var cache = new ArtworkCache(http, log, Path.Combine(root, "artwork"),
                [new EpicContentPolicy(), new UbisoftContentPolicy()]);
            Assert.NotNull(await cache.GetImageUriAsync(UbisoftArtwork));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ArtworkThatIsNotAnImageIsStillRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-artwork-type-" + Guid.NewGuid().ToString("N"));
        using var log = new LoggerConfiguration().CreateLogger();
        using var http = new HttpClient(new HtmlHandler());
        try
        {
            var cache = new ArtworkCache(http, log, Path.Combine(root, "artwork"), [new UbisoftContentPolicy()]);
            Assert.Null(await cache.GetImageUriAsync(UbisoftArtwork));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void EachStoreOnlyVouchesForItsOwnArtworkHosts()
    {
        IProviderContentPolicy ubisoft = new UbisoftContentPolicy();
        IProviderContentPolicy epic = new EpicContentPolicy();

        Assert.True(ubisoft.AllowsArtworkUri(UbisoftArtwork));
        Assert.False(ubisoft.AllowsArtworkUri(EpicArtwork));
        Assert.True(epic.AllowsArtworkUri(EpicArtwork));
        Assert.False(epic.AllowsArtworkUri(UbisoftArtwork));
    }

    private sealed class PngHandler(string? contentType = "image/png") : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var content = new ByteArrayContent(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            if (contentType is not null) content.Headers.ContentType = new(contentType);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class HtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent("<html>not an image</html>");
            content.Headers.ContentType = new("text/html");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}
