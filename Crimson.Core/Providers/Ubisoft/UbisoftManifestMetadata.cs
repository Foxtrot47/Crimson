using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft;

/// <summary>Size of one manifest chunk, in manifest chunk order; gated like <see cref="UbisoftManifestChunk"/>.</summary>
public sealed record UbisoftMetadataChunk(uint? UplayId, string? Language, ulong BytesOnDisk, IReadOnlyList<uint> UplayIds);

/// <summary>
/// The small <c>.metadata</c> companion of a manifest (<c>mg.protocol.download.ManifestMetaData</c>), in the same
/// container format. Used to size an install before downloading the full manifest.
/// </summary>
public sealed class UbisoftManifestMetadata
{
    private UbisoftManifestMetadata() { }

    public ulong BytesOnDisk { get; private init; }
    public ulong BytesToDownload { get; private init; }
    public IReadOnlyList<uint> UplayIds { get; private init; } = [];
    public IReadOnlyList<UbisoftMetadataChunk> Chunks { get; private init; } = [];
    public IReadOnlyList<UbisoftManifestLanguage> Languages { get; private init; } = [];

    public static UbisoftManifestMetadata Parse(byte[] container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var body = UbisoftWire.Read(UbisoftManifest.Inflate(container));
        return new UbisoftManifestMetadata
        {
            BytesOnDisk = body.Number(2),
            BytesToDownload = body.Number(3),
            UplayIds = UbisoftManifest.UInt32s(body, 7),
            Chunks = UbisoftManifest.Repeated(body, 8).Select(ParseChunk).ToArray(),
            Languages = UbisoftManifest.ParseLanguages(body, 10),
        };
    }

    /// <summary>
    /// Download and install size of the chunks a selection would install. The install size is exact per chunk;
    /// the download size is scaled by the build's overall <see cref="BytesToDownload"/>/<see cref="BytesOnDisk"/> ratio.
    /// </summary>
    public (ulong Download, ulong Install) Estimate(IReadOnlyCollection<string> languages, IReadOnlySet<uint> owned)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(owned);
        if (Chunks.Count == 0) return (BytesToDownload, BytesOnDisk);
        ulong install = 0;
        foreach (var chunk in Chunks)
            if (UbisoftInstallSelection.Passes(chunk.UplayIds, chunk.Language, languages, owned))
                install += chunk.BytesOnDisk;
        if (BytesOnDisk == 0) return (install, install);
        var download = (UInt128)install * BytesToDownload / BytesOnDisk;
        return ((ulong)download, install);
    }

    private static UbisoftMetadataChunk ParseChunk(IReadOnlyList<Field> chunk)
    {
        uint? uplayId = chunk.Has(1) ? UbisoftManifest.UInt32(chunk, 1) : null;
        var uplayIds = UbisoftManifest.UInt32s(chunk, 5);
        if (uplayIds.Count == 0 && uplayId is > 0) uplayIds = [uplayId.Value];
        return new UbisoftMetadataChunk(uplayId, UbisoftManifest.OptionalText(chunk, 2), chunk.Number(3), uplayIds);
    }
}
