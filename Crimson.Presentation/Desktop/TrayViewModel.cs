using CommunityToolkit.Mvvm.Input;

namespace Crimson.Presentation;

public interface IDesktopApplicationControl
{
    void ToggleMainWindow();

    void Quit();
}

public partial class TrayViewModel(IDesktopApplicationControl application)
{
    [RelayCommand]
    private void Toggle() => application.ToggleMainWindow();

    [RelayCommand]
    private void Quit() => application.Quit();
}
