using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VPNGate.Core;

public sealed record CoreStartRequest(VpnNode? Node, AppSettings Settings, string ListenAddress, int ListenPort,
    int ProbePort, RoutingControl Control, bool TunEnabled);

// One active sing-box instance owns the upstream dialer, OpenVPN endpoint, mixed listener and optional TUN.
public sealed class EngineSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _worker;
    private ProcessJob? _job;
    private Task? _logs;
    private bool _elevated, _cycleActive, _disposing, _closingWorker;
    private CoreStartRequest? _request;
    private TaskCompletionSource _ready = NewSignal(), _vpnReady = NewSignal(), _stopped = NewSignal();
    private readonly Queue<string> _recent = new();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<string>? Log;
    public event Action<string>? Failed;
    public bool IsRunning { get; private set; }
    public bool IsVpnConnected { get; private set; }
    public bool TunEnabled => _request?.TunEnabled == true && IsRunning;
    public VpnNode? Node => _request?.Node;
    public int Port => _request?.ListenPort ?? 0;
    public int ProbePort => _request?.ProbePort ?? 0;
    public string ProxyAddress => $"http://127.0.0.1:{Port}";
    public int? HostProcessId => _worker?.Id;
    public int? CoreProcessId { get; private set; }
    public string RecentError { get { lock (_recent) return string.Join('\n', _recent.TakeLast(4)); } }
    public Task StartAsync(VpnNode? node, AppSettings settings, string listenAddress, int listenPort, CancellationToken token, bool tunEnabled = false)
        => ConfigureAsync(node, settings, listenAddress, listenPort, tunEnabled, token);
    public async Task ConfigureAsync(VpnNode? node, AppSettings settings, string address, int port, bool tun, CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try
        {
            if (_disposing) throw new ObjectDisposedException(nameof(EngineSession));
            settings.Validate(); await Task.Run(() => EngineRuntime.Verify(EngineRuntime.CorePath), token);
            await new RoutingRuleSets().EnsureAsync(token);
            var previous = _request;
            var probe = previous?.ProbePort ?? EngineRuntime.FreePort();
            while (probe == port) probe = EngineRuntime.FreePort();
            var control = previous?.Control ?? RoutingControl.Create(port, probe);
            var next = new CoreStartRequest(node?.Copy(), settings.Copy(), address, port, probe, control, tun);
            try { await ApplyAsync(next, token); }
            catch
            {
                try { await StopCycleAsync(); if (previous is not null && !_disposing) await ApplyAsync(previous, CancellationToken.None); }
                catch (Exception error) { Log?.Invoke("恢复主核心失败：" + error.Message); }
                throw;
            }
        }
        finally { _operations.Release(); }
    }
    public async Task SetRoutingModeAsync(string mode, CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try
        {
            var request = _request ?? throw new InvalidOperationException("主核心尚未启动。");
            await request.Control.SetModeAsync(mode, token); request.Settings.RoutingMode = mode;
        }
        finally { _operations.Release(); }
    }
    public Task SetTunEnabledAsync(bool enabled, CancellationToken token = default)
    {
        var request = _request ?? throw new InvalidOperationException("主核心尚未启动。");
        return ConfigureAsync(request.Node, request.Settings, request.ListenAddress, request.ListenPort, enabled, token);
    }
    public Task DisconnectVpnAsync(CancellationToken token = default)
    {
        var request = _request ?? throw new InvalidOperationException("主核心尚未启动。");
        return ConfigureAsync(null, request.Settings, request.ListenAddress, request.ListenPort, request.TunEnabled, token);
    }
    private async Task ApplyAsync(CoreStartRequest request, CancellationToken token)
    {
        await StopCycleAsync();
        if (request.TunEnabled && !_elevated && _worker is not null) await CloseWorkerAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        if (_worker is null) await StartWorkerAsync(request.TunEnabled, timeout.Token);
        _ready = NewSignal(); _vpnReady = NewSignal(); _stopped = NewSignal();
        _request = request; _cycleActive = true; IsRunning = false; IsVpnConnected = false; CoreProcessId = null;
        lock (_recent) _recent.Clear();
        await _writer!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
        await _ready.Task.WaitAsync(timeout.Token); IsRunning = true;
        if (request.Node is not null)
        {
            using var vpnTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            vpnTimeout.CancelAfter(TimeSpan.FromSeconds(request.Settings.ConnectionTimeoutSeconds));
            try { await _vpnReady.Task.WaitAsync(vpnTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException($"OpenVPN 连接超过 {request.Settings.ConnectionTimeoutSeconds} 秒。{RecentError}"); }
            IsVpnConnected = true;
        }
    }
    private async Task StartWorkerAsync(bool administrator, CancellationToken token)
    {
        var name = "VPNGateXray.Core." + Guid.NewGuid().ToString("N");
        _pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        if (administrator && !EngineRuntime.IsAdministrator) start.Verb = "runas";
        start.ArgumentList.Add("--core-worker"); start.ArgumentList.Add(name);
        _worker = await Task.Run(() => Process.Start(start), CancellationToken.None).WaitAsync(token) ?? throw new IOException("无法启动主核心后台进程。");
        _elevated = administrator || EngineRuntime.IsAdministrator;
        if (!_elevated) { _job = new ProcessJob(); _job.Add(_worker); }
        var connected = _pipe.WaitForConnectionAsync(token); var exited = _worker.WaitForExitAsync();
        if (await Task.WhenAny(connected, exited) == exited && !_pipe.IsConnected) throw new IOException("主核心在建立控制通道前退出。");
        await connected;
        _reader = new(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _logs = Task.Run(ReadLogsAsync);
    }
    private async Task ReadLogsAsync()
    {
        try
        {
            while (await _reader!.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("CORE_PID:", StringComparison.Ordinal)) { CoreProcessId = int.Parse(line[9..]); continue; }
                if (line == "CORE_READY") { IsRunning = true; _ready.TrySetResult(); continue; }
                if (line == "CORE_STOPPED")
                {
                    IsRunning = false; IsVpnConnected = false; _cycleActive = false; CoreProcessId = null;
                    _stopped.TrySetResult(); _ready.TrySetException(new IOException("主核心在启动前停止。"));
                    if (!_vpnReady.Task.IsCompleted) _vpnReady.TrySetException(new IOException("OpenVPN 在连接前停止。"));
                    continue;
                }
                lock (_recent) { _recent.Enqueue(line); while (_recent.Count > 16) _recent.Dequeue(); }
                Log?.Invoke(line);
                if (line.Contains("tunnel established", StringComparison.OrdinalIgnoreCase)) { IsVpnConnected = true; _vpnReady.TrySetResult(); }
                if (line.Contains("FATAL", StringComparison.Ordinal) || line.StartsWith("HOST_ERROR:", StringComparison.Ordinal) ||
                    line.Contains("client terminated:", StringComparison.Ordinal) && line.Contains("terminal", StringComparison.Ordinal))
                {
                    var error = new IOException(line); _ready.TrySetException(error);
                    if (!_vpnReady.TrySetException(error) && !_disposing) Failed?.Invoke(line);
                }
            }
            if (!_disposing && !_closingWorker) Fail(new IOException("主核心后台进程已退出。"));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { if (!_disposing && !_closingWorker) Fail(error); }
    }
    private void Fail(Exception error)
    {
        IsRunning = false; IsVpnConnected = false;
        _ready.TrySetException(error); _vpnReady.TrySetException(error); _stopped.TrySetException(error); Failed?.Invoke(error.Message);
    }
    private async Task StopCycleAsync()
    {
        if (!_cycleActive) return;
        await _writer!.WriteLineAsync("STOP"); await _writer.FlushAsync();
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(30)); IsRunning = false; IsVpnConnected = false;
    }
    private async Task CloseWorkerAsync()
    {
        _closingWorker = true;
        try
        {
            if (_writer is not null) { await _writer.WriteLineAsync("EXIT"); await _writer.FlushAsync(); } else _pipe?.Dispose();
            if (_worker is not null) await _worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException) { }
        finally
        {
            _pipe?.Dispose(); if (_logs is not null) { try { await _logs; } catch (IOException) { } }
            _reader?.Dispose(); try { _writer?.Dispose(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            _job?.Dispose(); _job = null; _worker?.Dispose(); _worker = null; _reader = null; _writer = null; _pipe = null; _logs = null;
            _cycleActive = false; IsRunning = false; IsVpnConnected = false; CoreProcessId = null;
            _closingWorker = false;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposing) return; _disposing = true;
            try { await StopCycleAsync(); } catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException) { }
            await CloseWorkerAsync();
        }
        finally { _operations.Release(); }
    }
}

