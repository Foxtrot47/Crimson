using System.Threading.Tasks;
using System.Threading;
using Crimson.Providers;

namespace Crimson.Core;

public enum GameShortcutLocation
{
    Desktop,
    StartMenu
}

public interface IGameShortcutManager
{
    Task CreateAsync(
        LauncherGame game,
        GameShortcutLocation location,
        CancellationToken cancellationToken = default);
    void Remove(LauncherGame game);
}
