using System.Collections.Immutable;

namespace Crimson.Core;

public sealed record LaunchCredentials(
    string ExchangeCode,
    string AccountId,
    string DisplayName);

public sealed record RuntimeProfile(string Name);

public sealed record LaunchPlan(
    string FileName,
    string WorkingDirectory,
    ImmutableArray<string> Arguments,
    ImmutableDictionary<string, string> Environment);

public interface ILaunchPlanner
{
    LaunchPlan Create(
        GameSnapshot game,
        LaunchCredentials credentials,
        RuntimeProfile runtimeProfile);
}

public interface IRuntimeProfileResolver
{
    Task<RuntimeProfile> ResolveAsync(
        GameSnapshot game,
        CancellationToken cancellationToken = default);
}

public interface IInstallRecoveryStatus
{
    bool HasUnresolvedTransaction(string installRoot);
}

public sealed class EpicLaunchPlanner : ILaunchPlanner
{
    public LaunchPlan Create(
        GameSnapshot game,
        LaunchCredentials credentials,
        RuntimeProfile runtimeProfile)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(runtimeProfile);
        if (string.IsNullOrWhiteSpace(game.InstallPath) || string.IsNullOrWhiteSpace(game.Executable))
            throw new InvalidOperationException("Installed game has no launch path.");

        var executable = Utils.ManifestRelativePath.Parse(game.Executable);
        return new LaunchPlan(
            Utils.ManifestPath.ResolveUnderRoot(game.InstallPath, executable),
            game.InstallPath,
            [
                "-AUTH_LOGIN=unused",
                $"-AUTH_PASSWORD={credentials.ExchangeCode}",
                "-AUTH_TYPE=exchangecode",
                $"-epicapp={game.AppName}",
                "-epicenv=Prod",
                "-EpicPortal",
                $"-epicusername={credentials.DisplayName}",
                $"-epicuserid={credentials.AccountId}",
                $"-epicsandboxid={game.Namespace}",
                "-epiclocale=en"
            ],
            ImmutableDictionary<string, string>.Empty);
    }
}
