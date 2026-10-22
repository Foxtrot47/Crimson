using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft.Protocol;

public enum UbisoftActivationResult { Success, NotOwned, Failure, ExceededActivations, TimeOut, ServerError }
public sealed class UbisoftActivationException(UbisoftActivationResult result)
    : Exception($"Ubisoft activation failed: {result}.")
{
    public UbisoftActivationResult Result { get; } = result;
}
public sealed record UbisoftPcSession(byte[] PcTicket, IReadOnlyList<uint> OwnedProducts)
{
    public IReadOnlyList<uint> AddonsToValidate { get; init; } = [];
}
public interface IUbisoftTickets
{
    Task<UbisoftPcSession> GetPcSession(string loginTicket, string sessionId, uint productId, CancellationToken token);
    Task<byte[]> Activate(string loginTicket, string sessionId, uint productId, byte[] challenge, CancellationToken token);
}

/// <summary>Live issuance only. Every operation owns its TLS connection; no replay or automatic activation retry.</summary>
public sealed class UbisoftTickets : IUbisoftTickets
{
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    public UbisoftTickets() : this(ConnectTls) { }
    internal UbisoftTickets(Func<CancellationToken, Task<Stream>> connect) => _connect = connect;

    private static async Task<Stream> ConnectTls(CancellationToken token)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync("dmx.upc.ubisoft.com", 443, token);
            // NetworkStream owns the socket. Standard OS certificate and hostname validation stays enabled.
            var tls = new SslStream(new NetworkStream(tcp.Client, ownsSocket: true), leaveInnerStreamOpen: false);
            try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "dmx.upc.ubisoft.com" }, token); }
            catch { tls.Dispose(); throw; }
            return tls;
        }
        catch { tcp.Dispose(); throw; }
    }
    public async Task<UbisoftPcSession> GetPcSession(string loginTicket, string sessionId, uint productId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using var stream = await _connect(deadline.Token);
        var client = new UbisoftDemuxClient(stream);
        await client.Authenticate(loginTicket, deadline.Token);
        var (connection, catalog) = await client.InitializeOwnership(deadline.Token);
        var info = Message(Integer(1, productId), Integer(2, 0), Integer(3, productId));
        var response = await client.Call(connection, 1, Message(Bytes(14, info), Text(30, loginTicket), Text(31, sessionId)), deadline.Token);
        var ticket = Read(response.Data(13));
        if (ticket.Number(1) != 1 || ticket.Data(2).Length == 0)
            throw new InvalidDataException("Ubisoft did not issue a PC session ticket.");
        return new(ticket.Data(2), UbisoftDemuxClient.SessionProducts(catalog))
        { AddonsToValidate = UbisoftDemuxClient.SessionAddons(catalog) };
    }
    public async Task<byte[]> Activate(string loginTicket, string sessionId, uint productId, byte[] challenge, CancellationToken token)
    {
        if (challenge.Length is 0 or > 65536) throw new ArgumentException("Invalid activation challenge size.", nameof(challenge));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        await using var stream = await _connect(deadline.Token);
        var client = new UbisoftDemuxClient(stream);
        await client.Authenticate(loginTicket, deadline.Token);
        var (ownershipConnection, _) = await client.InitializeOwnership(deadline.Token);
        var ownershipReply = await client.Call(ownershipConnection, 1,
            Message(Bytes(28, Integer(1, productId)), Text(30, loginTicket), Text(31, sessionId)), deadline.Token);
        var ownership = Read(ownershipReply.Data(27));
        if (ownership.Number(1) != 1 || ownership.Data(2).Length == 0)
            throw new InvalidDataException("Ubisoft did not issue an ownership token.");
        var denuvo = await client.Open("denuvo_service", 2, deadline.Token);
        var response = await client.Call(denuvo, 1, Bytes(2,
            Message(Bytes(1, ownership.Data(2)), Bytes(2, challenge))), deadline.Token);
        var result = (UbisoftActivationResult)response.Number(2); // proto3 omits Success=0
        if (result != UbisoftActivationResult.Success) throw new UbisoftActivationException(result);
        var answer = Read(response.Data(3)).Data(1);
        if (answer.Length == 0) throw new InvalidDataException("Ubisoft returned an empty activation answer.");
        return answer;
    }
}

