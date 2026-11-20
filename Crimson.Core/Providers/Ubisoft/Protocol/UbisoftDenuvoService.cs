using System;
using System.IO;
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

/// <summary>denuvo_service: exchanges a game's activation challenge for its game token. Never retried automatically.</summary>
internal static class UbisoftDenuvoService
{
    internal const string ServiceName = "denuvo_service";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    internal static async Task<byte[]> GetGameTokenAsync(UbisoftDemuxConnection c, UbisoftOwnershipToken t, byte[] challenge, CancellationToken ct)
    {
        if (challenge.Length is 0 or > 65536) throw new ArgumentException("Invalid activation challenge size.", nameof(challenge));
        var channel = await c.OpenAsync(ServiceName, ct);
        var response = await channel.CallAsync(Bytes(2, Message(Text(1, t.Token), Bytes(2, challenge))), ct, Timeout);
        var result = (UbisoftActivationResult)response.Number(2); // Success = 0 is omitted on the wire
        if (result != UbisoftActivationResult.Success) throw new UbisoftActivationException(result);
        var answer = Read(response.Data(3)).Data(1);
        if (answer.Length == 0) throw new InvalidDataException("Ubisoft returned an empty activation answer.");
        return answer;
    }
}
