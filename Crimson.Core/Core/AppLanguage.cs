using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Crimson.Resources;

namespace Crimson.Core;

public sealed record LanguageOption(string Code, string DisplayName);

public static class AppLanguage
{
    private static readonly LanguageOption[] Translations =
    [
        new("en", "English"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("ja", "日本語"),
        new("pt-BR", "Português (Brasil)"),
        new("zh-CN", "简体中文")
    ];

    public static IReadOnlyList<LanguageOption> GetOptions() =>
        [new(string.Empty, UiStrings.SystemLanguage), .. Translations];

    public static string? NormalizePreference(string? code) => Translations
        .FirstOrDefault(option => string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase))?.Code;

    public static CultureInfo Resolve(string? preference, CultureInfo systemLanguage)
    {
        var selected = NormalizePreference(preference);
        if (selected is not null) return CultureInfo.GetCultureInfo(selected);
        for (var culture = systemLanguage; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            var supported = NormalizePreference(culture.Name);
            if (supported is not null) return CultureInfo.GetCultureInfo(supported);
        }
        return CultureInfo.GetCultureInfo("en");
    }

    public static void Apply(string? preference)
    {
        var culture = Resolve(preference, CultureInfo.CurrentUICulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
