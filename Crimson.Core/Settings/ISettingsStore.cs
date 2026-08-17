using Crimson.Models;

namespace Crimson.Core;

public interface ISettingsStore
{
    Settings? Get();

    void Save(Settings settings);
}
