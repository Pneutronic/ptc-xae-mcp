namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Says why <c>CreateChild</c> may have refused an EtherCAT box type: the XAE only knows the ESI files it
/// loaded, so a vendor ESI placed under <c>Config\Io\EtherCAT</c> afterwards is "Invalid item sub type" until
/// the device descriptions are reloaded. The ESI directory is searched with the same cache as
/// <c>twincat_list_ethercat_box_catalog</c>.
/// </summary>
internal static class EsiInstallHint
{
    /// <summary>
    /// The sentence to add to a failed add-box message, or null when it cannot be told (no ESI directory
    /// or an unreadable one).
    /// </summary>
    /// <param name="esiDirectory">Null → <see cref="EsiCatalogScanner.ResolveEsiDirectory"/>.</param>
    public static string? For(string vInfo, string? esiDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(vInfo)) return null;
        try
        {
            string? dir = esiDirectory ?? EsiCatalogScanner.ResolveEsiDirectory();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            string[] candidates = EtherCatVInfo.GetCreateChildVInfoCandidates(vInfo);
            EsiCatalogScan scan = EsiCatalogScanner.Scan(dir);
            EsiCatalogDevice? found = scan.Devices.FirstOrDefault(d =>
                candidates.Contains(d.VInfo, StringComparer.OrdinalIgnoreCase) ||
                candidates.Contains(d.Type, StringComparer.OrdinalIgnoreCase));

            return found is not null
                ? $"The ESI for {found.Type} is installed ({Path.GetRelativePath(scan.Directory, found.SourceFile)}) " +
                  "but the XAE has not loaded it: run twincat_reload_device_descriptions, then retry."
                : $"No ESI describes {vInfo} under {scan.Directory}: place the vendor's ESI there, " +
                  "then run twincat_reload_device_descriptions.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
