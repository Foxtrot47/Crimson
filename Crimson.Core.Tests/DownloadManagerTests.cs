using System.Net;
using Crimson.Core;
using Serilog;

namespace Crimson.Tests;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task DownloadFileWithFallback_UsesNextMirrorAfterHttpFailure()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            var response = request.RequestUri!.Host switch
            {
                "first.epicgamescdn.com" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "second.epicgamescdn.com" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            return Task.FromResult(response);
        }));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com/base", "https://second.epicgamescdn.com/base"]);
        var destination = CreateDestination();

        try
        {
            var success = await manager.DownloadFileWithFallback("chunks/one.chunk", destination);

            Assert.True(success);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
            Assert.Collection(
                requests,
                request => Assert.Equal("https://first.epicgamescdn.com/base/chunks/one.chunk", request.AbsoluteUri),
                request => Assert.Equal("https://second.epicgamescdn.com/base/chunks/one.chunk", request.AbsoluteUri));
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task DownloadFileWithFallback_StopsAfterConfiguredAttempts()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com", "https://second.epicgamescdn.com"]);
        var destination = CreateDestination();

        try
        {
            var success = await manager.DownloadFileWithFallback(
                "one.chunk", destination, maxRetries: 2);

            Assert.False(success);
            Assert.Equal(4, requestCount);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadFileWithFallback_PreservesLocalWriteErrorsInsteadOfTreatingThemAsNetworkRetries(bool diskFull)
    {
        var requests = 0;
        Exception failure = diskFull
            ? new IOException("Synthetic disk full", unchecked((int)0x80070070))
            : new UnauthorizedAccessException("Synthetic permission failure");
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requests++;
            return Task.FromException<HttpResponseMessage>(failure);
        }));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com", "https://second.epicgamescdn.com"]);
        var destination = CreateDestination();
        try
        {
            var error = await Record.ExceptionAsync(() => manager.DownloadFileWithFallback("one.chunk", destination));
            Assert.Same(failure, error);
            Assert.Equal(1, requests);
            Assert.Equal(diskFull ? "storage_full" : "permission_denied", OperationFailureClassifier.Classify(error).Code);
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryNotificationCoversBackoffUntilRecoveryOrExhaustion(bool recovers)
    {
        var requests = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(recovers && requests > 1 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com"]);
        var changes = new List<bool>();
        var destination = CreateDestination();
        try
        {
            var succeeded = await manager.DownloadFileWithFallback("one.chunk", destination, maxRetries: 2,
                retryStateChanged: retrying => changes.Add(retrying));

            Assert.Equal(recovers, succeeded);
            Assert.Equal(new[] { true, false }, changes);
            Assert.Equal(2, requests);
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task CancellationDuringBackoffClearsRetryNotification()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com"]);
        using var cancellation = new CancellationTokenSource();
        var changes = new List<bool>();
        var destination = CreateDestination();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DownloadFileWithFallback(
                "one.chunk", destination, cancellationToken: cancellation.Token, retryStateChanged: retrying =>
                {
                    changes.Add(retrying);
                    if (retrying) cancellation.Cancel();
                }));
            Assert.Equal(new[] { true, false }, changes);
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task SuccessfulMirrorFallbackDoesNotShowAnOperationRetry()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(request.RequestUri!.Host.StartsWith("first") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            })));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com", "https://second.epicgamescdn.com"]);
        var destination = CreateDestination();
        var changes = new List<bool>();
        try
        {
            Assert.True(await manager.DownloadFileWithFallback("one.chunk", destination, retryStateChanged: retrying => changes.Add(retrying)));
            Assert.Empty(changes);
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task DownloadFileWithFallback_CancellationStopsActiveRequest()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com"]);
        var destination = CreateDestination();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var download = manager.DownloadFileWithFallback(
                "one.chunk", destination, cancellationToken: cancellation.Token);
            await requestStarted.Task;
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task DownloadFileWithFallback_RejectsShortBodyAndUsesNextMirror()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var bytes = request.RequestUri!.Host == "first.epicgamescdn.com"
                ? new byte[] { 1, 2, 3 }
                : new byte[] { 4, 5, 6, 7 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com", "https://second.epicgamescdn.com"]);
        var destination = CreateDestination();

        try
        {
            long receivedBytes = 0;
            var success = await manager.DownloadFileWithFallback(
                "one.chunk", destination, expectedSize: 4, bytesTransferred: bytes => receivedBytes += bytes);

            Assert.True(success);
            Assert.Equal(7, receivedBytes); // Include traffic from the rejected first body, not just the final file.
            Assert.Equal(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task DownloadFileWithFallback_FailedAttemptDoesNotReplaceExistingDestination()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            })));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com"]);
        var destination = CreateDestination();
        var original = new byte[] { 9, 8, 7, 6 };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, original);

            var success = await manager.DownloadFileWithFallback(
                "one.chunk", destination, maxRetries: 1, expectedSize: 4);

            Assert.False(success);
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task DownloadFileWithFallback_WithoutMirrorsFailsImmediately()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("No request should be made")));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors([]);
        var destination = CreateDestination();

        try
        {
            var success = await manager.DownloadFileWithFallback("one.chunk", destination);

            Assert.False(success);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteDestinationDirectory(destination);
        }
    }

    [Fact]
    public async Task TransferCallbackReportsBytesBeforeBodyCompletes()
    {
        var stream = new GatedStream();
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        using var logger = new LoggerConfiguration().CreateLogger();
        var manager = new DownloadManager(logger, client);
        await manager.InitializeMirrors(["https://first.epicgamescdn.com"]);
        var destination = CreateDestination();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long received = 0;
        try
        {
            var download = manager.DownloadFileWithFallback("one.chunk", destination, bytesTransferred: bytes =>
            {
                received += bytes;
                reported.TrySetResult();
            });
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4096, received);
            Assert.False(download.IsCompleted);
            stream.Release.TrySetResult();
            Assert.True(await download.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(4096, new FileInfo(destination).Length);
        }
        finally
        {
            stream.Release.TrySetResult();
            DeleteDestinationDirectory(destination);
        }
    }

    private sealed class GatedStream : Stream
    {
        private bool _read;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read)
            {
                _read = true;
                buffer.Span[..4096].Fill(1);
                return 4096;
            }
            await Release.Task.WaitAsync(cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string CreateDestination() =>
        Path.Combine(Path.GetTempPath(), $"crimson-test-{Guid.NewGuid():N}", "chunk.bin");

    private static void DeleteDestinationDirectory(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
