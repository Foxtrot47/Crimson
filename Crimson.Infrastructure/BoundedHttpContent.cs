using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Repository;

public static class BoundedHttpContent
{
    public static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        if (content.Headers.ContentLength is > 0 and var declaredLength && declaredLength > maximumBytes)
            throw new ResponseBodyTooLargeException(maximumBytes);

        var initialCapacity = content.Headers.ContentLength is > 0 and var contentLength
            ? checked((int)Math.Min(contentLength, int.MaxValue))
            : 0;
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(initialCapacity);
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;

                total = checked(total + read);
                if (total > maximumBytes)
                    throw new ResponseBodyTooLargeException(maximumBytes);

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<string> ReadUtf8Async(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(content, maximumBytes, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    public static async Task<long> CopyToFileAsync(
        HttpContent content,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        if (content.Headers.ContentLength is > 0 and var declaredLength && declaredLength > maximumBytes)
            throw new ResponseBodyTooLargeException(maximumBytes);

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;

                total = checked(total + read);
                if (total > maximumBytes)
                    throw new ResponseBodyTooLargeException(maximumBytes);

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await output.FlushAsync(cancellationToken);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class ResponseBodyTooLargeException(long maximumBytes)
    : IOException($"HTTP response body exceeds the {maximumBytes}-byte limit.");
