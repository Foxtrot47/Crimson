using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft.Protocol;

public enum UbisoftCloudSaveMethod { Put = 1, Get = 2, Delete = 3 }
public sealed record UbisoftCloudSaveItem(string Name, int? WriteLength = null, string? Md5Base64 = null);

/// <summary>A pre-signed cloud-save HTTP request. The URL carries a signature: never log it.</summary>
public sealed record UbisoftSignedRequest(string Url, string Header)
{
    public override string ToString() => "signed cloud save request (redacted)";
}

internal sealed class UbisoftCloudSaveService
{
    internal const string ServiceName = "cloudsave_service";
    private const uint StatusOk = 1;
    private readonly UbisoftDemuxChannel _channel;

    private UbisoftCloudSaveService(UbisoftDemuxChannel channel) => _channel = channel;

    internal static async Task<UbisoftCloudSaveService> OpenAsync(UbisoftDemuxConnection connection, CancellationToken ct) =>
        new(await connection.OpenAsync(ServiceName, ct));

    internal async Task<IReadOnlyList<UbisoftSignedRequest>> GetUrlsAsync(UbisoftOwnershipToken token, uint productId,
        UbisoftCloudSaveMethod method, IReadOnlyList<UbisoftCloudSaveItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var response = await _channel.CallAsync(Bytes(2, EncodeRequest(token, productId, method, items)), ct);
        var answer = Read(response.Data(2));
        var status = answer.Number(1);
        if (status != StatusOk)
            throw new UbisoftDemuxException(UbisoftDemuxFailure.Rejected, $"Ubisoft refused the cloud save request (status {status}).");
        var requests = answer.All(2).Select(Read).Select(r => new UbisoftSignedRequest(r.String(2), r.String(1))).ToList();
        if (requests.Count != items.Count)
            throw new InvalidDataException($"Ubisoft signed {requests.Count} of {items.Count} cloud save requests.");
        return requests;
    }

    /// <summary><c>CloudsaveUrlReq</c>; proto3 zero/empty values are omitted.</summary>
    internal static byte[] EncodeRequest(UbisoftOwnershipToken token, uint productId, UbisoftCloudSaveMethod method,
        IReadOnlyList<UbisoftCloudSaveItem> items) =>
        Message([Text(1, token.Token), Integer(2, productId), Integer(3, (ulong)method), .. items.Select(EncodeItem)]);

    private static byte[] EncodeItem(UbisoftCloudSaveItem item)
    {
        ArgumentException.ThrowIfNullOrEmpty(item.Name);
        var options = Message(
            item.WriteLength is > 0 and var length ? Integer(1, (ulong)length) : [],
            string.IsNullOrEmpty(item.Md5Base64) ? [] : Text(2, item.Md5Base64));
        return Bytes(4, Message(Text(1, item.Name), options.Length == 0 ? [] : Bytes(2, options)));
    }
}
