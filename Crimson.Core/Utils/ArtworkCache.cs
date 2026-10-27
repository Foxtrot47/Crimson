using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Serilog;

namespace Crimson.Utils;

public interface IArtworkCache
{
    Task<string?> GetImageUriAsync(string? url);
}

/// <summary>Shared, bounded disk cache for public Epic artwork. No account credentials are sent.</summary>
public sealed class ArtworkCache : IArtworkCache
{
    private const long MaximumImageBytes = 20 * 1024 * 1024;
    private const long MaximumCacheBytes = 256 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string _directory;
    private readonly object _diskLock = new();
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _requests = new(StringComparer.Ordinal);
    private readonly TimeSpan _downloadTimeout;

    public ArtworkCache(HttpClient http, ILogger log, string directory)
        : this(http, log, directory, DownloadTimeout)
    {
    }

    internal ArtworkCache(HttpClient http, ILogger log, string directory, TimeSpan downloadTimeout)
    {
        _http = http;
        _log = log;
        _directory = Path.GetFullPath(directory);
        _downloadTimeout = downloadTimeout > TimeSpan.Zero ? downloadTimeout : throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
    }

    public async Task<string?> GetImageUriAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !EpicEndpointPolicy.IsAllowedContentUri(uri))
            return null;

        var request = _requests.GetOrAdd(url!, key => new Lazy<Task<string?>>(() => LoadAsync(key)));
        try
        {
            return await request.Value.ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(url!, request));
        }
    }

    private async Task<string?> LoadAsync(string url)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var path = Path.Combine(_directory, key + ".image");
        var temporary = Path.Combine(_directory, key + $".{Guid.NewGuid():N}.tmp");
        var entered = false;
        try
        {
            if (File.Exists(path) && IsUsableCachedImage(path) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Lifetime)
                return new Uri(path).AbsoluteUri;

            Directory.CreateDirectory(_directory);
            using var timeout = new CancellationTokenSource(_downloadTimeout);
            await _downloadGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumImageBytes ||
                response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidDataException("The artwork response is not a supported image.");

            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long size = 0;
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    size += count;
                    if (size > MaximumImageBytes)
                        throw new InvalidDataException("Artwork exceeds the cache size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
                if (size == 0) throw new InvalidDataException("Artwork was empty.");
            }
            if (!HasSupportedImageSignature(temporary))
                throw new InvalidDataException("Artwork does not have a supported image signature.");

            lock (_diskLock)
            {
                File.Move(temporary, path, overwrite: true);
                TrimCache(path);
            }
            return new Uri(path).AbsoluteUri;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to cache artwork");
            // A failed refresh must not discard previously downloaded artwork.
            return File.Exists(path) && IsUsableCachedImage(path) ? new Uri(path).AbsoluteUri : null;
        }
        finally
        {
            if (entered) _downloadGate.Release();
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsUsableCachedImage(string path)
    {
        try { return new FileInfo(path).Length > 0 && HasSupportedImageSignature(path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasSupportedImageSignature(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var file = File.OpenRead(path);
        var read = file.Read(header);
        return (read >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) ||
            (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) ||
            (read >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) ||
            (read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8));
    }

    private void TrimCache(string retainedPath)
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.image")
            .OrderBy(file => file.LastWriteTimeUtc).ToList();
        var retainedBytes = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (file.FullName == retainedPath ||
                (retainedBytes <= MaximumCacheBytes && DateTime.UtcNow - file.LastWriteTimeUtc < Lifetime))
                continue;
            var length = file.Length;
            try
            {
                file.Delete();
                retainedBytes -= length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An image being displayed can hold its file open. Skip it so the download
                // that triggered the trim still succeeds; a later trim removes it.
                _log.Warning(ex, "Failed to trim cached artwork");
            }
        }
    }
}
