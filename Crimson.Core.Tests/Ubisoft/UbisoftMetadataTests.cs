using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Providers.Ubisoft;
using Serilog;
using static Crimson.Core.Tests.Ubisoft.UbisoftAuthenticationTests;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftMetadataTests
{
    private const string QuotedSpace = "11111111-1111-1111-1111-111111111111";
    private const string LocalizedSpace = "22222222-2222-2222-2222-222222222222";
    private const string TokenSpace = "33333333-3333-3333-3333-333333333333";

    // Mirrors the official client's dump: unindented block markers, root/addons sections,
    // lN name keys resolved through localizations.default, and 32-hex asset hashes.
    private static string[] Catalog() =>
    [
        "version: 2.0",
        "root:",
        "  name: \"Quoted Game\"",
        "  thumb_image: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg",
        "  background_image: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.jpg",
        "  icon_image: cccccccccccccccccccccccccccccccc.ico",
        "  installer:",
        "    game_identifier: Quoted Game",
        "  start_game:",
        "    online:",
        "      executables:",
        "        - path:",
        "            register: HKEY_CURRENT_USER\\SOFTWARE\\Ubisoft\\Quoted Game\\ExecutableName",
        "          working_directory:",
        "            register: HKEY_LOCAL_MACHINE\\SOFTWARE\\Ubisoft\\Launcher\\Installs\\2688\\InstallDir",
        "          shortcut_name: \"Quoted Game\"",
        "        - path:",
        "            relative: bin\\MapEditor.exe",
        "          shortcut_name: \"Quoted Game Map Editor\"",
        "  addons:",
        "  - id: 9001",
        "    is_visible: yes",
        "    thumb_image: dddddddddddddddddddddddddddddddd.jpg",
        "    name: l2",
        "  - id: 9002",
        "    is_visible: no",
        "    name: Bare Addon",
        "  space_id: " + QuotedSpace,
        "localizations:",
        "  default:",
        "    l1: Localized Game",
        "    l2: Quoted Game Expansion",
        "  sv-SE:",
        "    l1: Lokaliserat Spel",
        "version: 2.0",
        "root:",
        "  name: l1",
        "  background_image: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee.jpg",
        "  space_id: " + LocalizedSpace,
        "localizations:",
        "  default:",
        "    l1: Localized Game",
        "version: 2.0",
        "root:",
        "  name: GAMENAME",
        "  installer:",
        "    game_identifier: Identifier Game",
        "  space_id: " + TokenSpace
    ];

    [Fact]
    public void ClientCacheSuppliesNamesArtworkAndAddonOwnership()
    {
        var catalog = UbisoftConfigCatalog.Parse(Catalog());

        Assert.Equal(3, catalog.Count);
        var quoted = catalog.FindGame(QuotedSpace)!;
        Assert.Equal("Quoted Game", quoted.Title);
        Assert.Equal(UbisoftConfigCatalog.AssetBaseUrl + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", quoted.CoverImageUrl);
        Assert.Equal(UbisoftConfigCatalog.AssetBaseUrl + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.jpg", quoted.LandscapeImageUrl);
        Assert.Equal(UbisoftConfigCatalog.AssetBaseUrl + "cccccccccccccccccccccccccccccccc.ico", quoted.IconImageUrl);

        Assert.Equal("Quoted Game Expansion", Assert.Single(catalog.FindGame(QuotedSpace)!.Addons, addon => addon.ProductId == 9001).Title);
        Assert.Equal("Bare Addon", catalog.FindAddon(9002)!.Title);
        Assert.False(catalog.FindAddon(9002)!.IsVisible);
        Assert.Equal(QuotedSpace, catalog.ParentSpaceId(9001));

        Assert.Equal("Localized Game", catalog.FindGame(LocalizedSpace)!.Title);
        Assert.Equal("Identifier Game", catalog.FindGame(TokenSpace)!.Title);
        Assert.Null(catalog.FindGame(TokenSpace)!.CoverImageUrl);
    }

    [Fact]
    public void RetailBlockWithoutImagesBorrowsThemFromASiblingTitle()
    {
        // The client sometimes ships the retail block with placeholder tokens only,
        // while a pre-order block carries the real hashes and no space id.
        var catalog = UbisoftConfigCatalog.Parse([
            "version: 2.0", "root:", "  name: GAMENAME",
            "    game_identifier: Far Cry 4",
            "  background_image: BACKGROUNDIMAGE",
            "  thumb_image: THUMBIMAGE",
            "  space_id: aab6ceb2-2ea5-4d27-9719-cc2cb87395b7",
            "version: 2.0", "root:",
            "  name: \"Far Cry 4 PREORDER\"",
            "  background_image: 856bccf231cdf8341d9aec04411d22f5.jpg",
            "  thumb_image: 5216494f61c947708a013e1bc8ed099e.jpg"
        ]);

        var game = catalog.FindGame("aab6ceb2-2ea5-4d27-9719-cc2cb87395b7")!;
        Assert.Equal("Far Cry 4", game.Title);
        Assert.Equal(UbisoftConfigCatalog.AssetBaseUrl + "5216494f61c947708a013e1bc8ed099e.jpg", game.CoverImageUrl);
        Assert.Equal(UbisoftConfigCatalog.AssetBaseUrl + "856bccf231cdf8341d9aec04411d22f5.jpg", game.LandscapeImageUrl);
    }

    [Fact]
    public void ClientBlocksThatLeadWithShortcutNameStillYieldTheGameExecutable()
    {
        var catalog = UbisoftConfigCatalog.Parse([
            "version: 2.0", "root:", "  name: l1", "  start_game:", "    online:",
            "      executables:", "      - shortcut_name: Tom Clancy's The Division",
            "        path:", "          relative: TheDivision.exe", "        working_directory:",
            @"          register: HKEY_LOCAL_MACHINE\\SOFTWARE\\Ubisoft\\Launcher\\Installs\\568\\InstallDir",
            "  space_id: 6edd234a-abff-4e90-9aab-b9b9c6e49ff7",
            "localizations:", "  default:", "    l1: Tom Clancy's The Division (retail)"
        ]);

        var game = catalog.FindGame("6edd234a-abff-4e90-9aab-b9b9c6e49ff7")!;
        Assert.Equal("Tom Clancy's The Division (retail)", game.Title);
        Assert.Equal("TheDivision.exe", game.Executable!.RelativePath);
        Assert.EndsWith(@"Ubisoft\\Launcher\\Installs\\568\\InstallDir", game.Executable.WorkingDirectoryRegistry);
    }

    [Fact]
    public void ClientCacheNamesTheExecutableTheClientUsesToStartTheGame()
    {
        var executable = UbisoftConfigCatalog.Parse(Catalog()).FindGame(QuotedSpace)!.Executable;

        Assert.NotNull(executable);
        Assert.Equal(@"HKEY_CURRENT_USER\SOFTWARE\Ubisoft\Quoted Game\ExecutableName", executable!.PathRegistry);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Launcher\Installs\2688\InstallDir",
            executable.WorkingDirectoryRegistry);
        // The second entry belongs to the map editor; the game is always the first executable.
        Assert.Null(executable.RelativePath);
    }

    [Fact]
    public async Task InstalledGamesBecomePlayableWithoutAnyManualRegistration()
    {
        var root = NewRoot();
        try
        {
            var catalog = ServicedCatalog();
            var config = new UbisoftConfigurationStore(Path.Combine(root, "provider"));
            await config.Save(new("", []));
            var auth = new UbisoftAuthentication(Api(_ => Json(SessionPayload())), new MemoryStore());
            await auth.SignIn("email", "password", false);
            var metadata = new UbisoftMetadata(Api(_ => Json(new { })), Path.Combine(root, "provider"), catalog);

            // An imported installation is Crimson's own record - no client detection involved.
            await config.Save(new("", [new(2688, "Quoted Game", @"C:\Games\Quoted", @"C:\Games\Quoted\Game.exe")]));
            var installed = new UbisoftLibrary(auth, Api(Entitlements), metadata, config, new NoRuntime(), Log());
            var game = (await installed.GetLibraryData()).Single(entry => entry.AppName == "ubisoft:2688");
            Assert.True(game.CanLaunch);
            Assert.Equal(InstallState.Installed, game.LocalAppState!.InstallStatus);

            await config.Save(new("", []));
            var absent = new UbisoftLibrary(auth, Api(Entitlements), metadata, config, new NoRuntime(), Log());
            var uninstalled = (await absent.GetLibraryData()).Single(entry => entry.AppName == "ubisoft:2688");
            Assert.False(uninstalled.CanLaunch);
            Assert.Null(uninstalled.LocalAppState);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OwnedAddonsBecomeChildrenOfTheirGameWithSharedDisplayData()
    {
        var root = NewRoot();
        try
        {
            var catalog = ServicedCatalog();
            var metadata = new UbisoftMetadata(Api(_ => Json(new { })), Path.Combine(root, "provider"), catalog);
            var config = new UbisoftConfigurationStore(Path.Combine(root, "provider"));
            await config.Save(new("", []));
            var auth = new UbisoftAuthentication(Api(_ => Json(SessionPayload())), new MemoryStore());
            await auth.SignIn("email", "password", false);
            var library = new UbisoftLibrary(auth, Api(Entitlements), metadata, config, new NoRuntime(), Log());

            var games = (await library.GetLibraryData()).ToList();

            var game = games.Single(entry => entry.AppName == "ubisoft:2688");
            Assert.Equal("Quoted Game", game.AppTitle);
            Assert.StartsWith(UbisoftConfigCatalog.AssetBaseUrl, game.CoverImageUrl);
            Assert.False(game.IsDownloadableContent);

            var addon = games.Single(entry => entry.AppName == "ubisoft:9001");
            Assert.Equal("Quoted Game Expansion", addon.AppTitle);
            Assert.True(addon.IsDownloadableContent);
            Assert.Equal("ubisoft:2688", library.GetBaseGameAppName(addon.AppName));
            Assert.Equal(addon.AppName, Assert.Single(library.GetDlcsForGame(game.AppName)).AppName);
            Assert.Empty(library.GetDlcsForGame(addon.AppName));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnnamedGamesUseTheApplicationsEndpointOnceAndAreCached()
    {
        var root = NewRoot();
        try
        {
            var catalog = ServicedCatalog();
            var requests = 0;
            UbisoftApi ApiWithCounter()
            {
                var api = Api(request =>
                {
                    if (!request.RequestUri!.AbsolutePath.Contains("applications")) return Json(new { });
                    Interlocked.Increment(ref requests);
                    return Json(new
                    {
                        applications = new[]
                        {
                            new { spaceId = "44444444-4444-4444-4444-444444444444", name = "Remote Named Game" }
                        }
                    });
                });
                return api;
            }

            async Task<IReadOnlyList<LauncherGame>> Load()
            {
                var metadata = new UbisoftMetadata(ApiWithCounter(), Path.Combine(root, "provider"), catalog);
                var config = new UbisoftConfigurationStore(Path.Combine(root, "provider"));
                await config.Save(new("", []));
                var auth = new UbisoftAuthentication(Api(_ => Json(SessionPayload())), new MemoryStore());
                await auth.SignIn("email", "password", false);
                var library = new UbisoftLibrary(auth, Api(UnknownGame), metadata, config, new NoRuntime(), Log());
                return (await library.GetLibraryData()).ToList();
            }

            var first = await Load();
            Assert.Equal("Remote Named Game", Assert.Single(first).AppTitle);
            Assert.Equal(1, requests);

            var second = await Load();
            Assert.Equal("Remote Named Game", Assert.Single(second).AppTitle);
            Assert.Equal(1, requests);
            Assert.True(File.Exists(Path.Combine(root, "provider", "metadata.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/abc.jpg", true)]
    [InlineData("https://ubistatic3-a.akamaihd.net/uplay_launcher_3_0/assets/abc.jpg", true)]
    [InlineData("http://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/abc.jpg", false)]
    [InlineData("https://example.test/artwork.jpg", false)]
    public void ArtworkPolicyOnlyAllowsUbisoftContentHosts(string url, bool allowed)
    {
        var policy = new UbisoftContentPolicy();
        if (allowed) Assert.Equal(url, policy.RequireArtworkUri(url).ToString());
        else Assert.Throws<InvalidOperationException>(() => policy.RequireArtworkUri(url));
        Assert.Equal("ubisoft", policy.ProviderId);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-ubisoft-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>The same YAML text, delivered the way Ubisoft's service delivers it (catalog stream).</summary>
    private static Func<UbisoftSession, CancellationToken, Task<byte[]>> ServicedCatalog()
    {
        var newline = ((char)13).ToString() + (char)10;
        var text = string.Join(newline, Catalog()) + newline;
        var bytes = System.Text.Encoding.Latin1.GetBytes(text);
        return (_, _) => Task.FromResult(bytes);
    }

    private static ILogger Log() => new LoggerConfiguration().CreateLogger();

    private static UbisoftApi Api(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)));

    private static HttpResponseMessage Json(object payload) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload))
    };

    // The ownership endpoint answers every GET, so the test handler inspects the path first.
    private static HttpResponseMessage Entitlements(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("entitlements")
            ? Json(new
            {
                entitlements = new object[]
                {
                    new { productId = 2688, type = "game", playable = true, spaceId = QuotedSpace,
                        applicationId = "61ec338e-d607-4042-a55b-fc009cfc2dc6" },
                    new { productId = 9001, type = "addon", playable = true, spaceId = "" }
                }
            })
            : Json(new { });

    private static HttpResponseMessage UnknownGame(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("entitlements")
            ? Json(new
            {
                entitlements = new object[]
                {
                    new { productId = 5555, type = "game", playable = true,
                        spaceId = "44444444-4444-4444-4444-444444444444",
                        applicationId = "61ec338e-d607-4042-a55b-fc009cfc2dc6" }
                }
            })
            : Json(new { });

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }

    private sealed class MemoryStore : IUbisoftSessionStore
    {
        private UbisoftSession? _session;
        public Task<UbisoftSession?> Load(CancellationToken token) => Task.FromResult(_session);
        public Task Save(UbisoftSession session, CancellationToken token) { _session = session; return Task.CompletedTask; }
        public Task Delete() { _session = null; return Task.CompletedTask; }
    }

    private sealed class NoRuntime : IUbisoftRuntime
    {
        public Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftEntitlement game,
            UbisoftInstalledGame installation, UbisoftConfigurationStore configuration, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
