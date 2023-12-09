using Crimson.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crimson.Repository;

public interface IStoreRepository
{
    public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId);

    public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live");

    public Task<GetGameManifest> GetGameManifest(string nameSpace, string catalogItem, string appName, bool disableHttps = false);
    
    public Task DownloadFileAsync(string url, string destinationPath);

    public Task<string> GetGameToken();
}