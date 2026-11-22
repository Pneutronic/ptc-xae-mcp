using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.Views;

namespace PtcXaeMcp.WinForms;

// Approvals wiring (issue #272): attaches the Approvals view to the host and shows its badge —
// "Approvals (n)" on the tab and a Warn chip in the header while a proposal or HMI popup is
// pending or IBN Live is armed. Clicking the chip opens the view.
public partial class Form1
{
    private FlowLayoutPanel _flpApprovalsChip = null!;
    private Label _lblApprovalsDot = null!;
    private Label _lblApprovalsState = null!;

    /// <summary>Called from <see cref="CreateViews"/>, before the first theme apply.</summary>
    private void AttachApprovals()
    {
        _flpApprovalsChip = new FlowLayoutPanel();
        _lblApprovalsDot = new Label();
        _lblApprovalsState = new Label();
        ConfigureStatusChip(_flpApprovalsChip, "_flpApprovalsChip", _lblApprovalsDot, "_lblApprovalsDot",
            _lblApprovalsState, "_lblApprovalsState", string.Empty);
        _flpApprovalsChip.Visible = false;
        _flpApprovalsChip.Cursor = Cursors.Hand;
        foreach (Control c in new Control[] { _flpApprovalsChip, _lblApprovalsDot, _lblApprovalsState })
            c.Click += (_, _) => _tabControl.SelectedTab = _tabApprovals;
        _toolTip.SetToolTip(_lblApprovalsState, Loc.Get("main.approvals.chip.tooltip"));
        _flpStatusChips.Controls.Add(_flpApprovalsChip);

        // The form's poster and the form as popup owner: a view on a tab that was never shown
        // has no window handle, but a proposal must pop up at once.
        _approvalsView.Attach(_host, _guiDispatch, this, action => BeginInvokeSafe(action));
        _approvalsView.AttentionChanged += RenderApprovalsBadge;
        RenderApprovalsBadge(_approvalsView.Attention);
    }

    /// <summary>Tab caption and header chip. Runs on the UI thread (raised by the view).</summary>
    private void RenderApprovalsBadge(ApprovalsAttention attention)
    {
        if (IsDisposed) return;
        if (_tabApprovals.Text != attention.TabCaption)
        {
            _tabApprovals.Text = attention.TabCaption;
            _tabControl.Invalidate(); // owner-drawn tab strip (ThemeManager)
        }

        _flpApprovalsChip.Visible = attention.NeedsAttention;
        if (!attention.NeedsAttention) return;
        Color warn = ThemeManager.Palette.Warn;
        _lblApprovalsState.Text = attention.ChipText;
        _lblApprovalsState.ForeColor = warn;
        _lblApprovalsDot.BackColor = warn;
    }
}
