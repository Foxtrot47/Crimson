using Crimson.Core;
using Crimson.Providers;
using Crimson.Providers.Epic;
using Crimson.Providers.Ubisoft;
using Crimson.Utils;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

/// <summary>
/// Cards must show the artwork each store publishes, not only Epic's: the reported symptom was a
/// card that rendered nothing but its title.
/// </summary>
public class LibraryArtworkTests
{
    private const string UbisoftCover = "https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/cover.jpg";
    private const string UntrustedCover = "https://example.test/tracker.png";

    [Fact]
    public async Task CardCarriesStoreArtworkAndStoreNameFromAnyRegisteredProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-library-artwork-" + Guid.NewGuid().ToString("N"));
        using var log = new LoggerConfiguration().CreateLogger();
        try
        {
            var provider = new TestLauncherProvider("ubisoft");
            provider.Games.Add("2688", coverImageUrl: UbisoftCover);
            var registry = new LauncherRegistry([provider]);
            using var http = new HttpClient(new PngHandler());
            var cache = new ArtworkCache(http, log, Path.Combine(root, "artwork"),
                [new EpicContentPolicy(), new UbisoftContentPolicy()]);
            var viewModel = new LibraryViewModel(log, new LauncherInstaller(registry),
                new LauncherLibrary(registry), registry, new ImmediateDispatcher(), cache);

            await viewModel.OnNavigatedTo(null!);
            var card = Assert.Single(viewModel.GamesList);
            // Artwork resolves on a background task; the card fills in when the cache answers.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (card.ImageUrl is null && DateTime.UtcNow < deadline) await Task.Delay(20);

            Assert.Equal("ubisoft", card.ProviderName);
            Assert.Equal(UbisoftCover, card.SourceImageUrl);
            Assert.StartsWith("file:", card.ImageUrl);
            viewModel.OnNavigatedFrom();
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task CardIgnoresArtworkFromAnUnregisteredHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-library-artwork-" + Guid.NewGuid().ToString("N"));
        using var log = new LoggerConfiguration().CreateLogger();
        try
        {
            var provider = new TestLauncherProvider("ubisoft");
            provider.Games.Add("2688", coverImageUrl: UntrustedCover);
            var registry = new LauncherRegistry([provider]);
            using var http = new HttpClient(new PngHandler());
            var cache = new ArtworkCache(http, log, Path.Combine(root, "artwork"),
                [new EpicContentPolicy(), new UbisoftContentPolicy()]);
            var viewModel = new LibraryViewModel(log, new LauncherInstaller(registry),
                new LauncherLibrary(registry), registry, new ImmediateDispatcher(), cache);

            await viewModel.OnNavigatedTo(null!);

            Assert.Null(Assert.Single(viewModel.GamesList).ImageUrl);
            viewModel.OnNavigatedFrom();
        }
        finally { Cleanup(root); }
    }

    // The cache may still be finishing an unrelated download when the test ends.
    private static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }

    private sealed class PngHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}
