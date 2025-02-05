using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Crimson.Models;

namespace Crimson.Core;

public class DownloadManager
{
    private readonly Dictionary<string, MirrorStats> _mirrorStats = new();
    private readonly ILogger _log;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _statLock = new(1);

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";

    public DownloadManager(ILogger log, HttpClient httpClient)
    {
        _log = log;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public async Task InitializeMirrors(List<string> baseUrls)
    {
        await _statLock.WaitAsync();
        try
        {
            _mirrorStats.Clear();
            foreach (var url in baseUrls)
            {
                _mirrorStats[url] = new MirrorStats
                {
                    BaseUrl = url,
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

    public async Task<bool> DownloadFileWithFallback(string relativePath, string destinationPath, int maxRetries = 3)
    {
        var orderedMirrors = await GetPrioritizedMirrors();

        // retry the download until we finally download the file
        // TODO: handle case of being offline
        while (true)
        {
            foreach (var mirror in orderedMirrors)
            {

                var fullUrl = $"{mirror.BaseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
                var attempts = 0;

                try
                {
                    var success = await MeasureDownloadSpeed(mirror, fullUrl, destinationPath);
                    if (success) return true;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Attempt {attempts + 1} failed for mirror {mirror.BaseUrl}");
                    await UpdateMirrorStats(mirror.BaseUrl, false, 0);
                }
                attempts++;
            }
            Task.Delay(100).Wait();
        }
    }

    private async Task<List<MirrorStats>> GetPrioritizedMirrors()
    {
        await _statLock.WaitAsync();
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

    private async Task<bool> MeasureDownloadSpeed(MirrorStats mirror, string url, string destinationPath)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var fileSize = response.Content.Headers.ContentLength ?? 0;
            await using var stream = await response.Content.ReadAsStreamAsync();
            stopwatch.Stop();
            var speedMbps = (fileSize / 1024.0 / 1024.0) / (stopwatch.ElapsedMilliseconds / 1000.0);
            await UpdateMirrorStats(mirror.BaseUrl, true, speedMbps);

            var directoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);

            return true;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            throw;
        }
    }

    private async Task UpdateMirrorStats(string baseUrl, bool success, double speed)
    {
        await _statLock.WaitAsync();
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
