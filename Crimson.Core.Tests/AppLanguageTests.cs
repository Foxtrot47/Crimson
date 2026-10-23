using System.Globalization;
using Crimson.Core;

namespace Crimson.Tests;

public sealed class AppLanguageTests
{
    [Theory]
    [InlineData(null, "de-DE", "de")]
    [InlineData("", "fr-CA", "fr")]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData(null, "pt-BR", "pt-BR")]
    [InlineData(null, "zh-CN", "zh-CN")]
    [InlineData(null, "ko-KR", "en")]
    [InlineData(null, "pt-PT", "en")]
    [InlineData("es", "de-DE", "es")]
    [InlineData("en", "fr-FR", "en")]
    [InlineData("PT-br", "en-US", "pt-BR")]
    [InlineData("invalid", "it-IT", "it")]
    public void PreferenceOverridesSystemAndUnsupportedSystemLanguagesFallBackToEnglish(
        string? preference, string systemLanguage, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(preference, CultureInfo.GetCultureInfo(systemLanguage)).Name);
    }

    [Fact]
    public void OptionsStartWithSystemAndUseValidUniqueCultureCodes()
    {
        var options = AppLanguage.GetOptions();
        Assert.Equal(string.Empty, options[0].Code);
        Assert.Equal(9, options.Count);
        Assert.Equal(options.Count, options.Select(option => option.Code).Distinct().Count());
        foreach (var option in options.Skip(1))
            Assert.Equal(option.Code, CultureInfo.GetCultureInfo(option.Code).Name);
        Assert.Contains(options, option => option.Code == "ja");
        Assert.DoesNotContain(options, option => option.Code == "jp");
    }
}
