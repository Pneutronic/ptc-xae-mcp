using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // twincat_dump_nc_tree moved to TwinCatMcpToolsDiagnostics (Mcp/TwinCatMcpTools.Diagnostics.cs) —
    // a research/discovery diagnostic, registered only with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

    [McpServerTool(Name = "twincat_list_nc_tasks_axes", ReadOnly = true)]
    [Description("List every NC-Task and its axes in the Motion (NC) tree. " +
                 "Read-only; needs an open solution. Returns a summary line and one line per NC-Task and axis with its " +
                 "tree path, for twincat_create_axis / twincat_configure_axis.")]
    public string ListNcTasksAxes(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_nc_tasks_axes", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, tasks) = new NcAxesCoordinator(bridge.Dispatcher.Session).ListNcTasksAxes();
                if (McpErrors.IsError(summary) || tasks.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var t in tasks)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  NC-Task  {t.Path}");
                    foreach (var a in t.Axes)
                        sb.AppendLine(CultureInfo.InvariantCulture, $"    AXIS   {a.Path}");
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_create_nc_task", ReadOnly = false, Destructive = false)]
    [Description("Create an NC-Task under the Motion (TINC) root. " +
                 "Needs an open solution. Works only when the project has no NC-Task yet; otherwise it returns guidance to " +
                 "reuse the existing one (twincat_list_nc_tasks_axes + twincat_create_axis). Returns the created tree path " +
                 "as read back. NOT saved: call twincat_save_project.")]
    public string CreateNcTask(
        [Description("Name for the new NC-Task, e.g. 'NC-Task 1'.")] string name,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_create_nc_task", new { name }, () =>
            bridge.Dispatcher.Run(() =>
                new NcAxesCoordinator(bridge.Dispatcher.Session).CreateNcTask(name)), cancellationToken);

    [McpServerTool(Name = "twincat_create_axis", ReadOnly = false, Destructive = false)]
    [Description("Create an axis under an existing NC-Task. " +
                 "Needs an open solution. Returns the created axis path as read back. NOT saved: call twincat_save_project.")]
    public string CreateAxis(
        [Description("Tree path of the NC-Task, from twincat_list_nc_tasks_axes.")] string ncTaskPath,
        [Description("Name of the new axis, e.g. 'Axis 1'.")] string axisName,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_create_axis", new { ncTaskPath, axisName }, () =>
            bridge.Dispatcher.Run(() =>
                new NcAxesCoordinator(bridge.Dispatcher.Session).CreateAxis(ncTaskPath, axisName)), cancellationToken);

    [McpServerTool(Name = "twincat_configure_axis", ReadOnly = false, Destructive = true)]
    [Description("Change parameters of an existing NC axis; unspecified parameters are kept. " +
                 "Needs an open solution. Pass only the fields to change, as text (numbers/booleans too). " +
                 "NOT saved: call twincat_save_project.")]
    public string ConfigureAxis(
        [Description("Tree path of the axis, from twincat_list_nc_tasks_axes.")]
        string axisPath,
        [Description("Drive class: 'canopen_ds402' (EL72xx/ELM72xx/AX8xxx, CoE) or 'sercos_soe' (AX5xxx, SoE). Sets " +
                     "axis IO type, encoder type and drive type together; TwinCAT regenerates the process image.")]
        string? axisType = null,
        [Description("Engineering unit, e.g. 'mm' or 'deg'.")]
        string? unit = null,
        [Description("Encoder scaling numerator.")]
        string? scaleFactorNumerator = null,
        [Description("Encoder scaling denominator.")]
        string? scaleFactorDenominator = null,
        [Description("Maximum velocity.")]
        string? velocityMax = null,
        [Description("Acceleration.")]
        string? acceleration = null,
        [Description("Deceleration.")]
        string? deceleration = null,
        [Description("Jerk.")]
        string? jerk = null,
        [Description("Encoder inversion 'true'/'false'.")]
        string? encoderInverse = null,
        [Description("Encoder modulo factor.")]
        string? moduloFactor = null,
        [Description("Enable soft minimum position limit 'true'/'false'.")]
        string? softMinEnable = null,
        [Description("Soft minimum position limit value.")]
        string? softMinRange = null,
        [Description("Enable soft maximum position limit 'true'/'false'.")]
        string? softMaxEnable = null,
        [Description("Soft maximum position limit value.")]
        string? softMaxRange = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_configure_axis",
            new
            {
                axisPath, axisType, unit, scaleFactorNumerator, scaleFactorDenominator,
                velocityMax, acceleration, deceleration, jerk, encoderInverse, moduloFactor,
                softMinEnable, softMinRange, softMaxEnable, softMaxRange
            }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) fields[k] = v!.Trim(); }
                Add("axisType", axisType);
                Add("unit", unit);
                Add("scaleFactorNumerator", scaleFactorNumerator);
                Add("scaleFactorDenominator", scaleFactorDenominator);
                Add("velocityMax", velocityMax);
                Add("acceleration", acceleration);
                Add("deceleration", deceleration);
                Add("jerk", jerk);
                Add("encoderInverse", encoderInverse);
                Add("moduloFactor", moduloFactor);
                Add("softMinEnable", softMinEnable);
                Add("softMinRange", softMinRange);
                Add("softMaxEnable", softMaxEnable);
                Add("softMaxRange", softMaxRange);
                return new NcAxesCoordinator(bridge.Dispatcher.Session).ConfigureAxis(axisPath, fields);
            }), cancellationToken);

    [McpServerTool(Name = "twincat_read_axis_params", ReadOnly = true)]
    [Description("Read the current parameters of an NC axis. " +
                 "Read-only; needs an open solution. Returns a summary, the known fields (same keys as " +
                 "twincat_configure_axis) and the raw axis XML.")]
    public string ReadAxisParams(
        [Description("Tree path of the axis, from twincat_list_nc_tasks_axes.")] string axisPath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_axis_params", new { axisPath }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, p) = new NcAxesCoordinator(bridge.Dispatcher.Session).ReadAxisParams(axisPath);
                if (McpErrors.IsError(summary) || p is null)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var kv in p.Fields)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {kv.Key} = {kv.Value}");
                sb.AppendLine("--- raw ProduceXml ---");
                sb.Append(p.RawXml);
                return sb.ToString();
            }), cancellationToken);
}
