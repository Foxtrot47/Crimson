using System;
using System.IO;
using System.IO.Compression;
using ZstdSharp;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Decodes one downloaded slice per the manifest's <see cref="UbisoftManifest.Compression"/>. A slice whose
/// downloaded length already equals its decoded length and that does not decode is stored uncompressed (the
/// packager keeps slices that do not shrink as they are); the caller's content hash check is the final authority.
/// </summary>
public static class UbisoftSliceCodec
{
    /// <summary>Zstd frame magic, little endian: 0xFD2FB528.</summary>
    private static ReadOnlySpan<byte> ZstdMagic => [0x28, 0xB5, 0x2F, 0xFD];

    /// <exception cref="NotSupportedException">The build uses LZHAM.</exception>
    public static byte[] Decode(byte[] downloaded, UbisoftCompression compression, bool isCompressed, int expectedSize)
    {
        ArgumentNullException.ThrowIfNull(downloaded);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        if (!isCompressed) return Checked(downloaded, expectedSize);
        compression = EffectiveCompression(downloaded, compression);

        byte[]? decoded;
        try
        {
            decoded = Decompress(downloaded, compression, expectedSize);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ZstdException)
        {
            if (downloaded.Length == expectedSize) return downloaded;
            throw new InvalidDataException("Ubisoft slice could not be decompressed.", exception);
        }
        if (decoded is not null && decoded.Length == expectedSize) return decoded;
        if (downloaded.Length == expectedSize) return downloaded;
        return decoded is null
            ? throw new InvalidDataException("Ubisoft slice is not a zstd frame.")
            : Checked(decoded, expectedSize);
    }

    private static UbisoftCompression EffectiveCompression(byte[] downloaded, UbisoftCompression compression)
    {
        // Manifests that predate compressionMethod still compress; recognise the container by its header.
        if (compression == UbisoftCompression.None)
            return IsZstdFrame(downloaded) ? UbisoftCompression.Zstd : UbisoftCompression.Deflate;
        if (compression == UbisoftCompression.Lzham)
            throw new NotSupportedException("Ubisoft slices compressed with LZHAM are not supported.");
        return compression;
    }

    private static byte[]? Decompress(byte[] downloaded, UbisoftCompression compression, int expectedSize) => compression switch
    {
        UbisoftCompression.Zstd => IsZstdFrame(downloaded) ? Zstd(downloaded, expectedSize) : null,
        UbisoftCompression.Deflate => Zlib(downloaded, expectedSize),
        _ => throw new InvalidDataException($"Unknown Ubisoft slice compression {(int)compression}."),
    };

    private static bool IsZstdFrame(byte[] downloaded) => downloaded.AsSpan().StartsWith(ZstdMagic);

    private static byte[] Checked(byte[] decoded, int expectedSize) => decoded.Length == expectedSize
        ? decoded
        : throw new InvalidDataException($"Ubisoft slice decoded to {decoded.Length} bytes, not {expectedSize}.");

    private static byte[] Zstd(byte[] downloaded, int expectedSize)
    {
        using var decompressor = new Decompressor();
        // One byte of headroom so an oversized frame is detected instead of silently truncated.
        var buffer = new byte[expectedSize + 1];
        var written = decompressor.Unwrap(downloaded, buffer, 0);
        return written == buffer.Length ? buffer : buffer[..written];
    }

    private static byte[] Zlib(byte[] downloaded, int expectedSize)
    {
        using var zlib = new ZLibStream(new MemoryStream(downloaded, writable: false), CompressionMode.Decompress);
        var buffer = new byte[expectedSize + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = zlib.Read(buffer, read, buffer.Length - read);
            if (count <= 0) break;
            read += count;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }
}
