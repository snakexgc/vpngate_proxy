using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VPNGate.Core;

public sealed class TunSession : IAsyncDisposable
{
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _worker;
    private Task? _logs;
    private bool _disposing;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _cycleActive;
    public bool IsRunning { get; private set; }
    public int? WorkerProcessId => _worker?.Id;
    public event Action<string>? Log;
    public event Action<string>? Failed;

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_disposing || _cycleActive) throw new InvalidOperationException("此 TUN 会话已经启动或已关闭。");
        settings.Validate();
        await Task.Run(() => EngineRuntime.Verify(EngineRuntime.CorePath), cancellationToken);
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(45));
        if (_pipe is null)
        {
        var pipeName = "VPNGateXray.Tun." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        if (!EngineRuntime.IsAdministrator) start.Verb = "runas";
        start.ArgumentList.Add("--tun-worker"); start.ArgumentList.Add(pipeName);
        _worker = await Task.Run(() => Process.Start(start), CancellationToken.None) ?? throw new IOException("无法启动 TUN 进程。");
        var connection = _pipe.WaitForConnectionAsync(startup.Token);
        var exit = _worker.WaitForExitAsync();
        if (await Task.WhenAny(connection, exit) == exit && !_pipe.IsConnected)
            throw new IOException("TUN 进程在建立控制通道前退出。");
        await connection;
        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        // Never let a continuously readable pipe keep the WPF dispatcher busy.
        _logs = Task.Run(ReadLogsAsync);
        }
        _cycleActive = true;
        var target = settings.Copy(); target.UpstreamProxy = settings.Validate().SafeAddress;
        target.Password = ""; target.Username = ""; target.ProtectedPassword = "";
        await _writer!.WriteLineAsync(JsonSerializer.Serialize(target));
        await _ready.Task.WaitAsync(startup.Token);
        IsRunning = true;
    }
    private async Task ReadLogsAsync()
    {
        try
        {
            while (await _reader!.ReadLineAsync() is { } line)
            {
                if (line == "TUN_READY") _ready.TrySetResult();
                else if (line == "TUN_STOPPED")
                {
                    IsRunning = false; _cycleActive = false;
                    _stopped.TrySetResult();
                    _ready.TrySetException(new IOException("TUN 在完成启动前停止。"));
                }
                else Log?.Invoke(line);
                if (line.Contains("FATAL", StringComparison.Ordinal) || line.StartsWith("HOST_ERROR:", StringComparison.Ordinal))
                    _ready.TrySetException(new IOException(line));
            }
            if (!_disposing)
            {
                IsRunning = false;
                var error = "TUN 进程已退出。";
                _ready.TrySetException(new IOException(error)); _stopped.TrySetException(new IOException(error)); Failed?.Invoke(error);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            if (!_disposing) { IsRunning = false; _ready.TrySetException(exception); _stopped.TrySetException(exception); Failed?.Invoke(exception.Message); }
        }
    }
    public async Task StopAsync()
    {
        if (!_cycleActive) return;
        await _writer!.WriteLineAsync("STOP"); await _writer.FlushAsync();
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
        IsRunning = false;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposing) return;
        _disposing = true;
        try
        {
            if (_writer is not null)
            {
                await StopAsync();
                await _writer.WriteLineAsync("EXIT"); await _writer.FlushAsync();
            }
            else _pipe?.Dispose();
            if (_worker is not null) await _worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException) { }
        finally
        {
            IsRunning = false;
            _pipe?.Dispose();
            if (_logs is not null) { try { await _logs; } catch (IOException) { } }
            _reader?.Dispose();
            try { _writer?.Dispose(); } catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            _worker?.Dispose();
        }
    }
}

public static class TunWorker
{
    public static async Task<int> RunAsync(string pipeName)
    {
        if (!Regex.IsMatch(pipeName, @"\AVPNGateXray\.Tun\.[0-9a-f]{32}\z") || !EngineRuntime.IsAdministrator) return 1;
        // The server restricts access to the current user's SID. The elevated token may have a different default owner,
        // so the client must not apply CurrentUserOnly's additional pipe-owner comparison.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(20000); } catch (TimeoutException) { return 1; }
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        while (true)
        {
            string? directory = null;
            try
            {
            var json = await reader.ReadLineAsync();
            if (json is null || json == "EXIT") return 0;
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidDataException("缺少 TUN 配置。");
            settings.Validate(); await SystemDnsLease.RecoverStaleAsync();
            directory = Path.Combine(AppDataPaths.Sessions, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); AppDataPaths.RestrictDirectory(directory);
            var config = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(config, CoreConfig.Serialize(CoreConfig.BuildTun(settings, CoreConfig.PhysicalInterface())), new UTF8Encoding(false));
            await EngineHost.RunAsync(EngineRuntime.CorePath, config, reader, writer);
            }
            catch (Exception exception)
            {
                try { await writer.WriteLineAsync($"HOST_ERROR: {exception.Message}"); } catch (IOException) { return 1; }
            }
            finally
            {
                if (directory is not null) { try { File.Delete(Path.Combine(directory, "config.json")); Directory.Delete(directory); } catch (IOException) { } }
            }
            try { await writer.WriteLineAsync("TUN_STOPPED"); } catch (IOException) { return 0; }
        }
    }
}
