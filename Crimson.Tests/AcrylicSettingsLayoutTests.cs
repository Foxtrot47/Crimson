using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class AcrylicSettingsLayoutTests
{
    [Fact]
    public void AppearanceToggleUsesAcrylicLabelAndLivePreference()
    {
        var settings = PresentationXaml.Load("SettingsPage.xaml");
        var card = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "SettingsCard" && (string?)element.Attribute("Header") == "Enable Acrylic");
        var toggle = Assert.Single(card.Elements(), element => element.Name.LocalName == "ToggleSwitch");
        Assert.Equal("{x:Bind ViewModel.AcrylicEnabled, Mode=TwoWay}", (string?)toggle.Attribute("IsOn"));
        Assert.Equal("{x:Bind ViewModel.CanChangeAcrylic, Mode=OneWay}", (string?)toggle.Attribute("IsEnabled"));
        var feedback = Assert.Single(settings.Descendants(), element =>
            (string?)element.Attribute("Text") == "{x:Bind ViewModel.AcrylicErrorMessage, Mode=OneWay}");
        Assert.Equal("{x:Bind ViewModel.HasAcrylicError, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            (string?)feedback.Attribute("Visibility"));
        Assert.DoesNotContain(settings.Descendants().Attributes(), attribute => attribute.Value.Contains("Mica", StringComparison.Ordinal));
    }

    [Fact]
    public void SolidFallbackCoversTheWindowWithoutInterceptingInput()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var window = PresentationXaml.Load("MainWindow.xaml");
        var fallback = Assert.Single(window.Descendants(), element => (string?)element.Attribute(xaml + "Name") == "SolidBackdrop");
        Assert.Equal("Border", fallback.Name.LocalName);
        Assert.Equal("2", (string?)fallback.Attribute("Grid.RowSpan"));
        Assert.Equal("{ThemeResource SolidBackgroundFillColorBaseBrush}", (string?)fallback.Attribute("Background"));
        Assert.Equal("False", (string?)fallback.Attribute("IsHitTestVisible"));
        var root = fallback.Parent!;
        Assert.Null(root.Attribute("Background"));
        Assert.Same(fallback, root.Elements().First(element => !element.Name.LocalName.Contains('.')));
    }
}
