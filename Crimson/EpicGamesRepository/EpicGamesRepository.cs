using Crimson.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Web.Http;
using Serilog;

namespace Crimson.EpicGamesRepository
{
    internal class EpicGamesRepository: IStoreRepository
    {
        private const string LauncherHost = "launcher-public-service-prod06.ol.epicgames.com";
        private const string CatalogHost = "catalog-public-service-prod06.ol.epicgames.com";
        private const string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

        private static readonly HttpClient HttpClient;
        private ILogger _log;

        static EpicGamesRepository()
        {
            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }
        public EpicGamesRepository()
        {

        }

    }
}
