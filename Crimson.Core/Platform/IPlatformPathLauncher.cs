namespace Crimson.Core;

public interface IPlatformPathLauncher
{
    Task OpenDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);
}
