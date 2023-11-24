using Crimson.Core;
using Crimson.Repository;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using Windows.Storage;
using Crimson.Utils;

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
                var localFolder = ApplicationData.Current.LocalFolder;
                var logFilePath = $@"{localFolder.Path}\logs\{DateTime.Now:yyyy-MM-dd}.txt";
                return new LoggerConfiguration().WriteTo.File(logFilePath).CreateLogger();
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
