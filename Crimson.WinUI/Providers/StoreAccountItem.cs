using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Crimson.Providers;

/// <summary>An account row paired with the store mark and import its frontend supplies.</summary>
public sealed class StoreAccountItem
{
    private StoreAccountItem(LauncherAccount account, ImageSource? logo, Brush tileBrush, Func<Task>? import)
    {
        Account = account;
        Logo = logo;
        TileBrush = tileBrush;
        Import = import;
    }

    public LauncherAccount Account { get; }
    public ImageSource? Logo { get; }
    public Brush TileBrush { get; }

    /// <summary>The store's import of games it finds already installed; null when it offers none.</summary>
    public Func<Task>? Import { get; }

    public static IReadOnlyList<StoreAccountItem> Create(
        IEnumerable<LauncherAccount> accounts, LauncherFrontendRegistry frontends) =>
        accounts.Select(account => For(account, frontends.Get(account.ProviderId))).ToList();

    public static StoreAccountItem For(LauncherAccount account, ILauncherFrontend frontend)
    {
        var logo = string.IsNullOrWhiteSpace(frontend.StoreLogo) ? null : new BitmapImage(new Uri(frontend.StoreLogo));
        var tile = frontend.StoreTileColor is { } color
            ? new SolidColorBrush(color)
            : (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        return new StoreAccountItem(account, logo, tile, frontend.Import);
    }
}
