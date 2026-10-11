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
    private readonly DispatcherTimer _logTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly BufferedSessionLog _log;
    private long _displayedLogVersion;
    private readonly string _logPath;
    private EngineSession? _session;
    private bool _tunRequested;
    private readonly NodePool _pool;
    private CancellationTokenSource? _tunCts;
    private Task? _tunTask;
    private bool _tunBusy, _tunStopping;
    private CancellationTokenSource? _connectCts;
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
        _pool = new NodePool(_store.LoadNodes(), _store.LoadPoolRefresh(), probe: ProbePooledNodeAsync);
        FillSettings(_settings);
        Directory.CreateDirectory(AppDataPaths.Logs);
        _logPath = Path.Combine(AppDataPaths.Logs, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        _log = new BufferedSessionLog(_logPath);
        foreach (var old in Directory.GetFiles(AppDataPaths.Logs, "session-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(20))
            try { File.Delete(old); } catch (IOException) { }
        PrivilegeText.Text = EngineRuntime.IsAdministrator ? "管理员权限 · TUN 可直接启动" : "普通用户权限 · TUN 启动时请求授权";
        _timer.Tick += (_, _) =>
        {
            UptimeText.Text = _connectedAt is { } since ? $"已连接 {(DateTimeOffset.Now - since):hh\\:mm\\:ss}" : "";
            if (_testing) { _view.Refresh(); UpdateCount(); }
        };
        _timer.Start();
        _logTimer.Tick += (_, _) => RefreshLogDisplay();
        _logTimer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var node in _pool.Nodes) _nodes.Add(node);
        _ipInfo.ApplyCached(_nodes);
        UpdateCountries(); ApplySort();
        NodeGrid.SelectedItem = _nodes.FirstOrDefault(n => n.Id == _settings.SelectedNodeId) ?? _view.Cast<VpnNode>().FirstOrDefault();
        UpdateControls();
        if (!_arguments.Contains("--ui-smoke", StringComparer.Ordinal)) _rulesLoopTask = RunRulesLoopAsync();
        Log("程序就绪。上游代理承载 OpenVPN 数据，本地 HTTP/SOCKS5 和 TUN 按所选模式分流。");
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
        if (_arguments.Contains("--log-ui", StringComparer.Ordinal) || _arguments.Contains("--tun-only-ui", StringComparer.Ordinal))
        {
            var reportIndex = Array.IndexOf(_arguments, "--ui-smoke");
            var directory = reportIndex >= 0 && reportIndex + 1 < _arguments.Length
                ? _arguments[reportIndex + 1] : Path.Combine(AppDataPaths.Root, "ui-smoke");
            await RunTunStartupSmokeAsync(directory); return;
        }
        if (_arguments.Contains("--routing-ui", StringComparer.Ordinal))
        {
            var index = Array.IndexOf(_arguments, "--ui-smoke");
            await RunRoutingSmokeAsync(index >= 0 && index + 1 < _arguments.Length ? _arguments[index + 1] : Path.Combine(AppDataPaths.Root, "routing-smoke"));
            return;
        }
        if (_arguments.Contains("--pool-ui", StringComparer.Ordinal))
        {
            var index = Array.IndexOf(_arguments, "--ui-smoke");
            await RunPoolSmokeAsync(index >= 0 && index + 1 < _arguments.Length ? _arguments[index + 1] : Path.Combine(AppDataPaths.Root, "pool-smoke"));
            return;
        }
        _ipInfoTask = RefreshNodeTypesAsync(_nodes.ToArray(), _settings);
        var smoke = Array.IndexOf(_arguments, "--ui-smoke");
        if (smoke >= 0)
        {
            await RunUiSmokeAsync(smoke + 1 < _arguments.Length ? _arguments[smoke + 1] : Path.Combine(AppDataPaths.Root, "ui-smoke"));
            return;
        }
        var startup = EnsureCoreAsync(_settings, _poolCts.Token);
        _poolLoopTask = RunPoolLoopAsync();
        try { await startup; }
        catch (Exception error) { Log("主核心启动失败：" + error.Message); FooterText.Text = "本地核心启动失败，节点池后台维护继续运行。"; }
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
        TimeoutBox.Text = settings.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ProbeUrlBox.Text = settings.ProbeUrl;
        SetRoutingModeUi(settings.RoutingMode);
        AutoRulesBox.IsChecked = settings.AutoUpdateRules; RuleHoursBox.Text = settings.RuleUpdateHours.ToString(CultureInfo.InvariantCulture);
    }
    private AppSettings ReadSettings()
    {
        static int Number(TextBox box, string name) => int.TryParse(box.Text.Trim(), out var value) ? value : throw new ArgumentException($"{name}必须为整数。");
        var settings = _settings.Copy();
        settings.UpstreamProxy = UpstreamBox.Text.Trim(); settings.Username = UsernameBox.Text; settings.Password = PasswordBox.Password;
        settings.ListenAddress = ListenBox.Text.Trim(); settings.ListenPort = Number(PortBox, "本地端口");
        settings.ConnectionTimeoutSeconds = Number(TimeoutBox, "连接超时");
        settings.ProbeUrl = ProbeUrlBox.Text.Trim(); settings.SelectedNodeId = (NodeGrid.SelectedItem as VpnNode)?.Id ?? "";
        settings.RoutingMode = DirectModeButton.IsChecked == true ? "direct" : GlobalModeButton.IsChecked == true ? "global" : "rule";
        settings.AutoUpdateRules = AutoRulesBox.IsChecked == true; settings.RuleUpdateHours = Number(RuleHoursBox, "规则更新间隔");
        var proxy = settings.Validate(); settings.UpstreamProxy = proxy.SafeAddress; settings.Username = proxy.Username; settings.Password = proxy.Password;
        return settings;
    }
    private bool TrySaveSettings(out AppSettings settings)
    {
        try { settings = ReadSettings(); _store.SaveSettings(settings); _settings = settings; return true; }
        catch (Exception exception) { settings = _settings; ShowError(exception.Message); return false; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveSettings(out var settings))
        {
            try
            {
                if (_session?.IsRunning == true && !_session.IsVpnConnected && !_session.TunEnabled)
                    await _session.ConfigureAsync(null, settings, settings.ListenAddress, settings.ListenPort, false, _poolCts.Token);
                FillSettings(settings); Log("设置已保存并应用于主核心。代理密码由 Windows DPAPI 加密保存。"); FooterText.Text = "设置已保存";
            }
            catch (Exception error) { ShowError("应用设置失败：" + error.Message); }
        }
    }
    private void Fetch_Click(object sender, RoutedEventArgs e) { if (SettingsPanel.IsEnabled && !TrySaveSettings(out _)) return; _fetchTask = RefreshPoolAsync(true); }
    private Task FetchNodesAsync() => RefreshPoolAsync(true);

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
        SettingsPanel.IsEnabled = !_connectionBusy && _session?.IsVpnConnected != true && !_tunBusy && !_routingBusy && !_fetching && !_testing;
        SaveButton.IsEnabled = SettingsPanel.IsEnabled;
        FetchButton.IsEnabled = !_fetching && !_testing && !_routingBusy;
        FetchButton.Content = _fetching ? "正在获取…" : _testing ? "正在验证…" : "立即刷新节点池";
        ConnectButton.IsEnabled = NodeGrid.SelectedItem is VpnNode && !_connectionBusy && !_tunBusy && !_routingBusy;
        ConnectButton.Content = _connectionBusy ? "正在连接…" : _session?.IsVpnConnected != true ? "连接选中节点" : "切换选中节点";
        DisconnectButton.IsEnabled = _session?.IsVpnConnected == true || _connectionBusy;
        TestSelectedButton.IsEnabled = NodeGrid.SelectedItems.Count > 0 && !_testing && !_connectionBusy && !_fetching && !_routingBusy;
        TestAllButton.IsEnabled = _nodes.Count > 0 && !_testing && !_connectionBusy && !_fetching && !_routingBusy;
        CancelTestsButton.IsEnabled = _testing || _fetching;
        UpdateTunControls();
        UpdateRoutingControls();
    }
    private void UpdateCount() => NodeCountText.Text = _view.Count == _nodes.Count ? $"{_nodes.Count} 个可用节点" : $"{_view.Count} / {_nodes.Count} 个可用节点";
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
        _log.Write(line);
    }
    private void RefreshLogDisplay()
    {
        var text = _log.Snapshot(ref _displayedLogVersion);
        if (text is null) return;
        LogBox.Text = text; LogBox.ScrollToEnd();
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
        _closing = true; IsEnabled = false; _timer.Stop(); _logTimer.Stop();
        _connectCts?.Cancel(); _tunCts?.Cancel(); _ipInfoCts?.Cancel();
        _rulesCts.Cancel(); _poolCts.Cancel(); _poolRoundCts?.Cancel();
        foreach (var task in new[] { _connectTask, _testTask, _fetchTask, _tunTask, _ipInfoTask, _routingTask, _rulesLoopTask, _ruleUpdateTask, _poolLoopTask }.Where(t => t is not null))
            try { await task!; } catch (Exception exception) { Log($"退出清理：{exception.Message}"); }
        await StopTunAsync(dispose: true); await DisconnectSessionAsync(); _store.SaveNodes(_nodes);
        await _log.DisposeAsync();
        _rulesCts.Dispose(); _poolCts.Dispose();
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
            foreach (var candidate in candidates)
            {
                NodeGrid.SelectedItem = candidate;
                ConnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await _connectTask!;
                if (_session?.IsRunning == true && _connectedAt is not null) break;
            }
            if (_session?.IsRunning != true || _connectedAt is null) throw new IOException("UI 真实连接失败。");
            if (_arguments.Contains("--routing-connected-ui", StringComparer.Ordinal))
            {
                var vpn = _session; var pid = vpn.HostProcessId;
                foreach (var (button, mode) in new[] { (GlobalModeButton, "global"), (DirectModeButton, "direct"), (RuleModeButton, "rule") })
                {
                    button.IsChecked = true; if (_routingTask is not null) await _routingTask;
                    if (_settings.RoutingMode != mode || _session != vpn || vpn.HostProcessId != pid || !vpn.IsRunning)
                        throw new IOException("切换模式重建了 OpenVPN 或未成功生效。");
                    await ProbeService.MeasureAsync(vpn.ProbePort, _settings, CancellationToken.None);
                    if (mode != "global")
                    {
                        using var client = ProbeService.ProxyClient(vpn.Port, 12, _settings.ListenAddress);
                        using var result = await client.GetAsync("https://www.baidu.com/"); result.EnsureSuccessStatusCode();
                    }
                }
                checks["allModesKeepSameVpnProcess"] = true; checks["vpnProbeWorksInEveryMode"] = true;
                checks["domesticLocalProxySplitsCorrectly"] = true;
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
