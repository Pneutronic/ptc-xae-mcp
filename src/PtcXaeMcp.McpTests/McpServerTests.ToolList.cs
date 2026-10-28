using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Tool registration: the full tool surface is listed.</summary>
public partial class McpServerTests
{
    // ── Test 1 ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T1_ListTools_ReturnsAllRegisteredTools()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("twincat_get_status", names);
        Assert.Contains("twincat_get_log", names);
        Assert.Contains("twincat_stop", names);
        Assert.Contains("twincat_validate_schema", names);
        Assert.Contains("twincat_apply_schema", names);
        Assert.Contains("twincat_open_project", names);
        Assert.Contains("twincat_save_project", names);
        Assert.Contains("twincat_close_project", names);
        Assert.Contains("twincat_shutdown", names);
        Assert.Contains("twincat_list_library_references", names);
        Assert.Contains("twincat_list_installed_libraries", names);
        Assert.Contains("twincat_check_libraries_installed", names);
        Assert.Contains("twincat_add_library_reference", names);
        Assert.Contains("twincat_remove_library_reference", names);
        Assert.Contains("twincat_set_library_version", names);
        Assert.Contains("twincat_get_fieldbus_topology", names);
        Assert.Contains("twincat_get_device_xml", names);
        Assert.Contains("twincat_configure_ethercat_slave", names);
        Assert.Contains("twincat_add_ethercat_device", names);
        Assert.Contains("twincat_add_ethercat_box", names);
        Assert.Contains("twincat_list_ethercat_box_catalog", names);
        Assert.Contains("twincat_reload_device_descriptions", names);
        Assert.Contains("twincat_hmi_list_internal_symbols", names);
        Assert.Contains("twincat_hmi_add_internal_symbol", names);
        Assert.Contains("twincat_hmi_remove_internal_symbol", names);
        Assert.Contains("twincat_hmi_map_offline_symbol", names);
        Assert.Contains("twincat_remove_ethercat_box", names);
        Assert.Contains("twincat_set_previous_port", names);
        Assert.Contains("twincat_get_iolink_config", names);
        Assert.Contains("twincat_import_iodd", names);
        Assert.Contains("twincat_check_iodd_present", names);
        Assert.Contains("twincat_list_pous", names);
        Assert.Contains("twincat_read_pou", names);
        Assert.Contains("twincat_read_gvl", names);
        Assert.Contains("twincat_write_pou_body", names);
        Assert.Contains("twincat_create_pou", names);
        Assert.Contains("twincat_create_dut", names);
        Assert.Contains("twincat_create_gvl", names);
        Assert.Contains("twincat_create_interface", names);
        Assert.Contains("twincat_list_pou_members", names);
        Assert.Contains("twincat_add_pou_member", names);
        Assert.Contains("twincat_read_pou_member", names);
        Assert.Contains("twincat_write_pou_member", names);
        Assert.Contains("twincat_delete_plc_object", names);
        Assert.Contains("twincat_rename_plc_object", names);
        Assert.Contains("twincat_plc_list", names);
        Assert.Contains("twincat_plc_create", names);
        Assert.Contains("twincat_plc_read", names);
        Assert.Contains("twincat_plc_write", names);
        Assert.Contains("twincat_plc_delete", names);
        Assert.Contains("twincat_plc_rename", names);
        Assert.Contains("twincat_plc_list_entries", names);
        Assert.Contains("twincat_plc_edit_entries", names);
        Assert.Contains("twincat_plc_project_create", names);
        Assert.Contains("twincat_plc_instance_from_tmc", names);
        Assert.Contains("twincat_solution_open_archive", names);
        Assert.Contains("twincat_solution_save_archive", names);
        Assert.Contains("twincat_plc_project_import", names);
        Assert.Contains("twincat_plc_project_export", names);
        Assert.Contains("twincat_plc_project_add_existing", names);
        Assert.Contains("twincat_read_init_symbols", names);
        Assert.Contains("twincat_write_init_symbol", names);
        Assert.Contains("twincat_read_retain_config", names);
        Assert.Contains("twincat_get_type_file_generation", names);
        Assert.Contains("twincat_set_type_file_generation", names);
        Assert.Contains("twincat_plcopen_export", names);
        Assert.Contains("twincat_plcopen_import", names);
        Assert.Contains("twincat_build_plc", names);
        Assert.Contains("twincat_build_solution", names);
        Assert.Contains("twincat_activate_config", names);
        Assert.Contains("twincat_clear_error_list", names);
        // build-recovery / plc-login
        Assert.Contains("twincat_login", names);
        Assert.Contains("twincat_logout", names);
        Assert.Contains("twincat_clean_solution", names);
        Assert.Contains("twincat_rebuild_solution", names);
        Assert.Contains("twincat_rebuild_plc", names);

