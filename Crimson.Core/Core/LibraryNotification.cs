using System.Collections.Generic;
using Crimson.Models;

namespace Crimson.Core;

public static class LibraryNotification
{
    public static AppNotification? ForAddedGames(IReadOnlyList<Game> games)
    {
        if (games.Count == 0) return null;
        if (games.Count > 1)
            return new AppNotification("New games added", $"{games.Count} games were added to your library.", NotificationSeverity.Success);

        var game = games[0];
        var title = string.IsNullOrWhiteSpace(game.AppTitle) ? game.AppName : game.AppTitle;
        return new AppNotification("New game added", $"{title} is now in your library.", NotificationSeverity.Success);
    }
}
