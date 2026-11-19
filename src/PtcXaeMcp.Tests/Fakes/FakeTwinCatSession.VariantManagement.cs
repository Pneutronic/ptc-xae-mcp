using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- variant-management ----
    public VariantStatusResult FakeVariantStatus { get; set; } =
        new(string.Empty, [], []);
    public Exception? GetVariantStatusThrows { get; set; }
    public Exception? SetActiveVariantThrows { get; set; }
    public string FakeSetActiveVariantResult { get; set; } = "Variant set.";

    public VariantStatusResult GetVariantStatus()
    {
        CallLog.Add("GetVariantStatus()");
        if (GetVariantStatusThrows is not null) throw GetVariantStatusThrows;
        return FakeVariantStatus;
    }

    /// <summary>
    /// What CurrentProjectVariant reads after a set. Null (default) = the requested name
    /// (a working setter); set it to simulate a setter that does not take effect / clears it.
    /// </summary>
    public string? VariantAfterSet { get; set; }
    /// <summary>Applied even when <see cref="SetActiveVariantThrows"/> is set (LIVE-10: a failing set cleared the variant).</summary>
    public string? VariantAfterFailedSet { get; set; }

    public string SetActiveVariant(string variantName)
    {
        CallLog.Add($"SetActiveVariant({variantName})");
        if (SetActiveVariantThrows is not null)
        {
            if (VariantAfterFailedSet is not null)
            {
                FakeVariantStatus = FakeVariantStatus with { ActiveVariant = VariantAfterFailedSet };
                VariantAfterFailedSet = null; // only the first (failing) call corrupts the state
            }
            var ex = SetActiveVariantThrows;
            SetActiveVariantThrows = null; // the restore call succeeds
            throw ex;
        }
        FakeVariantStatus = FakeVariantStatus with { ActiveVariant = VariantAfterSet ?? variantName };
        VariantAfterSet = null;
        return FakeSetActiveVariantResult;
    }

    // ---- variant-editing ----
    /// <summary>The ProjectVariantConfig XML the fake holds; null derives it from <see cref="FakeVariantStatus"/>.</summary>
    public string? FakeVariantConfigXml { get; set; }
    public string FakeVariantItemsXml { get; set; } = string.Empty;
    public Exception? ReadVariantConfigThrows { get; set; }
    public Exception? WriteVariantConfigThrows { get; set; }
    /// <summary>When true a write is accepted but the read-back shows the previous XML (a setter that does not land).</summary>
    public bool WriteVariantConfigIsIgnored { get; set; }
    public List<string> WrittenVariantConfigs { get; } = [];

    public string ReadProjectVariantConfig()
    {
        CallLog.Add("ReadProjectVariantConfig()");
        if (ReadVariantConfigThrows is not null) throw ReadVariantConfigThrows;
        if (FakeVariantConfigXml is not null) return FakeVariantConfigXml;
        var xml = new System.Xml.Linq.XElement("ProjectVariants");
        foreach (var v in FakeVariantStatus.AvailableVariants)
            xml.Add(new System.Xml.Linq.XElement("Variant", new System.Xml.Linq.XElement("Name", v)));
        foreach (var g in FakeVariantStatus.Groups)
        {
            var ge = new System.Xml.Linq.XElement("VariantGroup", new System.Xml.Linq.XElement("Name", g.Name));
            foreach (var m in g.Members)
                ge.Add(new System.Xml.Linq.XElement("Variant", new System.Xml.Linq.XElement("Name", m)));
            xml.Add(ge);
        }
        return FakeVariantStatus.AvailableVariants.Count + FakeVariantStatus.Groups.Count == 0 ? string.Empty : xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    public void WriteProjectVariantConfig(string xml)
    {
        CallLog.Add("WriteProjectVariantConfig()");
        if (WriteVariantConfigThrows is not null) throw WriteVariantConfigThrows;
        WrittenVariantConfigs.Add(xml);
        if (WriteVariantConfigIsIgnored) return;
        FakeVariantConfigXml = xml;
        var (variants, groups) = VariantConfigParser.Parse(xml);
        FakeVariantStatus = FakeVariantStatus with { AvailableVariants = variants, Groups = groups };
    }

    public string ReadProjectVariantItems()
    {
        CallLog.Add("ReadProjectVariantItems()");
        return FakeVariantItemsXml;
    }

    public Dictionary<string, FakeVariantItem> FakeVariantItems { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? WriteVariantItemThrows { get; set; }

    public VariantItemState ReadVariantItem(string treePath)
    {
        CallLog.Add($"ReadVariantItem({treePath})");
        if (!FakeVariantItems.TryGetValue(treePath, out var item))
            throw new InvalidOperationException($"Tree path not found: {treePath}");
        string v = FakeVariantStatus.ActiveVariant;
        item.Disabled.TryGetValue(v, out bool disabled);
        bool? sim = item.IsNcAxis ? (item.Simulation.TryGetValue(v, out bool s) && s) : null;
        return new VariantItemState(treePath, item.IsNcAxis, disabled, sim, item.PvDisable, item.PvParameter, item.PvLink);
    }

    public void SetVariantFlags(string treePath, bool pvDisable, bool pvParameter)
    {
        CallLog.Add($"SetVariantFlags({treePath}, pvDisable={pvDisable}, pvParameter={pvParameter})");
        var item = FakeVariantItems[treePath];
        item.PvDisable |= pvDisable;
        item.PvParameter |= pvParameter;
    }

    public void WriteVariantItemValue(string treePath, bool? disabled, bool? axisSimulation)
    {
        CallLog.Add($"WriteVariantItemValue({treePath}, disabled={disabled}, axisSimulation={axisSimulation})");
        if (WriteVariantItemThrows is not null) throw WriteVariantItemThrows;
        var item = FakeVariantItems[treePath];
        string v = FakeVariantStatus.ActiveVariant;
        if (disabled is { } d) item.Disabled[v] = d;
        if (axisSimulation is { } a) item.Simulation[v] = a;
    }

    public void SaveVariantChanges() => CallLog.Add("SaveVariantChanges()");
}

internal sealed class FakeVariantItem
{
    public bool IsNcAxis { get; set; }
    public bool PvDisable { get; set; }
    public bool PvParameter { get; set; }
    public bool PvLink { get; set; }
    public Dictionary<string, bool> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Simulation { get; } = new(StringComparer.OrdinalIgnoreCase);
}
