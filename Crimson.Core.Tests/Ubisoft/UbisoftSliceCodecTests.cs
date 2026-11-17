using System.IO.Compression;
using Crimson.Providers.Ubisoft;
using ZstdSharp;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftSliceCodecTests
{
    [Fact]
    public void Decodes_a_zstd_slice()
    {
        var content = Payload(120_000);
        Assert.Equal(content, UbisoftSliceCodec.Decode(Zstd(content), UbisoftCompression.Zstd, true, content.Length));
    }

    [Fact]
    public void Decodes_a_deflate_slice_as_a_zlib_stream()
    {
        var content = Text(80_000);
        Assert.Equal(content, UbisoftSliceCodec.Decode(Zlib(content), UbisoftCompression.Deflate, true, content.Length));
    }

    [Fact]
    public void Returns_an_uncompressed_build_as_it_is()
    {
        var content = Payload(2048);
        Assert.Equal(content, UbisoftSliceCodec.Decode(content, UbisoftCompression.None, false, content.Length));
    }

    [Fact]
    public void Keeps_a_slice_stored_uncompressed_inside_a_compressed_build()
    {
        // The packager stores slices that do not shrink as they are; their download size equals their size.
        var content = Payload(4096);
        Assert.Equal(content, UbisoftSliceCodec.Decode(content, UbisoftCompression.Zstd, true, content.Length));
        Assert.Equal(content, UbisoftSliceCodec.Decode(content, UbisoftCompression.Deflate, true, content.Length));
    }

    [Theory]
    [InlineData(UbisoftCompression.Zstd)]
    [InlineData(UbisoftCompression.Deflate)]
    public void Rejects_a_slice_that_decodes_to_another_length(UbisoftCompression compression)
    {
        var content = Text(4096);
        var compressed = compression == UbisoftCompression.Zstd ? Zstd(content) : Zlib(content);
        Assert.Throws<InvalidDataException>(() => UbisoftSliceCodec.Decode(compressed, compression, true, 5000));
        Assert.Throws<InvalidDataException>(() => UbisoftSliceCodec.Decode(compressed, compression, true, 1000));
    }

    [Fact]
    public void Rejects_an_uncompressed_slice_of_the_wrong_length() =>
        Assert.Throws<InvalidDataException>(() => UbisoftSliceCodec.Decode(Payload(512), UbisoftCompression.None, false, 4096));

    [Fact]
    public void Rejects_garbage_that_is_neither_compressed_nor_the_right_length() =>
        Assert.Throws<InvalidDataException>(() => UbisoftSliceCodec.Decode(Payload(512), UbisoftCompression.Zstd, true, 4096));

    [Fact]
    public void Lzham_is_not_supported() =>
        Assert.Throws<NotSupportedException>(() => UbisoftSliceCodec.Decode(Payload(16), UbisoftCompression.Lzham, true, 64));

    private static byte[] Zlib(byte[] content)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(content);
        return compressed.ToArray();
    }

    private static byte[] Zstd(byte[] content)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(content).ToArray();
    }

    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Compressible content, so compressed and decoded lengths always differ.</summary>
    private static byte[] Text(int size) => Enumerable.Range(0, size).Select(index => (byte)('a' + index % 7)).ToArray();
}
