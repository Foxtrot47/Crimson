using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft.Protocol;

public sealed class UbisoftDownloadDeniedException(uint productId, string message) : Exception(message)
{
    public uint ProductId { get; } = productId;
}

internal sealed class UbisoftDownloadService
{
    internal const string ServiceName = "download_service";
    internal const string NetworkId = "5af8ea68-c4ba-4d8c-b7b1-ac4fd45deb9b";
    internal const int BatchSize = 64;

    private readonly UbisoftDemuxChannel _channel;
    private long _lifetimeTicks = TimeSpan.FromHours(1).Ticks;

    private UbisoftDownloadService(UbisoftDemuxChannel channel) => _channel = channel;

    /// <summary>Lifetime of the most recently signed URLs (<c>ttl_seconds</c>); one hour until the first answer.</summary>
    internal TimeSpan UrlLifetime => TimeSpan.FromTicks(Interlocked.Read(ref _lifetimeTicks));

    internal static async Task<UbisoftDownloadService> OpenAsync(UbisoftDemuxConnection connection, UbisoftOwnershipToken token, CancellationToken ct)
    {
        var channel = await connection.OpenAsync(ServiceName, ct);
        var response = await channel.CallAsync(Bytes(2, Message(Text(5, token.Token), Text(6, NetworkId))), ct);
        if (Read(response.Data(2)).Number(1) != 1)
            throw new UbisoftDemuxException(UbisoftDemuxFailure.Rejected, "Ubisoft refused to initialize the download service.");
        return new(channel);
    }

    internal async Task<IReadOnlyList<IReadOnlyList<string>>> GetUrlsAsync(uint productId, IReadOnlyList<string> relativePaths, CancellationToken ct)
    {
        var result = new List<IReadOnlyList<string>>(relativePaths.Count);
        foreach (var batch in relativePaths.Chunk(BatchSize))
        {
            var request = Bytes(3, Bytes(1, Message([Integer(1, productId), .. batch.Select(path => Text(2, path))])));
            var response = Read((await _channel.CallAsync(request, ct)).Data(3));
            var answers = response.All(1).Select(Read).ToList();
            if (answers.Count != 1) throw new InvalidDataException("Ubisoft returned an unexpected download URL answer.");
            if (answers[0].Number(1) == 1)
                throw new UbisoftDownloadDeniedException(productId, $"Ubisoft reports product {productId} as not owned for download.");
            var urls = answers[0].All(2).Select(set => (IReadOnlyList<string>)Read(set).All(1)
                .Select(url => Encoding.UTF8.GetString(url)).ToList()).ToList();
            if (urls.Count != batch.Length)
                throw new InvalidDataException($"Ubisoft signed {urls.Count} of {batch.Length} download paths.");
            result.AddRange(urls);
            if (response.Number(2) is > 0 and <= 86400 and var ttl)
                Interlocked.Exchange(ref _lifetimeTicks, TimeSpan.FromSeconds((double)ttl).Ticks);
        }
        return result;
    }
}
