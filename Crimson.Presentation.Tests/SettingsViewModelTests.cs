using Crimson.Core;
using Crimson.Presentation;
using Xunit;

namespace Crimson.Presentation.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task SettingsWorkflowLoadsBrowsesSavesAndOpensLogs()
    {
        var settings = new StubSettingsService(new AppSettings("C:/Games"));
        var picker = new StubFolderPicker("D:/Games");
        var launcher = new StubPathLauncher();
        var viewModel = new SettingsViewModel(settings, picker, launcher, "C:/Logs");

        await viewModel.ActivateAsync();
        Assert.Equal("C:/Games", viewModel.DefaultInstallLocation);

        await viewModel.BrowseCommand.ExecuteAsync(null);
        Assert.Equal("D:/Games", viewModel.DefaultInstallLocation);
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new AppSettings("D:/Games"), settings.Saved);
        Assert.Equal("Settings saved", viewModel.StatusMessage);
        await viewModel.OpenLogsDirectoryCommand.ExecuteAsync(null);
        Assert.Equal("C:/Logs", launcher.OpenedPath);
    }

    private sealed class StubSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings? Saved { get; private set; }

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFolderPicker(string selected) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(
            string? suggestedPath,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(selected);
    }

    private sealed class StubPathLauncher : IExternalPathLauncher
    {
        public string? OpenedPath { get; private set; }

        public Task OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            OpenedPath = path;
            return Task.CompletedTask;
        }
    }
}
