using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace VPNGate.Core;

public static class AppDataPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPNGateXray");
    public static string Sessions => Path.Combine(Root, "sessions");
    public static string Logs => Path.Combine(Root, "logs");
    public static void RestrictDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}

public sealed class AppDataStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _root;
    public AppDataStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? AppDataPaths.Root);
        Directory.CreateDirectory(_root); AppDataPaths.RestrictDirectory(_root);
    }
    public AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>("settings.json") ?? new();
        if (settings.ProtectedPassword.Length > 0)
        {
            try { settings.Password = SecretProtection.Unprotect(settings.ProtectedPassword); }
            catch { settings.Password = ""; }
        }
        return settings;
    }
    public void SaveSettings(AppSettings settings)
    {
        var proxy = settings.Validate();
        var copy = settings.Copy();
        copy.UpstreamProxy = proxy.SafeAddress; copy.Username = proxy.Username; copy.ProtectedPassword = proxy.Password.Length > 0 ? SecretProtection.Protect(proxy.Password) : "";
        Write("settings.json", copy);
    }
    public IReadOnlyList<VpnNode> LoadNodes() => Read<List<VpnNode>>("nodes.json") ?? [];
    public void SaveNodes(IEnumerable<VpnNode> nodes) => Write("nodes.json", nodes.ToList());
    public DateTimeOffset? LoadPoolRefresh() => Read<DateTimeOffset?>("node-pool-refresh.json");
    public void SavePoolRefresh(DateTimeOffset refresh) => Write("node-pool-refresh.json", refresh);
    public Dictionary<string, IpNetworkInfo> LoadIpNetworkCache() => Read<Dictionary<string, IpNetworkInfo>>("ip-network-cache.json") ?? [];
    public void SaveIpNetworkCache(Dictionary<string, IpNetworkInfo> cache) => Write("ip-network-cache.json", cache);
    public DateTimeOffset LoadIpApiNextRequest() => Read<DateTimeOffset>("ip-api-next-request.json");
    public void SaveIpApiNextRequest(DateTimeOffset nextRequest) => Write("ip-api-next-request.json", nextRequest);
    private T? Read<T>(string name)
    {
        var path = Path.Combine(_root, name);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default; }
        catch (Exception e) when (e is IOException or JsonException) { return default; }
    }
    private void Write<T>(string name, T value)
    {
        var path = Path.Combine(_root, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

public static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] input, bool protect)
    {
        var blob = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, blob.Data, input.Length);
            Blob output;
            var success = protect ? CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(blob.Data); }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
