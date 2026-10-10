using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPNGate.Core;

public sealed record IpNetworkInfo(string Ip, bool? Hosting, bool? Mobile, string Isp, string Organization,
    string Asn, DateTimeOffset CheckedAt, string Error = "", string Source = "IP-API")
{
    [JsonIgnore] public string Label => Hosting switch { true => "机房", false => "住宅（推测）", null => "未知" };
    [JsonIgnore] public bool HasInformation => Hosting is not null || !string.IsNullOrWhiteSpace(Isp) ||
        !string.IsNullOrWhiteSpace(Organization) || !string.IsNullOrWhiteSpace(Asn);
    // Valid information expires after seven days; failed/empty results retain their shorter retry cooldown.
    public bool NeedsLookup(DateTimeOffset now) => CheckedAt + (HasInformation ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(5)) <= now;
    [JsonIgnore] public string Detail => Hosting is null ? $"IP 类型未知：{Error}\n来源：{Source}"
        : $"{(Hosting.Value ? "托管 / 数据中心 IP" : "非机房 IP，按住宅 / 运营商接入网络推断；不保证是真实家庭宽带。")}" +
          $"{(Mobile == true ? "\n识别为移动网络。" : "")}\n运营商：{Isp}\n组织：{Organization}\nASN：{Asn}\n来源：{Source}\n查询时间：{CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
}

public sealed class IpNetworkInfoClient
{
    private readonly AppDataStore store;
    private readonly Func<AppSettings, HttpMessageHandler> _createHandler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextRequest;
    public IpNetworkInfoClient(AppDataStore store) : this(store, settings => new SocketsHttpHandler {
        Proxy = settings.Validate().ToWebProxy(), UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(8),
        AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }) { }
    internal IpNetworkInfoClient(AppDataStore store, Func<AppSettings, HttpMessageHandler> createHandler)
    { this.store = store; _createHandler = createHandler; }
    public void ApplyCached(IEnumerable<VpnNode> nodes)
    {
        var cache = store.LoadIpNetworkCache();
        foreach (var node in nodes)
            if (cache.TryGetValue(Canonical(node.Ip), out var info)) node.NetworkInfo = info;
    }

    public async Task<IReadOnlyDictionary<string, IpNetworkInfo>> LookupAsync(IEnumerable<string> addresses, AppSettings settings,
        Action<IpNetworkInfo>? progress, CancellationToken cancellationToken)
    {
        var ips = addresses.Select(Canonical).Distinct(StringComparer.Ordinal).ToArray();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = store.LoadIpNetworkCache();
            var now = DateTimeOffset.UtcNow;
            var pending = ips.Where(ip => !cache.TryGetValue(ip, out var info) || info.NeedsLookup(now)).ToArray();
            // Do not even initialize the network transport when all requested addresses are already cached.
            if (pending.Length == 0) return ips.ToDictionary(ip => ip, ip => cache[ip]);
            var persistedNext = store.LoadIpApiNextRequest();
            if (persistedNext > _nextRequest) _nextRequest = persistedNext;
            using var handler = _createHandler(settings);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("vpngate_proxy/1.2");
            foreach (var batch in pending.Chunk(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<string, IpNetworkInfo> received;
                try
                {
                    var wait = _nextRequest - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
                    using var response = await client.PostAsJsonAsync(
                        "http://ip-api.com/batch?fields=status,message,query,hosting,mobile,isp,org,as", batch, cancellationToken);
                    _nextRequest = DateTimeOffset.UtcNow.AddSeconds(5);
                    store.SaveIpApiNextRequest(_nextRequest);
                    if (response.Headers.TryGetValues("X-Rl", out var remaining) && remaining.FirstOrDefault() == "0" ||
                        response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var seconds = response.Headers.TryGetValues("X-Ttl", out var ttl) &&
                            int.TryParse(ttl.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 60;
                        _nextRequest = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 120));
                        if (response.Headers.RetryAfter?.Delta is { } retry && retry > TimeSpan.Zero)
                            _nextRequest = DateTimeOffset.UtcNow.Add(retry);
                        store.SaveIpApiNextRequest(_nextRequest);
                    }
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var buffer = new MemoryStream();
                    var bytes = new byte[8192]; int read;
                    while ((read = await stream.ReadAsync(bytes, cancellationToken)) > 0)
                    {
                        if (buffer.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("IP 信息响应过大。");
                        buffer.Write(bytes, 0, read);
                    }
                    using var document = JsonDocument.Parse(buffer.ToArray());
                    received = Parse(document.RootElement, batch);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
                {
                    received = batch.ToDictionary(ip => ip, ip => Unknown(ip, exception.Message));
                }
                foreach (var ip in batch)
                {
                    var info = received.TryGetValue(ip, out var item) ? item : Unknown(ip, "查询服务未返回该 IP。");
                    cache[ip] = info; progress?.Invoke(info);
                }
                store.SaveIpNetworkCache(cache);
            }
            return ips.ToDictionary(ip => ip, ip => cache.TryGetValue(ip, out var info) ? info : Unknown(ip, "暂无数据。"));
        }
        finally { _gate.Release(); }
    }

    // Missing / malformed flags remain unknown; VPN/proxy status never implies residential or datacenter.
    public static IReadOnlyDictionary<string, IpNetworkInfo> Parse(JsonElement response, IEnumerable<string> requested)
    {
        if (response.ValueKind != JsonValueKind.Array) throw new InvalidDataException("IP 信息响应格式错误。");
        var allowed = requested.Select(Canonical).ToHashSet(StringComparer.Ordinal);
        var results = new Dictionary<string, IpNetworkInfo>(StringComparer.Ordinal);
        foreach (var item in response.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string Text(string key) => item.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
            bool? Flag(string key) => item.TryGetProperty(key, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : null;
            if (!IPAddress.TryParse(Text("query"), out var parsed)) continue;
            var ip = parsed.ToString();
            if (!allowed.Contains(ip)) continue;
            var hosting = Text("status") == "success" ? Flag("hosting") : null;
            var error = hosting is null ? Text("message") is { Length: > 0 } message ? message : "查询结果缺少机房识别标记。" : "";
            results[ip] = new(ip, hosting, Flag("mobile"), Text("isp"), Text("org"), Text("as"), DateTimeOffset.UtcNow, error);
        }
        return results;
    }
    public static string Canonical(string ip) => IPAddress.Parse(ip).ToString();
    private static IpNetworkInfo Unknown(string ip, string error) => new(ip, null, null, "", "", "", DateTimeOffset.UtcNow, error);
}
