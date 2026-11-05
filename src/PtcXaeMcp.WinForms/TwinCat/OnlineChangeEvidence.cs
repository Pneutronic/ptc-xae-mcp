using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>What the login did to the running application, judged from the PLC itself.</summary>
/// <param name="Verdict">applied | not_needed | download | not_applied | unknown.</param>
internal sealed record OnlineChangeReport(
    string Verdict,
    ulong? CountBefore,
    ulong? CountAfter,
    bool? PlcCycling,
    string Message);

/// <summary>
/// The login result says which button the XAE's modal got, not whether the running PLC took the change.
/// The PLC counts its own online changes in <c>TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt</c>, so
/// reading it before and after the login settles it: exactly one step up means the change is in, an
/// unchanged counter after an "online change" answer means it never landed. The first task's
/// <c>_TaskInfo[1].CycleCount</c> moving shows the application still runs afterwards.
/// </summary>
internal static class OnlineChangeEvidence
{
    internal const string CounterSymbol = "TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt";
    internal const string CycleSymbol = "TwinCAT_SystemInfoVarList._TaskInfo[1].CycleCount";

    private static readonly TimeSpan CounterSettle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CounterPoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CycleGap = TimeSpan.FromMilliseconds(300);

    /// <summary>Reads the counter on the project's target; null when the PLC does not answer (no application, stopped runtime).</summary>
    internal static ulong? ReadCounter(ITwinCatSession session, int adsPort) => ReadUnsigned(session, CounterSymbol, adsPort);

    /// <summary>
    /// After the login: waits up to a few seconds for the counter to leave <paramref name="before"/> (the
    /// XAE finishes an online change after its command returns), then samples the task cycle counter twice.
    /// </summary>
    internal static OnlineChangeReport Capture(ITwinCatSession session, int adsPort, string dialogHandled, ulong? before)
    {
        ulong? after = ReadCounter(session, adsPort);
        if (before is not null && after == before && ExpectsChange(dialogHandled))
        {
            var deadline = DateTime.UtcNow + CounterSettle;
            while (after == before && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(CounterPoll);
                after = ReadCounter(session, adsPort) ?? after;
            }
        }

        bool? cycling = null;
        ulong? c1 = ReadUnsigned(session, CycleSymbol, adsPort);
        if (c1 is not null)
        {
            Thread.Sleep(CycleGap);
            ulong? c2 = ReadUnsigned(session, CycleSymbol, adsPort);
            if (c2 is not null) cycling = c2 != c1;
        }
        return Evaluate(dialogHandled, before, after, cycling);
    }

    /// <summary>Pure verdict from the modal's answer and the two counter readings.</summary>
    internal static OnlineChangeReport Evaluate(string dialogHandled, ulong? before, ulong? after, bool? cycling)
    {
        string running = cycling switch
        {
            true => " The PLC task is cycling.",
            false => " [Warn] The PLC task cycle counter did not move — the application is not running (Stop?).",
            null => "",
        };

        if (dialogHandled.Contains("(download)", StringComparison.Ordinal))
            return new("download", before, after, cycling,
                "A full download replaced the application (no online change; the counter restarts with the new application)." + running);

        if (before is null || after is null)
            return new("unknown", before, after, cycling,
                $"Online change not verified: {CounterSymbol} could not be read {(before is null ? "before" : "after")} the login " +
                "(is the application running on the project's target and adsPort right?)." + running);

        string counts = string.Create(CultureInfo.InvariantCulture, $"OnlineChangeCnt {before} → {after}");
        if (after == before + 1)
            return new("applied", before, after, cycling, $"Online change applied ({counts})." + running);
        if (after == before)
            return ExpectsChange(dialogHandled)
                ? new("not_applied", before, after, cycling,
                    $"[Error] The XAE was told to do an online change, but the PLC's counter did not move ({counts}) — the running application did not take the change." + running)
                : new("not_needed", before, after, cycling,
                    $"No online change was needed — the running application already matched the project ({counts})." + running);
        return new("unknown", before, after, cycling,
            $"[Warn] Unexpected counter step ({counts}); expected exactly one online change." + running);
    }

    // The app-changed modal ("app-changed-modal (online_change)") or the 3-way login dialog
    // ("login-dialog (OnlineChange)") was answered with the online-change button.
    private static bool ExpectsChange(string dialogHandled) =>
        dialogHandled.Contains("(online_change)", StringComparison.Ordinal)
        || dialogHandled.Contains($"login-dialog ({LoginMode.OnlineChange})", StringComparison.Ordinal);

    private static ulong? ReadUnsigned(ITwinCatSession session, string symbol, int adsPort)
    {
        AdsVariableResult r;
        try { r = session.ReadAdsVariable(symbol, "project", adsPort); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        if (!r.Success || r.Value is null) return null;
        try { return Convert.ToUInt64(r.Value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }
}
