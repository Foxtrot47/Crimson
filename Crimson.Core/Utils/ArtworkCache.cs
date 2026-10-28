using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

    /// <summary>Incremented by <see cref="Clear"/> so callers can drop file URIs that may no longer exist.</summary>
    int Generation { get; }

    long GetSizeBytes();

    /// <summary>Deletes cached images. Returns false if some files could not be removed.</summary>
    bool Clear();
}

/// <summary>
/// Shared, bounded disk cache for public Epic artwork. No account credentials are sent.
/// Entries are keyed by the full URL, expire after 30 days, and the folder is capped at
/// 256 MiB. Downloads are coalesced per URL, run four at a time, and retry transient
/// failures with backoff; a URL that still fails is skipped for <see cref="FailureCooldown"/>.
/// </summary>
public sealed class ArtworkCache : IArtworkCache
{
    private const long MaximumImageBytes = 20 * 1024 * 1024;
    private const long MaximumCacheBytes = 256 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);
    private const int MaximumAttempts = 3;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string _directory;
    private readonly object _diskLock = new();
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);
    private readonly TimeSpan _downloadTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly TimeProvider _timeProvider;
    private int _generation;

    public int Generation => Volatile.Read(ref _generation);

    public ArtworkCache(HttpClient http, ILogger log, string directory)
        : this(http, log, directory, DownloadTimeout)
    {
    }

    internal ArtworkCache(
        HttpClient http,
        ILogger log,
        string directory,
        TimeSpan downloadTimeout,
        TimeSpan? retryDelay = null,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _log = log;
        _directory = Path.GetFullPath(directory);
        _downloadTimeout = downloadTimeout > TimeSpan.Zero ? downloadTimeout : throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        _retryDelay = retryDelay ?? RetryDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        if (File.Exists(path) && IsUsableCachedImage(path) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Lifetime)
            return new Uri(path).AbsoluteUri;

        if (_failures.TryGetValue(url, out var failedAt) && _timeProvider.GetUtcNow() - failedAt < FailureCooldown)
            return UsableCachedUri(path);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var uri = await DownloadAsync(url, key, path).ConfigureAwait(false);
                _failures.TryRemove(url, out _);
                return uri;
            }
            catch (Exception ex) when (attempt < MaximumAttempts && IsTransient(ex))
            {
                _log.Warning(ex, "Artwork download attempt {Attempt} failed; retrying", attempt);
                // Exponential backoff with jitter, waited outside the download slot.
                var delay = _retryDelay * Math.Pow(2, attempt - 1) * (0.8 + Random.Shared.NextDouble() * 0.4);
                await Task.Delay(delay, _timeProvider).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to cache artwork");
                _failures[url] = _timeProvider.GetUtcNow();
                // A failed refresh must not discard previously downloaded artwork.
                return UsableCachedUri(path);
            }
        }
    }

    private static string? UsableCachedUri(string path) =>
        File.Exists(path) && IsUsableCachedImage(path) ? new Uri(path).AbsoluteUri : null;

    // Network failures, timeouts, server errors and throttling can succeed on retry;
    // client errors and invalid image data will not.
    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var status } => (int)status! >= 500 || status == HttpStatusCode.TooManyRequests,
        OperationCanceledException => true,
        // InvalidDataException is not an IOException, so bad image data falls through to false.
        IOException => true,
        _ => false
    };

    private async Task<string> DownloadAsync(string url, string key, string path)
    {
        var temporary = Path.Combine(_directory, key + $".{Guid.NewGuid():N}.tmp");
        var entered = false;
        try
        {
            Directory.CreateDirectory(_directory);
            await _downloadGate.WaitAsync().ConfigureAwait(false);
            entered = true;
            // Start the timeout once a slot is held so queueing behind other downloads does not count.
            using var timeout = new CancellationTokenSource(_downloadTimeout);
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

    public long GetSizeBytes()
    {
        lock (_diskLock)
        {
            if (!Directory.Exists(_directory)) return 0;
            long total = 0;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.image"))
            {
                try { total += file.Length; }
                catch (IOException) { }
            }
            return total;
        }
    }

    public bool Clear()
    {
        lock (_diskLock)
        {
            Interlocked.Increment(ref _generation);
            _failures.Clear();
            if (!Directory.Exists(_directory)) return true;
            var cleared = true;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.image").ToList())
            {
                try { file.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An image being decoded can hold the file open; it is replaced on the next download.
                    _log.Warning(ex, "Failed to remove cached artwork");
                    cleared = false;
                }
            }
            return cleared;
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
