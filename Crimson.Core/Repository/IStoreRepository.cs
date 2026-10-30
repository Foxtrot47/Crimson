using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Repository;

public interface IStoreRepository
{
    public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId);

    public Task<StoreProductDetails?> FetchStoreProductDetails(
        string nameSpace,
        CancellationToken cancellationToken = default);

    public Task<long?> FetchGamePlaytimeSeconds(
        string appName,
        CancellationToken cancellationToken = default);

    public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live");

    public Task<IReadOnlyList<StoreSearchResult>> SearchStore(
        string query,
        CancellationToken cancellationToken = default);

    public Task<byte[]> GetGameManifest(
        GetManifestUrlData urlData,
        CancellationToken cancellationToken = default);

    public Task DownloadFileAsync(string url, string destinationPath);

    public Task<string> GetGameToken();

    public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId);

    public Task<GetManifestUrlData> GetManifestUrls(
        string nameSpace,
        string catalogItem,
        string appName,
        string platform = "Windows",
        string label = "Live",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the account's cloud save files for a game. With <paramref name="manifestsOnly"/>
    /// only save manifests are listed, otherwise chunks are included as well.
    /// </summary>
    public Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> GetCloudSaveFilesAsync(
        string appName,
        bool manifestsOnly,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves cloud save file paths for a game; the returned entries carry pre-signed
    /// write links the file contents are uploaded to.
    /// </summary>
    public Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> CreateCloudSaveFilesAsync(
        string appName,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads a cloud save file through its pre-signed read link.</summary>
    public Task<byte[]?> DownloadCloudSaveFileAsync(string readLink, CancellationToken cancellationToken = default);

    /// <summary>Uploads a cloud save file through its pre-signed write link.</summary>
    public Task<bool> UploadCloudSaveFileAsync(
        string writeLink,
        byte[] data,
        CancellationToken cancellationToken = default);
}
