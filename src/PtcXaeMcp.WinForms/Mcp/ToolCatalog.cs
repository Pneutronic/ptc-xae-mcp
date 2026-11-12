using System.Collections.Frozen;
using System.Reflection;
using ModelContextProtocol.Server;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>A named group of tools. Every tool belongs to exactly one; <see cref="Meta"/> is always visible.</summary>
internal enum Toolset { Meta, Project, Build, Plc, Ads, Runtime, License, Io, Hmi, Scope, Nc, Safety, Ibn }

/// <summary>What a tool may change, ordered from least to most. The access policy allows a tool up to its level.</summary>
internal enum AccessLevel { Read = 0, Engineer = 1, Runtime = 2 }

/// <summary>Where a runtime action's target system comes from, for the NetId rules of the access policy.</summary>
internal enum TargetRule
{
    /// <summary>Not a runtime action, or one that never reaches a target system.</summary>
    None,
    /// <summary>The <c>amsNetId</c> argument; empty means the local system, <c>project</c> the open solution's target.</summary>
    AmsNetIdArgument,
    /// <summary>The open solution's target (the .tsproj's TargetNetId, Local when it has none).</summary>
    Project,
    /// <summary>The local system: the AMS router and the User Mode Runtime instances on this machine.</summary>
    Local,
    /// <summary>Acts on a target an earlier call chose and the policy checked then (an armed IBN chain, a pending restore).</summary>
    CheckedEarlier,
    /// <summary>The target cannot be told from the call.</summary>
    Unknown,
}

/// <summary>One tool as the tool lists and the access policy see it.</summary>
internal sealed record ToolInfo(string Name, Toolset Toolset, AccessLevel Level, TargetRule Target, bool AlwaysAllowed, bool ReadOnly);

