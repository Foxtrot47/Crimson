using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Providers.Ubisoft;

/// <summary>Fetches one signed CDN object. Non-success statuses surface as <see cref="HttpRequestException"/>
/// carrying <see cref="HttpRequestException.StatusCode"/>, so callers can re-sign on 403.</summary>
internal interface IUbisoftCdn
{
    Task<byte[]> GetAsync(string url, long maximumBytes, Action<long>? onBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Keep-alive pool of <see cref="UbisoftCdnConnection"/>s. Signed URLs are sent byte for byte: a URI type
/// would normalise the encoded path the signature covers.
/// </summary>
internal sealed class UbisoftCdnPool : IUbisoftCdn, IAsyncDisposable
{
    /// <summary>A stalled CDN socket must fail the request, not the whole install.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentBag<UbisoftCdnConnection> _idle = [];
    private bool _disposed;

    public async Task<byte[]> GetAsync(string url, long maximumBytes, Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            var (status, location, body) = await RequestAsync(url, maximumBytes, onBytes, cancellationToken);
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                url = location ?? throw new HttpRequestException("Ubisoft CDN redirected without a location.");
                continue;
            }
            if (status != 200)
                throw new HttpRequestException($"Ubisoft CDN returned {status} for {ObjectName(url)}.", null,
                    (HttpStatusCode)status);
            return body;
        }
        throw new HttpRequestException($"Ubisoft CDN redirected too often for {ObjectName(url)}.");
    }

    private async Task<(int Status, string? Location, byte[] Body)> RequestAsync(string url, long maximumBytes,
        Action<long>? onBytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        var (secure, host, target) = SplitUrl(url);
        var connection = await TakeIdleAsync(host, secure);
        try
        {
            connection ??= await UbisoftCdnConnection.OpenAsync(host, secure, deadline.Token);
            var response = await connection.GetAsync(target, maximumBytes, onBytes, deadline.Token);
            await ReleaseAsync(connection);
            return response;
        }
        catch (Exception error)
        {
            if (connection is not null) await connection.DisposeAsync();
            if (IsTransportFailure(error, cancellationToken))
                throw new HttpRequestException($"Ubisoft CDN request failed for {ObjectName(url)}.", error);
            throw;
        }
    }

    /// <summary>A reusable idle connection to the host; idle connections to other hosts are closed.</summary>
    private async Task<UbisoftCdnConnection?> TakeIdleAsync(string host, bool secure)
    {
        while (_idle.TryTake(out var idle))
        {
            if (!idle.Broken && idle.Matches(host, secure)) return idle;
            await idle.DisposeAsync();
        }
        return null;
    }

    private async Task ReleaseAsync(UbisoftCdnConnection connection)
    {
        if (connection.Broken || _disposed) await connection.DisposeAsync();
        else _idle.Add(connection);
    }

    private static bool IsTransportFailure(Exception error, CancellationToken cancellationToken) =>
        error is IOException or SocketException or InvalidDataException or OperationCanceledException
        && !cancellationToken.IsCancellationRequested;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idle.TryTake(out var connection)) await connection.DisposeAsync();
    }

    /// <summary>The object name only: the query string carries the signature and must never be logged.</summary>
    internal static string ObjectName(string url)
    {
        var query = url.IndexOf('?');
        var trimmed = query < 0 ? url : url[..query];
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>Scheme, host and the untouched remainder: parsing through a URI type would re-encode the ACL.</summary>
    internal static (bool Secure, string Host, string Target) SplitUrl(string url)
    {
        var secure = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var offset = secure ? 8 : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        if (offset == 0) throw new ArgumentException("Only http and https CDN URLs are supported.", nameof(url));
        var rest = url[offset..];
        var slash = rest.IndexOf('/');
        var host = slash < 0 ? rest : rest[..slash];
        if (host.Length == 0 || host.IndexOfAny([' ', '\r', '\n', '@']) >= 0)
            throw new ArgumentException("The CDN URL has no usable host.", nameof(url));
        return slash < 0 ? (secure, host, "/") : (secure, host, rest[slash..]);
    }
}

/// <summary>
/// One HTTP/1.1 connection to the download CDN, reused across slices. Opening a connection per slice
/// gets the caller throttled: the edge answers slower and slower until requests time out, which looks
/// like a stalled download. The request line still carries the signed target verbatim, because the
/// signature covers the encoded path and a URI type would normalise it.
/// </summary>
internal sealed class UbisoftCdnConnection : IAsyncDisposable
{
    private const string CrLf = "\r\n";

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly string _host;
    private readonly bool _secure;

    private UbisoftCdnConnection(TcpClient tcp, Stream stream, string host, bool secure)
    {
        _tcp = tcp;
        // Header parsing reads byte by byte; buffering keeps that off the socket.
        _stream = new BufferedStream(stream, 64 * 1024);
        _host = host;
        _secure = secure;
    }

