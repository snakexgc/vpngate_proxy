using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace VPNGate.Core;

// A uniquely owned temporary NRPT rule forces system DNS to the tunnel's loopback DNS listener.
// The guardian lives outside the UI's kill-on-close job, so an abrupt UI/core-host exit also removes the rule.
public sealed class SystemDnsLease : IAsyncDisposable
{
    private readonly string _identity = "VPNGateXray." + Guid.NewGuid().ToString("N");
    private readonly string _dnsServer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _activated;
    private static string LeaseDirectory => Path.Combine(AppDataPaths.Root, "dns-leases");
    private sealed record Owner(int ProcessId, long StartTicks, string Identity);
    public SystemDnsLease(string dnsServer) => _dnsServer = System.Net.IPAddress.Parse(dnsServer).ToString();
    public void StartGuardian()
    {
        var exe = Environment.ProcessPath!;
        using var self = Process.GetCurrentProcess();
        Directory.CreateDirectory(LeaseDirectory);
        AppDataPaths.RestrictDirectory(LeaseDirectory);
        File.WriteAllText(Path.Combine(LeaseDirectory, _identity + ".json"), JsonSerializer.Serialize(new Owner(self.Id, self.StartTime.ToUniversalTime().Ticks, _identity)));
        var command = new StringBuilder($"\"{exe}\" --dns-guard {self.Id} {self.StartTime.ToUniversalTime().Ticks} {_identity}");
        var info = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
        if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x01000000 | 0x08000000, IntPtr.Zero, AppContext.BaseDirectory, ref info, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 DNS 自动恢复守护进程。");
        CloseHandle(process.Thread); CloseHandle(process.Process);
    }
    public async Task ActivateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_activated) return;
            await PowerShellAsync($"Add-DnsClientNrptRule -Namespace '.' -NameServers '{_dnsServer}' -DisplayName 'VPNGate Xray temporary DNS' -Comment '{_identity}' | Out-Null; Clear-DnsClientCache");
            _activated = true;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        // Cleanup also runs after a partially successful Add-DnsClientNrptRule.
        await RemoveAsync(_identity);
        File.Delete(Path.Combine(LeaseDirectory, _identity + ".json"));
    }
    private static Task<string> RemoveAsync(string identity) => PowerShellAsync($"$owned = @(Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq '{identity}' }}); if ($owned.Count -gt 0) {{ $owned | Remove-DnsClientNrptRule -Force; Clear-DnsClientCache }}");
    private static IEnumerable<Owner> StaleOwners()
    {
        if (!Directory.Exists(LeaseDirectory)) yield break;
        foreach (var path in Directory.GetFiles(LeaseDirectory, "VPNGateXray.*.json"))
        {
            Owner? owner;
            try { owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(path)); } catch (Exception e) when (e is IOException or JsonException) { continue; }
            if (owner is null || !Regex.IsMatch(owner.Identity, @"\AVPNGateXray\.[0-9a-f]{32}\z")) continue;
            var alive = false;
            try { using var process = Process.GetProcessById(owner.ProcessId); alive = process.StartTime.ToUniversalTime().Ticks == owner.StartTicks && !process.HasExited; }
            catch (ArgumentException) { }
            catch (Win32Exception) { alive = true; }
            if (!alive) yield return owner;
        }
    }
    public static bool NeedsRecovery => StaleOwners().Any();
    public static async Task RecoverStaleAsync()
    {
        foreach (var owner in StaleOwners())
        {
            await RemoveAsync(owner.Identity);
            File.Delete(Path.Combine(LeaseDirectory, owner.Identity + ".json"));
        }
    }
    public static async Task<int> GuardAsync(int pid, long startTicks, string identity)
    {
        if (!Regex.IsMatch(identity, @"\AVPNGateXray\.[0-9a-f]{32}\z")) return 1;
        try
        {
            try
            {
                using var parent = Process.GetProcessById(pid);
                if (parent.StartTime.ToUniversalTime().Ticks == startTicks)
                {
                    var marker = Path.Combine(LeaseDirectory, identity + ".json");
                    while (!parent.HasExited && File.Exists(marker)) await Task.Delay(500);
                    if (!File.Exists(marker)) return 0;
                }
            }
            catch (ArgumentException) { }
            // Retry covers a policy installation command that was already completing when its host died.
            for (var i = 0; i < 3; i++)
            {
                if (i > 0) await Task.Delay(2000);
                await RemoveAsync(identity);
            }
            File.Delete(Path.Combine(LeaseDirectory, identity + ".json"));
            return 0;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(AppDataPaths.Logs);
            await File.AppendAllTextAsync(Path.Combine(AppDataPaths.Logs, "dns-recovery.log"), $"{DateTimeOffset.Now:O} {identity}: {exception}\n");
            return 1;
        }
    }
    public static Task<string> OwnedRuleCountAsync() => PowerShellAsync("@(Get-DnsClientNrptRule | Where-Object { $_.Comment -like 'VPNGateXray.*' }).Count");
    private static async Task<string> PowerShellAsync(string body)
    {
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " + body;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows DNS 配置命令。");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { process.Kill(true); throw new IOException("Windows DNS 配置命令超时。"); }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException(error.Trim());
        return output.Trim();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string directory, ref StartupInfo info, out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
