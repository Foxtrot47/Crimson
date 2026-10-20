using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Crimson.Tests;

public sealed partial class LocalizationLayoutTests
{
    private static readonly HashSet<string> TextProperties = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "Description", "PlaceholderText", "ToolTipService.ToolTip",
        "ToolTipText", "AutomationProperties.Name", "AutomationProperties.HelpText",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText"
    };

    [Fact]
    public void AllLocalXamlTextUsesResourcesOrDataBindings()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Presentation"), "*.xaml");
        Assert.NotEmpty(files);
        var resourceReferences = 0;
        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            foreach (var attribute in document.Descendants().Attributes().Where(attribute => TextProperties.Contains(attribute.Name.LocalName)))
            {
                var value = attribute.Value;
                if (string.IsNullOrWhiteSpace(value) || !value.Any(char.IsLetter)) continue;
                Assert.True(value.StartsWith('{'), $"Hardcoded text in {Path.GetFileName(file)}: {attribute.Name}");
                if (!value.StartsWith("{x:Bind strings:UiStrings.", StringComparison.Ordinal)) continue;
                Assert.Matches(ResourceBinding(), value);
                resourceReferences++;
            }
            // Resolve every resource, including bindings inside item templates and the page root.
            PresentationXaml.Load(Path.GetFileName(file));
            Assert.DoesNotContain(document.Descendants().OfType<XElement>().SelectMany(element => element.Nodes().OfType<XText>()),
                text => (text.Parent?.Name.LocalName is "TextBlock" or "Run") && !string.IsNullOrWhiteSpace(text.Value));
        }
        Assert.True(resourceReferences > 100);
    }

    [GeneratedRegex(@"^\{x:Bind strings:UiStrings\.[A-Za-z][A-Za-z0-9_]*\}$")]
    private static partial Regex ResourceBinding();
}
