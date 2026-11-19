using System.Text.Json.Serialization;

namespace Crimson.Providers.Ubisoft;

[JsonSerializable(typeof(UbisoftSystemSetupRequest))]
[JsonSerializable(typeof(UbisoftSystemSetupResult))]
internal sealed partial class UbisoftSetupJsonContext : JsonSerializerContext
{
}
