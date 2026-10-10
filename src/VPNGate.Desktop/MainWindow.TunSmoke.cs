using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VPNGate.Core;

namespace VPNGate.Desktop;

public partial class MainWindow
{
    private async Task RunTunStartupSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, object>();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        var watch = Stopwatch.StartNew();
        var previousTick = watch.Elapsed.TotalMilliseconds;
        double maxGap = 0;
        var ticks = 0;
        heartbeat.Tick += (_, _) =>
        {
            var now = watch.Elapsed.TotalMilliseconds;
            maxGap = Math.Max(maxGap, now - previousTick); previousTick = now; ticks++;
        };
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            previousTick = watch.Elapsed.TotalMilliseconds; heartbeat.Start();
            if (_arguments.Contains("--log-ui", StringComparer.Ordinal))
            {
                // Replays the log volume from a disconnected TUN without changing the machine's network.
                var flood = Task.Run(() =>
                {
                    for (var i = 0; i < 6000; i++)
                        Log($"[TUN] ERROR [{i}] connection: dial tcp 127.0.0.1:10800: target actively refused it.");
                });
                await flood.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Delay(350);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                checks["replayedLines"] = 6000;
            }
            else
            {
                var v2ray = Process.GetProcessesByName("v2rayN").Select(p => p.Id).Order().ToArray();
                var xray = Process.GetProcessesByName("xray").Select(p => p.Id).Order().ToArray();
                var beforeRules = await SystemDnsLease.OwnedRuleCountAsync();
                int? worker = null;
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    if (_session is not null || !TunButton.IsEnabled) throw new IOException("Standalone TUN button is unavailable.");
                    TunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await _tunTask!;
                    if (_tunSession?.IsRunning != true || _session is not null || !TunButton.IsEnabled)
                        throw new IOException("TUN did not start without an OpenVPN node.");
                    worker ??= _tunSession.WorkerProcessId;
                    if (_tunSession.WorkerProcessId != worker) throw new IOException("The authorized TUN worker was not reused.");
                    await Task.Delay(3000);
                    if (cycle == 0) SaveScreenshot(Path.Combine(directory, "tun-without-openvpn.png"));
                    TunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await _tunTask!;
                    if (_tunSession.IsRunning || !TunButton.IsEnabled) throw new IOException("Standalone TUN did not stop.");
                }
                await StopTunAsync(dispose: true);
                if (System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == CoreConfig.TunName))
                    throw new IOException("The test's TUN adapter was not removed.");
                if (await SystemDnsLease.OwnedRuleCountAsync() != beforeRules) throw new IOException("The test's DNS policy was not removed.");
                if (!v2ray.SequenceEqual(Process.GetProcessesByName("v2rayN").Select(p => p.Id).Order()) ||
                    !xray.SequenceEqual(Process.GetProcessesByName("xray").Select(p => p.Id).Order()))
                    throw new IOException("An existing upstream process was changed.");
                checks["standaloneToggleCycles"] = 3; checks["workerReused"] = true;
                checks["adapterAndDnsRestored"] = true; checks["upstreamProcessesPreserved"] = true;
            }
            checks["heartbeatTicks"] = ticks; checks["maxUiGapMs"] = maxGap;
            if (ticks < 3 || maxGap > 1000) throw new IOException($"UI stopped responding for {maxGap:0} ms.");
            checks["success"] = true;
        }
        catch (Exception exception)
        {
            checks["success"] = false; checks["error"] = exception.ToString();
            checks["heartbeatTicks"] = ticks; checks["maxUiGapMs"] = maxGap;
        }
        finally
        {
            heartbeat.Stop();
            await StopTunAsync(dispose: true);
            await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
