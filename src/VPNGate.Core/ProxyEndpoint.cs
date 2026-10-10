using System.Net;
using System.Text.Json.Nodes;

namespace VPNGate.Core;

public sealed record ProxyEndpoint(string Scheme, string Host, int Port, string Username, string Password)
{
    public bool IsHttp => Scheme is "http" or "https";
    public string SafeAddress => new UriBuilder(Scheme, Host, Port).Uri.GetLeftPart(UriPartial.Authority);

    public static ProxyEndpoint Parse(string address, string username = "", string password = "")
    {
        address = address.Trim();
        if (address.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)) address = "socks5://" + address[8..];
        if (address.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase)) address = "socks5://" + address[10..];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("请输入完整的上游代理地址，例如 http://127.0.0.1:10808 或 socks5://127.0.0.1:10808。");
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
            throw new ArgumentException("支持 HTTP、HTTPS、SOCKS4、SOCKS4a 和 SOCKS5 代理。");
        if (uri.AbsolutePath != "/" && uri.AbsolutePath != "" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("代理地址只能包含协议、主机和端口。");
        var port = uri.Port;
        if (port < 0) port = scheme.StartsWith("socks", StringComparison.Ordinal) ? 1080 : scheme == "https" ? 443 : 80;
        if (port is < 1 or > 65535) throw new ArgumentException("上游代理端口无效。");
        if (string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        if (username.Contains('\n') || username.Contains('\r') || password.Contains('\n') || password.Contains('\r'))
            throw new ArgumentException("用户名或密码不能包含换行。");
        return new(scheme, uri.IdnHost.Trim('[', ']'), port, username, password);
    }

    public WebProxy ToWebProxy()
    {
        var proxy = new WebProxy(new UriBuilder(Scheme, Host, Port).Uri) { BypassProxyOnLocal = false };
        if (Username.Length > 0) proxy.Credentials = new NetworkCredential(Username, Password);
        return proxy;
    }

    public JsonObject ToOutbound(string physicalInterface = "")
    {
        var output = new JsonObject { ["type"] = IsHttp ? "http" : "socks", ["tag"] = "upstream", ["server"] = Host, ["server_port"] = Port,
            ["connect_timeout"] = "15s" };
        if (!IsHttp) output["version"] = Scheme == "socks5" ? "5" : Scheme == "socks4a" ? "4a" : "4";
        if (Username.Length > 0) { output["username"] = Username; if (Scheme is not ("socks4" or "socks4a")) output["password"] = Password; }
        if (Scheme == "https") output["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = Host };
        if (physicalInterface.Length > 0 && (!IPAddress.TryParse(Host, out var ip) || !IPAddress.IsLoopback(ip)))
            output["bind_interface"] = physicalInterface;
        // Bootstrap only resolves the upstream proxy itself. Destination DNS uses the OpenVPN tunnel.
        if (!IPAddress.TryParse(Host, out _)) output["domain_resolver"] = "bootstrap";
        return output;
    }
}
