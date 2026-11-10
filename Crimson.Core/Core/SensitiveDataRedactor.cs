using System;
using System.Text.RegularExpressions;

namespace Crimson.Core;

public static partial class SensitiveDataRedactor
{
    public const string Redacted = "[REDACTED]";

    public static string UriWithoutQuery(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return Redacted;

        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    public static string Fields(string value) => SensitiveFieldPattern().Replace(
        value,
        match => $"{match.Groups[1].Value}{match.Groups[2].Value}{Redacted}");

    [GeneratedRegex(
        "(?i)(access_token|refresh_token|exchange_code|authorization|password|code)([\\\"']?\\s*[:=]\\s*[\\\"']?)([^&\\s\\\"',}]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveFieldPattern();
}
