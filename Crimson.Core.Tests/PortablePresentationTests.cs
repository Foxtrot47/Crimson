using Crimson.Core;
using Crimson.Models;
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
            new StoreProductImage { Type = "featuredMedia", Url = "https://example.test/unsafe.jpg", IsVideo = false },
            new StoreProductImage { Type = "ProductLogo", Url = "https://cdn1.epicgames.com/logo.png", IsVideo = false }
        ]);

        Assert.Collection(screenshots, image => Assert.Equal(screenshotUrl, image.Url));
    }

    [Fact]
    public void InstallArtworkPrefersTallImage()
    {
        var game = GameWithImages(
            new KeyImage { Type = "DieselGameBox", Url = "box" },
            new KeyImage { Type = "DieselGameBoxTall", Url = "tall" });

        Assert.Equal("tall", AppInstallDialogViewModel.SelectImageUrl(game));
    }

    [Fact]
    public void InstallArtworkFallsBackToRegularBox()
    {
        var game = GameWithImages(new KeyImage { Type = "DieselGameBox", Url = "box" });

        Assert.Equal("box", AppInstallDialogViewModel.SelectImageUrl(game));
    }

    [Fact]
    public void MissingInstallArtworkProducesNull()
    {
        Assert.Null(AppInstallDialogViewModel.SelectImageUrl(GameWithImages()));
    }

    private static Game GameWithImages(params KeyImage[] images) => new()
    {
        AppName = "test",
        AppTitle = "Test",
        AssetInfos = null!,
        Metadata = new Metadata { KeyImages = [.. images] }
    };
}
