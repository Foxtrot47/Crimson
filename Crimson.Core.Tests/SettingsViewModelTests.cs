using Crimson.Core;
using Crimson.Utils;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _appDataPath = Path.Combine(
        Path.GetTempPath(),
        $"crimson-settings-{Guid.NewGuid():N}");
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void OpenLogsDirectoryUsesFolderLauncher()
    {
        var installPath = Path.Combine(_appDataPath, "games");
        var logsPath = Path.Combine(_appDataPath, "logs");
        var storage = new Storage(_logger, _appDataPath, installPath);
        var settings = new SettingsManager(
            storage,
            NullLogger<SettingsManager>.Instance,
            installPath,
            logsPath);
        var auth = new AuthManager(
            _logger,
            storage,
            new TestCredentialProtector(),
            new HttpClient());
        var folderLauncher = new RecordingFolderLauncher();
        var viewModel = new SettingsViewModel(settings, auth, folderLauncher);

        viewModel.OpenLogsDirectoryCommand.Execute(null);

        Assert.Equal(logsPath, folderLauncher.OpenedPath);
    }

    [Fact]
    public async Task LanguageDefaultsToSystemAndPersistsWithoutChangingTheRunningCulture()
    {
        var (model, settings, storage) = CreateLanguageFixture();
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        Assert.Equal(string.Empty, model.SelectedLanguage.Code);
        Assert.False(model.IsLanguageRestartRequired);

        await model.ChangeLanguageCommand.ExecuteAsync(model.Languages.Single(option => option.Code == "fr"));

        Assert.Equal("fr", settings.Language);
        Assert.Equal("fr", model.SelectedLanguage.Code);
        Assert.True(model.IsLanguageRestartRequired);
        Assert.Equal(culture, System.Globalization.CultureInfo.CurrentUICulture);
        var reloaded = new SettingsManager(storage, NullLogger<SettingsManager>.Instance, "games", "logs");
        Assert.Equal("fr", reloaded.Language);
        Assert.Equal("fr", reloaded.StartupLanguage);
        var revisited = new SettingsViewModel(settings,
            new AuthManager(_logger, storage, new TestCredentialProtector(), new HttpClient()), new RecordingFolderLauncher());
        Assert.True(revisited.IsLanguageRestartRequired);

        await model.ChangeLanguageCommand.ExecuteAsync(model.Languages[0]);

        Assert.Null(settings.Language);
        Assert.False(model.IsLanguageRestartRequired);
        Assert.Null(new SettingsManager(storage, NullLogger<SettingsManager>.Instance, "games", "logs").Language);
    }

    [Fact]
    public async Task LanguageSaveFailureKeepsThePreviousChoiceAndShowsInlineFeedback()
    {
        var (model, settings, _) = CreateLanguageFixture();
        Directory.CreateDirectory(Path.Combine(_appDataPath, "settings.json"));

        await model.ChangeLanguageCommand.ExecuteAsync(model.Languages.Single(option => option.Code == "de"));

        Assert.Null(settings.Language);
        Assert.Equal(string.Empty, model.SelectedLanguage.Code);
        Assert.True(model.HasLanguageError);
        Assert.False(model.IsLanguageRestartRequired);
    }

    [Fact]
    public async Task UnknownSavedLanguageUsesSystemInsteadOfFailingStartup()
    {
        var (_, _, storage) = CreateLanguageFixture();
        await storage.SaveSettingsData("{\"Language\":\"not-a-supported-language\"}");
        var settings = new SettingsManager(storage, NullLogger<SettingsManager>.Instance, "games", "logs");
        Assert.Null(settings.Language);
        Assert.Null(settings.StartupLanguage);
    }

    private (SettingsViewModel Model, SettingsManager Settings, Storage Storage) CreateLanguageFixture()
    {
        var storage = new Storage(_logger, _appDataPath, Path.Combine(_appDataPath, "games"));
        var settings = new SettingsManager(storage, NullLogger<SettingsManager>.Instance,
            Path.Combine(_appDataPath, "games"), Path.Combine(_appDataPath, "logs"));
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), new HttpClient());
        return (new SettingsViewModel(settings, auth, new RecordingFolderLauncher()), settings, storage);
    }

    [Fact]
    public async Task AcrylicToggleNotifiesTheWindowImmediatelyAndPersistsBothStates()
    {
        var (model, settings, storage) = CreateLanguageFixture();
        var states = new List<bool>();
        settings.AcrylicChanged += (_, _) => states.Add(settings.AcrylicEnabled);
        Assert.False(model.AcrylicEnabled);

        model.AcrylicEnabled = true;
        model.AcrylicEnabled = true;
        Assert.Equal(new[] { true }, states);
        await WaitForAcrylicSave(model);
        Assert.True(new SettingsManager(storage, NullLogger<SettingsManager>.Instance, "games", "logs").AcrylicEnabled);

        model.AcrylicEnabled = false;
        Assert.Equal(new[] { true, false }, states);
        await WaitForAcrylicSave(model);
        Assert.False(new SettingsManager(storage, NullLogger<SettingsManager>.Instance, "games", "logs").AcrylicEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcrylicHonorsTheExistingSavedMicaPreference(bool enabled)
    {
        var (_, _, storage) = CreateLanguageFixture();
        await storage.SaveSettingsData($"{{\"MicaEnabled\":{enabled.ToString().ToLowerInvariant()}}}");
        var settings = new SettingsManager(storage, NullLogger<SettingsManager>.Instance, "games", "logs");
        Assert.Equal(enabled, settings.AcrylicEnabled);
        Assert.Equal(enabled, settings.MicEnabled);
        settings.MicEnabled = !enabled;
        Assert.Equal(!enabled, settings.AcrylicEnabled);
    }

    [Fact]
    public async Task AcrylicSaveFailureRestoresTheWindowPreferenceAndShowsInlineFeedback()
    {
        var (model, settings, _) = CreateLanguageFixture();
        Directory.CreateDirectory(Path.Combine(_appDataPath, "settings.json"));
        var states = new List<bool>();
        settings.AcrylicChanged += (_, _) => states.Add(settings.AcrylicEnabled);

        model.AcrylicEnabled = true;
        await WaitForAcrylicSave(model);

        Assert.False(model.AcrylicEnabled);
        Assert.False(settings.AcrylicEnabled);
        Assert.Equal(new[] { true, false }, states);
        Assert.True(model.HasAcrylicError);
        Assert.Contains("could not be saved", model.AcrylicErrorMessage);
        Assert.True(model.CanChangeAcrylic);

        Directory.Delete(Path.Combine(_appDataPath, "settings.json"));
        model.AcrylicEnabled = true;
        await WaitForAcrylicSave(model);
        Assert.True(model.AcrylicEnabled);
        Assert.False(model.HasAcrylicError);
    }

    [Fact]
    public async Task LockedSettingsFilePreservesTheSavedEnabledAcrylicPreference()
    {
        var (model, settings, storage) = CreateLanguageFixture();
        model.AcrylicEnabled = true;
        await WaitForAcrylicSave(model);
        var settingsPath = Path.Combine(_appDataPath, "settings.json");
        var savedBytes = await File.ReadAllBytesAsync(settingsPath);
        // Unix uses advisory shared locks for FileShare.Read; None excludes the writer on both platforms.
        using (var locked = File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() =>
            {
                using var writer = File.Open(settingsPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            });

            model.AcrylicEnabled = false;
            await WaitForAcrylicSave(model);

            Assert.True(model.AcrylicEnabled);
            Assert.True(model.HasAcrylicError);
        }

        Assert.Equal(savedBytes, await File.ReadAllBytesAsync(settingsPath));
        Assert.True(new SettingsManager(storage, NullLogger<SettingsManager>.Instance, "games", "logs").AcrylicEnabled);
    }

    [Fact]
    public async Task AcrylicSavePreventsOverlappingToggleWrites()
    {
        var (model, settings, _) = CreateLanguageFixture();
        var gate = (SemaphoreSlim)typeof(SettingsManager).GetField("_saveGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(settings)!;
        await gate.WaitAsync();
        try
        {
            model.AcrylicEnabled = true;
            Assert.False(model.CanChangeAcrylic);
            model.AcrylicEnabled = false;
            Assert.True(model.AcrylicEnabled);
        }
        finally { gate.Release(); }
        await WaitForAcrylicSave(model);
        Assert.True(model.CanChangeAcrylic);
        Assert.False(model.HasAcrylicError);

        model.AcrylicEnabled = false;
        await WaitForAcrylicSave(model);
        Assert.False(model.AcrylicEnabled);
        Assert.False(model.HasAcrylicError);
    }

    private static async Task WaitForAcrylicSave(SettingsViewModel model)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(model.IsSavingAcrylic) && !model.IsSavingAcrylic)
                completed.TrySetResult();
        }
        model.PropertyChanged += Changed;
        try
        {
            if (!model.IsSavingAcrylic) completed.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { model.PropertyChanged -= Changed; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_appDataPath))
            Directory.Delete(_appDataPath, recursive: true);
    }

    private sealed class RecordingFolderLauncher : IFolderLauncher
    {
        public string? OpenedPath { get; private set; }

        public void Open(string path)
        {
            OpenedPath = path;
        }
    }
}
