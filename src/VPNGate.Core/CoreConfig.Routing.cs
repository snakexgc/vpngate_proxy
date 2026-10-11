using System.Text.Json.Nodes;

namespace VPNGate.Core;

public static partial class CoreConfig
{
    private static JsonObject Route(string field, string value, string outbound) => new() { [field] = value, ["action"] = "route", ["outbound"] = outbound };
    private static void AddTrafficRules(JsonArray rules, string vpn, bool hasVpn = true)
    {
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "physical" });
        rules.Add(Route("clash_mode", "direct", "physical"));
        rules.Add(hasVpn ? Route("clash_mode", "global", vpn) : new JsonObject { ["clash_mode"] = "global", ["action"] = "reject" });
        var domains = Route("rule_set", "geosite-cn", "physical"); domains["clash_mode"] = "rule"; rules.Add(domains);
        if (hasVpn) rules.Add(new JsonObject { ["clash_mode"] = "rule", ["action"] = "resolve", ["strategy"] = "ipv4_only" });
        var addresses = Route("rule_set", "geoip-cn", "physical"); addresses["clash_mode"] = "rule"; rules.Add(addresses);
        if (!hasVpn) rules.Add(new JsonObject { ["action"] = "reject" });
    }
    private static JsonObject TrafficRoute(JsonArray rules, string vpn, bool findProcess = false, bool hasVpn = true) => new() {
        ["rules"] = rules, ["final"] = vpn, ["default_domain_resolver"] = hasVpn ? "vpn-dns" : "direct-dns", ["find_process"] = findProcess,
        ["rule_set"] = new RoutingRuleSets().Configuration()
    };
    private static JsonObject PhysicalOutbound(string physical)
    {
        var outbound = new JsonObject { ["type"] = "direct", ["tag"] = "physical", ["domain_resolver"] = "direct-dns" };
        if (physical.Length > 0) outbound["bind_interface"] = physical;
        return outbound;
    }
    private static JsonObject DirectDns(string tag, string physical)
    {
        var dns = new JsonObject { ["type"] = "https", ["tag"] = tag, ["server"] = "223.5.5.5", ["server_port"] = 443,
            ["path"] = "/dns-query", ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "dns.alidns.com" } };
        if (physical.Length > 0) dns["bind_interface"] = physical;
        return dns;
    }
    private static JsonObject TrafficDns(string vpn, string physical, bool hasVpn = true)
    {
        var servers = new JsonArray(DirectDns("direct-dns", physical), DirectDns("bootstrap", physical));
        if (hasVpn) servers.Add(HttpsDns("vpn-dns", vpn));
        JsonObject VpnRule(string field, string value) => hasVpn ? new() { [field] = value, ["action"] = "route", ["server"] = "vpn-dns" }
            : new() { [field] = value, ["action"] = "predefined", ["rcode"] = "REFUSED" };
        var rules = new JsonArray(
            VpnRule("inbound", "probe-vpn"),
            new JsonObject { ["clash_mode"] = "direct", ["action"] = "route", ["server"] = "direct-dns" },
            VpnRule("clash_mode", "global"),
            new JsonObject { ["clash_mode"] = "rule", ["rule_set"] = "geosite-cn", ["action"] = "route", ["server"] = "direct-dns" });
        if (!hasVpn) rules.Add(new JsonObject { ["action"] = "predefined", ["rcode"] = "REFUSED" });
        return new() { ["servers"] = servers, ["rules"] = rules, ["final"] = hasVpn ? "vpn-dns" : "direct-dns",
            ["strategy"] = "ipv4_only", ["independent_cache"] = true };
    }
}
