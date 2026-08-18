using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Core;

internal sealed class InstallOperationContext : IDisposable
{
    public InstallOperationContext(InstallItem item, string? operationId = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        OperationId = operationId ?? Guid.NewGuid().ToString("N");
    }

    public string OperationId { get; }
    public InstallOperationPlan? Plan { get; set; }
    public InstallTransactionState? Transaction { get; set; }
    public InstallItem Item { get; }
    public ConcurrentDictionary<string, object> FileLocks { get; } = new();
    public ConcurrentDictionary<BigInteger, List<FileManifest>> ChunkFiles { get; set; } = new();
    public ConcurrentDictionary<BigInteger, int> ChunkReferences { get; set; } = new();
    public ConcurrentDictionary<string, byte> IoTaskSet { get; } = new();
    public List<string> UninstallManifestPaths { get; } = [];
    public List<FileManifest>? ImportVerificationResult { get; set; }
    public BlockingCollection<DownloadTask> DownloadQueue { get; set; } = [];
    public BlockingCollection<IoTask> IoQueue { get; set; } = [];
    public BlockingCollection<BigInteger> CompletedChunks { get; set; } = [];
    public List<BigInteger> ResumeCompletedChunks { get; } = [];
    public List<Task>? DownloadWorkers { get; set; }
    public List<Task>? InstallWorkers { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public Stopwatch Stopwatch { get; } = new();
    public DateTime LastProgressUpdate { get; set; } = DateTime.MinValue;
    public bool PauseRequested { get; set; }
    public bool UserCancellationRequested { get; set; }
    public bool AcceptCancellation { get; set; }
    public bool RecoveryRequested { get; set; }
    public TaskCompletionSource Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? WorkerFailure { get; set; }
    public InstallPlanningFailure? PlanningFailure { get; set; }
    public UpdateTransactionState? UpdateTransaction { get; set; }

    public void Dispose()
    {
        DownloadQueue.Dispose();
        IoQueue.Dispose();
        CompletedChunks.Dispose();
        Cancellation.Dispose();
    }
}
