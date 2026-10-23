namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- IO mapping tools (feature/io-mapping) ------------------------------

    public void LinkVariables(string v1, string v2, int offs1 = 0, int offs2 = 0, int size = 0) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object? sysMan = RequireSystemManager(dte);
            try { ((dynamic)sysMan).LinkVariables(v1, v2, offs1, offs2, size); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(DiagnoseLinkFailure(sysMan, v1, v2, ex), ex);
            }
            // #171 audit (issue #152): a replayed LinkVariables is not idempotent — keep busy local.
            SaveAllAfterMutation(dte);
        }, OpenTimeout);

    // Failure path only: reads both items to name the cause. A busy XAE or any read problem here must
    // not hide the original error, so every probe failure falls back to the plain code-named message.
    private static string DiagnoseLinkFailure(object sysMan, string v1, string v2, Exception ex)
    {
        int hr = LinkFailureDiagnosis.HResultOf(ex);
        try
        {
            return LinkFailureDiagnosis.Describe(hr, ex.Message,
                ProbeLinkEndpoint(sysMan, v1), ProbeLinkEndpoint(sysMan, v2));
        }
        catch (Exception)
        {
            // Includes a busy XAE: the link itself already failed and a replay is not idempotent.
            return LinkFailureDiagnosis.Fallback(hr, ex.Message);
        }
    }

    private static LinkEndpoint ProbeLinkEndpoint(object sysMan, string path)
    {
        dynamic? node = LookupTreeItemOrNull(sysMan, path);
        if (node is null) return LinkEndpoint.Missing(path);
        int itemType = ComCall.ReadProp(() => (int)node.ItemType, -1);
        string? xml = ComCall.ReadProp<string?>(() => (string)node.ProduceXml(false), null);
        return new LinkEndpoint(path, true, itemType, VariableInfo.TryParse(xml));
    }

    public void UnlinkVariables(string v1, string v2 = "") =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object? sysMan = RequireSystemManager(dte);
            try { ((dynamic)sysMan).UnlinkVariables(v1, v2); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"UnlinkVariables failed: {ex.Message}", ex);
            }
            SaveAllAfterMutation(dte);
        }, OpenTimeout);

    public string GetMappingInfo() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        try { return (string)((dynamic)sysMan).ProduceMappingInfo(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ProduceMappingInfo failed: {ex.Message}", ex);
        }
    }, OpenTimeout);

    // SMDS_DISABLED=1, SMDS_NOT_DISABLED=0 (ITcSmTreeItem::Disabled property)
    public void SetDeviceDisabled(string treePath, bool disabled) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object? sysMan = RequireSystemManager(dte);
            dynamic? node = LookupTreeItemOrNull(sysMan, treePath)
                ?? throw new InvalidOperationException($"Tree path not found: {treePath}");
            try { node.Disabled = disabled ? 1 : 0; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Setting Disabled on '{treePath}' failed: {ex.Message}", ex);
            }
            // Read-back on a freshly resolved node (issue #152): the flag must have changed.
            int? now = AfterMutation(() =>
            {
                dynamic? fresh = LookupTreeItemOrNull(sysMan, treePath);
                return fresh is null ? (int?)null : ComCall.ReadProp(() => (int?)(int)fresh.Disabled, null);
            });
            if (now is null)
                throw new InvalidOperationException(
                    $"Disabled was set on '{treePath}', but it could not be read back, so the change is not confirmed. Nothing was saved.");
            if ((now.Value != 0) != disabled)
                throw new InvalidOperationException(
                    $"Setting Disabled={(disabled ? 1 : 0)} on '{treePath}' did not land: the read-back shows {now.Value}. Nothing was saved.");
            SaveAllAfterMutation(dte);
        }, OpenTimeout);
}
