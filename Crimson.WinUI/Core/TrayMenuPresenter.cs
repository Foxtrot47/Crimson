using System;
using System.Collections.Generic;
using System.Linq;
using Crimson.Models;

namespace Crimson.Core;

internal sealed record TrayGameMenuItem(string AppName, string Title, bool CanLaunch);

internal static class TrayMenuPresenter
{
    internal static IReadOnlyList<TrayGameMenuItem> GetInstalledGames(
        IEnumerable<Game> games,
        bool isLoggedIn,
        Func<string, bool> hasRelatedOperation)
    {
        if (!isLoggedIn) return [];

        return games
            .Where(game => !game.IsDlc() && game.LocalAppState is { InstallStatus: not InstallState.NotInstalled })
            .DistinctBy(game => game.AppName, StringComparer.Ordinal)
            .Select(game => new TrayGameMenuItem(
                game.AppName,
                string.IsNullOrWhiteSpace(game.AppTitle) ? game.AppName : game.AppTitle,
                (game.LocalAppState!.InstallStatus is InstallState.Installed or InstallState.NeedUpdate) &&
                    !hasRelatedOperation(game.AppName)))
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(game => game.AppName, StringComparer.Ordinal)
            .ToList();
    }

    internal static string EscapeMenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);
}
