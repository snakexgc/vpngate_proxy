using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VPNGate.Core;

namespace VPNGate.Desktop;

public partial class MainWindow
{
    private readonly RoutingRuleSets _routingRules = new();
    private readonly CancellationTokenSource _rulesCts = new();
    private Task? _routingTask, _rulesLoopTask, _ruleUpdateTask;
    private bool _routingBusy, _routingUiUpdating, _rulesBusy;
    private void SetRoutingModeUi(string mode)
    {
        _routingUiUpdating = true;
        try { GlobalModeButton.IsChecked = mode == "global"; RuleModeButton.IsChecked = mode == "rule"; DirectModeButton.IsChecked = mode == "direct"; }
        finally { _routingUiUpdating = false; }
    }
    private void RoutingMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _routingUiUpdating || _closing || _routingBusy) return;
        _routingTask = SwitchRoutingModeAsync((string)((RadioButton)sender).Tag);
    }
    private async Task SwitchRoutingModeAsync(string mode)
    {
        var previous = _settings.RoutingMode;
        if (previous == mode) return;
        var vpn = _session?.IsRunning == true ? _session : null;
        _routingBusy = true; UpdateControls();
        try
        {
            if (vpn is not null) await vpn.SetRoutingModeAsync(mode, _rulesCts.Token);
            var settings = _settings.Copy(); settings.RoutingMode = mode;
            _store.SaveSettings(settings); _settings = settings;
            Log($"已切换为{RoutingModes.Label(mode)}模式，本地代理与 TUN 使用同一分流策略，OpenVPN 连接保持运行。");
        }
        catch (Exception exception)
        {
            foreach (var rollback in new Func<Task>[] {
                () => vpn is null ? Task.CompletedTask : vpn.SetRoutingModeAsync(previous) })
                try { await rollback(); } catch (Exception error) { Log("恢复分流模式：" + error.Message); }
            SetRoutingModeUi(previous);
            if (!_closing) ShowError("切换分流模式失败：" + exception.Message);
        }
        finally { _routingBusy = false; UpdateControls(); }
    }
    private void UpdateRoutingControls()
    {
        RoutingModePanel.IsEnabled = !_closing && !_routingBusy && !_connectionBusy && !_tunBusy;
        var mode = _settings.RoutingMode;
        RoutingStateText.Text = _routingBusy ? "正在切换分流模式…" : mode switch {
            "global" => "所有公网流量经 OpenVPN；局域网和上游客户端连接直连。",
            "rule" => "国内域名 / IP 和局域网直连，其他流量经 OpenVPN。",
            _ => "所有流量经物理网卡直连，TUN 无需连接 OpenVPN。" };
        TrafficPathText.Text = mode switch {
            "global" => "应用 / TUN → OpenVPN 节点\n                   → VPN 上游代理",
            "rule" => "应用 / TUN → 国内 / 局域网直连\n               → 其他经 OpenVPN → 上游代理",
            _ => "应用 / TUN → 物理网卡 → 互联网" };
        UpdateRulesButton.IsEnabled = !_closing && !_rulesBusy;
        UpdateRulesButton.Content = _rulesBusy ? "正在更新规则…" : "立即更新规则";
        var state = _routingRules.State;
        RulesStatusText.Text = _rulesBusy ? "正在校验并更新，现有规则继续生效。"
            : state.Error.Length > 0 ? "更新失败，继续使用已有规则。\n" + state.Error
            : state.LastChecked is { } checkedAt ? $"最近检查：{checkedAt.ToLocalTime():MM-dd HH:mm}\n" +
                (state.LastUpdated is { } updated ? $"最近更新：{updated.ToLocalTime():MM-dd HH:mm}" : "内置规则与远程版本一致。")
            : "已内置中国域名和 IP 规则，等待检查更新。";
    }
    private void RulesPreferences_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closing) return;
        try
        {
            if (!int.TryParse(RuleHoursBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) || hours is < 1 or > 168)
                throw new ArgumentException("规则更新间隔须为 1–168 小时的整数。");
            var settings = _settings.Copy(); settings.AutoUpdateRules = AutoRulesBox.IsChecked == true; settings.RuleUpdateHours = hours;
            _store.SaveSettings(settings); _settings = settings;
            if (settings.AutoUpdateRules) _ruleUpdateTask = UpdateRoutingRulesAsync(false);
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }
    private void UpdateRules_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.IsEnabled && !TrySaveSettings(out _)) return;
        _ruleUpdateTask = UpdateRoutingRulesAsync(true);
    }
    private async Task UpdateRoutingRulesAsync(bool force)
    {
        if (_rulesBusy || _closing) return;
        _rulesBusy = true; UpdateRoutingControls();
        try
        {
            var state = await _routingRules.UpdateAsync(_settings.Copy(), force, _rulesCts.Token);
            if (force || state.LastChecked is not null) Log("分流规则已检查；有效更新由 sing-box 自动载入，无需重连节点或重启 TUN。");
        }
        catch (OperationCanceledException) when (_rulesCts.IsCancellationRequested) { }
        catch (Exception exception) { Log(exception.Message); if (force && !_closing) ShowError(exception.Message); }
        finally { _rulesBusy = false; if (!_closing) UpdateRoutingControls(); }
    }
    private async Task RunRulesLoopAsync()
    {
        await UpdateRoutingRulesAsync(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try { while (await timer.WaitForNextTickAsync(_rulesCts.Token)) await UpdateRoutingRulesAsync(false); }
        catch (OperationCanceledException) when (_rulesCts.IsCancellationRequested) { }
    }
}
