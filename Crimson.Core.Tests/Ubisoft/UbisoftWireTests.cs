using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftWireTests
{
    [Fact]
    public void ProductIdsAreVarintsAndOpaqueChallengesKeepTheirBytes()
    {
        byte[] challenge = [0, 255, 128, 45];
        var fields = Read(Message(Integer(1, 2688), Bytes(2, challenge)));
        Assert.Equal(2688UL, fields.Number(1));
        Assert.Equal(0, fields[0].WireType);
        Assert.Equal(challenge, fields.Data(2));
    }
    [Theory]
    [InlineData("0A05FF")]
    [InlineData("0880808080808080808002")]
    [InlineData("00")]
    public void RejectsMalformedMessages(string hex) =>
        Assert.ThrowsAny<Exception>(() => Read(Convert.FromHexString(hex)));

    [Fact]
    public async Task RejectsOversizedFramesBeforeReadingPayload()
    {
        using var stream = new MemoryStream(Convert.FromHexString("7FFFFFFF"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadFrame(stream, default));
    }
    [Fact]
    public async Task ReadsFrameAcrossShortReadsAndDetectsTruncation()
    {
        using var stream = new ShortReadStream(Frame(Text(1, "test")));
        Assert.Equal("test", Read(await ReadFrame(stream, default)).String(1));
        using var truncated = new MemoryStream([0, 0, 0, 2, 8]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ReadFrame(truncated, default));
    }
    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}
