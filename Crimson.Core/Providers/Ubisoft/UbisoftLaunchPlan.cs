using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Crimson.Providers.Ubisoft.Protocol;
using Microsoft.Win32;

namespace Crimson.Providers.Ubisoft;
/// <summary>In the runtime SDK's own enums, not the ownership service's.</summary>
public sealed record UbisoftSdkProduct(uint ProductId, uint Type, bool Owned, uint State, uint Balance, uint Activation)
{
    public const uint TypeGame = 1, TypeAddon = 2, TypeConsumable = 4;
    private const uint OwnershipTypeConsumable = 8;

    /// <summary>Consumables stay listed even when none are owned.</summary>
    public static IReadOnlyList<UbisoftSdkProduct> For(UbisoftOwnedProduct game, IReadOnlyList<UbisoftOwnedProduct> products)
    {
        if (!game.Owned) return [];
        var byId = products.GroupBy(product => product.ProductId).ToDictionary(group => group.Key, group => group.First());
        var result = new List<UbisoftSdkProduct>();
        foreach (var id in game.Associations.Prepend(game.ProductId).Distinct())
        {
            var product = id == game.ProductId ? game : byId.GetValueOrDefault(id);
            if (product is not null && Entry(product, game) is { } entry) result.Add(entry);
        }
        return result;
    }

    private static UbisoftSdkProduct? Entry(UbisoftOwnedProduct product, UbisoftOwnedProduct game)
    {
        if (SdkType(product.ProductType) is not { } type) return null;
        var state = product.State != 0 ? product.State : game.State;
        if (type != TypeConsumable && (!product.Owned || state != UbisoftOwnedProduct.StateReleased)) return null;
        return new UbisoftSdkProduct(product.ProductId, type, product.Owned, state,
            type == TypeConsumable ? product.Balance : 0, ActivationOf(product));
    }

    private static uint? SdkType(uint productType) => productType switch
    {
        UbisoftOwnedProduct.TypeGame => TypeGame,
        UbisoftOwnedProduct.TypeAddon => TypeAddon,
        OwnershipTypeConsumable => TypeConsumable,
        _ => null
    };

    // Activation: 1 purchased, 2 trial (package ownership 1), 3 for ownership activation type 2.
    private static uint ActivationOf(UbisoftOwnedProduct product) =>
        product.PackageOwnershipState == 1 ? 2u : product.ActivationType == 2 ? 3u : 1u;
}

/// <summary>Paths are absolute and always inside the installation.</summary>
public sealed record UbisoftLaunchPlan(uint ProductId, string InstallPath, string Executable, string WorkingDirectory,
    string Arguments, string? ApplicationId, string Language)
{
    public IReadOnlyList<uint> AddonIds { get; init; } = [];

    public IReadOnlyList<UbisoftSdkProduct> SdkProducts { get; init; } = [];

    /// <summary>Ends with a separator.</summary>
    public string SaveDirectory { get; init; } = "";

    /// <summary>Picks the executable the official client would start.</summary>
    /// <exception cref="InvalidOperationException">No configured executable exists in the installation.</exception>
    public static UbisoftLaunchPlan Resolve(UbisoftInstalledGame installed, UbisoftOwnedProduct product,
        UbisoftProductConfig config, Func<string, string?>? readRegistry = null)
    {
        readRegistry ??= ReadRegistry;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed.InstallPath));
        var candidates = new[] { config.OnlineExecutables.FirstOrDefault(), config.OfflineExecutables.FirstOrDefault() };
        foreach (var entry in candidates)
        {
            if (entry is null) continue;
            var executable = Locate(root, entry.RelativePath, entry.RegisterPath, entry.RegisterAppend, readRegistry);
            if (executable is null || !File.Exists(executable)) continue;

            var workingDirectory = Locate(root, entry.WorkingDirectoryRelative, entry.WorkingDirectoryRegister,
                entry.WorkingDirectoryAppend, readRegistry);
            if (workingDirectory is null || !Directory.Exists(workingDirectory)) workingDirectory = root;

            return new UbisoftLaunchPlan(product.ProductId, root, executable, workingDirectory, SafeArguments(entry),
                NonEmpty(product.AppId) ?? NonEmpty(config.AppId), installed.Languages.FirstOrDefault() ?? "en-US");
        }
        throw new InvalidOperationException(Crimson.Resources.UiStrings.UbisoftLaunchExecutableMissing);
    }

    private static string? Locate(string root, string? relative, string? register, string? append,
        Func<string, string?> readRegistry)
    {
        if (relative is { Length: > 0 }) return Within(root, relative);
        if (register is { Length: > 0 }) return Within(root, RegisteredDirectory(root, register, readRegistry), append);
        return null;
    }

    private static string SafeArguments(UbisoftExecutableEntry entry)
    {
        var arguments = entry.Arguments?.Trim() ?? "";
        return arguments.Any(char.IsControl) ? "" : arguments;
    }

    private static string RegisteredDirectory(string root, string register, Func<string, string?> readRegistry)
    {
        string? value;
        try { value = readRegistry(register); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            value = null;
        }
        if (string.IsNullOrWhiteSpace(value)) return root;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim().Trim('"')));
            return IsInside(root, full) ? full : root;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return root;
        }
    }

    private static string? Within(string root, string first, string? second = null)
    {
        try
        {
            var start = Path.IsPathFullyQualified(first) ? first : Path.Combine(root, Relative(first));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(start, Relative(second))));
            return IsInside(root, full) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Configuration paths are written <c>'\bin\Game.exe'</c>: quoted, with a leading separator.</summary>
    private static string Relative(string? value) => (value ?? "").Trim().Trim('\'', '"')
        .Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static bool IsInside(string root, string full) =>
        string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
        full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>32-bit view first: the official client writes there.</summary>
    public static string? ReadRegistry(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var separator = path.IndexOf('\\');
        var last = path.LastIndexOf('\\');
        if (separator <= 0 || last <= separator) return null;
        RegistryHive? hive = path[..separator].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            _ => null
        };
        if (hive is null) return null;
        var subKey = path[(separator + 1)..last];
        var valueName = path[(last + 1)..];
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
