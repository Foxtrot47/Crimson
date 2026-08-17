namespace Crimson.Presentation;

public interface IActivatable
{
    Task ActivateAsync(CancellationToken cancellationToken = default);

    void Deactivate();
}
