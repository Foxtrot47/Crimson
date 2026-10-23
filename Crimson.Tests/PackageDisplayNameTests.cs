using System.Xml.Linq;
using Crimson.Resources;

namespace Crimson.Tests;

public sealed class PackageDisplayNameTests
{
    [Fact]
    public void StoreFacingNamesUseTheReservationWithoutChangingIdentityOrInAppBranding()
    {
        XNamespace package = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        var manifest = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Presentation", "Package.appxmanifest"));
        var identity = manifest.Root!.Element(package + "Identity")!;
        Assert.Equal("Foxtrot47.CrimsonLauncher", (string?)identity.Attribute("Name"));
        Assert.Equal("CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F", (string?)identity.Attribute("Publisher"));
        var properties = manifest.Root.Element(package + "Properties")!;
        Assert.Equal("Crimson Launcher", properties.Element(package + "DisplayName")!.Value);
        Assert.Equal("Foxtrot47", properties.Element(package + "PublisherDisplayName")!.Value);
        var application = Assert.Single(manifest.Descendants(package + "Application"));
        Assert.Equal("Crimson Launcher", (string?)application.Element(uap + "VisualElements")!.Attribute("DisplayName"));
        Assert.Equal("Crimson", UiStrings.Crimson);
        var mainWindow = PresentationXaml.Load("MainWindow.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var title = Assert.Single(mainWindow.Descendants(), element => (string?)element.Attribute(xaml + "Name") == "AppTitleTextBlock");
        Assert.Equal("Crimson", (string?)title.Attribute("Text"));
        var tray = PresentationXaml.Load("TrayIconView.xaml");
        Assert.Single(tray.Descendants(), element => (string?)element.Attribute("ToolTipText") == "Crimson");
    }
}
