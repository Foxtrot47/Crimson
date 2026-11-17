using System.IO.Compression;
using System.Security.Cryptography;
using Crimson.Providers.Ubisoft;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftManifestTests
{
    private static readonly byte[] HashA = Convert.FromHexString("73AF6807E1D23A4B5C6D7E8F9012345678901234");
    private static readonly byte[] HashB = Convert.FromHexString("EDE115E6A9B8C7D6E5F40123456789ABCDEF0123");

    [Theory]
    [InlineData("73AF6807E1D23A4B5C6D7E8F9012345678901234", "slices_v3/j/73AF6807E1D23A4B5C6D7E8F9012345678901234")]
    [InlineData("1f00000000000000000000000000000000000000", "slices_v3/v/1F00000000000000000000000000000000000000")]
    [InlineData("2000000000000000000000000000000000000000", "slices_v3/0/2000000000000000000000000000000000000000")]
    public void Slice_path_folds_the_first_byte_into_32_upper_case_buckets(string sha1, string expected) =>
        Assert.Equal(expected, UbisoftManifest.SlicePath(sha1));

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("3DBE5E6A1C2B3A49586778899AABBCCDDEEFF00Z")]
    public void Slice_path_rejects_malformed_hashes(string sha1) =>
        Assert.Throws<ArgumentException>(() => UbisoftManifest.SlicePath(sha1));

    [Fact]
    public void Parse_reads_files_slices_content_hashes_and_offsets()
    {
        var contentA = SHA1.HashData("a"u8.ToArray());
        var contentB = SHA1.HashData("b"u8.ToArray());
        var manifest = UbisoftManifest.Parse(Container(Message(
            Chunk(10, optional: false, files: [
                FileEntry("\\bin\\Game.exe", 9000,
                    [contentA, contentB],
                    Slice(4096, 1100, HashA, offset: null),
                    Slice(4904, 1200, HashB, offset: null))]),
            Chunk(11, optional: true, language: "fr-FR", tags: "French", uplayIds: [3671, 3367], files: [
                FileEntry("data/fr.big", 600, [], Slice(600, 100, HashA, offset: 0))]),
            Integer(17, 1), Integer(21, 3), Text(20, "1.17"), Integer(22, 3))));

        Assert.Equal(UbisoftCompression.Zstd, manifest.Compression);
        Assert.True(manifest.IsCompressed);
        Assert.Equal("1.17", manifest.GameVersion);
        Assert.Equal(3u, manifest.Version);

        var game = Assert.Single(manifest.Chunks[0].Files);
        Assert.Equal("bin\\Game.exe", game.Path); // the leading separator would escape the install root
        Assert.Equal(9000ul, game.Size);
        Assert.Equal("73AF6807E1D23A4B5C6D7E8F9012345678901234", game.Slices[0].DownloadSha1);
        Assert.Equal(1100u, game.Slices[0].DownloadSize);
        Assert.Equal(contentA, game.Slices[0].ContentSha1);
        Assert.Equal(contentB, game.Slices[1].ContentSha1);
        Assert.Equal(0ul, game.Slices[0].Offset);
        Assert.Equal(4096ul, game.Slices[1].Offset); // absent offsets follow the previous slice

        var french = manifest.Chunks[1];
        Assert.True(french.Optional);
        Assert.Equal("fr-FR", french.Language);
        Assert.Equal("French", french.Tags);
        Assert.Equal(new[] { 3671u, 3367u }, french.UplayIds);
        Assert.Equal("data\\fr.big", french.Files[0].Path);
        Assert.Null(french.Files[0].Slices[0].ContentSha1);
    }

    [Fact]
    public void Parse_reads_install_runs_registry_and_languages()
    {
        var manifest = UbisoftManifest.Parse(Container(Message(
            Bytes(2, Message(Text(1, "$INSTALL_DIR\\EasyAntiCheat\\Setup.exe"), Text(2, "$INSTALL_DIR\\"),
                Text(3, "install 90 -console"), Text(4, "EasyAntiCheat"), Text(5, "EAC_install"), Integer(6, 1),
                Integer(9, 1), Integer(10, 0))),
            Bytes(5, Message(Text(1, "HKEY_LOCAL_MACHINE\\SOFTWARE\\Ubisoft\\Game\\Language"), Integer(2, 0),
                Bytes(3, Message(Text(1, "English"), Text(2, "en-US"))),
                Bytes(3, Message(Text(1, "French"), Text(2, "fr-FR"))))),
            Bytes(5, Message(Text(1, "HKEY_LOCAL_MACHINE\\SOFTWARE\\Ubisoft\\Game\\Version"), Integer(2, 1),
                Bytes(4, Message(Integer(1, 7))))),
            Bytes(6, Message(Text(1, "HKEY_LOCAL_MACHINE\\SOFTWARE\\Ubisoft\\Game"))),
            Bytes(3, Message(Text(1, "$INSTALL_DIR\\uninstall.exe"), Text(2, "$INSTALL_DIR"), Text(3, "/s"))),
            Bytes(23, Message(Text(1, "en-US"))),
            Bytes(23, Message(Text(1, "ru-RU"), Integer(2, 3368), Integer(2, 3672))))));

        var run = Assert.Single(manifest.InstallRuns);
        Assert.Equal("install 90 -console", run.Arguments);
        Assert.Equal("EAC_install", run.Identifier);
        Assert.Equal(1u, run.Version);
        Assert.True(run.IgnoreAllExitCodes);
        Assert.Null(run.Platform);

        Assert.False(manifest.InstallRegistry[0].IsNumber);
        Assert.Equal(new UbisoftRegistryValue("French", "fr-FR"), manifest.InstallRegistry[0].Values[1]);
        Assert.True(manifest.InstallRegistry[1].IsNumber);
        Assert.Equal(new UbisoftRegistryValue("7", null), Assert.Single(manifest.InstallRegistry[1].Values));
        Assert.Equal("HKEY_LOCAL_MACHINE\\SOFTWARE\\Ubisoft\\Game", Assert.Single(manifest.UninstallRegistry));
        Assert.Equal("/s", Assert.Single(manifest.UninstallRuns).Arguments);
        Assert.Equal(new[] { 3368u, 3672u }, manifest.Languages[1].UplayIds);
    }

    [Fact]
    public void Parse_accepts_packed_uplay_ids()
    {
        var packed = Bytes(8, [0x87, 0x1C, 0xD8, 0x1C]); // 3591, 3672
        var manifest = UbisoftManifest.Parse(Container(Message(Bytes(4, Message(Integer(1, 1), Integer(2, 0), packed)))));
        Assert.Equal(new[] { 3591u, 3672u }, manifest.Chunks[0].UplayIds);
    }

    [Theory]
    [InlineData("..\\outside.dll")]
    [InlineData("bin\\..\\..\\outside.dll")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("bin\\file.txt:stream")]
    public void Parse_rejects_paths_that_leave_the_install_root(string path) =>
        Assert.Throws<InvalidDataException>(() => UbisoftManifest.Parse(Container(Message(
            Chunk(1, false, files: [FileEntry(path, 10, [], Slice(10, 10, HashA, 0))])))));

    [Fact]
    public void Parse_rejects_a_slice_beyond_the_file_size() =>
        Assert.Throws<InvalidDataException>(() => UbisoftManifest.Parse(Container(Message(
            Chunk(1, false, files: [FileEntry("a.bin", 100, [], Slice(64, 10, HashA, 50))])))));

    [Fact]
    public void Parse_rejects_a_truncated_or_inconsistent_container()
    {
        var complete = Container(Message(Chunk(1, false, files: [FileEntry("a", 0, [])])));
        Assert.Throws<InvalidDataException>(() => UbisoftManifest.Parse(complete[..10]));

        var signatureTooLong = complete.ToArray();
        BitConverter.TryWriteBytes(signatureTooLong.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => UbisoftManifest.Parse(signatureTooLong));

        var bodyTooLong = complete.ToArray();
        BitConverter.TryWriteBytes(bodyTooLong.AsSpan(8), (uint)complete.Length);
        Assert.Throws<InvalidDataException>(() => UbisoftManifest.Parse(bodyTooLong));
    }

    [Fact]
    public void Parse_rejects_a_body_that_is_not_zlib()
    {
        var container = Container(Message(Chunk(1, false, files: [FileEntry("a", 0, [])])));
        container[12 + 16] = 0xFF; // first body byte: not a zlib header
        Assert.Throws<InvalidDataException>(() => UbisoftManifest.Parse(container));
    }

    [Fact]
    public void Inflate_refuses_a_zlib_bomb()
    {
        // 4 MB of zeros compresses to a few KB; the bound must stop it rather than allocate it.
        var container = Container(new byte[4 * 1024 * 1024]);
        Assert.True(container.Length < 64 * 1024);
        var error = Assert.Throws<InvalidDataException>(() => UbisoftManifest.Inflate(container, maximumLength: 1024 * 1024));
        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_reads_sizes_and_scales_the_download_estimate()
    {
        var metadata = UbisoftManifestMetadata.Parse(Container(Message(
            Integer(2, 1000), Integer(3, 500), Integer(7, 3962),
            Bytes(8, Message(Text(2, ""), Integer(3, 600))),
            Bytes(8, Message(Text(2, "en-US"), Integer(3, 100), Integer(5, 3367))),
            Bytes(8, Message(Text(2, "fr-FR"), Integer(3, 100), Integer(5, 3367))),
            Bytes(8, Message(Integer(3, 200), Integer(5, 3962))),
            Bytes(10, Message(Text(1, "en-US"))),
            Bytes(10, Message(Text(1, "fr-FR"))))));

        Assert.Equal(1000ul, metadata.BytesOnDisk);
        Assert.Equal(500ul, metadata.BytesToDownload);
        Assert.Equal(new[] { 3962u }, metadata.UplayIds);
        Assert.Null(metadata.Chunks[0].Language);
        Assert.Equal(new[] { "en-US", "fr-FR" }, metadata.Languages.Select(language => language.Code));

        // Base + English, ultra textures not owned: 700 on disk, half of it downloaded.
        Assert.Equal((350ul, 700ul), metadata.Estimate(["en-US"], new HashSet<uint> { 3367 }));
        Assert.Equal((500ul, 1000ul), metadata.Estimate(["en-us", "fr-FR"], new HashSet<uint> { 3367, 3962 }));
        // Region pack not owned: language chunks drop out.
        Assert.Equal((300ul, 600ul), metadata.Estimate(["en-US"], new HashSet<uint>()));
    }

    internal static byte[] Chunk(uint id, bool optional, string? language = null, string? tags = null,
        uint[]? uplayIds = null, byte[][]? files = null)
    {
        var parts = new List<byte[]> { Integer(1, id), Integer(2, optional ? 1UL : 0UL) };
        parts.AddRange((files ?? []).Select(file => Bytes(3, file)));
        if (language is not null) parts.Add(Text(5, language));
        if (tags is not null) parts.Add(Text(7, tags));
        parts.AddRange((uplayIds ?? []).Select(uplayId => Integer(8, uplayId)));
        return Bytes(4, Message([.. parts]));
    }

    internal static byte[] FileEntry(string path, ulong size, byte[][] contentHashes, params byte[][] slices)
    {
        var parts = new List<byte[]> { Text(1, path), Integer(2, size), Integer(3, 0) };
        parts.AddRange(contentHashes.Select(hash => Bytes(4, hash)));
        parts.AddRange(slices.Select(slice => Bytes(7, slice)));
        return Message([.. parts]);
    }

    internal static byte[] Slice(uint size, uint downloadSize, byte[] downloadSha1, ulong? offset)
    {
        var parts = new List<byte[]> { Integer(1, size), Integer(2, downloadSize), Bytes(3, downloadSha1) };
        if (offset is { } value) parts.Add(Integer(4, value));
        return Message([.. parts]);
    }

    internal static byte[] Container(byte[] body)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(body);
        return Wrap(compressed.ToArray());
    }

    private static byte[] Wrap(byte[] compressed)
    {
        var signature = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var container = new byte[12 + signature.Length + compressed.Length];
        BitConverter.TryWriteBytes(container.AsSpan(0), 1u);
        BitConverter.TryWriteBytes(container.AsSpan(4), (uint)signature.Length);
        BitConverter.TryWriteBytes(container.AsSpan(8), (uint)compressed.Length);
        signature.CopyTo(container, 12);
        compressed.CopyTo(container, 12 + signature.Length);
        return container;
    }
}
