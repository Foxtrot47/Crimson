using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Repository;

public interface IStoreRepository
{
    Task<RepositoryResult<Metadata>> FetchGameMetaData(
        string nameSpace,
        string catalogItemId,
        CancellationToken cancellationToken = default);

    Task<RepositoryResult<IReadOnlyList<Asset>>> FetchGameAssets(
        EpicPayloadPlatform platform,
        string label = "Live",
        CancellationToken cancellationToken = default);

    Task<RepositoryResult<byte[]>> GetGameManifest(
        GetManifestUrlData urlData,
        CancellationToken cancellationToken = default);

    Task<RepositoryResult<long>> DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<RepositoryResult<string>> GetGameToken(CancellationToken cancellationToken = default);

    Task<RepositoryResult<GetManifestUrlData>> GetManifestUrls(
        string nameSpace,
        string catalogItem,
        string appName,
        EpicPayloadPlatform platform,
        string label = "Live",
        CancellationToken cancellationToken = default);
}
