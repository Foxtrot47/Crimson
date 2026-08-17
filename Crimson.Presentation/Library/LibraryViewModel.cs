using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;

namespace Crimson.Presentation;

public sealed record LibraryItemViewModel(
    string AppName,
    string Title,
    Uri? ImageUri,
    string BuildVersion,
    bool IsInstalled);

public partial class LibraryViewModel : ObservableObject, IActivatable
{
    private readonly ILibraryService _libraryService;
    private readonly IUiDispatcher _dispatcher;
    private readonly INavigationService _navigation;
    private bool _active;

    [ObservableProperty]
    private ObservableCollection<LibraryItemViewModel> _games = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private LibraryItemViewModel? _selectedGame;

    public LibraryViewModel(
        ILibraryService libraryService,
        IUiDispatcher dispatcher,
        INavigationService navigation)
    {
        _libraryService = libraryService;
        _dispatcher = dispatcher;
        _navigation = navigation;
    }

    public bool HasGames => Games.Count > 0;

    public bool IsEmpty => !IsLoading && !HasGames && !HasError;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_active)
            return;
        _active = true;
        _libraryService.Changed += OnLibraryChanged;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var snapshot = await _libraryService.GetSnapshotAsync(cancellationToken);
            await ApplySnapshotAsync(snapshot, cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _libraryService.Changed -= OnLibraryChanged;
    }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedGameChanged(LibraryItemViewModel? value)
    {
        if (value is null)
            return;
        _navigation.Navigate(new GameRoute(value.AppName));
        SelectedGame = null;
    }


    [RelayCommand]
    private void OpenGame(LibraryItemViewModel? game)
    {
        if (game is not null)
            _navigation.Navigate(new GameRoute(game.AppName));
    }

    private void OnLibraryChanged(object? sender, LibrarySnapshot snapshot) =>
        _ = ApplySnapshotAsync(snapshot, CancellationToken.None);

    private async Task ApplySnapshotAsync(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                Games = new ObservableCollection<LibraryItemViewModel>(snapshot.Games.Select(game =>
                    new LibraryItemViewModel(
                        game.AppName,
                        game.Title,
                        game.ImageUri,
                        game.BuildVersion,
                        game.IsInstalled)));
                ErrorMessage = snapshot.Error;
                OnPropertyChanged(nameof(HasGames));
                OnPropertyChanged(nameof(IsEmpty));
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() =>
                ErrorMessage = $"Library view could not be updated: {exception.GetType().Name}");
        }
    }
}
