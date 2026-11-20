using Crimson.Providers.Ubisoft;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftProductConfigTests
{
    private const string Yaml = """
        version: 2.0
        root:
          name: l1
          sort_string: "Watch Dogs 02"
          background_image: 160159cf6a4487f5605078896c68e294.jpg
          thumb_image: l2
          logo_image: LOGOIMAGE
          icon_image: 1dfa256ba4370c1212f01a1251268216.ico
          overlay_notifications:
          - id: 0
            text: l3
            permanent: no
          start_game:
            online:
              overlay_required: yes
              executables:
              - shortcut_name: l4
                path:
                  relative: '\bin\WatchDogs2.exe'
                trial:
                  relative: '\bin_plus\WatchDogs2.exe'
                working_directory:
                  register: HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\WatchDogs2\InstallDir
                  append: ''
                denuvo: yes
            offline:
              executables:
                - path:
                    register: HKEY_CURRENT_USER\SOFTWARE\UBISOFT\Far Cry 3\ExecutableName
                    append: bin
                  working_directory:
                    relative: bin
                  arguments: -offline -write_registry   # comment
          installer:
            game_identifier: WATCH_DOGS2
          change_language_enabled: yes
          cloud_saves: yes
          crash_reporting:
            space_id: crash-space
          app_id: 35c5c607-2717-47d9-9323-7df47c6e1c4d
          space_id: 8c8d9b22-498c-45e6-80da-7cd22787c9b3
          some_future_key:
            nested: [1, 2]
            text: |
              literal
              block
          addons:
          - id: 3367
            is_visible: no
          - id: 3962
            is_visible: yes
            name: l5
            thumb_image: f3fa5bffd6623a5e8ddb3ebb89415e45.jpg
            description: >-
              Folded text
              over two lines
          - id: not-a-number
          show_properties: yes
        localizations:
          default:
            l1: WATCH_DOGS® 2
            l2: 0f946fb03f8bdf913969d1c22df1625e.jpg
            l4: "WATCH_DOGS® 2 \"Shortcut\""
            l5: 'High-Rez Texture Pack'
          fr-CA:
            l1: Mauvais choix
          fr-FR:
            l1: WATCH_DOGS® 2 (FR)
          zh-CN:
            l2: 361d4ac8f27b8da4871863d4b837f9e1.jpg
        """;

    [Fact]
    public void Parses_titles_artwork_and_flags_with_default_localization()
    {
        var config = UbisoftProductConfig.Parse(Yaml);

        Assert.Equal("WATCH_DOGS® 2", config.Name);
        Assert.Equal("Watch Dogs 02", config.SortName);
        Assert.Equal(UbisoftProductConfig.AssetUrl("0f946fb03f8bdf913969d1c22df1625e.jpg"), config.CoverImage);
        Assert.Equal("https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/160159cf6a4487f5605078896c68e294.jpg", config.LandscapeImage);
        Assert.Null(config.LogoImage);
        Assert.EndsWith("1dfa256ba4370c1212f01a1251268216.ico", config.IconImage);
        Assert.True(config.CloudSaves);
        Assert.True(config.ChangeLanguageEnabled);
        Assert.Equal("WATCH_DOGS2", config.GameIdentifier);
        Assert.Equal("8c8d9b22-498c-45e6-80da-7cd22787c9b3", config.SpaceId);
        Assert.Equal("35c5c607-2717-47d9-9323-7df47c6e1c4d", config.AppId);
        Assert.Null(config.ThirdPartyPlatform);
    }

    [Theory]
    [InlineData("fr-FR", "WATCH_DOGS® 2 (FR)")]
    [InlineData("fr-BE", "WATCH_DOGS® 2 (FR)")] // language prefix prefers the language's own region
    [InlineData("de-DE", "WATCH_DOGS® 2")]
    [InlineData(null, "WATCH_DOGS® 2")]
    public void Localizes_through_the_locale_then_default(string? locale, string expected) =>
        Assert.Equal(expected, UbisoftProductConfig.Parse(Yaml, locale).Name);

    [Fact]
    public void Localized_values_missing_from_the_locale_fall_back_per_key()
    {
        var config = UbisoftProductConfig.Parse(Yaml, "zh-CN");
        Assert.Equal("WATCH_DOGS® 2", config.Name);
        Assert.EndsWith("361d4ac8f27b8da4871863d4b837f9e1.jpg", config.CoverImage);
    }

    [Fact]
    public void Parses_online_and_offline_executables()
    {
        var config = UbisoftProductConfig.Parse(Yaml);

        var online = Assert.Single(config.OnlineExecutables);
        Assert.Equal(new UbisoftExecutableEntry("\\bin\\WatchDogs2.exe", null, null, null,
            "HKEY_LOCAL_MACHINE\\SOFTWARE\\Ubisoft\\WatchDogs2\\InstallDir", null, null,
            "WATCH_DOGS® 2 \"Shortcut\"", true), online);

        var offline = Assert.Single(config.OfflineExecutables);
        Assert.Equal(new UbisoftExecutableEntry(null, "HKEY_CURRENT_USER\\SOFTWARE\\UBISOFT\\Far Cry 3\\ExecutableName", "bin",
            "bin", null, null, "-offline -write_registry", null, false), offline);
    }

    [Fact]
    public void Parses_addons_and_skips_invalid_entries()
    {
        var addons = UbisoftProductConfig.Parse(Yaml).Addons;

        Assert.Equal(2, addons.Count);
        Assert.Equal(new UbisoftConfigAddon(3367, null, false, null), addons[0]);
        Assert.Equal(new UbisoftConfigAddon(3962, "High-Rez Texture Pack", true,
            UbisoftProductConfig.AssetUrl("f3fa5bffd6623a5e8ddb3ebb89415e45.jpg")), addons[1]);
    }

    [Fact]
    public void Reads_third_party_platform_and_empty_localizations()
    {
        var config = UbisoftProductConfig.Parse("""
            version: 2.0
            root:
              name: The Crew
              third_party_platform:
                name: Steam
              overlay_notifications: []
            localizations: {}
            """, "en-US");

        Assert.Equal("The Crew", config.Name);
        Assert.Equal("Steam", config.ThirdPartyPlatform);
        Assert.False(config.CloudSaves);
        Assert.Empty(config.OnlineExecutables);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not yaml at all")]
    [InlineData("root:\n  name: [unterminated\n  start_game:\n   - - -\n  : :\n\"oops")]
    public void Tolerates_malformed_input(string yaml) =>
        Assert.Null(Record.Exception(() => UbisoftProductConfig.Parse(yaml)));

    [Fact]
    public void Rejects_oversized_input() =>
        Assert.Throws<InvalidDataException>(() => UbisoftProductConfig.Parse(new string(' ', UbisoftProductConfig.MaximumLength + 1)));
}
