using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Providers.Ubisoft;

/// <summary>Configure installations without modifying their files or relying on the official configuration cache.</summary>
public sealed class UbisoftConfigurationView : UserControl
{
    private readonly UbisoftConfigurationStore _store = App.GetService<UbisoftConfigurationStore>();
    private readonly UbisoftLibrary _library = App.GetService<UbisoftLibrary>();
    private readonly TextBox _runtime = new() { Header = "Ubisoft runtime directory", PlaceholderText = @"D:\UbisoftRuntime" };
    private readonly ComboBox _game = new() { Header = "Installed game", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _title = new() { Header = "Title" };
    private readonly TextBox _directory = new() { Header = "Install directory", PlaceholderText = @"D:\Games\MyGame" };
    private readonly TextBox _executable = new() { Header = "Game executable", PlaceholderText = @"D:\Games\MyGame\bin\Game.exe" };
    private readonly TextBox _arguments = new() { Header = "Launch arguments (optional)" };
    private readonly TextBox _language = new() { Header = "Game language", Text = "en-US" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Save configuration" };
    private readonly Button _remove = new() { Content = "Forget installation" };
    private bool _busy;
    private sealed record Choice(uint Id, string Title) { public override string ToString() => $"{Title} ({Id})"; }
    public UbisoftConfigurationView()
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "Connect your account to load games, then register an existing installation. Supply your own Ubisoft runtime; game startup and activation are ready for testing. Downloads and cloud-save sync are not available yet.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(_runtime);
        panel.Children.Add(new TextBlock { Text = "The machine's 32-bit Ubisoft Launcher InstallDir must already point to this runtime. Crimson leaves the registry unchanged.", TextWrapping = TextWrapping.Wrap });
        var refresh = new Button { Content = "Load account games" };
        refresh.Click += async (_, _) => await LoadGames();
        panel.Children.Add(refresh); panel.Children.Add(_game); panel.Children.Add(_title);
        panel.Children.Add(_directory); panel.Children.Add(_executable); panel.Children.Add(_arguments); panel.Children.Add(_language);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        buttons.Children.Add(_save); buttons.Children.Add(_remove); panel.Children.Add(buttons); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _game.SelectionChanged += (_, _) => SelectGame();
        _save.Click += async (_, _) => await Save();
        _remove.Click += async (_, _) => await Remove();
        Loaded += async (_, _) =>
        {
            try { await _store.Load(); _runtime.Text = _store.Current.RuntimeDirectory; Populate(); _status.Text = _library.LastLaunchError ?? ""; }
            catch (Exception) { _status.Text = "Could not read Ubisoft configuration."; }
        };
    }
    private void Populate(uint? selected = null)
    {
        _game.Items.Clear();
        var games = _library.Entitlements.Where(g => g.Type == "game").Select(g => new Choice(g.ProductId, g.Title))
            .Concat(_store.Current.Games.Select(g => new Choice(g.ProductId, g.Title)))
            .GroupBy(g => g.Id).Select(g => g.Last()).OrderBy(g => g.Title).ToList();
        foreach (var game in games) _game.Items.Add(game);
        _game.SelectedItem = games.FirstOrDefault(g => g.Id == selected) ?? games.FirstOrDefault();
    }
    private void SelectGame()
    {
        if (_game.SelectedItem is not Choice choice) return;
        var installed = _store.Current.Games.FirstOrDefault(g => g.ProductId == choice.Id);
        _title.Text = installed?.Title ?? choice.Title;
        _directory.Text = installed?.InstallDirectory ?? "";
        _executable.Text = installed?.Executable ?? "";
        _arguments.Text = installed?.Arguments ?? "";
        _language.Text = installed?.Language ?? "en-US";
    }
    private async Task LoadGames()
    {
        if (_busy) return;
        _busy = true;
        try { await _library.GetLibraryData(true); Populate(); _status.Text = "Choose the game to configure."; }
        catch (Exception) { _status.Text = "Connect your Ubisoft account and check your connection, then load games again."; }
        finally { _busy = false; }
    }
    private async Task Save()
    {
        if (_busy) return;
        _busy = true; _save.IsEnabled = false;
        try
        {
            var games = _store.Current.Games.ToList();
            uint? selected = null;
            if (_game.SelectedItem is Choice choice && (!string.IsNullOrWhiteSpace(_directory.Text) || !string.IsNullOrWhiteSpace(_executable.Text)))
            {
                selected = choice.Id;
                var game = new UbisoftInstalledGame(choice.Id, _title.Text.Trim(), _directory.Text.Trim(), _executable.Text.Trim(), _arguments.Text, _language.Text.Trim());
                UbisoftConfigurationStore.ValidateGame(game);
                games.RemoveAll(g => g.ProductId == choice.Id);
                games.Add(game);
            }
            await _store.Save(new(_runtime.Text.Trim(), games));
            Populate(selected);
            _status.Text = "Saved. Launch the game from your Crimson library. Saves stay in Crimson's Ubisoft data directory.";
        }
        catch (Exception error) when (error is ArgumentException or System.IO.FileNotFoundException) { _status.Text = error.Message; }
        catch (Exception) { _status.Text = "Could not save configuration. Check paths and permissions."; }
        finally { _busy = false; _save.IsEnabled = true; }
    }
    private async Task Remove()
    {
        if (_busy || _game.SelectedItem is not Choice choice) return;
        _busy = true;
        try
        {
            await _store.Save(_store.Current with { Games = _store.Current.Games.Where(g => g.ProductId != choice.Id).ToList() });
            Populate(choice.Id);
            _status.Text = "Installation forgotten. Your game files and saves were kept.";
        }
        catch (Exception) { _status.Text = "Could not update configuration."; }
        finally { _busy = false; }
    }
}
