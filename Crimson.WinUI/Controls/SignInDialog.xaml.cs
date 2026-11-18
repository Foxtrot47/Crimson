using System;
using Crimson.Providers;
using Crimson.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Controls;

/// <summary>
/// Hosts one store's sign-in view under a header naming the store. The view decides its own size;
/// a smaller window shrinks it, which browser sign-in pages reflow to fit.
/// </summary>
public sealed partial class SignInDialog : ContentDialog
{
    // Header row plus its divider, and the dialog's own border.
    private const double HeaderHeight = 61;
    private const double DialogBorder = 2;
    // Keeps the dialog clear of the window edges.
    private const double WindowMargin = 96;

    private readonly ILauncherSignInView _view;

    public SignInDialog(StoreAccountItem store, ILauncherSignInView view)
    {
        Store = store;
        Heading = UiStrings.Format(UiStrings.SignInToStoreFormat, store.Account.DisplayName);
        _view = view;
        InitializeComponent();
        // WinUI's default ceilings (548x756) would clip a browser page.
        var size = view.PreferredSize;
        Resources["ContentDialogMaxWidth"] = size.Width + DialogBorder;
        if (size.Height is { } height) Resources["ContentDialogMaxHeight"] = height + HeaderHeight + DialogBorder;
        Opened += (_, _) => Start();
    }

    public StoreAccountItem Store { get; }
    public string Heading { get; }

    /// <summary>Stops the view; safe to call more than once.</summary>
    public void EndSignIn()
    {
        if (ViewHost.Child is null) return;
        _view.CloseSignIn();
        ViewHost.Child = null;
    }

    private void Start()
    {
        var size = _view.PreferredSize;
        var available = XamlRoot.Size;
        var maxHeight = Math.Max(0, available.Height - WindowMargin - HeaderHeight);
        ViewHost.Width = Math.Max(0, Math.Min(size.Width, available.Width - WindowMargin));
        if (size.Height is { } height) ViewHost.Height = Math.Min(height, maxHeight);
        else ViewHost.MaxHeight = maxHeight;
        ViewHost.Child = _view.Element;
        _view.InitializeSignIn();
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Hide();
}
