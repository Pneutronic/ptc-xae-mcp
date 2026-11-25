using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

// Element-type constants for IMeasurementScope.CreateChild — values from Infosys
// tc3_automationinterface/498477707. Confirmed against the live TwinCAT Automation
// Interface documentation (2026-05-16).
internal static class ScopeChartType
{
    public const int Yt          = 0;
    public const int Xy          = 1;
    public const int Array       = 2;
    public const int SingleBar   = 3;
}

internal static class ScopeChartElement
{
    public const int AxisGroup = 0;
}

internal static class ScopeAxisGroupMember
{
    public const int Channel      = 0;
    public const int RoundShape   = 1;
    public const int AngularShape = 2;
}

internal static class ScopeAcquisitionType
{
    public const int Ads = 0;
    public const int Opc = 1;
}

// Axis display mode for twincat_set_scope_axis_mode.
internal static class ScopeAxisMode
{
    public const string Overlaid = "overlaid";
    public const string Stacked  = "stacked";
}

// Acquisition sample mode (twincat_add_scope_channel / set_scope_acquisition_symbol).
// Domain observed in the AdsAcquisition Properties pane (2026-05-17: "Task
// Sampletime"); the exact enum/string set is confirmed in the integration
// spike. Matching is case-insensitive and space-insensitive so the German-box
// display value round-trips regardless of the internal key.
internal static class ScopeSampleMode
{
    public const string Fixed = "Fixed Sampletime";

    public static readonly string[] All =
        ["Task Sampletime", Fixed, "On Change"];

    public static string Known => string.Join(", ", All);

    public static bool IsKnown(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return false;
        string n = Norm(mode);
        return All.Any(m => Norm(m) == n);
    }

    // Spike 2026-05-17: the scope `Symbol.Sample Mode` property is a BOOLEAN
    // (reads True/False); the pane label "Task Sampletime" is True, anything
    // else (Fixed/On Change) is False. Writes must pass the bool, not a string.
    public static bool IsTaskSampletime(string? mode) =>
        Norm(mode ?? string.Empty) == "tasksampletime";

    // Render the raw boolean Sample Mode value back to a readable label.
    public static string? InterpretValue(string? raw)
    {
        if (raw is null) return null;
        if (bool.TryParse(raw, out var b)) return b ? "Task Sampletime" : "Fixed Sampletime";
        return raw;
    }

    public static string Norm(string s) =>
        new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
}

// Kind tag for nodes returned by twincat_list_scope_channels.
internal static class ScopeNodeKind
{
    public const string Project     = "PROJECT";
    public const string Chart       = "CHART";
    public const string AxisGroup   = "AXISGROUP";
    public const string Channel     = "CHANNEL";
    public const string DataPool    = "DATAPOOL";
    public const string Acquisition = "ACQUISITION";
}

// One node in the scope tree as surfaced by twincat_list_scope_channels.
// SymbolName is the resolved PLC symbol of the acquisition a channel is bound
// to (feature/scope-acquisition-symbols) — null/empty means "would record
// nothing" and is surfaced as a warning.
internal sealed record ScopeNodeEntry(
    string Kind,
    string Name,
    string Path,
    string? BoundAcquisition = null,
    string? DisplayLabel     = null,
    bool   AtTemplateDefault = false,
    string? SymbolName       = null);

// Summary returned by twincat_get_scope_status. UnboundAcquisitionCount =
// acquisitions that have no PLC Symbol Name set (would capture no data).
internal sealed record ScopeStatus(
    bool   ScopeProjectExists,
    string? ProjectName,
    int    ChartCount,
    int    AxisCount,
    int    ChannelCount,
    int    AcquisitionCount,
    int    UnboundAcquisitionCount = 0);

// One raw ProjectItem.Properties entry of a scope acquisition. The full set is
// always carried so the real internal keys are discoverable from live output
// (the keys are German-locale display names; we match by behaviour, never
// hardcode — feedback_browse_never_name_tree).
internal sealed record ScopeAcquisitionProperty(string Key, string Value);

// Full symbol/ADS structure of one DataPool acquisition, surfaced by
// twincat_read_scope_acquisition. Typed fields are best-effort extracted from
// AllProperties by candidate-key matching; AllProperties is authoritative.
internal sealed record ScopeAcquisitionInfo(
    string  Name,
    string  Path,
    string? SymbolName,
    string? DataType,
    string? SampleMode,
    string? SampleTimeMs,
    string? TargetPort,
    string? TargetSystem,
    bool    HasSymbol,
    IReadOnlyList<ScopeAcquisitionProperty> AllProperties);

