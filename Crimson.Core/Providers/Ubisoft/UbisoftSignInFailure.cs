using System;
using System.Net;
using System.Net.Http;
using Crimson.Resources;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Turns a sign-in failure into advice a person can act on. Protocol status codes and response
/// bodies never reach the dialog, because they carry no useful instruction and may carry secrets.
/// </summary>
public static class UbisoftSignInFailure
{
    public static string Describe(Exception error, bool codeStep) => error is UbisoftApiException api
        ? api.Status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest =>
                codeStep ? UiStrings.UbisoftInvalidCode : UiStrings.UbisoftInvalidCredentials,
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => UiStrings.UbisoftSignInBlocked,
            _ => UiStrings.UbisoftSignInUnavailable
        }
        // Timeouts, DNS and TLS failures all reach the person as "could not be reached".
        : UiStrings.UbisoftSignInUnavailable;
}
