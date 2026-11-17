using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers.Ubisoft.Protocol;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace Crimson.Providers.Ubisoft;

/// <summary>Signs CDN paths for one product: one URL list (mirrors) per path, in request order.</summary>
internal interface IUbisoftContentSource : IAsyncDisposable
{
    Task<IReadOnlyList<IReadOnlyList<string>>> GetUrlsAsync(IReadOnlyList<string> relativePaths, CancellationToken ct);
    TimeSpan UrlLifetime { get; }
}

internal delegate Task<IUbisoftContentSource> UbisoftContentConnector(uint productId, CancellationToken ct);

internal sealed class UbisoftDemuxContentSource : IUbisoftContentSource
{
    private readonly UbisoftDemuxConnection _connection;
    private readonly UbisoftDownloadService _download;
    private readonly uint _productId;

    private UbisoftDemuxContentSource(UbisoftDemuxConnection connection, UbisoftDownloadService download, uint productId)
    {
        _connection = connection;
        _download = download;
        _productId = productId;
    }

    public TimeSpan UrlLifetime => _download.UrlLifetime;

    internal static async Task<IUbisoftContentSource> OpenAsync(UbisoftAuthentication auth, uint productId, CancellationToken ct)
    {
        var session = await auth.RequireSession(ct);
        var connection = await UbisoftDemuxConnection.ConnectAsync(session.Ticket, ct);
        try
        {
            var ownership = await UbisoftOwnershipService.OpenAsync(connection, ct);
            var token = await ownership.GetOwnershipTokenAsync(productId, ct);
            var download = await UbisoftDownloadService.OpenAsync(connection, token, ct);
            return new UbisoftDemuxContentSource(connection, download, productId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> GetUrlsAsync(IReadOnlyList<string> relativePaths, CancellationToken ct) =>
        _download.GetUrlsAsync(_productId, relativePaths, ct);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>
/// Opens its source lazily and reopens it when the connection drops or the ownership token expires, so a
/// multi-hour download survives the service closing an idle socket. A refusal is never retried.
/// </summary>
internal sealed class UbisoftReconnectingSource(UbisoftContentConnector connector, uint productId) : IUbisoftContentSource
{
    private const int Attempts = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IUbisoftContentSource? _current;
    private bool _disposed;

    public TimeSpan UrlLifetime => _current?.UrlLifetime ?? TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<IReadOnlyList<string>>> GetUrlsAsync(IReadOnlyList<string> relativePaths,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var source = await CurrentAsync(ct);
            try
            {
                return await source.GetUrlsAsync(relativePaths, ct);
            }
            catch (Exception error) when (attempt < Attempts && !ct.IsCancellationRequested && IsConnectionFailure(error))
            {
                await DropAsync(source);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    private static bool IsConnectionFailure(Exception error) => error switch
    {
        UbisoftDemuxException demux => demux.Reason is not UbisoftDemuxFailure.Banned,
        IOException or TimeoutException or System.Net.Sockets.SocketException or ObjectDisposedException => true,
        _ => false
    };

    private async Task<IUbisoftContentSource> CurrentAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _current ??= await connector(productId, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task DropAsync(IUbisoftContentSource source)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_current, source)) return;
            _current = null;
        }
        finally { _gate.Release(); }
        try { await source.DisposeAsync(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or UbisoftDemuxException) { }
    }

    public async ValueTask DisposeAsync()
    {
        IUbisoftContentSource? current;
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            current = _current;
            _current = null;
        }
        finally { _gate.Release(); }
        if (current is not null) await current.DisposeAsync();
    }
}

/// <summary>Stops workers between slices while paused; in-flight slices finish.</summary>
internal sealed class UbisoftPauseGate
{
    private readonly object _sync = new();
    private TaskCompletionSource _open = CreateOpen();

    internal bool IsPaused { get; private set; }

    internal void Pause()
    {
        lock (_sync)
        {
            if (IsPaused) return;
            IsPaused = true;
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void Resume()
    {
        lock (_sync)
        {
            if (!IsPaused) return;
            IsPaused = false;
            _open.TrySetResult();
        }
    }

    internal Task WaitAsync(CancellationToken ct)
    {
        Task open;
        lock (_sync) open = _open.Task;
        return open.IsCompleted ? Task.CompletedTask : open.WaitAsync(ct);
    }

    private static TaskCompletionSource CreateOpen()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

/// <summary>
/// Resume state of one transfer in <c>&lt;install&gt;/.Crimson/ubisoft-progress.json</c>: finished files and the
/// finished slices of files still in progress, for one manifest. <see cref="Fresh"/> marks a first install, whose
/// files a cancellation may delete.
/// </summary>
internal sealed class UbisoftInstallProgress
{
    internal const string CrimsonFolder = ".Crimson";
    private const string FileName = "ubisoft-progress.json";
    private readonly object _sync = new();

    public uint ProductId { get; set; }
    public string ManifestId { get; set; } = "";
    public bool Fresh { get; set; }
    public HashSet<string> Completed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<int>> Partial { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static string PathFor(string root) => Path.Combine(root, CrimsonFolder, FileName);

    /// <summary>The stored progress when it belongs to this product and build; otherwise null.</summary>
    internal static UbisoftInstallProgress? Load(string root, uint productId, string manifestId)
    {
        var file = PathFor(root);
        if (!File.Exists(file)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<UbisoftInstallProgress>(File.ReadAllText(file));
            if (stored is null || stored.ProductId != productId ||
                !string.Equals(stored.ManifestId, manifestId, StringComparison.OrdinalIgnoreCase)) return null;
            stored.Completed = new HashSet<string>(stored.Completed ?? [], StringComparer.OrdinalIgnoreCase);
            stored.Partial = new Dictionary<string, HashSet<int>>(stored.Partial ?? [], StringComparer.OrdinalIgnoreCase);
            return stored;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when some stored progress (any build) for this product exists in the folder.</summary>
    internal static bool BelongsTo(string root, uint productId)
    {
        var file = PathFor(root);
        if (!File.Exists(file)) return false;
        try
        {
            return JsonSerializer.Deserialize<UbisoftInstallProgress>(File.ReadAllText(file))?.ProductId == productId;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal bool IsComplete(string path)
    {
        lock (_sync) return Completed.Contains(path);
    }

    internal bool HasSlice(string path, int index)
    {
        lock (_sync) return Partial.TryGetValue(path, out var slices) && slices.Contains(index);
    }

    internal void MarkSlice(string path, int index)
    {
        lock (_sync)
        {
            if (!Partial.TryGetValue(path, out var slices)) Partial[path] = slices = [];
            slices.Add(index);
        }
    }

    internal void MarkFile(string path)
    {
        lock (_sync)
        {
            Completed.Add(path);
            Partial.Remove(path);
        }
    }

    internal void Forget(string path)
    {
        lock (_sync)
        {
            Completed.Remove(path);
            Partial.Remove(path);
        }
    }

    internal void Save(string root)
    {
        string json;
        lock (_sync) json = JsonSerializer.Serialize(this);
        var file = PathFor(root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, file, overwrite: true);
    }

    internal static void Delete(string root)
    {
        var file = PathFor(root);
        if (File.Exists(file)) File.Delete(file);
    }
}

/// <summary>Callbacks a transfer reports through; all are invoked from worker threads.</summary>
internal sealed record UbisoftTransferCallbacks(
    Action<long> Downloaded,
    Action<long> Written,
    Action<bool> Retrying);

internal sealed class UbisoftSliceCorruptException(string message) : Exception(message);

internal sealed class UbisoftDownloadEngine(
    IUbisoftContentSource source,
    IUbisoftCdn cdn,
    UbisoftCompression compression,
    bool isCompressed,
    UbisoftPauseGate pause,
    ILogger log)
{
    internal const int SignBatch = 64;
    internal const int Concurrency = 16;
    internal const int SliceAttempts = 4;
    private const int SignedLookahead = SignBatch * 4;
    private const long MaximumSliceBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    private readonly record struct PendingSlice(UbisoftManifestFile File, int Index, UbisoftSlice Slice);

    private sealed record SignedSlice(UbisoftManifestFile File, int Index, UbisoftSlice Slice,
        IReadOnlyList<string> Urls, DateTimeOffset SignedAt);

    private sealed class OpenFile(SafeFileHandle handle, int remaining)
    {
        public SafeFileHandle Handle { get; } = handle;
        public int Remaining = remaining;
    }

    private sealed class Transfer(string root, UbisoftInstallProgress progress, UbisoftTransferCallbacks callbacks)
    {
        private readonly object _saveLock = new();
        private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

        public string Root { get; } = root;
        public UbisoftInstallProgress Progress { get; } = progress;
        public UbisoftTransferCallbacks Callbacks { get; } = callbacks;
        public ConcurrentDictionary<string, OpenFile> Open { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SaveProgress(bool force)
        {
            lock (_saveLock)
            {
                if (!force && DateTimeOffset.UtcNow - _lastSave < SaveInterval) return;
                _lastSave = DateTimeOffset.UtcNow;
                Progress.Save(Root);
            }
        }
    }

    internal async Task RunAsync(string root, IReadOnlyList<UbisoftManifestFile> files, UbisoftInstallProgress progress,
        UbisoftTransferCallbacks callbacks, CancellationToken ct)
    {
        var transfer = new Transfer(root, progress, callbacks);
        try
        {
            var pending = PrepareFiles(transfer, files, ct);
            transfer.SaveProgress(force: true);
            if (pending.Count == 0) return;
            await TransferSlicesAsync(transfer, pending, ct);
        }
        finally
        {
            foreach (var entry in transfer.Open.Values) entry.Handle.Dispose();
            try { transfer.SaveProgress(force: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Warning("Ubisoft: transfer progress could not be saved ({Error})", error.GetType().Name);
            }
        }
    }

    private static List<PendingSlice> PrepareFiles(Transfer transfer, IReadOnlyList<UbisoftManifestFile> files,
        CancellationToken ct)
    {
        var progress = transfer.Progress;
        var pending = new List<PendingSlice>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (progress.IsComplete(file.Path)) continue;
            var target = InstallPathPolicy.ResolveFile(transfer.Root, file.Path);
            if (file.IsDirectory)
            {
                Directory.CreateDirectory(target);
                progress.MarkFile(file.Path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var remaining = RemainingSlices(file, progress);
            if (remaining.Count == 0 || file.Size == 0)
            {
                using var handle = OpenHandle(target, file.Size);
                progress.MarkFile(file.Path);
                continue;
            }
            transfer.Open[file.Path] = new OpenFile(OpenHandle(target, file.Size), remaining.Count);
            pending.AddRange(remaining);
        }
        return pending;
    }

    private static List<PendingSlice> RemainingSlices(UbisoftManifestFile file, UbisoftInstallProgress progress)
    {
        var remaining = new List<PendingSlice>();
        for (var index = 0; index < file.Slices.Count; index++)
            if (!progress.HasSlice(file.Path, index)) remaining.Add(new PendingSlice(file, index, file.Slices[index]));
        return remaining;
    }

    /// <summary>One signing stage feeds <see cref="Concurrency"/> fetchers; the first failure stops them all.</summary>
    private async Task TransferSlicesAsync(Transfer transfer, List<PendingSlice> pending, CancellationToken ct)
    {
        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = pipeline.Token;
        var signed = Channel.CreateBounded<SignedSlice>(new BoundedChannelOptions(SignedLookahead)
        {
            SingleWriter = true,
            SingleReader = false
        });

        var signing = Task.Run(() => SignAsync(pending, signed.Writer, token), token);
        var workers = Enumerable.Range(0, Concurrency)
            .Select(_ => Task.Run(() => WriteSlicesAsync(transfer, signed.Reader, token), token)).ToList();

        var stages = workers.Append(signing).ToList();
        foreach (var stage in stages)
            _ = stage.ContinueWith(_ => pipeline.Cancel(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        try { await Task.WhenAll(stages); }
        catch
        {
            // Report the first real failure, not the cancellations it caused.
            var fault = stages.Where(stage => stage.IsFaulted).SelectMany(stage => stage.Exception!.InnerExceptions)
                .FirstOrDefault(error => error is not OperationCanceledException and not ChannelClosedException);
            ct.ThrowIfCancellationRequested();
            if (fault is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fault);
            throw;
        }
    }

    private async Task SignAsync(List<PendingSlice> pending, ChannelWriter<SignedSlice> writer, CancellationToken token)
    {
        try
        {
            for (var start = 0; start < pending.Count; start += SignBatch)
            {
                await pause.WaitAsync(token);
                var batch = pending.Skip(start).Take(SignBatch).ToList();
                var urls = await source.GetUrlsAsync(
                    batch.Select(entry => UbisoftManifest.SlicePath(entry.Slice.DownloadSha1)).ToList(), token);
                if (urls.Count != batch.Count)
                    throw new InvalidDataException("Ubisoft signed a different number of slices than requested.");
                var signedAt = DateTimeOffset.UtcNow;
                for (var slot = 0; slot < batch.Count; slot++)
                    await writer.WriteAsync(
                        new SignedSlice(batch[slot].File, batch[slot].Index, batch[slot].Slice, urls[slot], signedAt), token);
            }
            writer.TryComplete();
        }
        catch (Exception error)
        {
            writer.TryComplete(error);
            throw;
        }
    }

    private async Task WriteSlicesAsync(Transfer transfer, ChannelReader<SignedSlice> reader, CancellationToken token)
    {
        await foreach (var work in reader.ReadAllAsync(token))
        {
            await pause.WaitAsync(token);
            var bytes = await FetchSliceAsync(work, transfer.Callbacks, token);
            var entry = transfer.Open[work.File.Path];
            await RandomAccess.WriteAsync(entry.Handle, bytes, (long)work.Slice.Offset, token);
            transfer.Callbacks.Written(bytes.Length);
            transfer.Progress.MarkSlice(work.File.Path, work.Index);
            if (Interlocked.Decrement(ref entry.Remaining) == 0)
            {
                RandomAccess.FlushToDisk(entry.Handle);
                entry.Handle.Dispose();
                transfer.Open.TryRemove(work.File.Path, out _);
                transfer.Progress.MarkFile(work.File.Path);
            }
            transfer.SaveProgress(force: false);
        }
    }

    private static SafeFileHandle OpenHandle(string path, ulong size)
    {
        // Preallocation reserves the space up front (a full disk fails now, not hours in); it only applies to new files.
        var handle = File.Exists(path)
            ? File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous)
            : File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous,
                preallocationSize: (long)size);
        try
        {
            if (RandomAccess.GetLength(handle) != (long)size) RandomAccess.SetLength(handle, (long)size);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private async Task<byte[]> FetchSliceAsync(SignedSlice work, UbisoftTransferCallbacks callbacks, CancellationToken ct)
    {
        var urls = work.Urls;
        var signedAt = work.SignedAt;
        var path = UbisoftManifest.SlicePath(work.Slice.DownloadSha1);
        Exception? last = null;
        var retrying = false;
        try
        {
            for (var attempt = 1; attempt <= SliceAttempts; attempt++)
            {
                if (NeedsSigning(urls, signedAt))
                {
                    urls = (await source.GetUrlsAsync([path], ct))[0];
                    signedAt = DateTimeOffset.UtcNow;
                }
                var (decoded, failure, resign) = await TryMirrorsAsync(work, urls, callbacks, ct);
                if (decoded is not null) return decoded;
                last = failure ?? last;
                if (attempt == SliceAttempts) break;
                if (!retrying)
                {
                    retrying = true;
                    callbacks.Retrying(true);
                }
                if (resign) urls = [];
                await WaitBeforeRetryAsync(work, attempt, last, ct);
            }
        }
        finally
        {
            if (retrying) callbacks.Retrying(false);
        }
        throw SliceFailure(last);
    }

    private bool NeedsSigning(IReadOnlyList<string> urls, DateTimeOffset signedAt) =>
        urls.Count == 0 || DateTimeOffset.UtcNow - signedAt > source.UrlLifetime - ExpiryMargin;

    private Task WaitBeforeRetryAsync(SignedSlice work, int attempt, Exception? last, CancellationToken ct)
    {
        log.Debug("Ubisoft: slice {Slice} retry {Attempt}/{Total} ({Reason})", work.Slice.DownloadSha1, attempt,
            SliceAttempts, last?.GetType().Name);
        return Task.Delay(RetryDelay * attempt, ct);
    }

    /// <summary>
    /// Tries each mirror once. Returns the decoded slice, or the last failure (null when there was no mirror) and
    /// whether a mirror refused the signature.
    /// </summary>
    private async Task<(byte[]? Decoded, Exception? Failure, bool Resign)> TryMirrorsAsync(SignedSlice work,
        IReadOnlyList<string> urls, UbisoftTransferCallbacks callbacks, CancellationToken ct)
    {
        Exception? last = null;
        var resign = false;
        foreach (var url in urls)
        {
            try
            {
                var body = await cdn.GetAsync(url, Math.Max(work.Slice.DownloadSize, 1) + 1024L * 1024,
                    callbacks.Downloaded, ct);
                if (!string.Equals(Convert.ToHexString(SHA1.HashData(body)), work.Slice.DownloadSha1,
                        StringComparison.OrdinalIgnoreCase))
                {
                    last = new UbisoftSliceCorruptException("A downloaded slice failed its hash check.");
                    continue;
                }
                return (Decode(work, body), null, resign);
            }
            catch (HttpRequestException error) when (!ct.IsCancellationRequested)
            {
                last = error;
                if (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Gone)
                    resign = true;
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                error is IOException or TimeoutException or OperationCanceledException)
            {
                last = error;
            }
        }
        return (null, last, resign);
    }

    private static Exception SliceFailure(Exception? last) => last switch
    {
        UbisoftSliceCorruptException => new UbisoftInstallUnavailableException("slice_hash_mismatch",
            OperationFailureKind.Verification),
        null => new HttpRequestException("Ubisoft CDN did not serve the slice."),
        _ => last
    };

    private byte[] Decode(SignedSlice work, byte[] body)
    {
        if (work.Slice.Size > MaximumSliceBytes) throw new InvalidDataException("Ubisoft slice is too large.");
        var decoded = UbisoftSliceCodec.Decode(body, compression, isCompressed, checked((int)work.Slice.Size));
        if (work.Slice.ContentSha1 is { Length: 20 } expected && !SHA1.HashData(decoded).AsSpan().SequenceEqual(expected))
            throw new UbisoftInstallUnavailableException("slice_content_mismatch", OperationFailureKind.Verification);
        return decoded;
    }
}

internal static class UbisoftFileVerifier
{
    private const int Parallelism = 4;

    /// <summary>
    /// Files that are missing, have the wrong size, or (when the manifest carries content hashes) contain a slice
    /// whose SHA-1 differs. <paramref name="hashed"/> receives each file's size once checked.
    /// </summary>
    internal static async Task<IReadOnlyList<UbisoftManifestFile>> FindDamagedAsync(string root,
        IReadOnlyList<UbisoftManifestFile> files, Action<long> hashed, UbisoftPauseGate pause, CancellationToken ct)
    {
        var damaged = new ConcurrentBag<UbisoftManifestFile>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (file, token) =>
            {
                await pause.WaitAsync(token);
                if (!await IsIntactAsync(root, file, token)) damaged.Add(file);
                hashed((long)file.Size);
            });
        var order = files.Select((file, index) => (file.Path, index))
            .ToDictionary(entry => entry.Path, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        return damaged.OrderBy(file => order[file.Path]).ToList();
    }

    internal static async Task<bool> IsIntactAsync(string root, UbisoftManifestFile file, CancellationToken ct)
    {
        string path;
        try { path = InstallPathPolicy.ResolveFile(root, file.Path); }
        catch (InvalidDataException) { return false; }
        if (file.IsDirectory) return Directory.Exists(path);
        if (!File.Exists(path)) return false;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous);
            return RandomAccess.GetLength(handle) == (long)file.Size && await SlicesMatchAsync(handle, file, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> SlicesMatchAsync(SafeFileHandle handle, UbisoftManifestFile file, CancellationToken ct)
    {
        byte[]? buffer = null;
        foreach (var slice in file.Slices)
        {
            if (slice.ContentSha1 is not { Length: 20 } expected) continue;
            if (buffer is null || buffer.Length < slice.Size) buffer = new byte[slice.Size];
            var read = 0;
            while (read < slice.Size)
            {
                var count = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read, (int)slice.Size - read),
                    (long)slice.Offset + read, ct);
                if (count == 0) return false;
                read += count;
            }
            if (!SHA1.HashData(buffer.AsSpan(0, (int)slice.Size)).AsSpan().SequenceEqual(expected)) return false;
        }
        return true;
    }
}

internal static class UbisoftInstallFiles
{
    internal static string Key(string path) => path.Replace('/', '\\').Trim('\\');

    internal static bool SameContent(UbisoftManifestFile left, UbisoftManifestFile right)
    {
        if (left.Size != right.Size || left.IsDirectory != right.IsDirectory || left.Slices.Count != right.Slices.Count)
            return false;
        for (var index = 0; index < left.Slices.Count; index++)
        {
            var a = left.Slices[index];
            var b = right.Slices[index];
            if (a.Size != b.Size || a.Offset != b.Offset ||
                !string.Equals(a.DownloadSha1, b.DownloadSha1, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>
    /// Deletes files of <paramref name="previous"/> that <paramref name="next"/> no longer lists, then any folder
    /// those deletions emptied. Nothing outside the previous selection is touched.
    /// </summary>
    internal static int DeleteLeaving(string root, IEnumerable<UbisoftManifestFile> previous,
        IEnumerable<UbisoftManifestFile> next, ILogger log)
    {
        var keep = next.Select(file => Key(file.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Delete(root, previous.Where(file => !keep.Contains(Key(file.Path))), log);
    }

    internal static int Delete(string root, IEnumerable<UbisoftManifestFile> files, ILogger log)
    {
        var removed = 0;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OrderByDescending(file => file.Path.Length))
        {
            string path;
            try { path = InstallPathPolicy.ResolveFile(root, file.Path); }
            catch (InvalidDataException) { continue; }
            try
            {
                if (file.IsDirectory)
                {
                    folders.Add(path);
                    continue;
                }
                if (!File.Exists(path)) continue;
                File.SetAttributes(path, FileAttributes.Normal);
                InstallPathPolicy.DeleteFile(root, path);
                removed++;
                if (Path.GetDirectoryName(path) is { } parent) folders.Add(parent);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Warning("Ubisoft: could not delete a game file ({Error})", error.GetType().Name);
            }
        }
        RemoveEmptyParents(root, folders);
        return removed;
    }

    internal static void RemoveEmptyParents(string root, IEnumerable<string> folders)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var folder in folders.OrderByDescending(folder => folder.Length))
        {
            for (var current = Path.TrimEndingDirectorySeparator(folder);
                 current.Length > top.Length && current.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                 current = Path.GetDirectoryName(current)!)
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) break;
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) break;
                    Directory.Delete(current);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { break; }
            }
        }
    }

    internal static ulong Bytes(IEnumerable<UbisoftManifestFile> files) =>
        files.Aggregate(0UL, (total, file) => total + (file.IsDirectory ? 0 : file.Size));

    internal static ulong DownloadBytes(IEnumerable<UbisoftManifestFile> files) =>
        files.Aggregate(0UL, (total, file) => file.Slices.Aggregate(total, (sum, slice) => sum + slice.DownloadSize));

    internal static string Describe(ulong bytes) => (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture);
}
