using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Generic PLC element tools (twincat_plc_*): argument contract and the kind gate, over the wire.</summary>
public partial class McpServerTests
{
    [SkippableFact]
    public async Task PlcObjects_List_UnknownKind_ReturnsErrorListingTheKinds()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_list",
            new Dictionary<string, object?> { ["kind"] = "STRUCT" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("Unknown kind 'STRUCT'", text);
        Assert.Contains("MEMBER", text);
    }

    [SkippableFact]
    public async Task PlcObjects_List_MemberWithoutParent_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_list",
            new Dictionary<string, object?> { ["kind"] = "MEMBER" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("parentPath", text);
    }

    [SkippableFact]
    public async Task PlcObjects_Read_MissingKind_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_read",
            new Dictionary<string, object?> { ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("kind is required", text);
    }

    [SkippableFact]
    public async Task PlcObjects_Read_EmptyPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_read",
            new Dictionary<string, object?> { ["path"] = "", ["kind"] = "POU" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("path", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task PlcObjects_Read_NonTextKind_ReturnsNotSupportedWithReason()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_read",
            new Dictionary<string, object?> { ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "TEXTLIST" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("not supported", text);
    }

    [SkippableFact]
    public async Task PlcObjects_Write_UnknownKind_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_write",
            new Dictionary<string, object?> { ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "TABLE", ["declaration"] = "x" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("Unknown kind", text);
    }

    // Regression: subtype was a required schema parameter, so creating a GVL/FOLDER/INTERFACE without one failed at
    // the protocol level ("An error occurred invoking ..."), not as a tool answer.
    [SkippableTheory]
    [InlineData("GVL")]
    [InlineData("FOLDER")]
    [InlineData("INTERFACE")]
    [InlineData("DUT")]
    public async Task PlcObjects_Create_WithoutSubtype_IsAToolAnswer_NotAProtocolError(string kind)
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_create",
            new Dictionary<string, object?> { ["kind"] = kind, ["parentPath"] = "TIPC^Nope", ["name"] = "X1" });

        var text = GetText(result);
        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error]", text);
    }

    [SkippableFact]
    public async Task PlcObjects_Create_PouWithoutSubtype_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_create",
            new Dictionary<string, object?> { ["kind"] = "POU", ["parentPath"] = "TIPC^Nope", ["name"] = "FB_X" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("subtype is required", text);
    }

    [SkippableFact]
    public async Task PlcObjects_Delete_RecursiveOnNonFolder_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_delete",
            new Dictionary<string, object?> { ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "DUT", ["recursive"] = true });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("recursive does not apply", text);
    }

    [SkippableFact]
    public async Task PlcObjects_ListEntries_MissingKind_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_list_entries",
            new Dictionary<string, object?> { ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("kind is required", text);
    }

    [SkippableFact]
    public async Task PlcObjects_EditEntries_UnknownOp_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_edit_entries",
            new Dictionary<string, object?> { ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "DUT", ["op"] = "insert", ["name"] = "X" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("Unknown op 'insert'", text);
    }

    [SkippableFact]
    public async Task PlcSearch_BadParameters_ReturnErrors()
    {
        SkipIfNotReachable();

        var empty = GetText(await CallToolAsync("twincat_plc_search", new Dictionary<string, object?> { ["pattern"] = " " }));
        Assert.Equal("[Error] pattern is required.", empty);

        var scope = GetText(await CallToolAsync("twincat_plc_search",
            new Dictionary<string, object?> { ["pattern"] = "x", ["scope"] = "body" }));
        Assert.StartsWith("[Error] Unknown scope 'body'", scope);

        var regex = GetText(await CallToolAsync("twincat_plc_search",
            new Dictionary<string, object?> { ["pattern"] = "(", ["regex"] = true }));
        Assert.StartsWith("[Error] pattern is not a valid regular expression", regex);
    }

    [SkippableFact]
    public async Task PlcReplaceText_BadSection_ReturnsErrorBeforeTouchingTheProject()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_replace_text",
            new Dictionary<string, object?>
            {
                ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "POU", ["section"] = "body",
                ["oldText"] = "a", ["newText"] = "b",
            });

        Assert.Equal("[Error] section must be declaration or implementation.", GetText(result));
    }

    [SkippableFact]
    public async Task PlcObjects_Rename_EmptyNewName_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_rename",
            new Dictionary<string, object?> { ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "POU", ["newName"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("newName", text);
    }

    // The XAE's "Automatisches Umbenennen über Refactoring" prompt is answered by the rename tool; the answer
    // is an explicit, optional boolean that defaults to "Nein" (false).
    [SkippableTheory]
    [InlineData("twincat_plc_rename")]
    public async Task RenameTools_HaveOptionalBooleanUpdateReferences(string tool)
    {
        SkipIfNotReachable();
        var schema = (await _client!.ListToolsAsync()).Single(t => t.Name == tool).JsonSchema;

        var prop = schema.GetProperty("properties").GetProperty("updateReferences");
        Assert.Equal("boolean", prop.GetProperty("type").GetString());
        Assert.False(prop.GetProperty("default").GetBoolean());
        Assert.Contains("Nein", prop.GetProperty("description").GetString());
        if (schema.TryGetProperty("required", out var required))
            Assert.DoesNotContain(required.EnumerateArray(), r => r.GetString() == "updateReferences");
    }

    [SkippableFact]
    public async Task PlcRename_AcceptsUpdateReferences_ArgumentChecksStillApply()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_rename",
            new Dictionary<string, object?>
            {
                ["path"] = "TIPC^Nope^DoesNotExist", ["kind"] = "POU", ["newName"] = "", ["updateReferences"] = true,
            });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("newName", text);
    }
}
