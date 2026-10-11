using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VPNGate.Core;

namespace VPNGate.Desktop;

public partial class MainWindow
{
    private async Task RunPoolSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory); var saved = _settings.Copy();
        var report = new Dictionary<string, object>();
        try
        {
            _settings.ConnectionTimeoutSeconds = 15; _settings.ProbeTimeoutSeconds = 5; _settings.ProbeSamples = 1;
            FillSettings(_settings);
            await EnsureCoreAsync(_settings, _poolCts.Token);
            _fetchTask = RefreshPoolAsync(true); await _fetchTask;
            if (_nodes.Count == 0 || _nodes.Any(n => !NodePoolPolicy.IsVerified(n))) throw new IOException("The pool did not retain exclusively verified real nodes.");
            report["verifiedRealNodes"] = _nodes.Count; report["hourlyRefreshAt"] = _nextPoolRefresh;
            NodeGrid.SelectedItem = _nodes.OrderBy(n => n.LatencyMs).First();
            _connectTask = ConnectSelectedAsync(); await _connectTask;
            if (_session?.IsVpnConnected != true) throw new IOException("A verified pool node did not connect in the unified core.");
            var core = _session;
            report["connectedNode"] = core.Node!.Ip;
            TunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await _tunTask!;
            if (!core.TunEnabled || !core.IsVpnConnected) throw new IOException("TUN and OpenVPN did not start together in the same core.");
            GlobalModeButton.IsChecked = true; if (_routingTask is not null) await _routingTask;
            var proxyExit = await ProbeService.ExitIpAsync(core.ProbePort, CancellationToken.None);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
            var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var tunExit = trace.Split('\n').First(l => l.StartsWith("ip=", StringComparison.Ordinal))[3..];
            if (proxyExit != tunExit) throw new IOException("TUN does not use the same core's OpenVPN endpoint.");
            report["tunAndVpnExitMatch"] = true; report["mainCoreProcessId"] = core.CoreProcessId!;
            SaveScreenshot(Path.Combine(directory, "unified-pool-connected.png"));
            await core.SetTunEnabledAsync(false); _tunRequested = false; UpdateControls();
            if (!core.IsVpnConnected || !core.IsRunning || core.TunEnabled) throw new IOException("The unified core did not reconnect correctly after disabling TUN.");
            report["unifiedToggleReconnected"] = true;
            await core.DisconnectVpnAsync(); ResetConnectionUi(); UpdateControls();
            if (!core.IsRunning || core.IsVpnConnected) throw new IOException("Disconnecting the node unexpectedly closed the local proxy.");
            report["localProxySurvivesDisconnect"] = true;
            report["success"] = true;
        }
        catch (Exception error) { report["success"] = false; report["error"] = error.ToString(); }
        finally
        {
            await StopTunAsync(dispose: true); _settings = saved; _store.SaveSettings(saved);
            await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
