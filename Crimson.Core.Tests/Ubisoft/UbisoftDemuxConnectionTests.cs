using System.Text;
using System.Threading.Channels;
using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftDemuxConnectionTests
{
    [Fact]
    public async Task HandshakeSendsClientVersionThenAuthenticatesWithTheLoginTicket()
    {
        using var server = new FakeDemuxServer();
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "login-ticket", default);

        // Body of the first frame: Bytes(2, Bytes(4, Integer(1, 13389)))
        Assert.Equal(Convert.FromHexString("1205220308CD68"), server.Upstream[0]);
        var ticket = Encoding.UTF8.GetBytes("login-ticket");
        var expected = Bytes(1, Message(Integer(1, 0), Bytes(2, Message(
            Bytes(1, Bytes(3, ticket)), Bytes(2, "uplay_pc"u8.ToArray()), Bytes(4, [0x08, 0x00])))));
        Assert.Equal(expected, server.Upstream[1]);
    }

    [Theory]
    [InlineData(2, UbisoftDemuxFailure.Expired)]
    [InlineData(3, UbisoftDemuxFailure.Banned)]
    [InlineData(0, UbisoftDemuxFailure.Rejected)]
    public async Task AuthenticationFailuresAreTyped(int flag, UbisoftDemuxFailure reason)
    {
        using var server = new FakeDemuxServer { OnAuthenticate = _ => Bytes(2, flag == 0 ? Integer(1, 0) : Integer(flag, 1)) };
        var error = await Assert.ThrowsAsync<UbisoftDemuxException>(() => UbisoftDemuxConnection.ConnectAsync(server, "t", default));
        Assert.Equal(reason, error.Reason);
        Assert.True(server.Disposed);
    }

    [Fact]
    public async Task ConcurrentChannelCallsAreCorrelatedByServiceRequestId()
    {
        var held = new List<(uint Connection, ulong Id, string Value)>();
        FakeDemuxServer server = null!;
        server = new FakeDemuxServer
        {
            OnServiceRequest = (_, connection, request) =>
            {
                lock (held)
                {
                    held.Add((connection, request.Number(1), request.String(9)));
                    if (held.Count < 2) return null;
                    // Answer in reverse order: the second call's reply arrives first.
                    foreach (var (c, id, value) in Enumerable.Reverse(held)) server.SendService(c, id, Text(9, "reply:" + value));
                    return null;
                }
            }
        };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);
        var channel = await connection.OpenAsync("probe_service", default);

        var first = channel.CallAsync(Text(9, "a"), default);
        var second = channel.CallAsync(Text(9, "b"), default);

        Assert.Equal("reply:a", (await first).String(9));
        Assert.Equal("reply:b", (await second).String(9));
        Assert.Equal(new ulong[] { 1, 2 }, held.Select(h => h.Id).Order());
        Assert.All(held, h => Assert.Equal(channel.ConnectionId, h.Connection));
    }

    [Fact]
    public async Task ServerClosingTheSocketFailsPendingCallsAsClosed()
    {
        FakeDemuxServer server = null!;
        server = new FakeDemuxServer { OnServiceRequest = (_, _, _) => { server.Hangup(); return null; } };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);
        var channel = await connection.OpenAsync("probe_service", default);

        var error = await Assert.ThrowsAsync<UbisoftDemuxException>(() => channel.CallAsync(Text(9, "x"), default));
        Assert.Equal(UbisoftDemuxFailure.Closed, error.Reason);
        var reopen = await Assert.ThrowsAsync<UbisoftDemuxException>(() => connection.OpenAsync("other", default));
        Assert.Equal(UbisoftDemuxFailure.Closed, reopen.Reason);
    }

    [Fact]
    public async Task UnansweredCallTimesOut()
    {
        using var server = new FakeDemuxServer { OnServiceRequest = (_, _, _) => null };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);
        var channel = await connection.OpenAsync("probe_service", default);

        var error = await Assert.ThrowsAsync<UbisoftDemuxException>(() =>
            channel.CallAsync(Text(9, "x"), default, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(UbisoftDemuxFailure.Timeout, error.Reason);
    }

    [Fact]
    public async Task OwnershipInitializeProjectsProductsAndDropsSecrets()
    {
        var game = Message(Integer(1, 2688), Text(2, "SECRET-CDKEY"), Integer(5, 3452), Integer(8, 0), Integer(13, 3),
            Integer(16, 3962), Integer(16, 3672), Text(17, "root:\n  name: l1\n"), Integer(19, 2688), Integer(20, 1),
            Text(21, "ABCDEF0123456789ABCDEF0123456789ABCDEF01"), Integer(23, 2), Text(24, "SECRET-ENCRYPTION"),
            Integer(25, 7), Bytes(26, Message(Integer(1, 7), Text(2, "public"))), Text(30, "space"), Text(31, "app"),
            Integer(33, 1), Integer(34, 2), Integer(36, 2), Integer(37, 1), Integer(38, 1));
        var addon = Message(Integer(1, 3962), Integer(8, 1), Integer(13, 3));
        byte[]? initializeRequest = null;
        using var server = new FakeDemuxServer
        {
            OnServiceRequest = (service, _, request) =>
            {
                Assert.Equal("ownership_service", service);
                Assert.Equal(1UL, request.Number(1));
                initializeRequest = request.Data(2);
                return Bytes(2, Message(Integer(1, 1), Bytes(2, Message(Bytes(1, game), Bytes(1, addon)))));
            }
        };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);

        var ownership = await UbisoftOwnershipService.OpenAsync(connection, default);

        Assert.Equal(Message(Integer(2, 1), Integer(3, 7), Integer(5, 0)), initializeRequest);
        var wd2 = ownership.Products[0];
        Assert.Equal(new UbisoftOwnedProduct(2688, true, 0, 3, 2688, 3452, "root:\n  name: l1\n",
            "ABCDEF0123456789ABCDEF0123456789ABCDEF01", 7, wd2.Branches, wd2.Associations, "space", "app", 2, 1, 2, 1, true, 2), wd2);
        Assert.Equal([new UbisoftProductBranch(7, "public")], wd2.Branches);
        Assert.Equal(new uint[] { 3962, 3672 }, wd2.Associations);
        var dlc = ownership.Products[1];
        Assert.False(dlc.Owned);
        Assert.Null(dlc.LatestManifest);
        Assert.Null(dlc.Configuration);
        var raw = Encoding.UTF8.GetString(ownership.InitializeResponse);
        Assert.DoesNotContain("SECRET", raw);
        Assert.Equal([2688u, 3962u], UbisoftOwnershipService.ParseProducts(ownership.InitializeResponse).Select(p => p.ProductId));
    }

    [Fact]
    public async Task OwnershipTokenCarriesExpiryAndIsRedactedWhenPrinted()
    {
        using var server = new FakeDemuxServer
        {
            OnServiceRequest = (_, _, request) => request.Has(2)
                ? Bytes(2, Integer(1, 1))
                : Bytes(27, Message(Integer(1, 1), Text(2, "owner-token"), Integer(3, 1_900_000_000)))
        };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);
        var ownership = await UbisoftOwnershipService.OpenAsync(connection, default);

        var token = await ownership.GetOwnershipTokenAsync(2688, default);

        Assert.Equal("owner-token", token.Token);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), token.ExpiresAt);
        Assert.DoesNotContain("owner-token", token.ToString());
        var request = Read(Read(Read(Unframe(Read(Read(Read(server.Upstream[^1]).Data(2)).Data(1)).Data(2))).Data(1)).Data(28));
        Assert.Equal(2688UL, request.Number(1));
    }

    [Fact]
    public async Task DownloadUrlsAreSignedInBatchesOfSixtyFourInRequestOrder()
    {
        var batches = new List<int>();
        using var server = new FakeDemuxServer
        {
            OnServiceRequest = (service, _, request) =>
            {
                Assert.Equal("download_service", service);
                if (request.Has(2))
                {
                    var init = Read(request.Data(2));
                    Assert.Equal("owner-token", init.String(5));
                    Assert.Equal(UbisoftDownloadService.NetworkId, init.String(6));
                    return Bytes(2, Integer(1, 1));
                }
                var url = Read(Read(request.Data(3)).Data(1));
                Assert.Equal(2688UL, url.Number(1));
                var paths = url.All(2).Select(p => Encoding.UTF8.GetString(p)).ToList();
                batches.Add(paths.Count);
                return Bytes(3, Message(Bytes(1, Message([.. paths.Select(p =>
                    Bytes(2, Message(Text(1, "http://a/" + p), Text(1, "http://b/" + p))))])), Integer(2, 1800)));
            }
        };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);
        var download = await UbisoftDownloadService.OpenAsync(connection, new("owner-token", DateTimeOffset.MaxValue), default);
        var paths = Enumerable.Range(0, 130).Select(i => $"slices_v3/{i}").ToList();

        var urls = await download.GetUrlsAsync(2688, paths, default);

        Assert.Equal([64, 64, 2], batches);
        Assert.Equal(paths.Select(p => new[] { "http://a/" + p, "http://b/" + p }), urls.Select(u => u.ToArray()));
        Assert.Equal(TimeSpan.FromSeconds(1800), download.UrlLifetime);
    }

    [Fact]
    public async Task NotOwnedDownloadIsDenied()
    {
        using var server = new FakeDemuxServer
        {
            OnServiceRequest = (_, _, request) => request.Has(2)
                ? Bytes(2, Integer(1, 1))
                : Bytes(3, Message(Bytes(1, Integer(1, 1)), Integer(2, 3600)))
        };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);
        var download = await UbisoftDownloadService.OpenAsync(connection, new("owner-token", DateTimeOffset.MaxValue), default);

        var error = await Assert.ThrowsAsync<UbisoftDownloadDeniedException>(() =>
            download.GetUrlsAsync(2688, ["manifests/X.manifest"], default));
        Assert.Equal(2688u, error.ProductId);
    }

    [Fact]
    public async Task CloudSaveUrlRequestEncodesTokenProductMethodAndItems()
    {
        IReadOnlyList<Field>? sent = null;
        using var server = new FakeDemuxServer
        {
            OnServiceRequest = (service, _, request) =>
            {
                Assert.Equal("cloudsave_service", service);
                sent = Read(request.Data(2));
                return Bytes(2, Message(Integer(1, 1),
                    Bytes(2, Message(Text(1, "Content-MD5: x"), Text(2, "https://s3/1"))),
                    Bytes(2, Message(Text(2, "https://s3/2")))));
            }
        };
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(server, "t", default);
        var cloud = await UbisoftCloudSaveService.OpenAsync(connection, default);

        var signed = await cloud.GetUrlsAsync(new("owner-token", DateTimeOffset.MaxValue), 2688, UbisoftCloudSaveMethod.Put,
            [new("1.save", 512, "bWQ1"), new("2.save")], default);

        Assert.Equal([new UbisoftSignedRequest("https://s3/1", "Content-MD5: x"), new UbisoftSignedRequest("https://s3/2", "")], signed);
        Assert.NotNull(sent);
        Assert.Equal("owner-token", sent.String(1));
        Assert.Equal(2688UL, sent.Number(2));
        Assert.Equal(1UL, sent.Number(3));
        var items = sent.All(4).Select(Read).ToList();
        Assert.Equal(["1.save", "2.save"], items.Select(i => i.String(1)));
        var options = Read(items[0].Data(2));
        Assert.Equal(512UL, options.Number(1));
        Assert.Equal("bWQ1", options.String(2));
        Assert.False(items[1].Has(2));
    }
}

