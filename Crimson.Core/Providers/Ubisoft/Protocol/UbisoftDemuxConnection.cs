using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft.Protocol;

public enum UbisoftDemuxFailure { Expired, Banned, Rejected, Closed, Timeout }

public sealed class UbisoftDemuxException(UbisoftDemuxFailure reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UbisoftDemuxFailure Reason { get; } = reason;
}

/// <summary>
/// One authenticated Demux ("Janus") socket. A single reader loop correlates top-level responses by
/// request id and routes <c>Push.data</c> to the service channel that owns its connection id. Writes are
/// serialised; reads are never cancelled mid-frame (a cancelled TLS read desynchronises the stream).
/// </summary>
internal sealed class UbisoftDemuxConnection : IAsyncDisposable
{
    internal const uint ClientVersion = 13389;
    internal const string Host = "dmx.upc.ubisoft.com";
    internal const string ClientId = "uplay_pc";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IReadOnlyList<Field>>> _pending = new();
    private readonly ConcurrentDictionary<uint, UbisoftDemuxChannel> _channels = new();
    private Task _reader = Task.CompletedTask;
    private int _nextRequestId; // 0 is the authentication request; Interlocked.Increment yields 1, 2, ...
    private UbisoftDemuxException? _failure;

    internal event Action<uint>? ProductStarted;
    internal event Action<uint>? ProductEnded;

    private UbisoftDemuxConnection(Stream stream) => _stream = stream;

