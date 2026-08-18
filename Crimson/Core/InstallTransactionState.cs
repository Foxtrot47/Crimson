using System.Collections.Generic;
using System.IO;

namespace Crimson.Core;

internal enum InstallTransactionPhase
{
    Planned,
    Staging,
    ReadyToCommit,
    Committing,
    Paused,
    Published,
    MetadataCommitted,
    Completed,
    RecoveryRequired
}

internal sealed class InstallTransactionState
{
    public InstallOperationPlan Plan { get; set; } = null!;
    public InstallTransactionPhase Phase { get; set; }
    public long Revision { get; set; }
    public string StagingRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    public string TrashRoot { get; set; } = string.Empty;
    public string? OldLocalStateJson { get; set; }
    public string? NewLocalStateJson { get; set; }
    public List<string> BackedUpPaths { get; set; } = [];
    public List<string> PublishedPaths { get; set; } = [];
    public List<string> TrashedPaths { get; set; } = [];

    public static InstallTransactionState Create(
        InstallOperationPlan plan,
        string? oldLocalStateJson,
        string? newLocalStateJson)
    {
        var operationRoot = Path.Combine(plan.InstallRoot, ".Crimson", "operations", plan.OperationId);
        return new InstallTransactionState
        {
            Plan = plan,
            Phase = InstallTransactionPhase.Planned,
            StagingRoot = Path.Combine(operationRoot, "staging"),
            BackupRoot = Path.Combine(operationRoot, "backup"),
            TrashRoot = Path.Combine(operationRoot, "trash"),
            OldLocalStateJson = oldLocalStateJson,
            NewLocalStateJson = newLocalStateJson
        };
    }

    public string JournalPath =>
        Path.Combine(Plan.InstallRoot, ".Crimson", "operations", Plan.OperationId, "journal.json");
}
