using System.Xml.Linq;
using Crimson.Providers.Ubisoft;

namespace Crimson.Tests;

/// <summary>The Ubisoft sign-in dialog is a two-step form that must stay localized and actionable.</summary>
public sealed class UbisoftSignInTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DialogOffersCredentialsThenTheAuthenticatorStep()
    {
        var view = Load();
        var email = Named(view, "EmailBox");
        Assert.Equal("{x:Bind strings:UiStrings.Email}", (string?)email.Attribute("Header"));

        var password = Named(view, "PasswordInput");
        Assert.Equal("{x:Bind strings:UiStrings.Password}", (string?)password.Attribute("Header"));
        Assert.Equal("Peek", (string?)password.Attribute("PasswordRevealMode"));

        Assert.NotNull(Named(view, "RememberBox"));
        Assert.Equal("Collapsed", (string?)Named(view, "TwoFactorPanel").Attribute("Visibility"));
        Assert.Equal("Number", (string?)Named(view, "CodeBox").Attribute("InputScope"));
        Assert.Equal("6", (string?)Named(view, "CodeBox").Attribute("MaxLength"));
        Assert.Equal("{x:Bind strings:UiStrings.SignIn}", (string?)Named(view, "SubmitButton").Attribute("Content"));
        Assert.Equal("Error", (string?)Named(view, "ErrorBar").Attribute("Severity"));
        Assert.Equal("Collapsed", (string?)Named(view, "BusyRing").Attribute("Visibility"));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, false, "Ubisoft did not accept that email or password.")]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, true, "That code was not accepted. Try again.")]
    [InlineData(System.Net.HttpStatusCode.Forbidden, false, "Ubisoft blocked this sign-in. Try again later.")]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, false, "Ubisoft blocked this sign-in. Try again later.")]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, false, "Ubisoft could not be reached. Check your connection and try again.")]
    public void FailuresExplainTheNextStepWithoutProtocolDetail(
        System.Net.HttpStatusCode status, bool codeStep, string expected)
    {
        var message = UbisoftSignInFailure.Describe(new UbisoftApiException(status), codeStep);

        Assert.Equal(expected, message);
        Assert.DoesNotContain(((int)status).ToString(), message);
    }

    [Fact]
    public void NetworkFailuresAskToCheckTheConnection()
    {
        Assert.Equal("Ubisoft could not be reached. Check your connection and try again.",
            UbisoftSignInFailure.Describe(new HttpRequestException("dns"), false));
        Assert.Equal("Ubisoft could not be reached. Check your connection and try again.",
            UbisoftSignInFailure.Describe(new TaskCanceledException(), true));
    }

    [Fact]
    public void FormUsesAColumnNarrowEnoughForADialog()
    {
        // Browser sign-in needs a page; this form is laid out for a dialog-sized column.
        var column = Assert.Single(Load().Descendants(), element => element.Name.LocalName == "StackPanel" &&
            (string?)element.Attribute("Width") == "400");
        Assert.NotNull(column);
    }

    private static XElement Named(XDocument document, string name) =>
        Assert.Single(document.Descendants(), element => (string?)element.Attribute(X + "Name") == name);

    private static XDocument Load() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Presentation", "UbisoftSignInView.xaml"));
}
