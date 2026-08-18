namespace Crimson.Core;

public sealed record GameProcessStartInfo(
    string FileName,
    string Arguments,
    string WorkingDirectory);

public interface IGameProcessRunner
{
    Task RunAsync(
        GameProcessStartInfo startInfo,
        CancellationToken cancellationToken = default);
}
