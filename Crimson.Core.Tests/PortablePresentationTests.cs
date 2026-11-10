using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Providers.Epic;
using Crimson.ViewModels;

namespace Crimson.Tests;

public sealed class PortablePresentationTests
{
    [Theory]
    [InlineData(null, GamePrimaryAction.Install)]
    [InlineData(InstallState.NotInstalled, GamePrimaryAction.Install)]
    [InlineData(InstallState.Installed, GamePrimaryAction.Play)]
    [InlineData(InstallState.NeedUpdate, GamePrimaryAction.Update)]
    [InlineData(InstallState.Broken, GamePrimaryAction.Repair)]
    [InlineData(InstallState.Installing, GamePrimaryAction.None)]
    public void MapsInstallStateToSemanticPrimaryAction(
        InstallState? installState,
        GamePrimaryAction expected)
    {
        Assert.Equal(expected, GameInfoViewModel.GetPrimaryAction(installState));
    }

    [Theory]
    [InlineData(null, "Not installed")]
    [InlineData(InstallState.Installed, "Ready to play")]
    [InlineData(InstallState.NeedUpdate, "Update available")]
    [InlineData(InstallState.Broken, "Repair required")]
    [InlineData(InstallState.Installing, "Operation in progress")]
    [InlineData(InstallState.UpdatingPaused, "Operation paused")]
    public void MapsInstallStateToGameStatus(InstallState? installState, string expected)
    {
        Assert.Equal(expected, GameInfoViewModel.GetGameStatusLabel(installState));
    }

    [Theory]
    [InlineData("<!--textBlock--><p>First &amp; second</p><br>Third", "First & second\n\nThird")]
    [InlineData("# Features\n![Scene](https://cdn.example/scene.jpg)\nUse **cross-play** with [friends](https://example.test).", "Features\n\nUse cross-play with friends.")]
    [InlineData(null, "")]
    public void FormatsStoreDescriptionAsPlainText(string? value, string expected)
    {
        Assert.Equal(expected, StoreDescriptionFormatter.ToPlainText(value));
    }

    [Fact]
    public void SelectsDistinctStoreScreenshots()
    {
        const string screenshotUrl = "https://cdn1.epicgames.com/images/one.jpg";
        var screenshots = GameInfoViewModel.GetScreenshots([
            new StoreProductImage { Type = "featuredMedia", Url = screenshotUrl, IsVideo = false },
            new StoreProductImage { Type = "featuredMedia", Url = screenshotUrl.ToUpperInvariant(), IsVideo = false },
            new StoreProductImage { Type = "featuredMedia", Url = "https://cdn1.epicgames.com/video.mp4", IsVideo = true },
            new StoreProductImage { Type = "ProductLogo", Url = "https://cdn1.epicgames.com/logo.png", IsVideo = false }
        ]);

        Assert.Collection(screenshots, image => Assert.Equal(screenshotUrl, image.Url));
    }

    [Fact]
    public void InstallArtworkPrefersTheCoverTheProviderPublished()
    {
        var game = GameWithImages(cover: "https://cdn1.epicgames.com/tall.png",
            landscape: "https://cdn1.epicgames.com/wide.png");

        Assert.Equal("https://cdn1.epicgames.com/tall.png", AppInstallDialogViewModel.SelectImageUrl(game));
    }

    [Fact]
    public void InstallArtworkFallsBackToTheLandscapeImage()
    {
        var game = GameWithImages(cover: null, landscape: "https://cdn1.epicgames.com/wide.png");

        Assert.Equal("https://cdn1.epicgames.com/wide.png", AppInstallDialogViewModel.SelectImageUrl(game));
    }

    [Fact]
    public void MissingInstallArtworkProducesNull()
    {
        Assert.Null(AppInstallDialogViewModel.SelectImageUrl(GameWithImages(null, null)));
    }

    [Fact]
    public void EpicCoverArtworkPrefersTallThenBoxArtworkFromTrustedHosts()
    {
        var images = new[]
        {
            new KeyImage { Type = "DieselGameBox", Url = "https://cdn1.epicgames.com/box.png" },
            new KeyImage { Type = "DieselGameBoxTall", Url = "https://unapproved.example/tall.png" },
            new KeyImage { Type = "OfferImageTall", Url = "https://cdn1.epicgames.com/offer-tall.png" }
        };

        Assert.Equal("https://cdn1.epicgames.com/offer-tall.png",
            EpicContentImages.Find(images, "DieselGameBoxTall", "OfferImageTall", "DieselGameBox"));
    }

    [Fact]
    public void EpicStoreArtworkKeepsOnlyTrustedContentHosts()
    {
        var details = EpicContentImages.TrustStoreDetails(new StoreProductDetails
        {
            Images =
            [
                new StoreProductImage { Type = "featuredMedia", Url = "https://cdn1.epicgames.com/safe.jpg" },
                new StoreProductImage { Type = "featuredMedia", Url = "https://example.test/unsafe.jpg" }
            ]
        });

        var image = Assert.Single(details.Images);
        Assert.Equal("https://cdn1.epicgames.com/safe.jpg", image.Url);
    }

    private static LauncherGame GameWithImages(string? cover, string? landscape) => new()
    {
        Id = new GameId("epic", "test"),
        AppTitle = "Test",
        CoverImageUrl = cover,
        LandscapeImageUrl = landscape
    };
}
