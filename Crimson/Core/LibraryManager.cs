using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Crimson.Models;
using Serilog;
using Crimson.Utils;
using Crimson.Repository;

namespace Crimson.Core;

public class LibraryManager
{
    private readonly ILogger _log;
    private readonly IStoreRepository _storeRepository;
    private readonly Storage _storage;

    public event Action<IEnumerable<Game>> LibraryUpdated;
    public event Action<Game> GameStatusUpdated;

    private DateTime _lastUpdateDateTime = DateTime.MinValue;

    public LibraryManager(ILogger log, IStoreRepository repository, Storage storage)
    {
        _log = log;
        _storeRepository = repository;
        _storage = storage;
    }
    /// <summary>
    /// Public method to get library data, call UpdateLibraryData it's been more than 20 minutes since last update
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <returns></returns>
    public async Task<IEnumerable<Game>> GetLibraryData(bool forceUpdate = false)
    {
        // Only update library data if it's been more than 20 minutes since last update
        var dataNeedsUpdate = forceUpdate || (_lastUpdateDateTime == DateTime.MinValue) || (DateTime.Now - _lastUpdateDateTime > TimeSpan.FromMinutes(20));

        if (!dataNeedsUpdate)
            return _storage.GameMetaDataDictionary.Values.ToList();

        // Update the library data
        await UpdateLibraryData(forceUpdate);
        // Optionally, you can update the last update timestamp here
        _lastUpdateDateTime = DateTime.Now;

        return _storage.GameMetaDataDictionary.Values.ToList();
    }
    /// <summary>
    /// Get list of installed games
    /// </summary>
    /// <returns></returns>
    public IEnumerable<InstalledGame> GetInstalledGames()
    {
        return _storage.InstalledGamesDictionary.Values.ToList();
    }

    public Game GetGameInfo(string name)
    {
        return _storage.GetGameMetaData(name);
    }

    /// <summary>
    /// Updates stored game data and fired GameStatusUpdated event
    /// Only thing that would call this function would be InstallManager
    /// </summary>
    /// <param name="game"></param>
    public void UpdateGameInfo(Game game)
    {
        _storage.SaveMetaData(game);
        GameStatusUpdated?.Invoke(game);
    }

    /// <summary>
    ///  Updates library data and triggers LibraryUpdated event
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <param name="updateAssets"></param>
    /// <returns></returns>
    private async Task UpdateLibraryData(bool forceUpdate = false, bool updateAssets = true)
    {
        try
        {
            var metadataUpdated = false;
            var gameAssets = await _storage.GetGameAssetsData();
            if (gameAssets == null)
            {
                _log.Information("UpdateLibraryData: No cached game assets found");
            }
            var gameAssetsList = gameAssets?.ToList() ?? new List<Asset>();
            if (forceUpdate || gameAssetsList.Count < 1)
            {
                _log.Error("UpdateLibraryData: No existing game assets data, updating");

                var assets = (await _storeRepository.FetchGameAssets()).ToList();
                if (assets.Count < 1)
                {
                    _log.Error("GetLibraryData: Error while fetching game assets");
                    return;
                }
                gameAssetsList = assets.ToList();
            }

            var fetchList = new List<FetchListItem>();
            var gameMetaDataDictionary = new Dictionary<string, Models.Game>();

            foreach (var asset in gameAssetsList)
            {
                if (asset.Namespace.Contains("ue")) continue;

                var game = _storage.GetGameMetaData(asset.AppName);
                var assetUpdated = false;
                if (game != null)
                {
                    assetUpdated = asset.BuildVersion != game.AssetInfos.Windows.BuildVersion;
                    gameMetaDataDictionary.Add(asset.AppName, game);
                }

                if (!updateAssets || (game != null && !forceUpdate && !assetUpdated)) continue;
                _log.Information($"Scheduling metadata update for {asset.AppName}");
                fetchList.Add(new FetchListItem()
                {
                    AppName = asset.AppName,
                    NameSpace = asset.Namespace,
                    CatalogItemId = asset.CatalogItemId
                });
            }

            // Only update metadata if there are any updates or if forced
            foreach (var item in fetchList)
            {
                var egFetchGameMetaData = await _storeRepository.FetchGameMetaData(item.NameSpace, item.CatalogItemId);

                // ignore if no metadata can be fetched
                if (egFetchGameMetaData == null) continue;
                var gameMetaData = new Models.Game()
                {
                    AppName = item.AppName,
                    AppTitle = egFetchGameMetaData.Title,
                    AssetInfos = new AssetInfos()
                    {
                        Windows = gameAssetsList.FirstOrDefault(asset => asset.AppName == item.AppName),
                    },
                    Metadata = egFetchGameMetaData
                };
                _storage.SaveMetaData(gameMetaData);
            }

            gameMetaDataDictionary = _storage.GameMetaDataDictionary;

            // Sort _gameData by name
            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(gameMetaDataDictionary.Values.ToList());
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
        }
    }

}

internal class FetchListItem
{
    public string AppName { get; set; }
    public string NameSpace { get; set; }
    public string CatalogItemId { get; set; }
}

