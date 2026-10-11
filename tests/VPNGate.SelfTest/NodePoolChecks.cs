using VPNGate.Core;

internal static class NodePoolChecks
{
    public static async Task RunAsync(Dictionary<string, object> report, Action<bool, string> check)
    {
        foreach (var (count, lanes) in new[] { (0,0), (1,1), (30,1), (31,2), (60,2), (61,3), (570,19), (571,20), (600,20), (601,20), (int.MaxValue,20) })
            check(NodePoolPolicy.Concurrency(count) == lanes, $"pool concurrency for {count} nodes is {lanes}");
        var now = DateTimeOffset.UtcNow;
        check(NodePoolPolicy.NextRefresh(null, 0, now) == now && NodePoolPolicy.NextRefresh(now, 0, now) == now,
            "an empty pool refreshes immediately on startup");
        check(NodePoolPolicy.NextRefresh(now, 1, now) == now.AddHours(1) && NodePoolPolicy.NextRefresh(now, 1, now.AddHours(1)) == now.AddHours(1),
            "a cached pool becomes due exactly one hour after its last refresh");
        VpnNode Node(string id, bool available = false) => new() { Id = id, Ip = "198.51.100.1", HostName = id,
            OpenVpnConfig = id, TestStatus = available ? "可用" : "未测试", TestedAt = available ? now : null, LatencyMs = available ? 10 : null };
        var fetched = new List<VpnNode> { Node("old", false), Node("new"), Node("new") };
        var calls = new List<string>();
        var pool = new NodePool([Node("old", true), Node("stale", true), Node("unverified")], now,
            (_, _) => Task.FromResult<IReadOnlyList<VpnNode>>(fetched), (node, _, _) =>
            {
                lock (calls) calls.Add(node.Id);
                return Task.FromResult(new ProbeResult(node.Id, node.Id != "stale", 12, 30, "", now));
            });
        check(pool.Nodes.Count == 2, "unverified cache entries are excluded from the usable pool");
        var round = await pool.RefreshAsync(new(), null, CancellationToken.None);
        check(calls.Order().SequenceEqual(new[] { "new", "old", "stale" }), "every refresh tests the deduplicated union of old and new nodes");
        check(pool.Nodes.Select(n => n.Id).Order().SequenceEqual(new[] { "new", "old" }), "failed old nodes are pruned and only verified new nodes are retained");
        check(round!.Removed == 1 && pool.LastRefresh > now, "successful rounds publish removal counts and the refresh timestamp");
        var fetchFailed = new NodePool(pool.Nodes, fetch: (_, _) => throw new IOException("API offline"), probe: (node, _, _) =>
            Task.FromResult(new ProbeResult(node.Id, node.Id == "new", 8, 1, "", now)));
        var fallback = await fetchFailed.RefreshAsync(new(), null, CancellationToken.None);
        check(fetchFailed.Nodes.Count == 1 && fallback!.FetchError.Length > 0, "an API failure still revalidates the existing pool");

        var active = 0; var peak = 0;
        var many = Enumerable.Range(0, 601).Select(i => Node(i.ToString())).ToArray();
        var concurrent = new NodePool([], fetch: (_, _) => Task.FromResult<IReadOnlyList<VpnNode>>(many), probe: async (node, _, token) =>
        {
            var current = Interlocked.Increment(ref active);
            int prior;
            do { prior = Volatile.Read(ref peak); } while (current > prior && Interlocked.CompareExchange(ref peak, current, prior) != prior);
            await Task.Delay(5, token); Interlocked.Decrement(ref active);
            return new(node.Id, true, 2, 1, "", now);
        });
        await concurrent.RefreshAsync(new(), null, CancellationToken.None);
        check(peak == 20 && active == 0 && concurrent.Nodes.Count == 601, "actual simultaneous probes reach but never exceed the 20-worker cap");
        using var cancel = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new NodePool([Node("keep", true)], fetch: (_, _) => Task.FromResult<IReadOnlyList<VpnNode>>([Node("pending")]),
            probe: async (node, _, token) => { waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(node.Id, true, 2, 1, "", now); });
        var operation = cancelled.RefreshAsync(new(), null, cancel.Token); await waiting.Task;
        var overlap = await cancelled.RefreshAsync(new(), null, CancellationToken.None);
        check(overlap is null, "manual and scheduled refreshes cannot overlap");
        cancel.Cancel(); try { await operation; } catch (OperationCanceledException) { }
        check(cancelled.Nodes.Single().Id == "keep" && cancelled.LastRefresh is null, "cancelling a round preserves the entire last verified pool");
        report["maxActiveProbes"] = peak;
    }
}
