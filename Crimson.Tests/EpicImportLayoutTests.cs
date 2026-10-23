using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class EpicImportLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void LibraryActionRowContainsInstalledFilterAndImportInsteadOfASidebarEntry()
    {
        var main = Load("MainWindow.xaml");
        Assert.DoesNotContain(main.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "EpicImportNavItem");
        var library = Load("LibraryPage.xaml");
        var actions = Assert.Single(library.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "LibraryActions");
        var filter = Assert.Single(actions.Descendants(), element => element.Name.LocalName == "CheckBox");
        Assert.Equal("Installed only", (string?)filter.Attribute("Content"));
        Assert.Equal("{x:Bind ViewModel.InstalledOnly, Mode=TwoWay}", (string?)filter.Attribute("IsChecked"));
        var import = Assert.Single(actions.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "EpicImportButton");
        Assert.Equal("ImportButton_Click", (string?)import.Attribute("Click"));
        Assert.Single(import.Descendants(), element => (string?)element.Attribute("Text") == "Import from Epic");
        Assert.Single(import.Descendants(), element => element.Name.LocalName == "FontIcon");
    }

    [Fact]
    public void LibraryExplainsAnEmptyInstalledFilter()
    {
        var library = Load("LibraryPage.xaml");
        var empty = Assert.Single(library.Descendants(), element => (string?)element.Attribute("Text") == "No installed games.");
        Assert.Equal("{x:Bind ViewModel.HasNoInstalledGames, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            (string?)empty.Attribute("Visibility"));
    }

    [Fact]
    public void PickerUsesSelectableCardsWithLocationAndBuildAndAnExplicitSelectionButton()
    {
        var document = Load("EpicImportDialog.xaml");
        var dialog = document.Root!;
        Assert.Equal("Import selected", (string?)dialog.Attribute("PrimaryButtonText"));
        Assert.Equal("{x:Bind ViewModel.CanImport, Mode=OneWay}", (string?)dialog.Attribute("IsPrimaryButtonEnabled"));
        Assert.Equal("Cancel", (string?)dialog.Attribute("CloseButtonText"));
        var card = Assert.Single(document.Descendants(), element => element.Name.LocalName == "ToggleButton");
        Assert.Equal("{x:Bind IsSelected, Mode=TwoWay}", (string?)card.Attribute("IsChecked"));
        Assert.Equal("Stretch", (string?)card.Attribute("HorizontalAlignment"));
        Assert.Equal("Stretch", (string?)card.Attribute("HorizontalContentAlignment"));
        Assert.Equal("{x:Bind Title}", (string?)card.Attribute("AutomationProperties.Name"));
        Assert.Single(card.Descendants(), element => (string?)element.Attribute("Text") == "{x:Bind Installation.InstallLocation}");
        var version = Assert.Single(card.Descendants(), element => (string?)element.Attribute("Text") == "{x:Bind Installation.BuildVersion}");
        Assert.Equal("TextBlock", version.Name.LocalName);
        Assert.DoesNotContain(card.Descendants(), element => ((string?)element.Attribute("Text"))?.Contains("Installed build") == true);
        var image = Assert.Single(card.Descendants(), element => element.Name.LocalName == "Image");
        Assert.Equal("{x:Bind ImageUrl, Converter={StaticResource ImageUrlConverter}}", (string?)image.Attribute("Source"));
        Assert.Equal("UniformToFill", (string?)image.Attribute("Stretch"));
        Assert.Single(card.Descendants(), element => element.Name.LocalName == "FontIcon");
        var items = Assert.Single(document.Descendants(), element => element.Name.LocalName == "ItemsControl");
        Assert.Equal("{x:Bind ViewModel.Items, Mode=OneWay}", (string?)items.Attribute("ItemsSource"));
        Assert.Equal("{x:Bind ViewModel.CanEdit, Mode=OneWay}", (string?)items.Attribute("IsEnabled"));
    }

    [Fact]
    public void PickerAllowsAWiderContentSurface()
    {
        var document = Load("EpicImportDialog.xaml");
        var maximumWidth = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Key") == "ContentDialogMaxWidth");
        Assert.Equal("688", maximumWidth.Value);
        var content = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "DialogContent");
        Assert.Null(content.Attribute("MaxWidth"));
    }

    [Fact]
    public void StatusAndProgressAreHiddenWhenThereIsNothingToShow()
    {
        var document = Load("EpicImportDialog.xaml");
        var status = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute("Text") == "{x:Bind ViewModel.StatusMessage, Mode=OneWay}");
        Assert.Equal("{x:Bind ViewModel.HasStatusMessage, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            (string?)status.Attribute("Visibility"));
        var progress = Assert.Single(document.Descendants(), element => element.Name.LocalName == "ProgressBar");
        Assert.Equal("{x:Bind ViewModel.IsBusy, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            (string?)progress.Attribute("Visibility"));
    }

    private static XDocument Load(string name) => PresentationXaml.Load(name);
}
