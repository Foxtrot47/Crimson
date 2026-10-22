using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Providers.Ubisoft;

public sealed record UbisoftInstalledGame(uint ProductId, string Title, string InstallDirectory, string Executable,
    string Arguments = "", string Language = "en-US", IReadOnlyList<uint>? AddonValidationIds = null);
public sealed record UbisoftConfiguration(string RuntimeDirectory, IReadOnlyList<UbisoftInstalledGame> Games)
{
    public static UbisoftConfiguration Empty { get; } = new("", []);
}

/// <summary>Explicit game paths, never inferred from captures or the official client's account/cache files.</summary>
public sealed class UbisoftConfigurationStore(string providerRoot)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    public string ProviderRoot { get; } = Path.GetFullPath(providerRoot);
    public UbisoftConfiguration Current { get; private set; } = UbisoftConfiguration.Empty;
    public event Action? Changed;
    public async Task Load()
    {
        await _gate.WaitAsync();
        try
        {
            if (_loaded) return;
            var file = Path.Combine(ProviderRoot, "configuration.json");
            Current = File.Exists(file) ? JsonSerializer.Deserialize<UbisoftConfiguration>(await File.ReadAllTextAsync(file))
                ?? UbisoftConfiguration.Empty : UbisoftConfiguration.Empty;
            _loaded = true;
        }
        finally { _gate.Release(); }
    }
    public async Task Save(UbisoftConfiguration configuration)
    {
        var duplicates = configuration.Games.GroupBy(g => g.ProductId).Any(g => g.Count() > 1);
        if (duplicates) throw new ArgumentException("Each Ubisoft product can have one installation.");
        foreach (var game in configuration.Games) ValidateGame(game, requireExecutable: false);
        var runtime = string.IsNullOrWhiteSpace(configuration.RuntimeDirectory) ? "" : Path.GetFullPath(configuration.RuntimeDirectory);
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(ProviderRoot);
            var file = Path.Combine(ProviderRoot, "configuration.json");
            await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(configuration with { RuntimeDirectory = runtime }));
            File.Move(file + ".tmp", file, overwrite: true);
            Current = configuration with { RuntimeDirectory = runtime };
            _loaded = true;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    public static void ValidateGame(UbisoftInstalledGame game, bool requireExecutable = true)
    {
        if (game.ProductId == 0 || string.IsNullOrWhiteSpace(game.Title)) throw new ArgumentException("Product ID and title are required.");
        if (!Path.IsPathFullyQualified(game.InstallDirectory) || !Path.IsPathFullyQualified(game.Executable))
            throw new ArgumentException("Use absolute paths for the installation and executable.");
        var root = Path.GetFullPath(game.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var executable = Path.GetFullPath(game.Executable);
        if (!executable.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The executable must be inside the installation directory.");
        if (game.AddonValidationIds?.Any(id => id == 0) == true) throw new ArgumentException("Addon IDs must be positive product IDs.");
        if (requireExecutable && !File.Exists(executable)) throw new FileNotFoundException("The configured game executable does not exist.");
        if (game.Arguments.IndexOf('\0') >= 0 || string.IsNullOrWhiteSpace(game.Language)) throw new ArgumentException("Invalid launch options.");
    }
    public string Saves(string userId, uint productId)
    {
        // User IDs are opaque API values, never path components supplied unchecked by the server.
        var key = ProviderDataPaths.GameFileKey(new GameId("ubisoft", userId));
        var directory = Path.Combine(ProviderRoot, "savegames", key, productId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
