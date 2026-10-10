using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNGate.Core;

public static class RoutingModes
{
    public static void Validate(string mode)
    { if (mode is not ("global" or "rule" or "direct")) throw new ArgumentException("分流模式必须为全局、规则或直连。"); }
    public static string Label(string mode) => mode switch { "global" => "全局", "rule" => "规则", "direct" => "直连", _ => throw new ArgumentException("无效分流模式。") };
}

public sealed record RoutingControl(int Port, string Secret)
{
    public static RoutingControl Create(params int[] excludedPorts)
    {
        int port;
        do { port = EngineRuntime.FreePort(); } while (excludedPorts.Contains(port));
        return new(port, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    }
    public JsonObject Configuration(string mode) => new() {
        ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{Port}", ["secret"] = Secret,
            ["default_mode"] = mode, ["access_control_allow_origin"] = new JsonArray("http://127.0.0.1"),
            ["access_control_allow_private_network"] = false }
    };
    private HttpClient Client()
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
        { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        return client;
    }
    public async Task<string> GetModeAsync(CancellationToken cancellationToken = default)
    {
        using var client = Client();
        using var response = await client.GetAsync("configs", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("mode").GetString()!.ToLowerInvariant();
    }
    public async Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        RoutingModes.Validate(mode);
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Patch, "configs") { Content = JsonContent.Create(new { mode }) };
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (await GetModeAsync(cancellationToken) != mode) throw new IOException("内核没有确认新的分流模式。");
    }
}
