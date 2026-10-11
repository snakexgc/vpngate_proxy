namespace VPNGate.Core;

public static class NodePoolPolicy
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    public static int Concurrency(int count) => count < 0 ? throw new ArgumentOutOfRangeException(nameof(count))
        : count == 0 ? 0 : (int)Math.Min(20, ((long)count + 29) / 30);
    public static bool IsVerified(VpnNode node) => node.TestStatus == "可用" && node.TestedAt is not null && node.LatencyMs is not null;
    public static DateTimeOffset NextRefresh(DateTimeOffset? lastRefresh, int poolCount, DateTimeOffset now) =>
        poolCount == 0 || lastRefresh is null ? now : lastRefresh.Value + RefreshInterval;
}

public sealed record NodePoolProgress(string Phase, int Completed, int Total, int Available, int Concurrency, ProbeResult? Result = null);
public sealed record NodePoolRound(IReadOnlyList<VpnNode> Nodes, int Candidates, int Removed, int Concurrency,
    DateTimeOffset CheckedAt, string FetchError = "");

// A round merges the current pool with new API candidates, tests the whole union, then publishes only successes.
public sealed class NodePool
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<AppSettings, CancellationToken, Task<IReadOnlyList<VpnNode>>> _fetch;
    private readonly Func<VpnNode, AppSettings, CancellationToken, Task<ProbeResult>> _probe;
    private readonly TimeProvider _time;
    private IReadOnlyList<VpnNode> _nodes;
    public IReadOnlyList<VpnNode> Nodes => _nodes;
    public DateTimeOffset? LastRefresh { get; private set; }
    public NodePool(IEnumerable<VpnNode> initial, DateTimeOffset? lastRefresh = null,
        Func<AppSettings, CancellationToken, Task<IReadOnlyList<VpnNode>>>? fetch = null,
        Func<VpnNode, AppSettings, CancellationToken, Task<ProbeResult>>? probe = null, TimeProvider? time = null)
    {
        _nodes = initial.Where(NodePoolPolicy.IsVerified).GroupBy(n => n.Id).Select(g => g.First().Copy()).ToArray();
        LastRefresh = lastRefresh; _fetch = fetch ?? new VpnGateClient().FetchAsync;
        _probe = probe ?? ((node, settings, token) => ProbeService.TestNodeAsync(node, settings, null, token));
        _time = time ?? TimeProvider.System;
    }
    public async Task<NodePoolRound?> RefreshAsync(AppSettings settings, Action<NodePoolProgress>? progress,
        CancellationToken token, bool fetchNew = true, IReadOnlyCollection<string>? selectedIds = null)
    {
        if (!await _gate.WaitAsync(0, token)) return null;
        try
        {
            settings.Validate();
            var previous = _nodes;
            var candidates = previous.ToDictionary(n => n.Id, n => n.Copy());
            var fetchError = "";
            DateTimeOffset? fetchedAt = null;
            if (fetchNew)
            {
                progress?.Invoke(new("fetch", 0, 0, previous.Count, 0));
                try
                {
                    var fresh = await _fetch(settings, token);
                    foreach (var node in fresh)
                    {
                        var updated = node.Copy();
                        if (candidates.TryGetValue(node.Id, out var existing)) updated.NetworkInfo = existing.NetworkInfo;
                        candidates[node.Id] = updated;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { fetchError = exception.Message; }
                fetchedAt = _time.GetUtcNow();
            }
            token.ThrowIfCancellationRequested();
            var targets = candidates.Values.Where(n => selectedIds is null || selectedIds.Contains(n.Id)).ToArray();
            var lanes = NodePoolPolicy.Concurrency(targets.Length);
            var results = new ProbeResult[targets.Length];
            var completed = 0; var available = 0;
            progress?.Invoke(new("test", 0, targets.Length, 0, lanes));
            if (targets.Length > 0)
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, targets.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = lanes }, async (index, _) =>
                    {
                        if (token.IsCancellationRequested) return;
                        ProbeResult result;
                        try { result = await _probe(targets[index], settings, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                        catch (Exception exception) { result = new(targets[index].Id, false, null, null, exception.Message, _time.GetUtcNow()); }
                        results[index] = result;
                        if (result.Success) Interlocked.Increment(ref available);
                        progress?.Invoke(new("test", Interlocked.Increment(ref completed), targets.Length,
                            Volatile.Read(ref available), lanes, result));
                    });
            }
            token.ThrowIfCancellationRequested();
            var survivors = previous.Where(n => selectedIds is not null && !selectedIds.Contains(n.Id)).Select(n => n.Copy()).ToList();
            for (var i = 0; i < targets.Length; i++)
            {
                if (results[i] is not { Success: true, Cancelled: false } result) continue;
                targets[i].Apply(result); survivors.Add(targets[i]);
            }
            _nodes = survivors.OrderBy(n => n.LatencyMs).ThenByDescending(n => n.Score).ToArray();
            var now = _time.GetUtcNow(); if (fetchNew) LastRefresh = fetchedAt;
            return new(_nodes, targets.Length, previous.Count(n => !_nodes.Any(current => current.Id == n.Id)), lanes, now, fetchError);
        }
        finally { _gate.Release(); }
    }
}
