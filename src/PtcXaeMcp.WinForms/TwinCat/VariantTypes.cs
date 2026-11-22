namespace PtcXaeMcp.WinForms.TwinCat;

// ---- variant-management ------------------------------------------------------

internal sealed record VariantStatusResult(
    string ActiveVariant,
    IReadOnlyList<string> AvailableVariants,
    IReadOnlyList<VariantGroupEntry> Groups,
    string? RawConfigXml = null);

internal sealed record VariantGroupEntry(string Name, IReadOnlyList<string> Members);

// ---- variant-editing ----------------------------------------------------------

/// <summary>
/// A tree item's variant-relevant state in the ACTIVE variant: the value (<c>Disabled</c>, simulation) and the
/// Pv* flags that say which of its settings are stored per variant.
/// </summary>
internal sealed record VariantItemState(
    string Path,
    bool IsNcAxis,
    bool Disabled,
    bool? AxisSimulation,
    bool PvDisable,
    bool PvParameter,
    bool PvLink);
