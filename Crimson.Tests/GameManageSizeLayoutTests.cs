using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class GameManageSizeLayoutTests
{
    [Fact]
    public void ImperativelyLoadedInstalledSizeHasNoBindingThatCanOverwriteItsResult()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = PresentationXaml.Load("GameManageDialog.xaml");
        var size = Assert.Single(page.Descendants(), element => (string?)element.Attribute(xaml + "Name") == "InstalledSizeText");
        Assert.Equal("TextBlock", size.Name.LocalName);
        Assert.Null(size.Attribute("Text"));
    }
}
