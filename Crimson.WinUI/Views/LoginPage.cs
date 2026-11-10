using Crimson.Providers;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Views;

/// <summary>Hosts one provider-owned sign-in view, sized the way that provider asked for.</summary>
public sealed class LoginPage(ILauncherSignInView signIn) : Page
{
    private readonly ILauncherSignInView _signIn = signIn;

    public void InitializeSignIn()
    {
        var size = _signIn.PreferredSize;
        Width = size.Width;
        Height = size.Height;
        Content = _signIn.Element;
        _signIn.InitializeSignIn();
    }

    public void CloseSignIn()
    {
        _signIn.CloseSignIn();
        Content = null;
    }
}
