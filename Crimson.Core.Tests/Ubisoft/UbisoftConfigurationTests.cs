using Crimson.Providers.Ubisoft;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftConfigurationTests
{
    [Fact]
    public async Task ConfigurationRoundTripsAndRejectsExecutableOutsideInstallRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "game.exe");
        await File.WriteAllTextAsync(exe, "fixture");
        try
        {
            var store = new UbisoftConfigurationStore(Path.Combine(root, "provider"));
            var game = new UbisoftInstalledGame(2688, "Title", root, exe);
            await store.Save(new("", [game]));
            await store.Load();
            Assert.Equal(game, Assert.Single(store.Current.Games));
            Assert.Throws<ArgumentException>(() => UbisoftConfigurationStore.ValidateGame(game with { InstallDirectory = Path.Combine(root, "other") }));
            var saves = store.Saves("../../other/account", 2688);
            Assert.StartsWith(store.ProviderRoot, saves);
            Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), saves);
            await Assert.ThrowsAsync<ArgumentException>(() => store.Save(new("", [game, game])));
        }
        finally { Directory.Delete(root, true); }
    }
}
