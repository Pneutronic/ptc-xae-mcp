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
}
