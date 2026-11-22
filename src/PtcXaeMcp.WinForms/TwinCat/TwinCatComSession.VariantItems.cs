namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- variant-editing: per-variant settings of tree items ------------------------
    //
    // ITcSmTreeItem9 carries the Pv* flags (PvDisable, PvParameter, PvLink, PvDefine). A flag turns the
    // matching setting of the item into a per-variant value; the value is then read and written in the
    // variant that is active (CurrentProjectVariant). Infosys: tc3_automationinterface 8204600715.

    private static dynamic ResolveVariantNode(object? sysMan, string treePath) =>
        LookupTreeItemOrNull(sysMan!, treePath)
            ?? throw new InvalidOperationException($"Tree path not found: {treePath}");

    private static dynamic? TryResolveAxis(object? sysMan, string treePath)
    {
        try { return ResolveAxis(sysMan, treePath); }
        catch (InvalidOperationException ex) when (ComCall.NotBusy(ex)) { return null; } // "not an axis"
    }

    public VariantItemState ReadVariantItem(string treePath) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic node = ResolveVariantNode(sysMan, treePath);
        return ReadVariantItemState(sysMan, node, treePath);
    }, OpenTimeout);

    private static VariantItemState ReadVariantItemState(object? sysMan, dynamic node, string treePath)
    {
        int disabled = ComCall.ReadProp(() => (int)node.Disabled, 0);
        bool pvDisable = ComCall.ReadProp(() => (bool)node.PvDisable, false);
        bool pvParameter = ComCall.ReadProp(() => (bool)node.PvParameter, false);
        bool pvLink = ComCall.ReadProp(() => (bool)node.PvLink, false);

        dynamic? axis = TryResolveAxis(sysMan, treePath);
        bool? simulation = null;
        if (axis is not null)
        {
            string xml = ComCall.ReadProp(() => (string?)axis.ProduceXml(false), null) ?? string.Empty;
            simulation = AxisSimulationXml.TryRead(xml);
        }
        // SMDS_DISABLED = 1; 2 means "disabled by the parent" and is not this item's own setting.
        return new VariantItemState(treePath, axis is not null, disabled == 1, simulation, pvDisable, pvParameter, pvLink);
    }

    public void SetVariantFlags(string treePath, bool pvDisable, bool pvParameter) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic node = ResolveVariantNode(sysMan, treePath);
        try
        {
            if (pvDisable) node.PvDisable = true;
            if (pvParameter) node.PvParameter = true;
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"Turning on the variant-dependent flag(s) of '{treePath}' failed: {ex.Message} " +
                "(per-variant item settings need TwinCAT XAE >= 3.1.4024, ITcSmTreeItem9).", ex);
        }
        var state = AfterMutation(() => ReadVariantItemState(sysMan, ResolveVariantNode(sysMan, treePath), treePath));
        if ((pvDisable && !state.PvDisable) || (pvParameter && !state.PvParameter))
            throw new InvalidOperationException(
                $"The variant-dependent flag(s) of '{treePath}' did not take effect (PvDisable={state.PvDisable}, " +
                $"PvParameter={state.PvParameter}).");
    }, OpenTimeout);

    public void WriteVariantItemValue(string treePath, bool? disabled, bool? axisSimulation) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic node = ResolveVariantNode(sysMan, treePath);

        if (disabled is { } d)
        {
            try { node.Disabled = d ? 1 : 0; }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                throw new InvalidOperationException($"Setting Disabled on '{treePath}' failed: {ex.Message}", ex);
            }
        }

        if (axisSimulation is { } sim)
        {
            dynamic axis = TryResolveAxis(sysMan, treePath)
                ?? throw new InvalidOperationException($"'{treePath}' is not an NC axis.");
            string xml;
            try { xml = (string)axis.ProduceXml(false); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                throw new InvalidOperationException($"ProduceXml failed on axis '{treePath}': {ex.Message}", ex);
            }
            string updated = AxisSimulationXml.TrySet(xml, sim)
                ?? throw new InvalidOperationException(
                    $"The axis XML of '{treePath}' does not carry the simulation flag on this XAE build, so it cannot be " +
                    "set through the Automation Interface. Nothing was changed for the axis.");
            try { axis.ConsumeXml(updated); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                throw new InvalidOperationException($"ConsumeXml failed on axis '{treePath}': {ex.Message}", ex);
            }
        }

        // Read back on a freshly resolved node (issue #152): the value must have landed in this variant.
        var now = AfterMutation(() => ReadVariantItemState(sysMan, ResolveVariantNode(sysMan, treePath), treePath));
        if (disabled.HasValue && now.Disabled != disabled.Value)
            throw new InvalidOperationException(
                $"Setting Disabled={disabled.Value} on '{treePath}' did not land: the read-back shows {now.Disabled}.");
        if (axisSimulation.HasValue && now.AxisSimulation != axisSimulation.Value)
            throw new InvalidOperationException(
                $"Setting the simulation flag={axisSimulation.Value} on '{treePath}' did not land: the read-back shows " +
                $"{(now.AxisSimulation?.ToString() ?? "no value")}.");
    }, OpenTimeout);

    public void SaveVariantChanges() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        SaveAllAfterMutation(dte);
    }, OpenTimeout);
}
