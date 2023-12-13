using Crimson.Core;
using Crimson.Repository;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using Windows.Storage;
using Crimson.Utils;
using System.IO;

namespace Crimson
{
    public static class DependencyResolver
    {
        private static ServiceProvider _serviceProvider;

        public static void Initialize()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILogger>(provider =>
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
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
            services.AddSingleton<Storage>();
            services.AddScoped<IStoreRepository, EpicGamesRepository>();
            services.AddSingleton<AuthManager>();
            services.AddSingleton<LibraryManager>();
            services.AddSingleton<InstallManager>();
            

            _serviceProvider = services.BuildServiceProvider();
        }

        public static T Resolve<T>()
        {
            return _serviceProvider.GetRequiredService<T>();
        }
    }

}
