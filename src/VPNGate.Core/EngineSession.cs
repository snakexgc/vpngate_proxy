using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VPNGate.Core;

public static class EngineRuntime
{
    public const string CoreHash = "7bbef1dea9189ee12799ae834ea4b4658355da25c47a21ad8804904c0ccd9410";
    public const string WintunHash = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce";
    private static readonly HashSet<string> Verified = new(StringComparer.OrdinalIgnoreCase);
    public static string CorePath => Path.Combine(AppContext.BaseDirectory, "runtime", "sing-box.exe");
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void Verify(string path)
    {
        lock (Verified)
        {
            if (Verified.Contains(path)) return;
            if (!File.Exists(path)) throw new FileNotFoundException("缺少 sing-box 内核，请运行 scripts/setup-runtime.ps1 后重新构建。", path);
            using var file = File.OpenRead(path);
            if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(CoreHash, StringComparison.Ordinal))
                throw new InvalidDataException("sing-box 内核校验失败，请重新下载受支持的 1.14.2 版本。");
            var dll = Path.Combine(Path.GetDirectoryName(path)!, "wintun.dll");
            if (File.Exists(dll))
            {
                using var driver = File.OpenRead(dll);
                if (Convert.ToHexStringLower(SHA256.HashData(driver)) != WintunHash) throw new InvalidDataException("Wintun 驱动校验失败。");
            }
            Verified.Add(path);
        }
    }
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}

public sealed class EngineSession : IAsyncDisposable
{
    private Process? _host;
    private ProcessJob? _job;
    private Task? _logs;
    private string? _directory;
    private bool _disposing;
    private readonly Queue<string> _recent = new();
    private readonly TaskCompletionSource _tunnelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<string>? Log;
    public event Action<string>? Failed;
    public VpnNode? Node { get; private set; }
    public int Port { get; private set; }
    public string ProxyAddress => $"http://127.0.0.1:{Port}";
    public bool IsRunning => _host is { HasExited: false } && !_disposing;
    public int? HostProcessId => _host?.Id;
    public string RecentError { get { lock (_recent) return string.Join("\n", _recent.TakeLast(4)); } }

    public async Task StartAsync(VpnNode node, AppSettings settings, string listenAddress, int listenPort, CancellationToken cancellationToken)
    {
        if (_host is not null) throw new InvalidOperationException("此会话已经启动。");
        EngineRuntime.Verify(EngineRuntime.CorePath);
        settings.Validate();
        var ip = IPAddress.Parse(listenAddress);
        var check = new TcpListener(ip, listenPort);
        try { check.Server.ExclusiveAddressUse = true; check.Start(); }
        catch (SocketException) { throw new IOException($"本地代理端口 {listenPort} 已被占用，请换一个端口。"); }
        finally { check.Stop(); }
        Node = node; Port = listenPort;
        _directory = Path.Combine(AppDataPaths.Sessions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        AppDataPaths.RestrictDirectory(_directory);
        var configPath = Path.Combine(_directory, "config.json");
        var config = CoreConfig.Build(node, settings, listenAddress, listenPort);
        await File.WriteAllTextAsync(configPath, CoreConfig.Serialize(config), new UTF8Encoding(false), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "--engine-host", EngineRuntime.CorePath, configPath }) start.ArgumentList.Add(arg);
        _job = new ProcessJob();
        _host = Process.Start(start) ?? throw new IOException("无法启动 VPN 内核宿主。");
        try { _job.Add(_host); } catch { _host.Kill(true); throw; }
        _logs = Task.WhenAll(ReadLogsAsync(_host.StandardOutput), ReadLogsAsync(_host.StandardError));
        _ = ObserveExitAsync();
        using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyTimeout.CancelAfter(TimeSpan.FromSeconds(settings.ConnectionTimeoutSeconds));
        try { await _tunnelReady.Task.WaitAsync(readyTimeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"OpenVPN 连接超过 {settings.ConnectionTimeoutSeconds} 秒。{RecentError}"); }
    }

    private async Task ReadLogsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_recent) { _recent.Enqueue(line); while (_recent.Count > 16) _recent.Dequeue(); }
            Log?.Invoke(line);
            if (line.Contains("tunnel established", StringComparison.OrdinalIgnoreCase)) _tunnelReady.TrySetResult();
            if (line.Contains("FATAL", StringComparison.Ordinal) || line.StartsWith("HOST_ERROR:", StringComparison.Ordinal))
                _tunnelReady.TrySetException(new IOException(line));
            if (line.Contains("client terminated:", StringComparison.Ordinal) && line.Contains("terminal", StringComparison.Ordinal))
            {
                if (!_tunnelReady.TrySetException(new IOException(line)) && !_disposing) Failed?.Invoke(line);
            }
        }
    }
    private async Task ObserveExitAsync()
    {
        var host = _host;
        if (host is null) return;
        try
        {
            await host.WaitForExitAsync();
            if (_logs is not null) await _logs;
            if (!_disposing)
            {
                var error = $"VPN 内核已退出（代码 {host.ExitCode}）。{RecentError}";
                _tunnelReady.TrySetException(new IOException(error)); Failed?.Invoke(error);
            }
        }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposing) return;
        _disposing = true;
        if (_host is not null)
        {
            try
            {
                if (!_host.HasExited)
                {
                    await _host.StandardInput.WriteLineAsync("STOP"); await _host.StandardInput.FlushAsync();
                    await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7));
                }
            }
            catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { }
            _job?.Dispose(); _job = null;
            if (_logs is not null) { try { await _logs.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { } }
            _host.Dispose(); _host = null;
        }
        _job?.Dispose();
        if (_directory is not null)
        {
            // Only this session's generated config is removed; no other applications are touched.
            try { File.Delete(Path.Combine(_directory, "config.json")); Directory.Delete(_directory); } catch (IOException) { }
        }
    }
}
