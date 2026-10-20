using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Xml.Linq;
using Crimson.Resources;

namespace Crimson.Tests;

public sealed class UiStringsTests
{
    private static readonly ResourceManager Resources = new("Crimson.Resources.UiStrings", typeof(UiStrings).Assembly);

    [Fact]
    public void EveryEnglishResourceHasATypedAccessorAndUsageComment()
    {
        var catalog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "UiStrings.resx"));
        var entries = catalog.Root!.Elements("data").ToList();
        var keys = entries.Select(entry => (string)entry.Attribute("name")!).Order(StringComparer.Ordinal).ToList();
        var accessors = typeof(UiStrings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys, accessors);
        foreach (var entry in entries)
        {
            var key = (string)entry.Attribute("name")!;
            var value = Assert.IsType<string>(Resources.GetString(key, CultureInfo.InvariantCulture));
            Assert.Equal(entry.Element("value")!.Value, value);
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.False(string.IsNullOrWhiteSpace(entry.Element("comment")?.Value));
            if (!value.Contains('{')) continue;
            var format = CompositeFormat.Parse(value);
            var arguments = Enumerable.Repeat<object>(12.5, format.MinimumArgumentCount).ToArray();
            Assert.NotNull(string.Format(CultureInfo.InvariantCulture, format, arguments));
        }
    }

    [Theory]
    [InlineData("InstallationError")]
    [InlineData("DownloadTaskFailed")]
    [InlineData("DownloadWorkerFailed")]
    [InlineData("IoTaskFailed")]
    [InlineData("FileWorkerFailed")]
    [InlineData("CancelInstall")]
    [InlineData("VerificationFailedFilesFormat")]
    [InlineData("TheDLCLibraryChangedReopenPropertiesAndTryAgain")]
    [InlineData("TheDLCInstallationLocationIsUnavailable")]
    [InlineData("OperationError")]
    [InlineData("OperationFailed")]
    [InlineData("IssueReportBodyFormat")]
    public void InternalReasonsExceptionsAndGeneratedReportsAreNotUiResources(string key)
    {
        Assert.Null(Resources.GetString(key, CultureInfo.InvariantCulture));
        Assert.Null(typeof(UiStrings).GetProperty(key, BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void CatalogDeclaresTheStandardResxReaderAndWriterForResourceEditors()
    {
        var catalog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "UiStrings.resx"));
        var headers = catalog.Root!.Elements("resheader").ToDictionary(
            header => (string)header.Attribute("name")!, header => header.Element("value")!.Value);
        Assert.Equal("text/microsoft-resx", headers["resmimetype"]);
        Assert.Equal("2.0", headers["version"]);
        const string assembly = ", System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
        Assert.Equal("System.Resources.ResXResourceReader" + assembly, headers["reader"]);
        Assert.Equal("System.Resources.ResXResourceWriter" + assembly, headers["writer"]);
    }

    [Fact]
    public void MissingTranslationFallsBackToEnglishWithoutExposingResourceKeys()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            Assert.Equal("Installed only", UiStrings.InstalledOnly);
            Assert.Equal("Import from Epic", UiStrings.ImportFromEpic);
            Assert.Equal("No Downloads in Queue", UiStrings.NoDownloadsInQueueTitle);
            Assert.Equal("0m", UiStrings.ZeroMinutes);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("ja")]
    [InlineData("pt-BR")]
    [InlineData("zh-CN")]
    public void TranslationFilesLoadAsSatellitesWithValidCompositeFormats(string language)
    {
        var catalog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", $"UiStrings.{language}.resx"));
        var culture = CultureInfo.GetCultureInfo(language);
        var entries = catalog.Root!.Elements("data").ToList();
        Assert.Equal(entries.Count, entries.Select(entry => (string)entry.Attribute("name")!).Distinct().Count());
        var neutralCatalog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "UiStrings.resx"));
        Assert.Equal(
            neutralCatalog.Root!.Elements("data").Select(entry => (string)entry.Attribute("name")!).Order(StringComparer.Ordinal),
            entries.Select(entry => (string)entry.Attribute("name")!).Order(StringComparer.Ordinal));
        foreach (var entry in entries)
        {
            var key = (string)entry.Attribute("name")!;
            var value = entry.Element("value")!.Value;
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.Equal(value, Resources.GetString(key, culture));
            var neutral = Resources.GetString(key, CultureInfo.InvariantCulture);
            Assert.NotNull(neutral);
            Assert.Equal(CompositeFormat.Parse(neutral).MinimumArgumentCount, CompositeFormat.Parse(value).MinimumArgumentCount);
            var format = CompositeFormat.Parse(value);
            Assert.NotNull(string.Format(culture, format, Enumerable.Repeat<object>(12.5, format.MinimumArgumentCount).ToArray()));
        }
        Assert.NotEqual(
            Resources.GetString("LanguageRestartRequired", CultureInfo.InvariantCulture),
            Resources.GetString("LanguageRestartRequired", culture));
    }

    [Fact]
    public void FormatsUseCurrentNumericCultureAndKeepUserTextAsAnArgument()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("0,5 MiB/s", UiStrings.Format(UiStrings.RateMiBFormat, 0.5));
            Assert.Equal("Play {untrusted title}", UiStrings.Format(UiStrings.PlayGameFormat, "{untrusted title}"));
            Assert.Equal("Verified 12 files: 3 require repair", UiStrings.Format(UiStrings.ImportRepairFilesFormat, 12, 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
