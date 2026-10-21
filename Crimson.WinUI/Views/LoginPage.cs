using Crimson.Providers;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Views;

/// <summary>Hosts provider-owned sign-in, including non-browser and multi-step flows.</summary>
public sealed class LoginPage : Page
{
    private ILauncherSignInView? _signIn;

    public void InitializeSignIn(string providerId)
    {
        CloseSignIn();
        _signIn = App.GetService<LauncherFrontendRegistry>().Get(providerId).CreateSignInView();
        Content = _signIn.Element;
        _signIn.InitializeSignIn();
    }

    public void CloseSignIn()
    {
        _signIn?.CloseSignIn();
        _signIn = null;
        Content = null;
    }
}
