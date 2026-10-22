using System.Text.Json;
using Crimson.Providers.Ubisoft;
using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftGameSessionTests
{
    private static UbisoftGameSession Create(Tickets tickets) => new(
        new UbisoftSession { Ticket = "login", SessionId = "session", ProfileId = "profile", UserId = "user" },
        new(2688, "Title", "game", "61ec338e-d607-4042-a55b-fc009cfc2dc6", true),
        new(2688, "Title", "/games", "/games/game.exe"),
        new([1, 2, 3], [2688, 90]) { AddonsToValidate = [90] },
        JsonSerializer.SerializeToElement(new { username = "Player", email = "email", country = "GB" }), "/saves/user/2688/", tickets);
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
