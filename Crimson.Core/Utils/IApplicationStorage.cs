using System.IO;
using System.Threading.Tasks;

namespace Crimson.Utils;

/// <summary>Application settings and local filesystem queries, independent of store data.</summary>
public interface IApplicationStorage
{
    string DefaultInstallPath { get; }
    string GetSettingsData();
    Task SaveSettingsData(string data);
    Task<DriveInfo> GetDriveInfo(string path);
}
