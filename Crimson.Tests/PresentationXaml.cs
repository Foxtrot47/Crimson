using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Crimson.Resources;

namespace Crimson.Tests;

internal static partial class PresentationXaml
{
    public static XDocument Load(string name)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Presentation", name));
        foreach (var attribute in document.Descendants().Attributes())
        {
            var match = ResourceBinding().Match(attribute.Value);
            if (!match.Success) continue;
            var property = typeof(UiStrings).GetProperty(match.Groups[1].Value, BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(property);
            attribute.Value = Assert.IsType<string>(property.GetValue(null));
        }
        return document;
    }

    [GeneratedRegex(@"^\{x:Bind strings:UiStrings\.([A-Za-z][A-Za-z0-9_]*)\}$")]
    private static partial Regex ResourceBinding();
}
