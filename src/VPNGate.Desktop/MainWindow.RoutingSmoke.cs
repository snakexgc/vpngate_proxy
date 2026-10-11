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
    private async Task RunRoutingSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var original = _settings.Copy();
        var checks = new Dictionary<string, object>();
        try
        {
            RulesExpander.IsExpanded = true;
            _ruleUpdateTask = UpdateRoutingRulesAsync(true); await _ruleUpdateTask;
            if (_routingRules.State.LastChecked is null || _routingRules.State.Error.Length > 0) throw new IOException("Real rule sources could not be updated.");
            checks["realRuleSourcesChecked"] = true;
            async Task SelectMode(RadioButton button, string mode)
            {
                if (!button.IsEnabled) throw new IOException("Routing mode controls are disabled.");
                button.IsChecked = true;
                if (_routingTask is not null) await _routingTask;
                if (_settings.RoutingMode != mode) throw new IOException("Routing mode did not change.");
            }
            await SelectMode(DirectModeButton, "direct");
            TunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await _tunTask!;
            if (_session?.TunEnabled != true || _session?.IsVpnConnected == true) throw new IOException("Direct TUN requires no OpenVPN session.");
            var worker = _session!.HostProcessId;
            async Task<bool> SystemRequest(string url, int timeout = 12)
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(timeout) }) { Timeout = TimeSpan.FromSeconds(timeout) };
                try { using var result = await client.GetAsync(url); return result.IsSuccessStatusCode; }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return false; }
            }
            if (!await SystemRequest("https://www.baidu.com/")) throw new IOException("Direct TUN could not load a real domestic website without OpenVPN.");
            checks["directTrafficWithoutVpn"] = true;
            await SelectMode(RuleModeButton, "rule");
            if (!await SystemRequest("https://www.baidu.com/")) throw new IOException("Rule-mode domestic traffic did not go directly.");
            checks["domesticRulesWithoutVpn"] = true;
            await SelectMode(GlobalModeButton, "global");
            if (await SystemRequest("http://1.1.1.1/cdn-cgi/trace", 3)) throw new IOException("Global traffic fell back to direct without OpenVPN.");
            checks["globalHasNoDirectFallback"] = true;
            if (_session.HostProcessId != worker || !_session.TunEnabled) throw new IOException("Switching modes restarted TUN.");
            checks["modeChangesKeepTunRunning"] = true;
            await SelectMode(RuleModeButton, "rule");
            await _session.SetTunEnabledAsync(false); UpdateControls();
            RulesExpander.IsExpanded = true;
            SettingsPanel.BringIntoView();
            SaveScreenshot(Path.Combine(directory, "routing-modes.png"));
            checks["success"] = true;
        }
        catch (Exception exception) { checks["success"] = false; checks["error"] = exception.ToString(); }
        finally
        {
            await StopTunAsync(dispose: true);
            _settings = original; SetRoutingModeUi(original.RoutingMode); _store.SaveSettings(original);
            await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
}
