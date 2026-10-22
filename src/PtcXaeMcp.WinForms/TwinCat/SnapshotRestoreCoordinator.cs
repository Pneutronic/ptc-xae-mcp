using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

internal enum SnapshotRestoreState { Pending, Confirmed, Executed, Rejected, Expired }

/// <summary>One line of the restore report. <c>Outcome</c> is "written", "failed" or "skipped".</summary>
internal sealed record RestoreReportItem(string Path, string Outcome, JsonElement? ReadBack, string Message);

internal sealed record RestoreProposal(
    string RestoreId,
    string AmsNetId,
    int AdsPort,
    string SnapshotPath,
    string SnapshotUtc,
    string PlcState,
    RestorePlan Plan,
    SnapshotRestoreState State,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    IReadOnlyList<RestoreReportItem>? Report,
    string? Reason);

/// <summary>
/// persistent-snapshot: the host-owned lifecycle of a restore proposal
/// (pending → confirmed → executed / rejected / expired), mirroring <see cref="IbnLiveCoordinator"/>.
/// SINGLETON on the host, shared by the MCP tools and the Approvals view.
/// <para>
/// Asymmetric by design: an AI may <see cref="Propose"/> and poll <see cref="Get"/>; the write itself
/// happens ONLY in <see cref="ConfirmProposal"/>, called from the host popup's OK handler.
/// <see cref="ConfirmProposal"/> is deliberately <b>not</b> bound to any MCP tool. Headless (no
/// subscriber to <see cref="ProposalRaised"/>) ⇒ no confirm path ⇒ <see cref="Propose"/> refuses.
/// </para>
/// </summary>
internal sealed class SnapshotRestoreCoordinator(ITwinCatSession session, Func<DateTime>? clock = null)
{
    public const int DefaultTtlSeconds = 300;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required RestoreProposal Proposal { get; set; }
        public required Func<(RestorePlan? Plan, string? Error)> Replan { get; init; }
    }

    /// <summary>Raised (outside the lock) when a proposal is created; the host opens the confirm popup.</summary>
    public event Action<RestoreProposal>? ProposalRaised;

    /// <summary>Raised after any state change so the GUI can refresh its badge.</summary>
    public event Action? StateChanged;

    public bool HasHost => ProposalRaised is not null;

    public IReadOnlyList<RestoreProposal> Snapshot()
    {
        lock (_gate)
        {
            foreach (var id in _entries.Keys.ToList()) ExpireIfDue(id);
            return _entries.Values.Select(e => e.Proposal).OrderBy(p => p.CreatedUtc).ToList();
        }
    }

    public int PendingCount => Snapshot().Count(p => p.State == SnapshotRestoreState.Pending);

    /// <summary>Creates a <i>pending</i> proposal. Writes nothing.</summary>
    /// <param name="replan">Rebuilds the plan from the live PLC at execute time (after the human confirmed).</param>
    public (RestoreProposal? Proposal, string? Error) Propose(
        string amsNetId, int adsPort, string snapshotPath, string snapshotUtc, string plcState,
        RestorePlan plan, Func<(RestorePlan? Plan, string? Error)> replan, int ttlSeconds = DefaultTtlSeconds)
    {
        if (ProposalRaised is null)
            return (null, "[Error] No host window is attached to confirm a restore (headless server) — " +
                          "a restore is only ever executed after a human confirms it in the Approvals popup.");
        if (plan.Writes.Count == 0)
            return (null, "[Error] The restore plan has nothing to write.");

        RestoreProposal created;
        lock (_gate)
        {
            DateTime now = _clock();
            created = new RestoreProposal(Guid.NewGuid().ToString("N")[..8], amsNetId, adsPort, snapshotPath,
                snapshotUtc, plcState, plan, SnapshotRestoreState.Pending, now, now.AddSeconds(ttlSeconds), null, null);
            _entries[created.RestoreId] = new Entry { Proposal = created, Replan = replan };
        }
        ProposalRaised?.Invoke(created);
        StateChanged?.Invoke();
        return (created, null);
    }

    public RestoreProposal? Get(string? restoreId)
    {
        lock (_gate)
        {
            string id = (restoreId ?? string.Empty).Trim();
            if (!_entries.ContainsKey(id)) return null;
            ExpireIfDue(id);
            return _entries[id].Proposal;
        }
    }

    // Caller holds _gate.
    private void ExpireIfDue(string id)
    {
        var e = _entries[id];
        if (e.Proposal.State == SnapshotRestoreState.Pending && _clock() >= e.Proposal.ExpiresUtc)
            e.Proposal = e.Proposal with { State = SnapshotRestoreState.Expired, Reason = "expired before a human confirmed" };
    }

    /// <summary>HOST ONLY (popup OK). Re-plans against the live PLC, refuses if it differs from what was
    /// reviewed, then writes and reports every leaf read back.</summary>
    public RestoreProposal ConfirmProposal(string restoreId)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(restoreId, out entry!))
                throw new KeyNotFoundException($"Unknown restoreId '{restoreId}'.");
            ExpireIfDue(restoreId);
            if (entry.Proposal.State != SnapshotRestoreState.Pending) return entry.Proposal;
            entry.Proposal = entry.Proposal with { State = SnapshotRestoreState.Confirmed };
        }
        StateChanged?.Invoke();

        var p = entry.Proposal;
        RestoreProposal finished;
        try
        {
            var (fresh, error) = entry.Replan();
            if (fresh is null)
                finished = p with { State = SnapshotRestoreState.Rejected, Reason = "re-plan failed: " + error };
            else if (fresh.PlanHash != p.Plan.PlanHash)
                finished = p with { State = SnapshotRestoreState.Rejected, Reason = "PLC changed since the dry run — nothing was written" };
            else
                finished = Execute(p);
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            finished = p with { State = SnapshotRestoreState.Rejected, Reason = "restore aborted: " + ex.Message };
        }

        lock (_gate) entry.Proposal = finished;
        StateChanged?.Invoke();
        return finished;
    }

    /// <summary>HOST ONLY (popup Cancel / close).</summary>
    public RestoreProposal RejectProposal(string restoreId, string? reason = null)
    {
        RestoreProposal result;
        lock (_gate)
        {
            if (!_entries.TryGetValue(restoreId, out var entry))
                throw new KeyNotFoundException($"Unknown restoreId '{restoreId}'.");
            ExpireIfDue(restoreId);
            if (entry.Proposal.State == SnapshotRestoreState.Pending)
                entry.Proposal = entry.Proposal with { State = SnapshotRestoreState.Rejected, Reason = reason ?? "rejected by the operator" };
            result = entry.Proposal;
        }
        StateChanged?.Invoke();
        return result;
    }

    private RestoreProposal Execute(RestoreProposal p)
    {
        var writes = p.Plan.Writes.Select(w => new SnapshotLeafWrite(w.Path, w.New)).ToList();
        var result = session.WriteSnapshotLeaves(writes, p.AmsNetId, p.AdsPort);
        if (!result.Success)
            return p with { State = SnapshotRestoreState.Rejected, Reason = McpErrors.EnsureError(result.Message) };

        var report = new List<RestoreReportItem>();
        var byPath = result.Items.ToDictionary(i => i.Path, StringComparer.Ordinal);
        foreach (var w in p.Plan.Writes)
        {
            if (!byPath.TryGetValue(w.Path, out var item))
            { report.Add(new(w.Path, "failed", null, "no result from the write")); continue; }
            // The read-back decides, never the intended value (D-3): a mismatch is a failed leaf.
            bool landed = item.Outcome == "written" && item.ReadBack is not null &&
                          SnapshotValues.ValueEquals(item.ReadBack.Value, w.New.Value);
            report.Add(new(w.Path, landed ? "written" : "failed", item.ReadBack?.Value,
                landed ? "OK" : (item.Outcome == "written" ? "read-back differs from the written value (overwritten by the program?)" : item.Message)));
        }
        foreach (var s in p.Plan.Skips) report.Add(new(s.Path, "skipped", null, s.Reason));
        return p with { State = SnapshotRestoreState.Executed, Report = report };
    }

    public static string StateName(SnapshotRestoreState s) => s switch
    {
        SnapshotRestoreState.Pending => "pending",
        SnapshotRestoreState.Confirmed => "confirmed",
        SnapshotRestoreState.Executed => "executed",
        SnapshotRestoreState.Rejected => "rejected",
        _ => "expired",
    };
}
