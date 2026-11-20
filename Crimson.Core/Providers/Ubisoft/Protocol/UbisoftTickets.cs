using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Providers.Ubisoft.Protocol;

public sealed record UbisoftPcSession(byte[] PcTicket, IReadOnlyList<uint> OwnedProducts)
{
    public IReadOnlyList<uint> AddonsToValidate { get; init; } = [];
}

public interface IUbisoftTickets
{
    Task<UbisoftPcSession> GetPcSession(string loginTicket, string sessionId, uint productId, CancellationToken token);
    Task<byte[]> Activate(string loginTicket, string sessionId, uint productId, byte[] challenge, CancellationToken token);
}

/// <summary>Live issuance only. Every operation owns its Demux connection; no replay or automatic activation retry.</summary>
public sealed class UbisoftTickets : IUbisoftTickets
{
    private readonly Func<string, CancellationToken, Task<UbisoftDemuxConnection>> _connect;
    public UbisoftTickets() : this(UbisoftDemuxConnection.ConnectAsync) { }
    internal UbisoftTickets(Func<string, CancellationToken, Task<UbisoftDemuxConnection>> connect) => _connect = connect;

    public async Task<UbisoftPcSession> GetPcSession(string loginTicket, string sessionId, uint productId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using var connection = await _connect(loginTicket, deadline.Token);
        var ownership = await UbisoftOwnershipService.OpenAsync(connection, deadline.Token);
        var ticket = await ownership.GetUplayPcTicketAsync(productId, loginTicket, sessionId, deadline.Token);
        return new(ticket, SessionProducts(ownership.Products, UbisoftOwnedProduct.TypeGame, UbisoftOwnedProduct.TypeAddon))
        { AddonsToValidate = SessionProducts(ownership.Products, UbisoftOwnedProduct.TypeAddon) };
    }

    public async Task<byte[]> Activate(string loginTicket, string sessionId, uint productId, byte[] challenge, CancellationToken token)
    {
        if (challenge.Length is 0 or > 65536) throw new ArgumentException("Invalid activation challenge size.", nameof(challenge));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        await using var connection = await _connect(loginTicket, deadline.Token);
        var ownership = await UbisoftOwnershipService.OpenAsync(connection, deadline.Token);
        var ownershipToken = await ownership.GetOwnershipTokenAsync(productId, deadline.Token);
        return await UbisoftDenuvoService.GetGameTokenAsync(connection, ownershipToken, challenge, deadline.Token);
    }

    /// <summary>The official session product list: owned products of the given types that are unavailable(0) or released(3), in server order.</summary>
    internal static IReadOnlyList<uint> SessionProducts(IReadOnlyList<UbisoftOwnedProduct> products, params uint[] types) => products
        .Where(p => p.Owned && types.Contains(p.ProductType) && p.State is 0 or UbisoftOwnedProduct.StateReleased && p.ProductId != 0)
        .Select(p => p.ProductId).ToList();
}
