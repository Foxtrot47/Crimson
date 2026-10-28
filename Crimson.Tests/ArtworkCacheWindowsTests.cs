using System.Net;
using Crimson.Utils;
using Serilog;
using Windows.Graphics.Imaging;

namespace Crimson.Tests;

public sealed class ArtworkCacheWindowsTests
{
    [Fact]
    public async Task CachedArtworkCanBeDecodedByWindowsAndReopenedWithoutNetwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"crimson-artwork-{Guid.NewGuid():N}");
        using var logger = new LoggerConfiguration().CreateLogger();
        var handler = new ImageHandler();
        using var http = new HttpClient(handler);
        try
        {
            const string url = "https://cdn1.epicgames.com/windows-artwork.png";
            var cache = new ArtworkCache(http, logger, directory);
            var uri = await cache.GetImageUriAsync(url);
            Assert.NotNull(uri);
            // The WinUI converter opens cached files as random-access streams too;
            // this exercises the actual Windows decoder rather than a file-exists check.
            using (var file = File.OpenRead(new Uri(uri).LocalPath))
            using (var stream = file.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                Assert.Equal(1u, decoder.PixelWidth);
                Assert.Equal(1u, decoder.PixelHeight);
                Assert.NotEmpty((await decoder.GetPixelDataAsync()).DetachPixelData());
            }
            Assert.Equal(uri, await new ArtworkCache(http, logger, directory).GetImageUriAsync(url));
            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var content = new ByteArrayContent(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