        // scope-measurement
        Assert.Contains("twincat_create_scope_project", names);
        Assert.Contains("twincat_remove_scope_project", names);
        Assert.Contains("twincat_add_scope_channel", names);
        Assert.Contains("twincat_set_scope_axis_mode", names);
        Assert.Contains("twincat_set_scope_labels", names);
        Assert.Contains("twincat_list_scope_channels", names);
        Assert.Contains("twincat_get_scope_status", names);
        Assert.Contains("twincat_export_scope_data", names);
        Assert.Contains("twincat_read_scope_acquisition", names);
        Assert.Contains("twincat_set_scope_acquisition_symbol", names);

        // scope-recording
        Assert.Contains("twincat_start_scope_record", names);
        Assert.Contains("twincat_stop_scope_record", names);
        Assert.Contains("twincat_get_scope_record_state", names);
        Assert.Contains("twincat_save_scope_data", names);
        Assert.Contains("twincat_set_scope_record_settings", names);
        Assert.Contains("twincat_create_scope_trigger_group", names);
        Assert.Contains("twincat_add_scope_trigger_set", names);
        Assert.Contains("twincat_list_scope_triggers", names);

        // runtime-activation
        Assert.Contains("twincat_get_license_info", names);
        Assert.Contains("twincat_ipc_read_inventory", names);
        Assert.Contains("twincat_ethercat_read_identities", names);
        Assert.Contains("twincat_list_library_repositories", names);
        Assert.Contains("twincat_list_required_licenses", names);
        Assert.Contains("twincat_add_required_license", names);
        Assert.Contains("twincat_remove_required_license", names);
        Assert.Contains("twincat_dismiss_security_dialog", names);
        Assert.Contains("twincat_open_license_manager_and_request_trial", names);
        Assert.Contains("twincat_generate_trial_licenses", names);
        Assert.Contains("twincat_submit_security_code", names);

        // runtime-control
        Assert.Contains("twincat_broadcast_search", names);
        Assert.Contains("twincat_add_route", names);
        Assert.Contains("twincat_set_target", names);
        Assert.Contains("twincat_restart_runtime", names);
        Assert.Contains("twincat_set_system_mode", names);
        Assert.Contains("twincat_umr_start", names);
        Assert.Contains("twincat_umr_set_mode", names);
        Assert.Contains("twincat_umr_stop", names);

        // multi-xae-robustness (issue #57)
        Assert.Contains("twincat_force_close_xae", names);

        // ads-variable-rw
        Assert.Contains("twincat_read_ads_variable", names);
        Assert.Contains("twincat_write_ads_variable", names);

        // coe-registers
        Assert.Contains("twincat_read_coe_dictionary", names);
        Assert.Contains("twincat_read_coe_startup", names);
        Assert.Contains("twincat_read_ethercat_startup", names);

        // safety
        Assert.Contains("twincat_safety_recon", names);
        Assert.Contains("twincat_safety_list", names);
        Assert.Contains("twincat_safety_read_io_map", names);
        Assert.Contains("twincat_safety_read_program", names);
        Assert.Contains("twincat_safety_add_alias_device", names);
        Assert.Contains("twincat_safety_link_alias_device", names);
        Assert.Contains("twincat_safety_probe_create_alias", names);
    }
}
