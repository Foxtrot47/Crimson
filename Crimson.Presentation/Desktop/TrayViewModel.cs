using CommunityToolkit.Mvvm.Input;

namespace Crimson.Presentation;

public interface IDesktopApplicationControl
{
    void ShowMainWindow();

    void Quit();
}

public partial class TrayViewModel(IDesktopApplicationControl application)
{
    [RelayCommand]
    private void Show() => application.ShowMainWindow();

    [RelayCommand]
    private void Quit() => application.Quit();
}
