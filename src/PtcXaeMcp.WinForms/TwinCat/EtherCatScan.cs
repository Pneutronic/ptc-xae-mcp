using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One box of a configured or scanned EtherCAT chain, by identity (VendorId + ProductCode + RevisionNo).</summary>
internal sealed record EtherCatScanBox(int Position, string Name, long? VendorId, long? ProductCode, long? RevisionNo);

/// <summary>The configured boxes of an EtherCAT master and the boxes a scan of the same network found.</summary>
internal sealed record EtherCatScanData(
    string MasterTreePath,
    IReadOnlyList<EtherCatScanBox> Configured,
    IReadOnlyList<EtherCatScanBox> Scanned);

internal sealed record EtherCatScanDifference(
    int Position, string Kind, EtherCatScanBox? Configured, EtherCatScanBox? Scanned);

internal sealed record EtherCatScanComparison(
    bool Identical, IReadOnlyList<EtherCatScanDifference> Differences);

/// <summary>
/// Compares the configured chain with the scanned one by position, not by box name. The chains are aligned on
/// (vendor, product) with the longest common subsequence, so a box inserted or pulled in the middle is one
/// difference, not one per box behind it.
/// </summary>
internal static class EtherCatScanComparer
{
    internal const string Missing = "missing";
    internal const string Extra = "extra";
    internal const string DifferentProduct = "different_product";
    internal const string DifferentRevision = "different_revision";

    private static string Key(EtherCatScanBox b) =>
        b.VendorId is not null && b.ProductCode is not null ? $"{b.VendorId}:{b.ProductCode}" : $"name:{b.Name}";

    internal static EtherCatScanComparison Compare(
        IReadOnlyList<EtherCatScanBox> configured, IReadOnlyList<EtherCatScanBox> scanned)
    {
        int n = configured.Count, m = scanned.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = Key(configured[i]) == Key(scanned[j])
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var differences = new List<EtherCatScanDifference>();
        var gapConfigured = new List<EtherCatScanBox>();
        var gapScanned = new List<EtherCatScanBox>();

        void FlushGap()
        {
            int pairs = Math.Min(gapConfigured.Count, gapScanned.Count);
            for (int k = 0; k < pairs; k++)
                differences.Add(new(gapScanned[k].Position, DifferentProduct, gapConfigured[k], gapScanned[k]));
            for (int k = pairs; k < gapConfigured.Count; k++)
                differences.Add(new(gapConfigured[k].Position, Missing, gapConfigured[k], null));
            for (int k = pairs; k < gapScanned.Count; k++)
                differences.Add(new(gapScanned[k].Position, Extra, null, gapScanned[k]));
            gapConfigured.Clear();
            gapScanned.Clear();
        }

        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (Key(configured[a]) == Key(scanned[b]))
            {
                FlushGap();
                if (configured[a].RevisionNo != scanned[b].RevisionNo)
                    differences.Add(new(scanned[b].Position, DifferentRevision, configured[a], scanned[b]));
                a++; b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1]) gapConfigured.Add(configured[a++]);
            else gapScanned.Add(scanned[b++]);
        }
        while (a < n) gapConfigured.Add(configured[a++]);
        while (b < m) gapScanned.Add(scanned[b++]);
        FlushGap();

        differences.Sort((x, y) => x.Position.CompareTo(y.Position));
        return new EtherCatScanComparison(differences.Count == 0, differences);
    }
}

/// <summary>Numbers the projected boxes of a master as a chain for the comparison.</summary>
internal static class EtherCatScanChain
{
    /// <summary>
    /// End caps and other passive boxes have no identity and are never found by a scan, so they are left out on
    /// both sides.
    /// </summary>
    internal static List<EtherCatScanBox> From(IEnumerable<EtherCatProjectedSlave> slaves)
    {
        var result = new List<EtherCatScanBox>();
        foreach (var s in slaves.Where(x => !x.Passive))
            result.Add(new EtherCatScanBox(result.Count + 1, s.Name, s.VendorId, s.ProductCode, s.RevisionNo));
        return result;
    }
}

/// <summary>The XML side of the scan: the adapter of the target device and the ConsumeXml that starts ScanBoxes.</summary>
internal static class EtherCatScanXml
{
    /// <summary>The <c>AddressInfo</c> element (the network adapter) of an EtherCAT device's XML, or an error text.</summary>
    internal static (string? AddressInfo, string? Error) ExtractAddressInfo(string deviceXml)
    {
        const string none = "The device has no network adapter assigned (empty AddressInfo), so there is nothing to scan. " +
                            "Assign an adapter to the EtherCAT device first.";
        try
        {
            if (string.IsNullOrWhiteSpace(deviceXml)) return (null, none);
            var info = XDocument.Parse(deviceXml).Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "AddressInfo", StringComparison.OrdinalIgnoreCase));
            bool empty = info is null || !info.Descendants().Any(d => !string.IsNullOrWhiteSpace(d.Value));
            return empty ? (null, none) : (info!.ToString(SaveOptions.DisableFormatting), null);
        }
        catch (Exception ex)
        {
            return (null, $"The device XML could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The XML for ConsumeXml on the temporary device: the target's adapter plus the ScanBoxes request
    /// (Infosys: <c>&lt;TreeItem&gt;&lt;DeviceDef&gt;&lt;ScanBoxes&gt;1&lt;/ScanBoxes&gt;&lt;/DeviceDef&gt;&lt;/TreeItem&gt;</c>).
    /// </summary>
    internal static string BuildScanXml(string addressInfo) =>
        new XElement("TreeItem",
            new XElement("DeviceDef",
                XElement.Parse(addressInfo),
                new XElement("ScanBoxes", "1"))).ToString(SaveOptions.DisableFormatting);

    internal static string TemporaryDeviceName() => "PtcScanTmp_" + Guid.NewGuid().ToString("N")[..8];
}
