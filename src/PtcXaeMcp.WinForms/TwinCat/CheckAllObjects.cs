namespace PtcXaeMcp.WinForms.TwinCat;

// "Check all objects" of a PLC project (Build menu): compiles every object, also the ones no task
// reaches. A build compiles only what a task's program calls, so an uncalled function with an
// undeclared variable builds with 0 errors (live 2026-11-05). It runs through the Automation Interface,
// ITcPlcIECProject2.CheckAllObjects() on the PLC project item (Infosys TE1000 ITcPlcIECProject2; not to be
// confused with ITcSysManager.CheckAllObjects(), the system check before an activation): no window is
// selected, so the XAE stays behind the user's work. Only an XAE that does not offer that call falls back
// to the XAE command, found by its locale-independent identity (German XAE: "Build.ÜberprüfealleObjekte",
// TcXaeShell 4026.22; same command group as ErrorListClearCommand) — it needs the PLC project selected in
// the Solution Explorer, which brings the XAE to the front, so it runs only with bringToFront=true.
internal static class CheckAllObjectsCommand
{
    public const string Guid = "{3B11520B-7E70-4008-A6CF-B60AE84E12B1}";
    public const int Id = 24619;
}

/// <summary>One look at the XAE while the check runs; null when the XAE rejected the calls.</summary>
internal readonly record struct CheckAllObjectsSample(bool? CommandAvailable, int ErrorListCount, int OutputLength);

internal static class CheckAllObjectsWait
{
    public const int StableReads = 3;

    /// <summary>
    /// Waits until the check has finished: the same sample (command state, Error List count, Output
    /// window length) on <see cref="StableReads"/> reads in a row; a rejected read starts over. False
    /// at <paramref name="timeout"/>. Live the command returned after the check had run; the wait
    /// covers a larger project where the XAE may still be compiling. The command state is not
    /// required to be enabled: it follows the selection, which the check may move.
    /// </summary>
    public static bool Until(
        Func<CheckAllObjectsSample?> probe,
        TimeSpan timeout,
        TimeSpan interval,
        Func<TimeSpan>? elapsed = null,
        Action<TimeSpan>? sleep = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        elapsed ??= () => sw.Elapsed;
        sleep ??= Thread.Sleep;

        CheckAllObjectsSample? previous = null;
        int streak = 0;
        while (true)
        {
            CheckAllObjectsSample? read = probe();
            if (read is { } sample)
            {
                streak = previous == sample ? streak + 1 : 1;
                previous = sample;
                if (streak >= StableReads) return true;
            }
            else
            {
                previous = null;
                streak = 0;
            }

            if (elapsed() >= timeout) return false;
            sleep(interval);
        }
    }
}

internal static class CheckAllObjectsVerdict
{
    /// <summary>Why the check does not count as passed, or null when it does.</summary>
    /// <param name="reportedSuccess">What ITcPlcIECProject2.CheckAllObjects() returned; null when the XAE command ran.</param>
    public static string? FailureReason(IReadOnlyList<BuildError> errors, string? compilerSummary, bool finished, TimeSpan timeout,
        bool? reportedSuccess = null)
    {
        if (!finished)
            return $"the check had not finished after {timeout.TotalMinutes:0} min (the Error List or the Output window was still changing).";
        int listed = errors.Count(e => e.Level == "error");
        if (listed > 0)
            return $"{listed} error(s) in the PLC project's objects.";
        if (compilerSummary is not null && BuildTruth.SummaryErrorCount(compilerSummary) is > 0)
            return $"the compiler reports {compilerSummary}.";
        if (reportedSuccess == false)
            return "CheckAllObjects() reported a failure, but neither the Error List nor the compiler output names an error — " +
                   "look at the XAE's Error List and Output window.";
        return null;
    }
}
