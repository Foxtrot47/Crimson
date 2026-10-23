using System;
using System.Collections.Generic;
using System.Threading;
using Crimson.Models;

namespace Crimson.Core;

internal sealed class TransferRateHistory
{
    private const double MiB = 1024d * 1024d;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly List<TransferRateSample> _samples = [];
    private long _downloadedBytes;
    private long _writtenBytes;
    private long _previousDownloaded;
    private long _previousWritten;
    private long _previousTimestamp;
    private bool _isTransferring;

    internal TransferRateHistory(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _started = _previousTimestamp = _time.GetTimestamp();
    }

    internal void RecordDownload(long bytes) => Interlocked.Add(ref _downloadedBytes, bytes);
    internal void RecordWrite(long bytes) => Interlocked.Add(ref _writtenBytes, bytes);

    internal void SetTransferring(bool value)
    {
        lock (_gate)
        {
            if (_isTransferring == value) return;
            _isTransferring = value;
            ResetBaseline();
        }
    }

    private void ResetBaseline()
    {
        _previousTimestamp = _time.GetTimestamp();
        _previousDownloaded = Interlocked.Read(ref _downloadedBytes);
        _previousWritten = Interlocked.Read(ref _writtenBytes);
    }

    internal TransferRateSample? Sample()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var seconds = _time.GetElapsedTime(_previousTimestamp, now).TotalSeconds;
            if (seconds <= 0) return null;
            var downloaded = Interlocked.Read(ref _downloadedBytes);
            var written = Interlocked.Read(ref _writtenBytes);
            var sample = new TransferRateSample(
                _time.GetElapsedTime(_started, now).TotalSeconds,
                _isTransferring ? Math.Max(0, downloaded - _previousDownloaded) / MiB / seconds : 0,
                _isTransferring ? Math.Max(0, written - _previousWritten) / MiB / seconds : 0);
            _previousTimestamp = now;
            _previousDownloaded = downloaded;
            _previousWritten = written;
            _samples.Add(sample);
            _samples.RemoveAll(item => item.Seconds < sample.Seconds - 60);
            if (_samples.Count > 61) _samples.RemoveAt(0);
            return sample;
        }
    }

    internal IReadOnlyList<TransferRateSample> GetSamples()
    {
        lock (_gate) return Array.AsReadOnly(_samples.ToArray());
    }
}
