using System.Diagnostics;
using System.Net.Sockets;
using VPNGate.Core;

internal static class UnifiedChecks
{
    public static async Task RunAsync(bool tun, Dictionary<string, object> report, Action<bool, string> check)
    {
        var settings = new AppSettings { ListenPort = EngineRuntime.FreePort(), RoutingMode = "direct" };
        var before = await SystemDnsLease.OwnedRuleCountAsync();
        var v2ray = Process.GetProcessesByName("v2rayN").Select(p => p.Id).Order().ToArray();
        await using (var session = new EngineSession())
        {
            session.Log += Console.WriteLine;
            await session.StartAsync(null, settings, "127.0.0.1", settings.ListenPort, CancellationToken.None, tun);
            check(session.IsRunning && !session.IsVpnConnected && session.CoreProcessId is > 0,
                "one native core serves the local proxy without an OpenVPN node");
            using var socket = new TcpClient(); await socket.ConnectAsync("127.0.0.1", session.Port);
            check(socket.Connected, "the unified core's HTTP/SOCKS listener is available without a node");
            var corePid = session.CoreProcessId;
            foreach (var mode in new[] { "global", "rule", "direct" }) await session.SetRoutingModeAsync(mode);
            check(session.CoreProcessId == corePid && session.IsRunning, "routing changes preserve the single core process");
            if (tun)
            {
                check(session.TunEnabled && System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == CoreConfig.TunName),
                    "the same running core creates its own TUN adapter");
                var worker = session.HostProcessId;
                await session.SetTunEnabledAsync(false);
                check(!session.TunEnabled && session.IsRunning && session.HostProcessId == worker, "disabling TUN reloads the unified core and preserves its authorized supervisor");
                await session.SetTunEnabledAsync(true);
                check(session.TunEnabled && session.HostProcessId == worker, "enabling TUN again uses the same supervisor");
            }
            report["coreProcessId"] = session.CoreProcessId!;
        }
        check(await SystemDnsLease.OwnedRuleCountAsync() == before, "unified session cleanup restores its own DNS policy");
        check(v2ray.SequenceEqual(Process.GetProcessesByName("v2rayN").Select(p => p.Id).Order()), "the existing v2rayN process remains running");
    }
}