// ---- scope-recording types -----------------------------------------------

// State string constants matching the ScopeViewState enum in
// TwinCAT.Measurement.AutomationInterface (confirmed: Config=0, Record=1, Reply=2).
internal static class ScopeViewState
{
    public const string Config = "Config";
    public const string Record = "Record";
    public const string Reply  = "Reply";
}

// Summary returned by twincat_get_scope_record_state.
internal sealed record ScopeRecordState(
    string State,       // ScopeViewState: "Config" | "Record" | "Reply"
    bool   IsRecording, // State == Record
    bool   HasData);    // State == Reply

// TE130x ShowTitleFrom (TwinCAT.Measurement.ControlBaseModels): False=0, True=1, Name=2, Specific=3.
// Live 2026-10-10: "Y-Axis.Show Title (Y)" and "Common.Show Title" accept 0..2; 3 is ignored.
internal static class ScopeShowTitle
{
    public const int False = 0;
    public const int True = 1;
    public const int Name = 2;
}

// Valid recordMode values for twincat_set_scope_record_settings.
internal static class ScopeRecordMode
{
    public static readonly string[] All = ["UserStart", "TriggerStart"];
    public static string Known => string.Join(", ", All);
    public static bool IsKnown(string? mode) =>
        All.Any(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase));
}

// Valid autoSaveMode values for twincat_set_scope_record_settings.
internal static class ScopeAutoSaveMode
{
    public static readonly string[] All = ["None", "SVDX", "Export"];
    public static string Known => string.Join(", ", All);
    public static bool IsKnown(string? mode) =>
        All.Any(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase));
}

// Valid syncMode values for twincat_set_scope_record_settings.
internal static class ScopeSyncMode
{
    public static readonly string[] All = ["Soft", "Medium", "Hard"];
    public static string Known => string.Join(", ", All);
    public static bool IsKnown(string? mode) =>
        All.Any(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase));
}

// Valid trigger actions for twincat_create_scope_trigger_group.
internal static class ScopeTriggerAction
{
    public static readonly string[] All =
        ["StopRecord", "StopDisplay", "RestartDisplay", "StartSubsave", "StopSubsave", "EventMarker"];
    public static string Known => string.Join(", ", All);
    public static bool IsKnown(string? action) =>
        All.Any(a => string.Equals(a, action?.Trim(), StringComparison.OrdinalIgnoreCase));

    // The group's "Trigger Action" property is TwinCAT.Scope2.Communications.TriggerEventAction, which
    // reaches us through COM as its integer value, and a string assignment is rejected (live
    // 2026-10-06). Ids read by reflection from TE130x TwinCAT.Scope2.Communications.dll: NONE=0,
    // START_RECORD=1, STOP_RECORD=2, STOP_DISPLAY=3, RESTART_DISPLAY=4, START_SUBSAVE=5, STOP_SUBSAVE=6.
    // EventMarker has no counterpart there and stays unmapped (null) rather than guessed.
    public static int? ToEventActionId(string action) => action.Trim().ToUpperInvariant() switch
    {
        "STOPRECORD"     => 2,
        "STOPDISPLAY"    => 3,
        "RESTARTDISPLAY" => 4,
        "STARTSUBSAVE"   => 5,
        "STOPSUBSAVE"    => 6,
        _                => null,
    };
}

// Trigger set type constants — element-type IDs for CreateChild on a trigger group.
// ChannelSet=0, DirectorySet=1 are confirmed on Infosys 498477707.
internal static class ScopeTriggerSetType
{
    public const int ChannelSet   = 0;
    public const int DirectorySet = 1;
    public static readonly string[] All = ["ChannelSet", "DirectorySet"];
    public static string Known => string.Join(", ", All);
    public static bool IsKnown(string? setType) =>
        All.Any(t => string.Equals(t, setType?.Trim(), StringComparison.OrdinalIgnoreCase));
    public static int ToId(string setType) =>
        string.Equals(setType.Trim(), "ChannelSet", StringComparison.OrdinalIgnoreCase)
            ? ChannelSet : DirectorySet;
}

// Issue #148: edge/compare were passed through unchecked, so a typo was either silently
// ignored or written verbatim while the result claimed it was set. Canonical spellings only.
internal static class ScopeTriggerCondition
{
    public static readonly string[] Edges = ["Rising", "Falling", "Both"];
    public static readonly string[] Compares = ["Greater", "Less", "Equal"];

