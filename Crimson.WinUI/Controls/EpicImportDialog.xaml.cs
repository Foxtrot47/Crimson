using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Controls;

public sealed partial class EpicImportDialog : ContentDialog
{
    private CancellationTokenSource? _cancellation;
    private Task _loading = Task.CompletedTask;
    public EpicImportViewModel ViewModel { get; }

    public EpicImportDialog()
    {
        ViewModel = App.GetService<EpicImportViewModel>();
        InitializeComponent();
    }

    public async Task ShowPickerAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
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
        _loading = ViewModel.LoadAsync(_cancellation!.Token);

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => _cancellation?.Cancel();

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

    public void ClosePicker()
    {
        _cancellation?.Cancel();
        Hide();
        ViewModel.Close();
    }
}