internal sealed class UbisoftDemuxClient(Stream stream)
{
    internal async Task Authenticate(string ticket, CancellationToken token)
    {
        await WriteFrame(stream, Bytes(2, Bytes(4, Integer(1, 13389))), token);
        var auth = Message(Bytes(1, Bytes(3, System.Text.Encoding.UTF8.GetBytes(ticket))), Text(2, "uplay_pc"), Bytes(4, Integer(1, 0)));
        await WriteFrame(stream, Bytes(1, Message(Integer(1, 0), Bytes(2, auth))), token);
        if (Read((await Await(0, null, token)).Data(2)).Number(1) != 1)
            throw new InvalidDataException("Ubisoft demux authentication was rejected.");
    }
    internal async Task<uint> Open(string service, uint requestId, CancellationToken token)
    {
        await WriteFrame(stream, Bytes(1, Message(Integer(1, requestId), Bytes(3, Text(2, service)))), token);
        var response = Read((await Await(requestId, null, token)).Data(3));
        if (!response.Has(1) || response.Number(1) == 0) throw new InvalidDataException("Ubisoft service connection failed.");
        return checked((uint)response.Number(1));
    }
    internal async Task<(uint Connection, byte[] Catalog)> InitializeOwnership(CancellationToken token)
    {
        var connection = await Open("ownership_service", 1, token);
        var response = await Call(connection, 0, Bytes(2, Message(Integer(2, 1), Integer(3, 7), Integer(5, 0))), token);
        var initialize = response.Data(2);
        if (Read(initialize).Number(1) != 1) throw new InvalidDataException("Ubisoft ownership initialization failed.");
        return (connection, Read(initialize).Data(2));
    }
    internal async Task<IReadOnlyList<Field>> Call(uint connection, uint requestId, byte[] operation, CancellationToken token)
    {
        var inner = Frame(Bytes(1, Message(Integer(1, requestId), operation)));
        await WriteFrame(stream, Bytes(2, Bytes(1, Message(Integer(1, connection), Bytes(2, inner)))), token);
        return await Await(requestId, connection, token);
    }
    internal async Task<IReadOnlyList<Field>> Await(uint requestId, uint? connection, CancellationToken token)
    {
        while (true)
        {
            var outer = Read(await ReadFrame(stream, token));
            byte[] response;
            if (connection is { } expected)
            {
                var data = Read(Read(outer.Data(2)).Data(1));
                if (data.Number(1) != expected) continue; // asynchronous traffic for a different connection
                if (data.Data(2).Length == 0) continue;
                response = Read(Unframe(data.Data(2))).Data(1);
            }
            else response = outer.Data(1);
            if (response.Length == 0) continue;
            var fields = Read(response);
            if (fields.Number(1) == requestId) return fields; // omitted requestId means zero
        }
    }
    internal static IReadOnlyList<uint> SessionAddons(byte[] catalog) => Read(catalog)
        .Where(f => f.Number == 1 && f.WireType == 2).Select(f => Read(f.Data))
        .Where(f => f.Number(20) == 1 && f.Number(8) == 1 && f.Number(13) is 0 or 3)
        .Select(f => checked((uint)f.Number(1))).Where(id => id != 0).ToList();
    internal static IReadOnlyList<uint> SessionProducts(byte[] catalog) => Read(catalog)
        .Where(f => f.Number == 1 && f.WireType == 2).Select(f => Read(f.Data))
        .Where(f => f.Number(20) == 1 && f.Number(8) is 0 or 1 && f.Number(13) is 0 or 3)
        .Select(f => checked((uint)f.Number(1))).Where(id => id != 0).ToList();
}