    internal static async Task<UbisoftDemuxConnection> ConnectAsync(string loginTicket, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(loginTicket);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectTimeout);
        var tcp = new TcpClient();
        SslStream? tls = null;
        try
        {
            await tcp.ConnectAsync(Host, 443, deadline.Token);
            tls = new SslStream(new NetworkStream(tcp.Client, ownsSocket: true), leaveInnerStreamOpen: false);
            // The Demux service drops TLS 1.3 handshakes; it speaks TLS 1.2 only.
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Host,
                EnabledSslProtocols = SslProtocols.Tls12
            }, deadline.Token);
        }
        catch (Exception e)
        {
            if (tls is not null) await tls.DisposeAsync();
            tcp.Dispose();
            if (ct.IsCancellationRequested) throw;
            if (e is OperationCanceledException)
                throw new UbisoftDemuxException(UbisoftDemuxFailure.Timeout, "Timed out connecting to the Ubisoft Demux service.", e);
            if (e is IOException or SocketException or AuthenticationException)
                throw new UbisoftDemuxException(UbisoftDemuxFailure.Closed, $"Could not connect to the Ubisoft Demux service: {e.Message}", e);
            throw;
        }
        return await ConnectAsync(tls, loginTicket, ct);
    }

    /// <summary>Authenticates over an already-open transport. The connection owns <paramref name="transport"/>.</summary>
    internal static async Task<UbisoftDemuxConnection> ConnectAsync(Stream transport, string loginTicket, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(loginTicket);
        var connection = new UbisoftDemuxConnection(transport);
        try
        {
            connection._reader = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
            await connection.WriteAsync(ClientVersionPush(), ct);
            var response = await connection.RequestAsync(0, AuthenticateRequest(loginTicket), ct, DefaultTimeout);
            var auth = Read(response.Data(2));
            if (auth.Number(1) != 1)
            {
                var reason = auth.Number(3) == 1 ? UbisoftDemuxFailure.Banned
                    : auth.Number(2) == 1 ? UbisoftDemuxFailure.Expired : UbisoftDemuxFailure.Rejected;
                throw new UbisoftDemuxException(reason, $"Ubisoft Demux authentication failed ({reason}).");
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static byte[] ClientVersionPush() => Bytes(2, Bytes(4, Integer(1, ClientVersion)));

    // Byte-exact with the live-verified PoC: send_keep_alive is sent as an embedded {1:0}.
    internal static byte[] AuthenticateRequest(string loginTicket) => Bytes(1, Message(Integer(1, 0), Bytes(2, Message(
        Bytes(1, Bytes(3, Encoding.UTF8.GetBytes(loginTicket))), Text(2, ClientId), Bytes(4, Integer(1, 0))))));

    internal async Task<UbisoftDemuxChannel> OpenAsync(string serviceName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        var id = (uint)Interlocked.Increment(ref _nextRequestId);
        var response = await RequestAsync(id, Bytes(1, Message(Integer(1, id), Bytes(3, Text(2, serviceName)))), ct, DefaultTimeout);
        var opened = Read(response.Data(3));
        var connectionId = opened.Number(1);
        if (opened.Number(2) != 1 || connectionId is 0 or > uint.MaxValue)
            throw new UbisoftDemuxException(UbisoftDemuxFailure.Rejected, $"Ubisoft refused to open '{serviceName}'.");
        var channel = new UbisoftDemuxChannel(this, (uint)connectionId, serviceName);
        _channels[channel.ConnectionId] = channel;
        return channel;
    }

    private async Task<IReadOnlyList<Field>> RequestAsync(uint id, byte[] upstream, CancellationToken ct, TimeSpan timeout)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<Field>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, pending)) throw new InvalidOperationException("Duplicate Demux request id.");
        try
        {
            ThrowIfFailed();
            await WriteAsync(upstream, ct);
            return await AwaitAsync(pending.Task, timeout, ct, "Demux request");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    internal static async Task<T> AwaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken ct, string what)
    {
        try { return await task.WaitAsync(timeout, ct); }
        catch (TimeoutException e)
        {
            throw new UbisoftDemuxException(UbisoftDemuxFailure.Timeout, $"Ubisoft {what} timed out after {timeout.TotalSeconds:0} s.", e);
        }
    }

    internal async Task WriteAsync(byte[] upstream, CancellationToken ct)
    {
        ThrowIfFailed();
        var frame = Frame(upstream);
        await _writeLock.WaitAsync(ct);
        try
        {
            ThrowIfFailed();
            // The write itself is not cancelled: an interrupted TLS record would poison the stream.
            var write = WriteFrameAsync(frame);
            try { await write.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                Fail(new UbisoftDemuxException(UbisoftDemuxFailure.Closed, "Ubisoft Demux write was abandoned."));
                throw;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
        {
            var failure = new UbisoftDemuxException(UbisoftDemuxFailure.Closed, "Ubisoft Demux connection closed.", e);
            Fail(failure);
            throw failure;
        }
        finally { _writeLock.Release(); }
    }

    private async Task WriteFrameAsync(byte[] frame)
    {
        await _stream.WriteAsync(frame, CancellationToken.None);
        await _stream.FlushAsync(CancellationToken.None);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
                Dispatch(await ReadFrame(_stream, _lifetime.Token));
            Fail(new UbisoftDemuxException(UbisoftDemuxFailure.Closed, "Ubisoft Demux connection closed."));
        }
        catch (UbisoftDemuxException e) { Fail(e); }
        catch (Exception e)
        {
            Fail(new UbisoftDemuxException(UbisoftDemuxFailure.Closed, "Ubisoft Demux connection closed.", e));
        }
    }

    private void Dispatch(byte[] frame)
    {
        var downstream = Read(frame);
        if (downstream.Has(1)) DispatchResponse(Read(downstream.Data(1)));
        if (downstream.Has(2)) DispatchPush(Read(downstream.Data(2)));
    }

    private void DispatchResponse(IReadOnlyList<Field> response)
    {
        var id = (uint)response.Number(1); // absent request_id == 0
        if (_pending.TryGetValue(id, out var waiter)) waiter.TrySetResult(response);
    }

    private void DispatchPush(IReadOnlyList<Field> push)
    {
        if (push.Has(1)) DeliverChannelData(Read(push.Data(1)));
        if (push.Has(2)) CloseChannel(Read(push.Data(2)));
        // keep_alive (3) needs no answer.
        if (push.Has(5))
            throw new UbisoftDemuxException(UbisoftDemuxFailure.Rejected, "Ubisoft reports this client version as outdated.");
        if (push.Has(6)) Raise(ProductStarted, Read(push.Data(6)).Number(1));
        if (push.Has(7)) Raise(ProductEnded, Read(push.Data(7)).Number(1));
    }

    private void DeliverChannelData(IReadOnlyList<Field> data)
    {
        if (_channels.TryGetValue((uint)data.Number(1), out var channel) && data.Data(2).Length > 0)
            channel.Deliver(Unframe(data.Data(2)));
    }

    private void CloseChannel(IReadOnlyList<Field> closed)
    {
        if (!_channels.TryRemove((uint)closed.Number(1), out var channel)) return;
        var reason = closed.Number(2) == 3 ? UbisoftDemuxFailure.Banned : UbisoftDemuxFailure.Closed;
        channel.Close(new UbisoftDemuxException(reason, $"Ubisoft closed the '{channel.ServiceName}' connection."));
    }

    private static void Raise(Action<uint>? handler, ulong productId)
    {
        try { handler?.Invoke((uint)productId); }
        catch { /* subscriber faults must not kill the reader loop */ }
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
            throw new UbisoftDemuxException(failure.Reason, failure.Message, failure);
    }

    private void Fail(UbisoftDemuxException failure)
    {
        Interlocked.CompareExchange(ref _failure, failure, null);
        failure = _failure!;
        foreach (var waiter in _pending.Values) waiter.TrySetException(failure);
        foreach (var channel in _channels.Values) channel.Close(failure);
        _channels.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        Fail(new UbisoftDemuxException(UbisoftDemuxFailure.Closed, "Ubisoft Demux connection was disposed."));
        _lifetime.Cancel();
        await _stream.DisposeAsync();
        try { await _reader; } catch { /* already surfaced through Fail */ }
        _lifetime.Dispose();
    }
}

/// <summary>A service connection on a Demux socket. Service request ids start at 1 (proto3 omits 0).</summary>
internal sealed class UbisoftDemuxChannel
{
    private readonly UbisoftDemuxConnection _connection;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IReadOnlyList<Field>>> _pending = new();
    private int _nextRequestId;
    private UbisoftDemuxException? _closed;

    internal UbisoftDemuxChannel(UbisoftDemuxConnection connection, uint connectionId, string serviceName)
    {
        _connection = connection;
        ConnectionId = connectionId;
        ServiceName = serviceName;
    }

    internal uint ConnectionId { get; }
    internal string ServiceName { get; }

    internal async Task<IReadOnlyList<Field>> CallAsync(byte[] requestFields, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = (uint)Interlocked.Increment(ref _nextRequestId);
        var pending = new TaskCompletionSource<IReadOnlyList<Field>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        if (Volatile.Read(ref _closed) is { } closed)
        {
            _pending.TryRemove(id, out _);
            throw new UbisoftDemuxException(closed.Reason, closed.Message, closed);
        }
        try
        {
            var service = Frame(Bytes(1, Message(Integer(1, id), requestFields)));
            await _connection.WriteAsync(Bytes(2, Bytes(1, Message(Integer(1, ConnectionId), Bytes(2, service)))), ct);
            return await UbisoftDemuxConnection.AwaitAsync(pending.Task, timeout ?? UbisoftDemuxConnection.DefaultTimeout, ct,
                $"'{ServiceName}' request");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Handles one service Downstream (<c>{1: Rsp}</c> or <c>{2: Push}</c>). Service pushes are ignored.</summary>
    internal void Deliver(byte[] serviceDownstream)
    {
        var downstream = Read(serviceDownstream);
        if (!downstream.Has(1)) return;
        var response = Read(downstream.Data(1));
        if (_pending.TryGetValue((uint)response.Number(1), out var waiter)) waiter.TrySetResult(response);
    }

    internal void Close(UbisoftDemuxException reason)
    {
        Interlocked.CompareExchange(ref _closed, reason, null);
        foreach (var waiter in _pending.Values) waiter.TrySetException(reason);
    }
}
