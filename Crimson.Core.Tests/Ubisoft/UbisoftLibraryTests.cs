using System.Text.Json;
using Crimson.Providers;
using Crimson.Providers.Ubisoft;
using Serilog;
using static Crimson.Core.Tests.Ubisoft.UbisoftAuthenticationTests;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftLibraryTests
{
    [Fact]
    public void EntitlementsAreValidatedDeduplicatedAndNeverInventApplicationIds()
    {
        var products = UbisoftLibrary.ParseEntitlements(JsonSerializer.SerializeToElement(new { items = new object[]
        {
            new { productId = 20, type = "game", playable = true, applicationId = "invalid" },
            new { productId = 30, type = "bundle", playable = true },
            new { productId = 20, type = "game", name = "Game", playable = true, applicationId = "61ec338e-d607-4042-a55b-fc009cfc2dc6" },
            new { productId = 40, type = "addon", playable = false },
            new { productId = 50, type = "game", playable = true }
        }}));
        Assert.Equal(new uint[] { 20, 40, 50 }, products.Select(p => p.ProductId));
        Assert.Equal("Game", products[0].Title);
        Assert.Null(products[2].ApplicationId);
    }
    [Fact]
    public async Task ConfiguredGameAppearsInUnifiedLibraryAndRoutesToRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "game.exe");
        await File.WriteAllTextAsync(executable, "fixture");
        try
        {
            var authApi = new UbisoftApi(new HttpClient(new Handler(_ => Task.FromResult(Json(SessionPayload())))));
            var auth = new UbisoftAuthentication(authApi, new MemoryStore());
            await auth.SignIn("email", "password", false);
            var api = new UbisoftApi(new HttpClient(new Handler(_ => Task.FromResult(Json(new[] {
                new { productId = 2688, type = "game", playable = true, applicationId = "61ec338e-d607-4042-a55b-fc009cfc2dc6" }
            })))));
            var config = new UbisoftConfigurationStore(Path.Combine(root, "provider"));
            var runtime = new Runtime();
            var library = new UbisoftLibrary(auth, api, config, runtime, new LoggerConfiguration().CreateLogger());
            var game = Assert.Single(await library.GetLibraryData());
            Assert.Equal("ubisoft:2688", game.AppName);
            Assert.False(game.CanLaunch);
            await config.Save(new(root, [new(2688, "Watch Dogs 2", root, executable)]));
            game = library.GetGameInfo("ubisoft:2688")!;
            Assert.True(game.CanLaunch);
            Assert.Equal("Watch Dogs 2", game.AppTitle);
            Assert.True(await library.LaunchApp(game.AppName));
            Assert.Equal(2688U, runtime.ProductId);
            Assert.False(await library.LaunchApp(game.AppName));
            Assert.Throws<ArgumentException>(() => library.GetGameInfo("epic:2688"));
            await auth.Logout();
            Assert.Empty(library.Entitlements);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Runtime : IUbisoftRuntime
    {
        public uint ProductId;
        public Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftEntitlement game,
            UbisoftInstalledGame installation, UbisoftConfigurationStore configuration, CancellationToken token)
        { ProductId = game.ProductId; return Task.FromResult<IUbisoftRunningGame>(new Running()); }
    }
    private sealed class Running : IUbisoftRunningGame
    {
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _complete.Task;
        public ValueTask DisposeAsync() { _complete.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
