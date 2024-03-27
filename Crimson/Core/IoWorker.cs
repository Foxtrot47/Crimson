using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Serilog;

namespace Crimson.Core
{
    public class IoWorker
    {
        private readonly ILogger _log;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ConcurrentQueue<IoTask> _ioQueue;
        private readonly InstallManager _installManager;
        private readonly ConcurrentDictionary<string, int> _chunkPartReferences;
        private static readonly ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary = new ConcurrentDictionary<string, object>();

        public IoWorker(
            ILogger log,
            ref CancellationTokenSource cancellationTokenSource,
            ref ConcurrentQueue<IoTask> ioQueue,
            InstallManager installManager,
            ref ConcurrentDictionary<string, int> chunkPartReferences)
        {
            _log = log;
            _cancellationTokenSource = cancellationTokenSource;
            _installManager = installManager;
            _chunkPartReferences = chunkPartReferences;
            _ioQueue = ioQueue;

            _ = ProcessIoQueue();
        }

        private async Task ProcessIoQueue()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                if (_ioQueue.TryDequeue(out var ioTask))
                {
                    try
                    {
                        switch (ioTask.TaskType)
                        {
                            case IoTaskType.Copy:
                                // Ensure there is a lock object for each destination file
                                var fileLock =
                                    _fileLocksConcurrentDictionary.GetOrAdd(ioTask.DestinationFilePath, new object());

                                var compressedChunkData = await File.ReadAllBytesAsync(ioTask.SourceFilePath);
                                var chunk = Chunk.ReadBuffer(compressedChunkData);
                                _log.Debug("ProcessIoQueue: Reading chunk buffers from {source} finished", ioTask.SourceFilePath);

                                var directoryPath = Path.GetDirectoryName(ioTask.DestinationFilePath);
                                if (!string.IsNullOrEmpty(directoryPath))
                                {
                                    Directory.CreateDirectory(directoryPath);
                                }

                                lock (fileLock)
                                {
                                    using var fileStream = new FileStream(ioTask.DestinationFilePath, FileMode.OpenOrCreate,
                                    FileAccess.Write, FileShare.None);

                                    _log.Debug("ProcessIoQueue: Seeking {seek}bytes on file {destination}", ioTask.FileOffset, ioTask.DestinationFilePath);
                                    fileStream.Seek(ioTask.FileOffset, SeekOrigin.Begin);

                                    // Since chunk offset is a long we cannot use it directly in File stream write or read
                                    // Use a memory stream to seek to the chunk offset
                                    using var memoryStream = new MemoryStream(chunk.Data);
                                    memoryStream.Seek(ioTask.Offset, SeekOrigin.Begin);

                                    var remainingBytesToWrite = ioTask.Size;
                                    // Buffer size is irrelevant as write is continuous
                                    const int bufferSize = 4096;
                                    var buffer = new byte[bufferSize];

                                    _log.Debug("ProcessIoQueue: Writing {size}bytes to {file}", ioTask.Size, ioTask.DestinationFilePath);

                                    while (remainingBytesToWrite > 0)
                                    {
                                        var bytesToRead = (int)Math.Min(bufferSize, remainingBytesToWrite);
                                        var bytesRead = memoryStream.Read(buffer, 0, bytesToRead);
                                        fileStream.Write(buffer, 0, bytesRead);

                                        remainingBytesToWrite -= bytesRead;
                                    }

                                    fileStream.Flush();
                                    _log.Debug("ProcessIoQueue: Finished Writing {size}bytes to {file}", ioTask.Size, ioTask.DestinationFilePath);
                                }

                                _installManager.UpdateInstallProgress(ioTask.Size);

                                // Check for references to the chunk and decrement by one
                                int newCount = _chunkPartReferences.AddOrUpdate(
                                    ioTask.GuidStr,
                                    (key) => 0, // Not expected to be called as the key should exist
                                    (key, oldValue) =>
                                    {
                                        _log.Debug("ProcessIoQueue: decrementing reference count of {guid} by 1. Current value:{oldValue}", ioTask.GuidStr, oldValue);
                                        return oldValue - 1;
                                    }
                                );

                                // Check if the updated count is 0 or less
                                if (newCount <= 0)
                                {
                                    // Attempt to remove the item from the dictionary
                                    if (_chunkPartReferences.TryRemove(ioTask.GuidStr, out _))
                                    {
                                        _log.Debug("ProcessIoQueue: Deleting chunk file {file}", ioTask.SourceFilePath);
                                        // Delete the file if successfully removed
                                        File.Delete(ioTask.SourceFilePath);
                                    }
                                }
                                break;
                            case IoTaskType.Delete:
                                File.Delete(ioTask.DestinationFilePath);
                                _installManager.UpdateInstallProgress(ioTask.Size);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error("ProcessIoQueue: IO task failed with exception {ex}", ex);
                        await _installManager.FailInstall();
                    }
                }
                else
                {
                    await Task.Delay(500);
                }

                _installManager.TryCheckInstallFinished();
            }
        }

    }
}

public class IoTask
{
    public string SourceFilePath { get; set; }
    public string DestinationFilePath { get; set; }
    public long Size { get; set; }
    public long Offset { get; set; }
    public long FileOffset { get; set; }
    public IoTaskType TaskType { get; set; }
    public string GuidStr { get; set; }
}

public enum IoTaskType
{
    Copy,
    Create,
    Delete,
    Read
}
