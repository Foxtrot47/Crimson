using System.Reflection;
using Crimson.Models;
using Crimson.Providers;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Tests;

public sealed class GameInfoDetailsTests
{
    [Theory]
    [InlineData(null, "Unavailable")]
    [InlineData("", "Unavailable")]
    [InlineData("  ", "Unavailable")]
    [InlineData("7250", "7250")]
    public void MissingDetailUsesExplicitFallback(string? value, string expected)
    {
        Assert.Equal(expected, GameInfoViewModel.GetDetailText(value));
    }

    [Theory]
    [InlineData(null, "Unavailable")]
    [InlineData(-1L, "Unavailable")]
    [InlineData(0L, "0m")]
    [InlineData(59L, "<1m")]
    [InlineData(60L, "1m")]
    [InlineData(3599L, "59m")]
    [InlineData(3600L, "1h 0m")]
    [InlineData(6540L, "1h 49m")]
    [InlineData(90000L, "25h 0m")]
    public void PlaytimeFormatsSecondsWithoutInventingMissingTotals(long? seconds, string expected)
    {
        Assert.Equal(expected, GameInfoViewModel.FormatPlaytime(seconds));
    }

    [Fact]
    public void PendingGameDistinguishesRemoteLoadingFromMissingLocalDetails()
    {
        var viewModel = CreateViewModel();
        Apply(viewModel, "ApplyCatalogMetadata", Game("sparse", "Sparse game"));

        Assert.Equal("Loading…", viewModel.DeveloperName);
        Assert.Equal("Loading…", viewModel.PublisherName);
        Assert.Equal("Loading…", viewModel.ReleaseDateText);
        Assert.Equal("Unavailable", viewModel.InstalledVersion);
        Assert.Equal("Unavailable", viewModel.LastUpdatedText);
        Assert.Equal(string.Empty, viewModel.TotalPlayTimeText);
    }

    [Fact]
    public void NavigatingToAnotherGameResetsPreviousDateAndBuild()
    {
        var viewModel = CreateViewModel();
        viewModel.LastUpdatedText = "previous date";
        viewModel.TotalPlayTimeText = "9h 0m";
        viewModel.InstalledVersion = "old-build";
        UseDetails(viewModel, new LauncherGameDetails(InstalledVersion: "new-build"));
        Apply(viewModel, "ApplyCatalogMetadata", Game("next", "Next game"));

        Assert.Equal("Unavailable", viewModel.LastUpdatedText);
        Assert.Equal("new-build", viewModel.InstalledVersion);
        Assert.Equal(string.Empty, viewModel.TotalPlayTimeText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinishedStoreRequestReplacesLoadingWithUnavailableForMissingFields(bool hasDetails)
    {
        var viewModel = CreateViewModel();
        viewModel.DeveloperName = "Loading…";
        viewModel.PublisherName = "Loading…";
        viewModel.ReleaseDateText = "Loading…";
        var method = typeof(GameInfoViewModel).GetMethod("ApplyStoreDetails", BindingFlags.NonPublic | BindingFlags.Instance)!;

        method.Invoke(viewModel, [hasDetails ? new StoreProductDetails() : null]);

        Assert.Equal("Unavailable", viewModel.DeveloperName);
        Assert.Equal("Unavailable", viewModel.PublisherName);
        Assert.Equal("Unavailable", viewModel.ReleaseDateText);
    }

    [Fact]
    public void LastUpdatedUsesSavedTimestampInLocalTime()
    {
        var viewModel = CreateViewModel();
        var timestamp = new DateTimeOffset(2026, 1, 12, 15, 30, 0, TimeSpan.Zero);
        Apply(viewModel, "ApplyLocalGameDetails", new LauncherGameDetails(LastUpdatedAt: timestamp));

        Assert.Equal(timestamp.ToLocalTime().ToString("g"), viewModel.LastUpdatedText);
    }

    [Fact]
    public void LastUpdatedRoundTripsAndLegacyStateRemainsUnknown()
    {
        var timestamp = new DateTimeOffset(2026, 1, 12, 15, 30, 0, TimeSpan.Zero);
        var state = new LocalAppState { AppName = "game", LastUpdatedAt = timestamp };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<LocalAppState>(json)!;
        var legacy = System.Text.Json.JsonSerializer.Deserialize<LocalAppState>("{\"app_name\":\"game\"}")!;

        Assert.Contains("last_updated_at", json);
        Assert.Equal(timestamp, restored.LastUpdatedAt);
        Assert.Null(legacy.LastUpdatedAt);
    }

    private static AssetInfos Assets(string appName) => new()
    {
        Windows = new Asset
        {
            AppName = appName,
            BuildVersion = "test-build",
            CatalogItemId = appName,
            Namespace = "test"
        }
    };

    private static GameInfoViewModel CreateViewModel() => new(
        NullLogger<GameInfoViewModel>.Instance, null!, null!, null!, null!);

    private static LauncherGame Game(string appName, string title) => new()
    {
        Id = new GameId("epic", appName),
        AppTitle = title
    };

    // The page only asks the owning provider for local details.
    private static void UseDetails(GameInfoViewModel viewModel, LauncherGameDetails details) =>
        typeof(GameInfoViewModel).GetField("_details", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, new StubDetails(details));

    private sealed class StubDetails(LauncherGameDetails details) : IGameDetailsSource
    {
        public event Action<string, bool>? RunningStateChanged { add { } remove { } }
        public bool IsRunning(string appName) => false;
        public bool AllowsMultipleInstances(string appName) => false;
        public LauncherGameDetails? GetGameDetails(string appName) => details;
        public Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);
        public Task<StoreProductDetails?> GetStoreProductDetails(string appName, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoreProductDetails?>(null);
        public Uri? GetStorePageUri(string appName) => null;
    }

    private static void Apply(GameInfoViewModel viewModel, string method, object argument) =>
        typeof(GameInfoViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, [argument]);
}
