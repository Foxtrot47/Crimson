namespace Crimson.Core;

public interface IApplicationDirectories
{
    string DataRoot { get; }

    string LogsDirectory { get; }

    string DefaultInstallRoot { get; }

    string WebViewDataDirectory { get; }
}
