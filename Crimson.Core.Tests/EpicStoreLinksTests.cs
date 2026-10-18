using Crimson.Core;
using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Tests;

public sealed class EpicStoreLinksTests
{
    [Theory]
    [InlineData("rocket-league", "https://store.epicgames.com/en-US/p/rocket-league")]
    [InlineData("rocket-league/home", "https://store.epicgames.com/en-US/p/rocket-league")]
    [InlineData(" /rocket-league/home/ ", "https://store.epicgames.com/en-US/p/rocket-league")]
    public void UsesExplicitCatalogSlugAndNormalizesLegacyHomeSuffix(string slug, string expected)
    {
        Assert.Equal(expected, EpicStoreLinks.ForGame(Game(slug))?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.test/product")]
    [InlineData("//evil.test")]
    [InlineData("../other")]
    [InlineData("game?redirect=evil")]
    public void MissingOrInvalidSlugFallsBackToAnExplicitStoreSearch(string? slug)
    {
        var uri = EpicStoreLinks.ForGame(Game(slug, "Game & DLC"))!;

        Assert.True(EpicEndpointPolicy.IsAllowedStoreUri(uri));
        Assert.Equal("store.epicgames.com", uri.Host);
        Assert.Equal("/en-US/browse", uri.AbsolutePath);
        Assert.Contains("q=Game%20%26%20DLC", uri.Query);
    }

    [Fact]
    public void MissingGameOrBothMissingIdentifiersHasNoLink()
    {
        Assert.Null(EpicStoreLinks.ForGame(null));
        Assert.Null(EpicStoreLinks.ForGame(Game(null, "")));
    }

    [Fact]
    public void GameChangeNotifiesStoreLinkBindings()
    {
        var viewModel = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance, null!, null!, null!, null!);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert.False(viewModel.CanViewStore);

        viewModel.Game = Game("rocket-league");

        Assert.True(viewModel.CanViewStore);
        Assert.Equal("/en-US/p/rocket-league", viewModel.StorePageUri!.AbsolutePath);
        Assert.Contains(nameof(GameInfoViewModel.StorePageUri), changes);
        Assert.Contains(nameof(GameInfoViewModel.CanViewStore), changes);
    }

    private static Game Game(string? slug, string title = "Rocket League") => new()
    {
        AppName = "game",
        AppTitle = title,
        AssetInfos = new AssetInfos
        {
            Windows = new Asset { AppName = "game", Namespace = "test", CatalogItemId = "game", BuildVersion = "test" }
        },
        Metadata = new Metadata
        {
            CustomAttributes = new CustomAttributes
            {
                ComEpicgamesAppProductSlug = new ComEpicgamesAppProductSlug { Value = slug! }
            }
        }
    };
}
