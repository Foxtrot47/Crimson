using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Core;

public sealed record EpicInstallScan(IReadOnlyList<EpicInstallation> Installations, int UnreadableCount);

public sealed class EpicInstallDiscovery(string manifestDirectory)
{
    private const int MaximumRecordBytes = 1024 * 1024;
    private const int MaximumRecords = 4096;

    public Task<EpicInstallScan> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanAsync(cancellationToken), cancellationToken);

    private async Task<EpicInstallScan> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = GetRecordFiles();
        if (files.Count > MaximumRecords)
            throw new InvalidDataException("Too many Epic installation records were found.");
        var installations = new List<EpicInstallation>();
        var unreadable = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var installation = await ReadRecordAsync(Path.GetFileName(file), cancellationToken);
                if (installation is not null) installations.Add(installation);
            }
            catch (Exception error) when (IsRecordError(error))
            {
                unreadable++;
            }
        }
        var groups = installations.GroupBy(item => item.AppName, StringComparer.Ordinal).ToList();
        var unique = groups.Where(group => group.All(item => SameInstallation(group.First(), item)))
            .Select(group => group.First()).ToList();
        return new(unique, unreadable + groups.Count - unique.Count);
    }

    private List<string> GetRecordFiles()
    {
        try
        {
            return Directory.EnumerateFiles(manifestDirectory, "*.item")
                .Take(MaximumRecords + 1).Order(StringComparer.Ordinal).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public Task<EpicInstallation> RevalidateAsync(EpicInstallation installation, CancellationToken cancellationToken) =>
        Task.Run(() => ReadCurrentInstallationAsync(installation, cancellationToken), cancellationToken);

    private async Task<EpicInstallation> ReadCurrentInstallationAsync(EpicInstallation installation, CancellationToken cancellationToken)
    {
        try
        {
            var current = await ReadRecordAsync(installation.RecordName, cancellationToken);
            if (current is null || !SameInstallation(current, installation))
                throw new InvalidDataException("The Epic installation changed. Detect installed games again.");
            return current;
        }
        catch (Exception error) when (IsRecordError(error))
        {
            throw new InvalidDataException("The Epic installation could not be read. Detect installed games again.");
        }
    }

    public Task<byte[]> ReadManifestAsync(EpicInstallation installation, CancellationToken cancellationToken) =>
        Task.Run(() => ReadInstalledManifestAsync(installation, cancellationToken), cancellationToken);

    private async Task<byte[]> ReadInstalledManifestAsync(EpicInstallation installation, CancellationToken cancellationToken)
    {
        await ReadCurrentInstallationAsync(installation, cancellationToken);
        try
        {
            return await ReadBoundedAsync(GetManifestPath(installation), EpicProtocolLimits.MaximumManifestBytes, cancellationToken);
        }
        catch (Exception error) when (IsRecordError(error))
        {
            throw new InvalidDataException("The installed Epic manifest could not be read. Detect installed games again.");
        }
    }

    private async Task<EpicInstallation?> ReadRecordAsync(string recordName, CancellationToken cancellationToken)
    {
        if (Path.GetFileName(recordName) != recordName || Path.GetExtension(recordName) != ".item")
            throw new InvalidDataException("Invalid Epic installation record.");
        var path = InstallPathPolicy.ResolveFile(manifestDirectory, recordName);
        var bytes = await ReadBoundedAsync(path, MaximumRecordBytes, cancellationToken);
        ReadOnlyMemory<byte> payload = bytes;
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) payload = payload[Encoding.UTF8.Preamble.Length..];
        using var document = JsonDocument.Parse(payload);
        var json = document.RootElement;
        if (json.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.GetBoolean()) return null;
        var installation = new EpicInstallation(recordName, RequiredString(json, "AppName"),
            RequiredString(json, "CatalogNamespace"), RequiredString(json, "CatalogItemId"),
            RequiredString(json, "InstallLocation"), RequiredString(json, "InstallationGuid"),
            RequiredString(json, "AppVersionString"), ReadInstallTags(json));
        ValidateLocation(installation);
        if (!File.Exists(GetManifestPath(installation)))
            throw new InvalidDataException("The installed Epic manifest is unavailable.");
        return installation;
    }

    private static bool SameInstallation(EpicInstallation first, EpicInstallation second) =>
        first with { RecordName = string.Empty, InstallTags = null } ==
        second with { RecordName = string.Empty, InstallTags = null } &&
        (first.InstallTags is null) == (second.InstallTags is null) &&
        (first.InstallTags ?? []).SequenceEqual(second.InstallTags ?? []);

    private static string[]? ReadInstallTags(JsonElement json)
    {
        if (!json.TryGetProperty("InstallTags", out var tags)) return null;
        return tags.EnumerateArray().Select(tag => tag.GetString() ??
            throw new InvalidDataException("Invalid Epic install tags.")).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateLocation(EpicInstallation installation)
    {
        if (!Path.IsPathFullyQualified(installation.InstallLocation) || !Directory.Exists(installation.InstallLocation))
            throw new InvalidDataException("The Epic installation folder is unavailable.");
        if (!Guid.TryParseExact(installation.InstallationGuid, "N", out _) &&
            !Guid.TryParseExact(installation.InstallationGuid, "D", out _))
            throw new InvalidDataException("Invalid Epic installation identifier.");
    }

    private static string GetManifestPath(EpicInstallation installation) =>
        InstallPathPolicy.ResolveFile(installation.InstallLocation,
            Path.Combine(".egstore", installation.InstallationGuid + ".manifest"));

    private static string RequiredString(JsonElement json, string name)
    {
        var value = json.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Incomplete Epic installation record.");
        return value;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length < 1 || length > maximumBytes)
            throw new InvalidDataException("The Epic installation record exceeds the supported size.");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static bool IsRecordError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException;
}
