namespace PtcXaeMcp.WinForms;

/// <summary>
/// Pure, COM-free decision functions that harden <see cref="NativeTwinCatRunner"/>'s
/// schema-apply against destroying a populated, productive brownfield target. Each
/// function encodes one fail-closed invariant so it can be unit-tested without a live
/// TwinCAT XAE.
///
/// Core invariant: a brownfield apply may only touch a provably-empty, exactly-addressed
/// target master and must hard-abort on any uncertainty rather than silently proceed.
/// </summary>
internal static class BrownfieldGuard
{
    // H1 — Fail-closed empty-check.
    //
    // The lenient GetChildCountSafe swallows every COM exception and returns 0, which the
    // brownfield empty-check (childCount > 0) then reads as "master is empty" on a full
    // master and proceeds to build into it. On the guard path the read must be strict:
    // a COM failure is uncertainty, and uncertainty must surface, not coerce to 0.

    /// <summary>
    /// Reads a child count under the configured failure policy. In fail-closed mode a COM
    /// read failure propagates (the caller must treat it as "unknown", never "empty"); in
    /// lenient mode the historical return-0 tolerance is preserved for the enumeration loops.
    /// </summary>
    public static int ReadChildCount(Func<int> read, bool failClosed)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (failClosed)
        {
            // No catch: surface the COM error to the guard caller.
            return read();
        }

        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Asserts the target EtherCAT master is empty before an additive brownfield apply.
    /// A non-zero direct-child count is a hard stop — nothing is ever deleted.
    /// </summary>
    public static void AssertMasterEmpty(int childCount, string masterName)
    {
        if (childCount > 0)
        {
            throw new InvalidOperationException(
                $"Unter dem EtherCAT-Master '{masterName}' sind bereits {childCount} Kindknoten vorhanden. " +
                "Es wird nichts geloescht. Bitte einen nachweislich leeren Ziel-Master verwenden.");
        }
    }

    // H2 — Exact master addressing.
    //
    // Greenfield resolution is fuzzy on purpose: ResolveMasterName falls back to the
    // first master (candidates[0]) and ResolveIoDevice accepts a StartsWith name match.
    // For a brownfield apply that fuzziness is dangerous — it can silently retarget the
    // wrong, populated master. Fail-closed mode forbids both fallbacks.

    /// <summary>
    /// The brownfield strict mode is active when explicitly requested via failClosed or
    /// implied by pinning an exact target master path.
    /// </summary>
    public static bool IsStrict(bool failClosed, string? targetMasterPath)
        => failClosed || !string.IsNullOrWhiteSpace(targetMasterPath);

    /// <summary>
    /// Selects an EtherCAT master by name. An exact (case-insensitive) match always wins.
    /// In strict mode the absence of an exact match is a hard error — there is no
    /// "first master" fallback; lenient mode keeps the historical candidates[0] behaviour.
    /// </summary>
    public static string SelectMaster(string requestedName, IReadOnlyList<string> candidates, bool strict)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            string? exact = candidates.FirstOrDefault(
                c => string.Equals(c, requestedName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }
        }

        if (strict)
        {
            throw new InvalidOperationException(
                $"No EtherCAT master with the exact name '{requestedName}' found " +
                $"(available: {(candidates.Count == 0 ? "<none>" : string.Join(", ", candidates))}). " +
                "Fail-closed: no fuzzy/first-master fallback. Specify the exact master name or targetMasterPath.");
        }

        return candidates.Count > 0 ? candidates[0] : requestedName;
    }

    /// <summary>
    /// Whether ResolveIoDevice may fall back to a StartsWith fuzzy name match. Forbidden in
    /// strict mode — only an exact name or an exact tree path resolves the device there.
    /// </summary>
    public static bool AllowFuzzyDeviceMatch(bool strict) => !strict;

    // H3 — GVL-collision guard: gone together with the GVL step of apply_schema. Links address
    // the AT %I*/%Q* members the schema names (ioPoints[].symbol); apply_schema creates and deletes no PLC object.

    // H4 — Build controllable.
    //
    // apply_schema always forces a full SolutionBuild.Build(true), rebuilding the entire
    // productive PLC project and re-binding the existing process image. A brownfield apply
    // can deliberately skip that to leave the running project untouched.

    /// <summary>
    /// Runs the full productive build only when autoBuild is on. Returns whether the build
    /// action was invoked, so the caller can log the intentional skip. The action carries the
    /// SolutionBuild.Build(true) call, so when autoBuild is off it is provably never invoked.
    /// </summary>
    public static bool RunBuildIfEnabled(bool autoBuild, Action runBuild)
    {
        ArgumentNullException.ThrowIfNull(runBuild);
        if (!autoBuild)
        {
            return false;
        }

        runBuild();
        return true;
    }

    // MCP surface: pre-flight validation so a malformed brownfield request comes back as a
    // clean tool error instead of a silent success or a late COM throw.

    /// <summary>
    /// Validates a brownfield request at the tool boundary. A non-empty target master path
    /// must be an absolute tree path under "TIID" (e.g. "TIID^Device 1 (EtherCAT)"); the
    /// bare root is rejected. Returns the list of issues (empty when the request is well-formed).
    /// </summary>
    public static IReadOnlyList<string> ValidateBrownfieldRequest(string? targetMasterPath)
    {
        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetMasterPath))
        {
            string p = targetMasterPath.Trim();
            const string prefix = "TIID^";
            if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || p.Length <= prefix.Length)
            {
                issues.Add(
                    $"targetMasterPath '{targetMasterPath}' must be an absolute tree path under 'TIID' " +
                    "(e.g. 'TIID^Device 1 (EtherCAT)').");
            }
        }

        return issues;
    }
}
