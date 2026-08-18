using System.Text.Json;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class InstallOperationPlannerTests
{
    [Fact]
    public void UpdatePlan_IsImmutableSerializableAndRebuiltFromVerifiedProgress()
    {
        var request = Request(ActionType.Update) with
        {
            InstalledManifest = Identity("1"),
            InstalledFiles =
            [
                File("Data/unchanged.bin", 10, "01"),
                File("Data/changed.bin", 20, "02"),
                File("Data/removed.bin", 30, "03")
            ],
            TargetFiles =
            [
                File("Data/unchanged.bin", 10, "01"),
                File("Data/changed.bin", 25, "04"),
                File("Data/added.bin", 40, "05")
            ],
            VerifiedStagedFiles = ["Data/changed.bin"]
        };

        var result = InstallOperationPlanner.Create(request);

        var plan = Assert.IsType<InstallOperationPlan>(result.Plan);
        Assert.Equal(["Data/added.bin"], plan.PendingStageFiles.Select(file => file.Path));
        Assert.Equal(["Data/changed.bin"], plan.VerifiedStageFiles.Select(file => file.Path));
        Assert.Equal(["Data/removed.bin"], plan.RemoveFiles.ToArray());
        Assert.Equal(40, plan.RequiredStagingBytes);

        var json = JsonSerializer.Serialize(plan);
        var restored = JsonSerializer.Deserialize<InstallOperationPlan>(json);
        Assert.Equal(json, JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void RepairPlan_StagesOnlyInvalidManifestFiles()
    {
        var result = InstallOperationPlanner.Create(Request(ActionType.Repair) with
        {
            TargetFiles =
            [
                File("Data/good.bin", 10, "01"),
                File("Data/broken.bin", 20, "02")
            ],
            InvalidFiles = ["Data/broken.bin"]
        });

        var plan = Assert.IsType<InstallOperationPlan>(result.Plan);
        Assert.Equal(["Data/broken.bin"], plan.PendingStageFiles.Select(file => file.Path));
        Assert.Equal(20, plan.RequiredStagingBytes);
    }

    [Fact]
    public void UninstallPlan_ContainsOnlyManifestOwnedPaths()
    {
        var result = InstallOperationPlanner.Create(Request(ActionType.Uninstall) with
        {
            InstalledFiles =
            [
                File("Data/owned.bin", 10, "01"),
                File("Engine/owned.bin", 20, "02")
            ]
        });

        var plan = Assert.IsType<InstallOperationPlan>(result.Plan);
        Assert.Equal(["Data/owned.bin", "Engine/owned.bin"], plan.RemoveFiles.ToArray());
    }

    [Fact]
    public void MovePlan_ReturnsTypedCrossVolumeFailure()
    {
        var result = InstallOperationPlanner.Create(Request(ActionType.Move) with
        {
            MoveDestination = Path.Combine(Path.GetTempPath(), "moved-game"),
            Source = Probe("source"),
            Destination = Probe("destination")
        });

        Assert.Equal(InstallPlanningFailure.CrossVolumeMove, result.Failure);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void DownloadPlan_RejectsInsufficientCapacityBeforeWorkBegins()
    {
        var result = InstallOperationPlanner.Create(Request(ActionType.Install) with
        {
            TargetFiles = [File("Data/game.bin", 100, "01")],
            Destination = Probe("volume", availableBytes: 99)
        });

        Assert.Equal(InstallPlanningFailure.InsufficientSpace, result.Failure);
    }

    [Fact]
    public void Plan_RejectsProgressOutsideManifestIdentity()
    {
        var result = InstallOperationPlanner.Create(Request(ActionType.Install) with
        {
            TargetFiles = [File("Data/game.bin", 100, "01")],
            VerifiedStagedFiles = ["Data/other.bin"]
        });

        Assert.Equal(InstallPlanningFailure.InvalidRequest, result.Failure);
    }

    private static InstallPlanningRequest Request(ActionType action) => new(
        "operation-1",
        "game",
        action,
        Path.Combine(Path.GetTempPath(), "game"),
        Identity("2"),
        [],
        Probe("volume"),
        MoveDestination: action == ActionType.Move
            ? Path.Combine(Path.GetTempPath(), "moved-game")
            : null,
        Source: action == ActionType.Move ? Probe("volume") : null);

    private static InstallManifestIdentity Identity(string version) =>
        new(version, $"sha1-{version}", $"sha256-{version}");

    private static InstallPlanFile File(string path, long size, string sha1) =>
        new(path, size, sha1);

    private static InstallFileSystemProbeResult Probe(
        string volume,
        long availableBytes = long.MaxValue) =>
        new(
            true,
            VolumeIdentity: volume,
            AvailableBytes: availableBytes,
            TotalBytes: long.MaxValue,
            AtomicRenameSupported: true,
            Location: InstallFileSystemLocation.Local);
}
