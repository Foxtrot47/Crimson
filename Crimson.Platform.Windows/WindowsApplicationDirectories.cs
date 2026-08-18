using Crimson.Core;

namespace Crimson.Platform.Windows;

public sealed class WindowsApplicationDirectories : IApplicationDirectories
{
    public WindowsApplicationDirectories()
    {
        DataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Crimson");
        LogsDirectory = Path.Combine(DataRoot, "logs");
        WebViewDataDirectory = Path.Combine(DataRoot, "webview2");
        DefaultInstallRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }

    public string DataRoot { get; }

    public string LogsDirectory { get; }

    public string DefaultInstallRoot { get; }

    public string WebViewDataDirectory { get; }
}
