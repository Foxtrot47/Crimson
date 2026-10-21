using System.Net.Http;
using System;
using Crimson.Core;
using Crimson.Repository;
using Crimson.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Serilog;

namespace Crimson.Providers.Epic;

public static class EpicServiceCollectionExtensions
{
    public static IServiceCollection AddEpicLauncher(this IServiceCollection services)
    {
        services.AddHttpClient("EpicOAuth", ConfigureEpicClient)
            .ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler);
        services.AddHttpClient("EpicApi", ConfigureEpicClient)
            .ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler)
            .AddResilienceHandler(
                "CustomPipeline",
                static builder =>
                {
                    // See: https://www.pollydocs.org/strategies/retry.html
                    builder.AddRetry(new HttpRetryStrategyOptions
                    {
                        BackoffType = DelayBackoffType.Exponential,
                        MaxRetryAttempts = 5,
                        UseJitter = true
                    });

                    // See: https://www.pollydocs.org/strategies/timeout.html
                    builder.AddTimeout(TimeSpan.FromSeconds(5));
                });
        services.AddHttpClient("EpicContent", ConfigureEpicClient)
            .ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler);
        services.AddHttpClient("EpicStore", ConfigureEpicStoreClient)
            .ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler);

        services.AddSingleton<EpicAuthManager>(provider => new EpicAuthManager(
            provider.GetRequiredService<ILogger>(),
            provider.GetRequiredService<Storage>(),
            provider.GetRequiredService<ICredentialProtector>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicOAuth")));
        services.AddSingleton<IEpicGamesRepository>(provider => new EpicGamesRepository(
            provider.GetRequiredService<EpicAuthManager>(),
            provider.GetRequiredService<ILogger>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicApi"),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicContent"),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicStore")));
        services.AddSingleton<EpicLibraryManager>();
        services.AddSingleton<EpicInstallManager>();
        services.AddSingleton<EpicDownloadManager>(provider => new EpicDownloadManager(
            provider.GetRequiredService<ILogger>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicContent")));

        services.AddSingleton<IProviderContentPolicy, EpicContentPolicy>();
        services.AddSingleton<ILauncherProvider, EpicLauncherProvider>();
        return services;
    }

    private static void ConfigureEpicClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
        client.Timeout = TimeSpan.FromSeconds(100);
    }

    private static void ConfigureEpicStoreClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EpicGamesLauncher/14.0.8-22004686+++Portal+Release-Live");
        client.Timeout = TimeSpan.FromSeconds(10);
    }

    private static HttpMessageHandler CreateSecureHttpHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies = false,
        MaxConnectionsPerServer = 16
    };

}
