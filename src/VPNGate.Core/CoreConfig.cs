using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNGate.Core;

public static class CoreConfig
{
    public const string TunName = "VPNGate-TUN";
    public static JsonObject Build(VpnNode node, AppSettings settings, string listenAddress, int listenPort)
    {
        var proxy = settings.Validate();
        var upstream = proxy.ToOutbound();
        if (proxy.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        { upstream["server"] = "127.0.0.1"; upstream.Remove("domain_resolver"); }
        var bootstrap = HttpsDns("bootstrap");
        var physical = PhysicalInterface();
        if (physical.Length > 0) bootstrap["bind_interface"] = physical;
        // Proxy hostname bootstrap must work before OpenVPN connects, even when TUN is already on.
        return new JsonObject {
            ["log"] = LogSettings(),
            ["dns"] = DnsSettings(new JsonArray(HttpsDns("vpn-dns", "vpn"), bootstrap)),
            ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "mixed", ["tag"] = "local", ["listen"] = listenAddress, ["listen_port"] = listenPort }),
            ["endpoints"] = new JsonArray(OpenVpnProfile.ToEndpoint(node.OpenVpnConfig, proxy, settings.ConnectionTimeoutSeconds)),
            ["outbounds"] = new JsonArray(upstream),
            ["route"] = new JsonObject { ["final"] = "vpn", ["default_domain_resolver"] = "vpn-dns" }
        };
    }
    // TUN starts without a node. A later OpenVPN session supplies the configured local SOCKS5 exit.
    public static JsonObject BuildTun(AppSettings settings, string physicalInterface)
    {
        var proxy = settings.Validate();
        if (settings.ListenPort == 53) throw new ArgumentException("开启 TUN 时，本地代理端口不能使用 DNS 专用端口 53。");
        if (physicalInterface.Length == 0) throw new InvalidOperationException("没有找到可用于上游代理的物理网络接口。");
        var dnsAddress = TunDnsListenAddress();
        var exclusions = new JsonArray("127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
            "169.254.0.0/16", "fc00::/7", "fe80::/10");
        if (IPAddress.TryParse(proxy.Host, out var upstreamIp) && !IPAddress.IsLoopback(upstreamIp))
            exclusions.Add($"{upstreamIp}/{(upstreamIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)}");
        var rules = new JsonArray(
            new JsonObject { ["inbound"] = "system-dns", ["action"] = "hijack-dns" },
            new JsonObject { ["inbound"] = "tun", ["process_name"] = new JsonArray("v2rayN.exe", "xray.exe", "v2ray.exe", "sing-box.exe", "mihomo.exe", "clash.exe", "clash-meta.exe"),
                ["action"] = "route", ["outbound"] = "physical" },
            new JsonObject { ["inbound"] = "tun", ["port"] = 53, ["action"] = "hijack-dns" },
            new JsonObject { ["inbound"] = "tun", ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "physical" });
        return new JsonObject {
            ["log"] = LogSettings(),
            ["dns"] = DnsSettings(new JsonArray(HttpsDns("vpn-dns", "vpn-local"), new JsonObject { ["type"] = "local", ["tag"] = "bootstrap" })),
            ["inbounds"] = new JsonArray(
                new JsonObject { ["type"] = "tun", ["tag"] = "tun", ["interface_name"] = TunName,
                    ["address"] = new JsonArray("172.30.222.1/30", "fdfe:7667:6174::1/126"), ["mtu"] = 1400,
                    ["auto_route"] = true, ["strict_route"] = false, ["dns_mode"] = "native", ["dns_address"] = new JsonArray(dnsAddress),
                    ["stack"] = "gvisor", ["route_exclude_address"] = exclusions },
                new JsonObject { ["type"] = "direct", ["tag"] = "system-dns", ["listen"] = dnsAddress, ["listen_port"] = 53 }),
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "socks", ["tag"] = "vpn-local", ["server"] = ProbeAddress(settings.ListenAddress), ["server_port"] = settings.ListenPort,
                    ["version"] = "5", ["connect_timeout"] = "3s" },
                new JsonObject { ["type"] = "direct", ["tag"] = "physical", ["bind_interface"] = physicalInterface, ["domain_resolver"] = "bootstrap" }),
            ["route"] = new JsonObject { ["rules"] = rules, ["final"] = "vpn-local", ["default_domain_resolver"] = "vpn-dns", ["find_process"] = true }
        };
    }
    public static string ProbeAddress(string address) => address == "0.0.0.0" ? "127.0.0.1" : address == "::" ? "::1" : address;
    private static JsonObject LogSettings() => new() { ["level"] = "info", ["timestamp"] = true };
    private static JsonObject DnsSettings(JsonArray servers) => new() { ["servers"] = servers, ["final"] = "vpn-dns", ["strategy"] = "ipv4_only" };
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
