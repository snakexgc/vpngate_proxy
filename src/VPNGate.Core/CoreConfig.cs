using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNGate.Core;

public static partial class CoreConfig
{
    public const string TunName = "VPNGate-TUN";
    public static JsonObject Build(VpnNode? node, AppSettings settings, string listenAddress, int listenPort, RoutingControl? control = null, int? probePort = null, bool tunEnabled = false, string? physicalInterface = null)
    {
        var proxy = settings.Validate();
        var upstream = proxy.ToOutbound();
        if (proxy.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        { upstream["server"] = "127.0.0.1"; upstream.Remove("domain_resolver"); }
        var physical = physicalInterface ?? PhysicalInterface();
        var inbounds = new JsonArray(new JsonObject { ["type"] = "mixed", ["tag"] = "local", ["listen"] = listenAddress, ["listen_port"] = listenPort });
        if (probePort is { } port) inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "probe-vpn", ["listen"] = "127.0.0.1", ["listen_port"] = port });
        var rules = new JsonArray();
        if (probePort is not null) rules.Add(node is not null ? Route("inbound", "probe-vpn", "vpn")
            : new JsonObject { ["inbound"] = "probe-vpn", ["action"] = "reject" });
        if (tunEnabled) AddTun(settings, physical, inbounds, rules);
        rules.Add(new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" });
        AddTrafficRules(rules, "vpn", node is not null);
        // Proxy hostname bootstrap must work before OpenVPN connects, even when TUN is already on.
        return new JsonObject {
            ["log"] = LogSettings(),
            ["dns"] = TrafficDns("vpn", physical, node is not null),
            ["inbounds"] = inbounds,
            ["endpoints"] = node is null ? new JsonArray() : new JsonArray(OpenVpnProfile.ToEndpoint(node.OpenVpnConfig, proxy, settings.ConnectionTimeoutSeconds)),
            ["outbounds"] = new JsonArray(upstream, PhysicalOutbound(physical)),
            ["route"] = TrafficRoute(rules, node is not null ? "vpn" : "physical", findProcess: tunEnabled, hasVpn: node is not null),
            ["experimental"] = (control ?? RoutingControl.Create(listenPort)).Configuration(settings.RoutingMode)
        };
    }
    private static void AddTun(AppSettings settings, string physicalInterface, JsonArray inbounds, JsonArray rules)
    {
        var proxy = settings.Validate();
        if (settings.ListenPort == 53) throw new ArgumentException("开启 TUN 时，本地代理端口不能使用 DNS 专用端口 53。");
        if (physicalInterface.Length == 0) throw new InvalidOperationException("没有找到可用于上游代理的物理网络接口。");
        var dnsAddress = TunDnsListenAddress();
        var exclusions = new JsonArray("127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
            "169.254.0.0/16", "fc00::/7", "fe80::/10");
        if (IPAddress.TryParse(proxy.Host, out var upstreamIp) && !IPAddress.IsLoopback(upstreamIp))
            exclusions.Add($"{upstreamIp}/{(upstreamIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)}");
        rules.Add(new JsonObject { ["inbound"] = "system-dns", ["action"] = "hijack-dns" });
        rules.Add(new JsonObject { ["inbound"] = "tun", ["process_name"] = new JsonArray("v2rayN.exe", "xray.exe", "v2ray.exe", "sing-box.exe", "mihomo.exe", "clash.exe", "clash-meta.exe"),
            ["action"] = "route", ["outbound"] = "physical" });
        rules.Add(new JsonObject { ["inbound"] = "tun", ["port"] = 53, ["action"] = "hijack-dns" });
        rules.Add(new JsonObject { ["inbound"] = "tun", ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "physical" });
        rules.Add(new JsonObject { ["inbound"] = "tun", ["action"] = "sniff", ["timeout"] = "100ms" });
        inbounds.Add(new JsonObject { ["type"] = "tun", ["tag"] = "tun", ["interface_name"] = TunName,
                    ["address"] = new JsonArray("172.30.222.1/30", "fdfe:7667:6174::1/126"), ["mtu"] = 1400,
                    ["auto_route"] = true, ["strict_route"] = false, ["dns_mode"] = "native", ["dns_address"] = new JsonArray(dnsAddress),
                    ["stack"] = "gvisor", ["route_exclude_address"] = exclusions });
        inbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "system-dns", ["listen"] = dnsAddress, ["listen_port"] = 53 });
    }
    public static string ProbeAddress(string address) => address == "0.0.0.0" ? "127.0.0.1" : address == "::" ? "::1" : address;
    private static JsonObject LogSettings() => new() { ["level"] = "info", ["timestamp"] = true };
    private static JsonObject HttpsDns(string tag, string? detour = null)
    {
        var dns = new JsonObject { ["type"] = "https", ["tag"] = tag, ["server"] = "1.1.1.1", ["server_port"] = 443,
            ["path"] = "/dns-query", ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" } };
        if (detour is not null) dns["detour"] = detour;
        return dns;
    }
    public static string PhysicalInterface()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
            !new[] { "wintun", "vpn", "virtual", "hyper-v", "tailscale", "wireguard", "tap-" }.Any(s => (n.Name + " " + n.Description).Contains(s, StringComparison.OrdinalIgnoreCase)) &&
            n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)));
        return candidates.OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1).Select(n => n.Name).FirstOrDefault() ?? "";
    }
    public static string TunDnsListenAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Name != TunName && n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
        .OrderBy(n => n.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString()).FirstOrDefault() ?? "127.0.0.1";
    public static string Serialize(JsonObject config) => config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
