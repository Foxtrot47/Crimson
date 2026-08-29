using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Core;

public enum GameShortcutLocation
{
    Desktop,
    StartMenu
}

public interface IGameShortcutManager
{
    Task CreateAsync(Game game, GameShortcutLocation location);
    void Remove(Game game);
}
