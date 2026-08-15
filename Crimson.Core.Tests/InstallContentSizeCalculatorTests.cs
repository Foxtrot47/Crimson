using Crimson.Core;

namespace Crimson.Tests;

public sealed class InstallContentSizeCalculatorTests
{
    [Fact]
    public void Calculate_IncludesOnlySelectedDlcs()
    {
        var sizes = new Dictionary<string, InstallContentSize>
        {
            ["base"] = new(100, 200),
            ["dlc-a"] = new(10, 20),
            ["dlc-b"] = new(30, 40)
        };

        var allSelected = InstallContentSizeCalculator.Calculate(
            "base",
            sizes,
            ["dlc-a", "dlc-b"]);
        var oneUnchecked = InstallContentSizeCalculator.Calculate(
            "base",
            sizes,
            ["dlc-b"]);
        var noneSelected = InstallContentSizeCalculator.Calculate(
            "base",
            sizes,
            []);

        Assert.Equal(new InstallContentSize(140, 260), allSelected);
        Assert.Equal(new InstallContentSize(130, 240), oneUnchecked);
        Assert.Equal(new InstallContentSize(100, 200), noneSelected);
    }

    [Fact]
    public void Calculate_DeduplicatesSelectedDlcIds()
    {
        var sizes = new Dictionary<string, InstallContentSize>
        {
            ["base"] = new(100, 200),
            ["dlc"] = new(10, 20)
        };

        var total = InstallContentSizeCalculator.Calculate("base", sizes, ["dlc", "dlc"]);

        Assert.Equal(new InstallContentSize(110, 220), total);
    }
}
