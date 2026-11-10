using System.Text.RegularExpressions;
using System;

namespace Crimson.Providers;

/// <summary>A launcher-local identifier qualified by its registered provider.</summary>
public sealed record GameId
{
    public const string LegacyProviderId = "epic";
    public string ProviderId { get; }
    public string LocalId { get; }

    public GameId(string providerId, string localId)
    {
        ValidateProviderId(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        if (localId.Length > 1024 || localId.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid launcher game identifier.", nameof(localId));
        ProviderId = providerId;
        LocalId = localId;
    }

    public static void ValidateProviderId(string providerId)
    {
        if (providerId is null || !Regex.IsMatch(providerId, @"\A[a-z][a-z0-9-]{0,63}\z"))
            throw new ArgumentException("Provider IDs must be lowercase slugs.", nameof(providerId));
    }

    public override string ToString() => $"{ProviderId}:{Uri.EscapeDataString(LocalId)}";

    // Unqualified IDs are accepted only for existing Epic shortcuts/navigation.
    public static GameId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(':');
        return separator < 0
            ? new GameId(LegacyProviderId, value)
            : new GameId(value[..separator], Uri.UnescapeDataString(value[(separator + 1)..]));
    }
}
