using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace VPNGate.Core;

public sealed class VpnGateClient
{
    public const string Source = "https://www.vpngate.net/api/iphone/";
    public async Task<IReadOnlyList<VpnNode>> FetchAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = settings.Validate();
        using var handler = new SocketsHttpHandler { Proxy = endpoint.ToWebProxy(), UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vpngate_proxy/1.2");
        using var response = await client.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("VPNGate 列表超过大小限制。");
            memory.Write(buffer, 0, read);
        }
        return Parse(Encoding.UTF8.GetString(memory.ToArray()));
    }

    public static IReadOnlyList<VpnNode> Parse(string csv)
    {
        csv = csv.TrimStart('\uFEFF');
        var start = csv.IndexOf("#HostName,", StringComparison.Ordinal);
        if (start < 0) start = csv.IndexOf("HostName,", StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("VPNGate 响应不包含节点列表。");
        csv = csv[start..].TrimStart('#');
        using var parser = new TextFieldParser(new StringReader(csv)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException("缺少 CSV 表头。");
        var names = headers.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i, StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { "HostName", "IP", "CountryShort", "OpenVPN_ConfigData_Base64" })
            if (!names.ContainsKey(required)) throw new InvalidDataException($"缺少 VPNGate 字段：{required}");
        var nodes = new List<VpnNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.EndOfData)
        {
            string[]? fields;
            try { fields = parser.ReadFields(); } catch (MalformedLineException) { continue; }
            if (fields is null || fields.Length < headers.Length || fields[0].StartsWith('*')) continue;
            string Field(string name) => names.TryGetValue(name, out var i) && i < fields.Length ? fields[i] : "";
            long Number(string name) => long.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var host = Field("HostName");
            if (host.Length == 0 || !IPAddress.TryParse(Field("IP"), out _)) continue;
            string profile;
            try { profile = Encoding.UTF8.GetString(Convert.FromBase64String(Field("OpenVPN_ConfigData_Base64"))); }
            catch (FormatException) { continue; }
            if (profile.Length is 0 or > 512 * 1024) continue;
            var id = $"{host}@{Field("IP")}";
            if (!ids.Add(id)) continue;
            var country = Field("CountryShort").ToUpperInvariant();
            nodes.Add(new VpnNode { Id = id, HostName = host, Ip = Field("IP"), CountryShort = country, CountryLong = Field("CountryLong"),
                Score = Number("Score"), AdvertisedPing = int.TryParse(Field("Ping"), out var ping) ? ping : null,
                Speed = Number("Speed"), Sessions = (int)Math.Clamp(Number("NumVpnSessions"), 0, int.MaxValue), Uptime = Number("Uptime"),
                Operator = Field("Operator"), LogPolicy = Field("LogType"), Message = Field("Message"), OpenVpnConfig = profile,
                SupportsTcp = OpenVpnProfile.HasTcpTransport(profile) });
        }
        if (nodes.Count == 0) throw new InvalidDataException("VPNGate 当前未返回可用节点，请稍后重试。");
        return nodes;
    }
}
