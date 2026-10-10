using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using VPNGate.Core;

if (args is ["--engine-host", var core, var hostConfig]) return await EngineHost.RunAsync(core, hostConfig);
if (args is ["--tun-worker", var pipeName]) return await TunWorker.RunAsync(pipeName);
if (args is ["--dns-guard", var pid, var ticks, var identity]) return await SystemDnsLease.GuardAsync(int.Parse(pid), long.Parse(ticks), identity);

var outputIndex = Array.IndexOf(args, "--output");
var outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : null;
var report = new Dictionary<string, object>();
var checks = new List<string>();
void Check(bool success, string name) { if (!success) throw new Exception(name); checks.Add(name); Console.WriteLine($"PASS {name}"); }
void Throws(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true, name); return; } catch (InvalidDataException) { Check(true, name); return; } throw new Exception(name); }

try
{
    if (args.Contains("--ip-cache"))
    {
        var root = Path.Combine(Path.GetTempPath(), "VPNGateXray-IpCacheTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppDataStore(root);
            var old = new IpNetworkInfo("198.51.100.1", true, false, "Cached ISP", "Cached Org", "AS64500",
                DateTimeOffset.UtcNow.AddDays(-6));
            var expired = new IpNetworkInfo("198.51.100.3", true, false, "Old ISP", "Old Org", "AS64502",
                DateTimeOffset.UtcNow.AddDays(-8));
            store.SaveIpNetworkCache(new() { [old.Ip] = old, [expired.Ip] = expired });
            var traffic = new CacheTestTraffic();
            Func<AppSettings, HttpMessageHandler> factory = _ =>
            { traffic.Transports++; return new CacheTestHandler(traffic); };
            var client = new IpNetworkInfoClient(store, factory);
            var first = await client.LookupAsync(["198.51.100.1", "198.51.100.1", "198.51.100.2", "198.51.100.2", expired.Ip],
                new AppSettings(), null, CancellationToken.None);
            Check(traffic.Requests == 1 && traffic.LastIps.SequenceEqual(["198.51.100.2", expired.Ip]),
                "only uncached or expired IPs are sent, with duplicate addresses removed");
            Check(first[old.Ip] == old, "six-day-old information is reused without an API request");
            Check(first[expired.Ip].CheckedAt > expired.CheckedAt && first[expired.Ip].Hosting == false,
                "expired information is refreshed and persisted");
            await client.LookupAsync(first.Keys, new AppSettings(), null, CancellationToken.None);
            Check(traffic.Requests == 1 && traffic.Transports == 1, "repeated refresh creates no network transport or API request");
            var restarted = new IpNetworkInfoClient(new AppDataStore(root), factory);
            await restarted.LookupAsync(first.Keys, new AppSettings { UpstreamProxy = "offline" }, null, CancellationToken.None);
            Check(traffic.Requests == 1 && traffic.Transports == 1, "persisted cache survives a new client and works without a usable proxy");
            Check(store.LoadIpApiNextRequest() > DateTimeOffset.UtcNow, "API request cooldown is persisted");
            var boundary = DateTimeOffset.UtcNow;
            var atExpiry = new IpNetworkInfo("198.51.100.3", false, false, "ISP", "", "", boundary.AddDays(-7));
            Check(!atExpiry.NeedsLookup(boundary.AddTicks(-1)) && atExpiry.NeedsLookup(boundary),
                "information expires exactly at the seven-day boundary");
            Check(!new IpNetworkInfo("198.51.100.3", null, null, "Known ISP", "", "",
                boundary.AddDays(-6)).NeedsLookup(boundary),
                "partial information also receives the seven-day cache lifetime");
            Check(!new IpNetworkInfo("198.51.100.4", null, null, "", "", "", DateTimeOffset.UtcNow, "failed")
                .NeedsLookup(DateTimeOffset.UtcNow), "failed results are not retried immediately");
            report["apiRequests"] = traffic.Requests; report["queriedIps"] = traffic.LastIps;
        }
        finally
        {
            if (Directory.Exists(root))
            { foreach (var file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
        }
    }
    else if (args.Contains("--node-types"))
    {
        var store = new AppDataStore();
        var nodes = store.LoadNodes();
        var infoClient = new IpNetworkInfoClient(store);
        var results = await infoClient.LookupAsync(nodes.Select(n => n.Ip).Append("1.1.1.1"), new AppSettings(), null, CancellationToken.None);
        Check(results["1.1.1.1"].Hosting == true, "real metadata identifies a datacenter address");
        Check(nodes.Any(n => results[IpNetworkInfoClient.Canonical(n.Ip)].Hosting == false), "real VPNGate IPs receive non-hosting metadata");
        infoClient.ApplyCached(nodes);
        Check(nodes.All(n => n.NetworkInfo is not null), "IP type cache populates existing node rows");
        report["datacenter"] = nodes.Count(n => n.NetworkInfo?.Hosting == true);
        report["residentialInferred"] = nodes.Count(n => n.NetworkInfo?.Hosting == false);
        report["unknown"] = nodes.Count(n => n.NetworkInfo?.Hosting is null);
    }
    else if (args.Contains("--tun-only"))
    {
        var settings = new AppSettings { ListenPort = EngineRuntime.FreePort() };
        await using var tunOnly = new TunSession();
        tunOnly.Log += Console.WriteLine;
        await tunOnly.StartAsync(settings, CancellationToken.None);
        Check(tunOnly.IsRunning && !await Listening(settings.ListenPort), "standalone TUN starts without OpenVPN");
    }
    else if (args.Contains("--tun"))
    {
        var settings = new AppSettings { ConnectionTimeoutSeconds = 25, ListenPort = EngineRuntime.FreePort() };
        var nodes = await new VpnGateClient().FetchAsync(settings, CancellationToken.None);
        var v2ray = Process.GetProcessesByName("v2rayN").Select(p => p.Id).ToArray();
        var xray = Process.GetProcessesByName("xray").Select(p => p.Id).ToArray();
        var before = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Name).ToArray();
        var beforeDnsRules = await SystemDnsLease.OwnedRuleCountAsync();
        var coreLogs = new List<string>();
        report["coreLogs"] = coreLogs;
        var candidates = await ProbeService.TestManyAsync(nodes.Where(n => n.SupportsTcp && !n.HostName.StartsWith("public-vpn-", StringComparison.Ordinal)).Take(8).ToArray(), settings, null, null, null, CancellationToken.None);
        await using (var beforeConnection = new TunSession())
        {
            beforeConnection.Log += line => { lock (coreLogs) coreLogs.Add(line); };
            await beforeConnection.StartAsync(settings, CancellationToken.None);
            Check(beforeConnection.IsRunning && !await Listening(settings.ListenPort), "TUN can start without any OpenVPN connection");
            Check(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == CoreConfig.TunName), "independent TUN creates a real adapter");
        }
        Check(!System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == CoreConfig.TunName), "TUN can stop without any OpenVPN connection");
        await using var tun = new TunSession();
        tun.Log += line => { lock (coreLogs) coreLogs.Add(line); Console.WriteLine(line); };
        await tun.StartAsync(settings, CancellationToken.None);
        EngineSession? connected = null;
        foreach (var usable in nodes.Where(n => candidates.Any(r => r.Success && r.NodeId == n.Id)))
        {
            var attempt = new EngineSession();
            attempt.Log += line => { lock (coreLogs) coreLogs.Add(line); Console.WriteLine(line); };
            try { await attempt.StartAsync(usable, settings, "127.0.0.1", settings.ListenPort, CancellationToken.None); connected = attempt; break; }
            catch (Exception exception) { Console.WriteLine($"Node {usable.Ip}: {exception.Message}"); await attempt.DisposeAsync(); }
        }
        if (connected is null) throw new IOException("No current VPNGate node accepted the TUN test connection.");
        await using (var session = connected)
        {
            var samples = await ProbeService.MeasureAsync(session.Port, settings, CancellationToken.None);
            report["proxyLatencyMs"] = ProbeService.Median(samples);
            Check(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == CoreConfig.TunName), "VPNGate-TUN adapter created");
            var proxyExit = await ProbeService.ExitIpAsync(session.Port, CancellationToken.None);
            NativeDns.Flush();
            var freshDns = await Dns.GetHostAddressesAsync("www.cloudflare.com");
            Check(freshDns.Length > 0, "fresh system DNS lookup works during TUN coexistence");
            report["systemDnsUsesLocalListener"] = coreLogs.Any(l => l.Contains("inbound/direct[system-dns]: inbound", StringComparison.Ordinal));
            Check(report["systemDnsUsesLocalListener"] is true, "system DNS goes through the OpenVPN DNS listener");
            if (args.Contains("--crash"))
            {
                report["crashStarted"] = true;
                report["hostProcessId"] = session.HostProcessId!; report["tunWorkerProcessId"] = tun.WorkerProcessId!;
                if (outputPath is null) throw new ArgumentException("Crash check requires --output.");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Process.GetCurrentProcess().Kill();
                await Task.Delay(Timeout.Infinite);
            }
            using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
            var trace = await direct.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var directExit = trace.Split('\n').First(l => l.StartsWith("ip="))[3..];
            report["proxyExit"] = proxyExit; report["tunExit"] = directExit;
            Check(proxyExit == directExit, "system traffic exits through the same OpenVPN node");
            using var upstream = new HttpClient(new SocketsHttpHandler { Proxy = new AppSettings().Validate().ToWebProxy(), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };
            Check((await upstream.GetAsync("https://www.gstatic.com/generate_204")).StatusCode == HttpStatusCode.NoContent, "existing 10808 upstream remains usable during TUN");
            Check(v2ray.SequenceEqual(Process.GetProcessesByName("v2rayN").Select(p => p.Id)), "v2rayN remains running");
            Check(xray.SequenceEqual(Process.GetProcessesByName("xray").Select(p => p.Id)), "existing Xray remains running");
            var vpnPid = session.HostProcessId;
            await tun.StopAsync();
            Check(session.IsRunning && session.HostProcessId == vpnPid, "disabling TUN preserves the same OpenVPN process");
            Check((await ProbeService.MeasureAsync(session.Port, settings, CancellationToken.None)).Count == settings.ProbeSamples, "local proxy still works after TUN is disabled");
            var workerPid = tun.WorkerProcessId;
            await tun.StartAsync(settings, CancellationToken.None);
                Check(session.IsRunning && session.HostProcessId == vpnPid, "enabling TUN on a connected node does not reconnect OpenVPN");
                Check(tun.WorkerProcessId == workerPid, "TUN toggles reuse the authorized worker");
                await session.DisposeAsync();
                Check(tun.IsRunning, "disconnecting OpenVPN leaves TUN running");
            await tun.StopAsync();
        }
        await Task.Delay(1000);
        Check(!System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == CoreConfig.TunName), "TUN adapter removed after disconnect");
        Check(before.All(n => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(current => current.Name == n)), "all pre-existing adapters preserved");
        Check(await SystemDnsLease.OwnedRuleCountAsync() == beforeDnsRules, "temporary DNS policy removed after disconnect");
    }
    else if (args.Contains("--live"))
    {
        var settings = new AppSettings { ConnectionTimeoutSeconds = 20, ProbeTimeoutSeconds = 10 };
        var nodes = await new VpnGateClient().FetchAsync(settings, CancellationToken.None);
        Check(nodes.Count > 10, "fetch real VPNGate nodes through 10808");
        Check(nodes.All(n => n.OpenVpnConfig.Contains("BEGIN CERTIFICATE")), "node profiles contain real certificates");
        var countIndex = Array.IndexOf(args, "--count");
        var count = countIndex >= 0 ? int.Parse(args[countIndex + 1]) : 4;
        var toTest = args.Contains("--all") ? nodes : nodes.Take(count).ToArray();
        var progress = new List<ProbeResult>();
        var results = await ProbeService.TestManyAsync(toTest, settings, null, (result, completed, total) =>
        {
            lock (progress) progress.Add(result);
            Console.WriteLine($"LIVE {completed}/{total} {result.NodeId} {(result.Success ? $"{result.LatencyMs:0}ms" : result.Error)}");
        }, null, CancellationToken.None);
        report["nodes"] = nodes.Count; report["results"] = results;
        Check(results.Count == toTest.Count, "every requested node receives an end-to-end test result");
        Check(results.Any(r => r.Success && r.Samples?.Count == 3), "successful nodes have three HTTPS latency samples");
        var usable = nodes.First(n => results.Any(r => r.Success && r.NodeId == n.Id));
        var port = EngineRuntime.FreePort();
        await using (var session = new EngineSession())
        {
            await session.StartAsync(usable, settings, "127.0.0.1", port, CancellationToken.None);
            report["exitIp"] = await ProbeService.ExitIpAsync(port, CancellationToken.None);
            var socks = settings.Copy(); socks.UpstreamProxy = $"socks5://127.0.0.1:{port}";
            using var client = new HttpClient(new SocketsHttpHandler { Proxy = socks.Validate().ToWebProxy(), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };
            Check((await client.GetAsync(settings.ProbeUrl)).StatusCode == HttpStatusCode.NoContent, "local SOCKS5 goes through the real OpenVPN tunnel");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var cancelled = await ProbeService.TestManyAsync(nodes.Take(12).ToArray(), settings, null, null, null, cancel.Token);
            Check(cancelled.Count < 12, "cancellation prevents queued nodes from starting");
            Check(session.IsRunning, "cancelling bulk tests preserves the active connection");
        }
        Check(!await Listening(port), "disconnect releases the local proxy port");
        var socksUpstream = settings.Copy(); socksUpstream.UpstreamProxy = "socks5://127.0.0.1:10808";
        var socksResult = await ProbeService.TestNodeAsync(usable, socksUpstream, null, CancellationToken.None);
        Check(socksResult.Success, "SOCKS5 upstream transports a real OpenVPN connection");
        report["socksUpstream"] = socksResult;
        var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        await using (var conflict = new EngineSession())
        {
            try { await conflict.StartAsync(usable, settings, "127.0.0.1", ((IPEndPoint)occupied.LocalEndpoint).Port, CancellationToken.None); throw new Exception("Port conflict was ignored"); }
            catch (IOException) { Check(true, "occupied local port is rejected"); }
            finally { occupied.Stop(); }
        }
    }
    else
    {
        Check(ProxyEndpoint.Parse(" http://127.0.0.1:10808 ").Port == 10808, "HTTP proxy parsing");
        Check(ProxyEndpoint.Parse("socks5h://user:p%40ss@localhost:1080").Password == "p@ss", "SOCKS5 remote DNS alias and percent-encoded auth");
        Check(ProxyEndpoint.Parse("https://[::1]").Port == 443, "IPv6 HTTPS proxy parsing");
        Check(ProxyEndpoint.Parse("socks://localhost").Port == 1080, "SOCKS alias and default port");
        Check(ProxyEndpoint.Parse("socks4a://localhost:1080").ToOutbound()["version"]!.GetValue<string>() == "4a", "SOCKS4a preserved");
        using var metadata = JsonDocument.Parse("""
            [
              {"query":"198.51.100.1","status":"success","hosting":true},
              {"query":"198.51.100.2","status":"success","hosting":false},
              {"query":"198.51.100.3","status":"success"},
              {"query":"198.51.100.4","status":"fail","hosting":false},
              {"query":"198.51.100.99","status":"success","hosting":true}
            ]
            """);
        var types = IpNetworkInfoClient.Parse(metadata.RootElement, ["198.51.100.1", "198.51.100.2", "198.51.100.3", "198.51.100.4"]);
        Check(types["198.51.100.1"].Label == "机房" && types["198.51.100.2"].Label.Contains("推测"), "IP types preserve the distinction between detected hosting and residential inference");
        Check(types["198.51.100.3"].Hosting is null && types["198.51.100.4"].Hosting is null, "missing or failed IP metadata never defaults to residential");
        Check(!types.ContainsKey("198.51.100.99"), "unrequested IP metadata is ignored");
        var publicRelay = new VpnNode { HostName = "public-vpn-97", Ip = "219.100.37.83",
            NetworkInfo = new("219.100.37.83", false, false, "", "", "", DateTimeOffset.UtcNow) };
        Check(publicRelay.IsDataCenter == true && publicRelay.NodeType == "机房（官方中继）", "known VPNGate relay source overrides an external non-hosting flag");
        Check(new VpnNode { HostName = "volunteer", Ip = "219.100.37.176" }.IsVpnGatePublicRelay &&
            !new VpnNode { HostName = "volunteer", Ip = "219.100.137.176" }.IsVpnGatePublicRelay, "official relay subnet matching uses address bytes rather than loose text matching");
        Throws(() => ProxyEndpoint.Parse("ftp://localhost:21"), "unsupported proxy rejected");
        Throws(() => ProxyEndpoint.Parse("http://localhost:1080/path"), "proxy URL path rejected");
        Throws(() => new AppSettings { ListenPort = 10808 }.Validate(), "loopback proxy recursion rejected");
        Throws(() => new AppSettings { ProbeUrl = "http://example.com" }.Validate(), "plaintext latency probe rejected");
        var encrypted = SecretProtection.Protect("test 密码 : @ \" ");
        Check(SecretProtection.Unprotect(encrypted) == "test 密码 : @ \" ", "Windows DPAPI credential round trip");
        Check(!encrypted.Contains("密码"), "stored credentials are encrypted");
        var profile = "client\ndev tun\nproto tcp\nremote 198.51.100.1 443\ncipher AES-128-CBC\nup evil.exe\nplugin bad.dll\n<ca>\n-----BEGIN CERTIFICATE-----\nplaceholder\n-----END CERTIFICATE-----\n</ca>\n";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(profile));
        var csv = $"*vpn_servers\r\n#HostName,IP,CountryLong,CountryShort,Message,OpenVPN_ConfigData_Base64\r\nnode,198.51.100.1,Japan,JP,\"hello, \"\"world\"\"\",{encoded}\r\nnode,198.51.100.1,Japan,JP,duplicate,{encoded}\r\nbad,not-ip,Japan,JP,bad,{encoded}\r\ninvalid,198.51.100.2,Japan,JP,invalid,***\r\n*\r\n";
        var nodes = VpnGateClient.Parse(csv);
        Check(nodes.Count == 1 && nodes[0].Message == "hello, \"world\"", "VPNGate CSV quotes, trailer, deduplication and malformed rows");
        Check(nodes[0].SupportsTcp, "TCP capability detected");
        var config = CoreConfig.Serialize(CoreConfig.Build(nodes[0], new AppSettings(), "127.0.0.1", 10800));
        Check(!config.Contains("evil.exe") && !config.Contains("bad.dll"), "untrusted profile scripts and plugins cannot execute");
        Check(!config.Contains("\"type\": \"direct\""), "local proxy mode has no direct-Internet fallback");
        var udp = profile.Replace("proto tcp", "proto udp");
        Throws(() => OpenVpnProfile.ToEndpoint(udp, ProxyEndpoint.Parse("http://localhost:8080"), 30), "HTTP upstream rejects UDP-only nodes");
        Check(OpenVpnProfile.ToEndpoint(udp, ProxyEndpoint.Parse("socks5://localhost:1080"), 30)["servers"]!.AsArray().Count == 1, "SOCKS5 can use UDP transport");
        Check(ProbeService.Median([100, 4, 2]) == 4 && ProbeService.Median([2, 8, 4, 6]) == 5, "latency median ignores sample order");
        var tun = CoreConfig.BuildTun(new AppSettings(), "Ethernet");
        Check(!tun.ContainsKey("endpoints") && tun["route"]!["final"]!.GetValue<string>() == "vpn-local", "independent TUN requires no OpenVPN node and only uses the local VPN proxy");
        Check(tun["outbounds"]![0]!["server_port"]!.GetValue<int>() == 10800, "TUN bridges to the configured local proxy");
        Check(tun["route"]!["rules"]!.AsArray().Any(r => r!["process_name"] is { } names && names.AsArray().Any(p => p!.GetValue<string>() == "xray.exe")), "TUN prevents upstream Xray routing recursion");
        Check(tun["outbounds"]![1]!["bind_interface"]!.GetValue<string>() == "Ethernet", "upstream bypass binds to physical interface");
        var physicalName = CoreConfig.PhysicalInterface();
        Check(physicalName.Length == 0 || System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.Name == physicalName && !n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)),
            "automatic NIC detection never selects a Wintun adapter");
        EngineRuntime.Verify(EngineRuntime.CorePath); Check(true, "pinned core and signed Wintun payload checksums");
    }
    report["success"] = true;
}
catch (Exception exception) { report["success"] = false; report["error"] = exception.ToString(); Console.Error.WriteLine(exception); }
finally
{
    report["checks"] = checks; report["time"] = DateTimeOffset.Now;
    if (outputPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
return report["success"] is true ? 0 : 1;

static async Task<bool> Listening(int port)
{
    using var client = new TcpClient();
    try { await client.ConnectAsync("127.0.0.1", port); return true; } catch (SocketException) { return false; }
}

static class NativeDns
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")] public static extern bool Flush();
}

sealed class CacheTestTraffic
{
    public int Transports, Requests;
    public string[] LastIps = [];
}
sealed class CacheTestHandler(CacheTestTraffic traffic) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        traffic.Requests++;
        traffic.LastIps = await request.Content!.ReadFromJsonAsync<string[]>(cancellationToken: cancellationToken) ?? [];
        var result = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent.Create(traffic.LastIps.Select(ip => new { status = "success", query = ip, hosting = false,
                mobile = false, isp = "Test ISP", org = "Test Network", @as = "AS64501" })) };
        result.Headers.Add("X-Rl", "14"); result.Headers.Add("X-Ttl", "60");
        return result;
    }
}
