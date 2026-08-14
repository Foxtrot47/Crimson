using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class ManifestParsingTests
{
    [Fact]
    public void ManifestRead_RejectsInvalidMagic()
    {
        var data = new byte[41];

        var error = Assert.Throws<InvalidOperationException>(() => Manifest.Read(data));

        Assert.Equal("No header magic!", error.Message);
    }

    [Fact]
    public void ManifestRead_ReadsUncompressedPayload()
    {
        var payload = Encoding.ASCII.GetBytes("manifest payload");
        var data = BuildManifest(payload);

        var manifest = Manifest.Read(data);

        Assert.False(manifest.Compressed);
        Assert.Equal(payload.Length, manifest.SizeUncompressed);
        Assert.Equal(payload, manifest.Data);
    }

    [Fact]
    public void ManifestRead_ReadsCompressedPayloadAndValidatesHash()
    {
        var payload = Encoding.ASCII.GetBytes("compressed manifest payload");
        var data = BuildManifest(payload, compressed: true);

        var manifest = Manifest.Read(data);

        Assert.True(manifest.Compressed);
        Assert.Equal(payload, manifest.Data);
    }

    [Fact]
    public void ManifestRead_RejectsCompressedPayloadWithInvalidHash()
    {
        var payload = Encoding.ASCII.GetBytes("compressed manifest payload");
        var data = BuildManifest(payload, compressed: true);
        data[16] ^= 0xFF;

        var error = Assert.Throws<InvalidOperationException>(() => Manifest.Read(data));

        Assert.Equal("Hash does not match!", error.Message);
    }

    [Fact]
    public void FileManifestListRead_ReadsFileAndChunkPart()
    {
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);

        writer.Write(0); // section size, patched below
        writer.Write((byte)0); // version
        writer.Write(1); // file count
        WriteString(writer, "folder/file.bin");
        WriteString(writer, string.Empty); // symlink target
        writer.Write(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        writer.Write((byte)0); // flags
        writer.Write(0); // install tag count
        writer.Write(1); // chunk part count
        writer.Write(28); // chunk part serialized size
        writer.Write(1);
        writer.Write(2);
        writer.Write(3);
        writer.Write(4);
        writer.Write(7); // offset in chunk
        writer.Write(11); // part size
        writer.Flush();

        body.Position = 0;
        writer.Write(checked((int)body.Length));
        writer.Flush();
        body.Position = 0;

        var list = FileManifestList.Read(body);

        var file = Assert.Single(list.Elements);
        Assert.Equal("folder/file.bin", file.Filename);
        Assert.Equal(11, file.FileSize);
        var part = Assert.Single(file.ChunkParts);
        Assert.Equal(new[] { 1, 2, 3, 4 }, part.Guid);
        Assert.Equal(7, part.Offset);
        Assert.Equal(11, part.Size);
        Assert.Equal(0, part.FileOffset);
    }

    [Fact]
    public void CdlRead_ReadsChunkMetadata()
    {
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);

        writer.Write(66); // section size
        writer.Write((byte)0); // version
        writer.Write(1); // chunk count
        writer.Write(1);
        writer.Write(2);
        writer.Write(3);
        writer.Write(4);
        writer.Write(123L); // rolling hash
        writer.Write(Enumerable.Repeat((byte)0xAB, 20).ToArray());
        writer.Write((byte)9); // group
        writer.Write(1_048_576); // window size
        writer.Write(512L); // compressed file size
        writer.Flush();
        body.Position = 0;

        var cdl = CDL.Read(body);

        var chunk = Assert.Single(cdl.Elements);
        Assert.Equal(new[] { 1, 2, 3, 4 }, chunk.Guid);
        Assert.Equal(123L, chunk.Hash);
        Assert.Equal(9, chunk.GroupNum);
        Assert.Equal(1_048_576, chunk.WindowSize);
        Assert.Equal(512L, chunk.FileSize);
        Assert.StartsWith("ChunksV4/09/", chunk.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChunkReadBuffer_ReadsPayload(bool compressed)
    {
        var payload = Encoding.ASCII.GetBytes("chunk data");
        var data = BuildChunk(payload, compressed);

        var chunk = Chunk.ReadBuffer(data);

        Assert.Equal(compressed, chunk.Compressed);
        Assert.Equal((uint)payload.Length, chunk.UncompressedSize);
        Assert.Equal(payload, chunk.Data);
        Assert.Equal("00000001-00000002-00000003-00000004", chunk.GuidStr);
    }

    private static byte[] BuildManifest(byte[] payload, bool compressed = false)
    {
        var storedPayload = compressed ? Compress(payload) : payload;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x44BEC00Cu);
        writer.Write(41);
        writer.Write(payload.Length);
        writer.Write(storedPayload.Length);
        writer.Write(SHA1.HashData(payload));
        writer.Write((byte)(compressed ? 1 : 0));
        writer.Write(18);
        writer.Write(storedPayload);
        return stream.ToArray();
    }

    private static byte[] BuildChunk(byte[] payload, bool compressed)
    {
        const uint headerSize = 66;
        var storedPayload = compressed ? Compress(payload) : payload;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0xB1FE3AA2u);
        writer.Write(3u);
        writer.Write(headerSize);
        writer.Write((uint)storedPayload.Length);
        writer.Write(1u);
        writer.Write(2u);
        writer.Write(3u);
        writer.Write(4u);
        writer.Write(0UL);
        writer.Write((byte)(compressed ? 1 : 0));
        writer.Write(SHA1.HashData(payload));
        writer.Write((byte)2);
        writer.Write((uint)payload.Length);
        writer.Write(storedPayload);
        return stream.ToArray();
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(payload);
        }
        return output.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (value.Length == 0)
        {
            writer.Write(0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
