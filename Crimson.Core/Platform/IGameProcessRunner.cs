namespace Crimson.Core;

public interface IGameProcessRunner
{
    Task RunAsync(
        LaunchPlan launchPlan,
        CancellationToken cancellationToken = default);
}
