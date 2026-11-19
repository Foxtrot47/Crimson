using System.Collections.Generic;
using System.Text.Json.Serialization;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers.Epic;
using Crimson.Providers.Ubisoft;

namespace Crimson.Utils;

/// <summary>
/// Source-generated JSON contracts. Keeping serialization metadata compile-time removes the
/// reflection paths that IL trimming cannot follow.
/// </summary>
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(UserData))]
[JsonSerializable(typeof(Game))]
[JsonSerializable(typeof(Metadata))]
[JsonSerializable(typeof(List<Asset>))]
[JsonSerializable(typeof(IEnumerable<Asset>))]
[JsonSerializable(typeof(Dictionary<string, LocalAppState>))]
[JsonSerializable(typeof(LibraryCacheSnapshot))]
[JsonSerializable(typeof(StoreDetailsCacheSnapshot))]
[JsonSerializable(typeof(GameTokenResponse))]
[JsonSerializable(typeof(ManifestUrlData))]
[JsonSerializable(typeof(CloudSaveListing))]
[JsonSerializable(typeof(UbisoftSession))]
[JsonSerializable(typeof(UbisoftSignInCredential))]
[JsonSerializable(typeof(UbisoftInstallProgress))]
[JsonSerializable(typeof(UbisoftInstallManager.PendingOperation))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class CrimsonJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UbisoftCloudSaves.StateFile))]
[JsonSerializable(typeof(UbisoftOwnershipCache.CacheFile))]
internal sealed partial class UbisoftCamelJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UbisoftInstalledStore.StoredFile))]
internal sealed partial class UbisoftInstalledJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(Converters = new[] { typeof(BigIntegerJsonConverter) })]
[JsonSerializable(typeof(InstallManagerState))]
internal sealed partial class EpicInstallJsonContext : JsonSerializerContext
{
}
