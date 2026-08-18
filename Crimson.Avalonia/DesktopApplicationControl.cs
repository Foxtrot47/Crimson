using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Crimson.Presentation;

namespace Crimson.Avalonia;

public sealed class DesktopApplicationControl(
    IClassicDesktopStyleApplicationLifetime lifetime,
    Window mainWindow) : IDesktopApplicationControl
{
    public void ToggleMainWindow()
    {
        if (mainWindow.IsVisible)
        {
            mainWindow.Hide();
            return;
        }
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    public void Quit() => lifetime.Shutdown();
}
