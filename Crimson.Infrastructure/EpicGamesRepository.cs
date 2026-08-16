using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Microsoft.Extensions.Logging;

namespace Crimson.Repository;

public sealed class EpicGamesRepository : IStoreRepository
{
    private const string LauncherHost = "launcher-public-service-prod06.ol.epicgames.com";
    private const string CatalogHost = "catalog-public-service-prod06.ol.epicgames.com";
    private const string OAuthHost = "account-public-service-prod03.ol.epicgames.com";
    private const long MaximumJsonBytes = 16 * 1024 * 1024;
    private const long MaximumManifestBytes = 512 * 1024 * 1024;
    private const long MaximumFileBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumGetAttempts = 3;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    private readonly HttpClient _apiClient;
    private readonly HttpClient _contentClient;
    private readonly ILogger<EpicGamesRepository> _log;
    private readonly IAccessTokenProvider _accessTokenProvider;

    public EpicGamesRepository(
        IAccessTokenProvider accessTokenProvider,
        ILogger<EpicGamesRepository> logger,
        HttpClient apiClient,
        HttpClient contentClient)
    {
        _log = logger;
        _accessTokenProvider = accessTokenProvider;
        _apiClient = apiClient;
        _contentClient = contentClient;
    }

    public async Task<RepositoryResult<Metadata>> FetchGameMetaData(
        string nameSpace,
        string catalogItemId,
        CancellationToken cancellationToken = default)
    {
        var authorization = await GetAuthorizationAsync(cancellationToken);
        if (authorization is null)
            return AuthenticationFailure<Metadata>();

        var uri = EpicEndpointPolicy.RequireApiUri(
            $"https://{CatalogHost}/catalog/api/shared/namespace/{Uri.EscapeDataString(nameSpace)}/bulk/items?id={Uri.EscapeDataString(catalogItemId)}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en");

        try
        {
            using var response = await SendGetWithRetryAsync(_apiClient, uri, authorization, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return HttpFailure<Metadata>(response);

            var bytes = await BoundedHttpContent.ReadBytesAsync(
                response.Content,
                MaximumJsonBytes,
                cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            var firstProperty = document.RootElement.EnumerateObject().FirstOrDefault();
            if (firstProperty.Value.ValueKind == JsonValueKind.Undefined)
                return InvalidResponse<Metadata>("Metadata response did not contain a game record.");

            var metadata = firstProperty.Value.Deserialize<Metadata>();
            return metadata is null
                ? InvalidResponse<Metadata>("Metadata response could not be parsed.")
                : RepositoryResult<Metadata>.Success(metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FailureFromException<Metadata>(exception, "Metadata request failed.");
        }
    }

    public async Task<RepositoryResult<IReadOnlyList<Asset>>> FetchGameAssets(
        EpicPayloadPlatform platform,
        string label = "Live",
        CancellationToken cancellationToken = default)
    {
        var authorization = await GetAuthorizationAsync(cancellationToken);
        if (authorization is null)
            return AuthenticationFailure<IReadOnlyList<Asset>>();

        var uri = EpicEndpointPolicy.RequireApiUri(
            $"https://{LauncherHost}/launcher/api/public/assets/{Uri.EscapeDataString(platform.ToApiValue())}?label={Uri.EscapeDataString(label)}");

        try
        {
            using var response = await SendGetWithRetryAsync(_apiClient, uri, authorization, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return HttpFailure<IReadOnlyList<Asset>>(response);

            var bytes = await BoundedHttpContent.ReadBytesAsync(
                response.Content,
                MaximumJsonBytes,
                cancellationToken);
            var assets = JsonSerializer.Deserialize<List<Asset>>(bytes);
            return assets is null
                ? InvalidResponse<IReadOnlyList<Asset>>("Asset response could not be parsed.")
                : RepositoryResult<IReadOnlyList<Asset>>.Success(assets);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FailureFromException<IReadOnlyList<Asset>>(exception, "Asset request failed.");
        }
    }

    public async Task<RepositoryResult<byte[]>> GetGameManifest(
        GetManifestUrlData urlData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urlData);
        RepositoryFailure? lastFailure = null;
        foreach (var value in urlData.ManifestUrls)
        {
            try
            {
                var uri = EpicEndpointPolicy.RequireContentUri(value);
                _log.LogInformation("GetGameManifest: Trying content endpoint {ManifestUri}",
                SensitiveDataRedactor.UriWithoutQuery(uri.AbsoluteUri));
                using var response = await SendGetWithRetryAsync(
                    _contentClient,
                    uri,
                    authorization: null,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastFailure = CreateHttpFailure(response);
                    continue;
                }

                var bytes = await BoundedHttpContent.ReadBytesAsync(
                    response.Content,
                    MaximumManifestBytes,
                    cancellationToken);
                return RepositoryResult<byte[]>.Success(bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = CreateFailureFromException(exception, "Manifest request failed.");
            }
        }

        return RepositoryResult<byte[]>.Failed(lastFailure ?? new RepositoryFailure(
            RepositoryFailureKind.InvalidResponse,
            "No manifest endpoint was available."));
    }

    public async Task<RepositoryResult<long>> DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var partialPath = destinationPath + ".partial";
        try
        {
            var uri = EpicEndpointPolicy.RequireContentUri(url);
            using var response = await SendGetWithRetryAsync(
                _contentClient,
                uri,
                authorization: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return HttpFailure<long>(response);

            var directoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directoryPath))
                Directory.CreateDirectory(directoryPath);

            File.Delete(partialPath);
            var bytesWritten = await BoundedHttpContent.CopyToFileAsync(
                response.Content,
                partialPath,
                MaximumFileBytes,
                cancellationToken);
            File.Move(partialPath, destinationPath, overwrite: true);
            return RepositoryResult<long>.Success(bytesWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FailureFromException<long>(exception, "File download failed.");
        }
        finally
        {
            try
            {
                File.Delete(partialPath);
            }
            catch (Exception exception)
            {
                _log.LogWarning("Failed to remove a partial repository download with {ErrorType}",
                exception.GetType().Name);
            }
        }
    }

    public async Task<RepositoryResult<string>> GetGameToken(
        CancellationToken cancellationToken = default)
    {
        var authorization = await GetAuthorizationAsync(cancellationToken);
        if (authorization is null)
            return AuthenticationFailure<string>();

        var uri = EpicEndpointPolicy.RequireApiUri($"https://{OAuthHost}/account/api/oauth/exchange");
        try
        {
            using var response = await SendGetWithRetryAsync(_apiClient, uri, authorization, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return HttpFailure<string>(response);

            var value = await BoundedHttpContent.ReadUtf8Async(
                response.Content,
                MaximumJsonBytes,
                cancellationToken);
            return RepositoryResult<string>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FailureFromException<string>(exception, "Game token request failed.");
        }
    }

    public async Task<RepositoryResult<GetManifestUrlData>> GetManifestUrls(
        string nameSpace,
        string catalogItem,
        string appName,
        EpicPayloadPlatform platform,
        string label = "Live",
        CancellationToken cancellationToken = default)
    {
        var authorization = await GetAuthorizationAsync(cancellationToken);
        if (authorization is null)
            return AuthenticationFailure<GetManifestUrlData>();

        var uri = EpicEndpointPolicy.RequireApiUri(
            $"https://{LauncherHost}/launcher/api/public/assets/v2/platform/{Uri.EscapeDataString(platform.ToApiValue())}/namespace/{Uri.EscapeDataString(nameSpace)}/catalogItem/{Uri.EscapeDataString(catalogItem)}/app/{Uri.EscapeDataString(appName)}/label/{Uri.EscapeDataString(label)}");
        try
        {
            using var response = await SendGetWithRetryAsync(_apiClient, uri, authorization, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return HttpFailure<GetManifestUrlData>(response);

            var bytes = await BoundedHttpContent.ReadBytesAsync(
                response.Content,
                MaximumJsonBytes,
                cancellationToken);
            var data = JsonSerializer.Deserialize<ManifestUrlData>(bytes);
            if (data?.Elements is not { Count: > 0 } || data.Elements[0].Manifests is null)
                return InvalidResponse<GetManifestUrlData>("Manifest metadata response was invalid.");

            var manifestUrls = new List<string>();
            var baseUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.Elements[0].Manifests)
            {
                if (!Uri.TryCreate(entry.Uri, UriKind.Absolute, out var contentUri) ||
                    !EpicEndpointPolicy.IsAllowedContentUri(contentUri))
                {
                    _log.LogWarning("Rejected unapproved Epic CDN host {Host}",
                    contentUri?.Host ?? "invalid");
                    continue;
                }

                var builder = new UriBuilder(contentUri);
                if (entry.QueryParams is { Count: > 0 })
                {
                    builder.Query = string.Join(
                        "&",
                        entry.QueryParams.Select(parameter =>
                            $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"));
                }

                manifestUrls.Add(builder.Uri.AbsoluteUri);
                var lastSlash = contentUri.AbsolutePath.LastIndexOf('/');
                if (lastSlash <= 0)
                    continue;

                var baseBuilder = new UriBuilder(contentUri)
                {
                    Path = contentUri.AbsolutePath[..lastSlash],
                    Query = string.Empty,
                    Fragment = string.Empty
                };
                baseUrls.Add(baseBuilder.Uri.AbsoluteUri.TrimEnd('/'));
            }

            if (manifestUrls.Count == 0)
                return InvalidResponse<GetManifestUrlData>("Manifest metadata contained no usable endpoints.");

            return RepositoryResult<GetManifestUrlData>.Success(new GetManifestUrlData
            {
                BaseUrls = baseUrls.ToList(),
                ManifestUrls = manifestUrls,
                ManifestHash = data.Elements[0].Hash
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FailureFromException<GetManifestUrlData>(exception, "Manifest metadata request failed.");
        }
    }

    private async Task<AuthenticationHeaderValue?> GetAuthorizationAsync(CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokenProvider.GetAccessToken(cancellationToken);
        return string.IsNullOrWhiteSpace(accessToken)
            ? null
            : new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static async Task<HttpResponseMessage> SendGetWithRetryAsync(
        HttpClient client,
        Uri uri,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = authorization;
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (attempt >= MaximumGetAttempts || !IsRetryable(response.StatusCode))
                return response;

            var delay = GetRetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta;
        if (!delay.HasValue && retryAfter?.Date is { } date)
            delay = date - DateTimeOffset.UtcNow;

        if (!delay.HasValue || delay <= TimeSpan.Zero)
            delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));

        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay.Value;
    }

    private static RepositoryResult<T> AuthenticationFailure<T>() => RepositoryResult<T>.Failed(
        new RepositoryFailure(
            RepositoryFailureKind.Authentication,
            "No authenticated Epic session is available."));

    private static RepositoryResult<T> HttpFailure<T>(HttpResponseMessage response) =>
        RepositoryResult<T>.Failed(CreateHttpFailure(response));

    private static RepositoryFailure CreateHttpFailure(HttpResponseMessage response) => new(
        RepositoryFailureKind.Http,
        $"Epic endpoint returned HTTP {(int)response.StatusCode}.",
        response.StatusCode);

    private static RepositoryResult<T> InvalidResponse<T>(string message) =>
        RepositoryResult<T>.Failed(new RepositoryFailure(RepositoryFailureKind.InvalidResponse, message));

    private static RepositoryResult<T> FailureFromException<T>(Exception exception, string message) =>
        RepositoryResult<T>.Failed(CreateFailureFromException(exception, message));

    private static RepositoryFailure CreateFailureFromException(Exception exception, string message) => exception switch
    {
        ResponseBodyTooLargeException => new RepositoryFailure(RepositoryFailureKind.SizeLimit, message),
        JsonException or InvalidDataException => new RepositoryFailure(RepositoryFailureKind.InvalidResponse, message),
        HttpRequestException httpException => new RepositoryFailure(
            RepositoryFailureKind.Network,
            message,
            httpException.StatusCode),
        InvalidOperationException => new RepositoryFailure(RepositoryFailureKind.Policy, message),
        _ => new RepositoryFailure(RepositoryFailureKind.Network, message)
    };
}
