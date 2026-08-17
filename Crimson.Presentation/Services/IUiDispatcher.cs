namespace Crimson.Presentation;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}
