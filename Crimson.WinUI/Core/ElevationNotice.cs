using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Core;

public enum ElevationOutcome
{
    Approved,
    Declined,
    Failed
}

/// <summary>
/// Lets background work explain a Windows administrator prompt and open it from a notice. The main window registers
/// the presenter (<see cref="ElevationNoticePresenter"/>); before it exists, the prompt opens once, directly.
/// </summary>
public sealed class ElevationNotice
{
    internal Func<IReadOnlyList<string>, Func<ElevationOutcome>, CancellationToken, Task>? Presenter { get; set; }

    /// <summary>
    /// Shows what the prompt is for and calls <paramref name="elevate"/>, which opens the prompt, when the user
    /// continues. The notice closes only after an approved prompt, when the user cancels, or when
    /// <paramref name="cancellationToken"/> is cancelled; after a declined or failed prompt it stays open so the
    /// user can try again. <paramref name="elevate"/> runs on a background thread and may run any number of times,
    /// including zero.
    /// </summary>
    public Task RunAsync(IReadOnlyList<string> reasons, Func<ElevationOutcome> elevate, CancellationToken cancellationToken)
    {
        if (Presenter is { } presenter) return presenter(reasons, elevate, cancellationToken);
        elevate();
        return Task.CompletedTask;
    }
}
