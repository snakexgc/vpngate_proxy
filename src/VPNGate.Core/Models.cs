using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VPNGate.Core;

public sealed class AppSettings
{
    public string UpstreamProxy { get; set; } = "http://127.0.0.1:10808";
    public string Username { get; set; } = "";
    [JsonIgnore] public string Password { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 10800;
    public int TestConcurrency { get; set; } = 4;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int ProbeTimeoutSeconds { get; set; } = 10;
    public int ProbeSamples { get; set; } = 3;
    public string ProbeUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public string SelectedNodeId { get; set; } = "";

    public ProxyEndpoint Validate()
    {
        var proxy = ProxyEndpoint.Parse(UpstreamProxy, Username, Password);
        if (!System.Net.IPAddress.TryParse(ListenAddress, out _))
            throw new ArgumentException("本地监听地址必须是有效的 IP 地址。");
        if (ListenPort is < 1 or > 65535) throw new ArgumentException("本地端口须在 1–65535 之间。");
        if (proxy.Port == ListenPort && (proxy.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(proxy.Host, out var ip) && (System.Net.IPAddress.IsLoopback(ip) || proxy.Host == ListenAddress)))
            throw new ArgumentException("本地代理与上游代理不能使用同一个端口，否则会形成循环。");
        if (TestConcurrency is < 1 or > 12) throw new ArgumentException("测速并发数须在 1–12 之间。");
        if (ConnectionTimeoutSeconds is < 5 or > 180) throw new ArgumentException("连接超时须在 5–180 秒之间。");
        if (ProbeTimeoutSeconds is < 2 or > 60) throw new ArgumentException("请求超时须在 2–60 秒之间。");
        if (ProbeSamples is < 1 or > 10) throw new ArgumentException("采样次数须在 1–10 之间。");
        if (!Uri.TryCreate(ProbeUrl, UriKind.Absolute, out var probe) || probe.Scheme != "https" || !string.IsNullOrEmpty(probe.UserInfo))
            throw new ArgumentException("测速 URL 必须是有效的 HTTPS 地址。");
        return proxy;
    }

    public AppSettings Copy() => (AppSettings)MemberwiseClone();
}

public sealed class VpnNode : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string HostName { get; set; } = "";
    public string Ip { get; set; } = "";
    public string CountryLong { get; set; } = "";
    public string CountryShort { get; set; } = "";
    public long Score { get; set; }
    public int? AdvertisedPing { get; set; }
    public long Speed { get; set; }
    public int Sessions { get; set; }
    public long Uptime { get; set; }
    public string Operator { get; set; } = "";
    public string LogPolicy { get; set; } = "";
    public string Message { get; set; } = "";
    public string OpenVpnConfig { get; set; } = "";
    public bool SupportsTcp { get; set; }
    private IpNetworkInfo? _networkInfo;
    private bool _networkInfoLoading;
    public IpNetworkInfo? NetworkInfo
    {
        get => _networkInfo;
        set { _networkInfo = value; Changed(); Changed(nameof(NodeType)); Changed(nameof(NodeTypeDetail)); }
    }
    [JsonIgnore] public bool NetworkInfoLoading
    {
        get => _networkInfoLoading;
        set { _networkInfoLoading = value; Changed(); Changed(nameof(NodeType)); }
    }
    [JsonIgnore] public bool IsVpnGatePublicRelay
    {
        get
        {
            if (HostName.StartsWith("public-vpn-", StringComparison.OrdinalIgnoreCase)) return true;
            if (!System.Net.IPAddress.TryParse(Ip, out var address)) return false;
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 219 && bytes[1] == 100 && bytes[2] == 37;
        }
    }
    [JsonIgnore] public bool? IsDataCenter => IsVpnGatePublicRelay ? true : NetworkInfo?.Hosting;
    [JsonIgnore] public string NodeType => IsVpnGatePublicRelay ? "机房（官方中继）"
        : NetworkInfoLoading && NetworkInfo is null ? "查询中…" : NetworkInfo?.Label ?? "未知";
    [JsonIgnore] public string NodeTypeDetail => IsVpnGatePublicRelay
        ? $"来源：VPNGate 原始节点信息\n名称：{HostName}\nIP：{Ip}\n依据：public-vpn-* 名称或已知官方 219.100.37.0/24 网段，按官方中继归类。\n这是源信息规则识别，CSV 没有独立的住宅/机房字段。"
        : NetworkInfo?.Detail ?? "尚未查询 IP 类型。刷新节点后自动查询。";
    private double? _latencyMs;
    private double? _connectMs;
    private string _testStatus = "未测试";
    private string _error = "";
    private DateTimeOffset? _testedAt;
    public double? LatencyMs { get => _latencyMs; set { _latencyMs = value; Changed(); Changed(nameof(LatencyText)); } }
    public double? ConnectMs { get => _connectMs; set { _connectMs = value; Changed(); } }
    public string TestStatus { get => _testStatus; set { _testStatus = value; Changed(); } }
    public string Error { get => _error; set { _error = value; Changed(); } }
    public DateTimeOffset? TestedAt { get => _testedAt; set { _testedAt = value; Changed(); } }
    [JsonIgnore] public string CountryDisplay => CountryNames.Translate(CountryShort, CountryLong);
    [JsonIgnore] public string CountryLabel => $"{CountryDisplay} · {CountryShort}";
    [JsonIgnore] public string LatencyText => LatencyMs is { } ms ? $"{ms:0} ms" : "—";
    [JsonIgnore] public string SpeedText => $"{Speed / 1_000_000d:0.0} Mbps";
    [JsonIgnore] public string AdvertisedPingText => AdvertisedPing is { } ping ? $"{ping} ms" : "—";
    [JsonIgnore] public string Transport => SupportsTcp ? "TCP" : "UDP";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Apply(ProbeResult result)
    {
        LatencyMs = result.LatencyMs;
        ConnectMs = result.ConnectMs;
        Error = result.Error;
        TestedAt = result.TestedAt;
        TestStatus = result.Success ? "可用" : result.Cancelled ? "已取消" : "失败";
    }
}

public sealed record ProbeResult(string NodeId, bool Success, double? LatencyMs, double? ConnectMs,
    string Error, DateTimeOffset TestedAt, bool Cancelled = false, IReadOnlyList<double>? Samples = null);

public static class CountryNames
{
    public static string Translate(string code, string fallback) => code.ToUpperInvariant() switch
    {
        "JP" => "日本", "KR" => "韩国", "US" => "美国", "CN" => "中国", "TW" => "中国台湾", "HK" => "中国香港",
        "SG" => "新加坡", "VN" => "越南", "TH" => "泰国", "AU" => "澳大利亚", "CA" => "加拿大", "GB" => "英国",
        "DE" => "德国", "FR" => "法国", "RU" => "俄罗斯", "RO" => "罗马尼亚", "MX" => "墨西哥", "ID" => "印度尼西亚",
        "IN" => "印度", "NL" => "荷兰", "MY" => "马来西亚", "PH" => "菲律宾", "BR" => "巴西", "IT" => "意大利",
        _ => fallback
    };
}
