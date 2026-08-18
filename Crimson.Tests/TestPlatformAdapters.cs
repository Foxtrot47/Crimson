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
    public LaunchPlan? LastPlan { get; private set; }

    public Task RunAsync(
        LaunchPlan launchPlan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPlan = launchPlan;
        return Task.CompletedTask;
    }
}

internal sealed class TestRuntimeProfileResolver : IRuntimeProfileResolver
{
    public Task<RuntimeProfile> ResolveAsync(
        GameSnapshot game,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeProfile("Test"));
    }
}

internal sealed class TestInstallRecoveryStatus(bool unresolved = false) : IInstallRecoveryStatus
{
    public bool HasUnresolvedTransaction(string installRoot) => unresolved;
}
