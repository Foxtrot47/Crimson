using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftTicketTests
{
    private static byte[] Bare(uint id, byte[] op) => Frame(Bytes(1, Message(Integer(1, id), op)));
    private static byte[] Reply(uint connection, uint id, byte[] op) => Frame(Bytes(2, Bytes(1,
        Message(Integer(1, connection), Bytes(2, Frame(Bytes(1, Message(id == 0 ? [] : Integer(1, id), op))))))));
    private static byte[] Setup => Message(
        Bare(0, Bytes(2, Integer(1, 1))),
        Bare(1, Bytes(3, Integer(1, 17))), // arbitrary connection ID, not a hardcoded channel
        Reply(17, 0, Bytes(2, Integer(1, 1))));

    [Fact]
    public async Task LiveActivationKeepsChallengeAndOwnershipTokenInTheirCorrectFields()
    {
        byte[] challenge = [0, 6, 255, 42];
        byte[] answer = [7, 8, 9];
        var transport = new ScriptedStream(Message(Setup,
            Reply(99, 1, Bytes(27, Integer(1, 1))), // unrelated push must be ignored
            Reply(17, 1, Bytes(27, Message(Integer(1, 1), Text(2, "ownership")))),
            Bare(2, Bytes(3, Integer(1, 44))),
            Reply(44, 1, Bytes(3, Bytes(1, answer)))));
        var client = new UbisoftTickets(_ => Task.FromResult<Stream>(transport));
        Assert.Equal(answer, await client.Activate("login", "session", 2688, challenge, default));
        var writes = transport.WrittenFrames();
        var data = Read(Read(Read(writes[^1]).Data(2)).Data(1));
        Assert.Equal(44UL, data.Number(1));
        var operation = Read(Read(Read(Unframe(data.Data(2))).Data(1)).Data(2));
        Assert.Equal("ownership", operation.String(1));
        Assert.Equal(challenge, operation.Data(2));
        Assert.True(transport.Disposed);
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task BusinessErrorsAreTypedAndNeverRetried(int result)
    {
        var connects = 0;
        var transport = new ScriptedStream(Message(Setup,
            Reply(17, 1, Bytes(27, Message(Integer(1, 1), Text(2, "ownership")))),
            Bare(2, Bytes(3, Integer(1, 44))), Reply(44, 1, Integer(2, (ulong)result))));
        var client = new UbisoftTickets(_ => { connects++; return Task.FromResult<Stream>(transport); });
        var error = await Assert.ThrowsAsync<UbisoftActivationException>(() => client.Activate("login", "session", 2688, [1], default));
        Assert.Equal(result, (int)error.Result);
        Assert.Equal(1, connects);
        Assert.True(transport.Disposed);
    }
    [Fact]
    public async Task PcTicketUsesLiveFilteredCatalogInServerOrder()
    {
        byte[] Product(uint id, uint owned, uint type, uint state) => Bytes(1, Message(
            Integer(1, id), Integer(20, owned), Integer(8, type), Integer(13, state)));
        var catalog = Message(Product(90, 1, 0, 0), Product(40, 0, 0, 0), Product(20, 1, 1, 3), Product(30, 1, 2, 0));
        var stream = new ScriptedStream(Message(
            Bare(0, Bytes(2, Integer(1, 1))), Bare(1, Bytes(3, Integer(1, 7))),
            Reply(7, 0, Bytes(2, Message(Integer(1, 1), Bytes(2, catalog)))),
            Reply(7, 1, Bytes(13, Message(Integer(1, 1), Text(2, "pc-ticket"))))));
        var client = new UbisoftTickets(_ => Task.FromResult<Stream>(stream));
        var session = await client.GetPcSession("login", "session", 90, default);
        Assert.Equal("pc-ticket", System.Text.Encoding.UTF8.GetString(session.PcTicket));
        Assert.Equal(new uint[] { 90, 20 }, session.OwnedProducts);
        Assert.True(stream.Disposed);
    }
    private sealed class ScriptedStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);
        private readonly MemoryStream _output = new();
        public bool Disposed { get; private set; }
        public List<byte[]> WrittenFrames()
        {
            var results = new List<byte[]>();
            using var output = new MemoryStream(_output.ToArray());
            while (output.Position < output.Length) results.Add(ReadFrame(output, default).GetAwaiter().GetResult());
            return results;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => _input.ReadAsync(buffer, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => _output.WriteAsync(buffer, token);
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; _input.Dispose(); base.Dispose(disposing); }
    }
}
