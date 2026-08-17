using Crimson.Core;

namespace Crimson.Tests;

public sealed class EpicAuthorizationCodeGateTests
{
    [Theory]
    [InlineData("\"code-123_ABC\"")]
    [InlineData("{\"authorizationCode\":\"code-123_ABC\"}")]
    [InlineData("code-123_ABC")]
    public void TryAccept_ReadsAuthorizationCodeFromEpicPage(string payload)
    {
        var gate = new EpicAuthorizationCodeGate();

        Assert.True(gate.TryAccept(
            "https://www.epicgames.com/id/api/redirect",
            payload,
            out var code));
        Assert.Equal("code-123_ABC", code);
    }

    [Fact]
    public void TryAccept_RejectsUntrustedOriginsAndReplays()
    {
        var gate = new EpicAuthorizationCodeGate();

        Assert.False(gate.TryAccept("https://evil.example", "\"code-123\"", out _));
        Assert.True(gate.TryAccept("https://www.epicgames.com/id/api/redirect", "\"code-123\"", out _));
        Assert.False(gate.TryAccept("https://www.epicgames.com/id/api/redirect", "\"code-123\"", out _));
    }
}
