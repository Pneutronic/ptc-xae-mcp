using PtcXaeMcp.WinForms.Localization;
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
                    () => Host.SnapshotRestore.ConfirmProposal(p.RestoreId),
                    done =>
                    {
                        // The operator reads the host language; the run log keeps English.
                        ShowLastAction(SummariseRestore(done), done.State != SnapshotRestoreState.Executed);
                        AppendLog(SummariseRestore(done, english: true));
                    },
                    text =>
                    {
                        // Busy lock: nothing was written and the proposal is still pending — ask again.
                        ShowLastAction(Loc.Format("approvals.restore.not_executed", p.RestoreId, text), isError: true);
                        AppendLog(text);
                        AskRestoreAgainIfStillPending(p);
                    });
            }
            else if (IsRestorePending(p.RestoreId))
            {
                bool expired = answer == DialogResult.Abort;
                // The reason goes to the agent and the log (English); the operator reads it in the host language.
                string reasonKey = expired ? "approvals.reason.expired" : "approvals.reason.cancelled";
                string reason = Loc.English(reasonKey);
                Host.SnapshotRestore.RejectProposal(p.RestoreId, reason);
                ShowLastAction(Loc.Format("approvals.restore.rejected", p.RestoreId, Loc.Get(reasonKey)), isError: false);
                AppendLog(Loc.FormatEnglish("approvals.restore.rejected", p.RestoreId, reason));
            }
            RefreshIbnState();
        });
    }

    private void AskRestoreAgainIfStillPending(RestoreProposal p)
    {
        if (IsRestorePending(p.RestoreId) && !_openRestorePopups.Contains(p.RestoreId) && DateTime.UtcNow < p.ExpiresUtc)
            OnRestoreProposalRaised(p);
    }

    /// <summary>One line about a finished restore: host language for the operator, <paramref name="english"/> for the run log.</summary>
    internal static string SummariseRestore(RestoreProposal done, bool english = false)
    {
        if (done.State != SnapshotRestoreState.Executed)
        {
            return english
                ? Loc.FormatEnglish("approvals.restore.summary.not_executed", done.RestoreId, SnapshotRestoreCoordinator.StateName(done.State),
                    done.Reason ?? Loc.English("approvals.restore.reason_default"))
                : Loc.Format("approvals.restore.summary.not_executed", done.RestoreId, RestoreStateText(done.State),
                    done.Reason ?? Loc.Get("approvals.restore.reason_default"));
        }
        int written = done.Report?.Count(r => r.Outcome == "written") ?? 0;
        int failed = done.Report?.Count(r => r.Outcome == "failed") ?? 0;
        int skipped = done.Report?.Count(r => r.Outcome == "skipped") ?? 0;
        return english
            ? Loc.FormatEnglish("approvals.restore.summary.executed", done.RestoreId, written, failed, skipped)
            : Loc.Format("approvals.restore.summary.executed", done.RestoreId, written, failed, skipped);
    }

    private static string RestoreStateText(SnapshotRestoreState state) => state switch
    {
        SnapshotRestoreState.Pending => Loc.Get("approvals.restore.state.pending"),
        SnapshotRestoreState.Confirmed => Loc.Get("approvals.restore.state.confirmed"),
        SnapshotRestoreState.Executed => Loc.Get("approvals.restore.state.executed"),
        SnapshotRestoreState.Rejected => Loc.Get("approvals.restore.state.rejected"),
        _ => Loc.Get("approvals.restore.state.expired"),
    };
}