/// <summary>
/// In-memory Demux server: answers authentication and open_connection itself and hands service requests
/// (the inner service <c>Req</c> fields) to <see cref="OnServiceRequest"/>.
/// </summary>
internal sealed class FakeDemuxServer : Stream
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly MemoryStream _written = new();
    private readonly Dictionary<uint, string> _services = [];
    private byte[] _current = [];
    private int _offset;
    private uint _nextConnection = 40;

    public List<byte[]> Upstream { get; } = [];
    public bool Disposed { get; private set; }
    /// <summary>Returns the AuthenticateRsp-bearing Rsp fields; the reply omits request_id (proto default 0).</summary>
    public Func<IReadOnlyList<Field>, byte[]> OnAuthenticate { get; init; } = _ => Bytes(2, Integer(1, 1));
    /// <summary>(service, connection, service Req fields) -> service Rsp fields without request_id, or null to stay silent.</summary>
    public Func<string, uint, IReadOnlyList<Field>, byte[]?>? OnServiceRequest { get; init; }

    public void Send(byte[] downstream) => _inbound.Writer.TryWrite(Frame(downstream));
    public void SendService(uint connection, ulong id, byte[] rsp) => Send(Bytes(2, Bytes(1, Message(Integer(1, connection),
        Bytes(2, Frame(Bytes(1, Message(id == 0 ? [] : Integer(1, id), rsp))))))));
    public void Hangup() => _inbound.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset == _current.Length)
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_inbound.Reader.TryRead(out var next)) return 0;
            (_current, _offset) = (next, 0);
        }
        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeDemuxServer));
        _written.Write(buffer.Span);
        while (TryTakeFrame(out var body))
        {
            lock (Upstream) Upstream.Add(body);
            Handle(body);
        }
        return ValueTask.CompletedTask;
    }

    private bool TryTakeFrame(out byte[] body)
    {
        body = [];
        var bytes = _written.ToArray();
        if (bytes.Length < 4) return false;
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes);
        if (bytes.Length < size + 4) return false;
        body = bytes[4..(size + 4)];
        _written.SetLength(0);
        _written.Write(bytes, size + 4, bytes.Length - size - 4);
        return true;
    }

    private void Handle(byte[] body)
    {
        var upstream = UbisoftWire.Read(body);
        if (upstream.Has(1))
        {
            var request = UbisoftWire.Read(upstream.Data(1));
            var id = request.Number(1);
            if (request.Has(2)) Send(Bytes(1, Message(id == 0 ? [] : Integer(1, id), OnAuthenticate(UbisoftWire.Read(request.Data(2))))));
            if (request.Has(3))
            {
                var connection = _nextConnection++;
                _services[connection] = UbisoftWire.Read(request.Data(3)).String(2);
                Send(Bytes(1, Message(Integer(1, id), Bytes(3, Message(Integer(1, connection), Integer(2, 1))))));
            }
        }
        if (!upstream.Has(2)) return;
        var push = UbisoftWire.Read(upstream.Data(2));
        if (!push.Has(1)) return;
        var data = UbisoftWire.Read(push.Data(1));
        var connectionId = (uint)data.Number(1);
        var serviceRequest = UbisoftWire.Read(UbisoftWire.Read(Unframe(data.Data(2))).Data(1));
        if (OnServiceRequest?.Invoke(_services[connectionId], connectionId, serviceRequest) is { } reply)
            SendService(connectionId, serviceRequest.Number(1), reply);
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        _inbound.Writer.TryComplete();
        base.Dispose(disposing);
    }
}
