using System.Text;

namespace Crimson.Utils;

public sealed class ManifestRelativePath
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidCharacters = ['\0', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string[] _segments;

    private ManifestRelativePath(string[] segments)
    {
        _segments = segments;
        Segments = segments;
        Value = string.Join('/', segments);
    }

    public IReadOnlyList<string> Segments { get; }

    public string Value { get; }

    public static ManifestRelativePath? ParseOptional(string? value) =>
        string.IsNullOrEmpty(value) ? null : Parse(value);

    public static ManifestRelativePath Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Manifest path is empty.");
        if (value[0] is '/' or '\\' ||
            (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
            throw new InvalidDataException($"Manifest path must be relative: {value}");

        var sourceSegments = value.Split(['/', '\\'], StringSplitOptions.None);
        var segments = new string[sourceSegments.Length];
        for (var index = 0; index < sourceSegments.Length; index++)
        {
            var segment = sourceSegments[index];
            if (segment.Length == 0 || segment is "." or "..")
                throw new InvalidDataException($"Manifest path contains an invalid segment: {value}");
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException($"Manifest path contains an invalid filename: {value}");
            if (segment.IndexOfAny(InvalidCharacters) >= 0 || segment.Any(char.IsControl))
                throw new InvalidDataException($"Manifest path contains an invalid filename: {value}");

            var normalized = segment.Normalize(NormalizationForm.FormC);
            var extensionIndex = normalized.IndexOf('.');
            var deviceName = extensionIndex < 0 ? normalized : normalized[..extensionIndex];
            if (ReservedDeviceNames.Contains(deviceName))
                throw new InvalidDataException($"Manifest path uses a reserved device name: {value}");
            segments[index] = normalized;
        }

        return new ManifestRelativePath(segments);
    }

    public string ToPlatformPath() => Path.Combine(_segments);

    public override string ToString() => Value;
}
