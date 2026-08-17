using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Crimson.Core;

public sealed class EpicAuthorizationCodeGate
{
    private readonly HashSet<string> _acceptedCodeDigests = new(StringComparer.Ordinal);

    public bool TryAccept(string? source, string? payload, out string authorizationCode)
    {
        authorizationCode = string.Empty;
        if (!EpicEndpointPolicy.IsAllowedLoginOrigin(source) ||
            string.IsNullOrWhiteSpace(payload) ||
            payload.Length > EpicLoginMessageGate.MaximumMessageLength)
            return false;

        string? value;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            value = root.ValueKind switch
            {
                JsonValueKind.String => root.GetString(),
                JsonValueKind.Object when
                    root.TryGetProperty("authorizationCode", out var code) &&
                    code.ValueKind == JsonValueKind.String => code.GetString(),
                _ => null
            };
        }
        catch (JsonException)
        {
            value = payload.Trim().Trim('"');
        }

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > EpicLoginMessageGate.MaximumCodeLength ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        if (!_acceptedCodeDigests.Add(digest))
            return false;

        authorizationCode = value;
        return true;
    }
}
