namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- variant-management --------------------------------------------------
    //
    // TwinCAT Variant Management exposes two properties on ITcSysManager14
    // (TCatSysManagerLib >= 3.3.0.0). Both are accessed via dynamic late binding
    // so no specific COM interface version cast is needed.
    //
    // CurrentProjectVariant  — read/write string; active variant name, or
    //                          "[GroupName]" when a variant group is active.
    // ProjectVariantConfig   — read-only string containing an XML document that
    //                          lists all variants and groups. Returns null / empty
    //                          when the project has no variants defined.
    //
    // XML schema (actual Beckhoff format — Name is a child element, not an attribute):
    //   <ProjectVariants>
    //     <Variant><Name>SIMULATION</Name></Variant>
    //     <Variant><Name>PRODUCTION</Name></Variant>
    //     <VariantGroup><Name>AllSim</Name>
    //       <Variant><Name>SIMULATION</Name></Variant>
    //     </VariantGroup>
    //   </ProjectVariants>

    public VariantStatusResult GetVariantStatus() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic sysMan = RequireSystemManager(dte);

        string active;
        string configXml;
        try
        {
            active    = (string?)sysMan.CurrentProjectVariant ?? string.Empty;
            configXml = (string?)sysMan.ProjectVariantConfig  ?? string.Empty;
        }
        catch (Exception ex)
        {
            // Message text only — the [Error] prefix belongs to the MCP result
            // contract and is added exactly once by the tool/coordinator catch
            // block that turns this exception into a string; prefixing it here
            // too produced a live "[Error] [Error] ..." double-prefix.
            throw new InvalidOperationException(
                $"Variant Management requires TwinCAT XAE >= 3.1.4024 " +
                $"(TCatSysManagerLib >= 3.3.0.0): {ex.Message}", ex);
        }

        string rawXml = string.IsNullOrWhiteSpace(configXml) ? string.Empty : configXml;
        if (string.IsNullOrWhiteSpace(configXml))
            configXml = "<ProjectVariants/>";

        var (variants, groups) = VariantConfigParser.Parse(configXml);
        // Include raw XML when variants are empty so callers can diagnose unexpected formats.
        string? diag = variants.Count == 0 && groups.Count == 0 ? rawXml : null;
        return new VariantStatusResult(active, variants, groups, diag);
    }, OpenTimeout);

    public string SetActiveVariant(string variantName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic sysMan = RequireSystemManager(dte);
        // Issue #148 (LIVE-10): the setter failing is NOT evidence of an old XAE — an unknown
        // variant name fails the same way. Report the real COM message, without a prefix (the
        // coordinator adds exactly one "[Error]"). Busy propagates to the retry scope.
        try
        {
            sysMan.CurrentProjectVariant = variantName;
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"Setting CurrentProjectVariant to '{variantName}' failed: {ex.Message}", ex);
        }
        return $"Variant set to '{variantName}'.";
    }, OpenTimeout);

    // ---- variant-editing -------------------------------------------------------
    //
    // ProjectVariantConfig is read/write (ITcSysManager14): the XML is replaced as a whole, so the caller
    // reads it right before editing (VariantConfigEditor) and reads it back after the write.
    // ProjectVariantItems holds the per-variant values of the tree items that carry a Pv* flag.

    public string ReadProjectVariantConfig() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic sysMan = RequireSystemManager(dte);
        try { return (string?)sysMan.ProjectVariantConfig ?? string.Empty; }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"Reading ProjectVariantConfig failed (Variant Management needs TwinCAT XAE >= 3.1.4024): {ex.Message}", ex);
        }
    }, OpenTimeout);

    public void WriteProjectVariantConfig(string xml) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic sysMan = RequireSystemManager(dte);
        try { sysMan.ProjectVariantConfig = xml; }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException($"Setting ProjectVariantConfig failed: {ex.Message}", ex);
        }
        SaveAllAfterMutation(dte);
    }, OpenTimeout);

    public string ReadProjectVariantItems() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic sysMan = RequireSystemManager(dte);
        try { return (string?)sysMan.ProjectVariantItems ?? string.Empty; }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return string.Empty; }
    }, OpenTimeout);
}
