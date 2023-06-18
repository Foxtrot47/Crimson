using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace WinUiApp.StateManager;

// Make the game info class implement INotifyPropertyChanged interface
public class Game : INotifyPropertyChanged
{
    // Declare the PropertyChanged event
    public event PropertyChangedEventHandler PropertyChanged;

    // Declare a method to raise the event when a property changes
    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Declare the properties with backing fields and raise the event in the setters
    private string _name;

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    private string _title;

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            OnPropertyChanged(nameof(Title));
        }
    }

    private string _installInstallLocation;

    public string InstallLocation
    {
        get => _installInstallLocation;
        set
        {
            _installInstallLocation = value;
            OnPropertyChanged(nameof(InstallLocation));
        }
    }

    public enum InstallState
    {
        NotInstalled,
        Pending,
        Installing,
        Installed,
        Playing,
        Broken,
        NeedUpdate,
        Uninstalling
    }

    private InstallState _installState;

    public InstallState State
    {
        get => _installState;
        set
        {
            _installState = value;
            OnPropertyChanged(nameof(State));
        }
    }

    private DateTime _installedInstalledDateTimeTime;

    public DateTime InstalledDateTime
    {
        get => _installedInstalledDateTimeTime;
        set
        {
            _installedInstalledDateTimeTime = value;
            OnPropertyChanged(nameof(InstalledDateTime));
        }
    }

    public class Image
    {
        public int Height { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
        public int Width { get; set; }
    }

    private List<Image> _images;

    public List<Image> Images
    {
        get => _images;
        set
        {
            _images = value;
            OnPropertyChanged(nameof(Images));
        }
    }

    private long _downloadSizeMiB;

    public long DownloadSizeMiB
    {
        get => _downloadSizeMiB;
        set
        {
            _downloadSizeMiB = value;
            OnPropertyChanged(nameof(DownloadSizeMiB));
        }
    }

    private long _diskSizeMiBSizeMiB;

    public long DiskSizeMiB
    {
        get => _diskSizeMiBSizeMiB;
        set
        {
            _diskSizeMiBSizeMiB = value;
            OnPropertyChanged(nameof(DiskSizeMiB));
        }
    }
}