/// <summary>
/// Toolset, access level and target rule of every MCP tool, in one place so the classification can be reviewed
/// and tested as a whole. The level follows the tool's <c>ReadOnly</c> annotation (read) unless the tool is listed
/// as a runtime action; every other tool changes the project (engineer). Emergency stops are always allowed.
/// </summary>
internal static class ToolCatalog
{
    private static readonly Dictionary<string, Toolset> Toolsets = Build(
        (Toolset.Meta,
        [
            "ptc_test_marker", "ptc_test_hold_xae_dialog", "twincat_get_status", "twincat_get_log", "twincat_stop", "twincat_shutdown",
            "twincat_get_client_info", "twincat_doctor", "twincat_xae_dialogs", "twincat_xae_dialog_answer",
        ]),
        (Toolset.Project,
        [
            "twincat_open_project", "twincat_close_project", "twincat_save_project", "twincat_get_active_project",
            "twincat_solution_open_archive", "twincat_solution_save_archive", "twincat_set_active_variant",
            "twincat_get_variant_status", "twincat_force_close_xae", "twincat_plc_project_create",
            "twincat_plc_project_add_existing", "twincat_plc_project_export", "twincat_plc_project_import",
            "twincat_plc_project_info", "twincat_plc_instance_from_tmc", "twincat_open_object", "twincat_close_object",
        ]),
        (Toolset.Build,
        [
            "twincat_build_plc", "twincat_build_solution", "twincat_rebuild_plc", "twincat_rebuild_solution",
            "twincat_clean_solution", "twincat_get_error_list", "twincat_clear_error_list",
            "twincat_plc_check_all_objects", "twincat_plc_static_analysis", "twincat_activate_config",
        ]),
        (Toolset.Plc,
        [
            "twincat_plc_list", "twincat_plc_list_entries", "twincat_plc_read", "twincat_plc_search", "twincat_plc_write",
            "twincat_plc_create", "twincat_plc_delete", "twincat_plc_rename", "twincat_plc_move",
            "twincat_plc_edit_entries", "twincat_plc_replace_text", "twincat_plc_task_create", "twincat_plc_start",
            "twincat_plc_stop", "twincat_activate_boot_project", "twincat_get_plc_online_state", "twincat_login",
            "twincat_logout", "twincat_plcopen_export", "twincat_plcopen_import", "twincat_tcunit_results",
            "twincat_add_library_reference", "twincat_remove_library_reference", "twincat_list_library_references",
            "twincat_list_installed_libraries", "twincat_list_library_repositories", "twincat_check_libraries_installed",
            "twincat_install_library", "twincat_set_library_version", "twincat_get_library_parameters",
            "twincat_set_library_parameter", "twincat_library_signatures", "twincat_save_as_library",
            "twincat_goto_line", "twincat_show_step", "twincat_locate_step", "twincat_read_init_symbols",
            "twincat_write_init_symbol", "twincat_read_retain_config", "twincat_get_type_file_generation",
            "twincat_set_type_file_generation", "twincat_event_class_create", "twincat_event_class_read",
            "twincat_event_class_update", "twincat_plc_add_global_data_type",
            "twincat_visu_list_element_types", "twincat_visu_read",
        ]),
        (Toolset.Ads,
        [
            "twincat_read_ads_variable", "twincat_read_ads_variables_batch", "twincat_write_ads_variable",
            "twincat_list_ads_symbols", "twincat_inspect_fb_instance", "twincat_persistent_snapshot_save",
            "twincat_persistent_snapshot_compare", "twincat_persistent_snapshot_restore",
            "twincat_persistent_snapshot_restore_status", "twincat_persistent_snapshot_test_confirm",
            "twincat_read_event_logger", "twincat_confirm_event_logger", "twincat_coredump_list",
            "twincat_coredump_analyze", "twincat_coredump_read_callstack",
        ]),
        (Toolset.Runtime,
        [
            "twincat_add_route", "twincat_broadcast_search", "twincat_set_target", "twincat_set_system_mode",
            "twincat_restart_runtime", "twincat_umr_list_instances", "twincat_umr_start", "twincat_umr_set_mode",
            "twincat_umr_stop", "twincat_resolve_host", "twincat_ipc_read_inventory",
        ]),
        (Toolset.License,
        [
            "twincat_get_license_info", "twincat_generate_trial_licenses", "twincat_open_license_manager_and_request_trial",
            "twincat_submit_security_code", "twincat_dismiss_security_dialog", "twincat_list_required_licenses",
            "twincat_add_required_license", "twincat_remove_required_license",
        ]),
        (Toolset.Io,
        [
            "twincat_add_ethercat_device", "twincat_add_ethercat_box", "twincat_remove_ethercat_box",
            "twincat_configure_ethercat_slave", "twincat_set_previous_port", "twincat_ethercat_read_identities",
            "twincat_list_ethercat_box_catalog", "twincat_get_fieldbus_topology", "twincat_get_device_xml",
            "twincat_reload_device_descriptions", "twincat_set_device_disabled", "twincat_read_coe_dictionary",
            "twincat_read_coe_startup", "twincat_read_ethercat_startup", "twincat_read_soe_parameter",
            "twincat_link_variables", "twincat_unlink_variables", "twincat_get_mapping_info",
            "twincat_get_iolink_config", "twincat_import_iodd", "twincat_check_iodd_present", "twincat_apply_schema",
            "twincat_validate_schema",
        ]),
        (Toolset.Scope,
        [
            "twincat_create_scope_project", "twincat_remove_scope_project", "twincat_add_scope_channel",
            "twincat_list_scope_channels", "twincat_add_scope_trigger_set", "twincat_create_scope_trigger_group",
            "twincat_list_scope_triggers", "twincat_get_scope_status", "twincat_get_scope_record_state",
            "twincat_start_scope_record", "twincat_stop_scope_record", "twincat_save_scope_data",
            "twincat_export_scope_data", "twincat_convert_scope_file", "twincat_read_scope_file",
            "twincat_read_scope_acquisition", "twincat_set_scope_acquisition_symbol", "twincat_set_scope_axis_mode",
            "twincat_set_scope_labels", "twincat_set_scope_record_settings", "twincat_scope_probe",
        ]),
        (Toolset.Nc,
        [
            "twincat_create_nc_task", "twincat_create_axis", "twincat_configure_axis", "twincat_list_nc_tasks_axes",
            "twincat_read_axis_params", "twincat_dump_nc_tree",
        ]),
        (Toolset.Safety,
        [
            "twincat_safety_list", "twincat_safety_read_program", "twincat_safety_read_io_map", "twincat_safety_recon",
            "twincat_safety_add_alias_device", "twincat_safety_link_alias_device", "twincat_safety_probe_create_alias",
        ]),
        (Toolset.Ibn,
        [
            "twincat_ibn_arm", "twincat_ibn_disarm", "twincat_ibn_abort", "twincat_ibn_release_all", "twincat_ibn_propose",
            "twincat_ibn_get_proposal_status", "twincat_list_active_chains", "twincat_measure_signal_transition",
        ]));

