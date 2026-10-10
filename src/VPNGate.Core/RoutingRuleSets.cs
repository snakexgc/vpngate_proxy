using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNGate.Core;

public sealed record RoutingRuleAsset(string Tag, string Repository, string SeedHash)
{
    public string FileName => Tag + ".srs";
    public string Url => $"https://raw.githubusercontent.com/SagerNet/{Repository}/rule-set/{FileName}";
}
public sealed record RoutingRuleState(DateTimeOffset? LastChecked = null, DateTimeOffset? LastUpdated = null,
    DateTimeOffset? LastAttempt = null, string Error = "", Dictionary<string, string>? Etags = null);

public sealed class RoutingRuleSets
{
    public static readonly RoutingRuleAsset[] Assets = [
        new("geosite-cn", "sing-geosite", "7e5edaa3156918ebb732629f7b4ab3f03040b9940456438f976c2046033fc15c"),
        new("geoip-cn", "sing-geoip", "ebee603fdf402314b44b9f653cdcf6d9cc9c41e84e2b3515e3123c5c920a93bc") ];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly Func<AppSettings, HttpMessageHandler>? _transport;
    private string StatePath => Path.Combine(_root, "state.json");
    public RoutingRuleSets(string? root = null, Func<AppSettings, HttpMessageHandler>? transport = null)
    { _root = Path.GetFullPath(root ?? Path.Combine(AppDataPaths.Root, "routing-rules")); _transport = transport; }
    public string FilePath(string tag) => Path.Combine(_root, Assets.Single(a => a.Tag == tag).FileName);
    public RoutingRuleState State
    {
        get
        {
            try { return JsonSerializer.Deserialize<RoutingRuleState>(File.ReadAllText(StatePath)) ?? new(); }
            catch (Exception exception) when (exception is IOException or JsonException) { return new(); }
        }
    }
    public JsonArray Configuration() => new(Assets.Select(a => (JsonNode)new JsonObject {
        ["type"] = "local", ["tag"] = a.Tag, ["format"] = "binary", ["path"] = FilePath(a.Tag) }).ToArray());
    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(_root, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await SeedAsync(cancellationToken); } finally { gate.Release(); }
    }
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root); AppDataPaths.RestrictDirectory(_root);
        foreach (var asset in Assets)
        {
            var destination = FilePath(asset.Tag);
            if (File.Exists(destination)) continue;
            var source = Path.Combine(AppContext.BaseDirectory, "runtime", asset.FileName);
            var data = await File.ReadAllBytesAsync(source, cancellationToken);
            if (Convert.ToHexStringLower(SHA256.HashData(data)) != asset.SeedHash) throw new InvalidDataException($"内置规则 {asset.Tag} 校验失败。");
            await AtomicWriteAsync(destination, data, cancellationToken);
            var state = State; var etags = new Dictionary<string, string>(state.Etags ?? []); etags.Remove(asset.Tag);
            await SaveStateAsync(state with { LastChecked = null, LastUpdated = null, Etags = etags }, cancellationToken);
        }
    }
    public async Task<RoutingRuleState> UpdateAsync(AppSettings settings, bool force, CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(_root, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        var temporary = new List<string>();
        try
        {
            await SeedAsync(cancellationToken);
            var previous = State; var now = DateTimeOffset.UtcNow;
            if (!force && (!settings.AutoUpdateRules || previous.LastChecked is { } checkedAt && now - checkedAt < TimeSpan.FromHours(settings.RuleUpdateHours) ||
                previous.LastAttempt is { } attempt && previous.Error.Length > 0 && now - attempt < TimeSpan.FromMinutes(15))) return previous;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var handler = _transport?.Invoke(settings) ?? new SocketsHttpHandler {
                UseProxy = true, Proxy = settings.Validate().ToWebProxy(), AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("vpngate_proxy/1.3.0");
            var etags = new Dictionary<string, string>(previous.Etags ?? []);
            var updates = new Dictionary<string, byte[]>();
            foreach (var asset in Assets)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
                if (etags.TryGetValue(asset.Tag, out var etag) && EntityTagHeaderValue.TryParse(etag, out var parsed)) request.Headers.IfNoneMatch.Add(parsed);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotModified) continue;
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("规则文件超过 4 MB。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var data = new MemoryStream(); var buffer = new byte[16384]; int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (data.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("规则文件超过 4 MB。");
                    data.Write(buffer, 0, read);
                }
                var bytes = data.ToArray();
                var existing = await File.ReadAllBytesAsync(FilePath(asset.Tag), timeout.Token);
                if (!bytes.AsSpan().SequenceEqual(existing))
                {
                    var temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".srs"); temporary.Add(temp);
                    await File.WriteAllBytesAsync(temp, bytes, timeout.Token);
                    await ValidateFileAsync(temp, timeout.Token); updates[asset.Tag] = bytes;
                }
                if (response.Headers.ETag is { } value) etags[asset.Tag] = value.ToString(); else etags.Remove(asset.Tag);
            }
            // Both downloads are validated before either running core sees a new rule file.
            var originals = new Dictionary<string, byte[]>();
            try
            {
                foreach (var update in updates)
                {
                    var path = FilePath(update.Key); originals[path] = await File.ReadAllBytesAsync(path, timeout.Token);
                    await AtomicWriteAsync(path, update.Value, timeout.Token);
                }
            }
            catch
            {
                foreach (var original in originals) await AtomicWriteAsync(original.Key, original.Value, CancellationToken.None);
                throw;
            }
            var state = new RoutingRuleState(now, updates.Count > 0 ? now : previous.LastUpdated, now, "", etags);
            await SaveStateAsync(state, timeout.Token); return state;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = exception.Message;
            if (settings.Password.Length > 0) message = message.Replace(settings.Password, "***", StringComparison.Ordinal);
            await SaveStateAsync(State with { LastAttempt = DateTimeOffset.UtcNow, Error = message }, CancellationToken.None);
            throw new IOException("规则更新失败，继续使用已有规则。" + message, exception);
        }
        finally
        {
            try { foreach (var file in temporary) { try { File.Delete(file); File.Delete(file + ".json"); } catch (IOException) { } } }
            finally { gate.Release(); }
        }
    }
    public static async Task ValidateFileAsync(string file, CancellationToken cancellationToken = default)
    {
        EngineRuntime.Verify(EngineRuntime.CorePath);
        var start = new ProcessStartInfo(EngineRuntime.CorePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "rule-set", "decompile", file, "-o", file + ".json" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法验证规则文件。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken); var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("sing-box 拒绝规则文件：" + error.Trim());
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file + ".json", cancellationToken));
        if (json.RootElement.GetProperty("rules").GetArrayLength() == 0) throw new InvalidDataException("拒绝空的分流规则文件。");
    }
    private Task SaveStateAsync(RoutingRuleState state, CancellationToken token) => AtomicWriteAsync(StatePath, JsonSerializer.SerializeToUtf8Bytes(state), token);
    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, token); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
