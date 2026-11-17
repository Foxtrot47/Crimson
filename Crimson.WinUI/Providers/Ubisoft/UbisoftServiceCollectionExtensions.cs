using System;
using System.Net.Http;
using Crimson.Core;
using Crimson.Providers.Ubisoft.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Crimson.Providers.Ubisoft;

public static class UbisoftServiceCollectionExtensions
{
    public static IServiceCollection AddUbisoftLauncher(this IServiceCollection services, string crimsonDataRoot)
    {
        var root = ProviderDataPaths.ForProvider(crimsonDataRoot, UbisoftLauncherProvider.ProviderId);
        // Authentication and activation requests are deliberately not wrapped in retry policies.
        services.AddHttpClient("UbisoftApi", client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton(provider => new UbisoftApi(provider.GetRequiredService<IHttpClientFactory>().CreateClient("UbisoftApi")));
        services.AddSingleton<IUbisoftSessionStore>(provider => new UbisoftSessionStore(root, provider.GetRequiredService<ICredentialProtector>()));
        services.AddSingleton(provider => new UbisoftSignInCredentialStore(root,
            provider.GetRequiredService<IUserCredentialProtector>()));
        services.AddSingleton<UbisoftAuthentication>();
        services.AddSingleton(new UbisoftMetadata());
        services.AddSingleton(provider => new UbisoftOwnershipCache(root, provider.GetRequiredService<UbisoftMetadata>(),
            provider.GetRequiredService<UbisoftAuthentication>()));
        services.AddSingleton<IUbisoftProductSource>(provider => provider.GetRequiredService<UbisoftOwnershipCache>());
        services.AddSingleton(new UbisoftInstalledStore(root));
        services.AddSingleton<IUbisoftTickets, UbisoftTickets>();
        services.AddSingleton<IUbisoftSystemSetup, UbisoftSystemSetup>();
        // Cloud save objects live on presigned storage URLs; the client sends no cookies or credentials of its own.
        services.AddHttpClient("UbisoftCloudSaves", client => client.Timeout = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton(provider => new UbisoftCloudSaves(root, provider.GetRequiredService<UbisoftApi>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("UbisoftCloudSaves"),
            provider.GetRequiredService<ILogger>()));
        services.AddSingleton<ICloudSaveConflictSource>(provider => provider.GetRequiredService<UbisoftCloudSaves>());
        services.AddSingleton<IUbisoftRuntime>(provider => new UbisoftRuntime(provider.GetRequiredService<UbisoftApi>(),
            provider.GetRequiredService<IUbisoftTickets>(), provider.GetRequiredService<ILogger>(), root));
        services.AddSingleton(provider => new UbisoftInstallManager(provider.GetRequiredService<UbisoftAuthentication>(),
            provider.GetRequiredService<IUbisoftProductSource>(), provider.GetRequiredService<UbisoftInstalledStore>(),
            provider.GetRequiredService<IUbisoftSystemSetup>(), provider.GetRequiredService<ILogger>()));
        services.AddSingleton(provider => new UbisoftLibrary(
            provider.GetRequiredService<UbisoftAuthentication>(), provider.GetRequiredService<UbisoftOwnershipCache>(),
            provider.GetRequiredService<UbisoftMetadata>(), provider.GetRequiredService<UbisoftInstalledStore>(),
            provider.GetRequiredService<IUbisoftRuntime>(), provider.GetRequiredService<ILogger>(),
            provider.GetRequiredService<UbisoftCloudSaves>(),
            // Resolved per call: the installer and the library are independent singletons.
            prepareLaunch: (productId, ct) =>
                provider.GetRequiredService<UbisoftInstallManager>().PrepareLaunchAsync(productId, ct)));
        services.AddSingleton<ILauncherProvider>(provider => new UbisoftLauncherProvider(
            provider.GetRequiredService<UbisoftAuthentication>(), provider.GetRequiredService<UbisoftLibrary>(),
            provider.GetRequiredService<UbisoftInstallManager>()));
        services.AddSingleton<ILauncherFrontend, UbisoftFrontend>();
        services.AddSingleton<IProviderContentPolicy, UbisoftContentPolicy>();
        return services;
    }
}
