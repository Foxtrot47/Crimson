using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Crimson.Models;
public class InstalledGame
{
    [JsonPropertyName("app_name")]
    public string AppName { get; set; }

    [JsonPropertyName("base_urls")]
    public List<string> BaseUrls { get; set; }

    [JsonPropertyName("can_run_offline")]
    public bool CanRunOffline { get; set; }

    [JsonPropertyName("egl_guid")]
    public string EglGuid { get; set; }

    [JsonPropertyName("executable")]
    public string Executable { get; set; }

    [JsonPropertyName("install_path")]
    public string InstallPath { get; set; }

    [JsonPropertyName("install_size")]
    public int InstallSize { get; set; }

    [JsonPropertyName("install_tags")]
    public List<object> InstallTags { get; set; }

    [JsonPropertyName("is_dlc")]
    public bool IsDlc { get; set; }

    [JsonPropertyName("launch_parameters")]
    public string LaunchParameters { get; set; }

    [JsonPropertyName("manifest_path")]
    public object ManifestPath { get; set; }

    [JsonPropertyName("needs_verification")]
    public bool NeedsVerification { get; set; }

    [JsonPropertyName("platform")]
    public string Platform { get; set; }

    [JsonPropertyName("prereq_info")]
    public object PrereqInfo { get; set; }

    [JsonPropertyName("requires_ot")]
    public bool RequiresOt { get; set; }

    [JsonPropertyName("save_path")]
    public object SavePath { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("uninstaller")]
    public object Uninstaller { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }
    
}
