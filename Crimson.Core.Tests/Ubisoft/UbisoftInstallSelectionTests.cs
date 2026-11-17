using Crimson.Providers.Ubisoft;
using static Crimson.Core.Tests.Ubisoft.UbisoftManifestTests;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftInstallSelectionTests
{
    private static readonly byte[] HashA = Convert.FromHexString("73AF6807E1D23A4B5C6D7E8F9012345678901234");
    private static readonly byte[] HashB = Convert.FromHexString("EDE115E6A9B8C7D6E5F40123456789ABCDEF0123");

    [Fact]
    public void Select_gates_chunks_by_ownership_and_language()
    {
        var manifest = UbisoftManifest.Parse(Container(Message(
            Chunk(1, false, files: [Entry("bin\\game.exe")]),
            Chunk(2, false, language: "en-US", uplayIds: [3671, 3367], files: [Entry("sound\\english.pck")]),
            Chunk(3, false, language: "fr-FR", uplayIds: [3671, 3367], files: [Entry("sound\\french.pck")]),
            Chunk(4, true, tags: "ultra_textures", uplayIds: [3962], files: [Entry("textures\\ultra.dat")]),
            Chunk(5, false, language: "ru-RU", files: [Entry("sound\\russian.pck")]))));

        var paths = UbisoftInstallSelection.Select(manifest, ["EN-us"], new HashSet<uint> { 3367 }).Select(file => file.Path);

        Assert.Equal(new[] { "bin\\game.exe", "sound\\english.pck" }, paths);
        Assert.Equal(new[] { "bin\\game.exe", "textures\\ultra.dat" },
            UbisoftInstallSelection.Select(manifest, [], new HashSet<uint> { 3962 }).Select(file => file.Path));
    }

    [Fact]
    public void Select_merges_identical_duplicates_across_chunks()
    {
        var manifest = UbisoftManifest.Parse(Container(Message(
            Chunk(1, false, files: [Entry("bin\\shared.dll")]),
            Chunk(2, false, files: [Entry("BIN/Shared.dll")]))));

        var file = Assert.Single(UbisoftInstallSelection.Select(manifest, [], new HashSet<uint>()));
        Assert.Equal("bin\\shared.dll", file.Path);
    }

    [Fact]
    public void Select_rejects_a_path_listed_with_different_content()
    {
        var manifest = UbisoftManifest.Parse(Container(Message(
            Chunk(1, false, files: [Entry("bin\\shared.dll", HashA)]),
            Chunk(2, false, language: "fr-FR", files: [Entry("bin\\shared.dll", HashB)]))));

        // Not selected together: no conflict.
        Assert.Single(UbisoftInstallSelection.Select(manifest, ["en-US"], new HashSet<uint>()));
        Assert.Throws<InvalidDataException>(() => UbisoftInstallSelection.Select(manifest, ["fr-FR"], new HashSet<uint>()));
    }

    [Fact]
    public void Available_languages_follow_the_manifest_list_gated_by_ownership()
    {
        var manifest = UbisoftManifest.Parse(Container(Message(
            Chunk(1, false, language: "de-DE", files: [Entry("de.pck")]),
            Bytes(23, Message(Text(1, "en-US"))),
            Bytes(23, Message(Text(1, "fr-FR"), Integer(2, 3367))),
            Bytes(23, Message(Text(1, "ru-RU"), Integer(2, 3368))))));

        Assert.Equal(new[] { "en-US", "fr-FR" }, UbisoftInstallSelection.AvailableLanguages(manifest, new HashSet<uint> { 3367 }));
    }

    [Fact]
    public void Available_languages_fall_back_to_chunk_languages()
    {
        var manifest = UbisoftManifest.Parse(Container(Message(
            Chunk(1, false, files: [Entry("base.dat")]),
            Chunk(2, false, language: "en-US", files: [Entry("en.pck")]),
            Chunk(3, true, language: "en-US", files: [Entry("en2.pck")]),
            Chunk(4, false, language: "ja-JP", uplayIds: [3367], files: [Entry("ja.pck")]))));

        Assert.Equal(new[] { "en-US" }, UbisoftInstallSelection.AvailableLanguages(manifest, new HashSet<uint>()));
        Assert.Equal(new[] { "en-US", "ja-JP" }, UbisoftInstallSelection.AvailableLanguages(manifest, new HashSet<uint> { 3367 }));
    }

    [Fact]
    public void Available_languages_from_metadata_are_gated_the_same_way()
    {
        var metadata = UbisoftManifestMetadata.Parse(Container(Message(
            Integer(2, 1), Integer(3, 1),
            Bytes(10, Message(Text(1, "en-US"))),
            Bytes(10, Message(Text(1, "zh-CN"), Integer(2, 3368))))));

        Assert.Equal(new[] { "en-US" }, UbisoftInstallSelection.AvailableLanguages(metadata, new HashSet<uint> { 3367 }));
    }

    [Theory]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("FR-ca", "fr-FR")]
    [InlineData("pl-PL", "en-US")]
    [InlineData("", "en-US")]
    public void Default_language_prefers_exact_then_same_language_then_english(string culture, string expected) =>
        Assert.Equal(expected, UbisoftInstallSelection.DefaultLanguage(["de-DE", "fr-FR", "en-US"], culture));

    [Fact]
    public void Default_language_falls_back_to_the_first_available() =>
        Assert.Equal("de-DE", UbisoftInstallSelection.DefaultLanguage(["de-DE", "ja-JP"], "pl-PL"));

    private static byte[] Entry(string path, byte[]? hash = null) =>
        FileEntry(path, 16, [], Slice(16, 8, hash ?? HashA, 0));
}
