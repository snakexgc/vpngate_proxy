using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VPNGate.Core;

// Public VPNGate profiles are data only: no OpenVPN executable, scripts, plugins or file references are executed.
public static partial class OpenVpnProfile
{
    [GeneratedRegex(@"<(ca|cert|key|tls-auth|tls-crypt)>\s*([\s\S]*?)</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineBlock();
    [GeneratedRegex("\"([^\"]*)\"|'([^']*)'|([^\\s]+)")]
    private static partial Regex Tokens();

    private static List<string[]> Directives(string profile)
    {
        var outside = InlineBlock().Replace(profile, "");
        return outside.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] is not ('#' or ';' or '<'))
            .Select(l => Tokens().Matches(l).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToArray())
            .Where(t => t.Length > 0).ToList();
    }
    public static bool HasTcpTransport(string profile)
    {
        var lines = Directives(profile);
        var proto = lines.LastOrDefault(t => t[0] == "proto")?.ElementAtOrDefault(1) ?? "udp";
        return lines.Where(t => t[0] == "remote").Any(t => (t.ElementAtOrDefault(3) ?? proto).StartsWith("tcp", StringComparison.OrdinalIgnoreCase));
    }

    public static JsonObject ToEndpoint(string profile, ProxyEndpoint proxy, int connectionTimeoutSeconds)
    {
        var lines = Directives(profile);
        var blocks = InlineBlock().Matches(profile).GroupBy(m => m.Groups[1].Value.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => string.Join('\n', g.Select(m => m.Groups[2].Value.Trim())));
        string? Value(string key) => lines.LastOrDefault(t => t[0].Equals(key, StringComparison.OrdinalIgnoreCase))?.ElementAtOrDefault(1);
        var proto = Value("proto") ?? "udp";
        var servers = new JsonArray();
        foreach (var line in lines.Where(t => t[0] == "remote"))
        {
            var network = (line.ElementAtOrDefault(3) ?? proto).StartsWith("tcp", StringComparison.OrdinalIgnoreCase) ? "tcp" : "udp";
            // HTTP CONNECT and SOCKS4 cannot carry UDP. Never pretend a UDP server is TCP.
            if (network == "udp" && (proxy.IsHttp || proxy.Scheme != "socks5")) continue;
            if (line.Length < 3 || !int.TryParse(line[2], out var port) || port is < 1 or > 65535) continue;
            servers.Add(new JsonObject { ["server"] = line[1], ["server_port"] = port, ["network"] = network });
        }
        if (servers.Count == 0) throw new InvalidDataException("该节点没有可通过当前上游代理连接的传输方式。HTTP/HTTPS/SOCKS4 需要 TCP 节点；UDP 需要支持 UDP 的 SOCKS5 代理。");
        if (!blocks.TryGetValue("ca", out var ca) || !ca.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            throw new InvalidDataException("节点配置缺少内嵌 CA 证书。");
        var tls = new JsonObject { ["certificate"] = ca };
        foreach (var (block, field) in new[] { ("cert", "client_certificate"), ("key", "client_key") })
            if (blocks.TryGetValue(block, out var content)) tls[field] = content;
        if (tls.ContainsKey("client_certificate") != tls.ContainsKey("client_key")) throw new InvalidDataException("节点的客户端证书和密钥不完整。");
        if (Value("remote-cert-tls") is { } remoteCert) tls["remote_certificate_tls"] = remoteCert;
        if (Value("ns-cert-type") is { } nsCert) tls["ns_certificate_type"] = nsCert;
        if (Value("tls-version-min") is { } tlsVersion) tls["version_min"] = tlsVersion;
        var name = lines.LastOrDefault(t => t[0] == "verify-x509-name");
        if (name?.Length >= 2) { tls["server_name"] = name[1]; tls["server_name_type"] = name.ElementAtOrDefault(2) ?? "subject"; }
        foreach (var wrap in new[] { "tls-auth", "tls-crypt" })
            if (blocks.TryGetValue(wrap, out var key))
            {
                var control = new JsonObject { ["type"] = wrap.Replace("tls-", ""), ["key"] = key };
                if (wrap == "tls-auth" && Value("key-direction") is { } direction) control["direction"] = direction == "1" ? "client" : "server";
                tls["control_wrap"] = control;
            }
        var cipher = Value("cipher") ?? "AES-128-CBC";
        var ciphers = (Value("data-ciphers") ?? $"AES-256-GCM:AES-128-GCM:{cipher}").Split(':', StringSplitOptions.RemoveEmptyEntries).Distinct();
        var endpoint = new JsonObject { ["type"] = "openvpn-client", ["tag"] = "vpn", ["servers"] = servers, ["detour"] = "upstream",
            ["system"] = false, ["tls"] = tls, ["data_ciphers"] = new JsonArray(ciphers.Select(c => JsonValue.Create(c)).ToArray()),
            ["data_ciphers_fallback"] = Value("data-ciphers-fallback") ?? cipher, ["auth"] = Value("auth") ?? "SHA1",
            ["route_no_pull"] = true, ["allow_compression"] = "asym", ["handshake_window"] = $"{connectionTimeoutSeconds}s" };
        if (Value("comp-lzo") is { } lzo) endpoint["compression_lzo"] = lzo;
        if (Value("compress") is { } compression) endpoint["compression"] = compression;
        if (int.TryParse(Value("tun-mtu"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtu) && mtu is >= 576 and <= 9000) endpoint["mtu"] = mtu;
        if (lines.Any(t => t[0] == "auth-user-pass")) { endpoint["username"] = "vpn"; endpoint["password"] = "vpn"; }
        return endpoint;
    }
}
