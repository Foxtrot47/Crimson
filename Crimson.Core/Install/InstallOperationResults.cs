using Crimson.Models;

namespace Crimson.Core;

public enum InstallCommandOutcome
{
    Accepted,
    Rejected,
    NotFound
}

public sealed record InstallCommandResult(
    InstallCommandOutcome Outcome,
    string? Message = null,
    Task<InstallTerminalResult>? Terminal = null)
{
    public bool IsAccepted => Outcome == InstallCommandOutcome.Accepted;
}

public enum InstallTerminalOutcome
{
    Succeeded,
    Failed,
    Cancelled
}

public sealed record InstallTerminalResult(
    string AppName,
    ActionType Action,
    InstallTerminalOutcome Outcome,
    string? Error = null,
    InstallPlanningFailure? PlanningFailure = null);
