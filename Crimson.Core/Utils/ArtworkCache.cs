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
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string _directory;
    private readonly object _diskLock = new();
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _requests = new(StringComparer.Ordinal);

    public ArtworkCache(HttpClient http, ILogger log, string directory)
    {
        _http = http;
        _log = log;
        _directory = Path.GetFullPath(directory);
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
            if (File.Exists(path) && new FileInfo(path).Length > 0 &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Lifetime)
                return new Uri(path).AbsoluteUri;

            Directory.CreateDirectory(_directory);
            await _downloadGate.WaitAsync().ConfigureAwait(false);
            entered = true;
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumImageBytes ||
                response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidDataException("The artwork response is not a supported image.");

            await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long size = 0;
                int count;
                while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    size += count;
                    if (size > MaximumImageBytes)
                        throw new InvalidDataException("Artwork exceeds the cache size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                }
                if (size == 0) throw new InvalidDataException("Artwork was empty.");
            }

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
            return File.Exists(path) && new FileInfo(path).Length > 0 ? new Uri(path).AbsoluteUri : null;
        }
        finally
        {
            if (entered) _downloadGate.Release();
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
