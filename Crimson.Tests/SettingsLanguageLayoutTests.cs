using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class SettingsLanguageLayoutTests
{
    [Fact]
    public void LanguageVisibilityConverterIsDeclaredInPageResources()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = PresentationXaml.Load("SettingsPage.xaml");
        var resources = Assert.Single(page.Root!.Elements(), element => element.Name.LocalName == "Page.Resources");
        var converter = Assert.Single(resources.Elements(), element =>
            (string?)element.Attribute(xaml + "Key") == "BoolToVisibilityConverter");
        Assert.Equal("BoolToVisibilityConverter", converter.Name.LocalName);
        Assert.Equal("using:CommunityToolkit.WinUI.Converters", converter.Name.NamespaceName);
    }

    [Fact]
    public void LanguagePickerUsesSupportedOptionsAndShowsRestartAndSaveFeedbackInline()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = PresentationXaml.Load("SettingsPage.xaml");
        var picker = Assert.Single(page.Descendants(), element => (string?)element.Attribute(xaml + "Name") == "LanguagePicker");
        Assert.Equal("ComboBox", picker.Name.LocalName);
        Assert.Equal("{x:Bind ViewModel.Languages}", (string?)picker.Attribute("ItemsSource"));
        Assert.Equal("DisplayName", (string?)picker.Attribute("DisplayMemberPath"));
        Assert.Equal("{x:Bind ViewModel.SelectedLanguage, Mode=OneWay}", (string?)picker.Attribute("SelectedItem"));
        Assert.Equal("LanguageSelectionChanged", (string?)picker.Attribute("SelectionChanged"));
        Assert.Equal("Language", (string?)picker.Parent!.Attribute("Header"));
        var restartArea = Assert.Single(page.Descendants(), element => (string?)element.Attribute("Visibility") ==
            "{x:Bind ViewModel.IsLanguageRestartRequired, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}");
        var restartButton = Assert.Single(restartArea.Descendants(), element => element.Name.LocalName == "Button");
        Assert.Equal("Restart Crimson", (string?)restartButton.Attribute("Content"));
        Assert.Equal("RestartClicked", (string?)restartButton.Attribute("Click"));
        Assert.Single(page.Descendants(), element => (string?)element.Attribute("Text") ==
            "{x:Bind ViewModel.LanguageErrorMessage, Mode=OneWay}");
    }
}
