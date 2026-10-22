using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Views;

// persistent-snapshot: the restore-proposal side of the Approvals view. A proposal raised by
// SnapshotRestoreCoordinator opens RestoreConfirmDialog (modal, TopMost); only OK reaches
// SnapshotRestoreCoordinator.ConfirmProposal (host-only, no MCP binding). The write runs off the
// UI thread under the shared dispatcher lock, like every other ADS-touching Approvals action.
internal sealed partial class ApprovalsView
{
    // Restore proposals whose popup is open right now (UI thread only).
    private readonly HashSet<string> _openRestorePopups = new(StringComparer.Ordinal);
    private Action<RestoreProposal>? _onRestoreRaised;

    private void AttachRestore(ServerHost host)
    {
        _onRestoreRaised = OnRestoreProposalRaised;
        host.SnapshotRestore.ProposalRaised += _onRestoreRaised;
        host.SnapshotRestore.StateChanged += _onStateChanged;
    }

    private void DetachRestore(ServerHost host)
    {
        if (_onRestoreRaised is not null) host.SnapshotRestore.ProposalRaised -= _onRestoreRaised;
        host.SnapshotRestore.StateChanged -= _onStateChanged;
    }

    /// <summary>Pending, unexpired restore proposals plus restore popups still on screen.</summary>
    private int PendingRestoreCount()
    {
        if (_host is null) return 0;
        var ids = _host.SnapshotRestore.Snapshot()
            .Where(p => p.State == SnapshotRestoreState.Pending)
            .Select(p => p.RestoreId)
            .ToHashSet(StringComparer.Ordinal);
        ids.UnionWith(_openRestorePopups);
        return ids.Count;
    }

    private bool IsRestorePending(string restoreId) =>
        _host?.SnapshotRestore.Get(restoreId)?.State == SnapshotRestoreState.Pending;

    // Posted through the main window so it appears at once, whichever view is active; non-blocking
    // for the MCP thread (the restore tool returned 'pending' already).
    private void OnRestoreProposalRaised(RestoreProposal p)
    {
        _post(async () =>
        {
            if (IsDisposed) return;
            _openRestorePopups.Add(p.RestoreId);
            DialogResult answer;
            try
            {
                RefreshIbnState();
                if (_owner is Form { WindowState: FormWindowState.Minimized } form)
                    form.WindowState = FormWindowState.Normal;
                using var dialog = new RestoreConfirmDialog(p, () => IsRestorePending(p.RestoreId));
                answer = dialog.ShowDialog(_owner);
            }
            finally
            {
                _openRestorePopups.Remove(p.RestoreId);
            }

            if (answer == DialogResult.OK)
            {
                await RunGuiComActionAsync(
                    () => SummariseRestore(Host.SnapshotRestore.ConfirmProposal(p.RestoreId)),
                    text =>
                    {
                        ShowLastAction(text, !text.StartsWith("Restore " + p.RestoreId + " executed", StringComparison.Ordinal));
                        AppendLog(text);
                    },
                    text =>
                    {
                        // Busy lock: nothing was written and the proposal is still pending — ask again.
                        ShowLastAction($"Restore {p.RestoreId} not executed: {text}", isError: true);
                        AppendLog(text);
                        AskRestoreAgainIfStillPending(p);
                    });
            }
            else if (IsRestorePending(p.RestoreId))
            {
                bool expired = answer == DialogResult.Abort;
                string reason = expired ? "confirm popup expired" : "operator cancelled";
                Host.SnapshotRestore.RejectProposal(p.RestoreId, reason);
                ShowLastAction($"Restore {p.RestoreId} rejected ({reason}).", isError: false);
                AppendLog($"Restore {p.RestoreId} rejected ({reason}).");
            }
            RefreshIbnState();
        });
    }

    private void AskRestoreAgainIfStillPending(RestoreProposal p)
    {
        if (IsRestorePending(p.RestoreId) && !_openRestorePopups.Contains(p.RestoreId) && DateTime.UtcNow < p.ExpiresUtc)
            OnRestoreProposalRaised(p);
    }

    internal static string SummariseRestore(RestoreProposal done)
    {
        if (done.State != SnapshotRestoreState.Executed)
            return $"Restore {done.RestoreId} {SnapshotRestoreCoordinator.StateName(done.State)}: {done.Reason ?? "not executed"}";
        int written = done.Report?.Count(r => r.Outcome == "written") ?? 0;
        int failed = done.Report?.Count(r => r.Outcome == "failed") ?? 0;
        int skipped = done.Report?.Count(r => r.Outcome == "skipped") ?? 0;
        return $"Restore {done.RestoreId} executed: {written} written, {failed} failed, {skipped} skipped.";
    }
}