    internal bool Broken { get; private set; }

    internal bool Matches(string host, bool secure) =>
        secure == _secure && string.Equals(host, _host, StringComparison.OrdinalIgnoreCase);

    internal static async Task<UbisoftCdnConnection> OpenAsync(string host, bool secure, CancellationToken token)
    {
        var tcp = new TcpClient();
        try
        {
            var colon = host.LastIndexOf(':');
            var explicitPort = 0;
            var hasPort = colon > 0 && int.TryParse(host[(colon + 1)..], CultureInfo.InvariantCulture, out explicitPort);
            var name = hasPort ? host[..colon] : host;
            var port = hasPort ? explicitPort : secure ? 443 : 80;
            await tcp.ConnectAsync(name, port, token);
            Stream stream = new NetworkStream(tcp.Client, ownsSocket: false);
            if (secure)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = name }, token);
                stream = tls;
            }
            return new(tcp, stream, host, secure);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    internal async Task<(int Status, string? Location, byte[] Body)> GetAsync(string target, long maximumBytes,
        Action<long>? onBytes, CancellationToken token)
    {
        var request = "GET " + target + " HTTP/1.1" + CrLf + "Host: " + _host + CrLf + "Accept: */*" + CrLf
            + "Connection: keep-alive" + CrLf + CrLf;
        // Any failure part-way leaves the stream mid-response: the connection cannot be reused.
        Broken = true;
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        await _stream.FlushAsync(token);

        var head = await ReadHeadAsync(token);
        var status = Status(head);
        var headers = ParseHeaders(head);
        var close = headers.TryGetValue("Connection", out var connection) &&
            connection.Contains("close", StringComparison.OrdinalIgnoreCase);
        var body = await ReadBodyAsync(headers, maximumBytes, onBytes, token);
        // Without a declared length the body ended at end of stream, so the connection is done.
        Broken = close || !headers.ContainsKey("Content-Length");
        return (status, headers.GetValueOrDefault("Location"), body);
    }

    public async ValueTask DisposeAsync()
    {
        Broken = true;
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    private async Task<string> ReadHeadAsync(CancellationToken token)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            if (await _stream.ReadAsync(single, token) == 0) break;
            buffer.Add(single[0]);
            if (buffer.Count >= 4 && buffer[^4] == 13 && buffer[^3] == 10 && buffer[^2] == 13 && buffer[^1] == 10)
                return Encoding.Latin1.GetString(buffer.ToArray(), 0, buffer.Count - 4);
        }
        throw new IOException("Ubisoft CDN response has no header block.");
    }

    private async Task<byte[]> ReadBodyAsync(IReadOnlyDictionary<string, string> headers, long maximumBytes,
        Action<long>? onBytes, CancellationToken token)
    {
        if (headers.TryGetValue("Content-Length", out var declared) &&
            long.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            return await ReadSizedBodyAsync(length, maximumBytes, onBytes, token);
        return await ReadToEndAsync(maximumBytes, onBytes, token);
    }

    private async Task<byte[]> ReadSizedBodyAsync(long length, long maximumBytes, Action<long>? onBytes, CancellationToken token)
    {
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("Ubisoft CDN body is too large.");
        var body = new byte[length];
        // Reported while it arrives: a rate sampled from completed slices looks like a sawtooth.
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await _stream.ReadAsync(body.AsMemory(offset, (int)Math.Min(256 * 1024, body.Length - offset)), token);
            if (read == 0) throw new EndOfStreamException("Ubisoft CDN closed the body early.");
            offset += read;
            onBytes?.Invoke(read);
        }
        return body;
    }

    private async Task<byte[]> ReadToEndAsync(long maximumBytes, Action<long>? onBytes, CancellationToken token)
    {
        var buffered = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await _stream.ReadAsync(chunk, token);
            if (read == 0) break;
            if (buffered.Length + read > maximumBytes) throw new InvalidDataException("Ubisoft CDN body is too large.");
            buffered.Write(chunk, 0, read);
            onBytes?.Invoke(read);
        }
        return buffered.ToArray();
    }

    private static int Status(string head)
    {
        var line = head.Split(CrLf, 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return line.Length > 1 && int.TryParse(line[1], CultureInfo.InvariantCulture, out var status)
            ? status : throw new InvalidDataException("Ubisoft CDN returned a malformed status line.");
    }

    private static Dictionary<string, string> ParseHeaders(string head)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = head.Split(CrLf);
        for (var index = 1; index < lines.Length; index++)
        {
            var colon = lines[index].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[index][..colon].Trim()] = lines[index][(colon + 1)..].Trim();
        }
        return headers;
    }
}
