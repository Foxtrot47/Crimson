namespace Crimson.Presentation;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(string? suggestedPath, CancellationToken cancellationToken = default);
}

public interface IExternalPathLauncher
{
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
}
