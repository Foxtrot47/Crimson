using System.Text.Json;
using Crimson.Providers.Ubisoft;
using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftGameSessionTests
{
    private static UbisoftGameSession Create(Tickets tickets, uint[]? addons = null, UbisoftSdkProduct[]? products = null) => new(
        new UbisoftSession { Ticket = "login", SessionId = "session", ProfileId = "profile", UserId = "user" },
        new UbisoftLaunchPlan(2688, "/games", "/games/game.exe", "/games", "", "61ec338e-d607-4042-a55b-fc009cfc2dc6", "en-US")
        {
            AddonIds = addons ?? [90],
            SdkProducts = products ?? [],
            SaveDirectory = "/saves/user/2688/"
        },
        new([1, 2, 3], [2688, 90]) { AddonsToValidate = [90, 91] },
        JsonSerializer.SerializeToElement(new { username = "Player", email = "email", country = "GB" }), tickets);

    private static UbisoftOwnedProduct Owned(uint id, uint type, uint state, bool owned, uint[]? associations = null,
        uint balance = 0) =>
        new(id, owned, type, state, id, 0, null, null, 0, [], associations ?? [], null, null, 0, 0, balance, 0, false, 0);

    [Fact]
    public void SdkProductsListTheGameItsOwnedReleasedAddonsAndItsConsumables()
    {
        var game = Owned(5487, UbisoftOwnedProduct.TypeGame, 3, true, [10, 11, 12, 13]);
        UbisoftOwnedProduct[] catalog =
        [
            game, Owned(10, UbisoftOwnedProduct.TypeAddon, 3, true), Owned(11, UbisoftOwnedProduct.TypeAddon, 3, false),
            Owned(12, 8, 0, false, balance: 7), Owned(13, UbisoftOwnedProduct.TypeAddon, 4, true),
            Owned(99, UbisoftOwnedProduct.TypeAddon, 3, true)
        ];
        Assert.Equal(
        [
            new UbisoftSdkProduct(5487, UbisoftSdkProduct.TypeGame, true, 3, 0, 1),
            new UbisoftSdkProduct(10, UbisoftSdkProduct.TypeAddon, true, 3, 0, 1),
            new UbisoftSdkProduct(12, UbisoftSdkProduct.TypeConsumable, false, 3, 7, 1)
        ], UbisoftSdkProduct.For(game, catalog));
        Assert.Empty(UbisoftSdkProduct.For(game with { Owned = false }, catalog));
    }

    [Fact]
    public void R2SessionCarriesEveryRequestedSubsystem()
    {
        var session = Create(new Tickets(), products: [new UbisoftSdkProduct(12, UbisoftSdkProduct.TypeConsumable, false, 3, 7, 1)]);
        var response = Read(Read(session.SessionResponse(Bytes(1, Message(Integer(1, 2688), Integer(4, 1),
            Integer(8, 1), Integer(9, 1), Integer(11, 1))))!).Data(1));
        Assert.Equal(1UL, Read(response.Data(18)).Number(2));
        Assert.Equal(1UL, Read(response.Data(20)).Number(2));
        Assert.True(response.Has(23));
        var install = Read(response.Data(14));
        Assert.True(Read(install.Data(2)).Has(1));
        Assert.Equal("en-US", install.String(3));
        var consumable = Read(response.Data(9));
        Assert.Equal((12UL, 7UL), (consumable.Number(1), consumable.Number(2)));

        var r1 = Read(Read(session.SessionResponse(Bytes(1, Integer(1, 2688)))!).Data(1));
        Assert.False(r1.Has(14) || r1.Has(20) || r1.Has(23));
    }

    [Fact]
    public void R2RequestsAreAnsweredWithTheirRequestIds()
    {
        var session = Create(new Tickets(), products: [new UbisoftSdkProduct(2688, UbisoftSdkProduct.TypeGame, true, 3, 0, 1)]);
        var friends = Read(Read(Read(session.SessionResponse(Bytes(7, Bytes(15, Integer(1, 5))))!).Data(4)).Data(12));
        Assert.Equal((5UL, 1UL), (friends.Number(1), friends.Number(2)));

        var achievements = Read(Read(session.SessionResponse(Bytes(8, Bytes(1, Integer(1, 6))))!).Data(5));
        Assert.Equal((6UL, 1UL), (Read(achievements.Data(1)).Number(1), Read(achievements.Data(1)).Number(2)));
        var earn = Read(Read(Read(session.SessionResponse(Bytes(8, Bytes(3, Integer(1, 7))))!).Data(5)).Data(3));
        Assert.Equal(8UL, earn.Number(2));

        var list = Read(Read(Read(session.SessionResponse(Bytes(23, Bytes(1, Integer(1, 8))))!).Data(15)).Data(1));
        Assert.Equal((8UL, 1UL), (list.Number(1), list.Number(2)));
        Assert.Equal(2688UL, Read(list.Data(3)).Number(1));
        var other = Read(Read(Read(session.SessionResponse(Bytes(23, Bytes(1, Message(Integer(1, 9), Text(3, "someone")))))!)
            .Data(15)).Data(1));
        Assert.Equal(2UL, other.Number(2));
        Assert.False(other.Has(3));
    }
    [Fact]
    public async Task OnlyAddonsTheLiveSessionValidatesAreSentToTheRuntime()
    {
        var session = Create(new Tickets(), [90, 999]);
        var init = Read(await session.AuxResponse(Message(Integer(1, 3), Bytes(2, Integer(1, 2688))), 123, default));
        Assert.Equal(new ulong[] { 90 }, Read(init.Data(2)).Numbers(3));
    }
    [Fact]
    public void SessionCarriesPcTicketRealAppIdAndEmptyPassword()
    {
        var session = Create(new Tickets());
        var response = Read(Read(session.SessionResponse(Bytes(1, Integer(1, 2688)))!).Data(1));
        Assert.Equal(new byte[] { 1, 2, 3 }, response.Data(8));
        Assert.Equal("", Read(response.Data(2)).String(5));
        Assert.Equal("61ec338e-d607-4042-a55b-fc009cfc2dc6", Read(response.Data(22)).String(1));
        Assert.Equal("/saves/user/2688/", Read(response.Data(17)).String(1));
        Assert.Throws<InvalidDataException>(() => session.SessionResponse(Bytes(1, Integer(1, 1))));
    }
    [Fact]
    public void InstallerInitializationIsNotMistakenForSessionInitialization()
    {
        var reply = Read(Create(new Tickets()).SessionResponse(Bytes(13, Bytes(1, [])))!);
        Assert.False(reply.Has(1));
        var installer = Read(Read(reply.Data(10)).Data(1));
        Assert.Equal(1UL, installer.Number(1));
        Assert.Equal("en-US", installer.String(3));
    }
    [Fact]
    public void StarterRequestCannotSelectAnUnconfiguredProduct()
    {
        var session = Create(new Tickets());
        Assert.Throws<InvalidDataException>(() => session.GameStartResponse(Bytes(1, Bytes(1, Integer(2, 999)))));
        Assert.True(Read(session.GameStartResponse(Bytes(1, Bytes(1, Integer(2, 2688))))).Has(2));
    }
    private static byte[] Challenge(byte[] bytes) => Message(Integer(1, 42), Bytes(3, Bytes(2, Bytes(2, bytes))));
    [Fact]
    public async Task AuxAnswersUseOnlyLiveActivationAndCoalesceIdenticalChallenges()
    {
        var tickets = new Tickets();
        var session = Create(tickets);
        var init = Read(await session.AuxResponse(Message(Integer(1, 3), Bytes(2, Integer(1, 2688))), 123, default));
        Assert.Equal(90UL, Read(init.Data(2)).Number(3));
        var response = await session.AuxResponse(Challenge([0, 6, 255]), 123, default);
        Assert.Equal(response, await session.AuxResponse(Challenge([0, 6, 255]), 123, default));
        Assert.Equal(1, tickets.Calls);
        Assert.Equal(new byte[] { 0, 6, 255 }, tickets.Challenge);
        var answer = Read(Read(Read(response).Data(3)).Data(2));
        Assert.Equal(new byte[] { 9, 8, 7 }, answer.Data(4));
        await session.AuxResponse(Challenge([1]), 123, default);
        Assert.Equal(2, tickets.Calls);
    }
    [Fact]
    public async Task RetriedRejectedChallengeCannotConsumeAnotherActivation()
    {
        var tickets = new Tickets { Reject = true };
        var session = Create(tickets);
        await Assert.ThrowsAsync<UbisoftActivationException>(() => session.AuxResponse(Challenge([1]), 1, default));
        await Assert.ThrowsAsync<UbisoftActivationException>(() => session.AuxResponse(Challenge([1]), 1, default));
        Assert.Equal(1, tickets.Calls);
    }
    private sealed class Tickets : IUbisoftTickets
    {
        public int Calls;
        public byte[]? Challenge;
        public bool Reject;
        public Task<byte[]> Activate(string loginTicket, string sessionId, uint productId, byte[] challenge, CancellationToken token)
        {
            Calls++; Challenge = challenge;
            return Reject ? Task.FromException<byte[]>(new UbisoftActivationException(UbisoftActivationResult.ExceededActivations))
                : Task.FromResult<byte[]>([9, 8, 7]);
        }
        public Task<UbisoftPcSession> GetPcSession(string loginTicket, string sessionId, uint productId, CancellationToken token) => throw new NotSupportedException();
    }
}