    // A trigger set's edge lives in "Common.Release" = TwinCAT.Scope2.Communications.TriggerReleaseOptions
    // (TE130x reflection 2026-10-06: RisingEdge=0, FallingEdge=1, OnChangeEnds=2, TimeInterval=3), which
    // arrives through COM as its integer. "Both" has no counterpart there and stays unmapped (null).
    public static int? ToReleaseId(string edge) => edge.Trim().ToUpperInvariant() switch
    {
        "RISING"  => 0,
        "FALLING" => 1,
        _         => null,
    };

    /// <summary>Null input stays null (not supplied). Unknown → Error "[Error] Unknown edge ...".</summary>
    public static (string? Value, string? Error) NormalizeEdge(string? edge) => Normalize("edge", edge, Edges);
    public static (string? Value, string? Error) NormalizeCompare(string? compare) => Normalize("compare", compare, Compares);

    private static (string? Value, string? Error) Normalize(string label, string? input, string[] allowed)
    {
        if (input is null) return (null, null);
        var hit = allowed.FirstOrDefault(a => string.Equals(a, input.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit is not null
            ? (hit, null)
            : (null, $"[Error] Unknown {label} '{input}'. Expected {string.Join(", ", allowed)}.");
    }
}

// One trigger set within a group, as surfaced by twincat_list_scope_triggers.
internal sealed record ScopeTriggerSetEntry(
    string  SetType,
    string? Symbol,
    double? Threshold,
    string? Edge,
    string? Compare);

// One trigger group as surfaced by twincat_list_scope_triggers.
internal sealed record ScopeTriggerGroupEntry(
    string Name,
    string Action,
    IReadOnlyList<ScopeTriggerSetEntry> Sets);

// fix/scope-units: the scope root's Record.Record Time is stored in 100 ns ticks, not seconds
// (live 2026-10-06: raw 2 rejected, template default 10000000 = 1 s, 20000000 accepted and saved as
// <RecordTime>20000000</RecordTime>). The tool keeps its seconds parameter and converts.
internal static class ScopeTime
{
    public const long TicksPerSecond = 10_000_000L;
    public static long SecondsToTicks(double seconds) => (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
    public static double TicksToSeconds(long ticks) => (double)ticks / TicksPerSecond;
}

// Issue #153 (D-5/D-6/D-24): value conversion + read-back comparison for scope property writes.
// COM-free so it is unit-tested; invariant culture throughout.
internal static class ScopePropertyValues
{
    /// <summary>Converts <paramref name="value"/> to the CLR type the property currently holds.</summary>
    public static object CoerceTo(object? current, object value)
    {
        try
        {
            return current switch
            {
                bool when value is bool => value,
                bool => bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
                double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                int => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                uint => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                short => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                ushort => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
                Enum e => ParseEnumLoose(e.GetType(), Convert.ToString(value, CultureInfo.InvariantCulture)!),
                string => Convert.ToString(value, CultureInfo.InvariantCulture)!,
                _ => value,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return value;   // let the assignment itself report the mismatch
        }
    }

    // TE130x enums use SCREAMING_SNAKE names (e.g. TriggerEventAction.STOP_RECORD) while the tools take
    // PascalCase ("StopRecord", live 2026-10-06): match names ignoring case and non-alphanumerics.
    private static object ParseEnumLoose(Type enumType, string text)
    {
        if (Enum.TryParse(enumType, text, ignoreCase: true, out object? exact)) return exact!;
        string want = Loose(text);
        foreach (string name in Enum.GetNames(enumType))
            if (Loose(name) == want) return Enum.Parse(enumType, name);
        throw new ArgumentException($"'{text}' is not a value of {enumType.Name}.");
    }

    private static string Loose(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static bool SameValue(object back, object written)
    {
        string b = Convert.ToString(back, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        string w = Convert.ToString(written, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (string.Equals(b, w, StringComparison.OrdinalIgnoreCase)) return true;
        // An enum read back as STOP_RECORD equals the written "StopRecord".
        if (back is Enum && b.Length > 0 && Loose(b) == Loose(w)) return true;
        if (double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) &&
            double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double dw))
            return Math.Abs(db - dw) <= 1e-9 * Math.Max(1.0, Math.Abs(dw));
        return false;
    }

}
