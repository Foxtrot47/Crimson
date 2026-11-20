using Crimson.Providers.Ubisoft;
using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Core.Tests.Ubisoft.UbisoftLibraryTests;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftMetadataTests
{
    [Theory]
    [InlineData("fr-FR", "Jeu")]
    [InlineData("fr-CA", "Jeu")]
    [InlineData("de-DE", "Game")]
    public void TitlesResolveThroughTheUiLanguage(string locale, string title)
    {
        var metadata = new UbisoftMetadata(() => locale);
        Assert.Equal(title, metadata.Describe(Product(1, config: GameConfig)).Title);
    }

    [Fact]
    public void ChangingTheUiLanguageReparsesTheConfiguration()
    {
        var locale = "en-US";
        var metadata = new UbisoftMetadata(() => locale);
        var product = Product(1, config: GameConfig);
        Assert.Equal("Game", metadata.Title(product));
        locale = "fr-FR";
        Assert.Equal("Jeu", metadata.Title(product));
    }

    [Fact]
    public void AnAddonWithoutItsOwnConfigurationUsesItsGamesEntry()
    {
        var metadata = new UbisoftMetadata(() => "en-US");
        var display = metadata.Describe(Product(9, type: UbisoftOwnedProduct.TypeAddon), Product(1, config: GameConfig));
        Assert.Equal("Addon", display.Title);
        Assert.Equal(UbisoftProductConfig.AssetUrl("f3fa5bffd6623a5e8ddb3ebb89415e45.jpg"), display.CoverImageUrl);
    }

    [Fact]
    public void AProductWithoutAConfigurationGetsAPlaceholderAndNoArtwork()
    {
        var display = new UbisoftMetadata(() => "en-US").Describe(Product(77));
        Assert.Equal("Ubisoft 77", display.Title);
        Assert.Null(display.CoverImageUrl);
        Assert.Null(display.LandscapeImageUrl);
    }

    [Theory]
    [InlineData("https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/abc.jpg", true)]
    [InlineData("http://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/abc.jpg", false)]
    [InlineData("https://static3.cdn.ubi.com:8443/orbit/uplay_launcher_3_0/assets/abc.jpg", false)]
    [InlineData("https://user@static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/abc.jpg", false)]
    [InlineData("https://ubistatic3-a.akamaihd.net/orbit/uplay_launcher_3_0/assets/abc.jpg", false)]
    [InlineData("https://static3.cdn.ubi.com.example.com/abc.jpg", false)]
    public void ArtworkIsOnlyTrustedFromTheConfigurationAssetHost(string url, bool trusted)
    {
        var policy = new UbisoftContentPolicy();
        if (trusted) Assert.Equal(new Uri(url), policy.RequireArtworkUri(url));
        else Assert.Throws<InvalidOperationException>(() => policy.RequireArtworkUri(url));
    }
}
