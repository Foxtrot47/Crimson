using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Crimson.Models;
using Crimson.Providers.Ubisoft;
using Crimson.Utils;

namespace Crimson.Core.Tests;

public class JsonContextTests
{
    [Fact]
    public void InstallManagerStateSurvivesBigIntegerRoundTrip()
    {
        var state = new InstallManagerState
        {
            CurrentInstall = new InstallItem("app", ActionType.Install, "C:/games"),
            CompletedChunks = [BigInteger.Parse("184467440737095516160")]
        };

        var json = JsonSerializer.Serialize(state, EpicInstallJsonContext.Default.InstallManagerState);
        var restored = JsonSerializer.Deserialize(json, EpicInstallJsonContext.Default.InstallManagerState);

        Assert.NotNull(restored);
        Assert.Equal("app", restored.CurrentInstall.AppName);
        Assert.Equal(BigInteger.Parse("184467440737095516160"), Assert.Single(restored.CompletedChunks));
    }

    [Fact]
    public void InstallProgressSurvivesSliceIndexRoundTrip()
    {
        var progress = new UbisoftInstallProgress { ProductId = 7, ManifestId = "build" };
        progress.Partial["file.bin"] = [0, 2];

        var json = JsonSerializer.Serialize(progress, CrimsonJsonContext.Default.UbisoftInstallProgress);
        var restored = JsonSerializer.Deserialize(json, CrimsonJsonContext.Default.UbisoftInstallProgress);

        Assert.NotNull(restored);
        Assert.Equal(7u, restored.ProductId);
        Assert.Equal([0, 2], restored.Partial["file.bin"]);
    }
}
