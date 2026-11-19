using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Pure vInfo (EtherCAT product-revision) resolution shared by the
/// apply-schema runner (<see cref="NativeTwinCatRunner"/>) and the granular
/// ethercat-topology-edit tools (<see cref="TwinCatComSession"/>).
///
/// Extracted verbatim from NativeTwinCatRunner so both code paths build
/// EtherCAT boxes with the same revision-fallback behaviour (placeholder →
/// base type, KEB ESI aliases). No COM, no state — safe to share.
/// </summary>
internal static class EtherCatVInfo
{
    /// <summary>EtherCAT box/terminal CreateChild ItemSubType (Infosys 242737035).</summary>
    public const int BoxItemSubType = 9099;

    /// <summary>
    /// Ordered vInfo candidates to try with ITcSmTreeItem.CreateChild: the
    /// requested revision first, then known ESI aliases, then the bare base
    /// type (which makes TwinCAT auto-select the latest installed revision).
    /// </summary>
    public static string[] GetCreateChildVInfoCandidates(string vInfo) => GetCreateChildVInfoCandidates(vInfo, null);

    /// <summary>
    /// As <see cref="GetCreateChildVInfoCandidates(string)"/>, with the identity vInfos
    /// (<c>V…_P…_R…</c>) that <paramref name="esiIdentities"/> finds for a bare third-party Type inserted right
    /// after the requested vInfo: CreateChild refuses a third-party box by its Type (#52).
    /// </summary>
    public static string[] GetCreateChildVInfoCandidates(string vInfo, Func<string, IReadOnlyList<string>>? esiIdentities)
    {
        var candidates = new List<string>();

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        AddCandidate(vInfo);
        if (esiIdentities is not null)
        {
            foreach (string identity in esiIdentities(vInfo))
            {
                AddCandidate(identity);
            }
        }

        foreach (string alias in GetKnownVInfoAliases(vInfo))
        {
            AddCandidate(alias);
        }

        if (TryGetBaseVInfo(vInfo, out string? baseVInfo))
        {
            AddCandidate(baseVInfo);
        }

        return candidates.ToArray();
    }

    private static IEnumerable<string> GetKnownVInfoAliases(string vInfo)
    {
        string normalized = Regex.Replace(vInfo ?? string.Empty, @"[^A-Za-z0-9]+", string.Empty)
            .ToUpperInvariant();

        // KEB drives use dedicated ESI files where the TwinCAT dialog shows
        // <Name>, while CreateChild typically expects the internal <Type>.
        if (normalized.StartsWith("10S6P", StringComparison.Ordinal))
        {
            yield return "PTC_S6P_STO_V1";
            yield return "KEB_PTC_S6P_STO_V1 (MDP)";
            yield return "PTC_S6P_SLS_V1";
            yield return "KEB_PTC_S6P_SLS_V1 (MDP)";
        }

        if (normalized.StartsWith("10F6P", StringComparison.Ordinal))
        {
            yield return "PTC_F6P_STO_V1";
            yield return "KEB_PTC_F6P_STO_V1 (MDP)";
            yield return "PTC_F6P_SLS_V1";
            yield return "KEB_PTC_F6P_SLS_V1 (MDP)";
        }
    }

    /// <summary>
    /// Collapse a revisioned/decorated vInfo to its bare terminal-like base
    /// token, e.g. "EK1100-0000-0017" → "EK1100",
    /// "KRC4 secondary EL6695-1001" → "EL6695".
    /// </summary>
    public static bool TryGetBaseVInfo(string vInfo, out string? baseVInfo)
    {
        baseVInfo = null;
        if (string.IsNullOrWhiteSpace(vInfo))
        {
            return false;
        }

        string trimmed = vInfo.Trim();
        MatchCollection matches = Regex.Matches(
            trimmed,
            @"\b(?<base>[A-Za-z]{2,4}\d{3,4})(?:-[A-Za-z0-9]+)*\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Prefer the last terminal-like token so labels such as
        // "KRC4 secondary EL6695-1001" collapse to "EL6695".
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            string candidate = matches[i].Groups["base"].Value;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                baseVInfo = candidate;
                return true;
            }
        }

        return false;
    }
}
