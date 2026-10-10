using System.IO;
using System.Windows;
using System.Diagnostics;
using VPNGate.Core;

namespace VPNGate.Desktop;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--tun-worker", var pipeName])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await TunWorker.RunAsync(pipeName)); return;
        }
        if (e.Args is ["--dns-guard", var pid, var ticks, var identity])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SystemDnsLease.GuardAsync(int.Parse(pid), long.Parse(ticks), identity));
            return;
        }
        if (e.Args.Contains("--recover-dns", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await SystemDnsLease.RecoverStaleAsync(); Shutdown(); return;
        }
        if (e.Args is ["--engine-host", var core, var config])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await EngineHost.RunAsync(core, config));
            return;
        }
        if (SystemDnsLease.NeedsRecovery)
        {
            try
            {
                if (EngineRuntime.IsAdministrator) await SystemDnsLease.RecoverStaleAsync();
                else
                {
                    var recover = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    recover.ArgumentList.Add("--recover-dns");
                    using var recovery = Process.Start(recover);
                    if (recovery is not null) await recovery.WaitForExitAsync();
                }
            }
            catch (Exception exception) { MessageBox.Show($"上一次 TUN 的临时 DNS 策略需要管理员权限恢复：{exception.Message}", "vpngate_proxy DNS 恢复"); }
        }
        var smokeIndex = Array.IndexOf(e.Args, "--ui-smoke");
        if (smokeIndex < 0)
        {
            _mutex = new Mutex(false, $"Local\\VPNGateXray.Gui.{Environment.UserName}.{EngineRuntime.IsAdministrator}");
            try { _ownsMutex = _mutex.WaitOne(0); } catch (AbandonedMutexException) { _ownsMutex = true; }
            if (!_ownsMutex) { MessageBox.Show("vpngate_proxy 已在运行。", "vpngate_proxy"); Shutdown(); return; }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Directory.CreateDirectory(AppDataPaths.Logs);
            File.AppendAllText(Path.Combine(AppDataPaths.Logs, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n");
            MessageBox.Show(args.Exception.Message, "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        MainWindow = new MainWindow(e.Args);
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
