namespace PtcXaeMcp.WinForms.TwinCat;

// Issue #148 (LIVE-4): the online state is read from the PLC Login/Logout DTE commands'
// IsAvailable flags. The only sound inference is from the LOGOUT command:
//   Logout available  ⇒ logged in (Online)
//   Logout disabled   ⇒ not logged in (Offline) — you cannot log out of a session you do not have
//   Logout unreadable ⇒ unknown
// "Login disabled ⇒ already online" is WRONG: with no reachable target both commands are
// disabled (reproduced 4× live: login returned success/isOnlineNow=true while offline).
internal enum LoginPrecheck
{
    /// <summary>Login is available and Logout is not: execute the Login command.</summary>
    Proceed,
    /// <summary>Logout is available: a session already exists — idempotent no-op.</summary>
    AlreadyOnline,
    /// <summary>Both commands disabled: offline and no login possible (no reachable target / not built).</summary>
    NoTarget,
    /// <summary>A flag could not be read: the state is unknown — never report success.</summary>
    Unknown,
}

internal static class PlcOnlineInference
{
    public static bool? IsOnline(bool? logoutAvailable) => logoutAvailable;

    public static LoginPrecheck BeforeLogin(bool? loginAvailable, bool? logoutAvailable)
    {
        if (logoutAvailable == true) return LoginPrecheck.AlreadyOnline;
        if (logoutAvailable is null || loginAvailable is null) return LoginPrecheck.Unknown;
        return loginAvailable.Value ? LoginPrecheck.Proceed : LoginPrecheck.NoTarget;
    }
}

/// <summary>IsAvailable of the PLC Login and Logout commands at one moment; null = unreadable.</summary>
internal readonly record struct PlcOnlineFlags(bool? Login, bool? Logout)
{
    // While the XAE logs in or out it rejects calls or shows both commands disabled for a few
    // seconds (live 2026-11-05: up to 10 s during an online change). Such a reading says nothing.
    public bool InTransition => Login is null || Logout is null || (Login == false && Logout == false);
}

/// <summary>Result of waiting for the login state after a Login or Logout command.</summary>
internal readonly record struct PlcOnlineSettled(bool? Online, bool Settled);

// After a Login or Logout command one read after a fixed sleep caught the XAE mid-transition:
// a login read "offline" that became online later, a logout read "offline" while it was still
// logging out. The state is taken only when the same non-transitional reading repeats.
internal static class PlcOnlineSettle
{
    public const int StableReads = 2;

    /// <summary>
    /// Reads <paramref name="probe"/> (null = the XAE rejected the call) until the same
    /// non-transitional state is read <see cref="StableReads"/> times in a row and is either the
    /// expected one or still holds after <paramref name="minWaitForOther"/> (a refused login
    /// stays offline). At <paramref name="timeout"/> the last readable Logout flag is returned
    /// with Settled=false.
    /// </summary>
    public static PlcOnlineSettled Wait(
        Func<PlcOnlineFlags?> probe,
        bool expectOnline,
        TimeSpan timeout,
        TimeSpan minWaitForOther,
        TimeSpan interval,
        Func<TimeSpan>? elapsed = null,
        Action<TimeSpan>? sleep = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        elapsed ??= () => sw.Elapsed;
        sleep ??= Thread.Sleep;

        PlcOnlineFlags? previous = null;
        int streak = 0;
        bool? lastOnline = null;
        while (true)
        {
            PlcOnlineFlags? read = probe();
            if (read is { InTransition: false } flags)
            {
                lastOnline = flags.Logout;
                streak = previous == flags ? streak + 1 : 1;
                previous = flags;
                bool expected = expectOnline
                    ? flags.Logout == true
                    : flags.Logout == false && flags.Login == true;
                if (streak >= StableReads && (expected || elapsed() >= minWaitForOther))
                    return new PlcOnlineSettled(flags.Logout, true);
            }
            else
            {
                if (read is { Logout: not null } partial) lastOnline = partial.Logout;
                previous = null;
                streak = 0;
            }

            if (elapsed() >= timeout) return new PlcOnlineSettled(lastOnline, false);
            sleep(interval);
        }
    }
}
