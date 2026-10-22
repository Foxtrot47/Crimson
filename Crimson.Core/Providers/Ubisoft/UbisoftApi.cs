using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftApiException(HttpStatusCode status) : Exception($"Ubisoft request failed (HTTP {(int)status}).")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Per-request credentials prevent cross-account header reuse. Error bodies can contain secrets and are never surfaced.</summary>
public sealed class UbisoftApi(HttpClient http)
{
    public const string AppId = "f68a4bb5-608a-4ff2-8123-be8ef797e0a6";
    private const string BaseUri = "https://public-ubiservices.ubi.com";
    internal Task<JsonElement> Password(string email, string password, bool remember, CancellationToken token) => Send(
        HttpMethod.Post, "/v3/profiles/sessions", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + password)),
        new { rememberMe = remember, enforceTwoFactorSession = false }, null, null, token);
    internal Task<JsonElement> TwoFactor(string challenge, string code, CancellationToken token) => Send(
        HttpMethod.Post, "/v3/profiles/sessions", "ubi_2fa_v1 t=" + challenge, new { twoFactorCode = code }, null, code, token);
    internal Task<JsonElement> Remember(string ticket, CancellationToken token) => Send(
        HttpMethod.Post, "/v3/profiles/sessions", "ubi_v1 t=" + ticket, new { }, null, null, token);
    public Task<JsonElement> Get(UbisoftSession session, string path, CancellationToken token = default) => Send(
        HttpMethod.Get, path, "Ubi_v1 t=" + session.Ticket, null, session.SessionId, null, token);
    public async Task PlaySession(UbisoftSession session, uint productId, bool end, CancellationToken token)
    {
        var path = $"/v1/profiles/{Uri.EscapeDataString(session.ProfileId)}/global/ubiconnect/playsession/api/sessions";
        await Send(end ? HttpMethod.Delete : HttpMethod.Put, end ? path + "?productId=" + productId : path,
            "Ubi_v1 t=" + session.Ticket, end ? null : new { productId }, session.SessionId, null, token);
    }
    private async Task<JsonElement> Send(HttpMethod method, string path, string authorization, object? body,
        string? session, string? code, CancellationToken token)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)) throw new ArgumentException("Expected Ubisoft API path.");
        using var request = new HttpRequestMessage(method, BaseUri + path);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.Add("Ubi-AppId", AppId);
        request.Headers.Add("Ubi-RequestedPlatformType", "uplay");
        request.Headers.UserAgent.ParseAdd("Massgate_174.2.0.13389");
        if (!string.IsNullOrEmpty(session)) request.Headers.Add("Ubi-SessionId", session);
        if (code is not null) request.Headers.Add("Ubi-2FA-Code", code);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new UbisoftApiException(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length == 0) return default;
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
}

public sealed class UbisoftSession
{
    public required string Ticket { get; init; }
    public required string SessionId { get; init; }
    public required string ProfileId { get; init; }
    public required string UserId { get; init; }
    public string Name { get; init; } = "";
    public string Country { get; init; } = "";
    public string? RememberMeTicket { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public override string ToString() => "Ubisoft session (credentials redacted)";
    internal static UbisoftSession Parse(JsonElement json, bool remember)
    {
        string Required(string key) => json.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new JsonException("Incomplete Ubisoft session.");
        string Optional(string key) => json.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        return new()
        {
            Ticket = Required("ticket"), SessionId = Required("sessionId"), ProfileId = Required("profileId"), UserId = Required("userId"),
            Name = Optional("nameOnPlatform"), Country = Optional("country"),
            RememberMeTicket = remember ? Optional("rememberMeTicket") : null,
            ExpiresAt = DateTimeOffset.TryParse(Optional("expiration"), out var expires) ? expires : DateTimeOffset.UtcNow.AddMinutes(30)
        };
    }
}
