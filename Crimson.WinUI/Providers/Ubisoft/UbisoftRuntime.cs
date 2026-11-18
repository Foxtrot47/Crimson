using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Providers.Ubisoft.Protocol;
using Microsoft.Win32.SafeHandles;
using Serilog;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftRuntime(UbisoftApi api, IUbisoftTickets tickets, ILogger log, string providerRoot) : IUbisoftRuntime
{
    public async Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftLaunchPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(plan.Executable)) throw new FileNotFoundException("The game executable does not exist.");
        var profile = await api.Get(session, "/v3/users/me", token);
        if (!profile.TryGetProperty("userId", out var user) || user.GetString() != session.UserId)
            throw new InvalidOperationException("Ubisoft account identity could not be validated.");
        var pc = await tickets.GetPcSession(session.Ticket, session.SessionId, plan.ProductId, token);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(plan.SaveDirectory);
        var protocol = new UbisoftGameSession(session, plan, pc, profile, tickets);
        var running = new Running(api, log, session, plan.ProductId, protocol, Path.GetFullPath(providerRoot), token);
        try { running.Start(plan); return running; }
        catch { await running.DisposeAsync(); throw; }
    }

    private sealed class Running : IUbisoftRunningGame
    {
        private readonly UbisoftApi _api;
        private readonly ILogger _log;
        private readonly UbisoftSession _login;
        private readonly uint _productId;
        private readonly UbisoftGameSession _protocol;
        private readonly string _root;
        private readonly CancellationTokenSource _stop;
        private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _pipes = new();
        private readonly ConcurrentDictionary<int, Process> _registered = new();
        private readonly ConcurrentBag<Task> _workers = [];
        private readonly UbisoftProcess _startup = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _executable = "";
        private string _installRoot = "";
        private int _activeGeneration;
        private int _disposed;
        private int _connections;
        private Task? _heartbeat;
        private bool _playSessionStarted;
        public Task Completion => _done.Task;
        internal Running(UbisoftApi api, ILogger log, UbisoftSession login, uint productId,
            UbisoftGameSession protocol, string root, CancellationToken token)
        { _api = api; _log = log; _login = login; _productId = productId; _protocol = protocol; _root = root; _stop = CancellationTokenSource.CreateLinkedTokenSource(token); }
        internal void Start(UbisoftLaunchPlan plan)
        {
            _executable = Path.GetFullPath(plan.Executable);
            _installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.InstallPath));
            string[] names = ["game_start_ipc_pipe_", "uplay_api_process_ipc_pipe_", "uplay_ipc_pipe_", "uplay_aux_ipc_pipe_",
                "uplay_service_ipc_pipe_", "upc_api_process_ipc_pipe_standard", "uplay_protocol_ipc_pipe_", "uplay_overlay_ipc_pipe_",
                "orbit_ipc_pipe_", "uplay_crash_reporter_ipc_pipe_"];
            var prefix = $"terminal_{Process.GetCurrentProcess().SessionId}_";
            // Reserve every pipe before starting the game. FirstPipeInstance rejects conflicts with Connect or another Crimson instance.
            var listeners = names.Select(name => (Name: name, Pipe: CreatePipe(prefix + name, true))).ToList();
            foreach (var (name, pipe) in listeners) _workers.Add(Accept(name, prefix + name, pipe));
            _stop.Token.ThrowIfCancellationRequested();
            _startup.Start(plan.Executable, plan.Arguments, plan.WorkingDirectory);
            _workers.Add(WatchStartup());
            _heartbeat = Heartbeat();
            _log.Information("Ubisoft runtime bridge started for product {ProductId}", _productId);
        }
        private NamedPipeServerStream CreatePipe(string name, bool first)
        {
            var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
            if (first) options |= PipeOptions.FirstPipeInstance;
            // R2 SDK games hold about 20 uplay_ipc connections open at once.
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options, 65536, 65536);
            _pipes.TryAdd(pipe, 0);
            return pipe;
        }

        /// <summary>The session carries account identity and a uplay_pc ticket, so only the game's own processes
        /// under this installation (or the starter) may connect.</summary>
        private bool IsAllowedClient(NamedPipeServerStream pipe)
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return false;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return IsOwnedProcess(process);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return false;
            }
        }
        private async Task Accept(string kind, string name, NamedPipeServerStream pipe)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    if (!IsAllowedClient(pipe))
                    {
                        _pipes.TryRemove(pipe, out _);
                        pipe.Dispose();
                        pipe = CreatePipe(name, false);
                        continue;
                    }
                    Interlocked.Increment(ref _connections);
                    _workers.Add(Handle(kind, pipe));
                    pipe = CreatePipe(name, false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { Fail("Pipe listener", error); }
            finally { _pipes.TryRemove(pipe, out _); pipe.Dispose(); }
        }
        private async Task Handle(string kind, NamedPipeServerStream pipe)
        {
            try
            {
                if (kind == "uplay_service_ipc_pipe_") await ServeStatistics(pipe);
                else await ServeRequests(kind, pipe);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { } // the starter and SDK routinely disconnect and reconnect
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or
                ArgumentException or OverflowException)
            {
                // A malformed or unsupported frame belongs to this connection only; the game can open another.
                _log.Warning("Ubisoft runtime ignored an invalid {Pipe} frame ({ErrorType})", kind, error.GetType().Name);
            }
            catch (Exception error) { Fail(kind, error); }
            finally { _pipes.TryRemove(pipe, out _); pipe.Dispose(); }
        }
        private async Task ServeStatistics(NamedPipeServerStream pipe)
        {
            var hwscore = Path.Combine(_root, "statistics", "hwscore");
            Directory.CreateDirectory(Path.GetDirectoryName(hwscore)!);
            await WriteFrame(pipe, Bytes(3, Message(Integer(1, 1), Bytes(3, Text(1, hwscore.Replace('\\', '/'))))), _stop.Token);
            var buffer = new byte[65536];
            while (await pipe.ReadAsync(buffer, _stop.Token) != 0) { } // cache sink, no credentials or bodies logged
        }
        private async Task ServeRequests(string kind, NamedPipeServerStream pipe)
        {
            while (!_stop.IsCancellationRequested && pipe.IsConnected)
            {
                var response = await Respond(kind, await ReadFrame(pipe, _stop.Token));
                if (response is not null) await WriteFrame(pipe, response, _stop.Token);
                if (kind == "uplay_api_process_ipc_pipe_") _startup.SignalReady();
                // Unimplemented auxiliary traffic is drained, never replayed from a capture.
            }
        }
        private async Task<byte[]?> Respond(string kind, byte[] request)
        {
            switch (kind)
            {
                case "game_start_ipc_pipe_": return _protocol.GameStartResponse(request);
                case "uplay_api_process_ipc_pipe_": return RegisterGameProcess(request);
                case "uplay_ipc_pipe_": return _protocol.SessionResponse(request);
                case "uplay_aux_ipc_pipe_":
                    try { return await _protocol.AuxResponse(request, (uint)Environment.ProcessId, _stop.Token); }
                    catch (IOException) { throw new InvalidOperationException("Live activation transport failed."); }
                default: return null;
            }
        }
        private byte[] RegisterGameProcess(byte[] request)
        {
            var process = Read(Read(request).Data(2));
            if (process.Number(1) != _productId || process.Number(2) == 0) throw new InvalidDataException("Invalid game process registration.");
            RegisterProcess(checked((int)process.Number(2)));
            return UbisoftGameSession.ProcessResponse();
        }
        private void RegisterProcess(int pid)
        {
            var process = Process.GetProcessById(pid); // retain a handle before the starter exits
            if (!IsOwnedProcess(process) || _registered.Count >= 16)
            {
                process.Dispose();
                throw new InvalidDataException("The registering process does not belong to this installation.");
            }
            _registered[pid] = process;
            _ = process.Handle;
            var generation = Interlocked.Increment(ref _activeGeneration);
            _workers.Add(WatchActive(process, generation));
        }

        private bool IsOwnedProcess(Process process)
        {
            try
            {
                if (process.SessionId != Process.GetCurrentProcess().SessionId) return false;
                var image = process.MainModule?.FileName;
                if (image is not { Length: > 0 }) return false;
                var full = Path.GetFullPath(image);
                return string.Equals(full, _executable, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(_installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException or IOException)
            {
                return false;
            }
        }

        /// <summary>Ends every process this runtime started when the bridge can no longer serve it.</summary>
        private void TerminateOwnedProcesses()
        {
            _startup.Terminate();
            foreach (var process in _registered.Values)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
            }
        }

        private async Task WatchActive(Process process, int generation)
        {
            using (process)
            {
                try
                {
                    await process.WaitForExitAsync(_stop.Token);
                    _log.Information("Ubisoft game process exited ({ExitCode})", process.ExitCode);
                    // EAC may replace a bootstrap process; a subsequent registration wins over this exit.
                    await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token);
                    if (generation == Volatile.Read(ref _activeGeneration)) _done.TrySetResult();
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception error) { Fail("Process monitoring", error); }
                finally { _registered.TryRemove(process.Id, out _); }
            }
        }
        private async Task WatchStartup()
        {
            try
            {
                var exit = _startup.WaitForExit(_stop.Token);
                await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(90), _stop.Token));
                if (exit.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(45), _stop.Token);
                if (Volatile.Read(ref _activeGeneration) == 0)
                {
                    // A game built for the pre-2013 launcher never talks to this runtime and waits forever.
                    if (Volatile.Read(ref _connections) == 0)
                    {
                        _startup.Terminate();
                        FailWith(Crimson.Resources.UiStrings.UbisoftLegacyLauncherRequired);
                        return;
                    }
                    Fail("Game registration", new TimeoutException());
                }
                await exit;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { Fail("Startup monitoring", error); }
        }
        private async Task Heartbeat()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    try { await _api.PlaySession(_login, _productId, false, _stop.Token); _playSessionStarted = true; }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception error) { _log.Warning("Ubisoft play-session refresh failed ({ErrorType})", error.GetType().Name); }
                    await Task.Delay(TimeSpan.FromSeconds(330), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private void FailWith(string message)
        {
            _log.Warning("Ubisoft runtime stopped: {Reason}", message);
            TerminateOwnedProcesses();
            _done.TrySetException(new InvalidOperationException(message));
        }
        private void Fail(string stage, Exception error)
        {
            var reason = error is UbisoftActivationException activation ? activation.Result.ToString() : error.GetType().Name;
            _log.Warning("Ubisoft runtime {Stage} failed: {Reason}", stage, reason);
            TerminateOwnedProcesses();
            _done.TrySetException(new InvalidOperationException($"Ubisoft {stage} failed: {reason}."));
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // A normal game exit completes _done; any other teardown must not leave the game attached to a dead bridge.
            if (!_done.Task.IsCompleted) TerminateOwnedProcesses();
            _stop.Cancel();
            foreach (var pipe in _pipes.Keys) pipe.Dispose();
            await DrainWorkersAsync();
            if (_heartbeat is not null) await _heartbeat;
            if (_playSessionStarted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _api.PlaySession(_login, _productId, true, timeout.Token); }
                catch (Exception error) { _log.Warning("Ubisoft play-session cleanup failed ({ErrorType})", error.GetType().Name); }
            }
            _startup.Dispose();
            _stop.Dispose();
            _done.TrySetResult();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        /// <summary>Listeners can add handlers while the snapshot is awaited; repeat until the bag stops growing.</summary>
        private async Task DrainWorkersAsync()
        {
            while (true)
            {
                var snapshot = _workers.ToArray();
                try { await Task.WhenAll(snapshot); } catch (Exception) { }
                if (_workers.Count == snapshot.Length) return;
            }
        }
    }
}
