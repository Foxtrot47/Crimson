using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class LibraryViewLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ViewControlsUseOneRadioGroupWithComplementaryViewBindings()
    {
        var document = Load();
        var actions = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "LibraryActions");
        var controls = actions.Descendants().Where(element => element.Name.LocalName == "RadioButton").ToList();
        Assert.Equal(2, controls.Count);
        var grid = Assert.Single(controls, element => (string?)element.Attribute(Xaml + "Name") == "GridViewButton");
        var list = Assert.Single(controls, element => (string?)element.Attribute(Xaml + "Name") == "ListViewButton");
        Assert.Equal("LibraryViewMode", (string?)grid.Attribute("GroupName"));
        Assert.Equal((string?)grid.Attribute("GroupName"), (string?)list.Attribute("GroupName"));
        Assert.Equal("{x:Bind ViewModel.IsGridView, Mode=OneWay}", (string?)grid.Attribute("IsChecked"));
        Assert.Equal("{x:Bind ViewModel.IsListView, Mode=TwoWay}", (string?)list.Attribute("IsChecked"));
        Assert.All(controls, control =>
        {
            Assert.Equal("{StaticResource DefaultToggleButtonStyle}", (string?)control.Attribute("Style"));
            Assert.Null(control.Attribute("Content"));
            Assert.Equal((string?)control.Attribute("AutomationProperties.Name"), (string?)control.Attribute("ToolTipService.ToolTip"));
        });
        Assert.Equal("\uE8A9", (string?)Assert.Single(grid.Elements()).Attribute("Glyph"));
        Assert.Equal("\uEA37", (string?)Assert.Single(list.Elements()).Attribute("Glyph"));
    }

    [Fact]
    public void ViewButtonsOverrideRadioMinimumsAndCenterSmallIconsInSquareTargets()
    {
        var document = Load();
        var controls = document.Descendants().Where(element => element.Name.LocalName == "RadioButton").ToList();
        Assert.Equal(2, controls.Count);
        Assert.All(controls, control =>
        {
            Assert.Equal("36", (string?)control.Attribute("Width"));
            Assert.Equal("36", (string?)control.Attribute("Height"));
            Assert.Equal("0", (string?)control.Attribute("MinWidth"));
            Assert.Equal("0", (string?)control.Attribute("MinHeight"));
            Assert.Equal("0", (string?)control.Attribute("Padding"));
            Assert.Equal("Center", (string?)control.Attribute("HorizontalContentAlignment"));
            Assert.Equal("Center", (string?)control.Attribute("VerticalContentAlignment"));
            var icon = Assert.Single(control.Elements());
            Assert.Equal("FontIcon", icon.Name.LocalName);
            Assert.Equal("14", (string?)icon.Attribute("FontSize"));
        });
        var import = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "EpicImportButton");
        Assert.Equal("36", (string?)import.Attribute("Height"));
        Assert.Equal("Center", (string?)import.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void AllLibraryActionsAreGroupedAtTheRight()
    {
        var document = Load();
        var actions = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "LibraryActions");
        Assert.Equal("Right", (string?)actions.Attribute("HorizontalAlignment"));
        Assert.All(actions.Elements().Single(element => element.Name.LocalName == "Grid.ColumnDefinitions").Elements(),
            column => Assert.Equal("Auto", (string?)column.Attribute("Width")));
        var import = Assert.Single(actions.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "EpicImportButton");
        Assert.Equal("2", (string?)import.Attribute("Grid.Column"));
    }

    [Fact]
    public void ToolbarStaysOutsideTheScrollingGamesInItsOwnAutoHeightRow()
    {
        var document = Load();
        var actions = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "LibraryActions");
        var scroller = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "LibraryScroller");
        Assert.Same(actions.Parent, scroller.Parent);
        Assert.DoesNotContain(actions.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("2", (string?)scroller.Attribute("Grid.Row"));
        Assert.Equal("Disabled", (string?)scroller.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        Assert.Single(scroller.Descendants(), element => element.Name.LocalName == "ItemsRepeater");
        var rows = Assert.Single(actions.Parent!.Elements(), element => element.Name.LocalName == "Grid.RowDefinitions").Elements().ToList();
        Assert.Equal("Auto", (string?)rows[0].Attribute("Height"));
        Assert.Equal("Auto", (string?)rows[1].Attribute("Height"));
        Assert.Equal("*", (string?)rows[2].Attribute("Height"));
        var main = PresentationXaml.Load("MainWindow.xaml");
        var frame = Assert.Single(main.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "ContentFrame");
        Assert.Equal("ContentFrame_Navigated", (string?)frame.Attribute("Navigated"));
        Assert.Equal("ContentScroller", (string?)frame.Parent!.Attribute(Xaml + "Name"));
    }

    [Fact]
    public void SpacingBelowActionsBelongsToTheStickyRowRatherThanTheScrollingGames()
    {
        var document = Load();
        var actions = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "LibraryActions");
        Assert.Equal("16,20,16,20", (string?)actions.Attribute("Margin"));
        var repeater = Assert.Single(document.Descendants(), element => element.Name.LocalName == "ItemsRepeater");
        Assert.Equal("0,0,0,20", (string?)repeater.Attribute("Margin"));
        var margins = document.Descendants().Where(element => (string?)element.Attribute("Target") == "ItemsRepeater.Margin")
            .Select(element => (string?)element.Attribute("Value")).ToList();
        Assert.Equal(["0,0,0,20", "16,0,16,20"], margins);
    }

    [Theory]
    [InlineData("GridView", "IsGridView", "UniformGridLayout", "GameCardTemplate", "Center")]
    [InlineData("ListView", "IsListView", "ListStackLayout", "GameListTemplate", "Stretch")]
    public void ViewStatesSwitchTheTemplateAndLayoutOfTheSameRepeater(
        string stateName, string property, string layout, string template, string alignment)
    {
        var document = Load();
        var state = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "VisualState" && (string?)element.Attribute(Xaml + "Name") == stateName);
        var trigger = Assert.Single(state.Descendants(), element => element.Name.LocalName == "StateTrigger");
        Assert.Equal($"{{x:Bind ViewModel.{property}, Mode=OneWay}}", (string?)trigger.Attribute("IsActive"));
        var setters = state.Descendants().Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(element => (string)element.Attribute("Target")!, element => (string?)element.Attribute("Value"));
        Assert.Equal($"{{StaticResource {layout}}}", setters["ItemsRepeater.Layout"]);
        Assert.Equal($"{{StaticResource {template}}}", setters["ItemsRepeater.ItemTemplate"]);
        Assert.Equal(alignment, setters["ItemsRepeater.HorizontalAlignment"]);
        var repeater = Assert.Single(document.Descendants(), element => element.Name.LocalName == "ItemsRepeater");
        Assert.Equal("{x:Bind ViewModel.GamesList, Mode=TwoWay}", (string?)repeater.Attribute("ItemsSource"));
    }

    [Theory]
    [InlineData("GameCardTemplate")]
    [InlineData("GameListTemplate")]
    public void BothViewsKeepGameNavigationAndOperationPresentation(string templateName)
    {
        var document = Load();
        var template = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Key") == templateName);
        var button = Assert.Single(template.Descendants(), element => element.Name.LocalName == "Button");
        Assert.Equal("GameButton_Click", (string?)button.Attribute("Click"));
        Assert.Equal("{x:Bind}", (string?)button.Attribute("DataContext"));
        Assert.Single(template.Descendants(), element => (string?)element.Attribute("Text") == "{x:Bind Title}");
        var status = Assert.Single(template.Descendants(), element => (string?)element.Attribute("Text") == "{x:Bind OperationStatus, Mode=OneWay}");
        Assert.Equal("{x:Bind HasActiveOperation, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}", (string?)status.Parent!.Attribute("Visibility"));
        var progress = Assert.Single(template.Descendants(), element => element.Name.LocalName == "ProgressBar");
        Assert.Equal("{x:Bind IsOperationIndeterminate, Mode=OneWay}", (string?)progress.Attribute("IsIndeterminate"));
        Assert.Equal("{x:Bind OperationProgress, Mode=OneWay}", (string?)progress.Attribute("Value"));
    }

    private static XDocument Load() => PresentationXaml.Load("LibraryPage.xaml");
}
