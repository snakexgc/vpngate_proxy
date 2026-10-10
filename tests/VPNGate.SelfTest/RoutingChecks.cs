using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNGate.Core;

internal static class RoutingChecks
{
    public static async Task RunAsync(Dictionary<string, object> report, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "VPNGate-RoutingTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var traffic = new RuleTraffic();
            foreach (var asset in RoutingRuleSets.Assets)
                traffic.Content[asset.Tag] = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "runtime", asset.FileName));
            var manager = new RoutingRuleSets(root, _ => new RuleHandler(traffic));
            var settings = new AppSettings();
            await manager.EnsureAsync();
            check(RoutingRuleSets.Assets.All(a => File.Exists(manager.FilePath(a.Tag))), "bundled rules allow the first startup without a network request");
            await manager.UpdateAsync(settings, true);
            check(traffic.Requests == 2 && manager.State.LastChecked is not null, "both country datasets are checked and the check time is persisted");
            await new RoutingRuleSets(root, _ => new RuleHandler(traffic)).UpdateAsync(settings, false);
            check(traffic.Requests == 2, "automatic checks reuse the persisted daily update interval across restarts");
            traffic.NotModified = true;
            await manager.UpdateAsync(settings, true);
            check(traffic.ConditionalRequests == 2 && traffic.Requests == 4, "manual updates use ETags and preserve unchanged rule data on HTTP 304");
            traffic.NotModified = false;

            // A valid replacement must be parsed by the actual pinned sing-box core.
            var fixture = Path.Combine(root, "replacement.json");
            var compiled = Path.Combine(root, "replacement.srs");
            await File.WriteAllTextAsync(fixture, """{"version":3,"rules":[{"ip_cidr":["198.51.100.7/32"]}]}""");
            await CoreCommand(["rule-set", "compile", fixture, "-o", compiled]);
            traffic.Content["geoip-cn"] = await File.ReadAllBytesAsync(compiled);
            await manager.UpdateAsync(settings, true);
            check((await File.ReadAllBytesAsync(manager.FilePath("geoip-cn"))).SequenceEqual(traffic.Content["geoip-cn"]), "valid binary country rules are installed after native validation");
            var valid = await File.ReadAllBytesAsync(manager.FilePath("geoip-cn"));
            var previousGeosite = await File.ReadAllBytesAsync(manager.FilePath("geosite-cn"));
            await File.WriteAllTextAsync(fixture, """{"version":3,"rules":[{"domain":["routing-cn.test"]}]}""");
            await CoreCommand(["rule-set", "compile", fixture, "-o", compiled]);
            traffic.Content["geosite-cn"] = await File.ReadAllBytesAsync(compiled);
            traffic.Content["geoip-cn"] = Encoding.UTF8.GetBytes("<html>not a rule set</html>");
            try { await manager.UpdateAsync(settings, true); throw new Exception("Invalid rules were accepted."); } catch (IOException) { }
            check((await File.ReadAllBytesAsync(manager.FilePath("geoip-cn"))).SequenceEqual(valid) && manager.State.Error.Length > 0,
                "failed validation retains working rules and records the failure");
            check((await File.ReadAllBytesAsync(manager.FilePath("geosite-cn"))).SequenceEqual(previousGeosite),
                "a failed IP dataset does not partially install a new domain dataset");

            await CheckNativeRouting(manager, settings, root, report, check);
        }
        finally
        {
            // This test owns only the files it generated in its unique temporary directory.
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
    private static async Task CheckNativeRouting(RoutingRuleSets manager, AppSettings settings, string root,
        Dictionary<string, object> report, Action<bool, string> check)
    {
        using var recorder = new SocksRecorder();
        settings.ListenPort = recorder.Port;
        var localPort = EngineRuntime.FreePort(); var control = RoutingControl.Create(localPort, recorder.Port);
        var config = CoreConfig.BuildTun(settings, "Ethernet", control);
        config["inbounds"] = new JsonArray(new JsonObject { ["type"] = "mixed", ["tag"] = "local", ["listen"] = "127.0.0.1", ["listen_port"] = localPort });
        config["outbounds"]![1]!.AsObject().Remove("bind_interface");
        foreach (var server in config["dns"]!["servers"]!.AsArray()) server!.AsObject().Remove("bind_interface");
        config["route"]!["rule_set"] = manager.Configuration();
        var path = Path.Combine(root, "core.json"); await File.WriteAllTextAsync(path, CoreConfig.Serialize(config));
        await CoreCommand(["check", "-c", path]);
        var start = new ProcessStartInfo(EngineRuntime.CorePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "--disable-color", "-c", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var job = new ProcessJob(); job.Add(process);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task Read(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) logs.Enqueue(line); }
        var readers = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
        try
        {
            for (var i = 0; i < 100; i++)
            {
                try { if (await control.GetModeAsync() == "rule") break; }
                catch (HttpRequestException) { }
                if (process.HasExited) throw new IOException(string.Join('\n', logs));
                await Task.Delay(50);
            }
            var pid = process.Id;
            async Task Request(string address)
            {
                using var client = ProbeService.ProxyClient(localPort, 2);
                try { using var response = await client.GetAsync(address); } catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
                await Task.Delay(100);
            }
            await control.SetModeAsync("global"); await Request("http://198.51.100.7/");
            check(recorder.Connections == 1, "global mode sends even a matched country IP through the VPN outbound");
            await control.SetModeAsync("direct"); await Request("http://198.51.100.7:81/");
            check(recorder.Connections == 1, "direct mode bypasses the VPN outbound");
            await control.SetModeAsync("rule"); await Request("http://198.51.100.7:81/");
            check(recorder.Connections == 1, "rule mode directly routes an address in the binary country IP dataset");
            await Request("http://203.0.113.7/");
            check(recorder.Connections == 2, "unmatched rule-mode traffic uses the VPN outbound");
            var replacement = Path.Combine(root, "replacement.json");
            await File.WriteAllTextAsync(replacement, """{"version":3,"rules":[{"ip_cidr":["203.0.113.7/32"]}]}""");
            var next = Path.Combine(root, "next.srs"); await CoreCommand(["rule-set", "compile", replacement, "-o", next]);
            File.Move(next, manager.FilePath("geoip-cn"), true);
            await Task.Delay(1000); await Request("http://203.0.113.7:81/");
            check(recorder.Connections == 2, "running sing-box reloads atomically replaced country rules without restarting");
            check(process.Id == pid && !process.HasExited, "all mode switches and rule updates preserve the same core process");
            check(logs.Any(l => l.Contains("outbound/direct[physical]", StringComparison.Ordinal)), "native route logs confirm the physical direct outbound is exercised");
            report["routingLogSample"] = logs.Where(l => l.Contains("outbound/", StringComparison.Ordinal)).Take(10).ToArray();
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(); await readers;
        }
    }
    private static async Task CoreCommand(string[] arguments)
    {
        var start = new ProcessStartInfo(EngineRuntime.CorePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        if (process.ExitCode != 0) throw new IOException(await errors);
        await errors;
    }
    private sealed class RuleTraffic
    {
        public int Requests, ConditionalRequests;
        public bool NotModified;
        public Dictionary<string, byte[]> Content { get; } = new();
    }
    private sealed class RuleHandler(RuleTraffic traffic) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            traffic.Requests++; if (request.Headers.IfNoneMatch.Count > 0) traffic.ConditionalRequests++;
            var tag = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(traffic.NotModified ? HttpStatusCode.NotModified : HttpStatusCode.OK) {
                Content = new ByteArrayContent(traffic.Content[tag]) };
            response.Headers.ETag = new('"' + Convert.ToHexString(SHA256.HashData(traffic.Content[tag])) + '"');
            return Task.FromResult(response);
        }
    }
    private sealed class SocksRecorder : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _connections;
        public int Connections => Volatile.Read(ref _connections);
        public int Port { get; }
        public SocksRecorder() { _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _ = Accept(); }
        private async Task Accept()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Handle(client); } }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException) { }
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); var header = new byte[2]; await stream.ReadExactlyAsync(header, _stop.Token);
                var methods = new byte[header[1]]; await stream.ReadExactlyAsync(methods, _stop.Token); await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
                var command = new byte[4]; await stream.ReadExactlyAsync(command, _stop.Token);
                var size = command[3] == 1 ? 4 : command[3] == 4 ? 16 : stream.ReadByte();
                await stream.ReadExactlyAsync(new byte[size + 2], _stop.Token);
                Interlocked.Increment(ref _connections);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, _stop.Token);
                using var request = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await request.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
    }
}
