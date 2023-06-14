// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT;
using WinUiApp.Legendary;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUiApp;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{

    public MainWindow()
    {
        InitializeComponent();

        ContentFrame.Navigate(typeof(Library));
    }

    private void navControl_BackRequested(NavigationView sender,
                                   NavigationViewBackRequestedEventArgs args)
    {
        if (!ContentFrame.CanGoBack)
            return;

        // Don't go back if the nav pane is overlayed.
        if (navControl.IsPaneOpen &&
            (navControl.DisplayMode == NavigationViewDisplayMode.Compact ||
             navControl.DisplayMode == NavigationViewDisplayMode.Minimal))
            return;

        ContentFrame.GoBack();
    }

}
