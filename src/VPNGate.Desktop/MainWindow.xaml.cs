using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VPNGate.Core;

namespace VPNGate.Desktop;

public partial class MainWindow : Window
{
    private readonly AppDataStore _store = new();
    private readonly IpNetworkInfoClient _ipInfo;
    private CancellationTokenSource? _ipInfoCts;
    private Task? _ipInfoTask;
    private readonly ObservableCollection<VpnNode> _nodes = [];
    private readonly string[] _arguments;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly object _logLock = new();
    private readonly Queue<string> _logLines = new();
    private readonly string _logPath;
    private EngineSession? _session;
    private TunSession? _tunSession;
    private CancellationTokenSource? _tunCts;
    private Task? _tunTask;
    private bool _tunBusy, _tunStopping;
    private CancellationTokenSource? _connectCts, _testCts, _fetchCts;
    private Task? _connectTask, _testTask, _fetchTask;
    private bool _loaded, _connectionBusy, _testing, _fetching, _closing, _canClose;
    private DateTimeOffset? _connectedAt;
    private string _sortProperty = "Score";
    private bool _descending = true;
    private AppSettings _settings;
    private sealed record CountryItem(string Code, string Label);

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        InitializeComponent();
        _ipInfo = new IpNetworkInfoClient(_store);
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_nodes);
        _view.Filter = Filter;
        NodeGrid.ItemsSource = _view;
        _settings = _store.LoadSettings();
        FillSettings(_settings);
        Directory.CreateDirectory(AppDataPaths.Logs);
        _logPath = Path.Combine(AppDataPaths.Logs, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        foreach (var old in Directory.GetFiles(AppDataPaths.Logs, "session-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(20))
            try { File.Delete(old); } catch (IOException) { }
        PrivilegeText.Text = EngineRuntime.IsAdministrator ? "管理员权限 · TUN 可直接启动" : "普通用户权限 · TUN 启动时请求授权";
        _timer.Tick += (_, _) =>
        {
            UptimeText.Text = _connectedAt is { } since ? $"已连接 {(DateTimeOffset.Now - since):hh\\:mm\\:ss}" : "";
            if (_testing) { _view.Refresh(); UpdateCount(); }
        };
        _timer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var node in _store.LoadNodes()) _nodes.Add(node);
        _ipInfo.ApplyCached(_nodes);
        UpdateCountries(); ApplySort();
        NodeGrid.SelectedItem = _nodes.FirstOrDefault(n => n.Id == _settings.SelectedNodeId) ?? _view.Cast<VpnNode>().FirstOrDefault();
        UpdateControls();
        Log("程序就绪。上游代理承载 OpenVPN 数据，本地 HTTP/SOCKS5 入口仅经 OpenVPN 出口。");
        if (_nodes.Count > 0) Log($"已载入 {_nodes.Count} 个缓存节点，可点击刷新获取当前列表。");
        var preview = Array.IndexOf(_arguments, "--ui-preview");
        if (preview >= 0 && preview + 1 < _arguments.Length)
        {
            await Dispatcher.InvokeAsync(() => SaveScreenshot(_arguments[preview + 1]), DispatcherPriority.ContextIdle);
            Close(); return;
        }
        if (_arguments.Contains("--node-type-preview", StringComparer.Ordinal))
        {
            _ipInfoTask = RefreshNodeTypesAsync(_nodes.ToArray(), _settings);
            await _ipInfoTask;
            var index = Array.IndexOf(_arguments, "--node-type-preview");
            if (index + 1 < _arguments.Length) SaveScreenshot(_arguments[index + 1]);
            Close(); return;
        }
        _ipInfoTask = RefreshNodeTypesAsync(_nodes.ToArray(), _settings);
        var smoke = Array.IndexOf(_arguments, "--ui-smoke");
        if (smoke >= 0)
        {
            await RunUiSmokeAsync(smoke + 1 < _arguments.Length ? _arguments[smoke + 1] : Path.Combine(AppDataPaths.Root, "ui-smoke"));
            return;
        }
        if (_arguments.Contains("--connect", StringComparer.Ordinal))
        {
            if (_nodes.Count == 0) { _fetchTask = FetchNodesAsync(); await _fetchTask; }
            _connectTask = ConnectSelectedAsync(); await _connectTask;
        }
    }

    private void FillSettings(AppSettings settings)
    {
        UpstreamBox.Text = settings.UpstreamProxy; UsernameBox.Text = settings.Username; PasswordBox.Password = settings.Password;
        ListenBox.Text = settings.ListenAddress; PortBox.Text = settings.ListenPort.ToString(CultureInfo.InvariantCulture);
        ConcurrencyBox.Text = settings.TestConcurrency.ToString(CultureInfo.InvariantCulture); TimeoutBox.Text = settings.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ProbeUrlBox.Text = settings.ProbeUrl;
    }
    private AppSettings ReadSettings()
    {
        static int Number(TextBox box, string name) => int.TryParse(box.Text.Trim(), out var value) ? value : throw new ArgumentException($"{name}必须为整数。");
        var settings = _settings.Copy();
        settings.UpstreamProxy = UpstreamBox.Text.Trim(); settings.Username = UsernameBox.Text; settings.Password = PasswordBox.Password;
        settings.ListenAddress = ListenBox.Text.Trim(); settings.ListenPort = Number(PortBox, "本地端口");
        settings.TestConcurrency = Number(ConcurrencyBox, "并发数"); settings.ConnectionTimeoutSeconds = Number(TimeoutBox, "连接超时");
        settings.ProbeUrl = ProbeUrlBox.Text.Trim(); settings.SelectedNodeId = (NodeGrid.SelectedItem as VpnNode)?.Id ?? "";
        var proxy = settings.Validate(); settings.UpstreamProxy = proxy.SafeAddress; settings.Username = proxy.Username; settings.Password = proxy.Password;
        return settings;
    }
    private bool TrySaveSettings(out AppSettings settings)
    {
        try { settings = ReadSettings(); _store.SaveSettings(settings); _settings = settings; return true; }
        catch (Exception exception) { settings = _settings; ShowError(exception.Message); return false; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveSettings(out var settings)) { FillSettings(settings); Log("设置已保存。代理密码由 Windows DPAPI 加密保存。"); FooterText.Text = "设置已保存"; }
    }
    private void Fetch_Click(object sender, RoutedEventArgs e) => _fetchTask = FetchNodesAsync();
    private async Task FetchNodesAsync()
    {
        if (_fetching || _testing || _closing || !TrySaveSettings(out var settings)) return;
        _fetching = true; _fetchCts = new(); UpdateControls();
        FooterText.Text = "正在通过上游代理获取 VPNGate 节点…";
        try
        {
            var nodes = await new VpnGateClient().FetchAsync(settings, _fetchCts.Token);
            var old = _nodes.ToDictionary(n => n.Id);
            var selectedId = (NodeGrid.SelectedItem as VpnNode)?.Id ?? settings.SelectedNodeId;
            _nodes.Clear();
            foreach (var node in nodes)
            {
                if (old.TryGetValue(node.Id, out var previous) && previous.OpenVpnConfig == node.OpenVpnConfig)
                { node.LatencyMs = previous.LatencyMs; node.ConnectMs = previous.ConnectMs; node.TestStatus = previous.TestStatus; node.Error = previous.Error; node.TestedAt = previous.TestedAt; }
                _nodes.Add(node);
            }
            _store.SaveNodes(_nodes); UpdateCountries(); ApplySort();
            _ipInfoCts?.Cancel();
            _ipInfoTask = RefreshNodeTypesAsync(_nodes.ToArray(), settings);
            NodeGrid.SelectedItem = _nodes.FirstOrDefault(n => n.Id == selectedId) ?? _view.Cast<VpnNode>().FirstOrDefault();
            Log($"已获取 {nodes.Count} 个节点，覆盖 {nodes.Select(n => n.CountryShort).Distinct().Count()} 个国家 / 地区。");
            FooterText.Text = $"节点更新于 {DateTime.Now:HH:mm:ss} · 来自 VPNGate 官方 HTTPS 接口";
        }
        catch (OperationCanceledException) when (_fetchCts.IsCancellationRequested) { Log("节点获取已取消。"); }
        catch (Exception exception) { ShowError($"获取节点失败：{exception.Message}，已保留缓存列表。"); }
        finally { _fetching = false; _fetchCts.Dispose(); _fetchCts = null; UpdateControls(); }
    }

    private async Task RefreshNodeTypesAsync(IReadOnlyList<VpnNode> nodes, AppSettings settings)
    {
        using var cancel = new CancellationTokenSource();
        _ipInfoCts = cancel;
        _ipInfo.ApplyCached(nodes);
        foreach (var node in nodes) node.NetworkInfoLoading = !node.IsVpnGatePublicRelay && (node.NetworkInfo is null || node.NetworkInfo.NeedsLookup(DateTimeOffset.UtcNow));
        try
        {
            await _ipInfo.LookupAsync(nodes.Where(n => !n.IsVpnGatePublicRelay).Select(n => n.Ip), settings, info => Dispatcher.Invoke(() =>
            {
                foreach (var node in nodes.Where(n => IpNetworkInfoClient.Canonical(n.Ip) == info.Ip))
                { node.NetworkInfo = info; node.NetworkInfoLoading = false; }
            }), cancel.Token);
            if (!_closing)
            {
                _view.Refresh(); UpdateCount(); _store.SaveNodes(_nodes);
                Log($"节点类型已更新：机房 {nodes.Count(n => n.IsDataCenter == true)}，住宅（推测） {nodes.Count(n => n.IsDataCenter == false)}，未知 {nodes.Count(n => n.IsDataCenter is null)}。");
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception exception) { Log($"节点类型查询未完成：{exception.Message}"); }
        finally
        {
            foreach (var node in nodes) node.NetworkInfoLoading = false;
            if (_ipInfoCts == cancel) _ipInfoCts = null;
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => _connectTask = ConnectSelectedAsync();
    private async Task ConnectSelectedAsync()
    {
        if (_connectionBusy || _closing || NodeGrid.SelectedItem is not VpnNode node || !TrySaveSettings(out var settings)) return;
        _connectionBusy = true; _connectCts = new();
        await DisconnectSessionAsync();
        SetStatus("正在连接", "#FFF4DC", "#E5A52E");
        ConnectedNodeText.Text = node.Ip; ConnectedCountryText.Text = $"{node.CountryLabel} · {node.HostName}";
        UpdateControls();
        var session = new EngineSession();
        _session = session;
        session.Log += line => Log(line);
        session.Failed += error => Dispatcher.BeginInvoke(async () =>
        {
            if (_session != session || _closing) return;
            Log(error);
            if (_connectionBusy) { _connectCts?.Cancel(); return; }
            await DisconnectSessionAsync(); SetStatus("连接中断", "#FFF0EE", "#DF6655"); UpdateControls();
        });
        try
        {
            var watch = Stopwatch.StartNew();
            await session.StartAsync(node, settings, settings.ListenAddress, settings.ListenPort, _connectCts.Token);
            var connectMs = watch.Elapsed.TotalMilliseconds;
            var samples = await ProbeService.MeasureAsync(session.Port, settings, _connectCts.Token, settings.ListenAddress);
            node.Apply(new(node.Id, true, ProbeService.Median(samples), connectMs, "", DateTimeOffset.Now, Samples: samples));
            _connectedAt = DateTimeOffset.Now;
            LatencyText.Text = node.LatencyText; LocalProxyText.Text = $"{settings.ListenAddress}:{settings.ListenPort}";
            UpdateTunControls();
            SetStatus("已连接", "#EAF8F0", "#25A76E");
            Log($"已连接 {node.CountryDisplay} {node.Ip}，实际网络延时 {node.LatencyText}，本地端口 {settings.ListenPort}。");
            ProgressText.Text = $"已连接 {node.HostName} · {node.CountryLabel}";
            FooterText.Text = $"已连接 · {node.CountryLabel} · {node.Ip} · 本地端口 {settings.ListenPort}";
            try { ExitIpText.Text = "出口 IP：" + await ProbeService.ExitIpAsync(session.Port, _connectCts.Token, settings.ListenAddress); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { Log($"出口 IP 查询暂不可用：{exception.Message}"); }
            _store.SaveNodes(_nodes);
        }
        catch (OperationCanceledException) when (_connectCts.IsCancellationRequested)
        { await DisconnectSessionAsync(); SetStatus("已取消", "#EFF3F8", "#8A98AB"); Log("连接已取消。"); }
        catch (Exception exception)
        {
            node.Apply(new(node.Id, false, null, null, exception.Message, DateTimeOffset.Now));
            await DisconnectSessionAsync(); SetStatus("连接失败", "#FFF0EE", "#DF6655"); ShowError(exception.Message);
        }
        finally { _connectionBusy = false; _connectCts.Dispose(); _connectCts = null; ApplySort(); UpdateControls(); }
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _connectCts?.Cancel();
        if (_connectionBusy && _connectTask is not null) await _connectTask;
        await DisconnectSessionAsync(); SetStatus("未连接", "#EFF3F8", "#8A98AB"); UpdateControls();
        Log(_tunSession?.IsRunning == true ? "OpenVPN 已断开；TUN 保持开启，等待节点连接。" : "OpenVPN 已断开，已停止本地代理。");
    }
    private async Task DisconnectSessionAsync()
    {
        var session = _session; _session = null;
        if (session is not null) await session.DisposeAsync();
        _connectedAt = null; LatencyText.Text = "—"; ExitIpText.Text = "出口 IP：—"; ConnectedNodeText.Text = "等待连接";
        ConnectedCountryText.Text = "从下方列表选择一个节点"; UpdateTunControls();
    }

    private void Tun_Click(object sender, RoutedEventArgs e)
    {
        if (_tunBusy) { if (!_tunStopping) _tunCts?.Cancel(); return; }
        _tunTask = ToggleTunAsync();
    }
    private async Task ToggleTunAsync()
    {
        if (_closing || _tunBusy) return;
        var stop = _tunSession?.IsRunning == true;
        AppSettings settings = _settings;
        if (!stop && !TrySaveSettings(out settings)) return;
        _tunBusy = true; _tunStopping = stop; _tunCts = new(); UpdateControls();
        try
        {
            if (stop)
            {
                await StopTunAsync();
                Log("TUN 已关闭；现有 OpenVPN 连接和本地代理保持运行。");
            }
            else
            {
                Log(EngineRuntime.IsAdministrator ? "正在独立开启 TUN…" : "正在请求 Windows 管理员授权，独立开启 TUN；OpenVPN 会话保持运行。");
                var tun = _tunSession;
                if (tun is null)
                {
                tun = new TunSession(); _tunSession = tun;
                tun.Log += line => Log("[TUN] " + line);
                tun.Failed += error => Dispatcher.BeginInvoke(async () =>
                {
                    if (_tunSession != tun || _closing) return;
                    Log(error);
                    if (_tunBusy) { _tunCts?.Cancel(); return; }
                    await StopTunAsync(dispose: true); UpdateControls();
                });
                }
                await tun.StartAsync(settings, _tunCts.Token);
                Log(_connectedAt is null ? "TUN 已开启，等待 OpenVPN 节点连接。" : "TUN 已开启，已接入当前 OpenVPN 连接。");
            }
        }
        catch (OperationCanceledException) when (_tunCts.IsCancellationRequested)
        { await StopTunAsync(dispose: true); Log("已取消开启 TUN。"); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { await StopTunAsync(dispose: true); Log("已取消管理员授权，TUN 保持关闭。"); }
        catch (Exception exception) { await StopTunAsync(dispose: true); ShowError($"TUN 操作失败：{exception.Message}"); }
        finally { _tunBusy = false; _tunStopping = false; _tunCts.Dispose(); _tunCts = null; UpdateControls(); }
    }
    private async Task StopTunAsync(bool dispose = false)
    {
        var tun = _tunSession;
        if (tun is not null)
        {
            if (dispose) { _tunSession = null; await tun.DisposeAsync(); }
            else await tun.StopAsync();
        }
        UpdateTunControls();
    }
    private void UpdateTunControls()
    {
        if (!_loaded) return;
        var on = _tunSession?.IsRunning == true;
        TunButton.IsEnabled = !_closing && !_tunStopping;
        TunButton.Content = _tunBusy ? _tunStopping ? "正在关闭 TUN…" : "取消开启 TUN" : on ? "关闭 TUN" : "开启 TUN";
        TunStateText.Text = _tunBusy ? _tunStopping ? "正在清理网卡和临时 DNS 策略…" : "正在开启 TUN，可点击按钮取消。"
            : on ? _connectedAt is null ? "TUN 已开启，等待 OpenVPN；连接节点后开始转发。" : "TUN 已开启，正在通过当前 OpenVPN 转发。"
            : "TUN 已关闭。可随时独立开启或关闭。";
        ModeText.Text = on ? _connectedAt is null ? "TUN · 等待 OpenVPN" : "TUN + HTTP + SOCKS5" : "HTTP + SOCKS5";
    }

    private void TestAll_Click(object sender, RoutedEventArgs e) => _testTask = TestNodesAsync(_nodes.ToArray());
    private void TestSelected_Click(object sender, RoutedEventArgs e) => _testTask = TestNodesAsync(NodeGrid.SelectedItems.Cast<VpnNode>().ToArray());
    private async Task TestNodesAsync(IReadOnlyList<VpnNode> nodes)
    {
        if (_testing || _connectionBusy || _closing || nodes.Count == 0 || !TrySaveSettings(out var settings)) return;
        _testing = true; _testCts = new(); TestProgress.Value = 0; UpdateControls();
        var success = 0; var failed = 0;
        foreach (var node in nodes) { node.LatencyMs = null; node.TestStatus = "排队中"; node.Error = ""; }
        ProgressText.Text = $"测速 0 / {nodes.Count} · 并发 {settings.TestConcurrency} · 保持当前连接";
        Log($"开始测试 {nodes.Count} 个节点。每个节点独立建立 OpenVPN 连接，HTTPS 请求延时取中位数。");
        try
        {
            await ProbeService.TestManyAsync(nodes, settings,
                (node, status) => Dispatcher.Invoke(() => node.TestStatus = status),
                (result, completed, total) => Dispatcher.Invoke(() =>
                {
                    var node = nodes.First(n => n.Id == result.NodeId); node.Apply(result);
                    if (result.Success) success++; else if (!result.Cancelled) failed++;
                    TestProgress.Value = (double)completed / total * 100;
                    ProgressText.Text = $"测速 {completed} / {total} · 可用 {success} · 失败 {failed}";
                    Log(result.Success ? $"[{node.Ip}] 可用，网络延时 {node.LatencyText}" : $"[{node.Ip}] {node.TestStatus}：{result.Error}");
                }), null, _testCts.Token);
            ProgressText.Text = _testCts.IsCancellationRequested ? $"测速已取消 · 可用 {success} · 失败 {failed}" : $"测速完成 · 可用 {success} / {nodes.Count} · 失败 {failed}";
            Log(ProgressText.Text);
        }
        catch (Exception exception) { ShowError($"测速异常：{exception.Message}"); }
        finally
        {
            foreach (var node in nodes.Where(n => n.TestStatus is "排队中" or "连接中")) node.TestStatus = "未测试";
            _testing = false; _testCts.Dispose(); _testCts = null; _store.SaveNodes(_nodes); ApplySort(); UpdateControls();
        }
    }
    private void CancelTests_Click(object sender, RoutedEventArgs e) { _testCts?.Cancel(); ProgressText.Text = "正在取消测速并清理会话…"; }

    private bool Filter(object item)
    {
        if (item is not VpnNode node) return false;
        var country = (CountryBox?.SelectedItem as CountryItem)?.Code ?? "";
        if (country.Length > 0 && node.CountryShort != country) return false;
        var search = SearchBox?.Text.Trim() ?? "";
        return search.Length == 0 || $"{node.Ip} {node.HostName} {node.CountryLong} {node.CountryShort} {node.CountryDisplay} {node.NodeType}".Contains(search, StringComparison.OrdinalIgnoreCase);
    }
    private void UpdateCountries()
    {
        var selected = (CountryBox.SelectedItem as CountryItem)?.Code ?? "";
        var items = new List<CountryItem> { new("", "全部国家 / 地区") };
        items.AddRange(_nodes.GroupBy(n => n.CountryShort).OrderBy(g => g.Key).Select(g => new CountryItem(g.Key, $"{g.First().CountryDisplay} ({g.Count()})")));
        CountryBox.ItemsSource = items;
        CountryBox.SelectedItem = items.FirstOrDefault(c => c.Code == selected) ?? items[0]; UpdateCount();
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (_loaded) { _view.Refresh(); UpdateCount(); } }
    private void Country_Changed(object sender, SelectionChangedEventArgs e) { if (_loaded) { _view.Refresh(); UpdateCount(); } }
    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        (_sortProperty, _descending) = SortBox.SelectedIndex switch { 1 => ("LatencyMs", false), 2 => ("CountryShort", false), 3 => ("LatencyMs", true), _ => ("Score", true) };
        ApplySort();
    }
    private void ApplySort() { _view.CustomSort = new NodeComparer(_sortProperty, _descending); UpdateCount(); }
    private sealed class NodeComparer(string property, bool descending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not VpnNode a || y is not VpnNode b) return 0;
            // Failures and untested nodes always follow usable nodes, in either direction.
            if (property == "LatencyMs" && (a.LatencyMs is null || b.LatencyMs is null))
            { if (a.LatencyMs is null && b.LatencyMs is not null) return 1; if (a.LatencyMs is not null && b.LatencyMs is null) return -1; }
            int result = property switch
            {
                "LatencyMs" => Nullable.Compare(a.LatencyMs, b.LatencyMs), "CountryShort" => string.CompareOrdinal(a.CountryShort, b.CountryShort),
                "Ip" => string.CompareOrdinal(a.Ip, b.Ip), "Speed" => a.Speed.CompareTo(b.Speed), "Sessions" => a.Sessions.CompareTo(b.Sessions),
                "AdvertisedPing" => Nullable.Compare(a.AdvertisedPing, b.AdvertisedPing), "TestStatus" => string.CompareOrdinal(a.TestStatus, b.TestStatus),
                "Transport" => string.CompareOrdinal(a.Transport, b.Transport), "NodeType" => string.CompareOrdinal(a.NodeType, b.NodeType), _ => a.Score.CompareTo(b.Score)
            };
            if (descending) result = -result;
            return result == 0 ? string.CompareOrdinal(a.Id, b.Id) : result;
        }
    }
    private void NodeGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        _descending = e.Column.SortDirection == ListSortDirection.Ascending;
        _sortProperty = e.Column.SortMemberPath;
        foreach (var column in NodeGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        ApplySort();
    }
    private void NodeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) UpdateControls(); }
    private void NodeGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin && FindParent<DataGridRow>(origin) is not null) _connectTask = ConnectSelectedAsync();
    }
    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null) { if (element is T match) return match; element = VisualTreeHelper.GetParent(element); }
        return null;
    }
    private void UpdateControls()
    {
        if (!_loaded || _closing) return;
        SettingsPanel.IsEnabled = !_connectionBusy && _session is null && !_testing && !_fetching && !_tunBusy && _tunSession?.IsRunning != true;
        SaveButton.IsEnabled = SettingsPanel.IsEnabled;
        FetchButton.IsEnabled = !_fetching && !_testing && !_connectionBusy;
        FetchButton.Content = _fetching ? "正在获取…" : "获取 / 刷新节点";
        ConnectButton.IsEnabled = NodeGrid.SelectedItem is VpnNode && !_connectionBusy && !_fetching;
        ConnectButton.Content = _connectionBusy ? "正在连接…" : _session is null ? "连接选中节点" : "切换选中节点";
        DisconnectButton.IsEnabled = _session is not null || _connectionBusy;
        TestSelectedButton.IsEnabled = NodeGrid.SelectedItems.Count > 0 && !_testing && !_connectionBusy && !_fetching;
        TestAllButton.IsEnabled = _nodes.Count > 0 && !_testing && !_connectionBusy && !_fetching;
        CancelTestsButton.IsEnabled = _testing;
        UpdateTunControls();
    }
    private void UpdateCount() => NodeCountText.Text = _view.Count == _nodes.Count ? $"{_nodes.Count} 个节点" : $"{_view.Count} / {_nodes.Count} 个节点";
    private void SetStatus(string text, string background, string color)
    {
        StatusText.Text = text; StatusPill.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }
    private void ShowError(string message) { Log(message); FooterText.Text = message; if (!_arguments.Contains("--ui-smoke")) MessageBox.Show(this, message, "vpngate_proxy", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void Log(string message)
    {
        // Credentials are never rendered or written to log files.
        var secrets = new[] { _settings.Password, PasswordBox.Dispatcher.CheckAccess() ? PasswordBox.Password : "" }.Where(s => s.Length > 0);
        foreach (var secret in secrets) message = message.Replace(secret, "***", StringComparison.Ordinal);
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_logLock)
        {
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 5 * 1024 * 1024) File.WriteAllText(_logPath, "");
            File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        Dispatcher.BeginInvoke(() =>
        {
            _logLines.Enqueue(line); while (_logLines.Count > 300) _logLines.Dequeue();
            LogBox.Text = string.Join(Environment.NewLine, _logLines); LogBox.ScrollToEnd();
        });
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(AppDataPaths.Logs) { UseShellExecute = true });
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = $"vpngate-tests-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Csv(string value) => "\"" + (value.Length > 0 && "=+-@".Contains(value[0]) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "国家代码,国家,节点名称,IP,节点类型,运营商,ASN,网络延时ms,连接耗时ms,状态,测试时间,错误" };
        lines.AddRange(_view.Cast<VpnNode>().Select(n => string.Join(',', new[] { n.CountryShort, n.CountryDisplay, n.HostName, n.Ip, n.NodeType, n.NetworkInfo?.Isp ?? "", n.NetworkInfo?.Asn ?? "",
            n.LatencyMs?.ToString("0.00", CultureInfo.InvariantCulture) ?? "", n.ConnectMs?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
            n.TestStatus, n.TestedAt?.ToString("O") ?? "", n.Error }.Select(Csv))));
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true)); Log($"测速结果已导出到 {dialog.FileName}");
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false; _timer.Stop();
        _connectCts?.Cancel(); _testCts?.Cancel(); _fetchCts?.Cancel(); _tunCts?.Cancel(); _ipInfoCts?.Cancel();
        foreach (var task in new[] { _connectTask, _testTask, _fetchTask, _tunTask, _ipInfoTask }.Where(t => t is not null))
            try { await task!; } catch (Exception exception) { Log($"退出清理：{exception.Message}"); }
        await StopTunAsync(dispose: true); await DisconnectSessionAsync(); _store.SaveNodes(_nodes);
        _canClose = true; _ = Dispatcher.BeginInvoke(Close);
    }

    private async Task RunUiSmokeAsync(string directory)
    {
        var savedSettings = _settings.Copy();
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, object>();
        try
        {
            _fetchTask = FetchNodesAsync(); await _fetchTask;
            if (_nodes.Count == 0) throw new IOException("UI 未获得真实 VPNGate 节点。");
            checks["nodes"] = _nodes.Count;
            SearchBox.Text = _nodes[0].Ip;
            if (_view.Count < 1) throw new IOException("IP 筛选失败。");
            SearchBox.Text = "";
            SortBox.SelectedIndex = 2;
            var countries = _view.Cast<VpnNode>().Select(n => n.CountryShort).ToArray();
            if (!countries.SequenceEqual(countries.Order(StringComparer.Ordinal))) throw new IOException("国家排序失败。");
            checks["countrySort"] = true;
            SortBox.SelectedIndex = 0;
            _testTask = TestNodesAsync(_nodes.Where(n => n.SupportsTcp && !n.HostName.StartsWith("public-vpn-", StringComparison.Ordinal)).Take(8).ToArray()); await _testTask;
            var candidates = _nodes.Where(n => n.LatencyMs is not null).OrderBy(n => n.LatencyMs).ToArray();
            var testTun = _arguments.Contains("--tun-ui", StringComparer.Ordinal);
            async Task ClickTun()
            {
                if (!TunButton.IsEnabled) throw new IOException("TUN 按钮不应被当前 OpenVPN 状态禁用。");
                TunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await _tunTask!;
            }
            if (testTun)
            {
                await ClickTun();
                if (_tunSession?.IsRunning != true || _session is not null) throw new IOException("未连接节点时独立开启 TUN 失败。");
                checks["tunStartedBeforeVpn"] = true;
                SaveScreenshot(Path.Combine(directory, "tun-waiting-for-vpn.png"));
                var worker = _tunSession.WorkerProcessId;
                await ClickTun();
                if (_tunSession.IsRunning) throw new IOException("未连接节点时关闭 TUN 失败。");
                checks["tunStoppedBeforeVpn"] = true;
                await ClickTun();
                if (_tunSession?.IsRunning != true || _tunSession.WorkerProcessId != worker) throw new IOException("TUN 再次开启未复用已授权进程。");
                checks["tunWorkerReused"] = true;
            }
            foreach (var candidate in candidates)
            {
                NodeGrid.SelectedItem = candidate;
                ConnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (testTun && _tunSession?.IsRunning == true)
                {
                    if (!_connectionBusy || !TunButton.IsEnabled) throw new IOException("OpenVPN 连接过程中 TUN 按钮不可用。");
                    await ClickTun();
                    checks["tunToggledWhileConnecting"] = true;
                }
                await _connectTask!;
                if (_session?.IsRunning == true && _connectedAt is not null) break;
            }
            if (_session?.IsRunning != true || _connectedAt is null) throw new IOException("UI 真实连接失败。");
            if (testTun)
            {
                var vpn = _session;
                var vpnPid = vpn.HostProcessId;
                await ClickTun();
                if (_tunSession?.IsRunning != true || _session != vpn || _session.HostProcessId != vpnPid) throw new IOException("开启 TUN 重建了 OpenVPN。");
                checks["tunOnPreservesVpnProcess"] = true;
                await ClickTun();
                if (_tunSession?.IsRunning == true || _session != vpn || _session.HostProcessId != vpnPid) throw new IOException("关闭 TUN 中断了 OpenVPN。");
                await ProbeService.MeasureAsync(vpn.Port, _settings, CancellationToken.None, _settings.ListenAddress);
                checks["tunOffPreservesWorkingLocalProxy"] = true;
                await ClickTun();
            }
            checks["firstNode"] = _session.Node!.Ip; checks["firstExit"] = ExitIpText.Text; checks["latency"] = LatencyText.Text;
            SaveScreenshot(Path.Combine(directory, "connected.png"));
            var firstId = _session.Node.Id;
            foreach (var candidate in candidates.Where(n => n.Id != firstId))
            {
                NodeGrid.SelectedItem = candidate;
                _connectTask = ConnectSelectedAsync(); await _connectTask;
                if (_session?.IsRunning == true && _connectedAt is not null) break;
            }
            if (_session?.IsRunning != true || _connectedAt is null) throw new IOException("UI 节点切换失败。");
            checks["switchedNode"] = _session.Node!.Ip; checks["switchedExit"] = ExitIpText.Text;
            _testTask = TestNodesAsync(_nodes.Where(n => n.SupportsTcp).Take(3).ToArray()); await _testTask;
            if (_session?.IsRunning != true) throw new IOException("批量测速中断了当前连接。");
            checks["testsKeptConnection"] = true;
            SortBox.SelectedIndex = 1;
            checks["latencySorted"] = _view.Cast<VpnNode>().Where(n => n.LatencyMs is not null).Select(n => n.LatencyMs).ToArray();
            SaveScreenshot(Path.Combine(directory, "tested.png"));
            if (testTun)
            {
                var worker = _tunSession?.WorkerProcessId;
                await DisconnectSessionAsync(); UpdateControls();
                if (_tunSession?.IsRunning != true || _tunSession.WorkerProcessId != worker) throw new IOException("断开 OpenVPN 意外关闭了 TUN。");
                checks["disconnectKeepsTunOn"] = true;
                await ClickTun();
                if (_tunSession?.IsRunning == true) throw new IOException("OpenVPN 断开后 TUN 无法关闭。");
                checks["tunStoppedAfterDisconnect"] = true;
            }
            checks["success"] = true;
        }
        catch (Exception exception) { checks["success"] = false; checks["error"] = exception.ToString(); SaveScreenshot(Path.Combine(directory, "error.png")); }
        finally
        {
            await StopTunAsync(dispose: true);
            await DisconnectSessionAsync();
            _store.SaveSettings(savedSettings); _settings = savedSettings;
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-smoke.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
    private void SaveScreenshot(string path)
    {
        UpdateLayout();
        var visual = (FrameworkElement)Content;
        var target = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        target.Render(background);
        target.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(path); encoder.Save(file);
    }
}