    /// <summary>Actions that change a target system (configuration, PLC state, variables, routes, licenses), with where their target comes from.</summary>
    private static readonly Dictionary<string, TargetRule> RuntimeActions = new(StringComparer.Ordinal)
    {
        ["twincat_activate_config"] = TargetRule.Project,
        ["twincat_activate_boot_project"] = TargetRule.Project,
        ["twincat_restart_runtime"] = TargetRule.Project,
        ["twincat_login"] = TargetRule.Project,
        ["twincat_generate_trial_licenses"] = TargetRule.Project,
        ["twincat_open_license_manager_and_request_trial"] = TargetRule.Project,
        ["twincat_submit_security_code"] = TargetRule.Project,
        ["twincat_set_system_mode"] = TargetRule.AmsNetIdArgument,
        ["twincat_plc_start"] = TargetRule.AmsNetIdArgument,
        ["twincat_plc_stop"] = TargetRule.AmsNetIdArgument,
        ["twincat_write_ads_variable"] = TargetRule.AmsNetIdArgument,
        ["twincat_persistent_snapshot_restore"] = TargetRule.AmsNetIdArgument,
        ["twincat_confirm_event_logger"] = TargetRule.AmsNetIdArgument,
        ["twincat_add_route"] = TargetRule.AmsNetIdArgument,
        ["twincat_set_target"] = TargetRule.AmsNetIdArgument,
        ["twincat_ibn_arm"] = TargetRule.AmsNetIdArgument,
        ["twincat_umr_start"] = TargetRule.Local,
        ["twincat_umr_set_mode"] = TargetRule.Local,
        ["twincat_umr_stop"] = TargetRule.Local,
        ["twincat_hmi_server_control"] = TargetRule.Local,
        ["twincat_ibn_propose"] = TargetRule.CheckedEarlier,
        ["twincat_persistent_snapshot_test_confirm"] = TargetRule.CheckedEarlier,
        ["twincat_hmi_write_symbol"] = TargetRule.Unknown,
    };

    /// <summary>Emergency stops and the way out: never refused, whatever the access policy says.</summary>
    private static readonly HashSet<string> Always = new(StringComparer.Ordinal)
    {
        "ptc_test_marker", "twincat_stop", "twincat_shutdown", "twincat_force_close_xae",
        "twincat_ibn_abort", "twincat_ibn_disarm", "twincat_ibn_release_all",
    };

    internal const string HmiPrefix = "twincat_hmi_";

    /// <summary>The tools the table assigns explicitly (the HMI tools go by <see cref="HmiPrefix"/>).</summary>
    internal static bool IsListed(string name) => Toolsets.ContainsKey(name) || name.StartsWith(HmiPrefix, StringComparison.Ordinal);

    private static readonly Lazy<FrozenDictionary<string, ToolInfo>> AllTools = new(Load);

    /// <summary>Every registered tool (the default set and the diagnostics set) by name.</summary>
    public static FrozenDictionary<string, ToolInfo> Tools => AllTools.Value;

    public static ToolInfo? Find(string name) => Tools.GetValueOrDefault(name);

    /// <summary>Tool names the toolset table lists that no tool has (a stale entry).</summary>
    internal static IEnumerable<string> UnknownNamesInTables() =>
        Toolsets.Keys.Concat(RuntimeActions.Keys).Concat(Always).Distinct().Where(n => !Tools.ContainsKey(n));

    /// <summary>The <c>[McpServerTool]</c> methods of the tool classes with their annotation.</summary>
    internal static IEnumerable<(MethodInfo Method, McpServerToolAttribute Attribute)> ToolMethods() =>
        new[] { typeof(TwinCatMcpTools), typeof(TwinCatMcpToolsDiagnostics) }
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attribute?.Name is not null)
            .Select(x => (x.Method, x.Attribute!));

    private static FrozenDictionary<string, ToolInfo> Load()
    {
        var tools = new Dictionary<string, ToolInfo>(StringComparer.Ordinal);
        foreach (var (_, attr) in ToolMethods())
        {
            string name = attr.Name!;
            // The HMI tools share their prefix. A tool missing from the table is left in Meta so it stays
            // visible; ToolCatalogTests fails on it.
            var toolset = Toolsets.TryGetValue(name, out var listed) ? listed
                : name.StartsWith(HmiPrefix, StringComparison.Ordinal) ? Toolset.Hmi
                : Toolset.Meta;
            bool runtime = RuntimeActions.TryGetValue(name, out var target);
            var level = runtime ? AccessLevel.Runtime : attr.ReadOnly ? AccessLevel.Read : AccessLevel.Engineer;
            tools[name] = new ToolInfo(name, toolset, level, runtime ? target : TargetRule.None, Always.Contains(name), attr.ReadOnly);
        }
        return tools.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static Dictionary<string, Toolset> Build(params (Toolset Set, string[] Names)[] groups)
    {
        var map = new Dictionary<string, Toolset>(StringComparer.Ordinal);
        foreach (var (set, names) in groups)
            foreach (var name in names)
                if (!map.TryAdd(name, set))
                    throw new InvalidOperationException($"{name} is listed in toolset {map[name]} and {set}.");
        return map;
    }
}
