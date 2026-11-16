using System.Text.RegularExpressions;
using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat.Schema;

/// <summary>
/// Pure candidate-path generation and scoring for IO-leaf and PLC-variable linking,
/// extracted from NativeTwinCatRunner (AddIoCandidates, FindIoLeaf's scoring block,
/// ScoreEthernetBranchPortCandidate). No COM: every method here takes plain
/// strings and returns candidate path strings or a score; the Linker does the tree lookups.
/// Issue #155 E-3: there is no PLC-variable scoring any more — a PLC variable is addressed by its
/// exact process-image symbol (the point's <c>symbol</c>, e.g. <c>MAIN.fbCyl.LimitSwitchWorkingPosition</c>)
/// in the exact image folders.
/// </summary>
internal static class LinkCandidates
{
    /// <summary>
    /// Designation key of a terminal/device name: upper-case, the " (type)" suffix dropped,
    /// blanks removed ("K2a (EL1008)" -> "K2A"). Used to find a terminal whose name carries a
    /// different type suffix than the schema's.
    /// </summary>
    internal static string TerminalNameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string normalized = name.Trim().ToUpperInvariant();
        int idx = normalized.IndexOf(" (", StringComparison.Ordinal);
        if (idx > 0)
        {
            normalized = normalized[..idx];
        }
        return normalized.Replace(" ", string.Empty);
    }

    /// <summary>
    /// Names of a task's process-image folder under the PLC instance for one direction, as
    /// TwinCAT labels them (English and German UI): "PlcTask Inputs", "PlcTask Eingänge", ...
    /// </summary>
    internal static IEnumerable<string> ImageFolderNames(string taskName, string direction)
    {
        string[] suffixes = IsOutput(direction)
            ? ["Outputs", "Ausgaenge", "Ausgänge"]
            : ["Inputs", "Eingaenge", "Eingänge"];
        return suffixes.Select(suffix => $"{taskName} {suffix}");
    }

    /// <summary>Names of a terminal's IO group folder for one direction ("Inputs", "Eingänge", ...).</summary>
    internal static IReadOnlyList<string> IoGroupFolderNames(string direction) =>
        IsOutput(direction)
            ? ["Ausgaenge", "Ausgänge", "Outputs"]
            : ["Eingaenge", "Eingänge", "Inputs"];

    private static bool IsOutput(string direction) =>
        string.Equals(direction?.Trim(), "Q", StringComparison.OrdinalIgnoreCase);

    internal static void AddIoCandidates(List<string> result, string terminalPath, int channel, string direction)
    {
        bool isInput = direction.Trim().Equals("I", StringComparison.OrdinalIgnoreCase);
        string leaf = isInput ? "Input" : "Output";
        string channelNode = $"Channel {channel}";
        string localizedChannelNode = $"Kanal {channel}";
        string ioFolder = isInput ? "Eingaenge" : "Ausgaenge";
        string ioFolderUmlaut = isInput ? "Eingänge" : "Ausgänge";
        foreach (string candidate in new[]
        {
            $"{terminalPath}^{channelNode}^{leaf}",
            $"{terminalPath}^{localizedChannelNode}^{leaf}",
            $"{terminalPath}^{channelNode}",
            $"{terminalPath}^{localizedChannelNode}",
            $"{terminalPath}^{ioFolder}^{channelNode}",
            $"{terminalPath}^{ioFolderUmlaut}^{channelNode}",
            $"{terminalPath}^{ioFolder}^{channelNode}^{leaf}",
            $"{terminalPath}^{ioFolderUmlaut}^{channelNode}^{leaf}",
            $"{terminalPath}^IO Inputs {channelNode}",
            $"{terminalPath}^IO Outputs {channelNode}"
        })
        {
            if (!result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(candidate);
            }
        }
    }

    /// <summary>Port-candidate scoring for TryResolveEthernetBranchParent (which upstream
    /// Ethernet port item matches a desired previousPort letter).</summary>
    internal static int ScoreEthernetBranchPortCandidate(string name, string path, string desiredPort)
    {
        string haystack = $"{name} {path}";
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(desiredPort))
        {
            return 0;
        }

        int score = 0;
        if (haystack.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (haystack.Contains("RJ45", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (Regex.IsMatch(haystack, $@"\bPort\s*{Regex.Escape(desiredPort)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            score += 50;
        }

        if (Regex.IsMatch(haystack, $@"\b{Regex.Escape(desiredPort)}\s*\(Ethernet\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            score += 70;
        }

        if (Regex.IsMatch(haystack, $@"\b{Regex.Escape(desiredPort)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            score += 10;
        }

        return score;
    }

    /// <summary>Whether a candidate node's name/path plausibly names the requested IO channel
    /// at all, for FindIoLeaf. A false here means "skip", independent of direction scoring.</summary>
    internal static bool IsIoLeafChannelMatch(string name, string path, int channel)
    {
        string escapedChannel = Regex.Escape(channel.ToString(CultureInfo.InvariantCulture));
        var channelRegex = new Regex($@"\b(Channel|Kanal)\s+{escapedChannel}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var ioInputRegex = new Regex($@"^IO\s+Inputs?\s+Channel\s+{escapedChannel}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var ioOutputRegex = new Regex($@"^IO\s+Outputs?\s+Channel\s+{escapedChannel}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return channelRegex.IsMatch(name) || channelRegex.IsMatch(path) || ioInputRegex.IsMatch(name) || ioOutputRegex.IsMatch(name);
    }

    /// <summary>Direction/override-aware score for a channel-matching FindIoLeaf candidate.
    /// -1 means "no positive signal" (caller keeps the previous best, doesn't force a pick).</summary>
    internal static int ScoreIoLeafCandidate(string name, int channel, string direction, string ioLeafOverride)
    {
        string dir = direction.Trim().ToUpperInvariant();
        string escapedChannel = Regex.Escape(channel.ToString(CultureInfo.InvariantCulture));
        var ioInputRegex = new Regex($@"^IO\s+Inputs?\s+Channel\s+{escapedChannel}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var ioOutputRegex = new Regex($@"^IO\s+Outputs?\s+Channel\s+{escapedChannel}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var channelRegex = new Regex($@"\b(Channel|Kanal)\s+{escapedChannel}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!string.IsNullOrWhiteSpace(ioLeafOverride) && name.Equals(ioLeafOverride, StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        if (dir == "I")
        {
            if (name.Equals("Input", StringComparison.OrdinalIgnoreCase)) return 120;
            if (name.Equals("Inputs", StringComparison.OrdinalIgnoreCase)) return 115;
            if (ioInputRegex.IsMatch(name)) return 110;
            if (channelRegex.IsMatch(name)) return 95;
            if (name.Contains("Input", StringComparison.OrdinalIgnoreCase)) return 90;
            if (name.Contains("Value", StringComparison.OrdinalIgnoreCase)) return 70;
        }
        else
        {
            if (name.Equals("Output", StringComparison.OrdinalIgnoreCase)) return 120;
            if (name.Equals("Outputs", StringComparison.OrdinalIgnoreCase)) return 115;
            if (ioOutputRegex.IsMatch(name)) return 110;
            if (channelRegex.IsMatch(name)) return 95;
            if (name.Contains("Output", StringComparison.OrdinalIgnoreCase)) return 90;
            if (name.Contains("Value", StringComparison.OrdinalIgnoreCase)) return 70;
        }

        return -1;
    }
}
