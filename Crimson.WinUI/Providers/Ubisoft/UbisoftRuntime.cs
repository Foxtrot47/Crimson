using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Providers.Ubisoft.Protocol;
using Microsoft.Win32;
using Serilog;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftRuntime(UbisoftApi api, IUbisoftTickets tickets, ILogger log) : IUbisoftRuntime
{
    private static readonly string[] RuntimeFiles = ["uplay_r1.dll", "uplay_r164.dll", "uplay_aux_r1.dll", "uplay_aux_r164.dll",
        "UbisoftGameLauncher.exe", "UbisoftGameLauncher64.exe", "discord-rpc.x86.dll", "discord-rpc.x64.dll", "steam_api.dll", "steam_api64.dll"];
    public async Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftEntitlement game,
        UbisoftInstalledGame installation, UbisoftConfigurationStore configuration, CancellationToken token)
    {
        ValidateRuntime(configuration.Current.RuntimeDirectory);
        UbisoftConfigurationStore.ValidateGame(installation);
        var profile = await api.Get(session, "/v3/users/me", token);
        if (!profile.TryGetProperty("userId", out var user) || user.GetString() != session.UserId)
            throw new InvalidOperationException("Ubisoft account identity could not be validated.");
        var pc = await tickets.GetPcSession(session.Ticket, session.SessionId, game.ProductId, token);
        var protocol = new UbisoftGameSession(session, game, installation, pc, profile,
            configuration.Saves(session.UserId, game.ProductId), tickets);
        var running = new Running(api, log, session, game.ProductId, protocol, configuration.ProviderRoot, token);
        try { running.Start(installation); return running; }
        catch { await running.DisposeAsync(); throw; }
    }
    internal static void ValidateRuntime(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("Configure your Ubisoft runtime directory in Settings > Accounts > Configure.");
        if (RuntimeFiles.Any(file => !File.Exists(Path.Combine(directory, file))))
            throw new InvalidOperationException("The Ubisoft runtime is incomplete. See docs/UBISOFT_INTEGRATION.md for the required files.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var launcher = machine.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher");
        var registered = launcher?.GetValue("InstallDir") as string;
        if (string.IsNullOrWhiteSpace(registered) || !string.Equals(Path.GetFullPath(registered).TrimEnd('\\', '/'),
            Path.GetFullPath(directory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ubisoft's 32-bit Launcher InstallDir registry setting must point to your runtime directory. Crimson does not modify it.");
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
        private readonly ConcurrentBag<Task> _workers = [];
        private readonly UbisoftProcess _startup = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeGeneration;
        private int _disposed;
        private Task? _heartbeat;
        private bool _playSessionStarted;
        public Task Completion => _done.Task;
        internal Running(UbisoftApi api, ILogger log, UbisoftSession login, uint productId,
            UbisoftGameSession protocol, string root, CancellationToken token)
        { _api = api; _log = log; _login = login; _productId = productId; _protocol = protocol; _root = root; _stop = CancellationTokenSource.CreateLinkedTokenSource(token); }
        internal void Start(UbisoftInstalledGame game)
        {
            string[] names = ["game_start_ipc_pipe_", "uplay_api_process_ipc_pipe_", "uplay_ipc_pipe_", "uplay_aux_ipc_pipe_",
                "uplay_service_ipc_pipe_", "upc_api_process_ipc_pipe_standard", "uplay_protocol_ipc_pipe_", "uplay_overlay_ipc_pipe_",
                "orbit_ipc_pipe_", "uplay_crash_reporter_ipc_pipe_"];
            var prefix = $"terminal_{Process.GetCurrentProcess().SessionId}_";
            // Reserve every pipe before starting the game. FirstPipeInstance rejects conflicts with Connect or another Crimson instance.
            var listeners = names.Select(name => (Name: name, Pipe: CreatePipe(prefix + name, true))).ToList();
            foreach (var (name, pipe) in listeners) _workers.Add(Accept(name, prefix + name, pipe));
            _startup.Start(game);
            _workers.Add(WatchStartup());
            _heartbeat = Heartbeat();
            _log.Information("Ubisoft runtime bridge started for product {ProductId}", _productId);
        }
        private NamedPipeServerStream CreatePipe(string name, bool first)
        {
            var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
            if (first) options |= PipeOptions.FirstPipeInstance;
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, options, 65536, 65536);
            _pipes.TryAdd(pipe, 0);
            return pipe;
        }
        private async Task Accept(string kind, string name, NamedPipeServerStream pipe)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
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
                if (kind == "uplay_service_ipc_pipe_")
                {
                    var hwscore = Path.Combine(_root, "statistics", "hwscore");
                    Directory.CreateDirectory(Path.GetDirectoryName(hwscore)!);
                    await WriteFrame(pipe, Bytes(3, Message(Integer(1, 1), Bytes(3, Text(1, hwscore.Replace('\\', '/'))))), _stop.Token);
                    var buffer = new byte[65536];
                    while (await pipe.ReadAsync(buffer, _stop.Token) != 0) { } // cache sink, no credentials or bodies logged
                    return;
                }
                while (!_stop.IsCancellationRequested && pipe.IsConnected)
                {
                    var request = await ReadFrame(pipe, _stop.Token);
                    byte[]? response = null;
                    switch (kind)
                    {
                        case "game_start_ipc_pipe_": response = _protocol.GameStartResponse(); break;
                        case "uplay_api_process_ipc_pipe_":
                            var process = Read(Read(request).Data(2));
                            if (process.Number(1) != _productId || process.Number(2) == 0) throw new InvalidDataException("Invalid game process registration.");
                            RegisterProcess(checked((int)process.Number(2)));
                            response = UbisoftGameSession.ProcessResponse();
                            break;
                        case "uplay_ipc_pipe_": response = _protocol.SessionResponse(request); break;
                        case "uplay_aux_ipc_pipe_":
                            try { response = await _protocol.AuxResponse(request, (uint)Environment.ProcessId, _stop.Token); }
                            catch (IOException) { throw new InvalidOperationException("Live activation transport failed."); }
                            break;
                    }
                    if (response is not null) await WriteFrame(pipe, response, _stop.Token);
                    if (kind == "uplay_api_process_ipc_pipe_") _startup.SignalReady();
                    // Unimplemented auxiliary traffic is drained, never replayed from a capture.
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { } // the starter and SDK routinely disconnect and reconnect
            catch (Exception error) { Fail(kind, error); }
            finally { _pipes.TryRemove(pipe, out _); pipe.Dispose(); }
        }
        private void RegisterProcess(int pid)
        {
            var process = Process.GetProcessById(pid); // retain a handle before the starter exits
            _ = process.Handle;
            var generation = Interlocked.Increment(ref _activeGeneration);
            _workers.Add(WatchActive(process, generation));
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
            }
        }
        private async Task WatchStartup()
        {
            try
            {
                var exit = _startup.WaitForExit(_stop.Token);
                await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(90), _stop.Token));
                if (exit.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(45), _stop.Token);
                if (Volatile.Read(ref _activeGeneration) == 0) Fail("Game registration", new TimeoutException());
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
        private void Fail(string stage, Exception error)
        {
            var reason = error is UbisoftActivationException activation ? activation.Result.ToString() : error.GetType().Name;
            _log.Warning("Ubisoft runtime {Stage} failed: {Reason}", stage, reason);
            _done.TrySetException(new InvalidOperationException($"Ubisoft {stage} failed: {reason}."));
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            foreach (var pipe in _pipes.Keys) pipe.Dispose();
            // Listener tasks finish before the final snapshot, so no newly accepted handler is missed.
            try { await Task.WhenAll(_workers.ToArray()); } catch (Exception) { }
            try { await Task.WhenAll(_workers.ToArray()); } catch (Exception) { }
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
    }
}
