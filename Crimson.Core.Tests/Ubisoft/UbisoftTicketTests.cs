using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftTicketTests
{
    private static byte[] Product(uint id, uint owned, uint type, uint state) => Bytes(1, Message(
        Integer(1, id), Integer(20, owned), Integer(8, type), Integer(13, state)));
    private static readonly byte[] Initialized = Bytes(2, Message(Integer(1, 1), Bytes(2, Product(2688, 1, 0, 3))));

    private static UbisoftTickets Client(FakeDemuxServer server, Action? connected = null) =>
        new((ticket, ct) => { connected?.Invoke(); return UbisoftDemuxConnection.ConnectAsync(server, ticket, ct); });

    [Fact]
    public async Task ActivationRelaysOwnershipTokenAndChallengeToDenuvo()
    {
        byte[] challenge = [0, 6, 255, 42];
        byte[] answer = [7, 8, 9];
        IReadOnlyList<Field>? denuvoRequest = null;
        var server = new FakeDemuxServer
        {
            OnServiceRequest = (service, _, request) =>
            {
                if (service == "denuvo_service")
                {
                    denuvoRequest = Read(request.Data(2));
                    return Bytes(3, Bytes(1, answer));
                }
                if (request.Has(2)) return Initialized;
                Assert.Equal(2688UL, Read(request.Data(28)).Number(1));
                return Bytes(27, Message(Integer(1, 1), Text(2, "ownership")));
            }
        };

        Assert.Equal(answer, await Client(server).Activate("login", "session", 2688, challenge, default));

        Assert.NotNull(denuvoRequest);
        Assert.Equal("ownership", denuvoRequest.String(1));
        Assert.Equal(challenge, denuvoRequest.Data(2));
        Assert.True(server.Disposed);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task BusinessErrorsAreTypedAndNeverRetried(int result)
    {
        var connects = 0;
        var server = new FakeDemuxServer
        {
            OnServiceRequest = (service, _, request) =>
                service == "denuvo_service" ? Integer(2, (ulong)result)
                : request.Has(2) ? Initialized
                : Bytes(27, Message(Integer(1, 1), Text(2, "ownership")))
        };

        var error = await Assert.ThrowsAsync<UbisoftActivationException>(() =>
            Client(server, () => connects++).Activate("login", "session", 2688, [1], default));

        Assert.Equal(result, (int)error.Result);
        Assert.Equal(1, connects);
        Assert.True(server.Disposed);
    }

    [Fact]
    public async Task PcSessionUsesLiveFilteredProductsInServerOrder()
    {
        var products = Message(Product(90, 1, 0, 0), Product(40, 0, 0, 0), Product(20, 1, 1, 3), Product(30, 1, 2, 0),
            Product(50, 1, 1, 4));
        IReadOnlyList<Field>? ticketRequest = null;
        var server = new FakeDemuxServer
        {
            OnServiceRequest = (_, _, request) =>
            {
                if (request.Has(2)) return Bytes(2, Message(Integer(1, 1), Bytes(2, products)));
                ticketRequest = request;
                return Bytes(13, Message(Integer(1, 1), Text(2, "pc-ticket")));
            }
        };

        var session = await Client(server).GetPcSession("login", "session", 90, default);

        Assert.Equal("pc-ticket", System.Text.Encoding.UTF8.GetString(session.PcTicket));
        Assert.Equal(new uint[] { 90, 20 }, session.OwnedProducts);
        Assert.Equal(new uint[] { 20 }, session.AddonsToValidate);
        Assert.NotNull(ticketRequest);
        Assert.Equal(Message(Integer(1, 90), Integer(2, 0), Integer(3, 90)), ticketRequest.Data(14));
        Assert.Equal("login", ticketRequest.String(30));
        Assert.Equal("session", ticketRequest.String(31));
        Assert.True(server.Disposed);
    }
}
