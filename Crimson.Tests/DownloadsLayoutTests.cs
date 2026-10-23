using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class DownloadsLayoutTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ActiveOperationsShareOneUnconditionalChartAndDetailsSurface()
    {
        var page = LoadPage();
        var active = Named(page, "ActiveDownloadSection");
        var chart = Named(page, "ActivityChart");
        var details = Named(page, "TransferDetails");
        var metrics = Named(page, "TransferMetrics");

        Assert.Contains("ShowCurrentDownload", (string)active.Attribute("Visibility")!);
        Assert.Same(active, chart.Parent);
        Assert.Same(active, details.Parent);
        Assert.Same(details, metrics.Parent);
        Assert.Null(chart.Attribute("Visibility"));
        Assert.Null(details.Attribute("Visibility"));
        Assert.Null(metrics.Attribute("Visibility"));
        Assert.Equal(["Compact", "Wide"], page.Descendants(Ui + "VisualState")
            .Select(state => (string)state.Attribute(Xaml + "Name")!));
    }

    [Fact]
    public void AllOperationControlsRemainInsideTheSharedDetailsPanel()
    {
        var page = LoadPage();
        var details = Named(page, "TransferDetails");
        Assert.Same(details, Named(page, "ActiveProgress").Parent);
        Assert.Contains(details.Descendants(Ui + "TextBlock"), element =>
            ((string?)element.Attribute("Text"))?.Contains("CurrentInstallItemName", StringComparison.Ordinal) == true);
        var commands = details.Descendants(Ui + "Button")
            .Select(button => (string?)button.Attribute("Command")).OfType<string>().ToList();

        Assert.Equal(3, commands.Count);
        Assert.Contains(commands, command => command.Contains("PauseInstallCommand", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("ResumeInstallCommand", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("CancelInstallCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void FailureActionsShareOneWrappingParagraph()
    {
        var history = Named(LoadPage(), "HistoryItemsList");
        var actions = Assert.Single(history.Descendants(Ui + "RichTextBlock"));
        var paragraph = Assert.Single(actions.Elements(Ui + "Paragraph"));
        var buttons = paragraph.Elements(Ui + "InlineUIContainer")
            .Select(container => Assert.Single(container.Elements(Ui + "Button"))).ToList();

        Assert.Equal("False", (string?)actions.Attribute("IsTextSelectionEnabled"));
        Assert.Equal(["RetryOperation_Click", "ReportProblem_Click", "CopyDiagnostics_Click"],
            buttons.Select(button => (string?)button.Attribute("Click")));
        Assert.Equal("{x:Bind CanRetry}", (string?)buttons[0].Attribute("IsEnabled"));
        Assert.Equal(3, history.Descendants(Ui + "Button").Count());
        Assert.Equal(2, paragraph.Elements(Ui + "Run").Count(run => (string?)run.Attribute("Text") == " "));
    }

    [Fact]
    public void IdleSidebarDownloadsUseTheSharedNavigationRowHeight()
    {
        var control = PresentationXaml.Load("CurrentDownloadControl.xaml");
        var layout = Assert.Single(control.Root!.Elements(Ui + "Grid"));

        Assert.Null(layout.Attribute("Height"));
        Assert.Null(layout.Attribute("MinHeight"));
        Assert.Equal("Auto", (string?)Assert.Single(layout.Descendants(Ui + "RowDefinition"),
            row => row.Parent?.Parent == layout).Attribute("Height"));
        Assert.Equal("Collapsed", (string?)Named(control, "DownloadStatus").Attribute("Visibility"));
        Assert.Equal("100", (string?)Named(control, "DownloadStatus").Attribute("MinHeight"));
    }

    [Fact]
    public void SidebarDownloadsKeepAProgressSizedIconWithoutBadges()
    {
        var window = PresentationXaml.Load("MainWindow.xaml");
        var downloads = Named(window, "DownloadsNavItem");
        var icon = Named(window, "DownloadIcon");

        Assert.Empty(window.Descendants(Ui + "InfoBadge"));
        Assert.Equal(Ui + "NavigationViewItem.Icon", icon.Parent!.Name);
        Assert.Same(downloads, icon.Parent.Parent);
        Assert.Equal("32", (string?)icon.Attribute("Width"));
        Assert.Equal("32", (string?)icon.Attribute("Height"));
        Assert.Equal("16", (string?)icon.Attribute("FontSize"));
        var standardRowHeight = window.Descendants(Xaml + "Double").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "NavigationViewItemOnLeftMinHeight").Value;
        Assert.Equal(standardRowHeight, (string?)downloads.Attribute("Height"));
    }

    private static XDocument LoadPage() => XDocument.Load(
        Path.Combine(AppContext.BaseDirectory, "Presentation", "DownloadsPage.xaml"));

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);
}
