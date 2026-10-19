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

    private static XDocument LoadPage() => XDocument.Load(
        Path.Combine(AppContext.BaseDirectory, "Presentation", "DownloadsPage.xaml"));

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);
}
