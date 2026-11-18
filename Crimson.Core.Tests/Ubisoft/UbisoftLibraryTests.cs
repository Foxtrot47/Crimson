using Crimson.Providers;
using Crimson.Models;
using Crimson.Providers.Ubisoft;
using Crimson.Providers.Ubisoft.Protocol;
using Serilog;
using static Crimson.Core.Tests.Ubisoft.UbisoftAuthenticationTests;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftLibraryTests
{
    private const string Manifest = "42616E0B6C54EAE6DCD49BA16CECFA004C0010A9";

    internal const string GameConfig = """
        version: 2.0
        root:
          name: l1
          thumb_image: 0f946fb03f8bdf913969d1c22df1625e.jpg
          background_image: 160159cf6a4487f5605078896c68e294.jpg
          start_game:
            online:
              executables:
              - path:
                  relative: '\bin\Game.exe'
                working_directory:
                  relative: bin
          app_id: 35c5c607-2717-47d9-9323-7df47c6e1c4d
          cloud_saves: yes
          addons:
          - id: 9
            name: l2
            thumb_image: f3fa5bffd6623a5e8ddb3ebb89415e45.jpg
        localizations:
          default:
            l1: Game
            l2: Addon
          fr-FR:
            l1: Jeu
        """;

    private const string ThirdPartyConfig = """
        version: 2.0
        root:
          name: Partner Game
          third_party_platform:
            name: steam
        """;

    internal static UbisoftOwnedProduct Product(uint id, uint type = UbisoftOwnedProduct.TypeGame,
        uint state = UbisoftOwnedProduct.StateReleased, bool owned = true, string? manifest = Manifest,
        string? config = null, uint partner = 0, uint uplayId = 0, uint[]? associations = null) =>
        new(id, owned, type, state, uplayId, 3452, config, manifest, 0, [], associations ?? [], null, null, 0, 0, 0, partner, false, 0);

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "crimson-ubisoft-" + Guid.NewGuid().ToString("N"));
        public UbisoftAuthentication Auth = null!;
        public UbisoftOwnershipCache Ownership = null!;
        public UbisoftInstalledStore Installed = null!;
        public UbisoftLibrary Library = null!;
        public readonly Runtime Runtime = new();
        public int Fetches;
        public Func<IReadOnlyList<UbisoftOwnedProduct>> Products = () => [];
        public string Provider => Path.Combine(Root, "provider");

        public static async Task<Fixture> Create(string locale = "en-US", Func<string, string?>? registry = null)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            fixture.Auth = new UbisoftAuthentication(
                new UbisoftApi(new HttpClient(new Handler(_ => Task.FromResult(Json(SessionPayload()))))), new MemoryStore());
            await fixture.Auth.SignIn("email", "password", false);
            fixture.Reset(locale, registry);
            return fixture;
        }

        /// <summary>A fresh library over the same folders, as after an application restart.</summary>
        public void Reset(string locale = "en-US", Func<string, string?>? registry = null)
        {
            var metadata = new UbisoftMetadata(() => locale);
            Ownership = new UbisoftOwnershipCache(Provider, metadata, Auth, (_, _) =>
            {
                Interlocked.Increment(ref Fetches);
                return Task.FromResult(Products());
            });
            Installed = new UbisoftInstalledStore(Provider);
            Library = new UbisoftLibrary(Auth, Ownership, metadata, Installed, Runtime, new LoggerConfiguration().CreateLogger(),
                readRegistry: registry ?? (_ => null));
        }

        public async ValueTask DisposeAsync()
        {
            await Auth.Logout();
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OwnershipCacheRoundTripsProductsWithoutSecrets()
    {
        // Raw InitializeRsp: success, owned_games{owned_game{product_id, cd_key, ..., configuration, latest_manifest, encryption_key}}.
        var game = Message(Integer(1, 2688), Text(2, "CDKEY-SECRET-1234"), Integer(5, 3452), Integer(8, 0), Integer(13, 3),
            Integer(16, 3962), Integer(16, 3672), Text(17, GameConfig), Integer(19, 2688), Integer(20, 1), Text(21, Manifest),
            Text(24, "ENCRYPTION-SECRET"), Integer(25, 7), Bytes(26, Message(Integer(1, 7), Text(2, "Live"))),
            Text(31, "35c5c607-2717-47d9-9323-7df47c6e1c4d"));
        var raw = Message(Integer(1, 1), Bytes(2, Bytes(1, game)));
        var products = UbisoftOwnershipService.ParseProducts(UbisoftOwnershipService.Sanitize(raw));

        await using var fixture = await Fixture.Create();
        fixture.Products = () => products;
        var session = fixture.Auth.Session!;
        await fixture.Ownership.RefreshAsync(session);

        var file = await File.ReadAllTextAsync(Path.Combine(fixture.Provider, "ownership.json"));
        Assert.DoesNotContain("CDKEY-SECRET", file);
        Assert.DoesNotContain("ENCRYPTION-SECRET", file);

        fixture.Reset();
        var cached = Assert.Single(await fixture.Ownership.LoadAsync(session.UserId));
        var original = Assert.Single(products);
        Assert.Equal(original.ProductId, cached.ProductId);
        Assert.Equal(original.LatestManifest, cached.LatestManifest);
        Assert.Equal(original.Configuration, cached.Configuration);
        Assert.Equal(original.DownloadId, cached.DownloadId);
        Assert.Equal(original.Associations, cached.Associations);
        Assert.Equal(original.AppId, cached.AppId);
        Assert.Equal("Live", Assert.Single(cached.Branches).Name);
        Assert.Empty(await fixture.Ownership.LoadAsync("someone-else"));
        Assert.Equal(1, fixture.Fetches);
    }

    [Fact]
    public async Task ProjectionShowsOwnedGamesAndAddonsAndGatesInstallability()
    {
        await using var fixture = await Fixture.Create();
        fixture.Products = () =>
        [
            Product(1, config: GameConfig),
            Product(2, type: 5),
            Product(3, type: 11),
            Product(4, owned: false),
            Product(5, state: UbisoftOwnedProduct.StateExpired),
            Product(6, manifest: null),
            Product(7, config: ThirdPartyConfig),
            Product(8, partner: 1), // licence redeemed through Epic Games; Ubisoft still serves the build
            Product(9, type: UbisoftOwnedProduct.TypeAddon),
            Product(10, state: UbisoftOwnedProduct.StateDownloadable),
        ];
        var games = (await fixture.Library.GetLibraryData()).ToDictionary(game => game.AppName);

        Assert.Equal(new[] { "ubisoft:1", "ubisoft:10", "ubisoft:5", "ubisoft:6", "ubisoft:7", "ubisoft:8", "ubisoft:9" },
            games.Keys.Order(StringComparer.Ordinal));
        Assert.True(games["ubisoft:1"].CanInstall);
        Assert.True(games["ubisoft:10"].CanInstall);
        Assert.False(games["ubisoft:5"].CanInstall);
        Assert.False(games["ubisoft:6"].CanInstall);
        Assert.False(games["ubisoft:7"].CanInstall);
        Assert.True(games["ubisoft:8"].CanInstall);

        var addon = games["ubisoft:9"];
        Assert.True(addon.IsDownloadableContent);
        Assert.Equal("Addon", addon.AppTitle);
        Assert.Equal("ubisoft:1", fixture.Library.GetBaseGameAppName("ubisoft:9"));
        Assert.Equal("ubisoft:9", Assert.Single(fixture.Library.GetDlcsForGame("ubisoft:1")).AppName);
        Assert.Throws<ArgumentException>(() => fixture.Library.GetGameInfo("epic:1"));
    }

    [Fact]
    public async Task TitlesAndArtworkFollowTheUiLanguage()
    {
        await using var fixture = await Fixture.Create("fr-FR");
        fixture.Products = () => [Product(1, config: GameConfig), Product(12)];
        var games = (await fixture.Library.GetLibraryData()).ToDictionary(game => game.AppName);

        Assert.Equal("Jeu", games["ubisoft:1"].AppTitle);
        Assert.Equal(UbisoftProductConfig.AssetUrl("0f946fb03f8bdf913969d1c22df1625e.jpg"), games["ubisoft:1"].CoverImageUrl);
        Assert.Equal(UbisoftProductConfig.AssetUrl("160159cf6a4487f5605078896c68e294.jpg"), games["ubisoft:1"].LandscapeImageUrl);
        Assert.Equal(UbisoftMetadata.Placeholder(12), games["ubisoft:12"].AppTitle);
        Assert.Equal("Jeu", fixture.Ownership.GetTitle(1));
    }

    [Fact]
    public async Task InstalledBuildOlderThanTheLatestManifestNeedsAnUpdate()
    {
        await using var fixture = await Fixture.Create();
        fixture.Products = () => [Product(1, config: GameConfig), Product(2, config: GameConfig)];
        await fixture.Installed.SaveAsync(new UbisoftInstalledGame { ProductId = 1, InstallPath = fixture.Root, ManifestId = "OLD" });
        await fixture.Installed.SaveAsync(new UbisoftInstalledGame { ProductId = 2, InstallPath = fixture.Root, ManifestId = Manifest.ToLowerInvariant() });
        var games = (await fixture.Library.GetLibraryData()).ToDictionary(game => game.AppName);

        Assert.Equal(InstallState.NeedUpdate, games["ubisoft:1"].LocalAppState!.InstallStatus);
        Assert.Equal(InstallState.Installed, games["ubisoft:2"].LocalAppState!.InstallStatus);

        LauncherGame? updated = null;
        fixture.Library.GameStatusUpdated += game => updated = game;
        await fixture.Installed.SaveAsync(new UbisoftInstalledGame { ProductId = 1, InstallPath = fixture.Root, ManifestId = Manifest });
        Assert.Equal(InstallState.Installed, updated?.LocalAppState?.InstallStatus);
    }

    [Fact]
    public async Task ColdStartShowsTheCachedLibraryAndObsoleteCatalogFilesAreRemoved()
    {
        await using var fixture = await Fixture.Create();
        fixture.Products = () => [Product(1, config: GameConfig)];
        await fixture.Library.GetLibraryData();
        Directory.CreateDirectory(fixture.Provider);
        foreach (var name in new[] { "catalog.bin", "manifests.json", "metadata.json" })
            await File.WriteAllTextAsync(Path.Combine(fixture.Provider, name), "obsolete");

        fixture.Reset();
        var refreshed = new TaskCompletionSource<IReadOnlyList<UbisoftOwnedProduct>>();
        fixture.Products = () => refreshed.Task.GetAwaiter().GetResult();
        var cached = Assert.Single(await fixture.Library.GetLibraryData());
        Assert.Equal("ubisoft:1", cached.AppName);
        Assert.Equal("Game", cached.AppTitle);
        foreach (var name in new[] { "catalog.bin", "manifests.json", "metadata.json" })
            Assert.False(File.Exists(Path.Combine(fixture.Provider, name)));
        refreshed.SetResult([Product(1, config: GameConfig)]);
    }

    [Fact]
    public async Task ARenewedSessionKeepsTheLibraryLoadedForTheSameAccount()
    {
        await using var fixture = await Fixture.Create();
        var released = new TaskCompletionSource<IReadOnlyList<UbisoftOwnedProduct>>();
        fixture.Products = () => released.Task.GetAwaiter().GetResult();
        var loading = Task.Run(() => fixture.Library.GetLibraryData());
        SpinWait.SpinUntil(() => Volatile.Read(ref fixture.Fetches) == 1, TimeSpan.FromSeconds(10));

        // The launch extension replaces the session object while the list is still loading.
        await fixture.Auth.SignIn("email", "password", false);
        released.SetResult([Product(1, config: GameConfig)]);

        Assert.Equal("ubisoft:1", Assert.Single(await loading).AppName);
    }

    [Fact]
    public async Task LaunchStartsTheConfiguredExecutableWithOwnedAddons()
    {
        await using var fixture = await Fixture.Create();
        var install = Path.Combine(fixture.Root, "Game");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        await File.WriteAllTextAsync(Path.Combine(install, "bin", "Game.exe"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(install, "bin", "uplay_r1_loader64.dll"), "fixture");
        fixture.Products = () => [Product(1, config: GameConfig), Product(9, type: UbisoftOwnedProduct.TypeAddon)];
        await fixture.Installed.SaveAsync(new UbisoftInstalledGame
        {
            ProductId = 1, InstallPath = install, ManifestId = Manifest, Languages = ["fr-FR"]
        });
        await fixture.Library.GetLibraryData();

        Assert.True(await fixture.Library.LaunchApp("ubisoft:1"), fixture.Library.LastLaunchError);
        var plan = fixture.Runtime.Plan!;
        Assert.Equal(Path.Combine(install, "bin", "Game.exe"), plan.Executable);
        Assert.Equal(Path.Combine(install, "bin"), plan.WorkingDirectory);
        Assert.Equal("35c5c607-2717-47d9-9323-7df47c6e1c4d", plan.ApplicationId);
        Assert.Equal("fr-FR", plan.Language);
        Assert.Equal(new uint[] { 9 }, plan.AddonIds);
        Assert.True(fixture.Library.IsRunning("ubisoft:1"));
        Assert.Equal(InstallState.Playing, fixture.Library.GetGameInfo("ubisoft:1")!.LocalAppState!.InstallStatus);
        Assert.False(await fixture.Library.LaunchApp("ubisoft:1"));
    }

    [Fact]
    public async Task AnInstallNeedingRepairCannotLaunch()
    {
        await using var fixture = await Fixture.Create();
        var install = Path.Combine(fixture.Root, "Broken");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        await File.WriteAllTextAsync(Path.Combine(install, "bin", "Game.exe"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(install, "bin", "uplay_r1_loader64.dll"), "fixture");
        fixture.Products = () => [Product(1, config: GameConfig)];
        await fixture.Installed.SaveAsync(new UbisoftInstalledGame
        {
            ProductId = 1, InstallPath = install, ManifestId = Manifest, NeedsRepair = true
        });
        await fixture.Library.GetLibraryData();

        var game = fixture.Library.GetGameInfo("ubisoft:1")!;
        Assert.False(game.CanLaunch);
        Assert.Equal(InstallState.Broken, game.LocalAppState!.InstallStatus);
        Assert.False(await fixture.Library.LaunchApp("ubisoft:1"));
        Assert.Null(fixture.Runtime.Plan);
    }

    [Fact]
    public async Task GameWithoutALoaderIsNotStarted()
    {
        await using var fixture = await Fixture.Create();
        var install = Path.Combine(fixture.Root, "Legacy");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        await File.WriteAllTextAsync(Path.Combine(install, "bin", "Game.exe"), "fixture");
        fixture.Products = () => [Product(1, config: GameConfig)];
        await fixture.Installed.SaveAsync(new UbisoftInstalledGame { ProductId = 1, InstallPath = install, ManifestId = Manifest });
        await fixture.Library.GetLibraryData();

        Assert.False(await fixture.Library.LaunchApp("ubisoft:1"));
        Assert.Null(fixture.Runtime.Plan);
    }

    internal sealed class Runtime : IUbisoftRuntime
    {
        public UbisoftLaunchPlan? Plan;
        public Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftLaunchPlan plan, CancellationToken token)
        {
            Plan = plan;
            return Task.FromResult<IUbisoftRunningGame>(new Running());
        }
    }

    private sealed class Running : IUbisoftRunningGame
    {
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _complete.Task;
        public ValueTask DisposeAsync() { _complete.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
