using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Controls;

/// <summary>
/// Chooses whose installed games to import. Only one dialog can show at a time, so the chosen import
/// or sign-in runs after this one has closed.
/// </summary>
public sealed partial class ImportSourceDialog : ContentDialog
{
    private Func<Task>? _next;

    public ImportSourceDialog(IReadOnlyList<StoreAccountItem> sources)
    {
        Sources = sources;
        InitializeComponent();
    }

    public IReadOnlyList<StoreAccountItem> Sources { get; }

    /// <summary>Shows the stores, then runs whatever the person picked.</summary>
    public async Task RunAsync()
    {
        await App.GetService<DialogCoordinator>().ShowAsync(async () => await ShowAsync());
        if (_next is { } next) await next();
    }

    private void ImportClicked(object sender, RoutedEventArgs e) =>
        Choose(((StoreAccountItem)((Button)sender).Tag).Import!);

    private void SignInClicked(object sender, RoutedEventArgs e)
    {
        var account = ((StoreAccountItem)((Button)sender).Tag).Account;
        Choose(() =>
        {
            account.ConnectCommand.Execute(null);
            return Task.CompletedTask;
        });
    }

    private void Choose(Func<Task> next)
    {
        _next = next;
        Hide();
    }
}
