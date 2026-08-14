using System;
using System.IO;
using System.Net.Http;
using Crimson.Core;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Crimson.Views;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Serilog;

namespace Crimson
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
        // https://docs.microsoft.com/dotnet/core/extensions/generic-host
        // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
        // https://docs.microsoft.com/dotnet/core/extensions/configuration
        // https://docs.microsoft.com/dotnet/core/extensions/logging
        public IHost Host
        {
            get;
        }
        public static bool HandleClosedEvents { get; set; } = true;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            this.UnhandledException += App_UnhandledException;

            Host = Microsoft.Extensions.Hosting.Host.
            CreateDefaultBuilder().
            UseContentRoot(AppContext.BaseDirectory).
            ConfigureServices((context, services) =>
            {
                services.AddSingleton<SettingsManager>();
                services.AddSingleton<ILogger>(provider =>
                {
                    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _ = Directory.CreateDirectory($@"{appDataPath}\Crimson\logs");
                    var logFilePath = $@"{appDataPath}\Crimson\logs\{DateTime.Now:yyyy-MM-dd}.txt";

                    return new LoggerConfiguration()
                        .MinimumLevel.Information()
                        .WriteTo.File(
                            logFilePath,
                            rollingInterval: RollingInterval.Month,
                            rollOnFileSizeLimit: true,
                            retainedFileCountLimit: 30,
                            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                        )
                        .CreateLogger();
                });
                services.AddHttpClient("EpicOAuth", client =>
                        ConfigureEpicClient(client, TimeSpan.FromSeconds(15)))
                    .ConfigurePrimaryHttpMessageHandler(() => CreateSecureHttpHandler(TimeSpan.FromSeconds(5)));
                services.AddHttpClient("EpicApi", client =>
                        ConfigureEpicClient(client, TimeSpan.FromSeconds(30)))
                    .ConfigurePrimaryHttpMessageHandler(() => CreateSecureHttpHandler(TimeSpan.FromSeconds(10)));
                services.AddHttpClient("EpicContent", client =>
                        ConfigureEpicClient(client, TimeSpan.FromMinutes(10)))
                    .ConfigurePrimaryHttpMessageHandler(() => CreateSecureHttpHandler(TimeSpan.FromSeconds(15)));

                services.AddSingleton<Storage>();
                services.AddSingleton<AuthManager>(provider => new AuthManager(
                    provider.GetRequiredService<ILogger>(),
                    provider.GetRequiredService<Storage>(),
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicOAuth")));
                services.AddSingleton<IStoreRepository>(provider => new EpicGamesRepository(
                    provider.GetRequiredService<AuthManager>(),
                    provider.GetRequiredService<ILogger>(),
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicApi"),
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicContent")));
                services.AddSingleton<LibraryManager>();
                services.AddSingleton<InstallManager>();
                services.AddSingleton<DownloadManager>(provider => new DownloadManager(
                    provider.GetRequiredService<ILogger>(),
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicContent")));

                services.AddTransient<SettingsViewModel>();
                services.AddTransient<DownloadsViewModel>();
                services.AddTransient<LibraryViewModel>();
                services.AddTransient<GameInfoViewModel>();
                services.AddTransient<AppInstallDialogViewModel>();
            }).
            Build();
        }

        private static void ConfigureEpicClient(HttpClient client, TimeSpan requestTimeout)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
            client.Timeout = requestTimeout;
        }

        private static HttpMessageHandler CreateSecureHttpHandler(TimeSpan connectTimeout) => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 16,
            ConnectTimeout = connectTimeout
        };

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();
            m_window.Closed += OnExit;
        }

        protected void OnExit(object sender, WindowEventArgs args)
        {
            if (HandleClosedEvents)
            {
                args.Handled = true;
                m_window.Hide();
            }
        }

        private Window m_window;

        public Window GetWindow()
        {
            return m_window;
        }
        public static T GetService<T>() where T : class
        {
            if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
            {
                throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
            }

            return service;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var logger = Host?.Services?.GetService(typeof(ILogger)) as ILogger;
            logger?.Fatal("Unhandled exception of type {ErrorType}", e.Exception.GetType().Name);
        }
    }
}
