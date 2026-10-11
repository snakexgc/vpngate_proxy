using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VPNGate.Core;

public static class EngineRuntime
{
    public const string CoreHash = "7bbef1dea9189ee12799ae834ea4b4658355da25c47a21ad8804904c0ccd9410";
    public const string WintunHash = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce";
    private static readonly HashSet<string> Verified = new(StringComparer.OrdinalIgnoreCase);
    public static string CorePath => Path.Combine(AppContext.BaseDirectory, "runtime", "sing-box.exe");
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void Verify(string path)
    {
        lock (Verified)
        {
            if (Verified.Contains(path)) return;
            if (!File.Exists(path)) throw new FileNotFoundException("缺少 sing-box 内核，请运行 scripts/setup-runtime.ps1 后重新构建。", path);
            using var file = File.OpenRead(path);
            if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(CoreHash, StringComparison.Ordinal))
                throw new InvalidDataException("sing-box 内核校验失败，请重新下载受支持的 1.14.2 版本。");
            var dll = Path.Combine(Path.GetDirectoryName(path)!, "wintun.dll");
            if (File.Exists(dll))
            {
                using var driver = File.OpenRead(dll);
                if (Convert.ToHexStringLower(SHA256.HashData(driver)) != WintunHash) throw new InvalidDataException("Wintun 驱动校验失败。");
            }
            Verified.Add(path);
        }
    }
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}
