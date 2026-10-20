using Crimson.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using System;
using System.Collections.Generic;
using System.Linq;
using Crimson.Core;
using Crimson.Models;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Crimson.Views
{
    [ObservableObject]
    public sealed partial class TrayIconView : UserControl
    {
        [ObservableProperty]
        private bool _isWindowVisible;

        private IReadOnlyList<TrayGameMenuItem> _installedGames = [];

        public TrayIconView()
        {
            InitializeComponent();
        }

        internal void UpdateInstalledGames(
            IEnumerable<Game> games,
            bool isLoggedIn,
            Func<string, bool> hasRelatedOperation)
        {
            var items = TrayMenuPresenter.GetInstalledGames(games, isLoggedIn, hasRelatedOperation);
            if (items.SequenceEqual(_installedGames)) return;
            _installedGames = items;
            var insertIndex = TrayMenu.Items.IndexOf(InstalledGamesStart) + 1;
            while (TrayMenu.Items[insertIndex] != InstalledGamesEnd)
                TrayMenu.Items.RemoveAt(insertIndex);
            foreach (var item in items)
            {
                TrayMenu.Items.Insert(insertIndex++, new MenuFlyoutItem
                {
                    Text = TrayMenuPresenter.EscapeMenuText(item.Title),
                    IsEnabled = item.CanLaunch,
                    Command = new RelayCommand(() =>
                        (((App)Application.Current).GetWindow() as MainWindow)?.LaunchFromTray(item.AppName))
                });
            }
            if (items.Count == 0)
                TrayMenu.Items.Insert(insertIndex, new MenuFlyoutItem { Text = UiStrings.NoInstalledGames, IsEnabled = false });
        }

        [RelayCommand]
        private void OpenLibrary() =>
            (((App)Application.Current).GetWindow() as MainWindow)?.OpenLibraryFromTray();

        [RelayCommand]
        private void OpenStore() =>
            (((App)Application.Current).GetWindow() as MainWindow)?.OpenStoreFromTray();

        [RelayCommand]
        public void ShowHideWindow()
        {
            var window = ((App)Application.Current).GetWindow();
            if (window == null)
            {
                return;
            }

            if (window.Visible)
            {
                window.Hide();
            }
            else
            {
                window.Show();
            }
            IsWindowVisible = window.Visible;
        }

        [RelayCommand]
        public void ExitApplication()
        {
            App.HandleClosedEvents = false;
            TrayIcon.Dispose();
            ((App)Application.Current).GetWindow()?.Close();
        }
    }
}
