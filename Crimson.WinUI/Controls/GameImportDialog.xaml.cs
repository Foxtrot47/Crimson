using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Resources;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Controls;

public sealed partial class GameImportDialog : ContentDialog
{
    private CancellationTokenSource? _cancellation;
    private Task _loading = Task.CompletedTask;
    private string _providerId = string.Empty;
    public GameImportViewModel ViewModel { get; }

    public GameImportDialog()
    {
        ViewModel = App.GetService<GameImportViewModel>();
        InitializeComponent();
    }

    public async Task ShowPickerAsync(string providerId)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _providerId = providerId;
        Title = UiStrings.Format(UiStrings.ImportFromProviderFormat,
            App.GetService<Crimson.Providers.LauncherRegistry>().Get(providerId).DisplayName);
        var root = XamlRoot;
        try
        {
            root.Changed += DialogRootChanged;
            UpdateWidth(root);
            await App.GetService<DialogCoordinator>().ShowAsync(async () => await ShowAsync(), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            root.Changed -= DialogRootChanged;
            cancellation.Cancel();
            await _loading;
            ViewModel.Close();
            _cancellation = null;
        }
    }

    private void DialogRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateWidth(sender);

    private void UpdateWidth(XamlRoot root) => DialogContent.Width = Math.Max(0, Math.Min(640, root.Size.Width - 96));

    private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        _loading = ViewModel.LoadAsync(_providerId, _cancellation!.Token);

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => _cancellation?.Cancel();

    // Two-way x:Bind on SelectedItem does not type-check against the choice type, so selection flows through here.
    private void GameSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedItem = GameList.SelectedItem as GameImportChoice;

    private async void ImportSelected(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.ImportSelectedAsync(_cancellation!.Token);
        }
        finally
        {
            deferral.Complete();
        }
    }

    public string ProviderId => _providerId;

    public void ClosePicker()
    {
        _cancellation?.Cancel();
        Hide();
        ViewModel.Close();
    }
}
