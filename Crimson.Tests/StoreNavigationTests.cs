using System.IO;
using System.Xml.Linq;

namespace Crimson.Tests;

public sealed class StoreNavigationTests
{
    [Fact]
    public void EveryNavigationTargetPageHasXamlSoTheShellCanCreateIt()
    {
        // Frame.Navigate faults inside XAML for a page type the app's XAML metadata does not know,
        // so a page reachable from the nav pane must exist as markup, not only as code.
        var targets = PresentationXaml.Load("MainWindow.xaml")
            .Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(element => (string?)element.Attribute("Tag"))
            .OfType<string>()
            .Where(tag => tag.StartsWith("Crimson.Views.", StringComparison.Ordinal))
            .Select(tag => tag["Crimson.Views.".Length..]);

        Assert.NotEmpty(targets);
        Assert.All(targets, name => Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "Presentation", $"{name}.xaml")),
            $"{name} is a navigation target with no XAML file."));
    }
}
