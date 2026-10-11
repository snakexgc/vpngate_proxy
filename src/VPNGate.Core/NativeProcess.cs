using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VPNGate.Core;

public sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    public ProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new JobExtendedLimit { BasicLimitInformation = new JobBasicLimit { LimitFlags = 0x2000 | 0x800 } }; // KILL_ON_JOB_CLOSE, explicit BREAKAWAY_OK for DNS cleanup guardian
        if (!SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf<JobExtendedLimit>()))
        { _handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为 VPN 内核创建进程清理组。");
    }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle handle, int infoClass, ref JobExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle handle, IntPtr process);
}

public static class EngineHost
{
    private delegate bool CtrlHandler(uint type);
    private static readonly CtrlHandler Handler = _ => true;
    // A separate hidden console lets CTRL+C reach only our core, allowing normal TUN route/adapter cleanup.
    public static async Task<int> RunAsync(string core, string config, TextReader? controller = null, TextWriter? transport = null)
    {
        using var standardInput = controller is null ? new StreamReader(Console.OpenStandardInput(), Encoding.UTF8) : null;
        using var standardOutput = transport is null ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true } : null;
        var input = controller ?? standardInput!;
        var output = transport ?? standardOutput!;
        FreeConsole();
        if (!AllocConsole()) { await output.WriteLineAsync("HOST_ERROR: 无法创建内核控制台。"); return 1; }
        ShowWindow(GetConsoleWindow(), 0);
        SetConsoleCtrlHandler(Handler, true);
        try
        {
            EngineRuntime.Verify(core);
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(config));
            var tun = document.RootElement.GetProperty("inbounds").EnumerateArray().Any(i => i.GetProperty("type").GetString() == "tun");
            var dnsAddress = tun ? document.RootElement.GetProperty("inbounds").EnumerateArray().First(i => i.GetProperty("tag").GetString() == "system-dns").GetProperty("listen").GetString()! : "";
            await using var dnsLease = tun ? new SystemDnsLease(dnsAddress) : null;
            dnsLease?.StartGuardian();
            var start = new ProcessStartInfo(core) { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(core)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("--disable-color"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
            using var process = Process.Start(start) ?? throw new IOException("内核启动失败。");
            await output.WriteLineAsync($"CORE_PID:{process.Id}");
            using var coreJob = new ProcessJob();
            coreJob.Add(process);
            var outputGate = new SemaphoreSlim(1, 1);
            async Task Pump(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Contains("sing-box started", StringComparison.Ordinal))
                    {
                        try { if (dnsLease is not null) await dnsLease.ActivateAsync(); line += "\nCORE_READY"; }
                        catch (Exception exception) { line = $"HOST_ERROR: 无法为 TUN 配置临时 DNS 策略：{exception.Message}"; }
                    }
                    await outputGate.WaitAsync(); try { await output.WriteLineAsync(line); } finally { outputGate.Release(); }
                }
            }
            var stdout = Pump(process.StandardOutput);
            var stderr = Pump(process.StandardError);
            var exited = process.WaitForExitAsync();
            using var readStop = new CancellationTokenSource();
            var stop = input.ReadLineAsync(readStop.Token).AsTask();
            var first = await Task.WhenAny(exited, stop);
            if (first == stop && !process.HasExited)
            {
                GenerateConsoleCtrlEvent(0, 0);
                try { await exited.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await exited; }
            }
            if (!stop.IsCompleted)
            {
                readStop.Cancel();
                try { await stop; } catch (OperationCanceledException) { }
            }
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            try { await output.WriteLineAsync($"HOST_ERROR: {exception.Message}"); } catch (IOException) { }
            return 1;
        }
        finally { SetConsoleCtrlHandler(Handler, false); FreeConsole(); }
    }
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
