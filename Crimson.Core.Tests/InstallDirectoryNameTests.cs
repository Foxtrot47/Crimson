using Crimson.Core;

namespace Crimson.Tests;

public sealed class InstallDirectoryNameTests
{
    [Theory]
    [InlineData("Honkai: Star Rail", "Honkai_ Star Rail")]
    [InlineData("New Game", "New Game")]
    [InlineData("ファイナルファンタジー", "ファイナルファンタジー")]
    [InlineData("Game<>:\"/\\|?*", "Game_________")]
    [InlineData("Game\0\u0001\t\r\n", "Game_____")]
    [InlineData("Game... ", "Game")]
    [InlineData("", "Game")]
    [InlineData("...", "Game")]
    [InlineData("..", "Game")]
    [InlineData("   ", "Game")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("PRN", "_PRN")]
    [InlineData("AUX", "_AUX")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("com9.exe", "_com9.exe")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("COM¹", "_COM¹")]
    [InlineData("LPT²", "_LPT²")]
    [InlineData("COM10", "COM10")]
    [InlineData("Conan Exiles", "Conan Exiles")]
    [InlineData("../Elsewhere", ".._Elsewhere")]
    [InlineData("C:\\Elsewhere", "C__Elsewhere")]
    public void GeneratedNamesAreSafeSingleWindowsDirectoryComponents(string title, string expected)
    {
        var name = InstallPathPolicy.GetDirectoryName(title);

        Assert.Equal(expected, name);
        Assert.DoesNotContain(name, character => character < ' ' || "<>:\"/\\|?*".Contains(character));
        Assert.False(name.EndsWith(' ') || name.EndsWith('.'));
        Assert.False(Path.IsPathRooted(name));
    }
}
