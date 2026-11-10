using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Fetches the Demux configuration catalog: Ubisoft's own source for per-game configuration, which
/// carries titles, artwork hashes, executables and the download manifest id for every product.
///
/// This replaces reading the official client's local cache. The sequence and the two fixed branch
/// acknowledgements are the ones the client sends; the catalog arrives as a stream of frames, so it
/// is collected until the service goes quiet rather than read as one message.
/// </summary>
public sealed class UbisoftConfigCatalogClient(Func<CancellationToken, Task<Stream>>? connect = null)
{
    private const string DemuxHost = "dmx.upc.ubisoft.com";
    private const string ClientVersion = "174.2";
    private const int BuildNumber = 13389;

    /// <summary>Two fixed frames the client sends while the catalog subscriptions are set up.</summary>
    private static readonly byte[] Ack7 = Convert.FromHexString(
        "0000001812160a14080212100000000c0a0a08011206100118072800");
    private static readonly byte[] Ack8 = Convert.FromHexString(
        "0000001212100a0e0801120a000000060a0408016a00");

    /// <summary>The catalog starts streaming a few seconds after the subscription is sent.</summary>
    private static readonly TimeSpan FirstFrameWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TotalWindow = TimeSpan.FromSeconds(60);
    private const int MaximumCatalogBytes = 8 * 1024 * 1024;

    public async Task<byte[]> FetchAsync(UbisoftSession session, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TotalWindow);
        await using var stream = await (connect ?? ConnectTls)(deadline.Token);
        var ticket = Encoding.UTF8.GetBytes(session.Ticket);
        var sessionId = Encoding.UTF8.GetBytes(session.SessionId);

        // hello, utility subscription, build number, simple authentication
        await Send(stream, Bytes(1, Message(Integer(1, 2), Bytes(5, Message(Text(1, ClientVersion), Integer(2, 0))))), deadline.Token);
        await Send(stream, Bytes(1, Message(Integer(1, 1), Bytes(6, Message(
            Text(1, "utility_service"), Bytes(2, Bytes(1, Bytes(1, []))))))), deadline.Token);
        await Send(stream, Bytes(2, Bytes(4, Integer(1, BuildNumber))), deadline.Token);
        await Send(stream, Bytes(1, Message(Integer(1, 2), Bytes(2, Bytes(1, Bytes(3, ticket))))), deadline.Token);
        await Drain(stream, TimeSpan.FromSeconds(2), deadline.Token);

        // friends and ownership subscriptions, then the session authentication handshake
        await Send(stream, Service("friends_service", 0), deadline.Token);
        await Send(stream, Service("ownership_service", 1), deadline.Token);
        await Drain(stream, TimeSpan.FromSeconds(3), deadline.Token);
        await Send(stream, SessionAuth(ticket, sessionId), deadline.Token);
        await SendRaw(stream, Ack7, deadline.Token);
        await SendRaw(stream, Ack8, deadline.Token);
        await Send(stream, Service("denuvo_service", 3), deadline.Token);
        var configSubscription = Message(
            Text(1, "client_configuration_service"),
            Bytes(2, Bytes(1, Message(Integer(1, 0), Bytes(3, Bytes(1, Integer(1, BuildNumber)))))));
        await Send(stream, Bytes(1, Message(Integer(1, 4), Bytes(6, configSubscription))), deadline.Token);

        // Wait for the catalog to start, then collect until the service goes quiet.
        return await Drain(stream, FirstFrameWindow, deadline.Token);
    }

    private static byte[] Service(string name, int counter) =>
        Bytes(1, Message(Integer(1, (ulong)counter), Bytes(3, Text(2, name))));

    private static byte[] SessionAuth(byte[] ticket, byte[] sessionId)
    {
        var inner = Bytes(1, Message(
            Integer(1, 2),
            Bytes(22, Message(Integer(1, 1), Integer(4, 0), Bytes(5, ticket), Text(6, "en-US"),
                Integer(7, 0), Bytes(8, sessionId)))));
        return Bytes(2, Bytes(1, Message(Integer(1, 1), Bytes(2, Frame(Bytes(1, inner))))));
    }

    /// <summary>
    /// Reads frames until the service goes quiet. <paramref name="firstFrame"/> bounds the wait for the
    /// first frame (a subscription can take seconds to start); afterwards a short silence ends the read.
    /// </summary>
    private async Task<byte[]> Drain(Stream stream, TimeSpan firstFrame, CancellationToken token)
    {
        var collected = new MemoryStream();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(TotalWindow);
        var waiting = firstFrame;
        while (!window.IsCancellationRequested)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(window.Token);
            silence.CancelAfter(waiting);
            byte[] frame;
            try { frame = await UbisoftWire.ReadFrame(stream, silence.Token); }
            catch (OperationCanceledException) when (silence.IsCancellationRequested) { break; } // service went quiet
            catch (EndOfStreamException) { break; }
            waiting = IdleWindow;
            if (frame.Length == 0) continue;
            collected.Write(frame);
            if (collected.Length >= MaximumCatalogBytes) break;
        }
        return collected.ToArray();
    }

    private static async Task Send(Stream stream, byte[] body, CancellationToken token)
    {
        await UbisoftWire.WriteFrame(stream, body, token);
    }

    /// <summary>For frames that are already length-prefixed (the two recorded branch acknowledgements).</summary>
    private static async Task SendRaw(Stream stream, byte[] frame, CancellationToken token)
    {
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    private static async Task<Stream> ConnectTls(CancellationToken token)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(DemuxHost, 443, token);
            var tls = new SslStream(new NetworkStream(tcp.Client, ownsSocket: true), leaveInnerStreamOpen: false);
            try
            {
                // The Demux service rejects TLS 1.3 by closing the connection; it speaks TLS 1.2 only.
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = DemuxHost,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                }, token);
            }
            catch { tls.Dispose(); throw; }
            return tls;
        }
        catch { tcp.Dispose(); throw; }
    }
}
