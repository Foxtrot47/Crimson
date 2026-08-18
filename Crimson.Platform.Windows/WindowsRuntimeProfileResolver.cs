using Crimson.Core;

namespace Crimson.Platform.Windows;

public sealed class WindowsRuntimeProfileResolver : IRuntimeProfileResolver
{
    public Task<RuntimeProfile> ResolveAsync(
        GameSnapshot game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeProfile("Windows"));
    }
}
