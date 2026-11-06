using System.Runtime.CompilerServices;

namespace PtcXaeMcp.McpTests;

/// <summary>
/// Server-free checks for the McpTests mutating-call guard (issue #157 item 7): a mutating tool is only
/// called when the server proves no solution is open, and every test goes through the guarded wrapper.
/// </summary>
public class MutatingCallGuardTests
{
    [Theory]
    [InlineData("{\"solutionOpen\":false,\"solutionPath\":null,\"solutionName\":null}", true)]
    [InlineData("[Error] No TwinCAT XAE instance is running.", true)]
    [InlineData("{\"solutionOpen\":true,\"solutionPath\":\"C:\\x\\a.sln\",\"solutionName\":\"a\"}", false)]
    [InlineData("{\"solutionPath\":null}", false)]
    [InlineData("[Error] XAE is busy (RPC_E_CALL_REJECTED)", false)]
    [InlineData("", false)]
    public void IsSafeForMutatingCall_OnlyWhenNoSolutionIsOpen(string activeProjectResponse, bool expected)
    {
        McpServerTests.IsSafeForMutatingCall(activeProjectResponse).Should().Be(expected);
    }

    [Fact]
    public void FeatureFiles_CallToolsOnlyThroughTheGuardedWrapper()
    {
        var dir = Path.GetDirectoryName(ThisFile())!;
        var offenders = Directory.GetFiles(dir, "McpServerTests.*.cs")
            .Where(f => File.ReadAllText(f).Contains("_client!.CallToolAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty(
            "feature tests must call tools via CallToolAsync(...) so mutating tools are guarded");
    }

    [Theory]
    [InlineData("twincat_activate_config")]
    [InlineData("twincat_build_solution")]
    [InlineData("twincat_close_project")]
    [InlineData("twincat_save_project")]
    [InlineData("twincat_plc_create")]
    [InlineData("twincat_plc_write")]
    [InlineData("twincat_plc_delete")]
    [InlineData("twincat_plc_rename")]
    [InlineData("twincat_plc_move")]
    [InlineData("twincat_plc_edit_entries")]
    [InlineData("twincat_plc_replace_text")]
    [InlineData("twincat_plc_static_analysis")]
    [InlineData("twincat_plc_project_create")]
    [InlineData("twincat_plc_instance_from_tmc")]
    [InlineData("twincat_solution_open_archive")]
    [InlineData("twincat_solution_save_archive")]
    [InlineData("twincat_plc_project_import")]
    [InlineData("twincat_plc_project_export")]
    [InlineData("twincat_plc_project_add_existing")]
    [InlineData("twincat_write_init_symbol")]
    [InlineData("twincat_set_type_file_generation")]
    [InlineData("twincat_remove_ethercat_box")]
    [InlineData("twincat_set_target")]
    [InlineData("twincat_force_close_xae")]
    [InlineData("twincat_hmi_server_control")]
    [InlineData("twincat_hmi_map_symbol")]
    [InlineData("twincat_hmi_unmap_symbol")]
    [InlineData("twincat_hmi_write_symbol")]
    [InlineData("twincat_hmi_add_item")]
    [InlineData("twincat_hmi_write_file")]
    [InlineData("twincat_hmi_import_file")]
    [InlineData("twincat_hmi_build")]
    [InlineData("twincat_hmi_apply_bundle")]
    [InlineData("twincat_hmi_delete_item")]
    [InlineData("twincat_hmi_attach_tmc_file")]
    [InlineData("twincat_hmi_detach_tmc_file")]
    [InlineData("twincat_hmi_enable_runtime_tmc")]
    [InlineData("twincat_hmi_disable_runtime_tmc")]
    [InlineData("twincat_hmi_add_internal_symbol")]
    [InlineData("twincat_hmi_remove_internal_symbol")]
    [InlineData("twincat_hmi_map_offline_symbol")]
    [InlineData("twincat_save_as_library")]
    [InlineData("twincat_plc_task_create")]
    [InlineData("twincat_plc_project_info")]
    public void MutatingTools_AreNotOnTheNonMutatingList(string tool)
    {
        McpServerTests.NonMutatingTools.Should().NotContain(tool);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
