using System;
using System.Net.Http;
using Crimson.Core;
using Crimson.Providers.Ubisoft.Protocol;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<UbisoftAuthentication>();
        services.AddSingleton(new UbisoftConfigurationStore(root));
        services.AddSingleton<IUbisoftTickets, UbisoftTickets>();
        services.AddSingleton<IUbisoftRuntime, UbisoftRuntime>();
        services.AddSingleton<UbisoftLibrary>();
        services.AddSingleton<ILauncherProvider, UbisoftLauncherProvider>();
        services.AddSingleton<ILauncherFrontend, UbisoftFrontend>();
        return services;
    }
}
