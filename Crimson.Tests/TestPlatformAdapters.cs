using System.Text;
using Crimson.Core;

namespace Crimson.Tests;

internal sealed class TestCredentialProtector : ICredentialProtector
{
    private const string Prefix = "test-protected:";

    public string Protect(string value) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedValue;
        return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
    }
}

internal sealed class RecordingGameProcessRunner : IGameProcessRunner
{
    public GameProcessStartInfo? LastStartInfo { get; private set; }

    public Task RunAsync(
        GameProcessStartInfo startInfo,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastStartInfo = startInfo;
        return Task.CompletedTask;
    }
}
