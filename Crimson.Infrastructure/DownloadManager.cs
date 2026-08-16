using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Crimson.Models;
using Crimson.Repository;

namespace Crimson.Core;

public class DownloadManager
{
    private readonly Dictionary<string, MirrorStats> _mirrorStats = new();
    private readonly ILogger<DownloadManager> _log;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _statLock = new(1);
    private const long MaximumChunkDownloadBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);


    public DownloadManager(ILogger<DownloadManager> log, HttpClient httpClient)
    {
        _log = log;
        _httpClient = httpClient;
    }

    public async Task InitializeMirrors(
        List<string> baseUrls,
        CancellationToken cancellationToken = default)
    {
        await _statLock.WaitAsync(cancellationToken);
        try
        {
            _mirrorStats.Clear();
            foreach (var value in baseUrls)
            {
                var uri = EpicEndpointPolicy.RequireContentUri(value);
                var baseUrl = uri.AbsoluteUri.TrimEnd('/');
                _mirrorStats[baseUrl] = new MirrorStats
                {
                    BaseUrl = baseUrl,
                    FailureCount = 0,
                    AverageSpeed = 0,
                    LastAttempt = DateTime.MinValue
                };
            }
        }
        finally
        {
            _statLock.Release();
        }
    }

    /// <summary>
    /// Attempts each configured mirror once per retry round and publishes only a complete download.
    /// </summary>
    public async Task<bool> DownloadFileWithFallback(
        string relativePath,
        string destinationPath,
        int maxRetries = 3,
        CancellationToken cancellationToken = default,
        long? expectedSize = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetries, 1);
        if (expectedSize is > MaximumChunkDownloadBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedSize), "Chunk exceeds the supported size limit.");

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var orderedMirrors = await GetPrioritizedMirrors(cancellationToken);
            if (orderedMirrors.Count == 0)
            {
                _log.LogError("DownloadFileWithFallback: No download mirrors are available");
                return false;
            }

            foreach (var mirror in orderedMirrors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullUrl = new Uri(new Uri($"{mirror.BaseUrl.TrimEnd('/')}/"), relativePath.TrimStart('/')).AbsoluteUri;

                try
                {
                    var success = await MeasureDownloadSpeed(
                        mirror, fullUrl, destinationPath, expectedSize, cancellationToken);
                    if (success) return true;

                    await UpdateMirrorStats(mirror.BaseUrl, false, 0, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError("Attempt {Attempt}/{MaxAttempts} failed for mirror {Mirror} with {ErrorType}",
                    attempt,
                    maxRetries,
                    SensitiveDataRedactor.UriWithoutQuery(mirror.BaseUrl),
                    ex.GetType().Name);
                    await UpdateMirrorStats(mirror.BaseUrl, false, 0, cancellationToken);
                }
            }

            if (attempt < maxRetries)
                await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private async Task<List<MirrorStats>> GetPrioritizedMirrors(CancellationToken cancellationToken = default)
    {
        await _statLock.WaitAsync(cancellationToken);
        try
        {
            return _mirrorStats.Values
                .OrderByDescending(m => m.AverageSpeed)
                .ThenBy(m => m.FailureCount)
                .ToList();
        }
        finally
        {
            _statLock.Release();
        }
    }

    private async Task<bool> MeasureDownloadSpeed(
        MirrorStats mirror,
        string url,
        string destinationPath,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var partialPath = destinationPath + ".partial";
        try
        {
            var uri = EpicEndpointPolicy.RequireContentUri(url);
            using var response = await _httpClient.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("MeasureDownloadSpeed: HTTP {StatusCode} for {Url}",
                (int)response.StatusCode,
                SensitiveDataRedactor.UriWithoutQuery(uri.AbsoluteUri));
                var retryDelay = GetRetryDelay(response);
                if (retryDelay.HasValue)
                    await Task.Delay(retryDelay.Value, cancellationToken);

                return false;
            }

            var directoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directoryPath))
                Directory.CreateDirectory(directoryPath);

            File.Delete(partialPath);
            var maximumBytes = expectedSize ?? MaximumChunkDownloadBytes;
            var downloadedSize = await BoundedHttpContent.CopyToFileAsync(
                response.Content,
                partialPath,
                maximumBytes,
                cancellationToken);
            var contentLength = response.Content.Headers.ContentLength;
            if ((contentLength.HasValue && downloadedSize != contentLength.Value) ||
                (expectedSize.HasValue && downloadedSize != expectedSize.Value))
            {
                _log.LogWarning("MeasureDownloadSpeed: Size mismatch for {Url}. Downloaded {DownloadedSize}, expected {ExpectedSize}, content length {ContentLength}",
                SensitiveDataRedactor.UriWithoutQuery(uri.AbsoluteUri),
                downloadedSize,
                expectedSize,
                contentLength);
                return false;
            }

            File.Move(partialPath, destinationPath, overwrite: true);

            stopwatch.Stop();
            var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            var speedMbps = (downloadedSize / 1024.0 / 1024.0) / elapsedSeconds;
            await UpdateMirrorStats(mirror.BaseUrl, true, speedMbps);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.LogError("MeasureDownloadSpeed failed for {Url} with {ErrorType}",
            SensitiveDataRedactor.UriWithoutQuery(url),
            ex.GetType().Name);
            throw;
        }
        finally
        {
            try
            {
                File.Delete(partialPath);
            }
            catch (Exception ex)
            {
                _log.LogWarning("MeasureDownloadSpeed: Failed to remove partial download with {ErrorType}",
                ex.GetType().Name);
            }
        }
    }

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        if (response.StatusCode is not (
                System.Net.HttpStatusCode.RequestTimeout or
                System.Net.HttpStatusCode.TooManyRequests or
                System.Net.HttpStatusCode.BadGateway or
                System.Net.HttpStatusCode.ServiceUnavailable or
                System.Net.HttpStatusCode.GatewayTimeout))
            return null;

        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta;
        if (!delay.HasValue && retryAfter?.Date is { } date)
            delay = date - DateTimeOffset.UtcNow;

        if (!delay.HasValue || delay <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(250);

        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    private async Task UpdateMirrorStats(
        string baseUrl,
        bool success,
        double speed,
        CancellationToken cancellationToken = default)
    {
        await _statLock.WaitAsync(cancellationToken);
        try
        {
            if (_mirrorStats.TryGetValue(baseUrl, out var stats))
            {
                if (!success)
                {
                    stats.FailureCount++;
                }
                else if (speed > 0)
                {
                    stats.AverageSpeed = stats.AverageSpeed == 0
                        ? speed
                        : (stats.AverageSpeed * 0.7 + speed * 0.3); // Weighted average
                }
                stats.LastAttempt = DateTime.UtcNow;
            }
        }
        finally
        {
            _statLock.Release();
        }
    }
}
