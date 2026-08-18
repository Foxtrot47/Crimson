using System.Security.Cryptography;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Xunit;

namespace Crimson.Tests;

public sealed class RocketLeagueManifestBaselineTests
{
    [LocalRocketLeagueManifestFact]
    public async Task CachedManifestMatchesRecordedBaseline()
    {
        var baselinePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "LiveGames",
            "rocket-league-baseline.json");
        using var baselineDocument = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
        var baseline = baselineDocument.RootElement;
        var bytes = await File.ReadAllBytesAsync(LocalRocketLeagueManifestFactAttribute.ManifestPath!);
        var manifest = Manifest.ReadAll(bytes);
        var actual = new
        {
            manifestSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            manifestFileSize = bytes.LongLength,
            appName = manifest.ManifestMeta.AppName,
            buildVersion = manifest.ManifestMeta.BuildVersion,
            launchExecutable = manifest.ManifestMeta.LaunchExe.Value,
            fileCount = manifest.FileManifestList.Elements.Count,
            chunkCount = manifest.CDL.Elements.Count,
            downloadSize = manifest.CDL.Elements.Sum(chunk => chunk.FileSize),
            installSize = manifest.FileManifestList.Elements.Sum(file => file.FileSize)
        };

        Assert.Equal(baseline.GetProperty("manifestSha256").GetString(), actual.manifestSha256);
        Assert.Equal(baseline.GetProperty("manifestFileSize").GetInt64(), actual.manifestFileSize);
        Assert.Equal(baseline.GetProperty("manifestAppName").GetString(), actual.appName);
        Assert.Equal(baseline.GetProperty("buildVersion").GetString(), actual.buildVersion);
        Assert.Equal(baseline.GetProperty("launchExecutable").GetString(), actual.launchExecutable);
        Assert.True(
            baseline.GetProperty("fileCount").GetInt32() == actual.fileCount &&
            baseline.GetProperty("chunkCount").GetInt32() == actual.chunkCount &&
            baseline.GetProperty("downloadSize").GetInt64() == actual.downloadSize &&
            baseline.GetProperty("installSize").GetInt64() == actual.installSize,
            JsonSerializer.Serialize(actual));
    }

    [LocalRocketLeagueUpdateFact]
    public void CandidateManifestProducesNonEmptyUpdatePlan()
    {
        var baseline = Manifest.ReadAll(
            File.ReadAllBytes(LocalRocketLeagueManifestFactAttribute.ManifestPath!));
        var candidate = Manifest.ReadAll(
            File.ReadAllBytes(LocalRocketLeagueUpdateFactAttribute.CandidateManifestPath!));

        Assert.Equal(baseline.ManifestMeta.AppName, candidate.ManifestMeta.AppName);
        Assert.NotEqual(baseline.ManifestMeta.BuildVersion, candidate.ManifestMeta.BuildVersion);
        var plan = ManifestUpdatePlanner.Create(
            baseline.FileManifestList.Elements,
            candidate.FileManifestList.Elements);
        Assert.True(
            plan.ChangedFiles.Count + plan.AddedFiles.Count + plan.RemovedFiles.Count > 0,
            "Rocket League build changed without any planned file changes.");
    }
}

public sealed class LocalRocketLeagueManifestFactAttribute : FactAttribute
{
    private const string BaselineDigest =
        "a805c5fd5c912c309c3a382b934226913bccf439e04848990afd429783db4674";

    public static string? ManifestPath { get; } = FindManifest();

    public LocalRocketLeagueManifestFactAttribute()
    {
        if (ManifestPath is null)
            Skip = "Set CRIMSON_ROCKET_LEAGUE_MANIFEST to the recorded Rocket League baseline manifest.";
    }

    private static string? FindManifest()
    {
        var configured = Environment.GetEnvironmentVariable("CRIMSON_ROCKET_LEAGUE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cached = Path.Combine(localAppData, "Crimson", "manifests", $"{BaselineDigest}.manifest");
        return File.Exists(cached) ? cached : null;
    }
}

public sealed class LocalRocketLeagueUpdateFactAttribute : FactAttribute
{
    public static string? CandidateManifestPath { get; } = FindCandidateManifest();

    public LocalRocketLeagueUpdateFactAttribute()
    {
        if (LocalRocketLeagueManifestFactAttribute.ManifestPath is null || CandidateManifestPath is null)
        {
            Skip = "Set CRIMSON_ROCKET_LEAGUE_CANDIDATE_MANIFEST when Epic publishes a newer Rocket League manifest.";
        }
    }

    private static string? FindCandidateManifest()
    {
        var path = Environment.GetEnvironmentVariable("CRIMSON_ROCKET_LEAGUE_CANDIDATE_MANIFEST");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }
}
