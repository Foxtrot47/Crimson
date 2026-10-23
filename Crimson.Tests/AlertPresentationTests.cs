using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class AlertPresentationTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void GameInfoHasNoStoreMetadataBannerAndUsesInlineActionFeedback()
    {
        var page = PresentationXaml.Load("GameInfoPage.xaml");
        Assert.DoesNotContain(page.Descendants(), element => element.Name.LocalName == "InfoBar");
        Assert.Single(page.Descendants(), element => (string?)element.Attribute("Text") == "{x:Bind ViewModel.ActionErrorMessage, Mode=OneWay}");
        Assert.DoesNotContain(page.Descendants().Attributes(), attribute => attribute.Value.Contains("IsStoreDetailsUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void InstallAndPropertiesDialogsHaveInlineErrorAreas()
    {
        var install = PresentationXaml.Load("AppInstallDialog.xaml");
        var error = Assert.Single(install.Descendants(), element => (string?)element.Attribute("Text") == "{x:Bind ViewModel.ErrorMessage, Mode=OneWay}");
        Assert.Equal("{x:Bind ViewModel.HasError, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}", (string?)error.Attribute("Visibility"));
        var properties = PresentationXaml.Load("GameManageDialog.xaml");
        var feedback = Assert.Single(properties.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "ActionErrorText");
        Assert.Equal("Collapsed", (string?)feedback.Attribute("Visibility"));
        Assert.Equal("TextBlock", feedback.Name.LocalName);
    }

    [Fact]
    public void LibraryErrorsHaveTheirOwnRowWithoutOverlappingScrollingGames()
    {
        var page = PresentationXaml.Load("LibraryPage.xaml");
        var error = Assert.Single(page.Descendants(), element => (string?)element.Attribute("Text") == "{x:Bind ViewModel.ActionErrorMessage, Mode=OneWay}");
        var scroller = Assert.Single(page.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "LibraryScroller");
        Assert.Equal("1", (string?)error.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)scroller.Attribute("Grid.Row"));
        Assert.Same(error.Parent, scroller.Parent);
    }
}
