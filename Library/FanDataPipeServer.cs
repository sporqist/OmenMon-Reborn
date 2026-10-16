#nullable enable
  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OmenMon.Library {

    // Implements a named-pipe server for publishing live fan RPM telemetry
    // to third-party tools (e.g. DeltaT, HWiNFO) without touching the EC (issue #131).
    // Avoids EC conflicts when multiple PawnIO applications run concurrently.
    //
    // Publish() is called by the monitor while it holds the hardware lock, so it never
    // waits: it stores the latest values and signals the background writer. The writer
    // sends the latest values to the connected client (intermediate values are
    // coalesced) and drops a client that does not take a write within WriteTimeout,
    // then waits for the next one.
    public sealed class FanDataPipeServer : IDisposable {

        public const string PipeName = "OmenMon_FanData";

        private static readonly FanDataPipeServer _instance = new FanDataPipeServer();
        public static FanDataPipeServer Instance => _instance;

        // Whether the server may run (<FanDataPipeServer> in the configuration)
        public static bool Enabled { get; set; } = true;

        // Applies the configuration setting: when disabled, a running server stops
        public static void Configure(bool enabled) {
            Enabled = enabled;
            if(!enabled)
                Instance.Stop();
        }

        private readonly object _lock = new object();
        private readonly object _dataLock = new object();
        private readonly AutoResetEvent _serverReady = new AutoResetEvent(false);
        private readonly SemaphoreSlim _dataSignal = new SemaphoreSlim(0, 1);

        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private volatile bool _running;
        private NamedPipeServerStream? _activePipe;

        private int _latestCpuRpm;
        private int _latestGpuRpm;
        private bool _hasData;
        private long _version;

        public bool IsRunning => _running;
        public string ActivePipeName { get; }

        // How long a client may take to accept one write before it is dropped
        internal TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(2);

        // Clients dropped for not reading (for tests and diagnostics)
        internal int DroppedClients => _droppedClients;
        private int _droppedClients;

        public FanDataPipeServer(string pipeName = PipeName) {
            ActivePipeName = pipeName;
        }

        public static string FormatJson(int cpuRpm, int gpuRpm) {
            return string.Format("{{\"cpu\":{0},\"gpu\":{1}}}\n",
                cpuRpm > 0 ? cpuRpm : 0,
                gpuRpm > 0 ? gpuRpm : 0);
        }

        // Starts the server and waits (up to 2 s) until the pipe exists
        public void Start() {
            if(StartCore())
                _serverReady.WaitOne(2000);
        }

        // Starts the server without waiting
        private bool StartCore() {
            lock (_lock) {
                if (_running || !Enabled) return false;
                _running = true;
                _cts = new CancellationTokenSource();
                _serverReady.Reset();
                CancellationToken token = _cts.Token;
                _listenTask = Task.Run(() => ListenLoopAsync(token));
                return true;
            }
        }

        public void Stop() {
            Task? taskToWait = null;
            lock (_lock) {
                if (!_running) return;
                _running = false;
                try {
                    _cts?.Cancel();
                } catch { }
                DisconnectActivePipe();
                taskToWait = _listenTask;
                _listenTask = null;
            }
            if (taskToWait != null) {
                try {
                    taskToWait.Wait(500);
                } catch { }
            }
        }

        // Never blocks on the pipe (see above)
        public void Publish(int cpuRpm, int gpuRpm) {
            if (!Enabled) return;

            lock (_dataLock) {
                _latestCpuRpm = cpuRpm;
                _latestGpuRpm = gpuRpm;
                _hasData = true;
                _version++;
            }
            Signal();

            if (!_running)
                StartCore();
        }

        private void Signal() {
            try {
                if (_dataSignal.CurrentCount == 0)
                    _dataSignal.Release();
            } catch (SemaphoreFullException) {
            } catch (ObjectDisposedException) { }
        }

        private async Task ListenLoopAsync(CancellationToken token) {
            while (_running && !token.IsCancellationRequested) {
                NamedPipeServerStream? pipe = null;
                try {
                    pipe = new NamedPipeServerStream(
                        ActivePipeName,
                        PipeDirection.Out,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    lock (_lock) {
                        _activePipe = pipe;
                    }

                    _serverReady.Set();

                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                    // Send the latest values whenever they change
                    long sent = -1;
                    while (_running && !token.IsCancellationRequested && _activePipe == pipe && pipe.IsConnected) {
                        int cpu, gpu;
                        long version;
                        bool hasData;
                        lock (_dataLock) {
                            cpu = _latestCpuRpm;
                            gpu = _latestGpuRpm;
                            version = _version;
                            hasData = _hasData;
                        }

                        if (hasData && version != sent) {
                            byte[] bytes = Encoding.UTF8.GetBytes(FormatJson(cpu, gpu));
                            Task write = WriteAsync(pipe, bytes);
                            Task done;
                            using (CancellationTokenSource delay = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                                done = await Task.WhenAny(write, Task.Delay(WriteTimeout, delay.Token)).ConfigureAwait(false);
                                delay.Cancel();
                            }
                            if (done != write) {

                                // The client is not reading: drop it. Disposing the pipe
                                // ends the pending write; observe its exception.
                                _ = write.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                                token.ThrowIfCancellationRequested();
                                Interlocked.Increment(ref _droppedClients);
                                break;

                            }
                            await write.ConfigureAwait(false);
                            sent = version;
                        }

                        // Wake up on new data, or every 250 ms to notice a disconnect
                        await _dataSignal.WaitAsync(250, token).ConfigureAwait(false);
                    }
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception) {
                    _serverReady.Set();
                    if (!_running || token.IsCancellationRequested) break;
                    try {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    } catch {
                        break;
                    }
                } finally {
                    DisconnectActivePipe();
                }
            }
        }

        private static async Task WriteAsync(NamedPipeServerStream pipe, byte[] bytes) {
            await pipe.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            pipe.Flush();
        }

        private void DisconnectActivePipe() {
            lock (_lock) {
                if (_activePipe != null) {
                    try {
                        if (_activePipe.IsConnected) {
                            _activePipe.Disconnect();
                        }
                    } catch { }
                    try {
                        _activePipe.Dispose();
                    } catch { }
                    _activePipe = null;
                }
            }
        }

        public void Dispose() {
            Stop();
            _serverReady.Dispose();
            _dataSignal.Dispose();
            _cts?.Dispose();
        }
    }
}
