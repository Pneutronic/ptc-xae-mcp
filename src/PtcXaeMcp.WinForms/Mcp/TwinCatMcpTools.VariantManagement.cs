using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_variant_status", ReadOnly = true)]
    [Description("Get the active project variant and all available variants and groups. " +
                 "Returns JSON { activeVariant, availableVariants, groups } or [Error]; empty lists when the project has no " +
                 "Variant Management.")]
    public string GetVariantStatus(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_variant_status", null, () =>
            bridge.Dispatcher.Run(() =>
                new VariantManagementCoordinator(bridge.Dispatcher.Session).GetVariantStatus()), cancellationToken);

    [McpServerTool(Name = "twincat_set_active_variant", ReadOnly = false, Destructive = true)]
    [Description("Set the active project variant by name, optionally building afterwards. " +
                 "An unknown name returns [Error] and changes nothing; the switch is read back and a failed switch restores " +
                 "the previous variant. Returns a status message (plus build log when building) or [Error].")]
    public string SetActiveVariant(
        [Description("Variant name as defined in the project, e.g. SIMULATION; [GroupName] activates a variant group.")] string variantName,
        [Description("Build the solution after switching. Default true; without a build the change does not take effect.")] bool rebuildAfter = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_active_variant", new { variantName, rebuildAfter }, () =>
            bridge.Dispatcher.Run(() =>
                new VariantManagementCoordinator(bridge.Dispatcher.Session)
                    .SetActiveVariant(variantName, rebuildAfter)), cancellationToken);

    [McpServerTool(Name = "twincat_variant_create", ReadOnly = false, Destructive = false)]
    [Description("Create a project variant, optionally as a member of an existing variant group. " +
                 "A duplicate or invalid name returns [Error] and changes nothing; the variant list is read back and a write " +
                 "that does not land is rolled back. The first variant of a project becomes the active variant. " +
                 "Returns JSON { variant, group, activeVariant, availableVariants, groups, notes } or [Error].")]
    public string VariantCreate(
        [Description("Name of the new variant, e.g. Machine or Simulation. Must not contain [ ] ^ < > & quotes or slashes.")] string variantName,
        [Description("Optional name of an existing variant group to add the variant to.")] string? group = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_variant_create", new { variantName, group }, () =>
            bridge.Dispatcher.Run(() =>
                new VariantEditingCoordinator(bridge.Dispatcher.Session).Create(variantName, group)), cancellationToken);

    [McpServerTool(Name = "twincat_variant_delete", ReadOnly = false, Destructive = true)]
    [Description("Delete a project variant and its memberships in variant groups. " +
                 "The active variant (or a member of the active group) is refused with a hint to switch first; an unknown name " +
                 "returns [Error] and changes nothing. Per-variant settings of tree items that remain are named in the result. " +
                 "Returns JSON { variant, removedFromGroups, availableVariants, groups, notes } or [Error].")]
    public string VariantDelete(
        [Description("Name of the variant to delete, as listed by twincat_get_variant_status.")] string variantName,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_variant_delete", new { variantName }, () =>
            bridge.Dispatcher.Run(() =>
                new VariantEditingCoordinator(bridge.Dispatcher.Session).Delete(variantName)), cancellationToken);
}
