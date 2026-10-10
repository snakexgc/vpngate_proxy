using System.Text.Json.Nodes;

namespace VPNGate.Core;

public static partial class CoreConfig
{
    private static JsonObject Route(string field, string value, string outbound) => new() { [field] = value, ["action"] = "route", ["outbound"] = outbound };
    private static void AddTrafficRules(JsonArray rules, string vpn)
    {
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "physical" });
        rules.Add(Route("clash_mode", "direct", "physical"));
        rules.Add(Route("clash_mode", "global", vpn));
        rules.Add(Route("rule_set", "geosite-cn", "physical"));
        rules.Add(new JsonObject { ["clash_mode"] = "rule", ["action"] = "resolve", ["strategy"] = "ipv4_only" });
        rules.Add(Route("rule_set", "geoip-cn", "physical"));
    }
    private static JsonObject TrafficRoute(JsonArray rules, string vpn, bool findProcess = false) => new() {
        ["rules"] = rules, ["final"] = vpn, ["default_domain_resolver"] = "vpn-dns", ["find_process"] = findProcess,
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
    private static JsonObject TrafficDns(string vpn, string physical) => new() {
        ["servers"] = new JsonArray(HttpsDns("vpn-dns", vpn), DirectDns("direct-dns", physical), DirectDns("bootstrap", physical)),
        ["rules"] = new JsonArray(
            new JsonObject { ["inbound"] = "probe-vpn", ["action"] = "route", ["server"] = "vpn-dns" },
            new JsonObject { ["clash_mode"] = "direct", ["action"] = "route", ["server"] = "direct-dns" },
            new JsonObject { ["clash_mode"] = "global", ["action"] = "route", ["server"] = "vpn-dns" },
            new JsonObject { ["rule_set"] = "geosite-cn", ["action"] = "route", ["server"] = "direct-dns" }),
        ["final"] = "vpn-dns", ["strategy"] = "ipv4_only", ["independent_cache"] = true
    };
}