public static class CoreWorker
{
    public static async Task<int> RunAsync(string pipeName)
    {
        if (!Regex.IsMatch(pipeName, @"\AVPNGateXray\.Core\.[0-9a-f]{32}\z")) return 1;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(20000); } catch (TimeoutException) { return 1; }
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        while (true)
        {
            string? directory = null;
            try
            {
                var line = await reader.ReadLineAsync(); if (line is null || line == "EXIT") return 0;
                var request = JsonSerializer.Deserialize<CoreStartRequest>(line) ?? throw new InvalidDataException("缺少主核心配置。");
                if (request.TunEnabled && !EngineRuntime.IsAdministrator) throw new InvalidOperationException("TUN 需要管理员权限。");
                request.Settings.Validate(); if (EngineRuntime.IsAdministrator) await SystemDnsLease.RecoverStaleAsync();
                directory = Path.Combine(AppDataPaths.Sessions, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory); AppDataPaths.RestrictDirectory(directory);
                var path = Path.Combine(directory, "config.json");
                var config = CoreConfig.Build(request.Node, request.Settings, request.ListenAddress, request.ListenPort,
                    request.Control, request.ProbePort, request.TunEnabled);
                await File.WriteAllTextAsync(path, CoreConfig.Serialize(config), new UTF8Encoding(false));
                await EngineHost.RunAsync(EngineRuntime.CorePath, path, reader, writer);
            }
            catch (Exception error) { try { await writer.WriteLineAsync("HOST_ERROR: " + error.Message); } catch (IOException) { return 1; } }
            finally { if (directory is not null) { try { File.Delete(Path.Combine(directory, "config.json")); Directory.Delete(directory); } catch (IOException) { } } }
            try { await writer.WriteLineAsync("CORE_STOPPED"); } catch (IOException) { return 0; }
        }
    }
}
