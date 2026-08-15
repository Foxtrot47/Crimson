namespace Crimson.Core;

public readonly record struct InstallContentSize(double DownloadBytes, double InstallBytes);

public static class InstallContentSizeCalculator
{
    public static InstallContentSize Calculate(
        string baseAppId,
        IReadOnlyDictionary<string, InstallContentSize> sizes,
        IEnumerable<string> selectedDlcAppIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAppId);
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(selectedDlcAppIds);
        if (!sizes.TryGetValue(baseAppId, out var total))
            throw new KeyNotFoundException($"Base game size is unavailable: {baseAppId}");

        foreach (var appId in selectedDlcAppIds.Distinct(StringComparer.Ordinal))
        {
            if (sizes.TryGetValue(appId, out var size))
                total = new InstallContentSize(
                    checked(total.DownloadBytes + size.DownloadBytes),
                    checked(total.InstallBytes + size.InstallBytes));
        }

        return total;
    }
}
