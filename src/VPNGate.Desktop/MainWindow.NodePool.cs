using System.Windows;
using VPNGate.Core;

namespace VPNGate.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource _poolCts = new();
    private CancellationTokenSource? _poolRoundCts;
    private Task? _poolLoopTask;
    private DateTimeOffset _nextPoolRefresh;
    private TaskCompletionSource? _poolRoundComplete;
    private async Task RunPoolLoopAsync()
    {
        _nextPoolRefresh = NodePoolPolicy.NextRefresh(_pool.LastRefresh, _pool.Nodes.Count, DateTimeOffset.UtcNow);
        try
        {
            while (!_poolCts.IsCancellationRequested)
            {
                var delay = _nextPoolRefresh - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) { await Task.Delay(delay, _poolCts.Token); continue; }
                if (_poolRoundComplete is { } pending) { await pending.Task.WaitAsync(_poolCts.Token); continue; }
                await RefreshPoolAsync(true);
                _nextPoolRefresh = (_pool.LastRefresh ?? DateTimeOffset.UtcNow) + NodePoolPolicy.RefreshInterval;
            }
        }
        catch (OperationCanceledException) when (_poolCts.IsCancellationRequested) { }
    }
    private async Task<ProbeResult> ProbePooledNodeAsync(VpnNode node, AppSettings settings, CancellationToken token)
    {
        var session = _session;
        if (session?.IsVpnConnected == true && session.Node?.Id == node.Id && session.Node.OpenVpnConfig == node.OpenVpnConfig && !_connectionBusy && !_tunBusy)
        {
            var samples = await ProbeService.MeasureAsync(session.ProbePort, settings, token);
            return new(node.Id, true, ProbeService.Median(samples), null, "", DateTimeOffset.Now, Samples: samples);
        }
        return await ProbeService.TestNodeAsync(node, settings, null, token);
    }
    private async Task RefreshPoolAsync(bool fetch, IReadOnlyCollection<string>? selected = null)
    {
        if (_closing || _fetching || _testing) return;
        _poolRoundCts = CancellationTokenSource.CreateLinkedTokenSource(_poolCts.Token);
        _poolRoundComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (fetch) _fetching = true; else _testing = true;
        UpdateControls();
        try
        {
            var settings = _settings.Copy();
            var round = await _pool.RefreshAsync(settings, progress => Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                _fetching = progress.Phase == "fetch"; _testing = progress.Phase == "test";
                TestProgress.Value = progress.Total > 0 ? 100d * progress.Completed / progress.Total : 0;
                ProgressText.Text = progress.Phase == "fetch" ? "正在刷新节点池…"
                    : $"后台验证 {progress.Completed}/{progress.Total} · {progress.Concurrency} 路并发 · 已通过 {progress.Available}";
                UpdateControls();
            }), _poolRoundCts.Token, fetch, selected);
            if (round is null) return;
            var selectedId = (NodeGrid.SelectedItem as VpnNode)?.Id ?? _settings.SelectedNodeId;
            _nodes.Clear(); foreach (var node in round.Nodes) _nodes.Add(node);
            _store.SaveNodes(_nodes); if (fetch && _pool.LastRefresh is { } refresh) _store.SavePoolRefresh(refresh);
            if (fetch) _nextPoolRefresh = (_pool.LastRefresh ?? DateTimeOffset.UtcNow) + NodePoolPolicy.RefreshInterval;
            UpdateCountries(); ApplySort();
            NodeGrid.SelectedItem = _nodes.FirstOrDefault(n => n.Id == selectedId) ?? _view.Cast<VpnNode>().FirstOrDefault();
            Log($"节点池已验证 {round.Candidates} 个候选，保留 {_nodes.Count} 个可用节点，剔除 {round.Removed} 个旧节点，并发 {round.Concurrency}。");
            if (round.FetchError.Length > 0) Log("本次 API 请求失败，已复测已有池：" + round.FetchError);
            ProgressText.Text = $"节点池 {_nodes.Count} 个可用 · 每小时自动刷新 · 下次 {_nextPoolRefresh.ToLocalTime():HH:mm}";
            _ipInfoCts?.Cancel(); _ipInfoTask = RefreshNodeTypesAsync(_nodes.ToArray(), settings);
            if (_session?.IsVpnConnected == true && _session.Node is { } active && !_nodes.Any(n => n.Id == active.Id))
            { Log("当前节点未通过本轮验证，已从池中移除并停止节点连接。"); await DisconnectSessionAsync(); }
        }
        catch (OperationCanceledException) when (_poolRoundCts.IsCancellationRequested) { Log("节点池验证已取消，保留上一轮可用节点。"); }
        catch (Exception error) { Log("节点池刷新失败，保留上一轮结果：" + error.Message); }
        finally
        {
            _fetching = false; _testing = false; _poolRoundCts.Dispose(); _poolRoundCts = null;
            _poolRoundComplete.TrySetResult(); _poolRoundComplete = null; UpdateControls();
        }
    }
    private void TestAll_Click(object sender, RoutedEventArgs e) => _testTask = RefreshPoolAsync(false);
    private void TestSelected_Click(object sender, RoutedEventArgs e) => _testTask = RefreshPoolAsync(false, NodeGrid.SelectedItems.Cast<VpnNode>().Select(n => n.Id).ToArray());
    private Task TestNodesAsync(IReadOnlyList<VpnNode> nodes) => RefreshPoolAsync(false, nodes.Select(n => n.Id).ToArray());
    private void CancelTests_Click(object sender, RoutedEventArgs e) { _poolRoundCts?.Cancel(); ProgressText.Text = "正在取消并清理后台验证会话…"; }
}
