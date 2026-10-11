using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using VPNGate.Core;

namespace VPNGate.Desktop;

public partial class MainWindow
{
    private async Task EnsureCoreAsync(AppSettings settings, CancellationToken token)
    {
        if (_session is null)
        {
            var session = new EngineSession(); _session = session;
            session.Log += Log;
            session.Failed += error => Dispatcher.BeginInvoke(() =>
            {
                if (_session != session || _closing) return;
                Log(error); if (_connectionBusy) _connectCts?.Cancel(); else { ResetConnectionUi(); UpdateControls(); }
            });
        }
        if (!_session.IsRunning) await _session.StartAsync(null, settings, settings.ListenAddress, settings.ListenPort, token, _tunRequested);
    }
    private void Connect_Click(object sender, RoutedEventArgs e) => _connectTask = ConnectSelectedAsync();
    private async Task ConnectSelectedAsync()
    {
        if (_connectionBusy || _tunBusy || _closing || NodeGrid.SelectedItem is not VpnNode node || !TrySaveSettings(out var settings)) return;
        _connectionBusy = true; _connectCts = new(); UpdateControls();
        SetStatus("正在连接", "#FFF4DC", "#E5A52E");
        try
        {
            await EnsureCoreAsync(settings, _connectCts.Token);
            var watch = Stopwatch.StartNew();
            await _session!.ConfigureAsync(node, settings, settings.ListenAddress, settings.ListenPort, _tunRequested, _connectCts.Token);
            var ms = watch.Elapsed.TotalMilliseconds;
            var samples = await ProbeService.MeasureAsync(_session.ProbePort, settings, _connectCts.Token);
            node.Apply(new(node.Id, true, ProbeService.Median(samples), ms, "", DateTimeOffset.Now, Samples: samples));
            _connectedAt = DateTimeOffset.Now; LatencyText.Text = node.LatencyText;
            ConnectedNodeText.Text = node.Ip; ConnectedCountryText.Text = node.CountryLabel;
            LocalProxyText.Text = $"{settings.ListenAddress}:{settings.ListenPort}";
            SetStatus("已连接", "#EAF8F0", "#25A76E");
            Log($"已连接 {node.CountryLabel} {node.Ip}，OpenVPN、上游代理、本地入口和 TUN 由主核心统一处理。");
            try { ExitIpText.Text = "节点出口 IP：" + await ProbeService.ExitIpAsync(_session.ProbePort, _connectCts.Token); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { Log("出口查询：" + error.Message); }
        }
        catch (OperationCanceledException) when (_connectCts.IsCancellationRequested) { Log("连接已取消。"); ResetConnectionUi(); }
        catch (Exception error) { Log("连接失败：" + error.Message); ResetConnectionUi(); ShowError(error.Message); }
        finally { _connectionBusy = false; _connectCts.Dispose(); _connectCts = null; ApplySort(); UpdateControls(); }
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _connectCts?.Cancel(); if (_connectionBusy && _connectTask is not null) await _connectTask;
        await DisconnectSessionAsync(); UpdateControls();
    }
    private async Task DisconnectSessionAsync()
    {
        if (_session?.IsVpnConnected == true) await _session.DisconnectVpnAsync();
        ResetConnectionUi();
    }
    private void ResetConnectionUi()
    {
        if (_session?.IsVpnConnected == true && _session.Node is { } node)
        {
            ConnectedNodeText.Text = node.Ip; ConnectedCountryText.Text = node.CountryLabel;
            SetStatus("已连接", "#EAF8F0", "#25A76E"); return;
        }
        _connectedAt = null; LatencyText.Text = "—"; ExitIpText.Text = "节点出口 IP：—";
        ConnectedNodeText.Text = "等待连接"; ConnectedCountryText.Text = "从可用节点池选择节点";
        SetStatus("未连接", "#EFF3F8", "#8A98AB");
    }
    private void Tun_Click(object sender, RoutedEventArgs e)
    {
        if (_tunBusy) { if (!_tunStopping) _tunCts?.Cancel(); return; }
        _tunTask = ToggleTunAsync();
    }
    private async Task ToggleTunAsync()
    {
        if (_closing || _tunBusy) return;
        var enabled = _session?.TunEnabled != true;
        if (!TrySaveSettings(out var settings)) return;
        _tunBusy = true; _tunStopping = !enabled; _tunCts = new(); UpdateControls();
        try
        {
            if (_connectionBusy) { _connectCts?.Cancel(); if (_connectTask is not null) await _connectTask; }
            await EnsureCoreAsync(settings, _tunCts.Token);
            await _session!.ConfigureAsync(_session.Node, settings, settings.ListenAddress, settings.ListenPort, enabled, _tunCts.Token);
            _tunRequested = enabled;
            Log(enabled ? "主核心已开启 TUN，直接将流量送入同一核心的 OpenVPN 端点。" : "主核心已关闭 TUN，本地 HTTP/SOCKS 入口继续运行。");
            ResetConnectionUi();
        }
        catch (OperationCanceledException) when (_tunCts.IsCancellationRequested) { Log("TUN 配置已取消。"); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { Log("管理员授权已取消，恢复原核心配置。"); }
        catch (Exception error) { ShowError("TUN 配置失败：" + error.Message); }
        finally { _tunBusy = false; _tunStopping = false; _tunCts.Dispose(); _tunCts = null; UpdateControls(); }
    }
    private async Task StopTunAsync(bool dispose = false)
    {
        if (_session is not null)
        {
            if (dispose) { var session = _session; _session = null; await session.DisposeAsync(); }
            else if (_session.TunEnabled) await _session.SetTunEnabledAsync(false);
        }
        _tunRequested = false; UpdateTunControls();
    }
    private void UpdateTunControls()
    {
        if (!_loaded) return;
        var on = _session?.TunEnabled == true;
        TunButton.IsEnabled = !_closing && !_tunStopping;
        TunButton.Content = _tunBusy ? _tunStopping ? "正在关闭 TUN…" : "取消开启 TUN" : on ? "关闭 TUN" : "开启 TUN";
        TunStateText.Text = _tunBusy ? "正在重载统一核心配置，节点会重新握手。"
            : !on ? "TUN 已关闭，本地代理和节点共用一个核心。"
            : _settings.RoutingMode == "direct" ? "TUN 已开启，全部直连，无需节点。"
            : _session?.IsVpnConnected == true ? "TUN 与 OpenVPN 已在同一核心中运行。"
            : _settings.RoutingMode == "rule" ? "国内直连，其他流量等待节点。" : "公网流量等待节点。";
        ModeText.Text = $"{RoutingModes.Label(_settings.RoutingMode)} · " + (on ? "TUN + HTTP + SOCKS5" : "HTTP + SOCKS5");
    }
}
