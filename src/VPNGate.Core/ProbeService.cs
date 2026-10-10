using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace VPNGate.Core;

public static class ProbeService
{
    public static HttpClient ProxyClient(int port, int timeoutSeconds = 10, string listenAddress = "127.0.0.1")
    {
        var host = listenAddress == "0.0.0.0" ? "127.0.0.1" : listenAddress == "::" ? "::1" : listenAddress;
        var handler = new SocketsHttpHandler { Proxy = new WebProxy(new UriBuilder("http", host, port).Uri) { BypassProxyOnLocal = false },
            UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds), AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds), DefaultRequestVersion = HttpVersion.Version11 };
    }
    public static async Task<IReadOnlyList<double>> MeasureAsync(int port, AppSettings settings, CancellationToken cancellationToken, string listenAddress = "127.0.0.1")
    {
        using var client = ProxyClient(port, settings.ProbeTimeoutSeconds, listenAddress);
        // Warm up DNS, CONNECT and TLS, then measure actual HTTPS round trips through the connected VPN.
        await RequestAsync(client, settings.ProbeUrl, cancellationToken);
        var samples = new List<double>();
        for (var i = 0; i < settings.ProbeSamples; i++)
        {
            var watch = Stopwatch.StartNew();
            await RequestAsync(client, settings.ProbeUrl, cancellationToken);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        return samples;
    }
    private static async Task RequestAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[4096]; var total = 0; int read;
        while ((read = await content.ReadAsync(bytes, cancellationToken)) != 0)
        { total += read; if (total > 1024 * 1024) throw new IOException("测速 URL 响应超过 1 MB，请使用轻量的测试地址。"); }
    }
    public static double Median(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) throw new ArgumentException("缺少延时采样。");
        var values = samples.Order().ToArray();
        return values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
    }
    public static async Task<ProbeResult> TestNodeAsync(VpnNode node, AppSettings settings, Action<string>? log, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await using var session = new EngineSession();
        session.Log += line => log?.Invoke($"[{node.HostName}] {line}");
        try
        {
            await session.StartAsync(node, settings, "127.0.0.1", EngineRuntime.FreePort(), cancellationToken);
            var connected = watch.Elapsed.TotalMilliseconds;
            var samples = await MeasureAsync(session.Port, settings, cancellationToken);
            return new(node.Id, true, Median(samples), connected, "", DateTimeOffset.Now, Samples: samples);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(node.Id, false, null, null, "测试已取消。", DateTimeOffset.Now, true); }
        catch (Exception exception) { return new(node.Id, false, null, null, exception.Message, DateTimeOffset.Now); }
    }
    public static async Task<IReadOnlyList<ProbeResult>> TestManyAsync(IReadOnlyList<VpnNode> nodes, AppSettings settings,
        Action<VpnNode, string>? status, Action<ProbeResult, int, int>? progress, Action<string>? log, CancellationToken cancellationToken)
    {
        settings.Validate();
        var results = new ProbeResult[nodes.Count];
        var completed = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, nodes.Count), new ParallelOptions { MaxDegreeOfParallelism = settings.TestConcurrency }, async (i, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            status?.Invoke(nodes[i], "连接中");
            var result = await TestNodeAsync(nodes[i], settings, log, cancellationToken);
            results[i] = result;
            progress?.Invoke(result, Interlocked.Increment(ref completed), nodes.Count);
        });
        return results.Where(r => r is not null).ToArray();
    }
    public static async Task<string> ExitIpAsync(int port, CancellationToken cancellationToken, string listenAddress = "127.0.0.1")
    {
        using var client = ProxyClient(port, listenAddress: listenAddress);
        var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken);
        return trace.Split('\n').FirstOrDefault(l => l.StartsWith("ip=", StringComparison.Ordinal))?[3..] ?? "未知";
    }
}
