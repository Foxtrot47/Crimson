using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft.Protocol;

public sealed record UbisoftProductBranch(uint Id, string Name);

/// <summary>One <c>OwnedGame</c> from ownership_service Initialize. CD keys and encryption keys are never retained.</summary>
public sealed record UbisoftOwnedProduct(
    uint ProductId, bool Owned, uint ProductType, uint State, uint UplayId, uint DownloadId,
    string? Configuration, string? LatestManifest, uint ActiveBranchId, IReadOnlyList<UbisoftProductBranch> Branches,
    IReadOnlyList<uint> Associations, string? SpaceId, string? AppId, uint ActivationType,
    uint PackageOwnershipState, uint Balance, uint TargetPartner, bool LockedBySubscription, uint SuspensionType)
{
    public const uint TypeGame = 0, TypeAddon = 1;
    public const uint StateReleased = 3, StatePreReleased = 1, StateDownloadable = 2, StateExpired = 4;
}

public sealed record UbisoftOwnershipToken(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "ownership token (redacted)";
}

internal sealed class UbisoftOwnershipService
{
    internal const string ServiceName = "ownership_service";
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(60);
    // OwnedGame secrets: cd_key (2) and encryption_key (24).
    private static readonly HashSet<int> SecretFields = [2, 24];

    private readonly UbisoftDemuxChannel _channel;

    private UbisoftOwnershipService(UbisoftDemuxChannel channel, byte[] initializeResponse, IReadOnlyList<UbisoftOwnedProduct> products)
    {
        _channel = channel;
        InitializeResponse = initializeResponse;
        Products = products;
    }

    internal IReadOnlyList<UbisoftOwnedProduct> Products { get; }

    /// <summary>The <c>Rsp.initialize_rsp</c> body with every OwnedGame's CD key and encryption key removed.</summary>
    internal byte[] InitializeResponse { get; }

    internal static async Task<UbisoftOwnershipService> OpenAsync(UbisoftDemuxConnection connection, CancellationToken ct)
    {
        var channel = await connection.OpenAsync(ServiceName, ct);
        // Scalars must be varints; the server rejects length-delimited encodings here.
        var response = await channel.CallAsync(Bytes(2, Message(Integer(2, 1), Integer(3, 7), Integer(5, 0))), ct, InitializeTimeout);
        var initialize = response.Data(2);
        if (Read(initialize).Number(1) != 1)
            throw new UbisoftDemuxException(UbisoftDemuxFailure.Rejected, "Ubisoft ownership initialization failed.");
        var sanitized = Sanitize(initialize);
        return new(channel, sanitized, ParseProducts(sanitized));
    }

    /// <summary>Parses the products of an <c>InitializeRsp</c> body. Secrets in the input are ignored.</summary>
    internal static IReadOnlyList<UbisoftOwnedProduct> ParseProducts(byte[] initializeResponse) =>
        Read(Read(initializeResponse).Data(2)).All(1).Select(ParseProduct).ToList();

    private static UbisoftOwnedProduct ParseProduct(byte[] bytes)
    {
        var f = Read(bytes);
        static uint U(ulong value) => checked((uint)value);
        var branches = f.All(26).Select(Read).Select(b => new UbisoftProductBranch(U(b.Number(1)), b.String(2))).ToList();
        return new(
            ProductId: U(f.Number(1)), Owned: f.Number(20) == 1, ProductType: U(f.Number(8)), State: U(f.Number(13)),
            UplayId: U(f.Number(19)), DownloadId: U(f.Number(5)),
            Configuration: f.OptionalString(17), LatestManifest: Blank(f.OptionalString(21)),
            ActiveBranchId: U(f.Number(25)), Branches: branches,
            Associations: f.Numbers(16).Select(U).ToList(),
            SpaceId: Blank(f.OptionalString(30)), AppId: Blank(f.OptionalString(31)),
            ActivationType: U(f.Number(36)), PackageOwnershipState: U(f.Number(33)), Balance: U(f.Number(23)),
            TargetPartner: U(f.Number(38)), LockedBySubscription: f.Number(37) == 1, SuspensionType: U(f.Number(34)));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static byte[] Sanitize(byte[] initializeResponse) =>
        Message(Read(initializeResponse).Select(field => field is { Number: 2 or 7, WireType: 2 }
            ? Bytes(field.Number, Message(Read(field.Data).Select(SanitizeGameListEntry).ToArray()))
            : Encode(field)).ToArray());

    private static byte[] SanitizeGameListEntry(Field entry) => entry is { Number: 1, WireType: 2 }
        ? Bytes(1, Message(Read(entry.Data).Where(g => !SecretFields.Contains(g.Number)).Select(Encode).ToArray()))
        : Encode(entry);

    internal async Task<UbisoftOwnershipToken> GetOwnershipTokenAsync(uint productId, CancellationToken ct)
    {
        var response = await _channel.CallAsync(Bytes(28, Integer(1, productId)), ct);
        var token = Read(response.Data(27));
        var value = token.String(2);
        if (token.Number(1) != 1 || value.Length == 0)
            throw new UbisoftDemuxException(UbisoftDemuxFailure.Rejected, $"Ubisoft did not issue an ownership token for product {productId}.");
        var expiration = token.Number(3);
        var expiresAt = expiration is > 0 and < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds((long)expiration)
            : DateTimeOffset.UtcNow.AddMinutes(30);
        return new(value, expiresAt);
    }

    /// <summary>GetUplayPCTicket in the PoC-verified form <c>{14:{1:pid, 2:0, 3:pid}, 30:ticket, 31:session}</c>.</summary>
    internal async Task<byte[]> GetUplayPcTicketAsync(uint productId, string loginTicket, string sessionId, CancellationToken ct)
    {
        var request = Message(Bytes(14, Message(Integer(1, productId), Integer(2, 0), Integer(3, productId))),
            Text(30, loginTicket), Text(31, sessionId));
        var response = await _channel.CallAsync(request, ct);
        var ticket = Read(response.Data(13));
        if (ticket.Number(1) != 1 || ticket.Data(2).Length == 0)
            throw new InvalidDataException($"Ubisoft did not issue a PC session ticket (error {ticket.Number(3, 1)}).");
        return ticket.Data(2);
    }
}
