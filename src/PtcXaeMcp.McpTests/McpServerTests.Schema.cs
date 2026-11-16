using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Schema validation and brownfield apply parameters.</summary>
public partial class McpServerTests
{
    // ── Test 4 ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T4_ValidateSchema_ValidFile_ReturnsValid()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(ValidSchemaPath), $"Fixture missing: {ValidSchemaPath}");

        var result = await CallToolAsync("twincat_validate_schema",
            new Dictionary<string, object?> { ["schemaPath"] = ValidSchemaPath });

        var text = GetText(result);
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
        Assert.StartsWith("Valid", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 5 ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T5_ValidateSchema_MissingFile_ReturnsInvalid()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_validate_schema",
            new Dictionary<string, object?> { ["schemaPath"] = NonExistentSchemaPath });

        var text = GetText(result);
        bool isInvalidResponse = text.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase)
            || result.IsError == true;
        Assert.True(isInvalidResponse, $"Expected Invalid/error for missing file, got: {text}");
    }

    // ── Test 6 ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T6_ValidateSchema_MalformedJson_ReturnsInvalid()
    {
        SkipIfNotReachable();

        var badPath = Path.Combine(FixturesDir, "malformed.json");
        await File.WriteAllTextAsync(badPath, "{ this is not valid json }");
        try
        {
            var result = await CallToolAsync("twincat_validate_schema",
                new Dictionary<string, object?> { ["schemaPath"] = badPath });

            var text = GetText(result);
            bool isInvalidResponse = text.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase)
                || result.IsError == true;
            Assert.True(isInvalidResponse, $"Expected Invalid for malformed JSON, got: {text}");
        }
        finally
        {
            File.Delete(badPath);
        }
    }

    // ── member symbols instead of a central GVL ────────────────────────────────

    private static readonly string LegacyGvlSchemaPath = Path.Combine(FixturesDir, "legacy_gvl_schema.json");
    private static readonly string BadSymbolSchemaPath = Path.Combine(FixturesDir, "bad_symbol_schema.json");

    [SkippableFact]
    public async Task Symbol_ValidateSchema_LegacyGvlFields_AreErrorsWithTheMigration()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(LegacyGvlSchemaPath), $"Fixture missing: {LegacyGvlSchemaPath}");

        var result = await CallToolAsync("twincat_validate_schema",
            new Dictionary<string, object?> { ["schemaPath"] = LegacyGvlSchemaPath });

        var text = GetText(result);
        Assert.False(result.IsError == true, $"Tool returned protocol error: {text}");
        Assert.StartsWith("Invalid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("twincat.gvlName", text);
        Assert.Contains("twincat.overwriteExistingGvl", text);
        Assert.Contains("removed in 4.0.0", text);
        Assert.Contains("ioPoints[].symbol", text);
    }

    [SkippableFact]
    public async Task Symbol_ValidateSchema_BlankInsideAnIndex_IsInvalid_NamingTheRule()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(BadSymbolSchemaPath), $"Fixture missing: {BadSymbolSchemaPath}");

        var result = await CallToolAsync("twincat_validate_schema",
            new Dictionary<string, object?> { ["schemaPath"] = BadSymbolSchemaPath });

        var text = GetText(result);
        Assert.StartsWith("Invalid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ioPoints[1].symbol", text);
        Assert.Contains("[2,1] without blanks", text);
    }

    [SkippableFact]
    public async Task Symbol_ApplySchema_LegacyGvlSchema_IsRefusedBeforeAnythingIsTouched()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(LegacyGvlSchemaPath), $"Fixture missing: {LegacyGvlSchemaPath}");

        var result = await CallToolAsync("twincat_apply_schema",
            new Dictionary<string, object?> { ["schemaPath"] = LegacyGvlSchemaPath });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("twincat.gvlName", text);
        Assert.Contains("removed in 4.0.0", text);
    }

    [SkippableFact]
    public async Task Symbol_ApplySchema_RefusesTheRemovedOverwriteExistingGvlArgument()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_apply_schema",
            new Dictionary<string, object?> { ["schemaPath"] = ValidSchemaPath, ["overwriteExistingGvl"] = true });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("overwriteExistingGvl", text);
    }

    // ── brownfield-guard ───────────────────────────────────────────────────────
    // apply_schema / validate_schema must expose the fail-safe params, and a malformed
    // exact target path must come back as a clean tool error, never a silent success.

    [SkippableFact]
    public async Task Bf_ApplySchema_AcceptsBrownfieldParams()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(ValidSchemaPath), $"Fixture missing: {ValidSchemaPath}");

        // A well-formed target path must be accepted by the tool surface (not rejected as
        // an unknown parameter). With no XAE/empty master it will fail downstream, but the
        // param plumbing itself must be intact.
        var result = await CallToolAsync("twincat_apply_schema",
            new Dictionary<string, object?>
            {
                ["schemaPath"] = ValidSchemaPath,
                ["targetMasterPath"] = "TIID^Device 1 (EtherCAT)",
                ["failClosed"] = true,
                ["autoBuild"] = false,
            });

        var text = GetText(result);
        Assert.False(string.IsNullOrWhiteSpace(text), "apply_schema swallowed the response");
        // Either it ran (log text) or it failed downstream ([Error]) — never a
        // parameter-rejection protocol error.
        Assert.False(result.IsError == true, $"params were rejected: {text}");
    }

    [SkippableFact]
    public async Task Bf_ApplySchema_MalformedTargetMasterPath_ReturnsCleanError()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(ValidSchemaPath), $"Fixture missing: {ValidSchemaPath}");

        var result = await CallToolAsync("twincat_apply_schema",
            new Dictionary<string, object?>
            {
                ["schemaPath"] = ValidSchemaPath,
                ["targetMasterPath"] = "Device 1 (EtherCAT)", // not anchored under TIID
            });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("targetMasterPath", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Bf_ValidateSchema_MalformedTargetMasterPath_ReturnsInvalid()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(ValidSchemaPath), $"Fixture missing: {ValidSchemaPath}");

        var result = await CallToolAsync("twincat_validate_schema",
            new Dictionary<string, object?>
            {
                ["schemaPath"] = ValidSchemaPath,
                ["targetMasterPath"] = "TIID", // bare root, addresses no device
            });

        var text = GetText(result);
        Assert.False(result.IsError == true, $"Tool returned protocol error: {text}");
        Assert.StartsWith("Invalid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("targetMasterPath", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Bf_ValidateSchema_NoTargetMasterPath_StillValidatesFileOnly()
    {
        SkipIfNotReachable();
        Assert.True(File.Exists(ValidSchemaPath), $"Fixture missing: {ValidSchemaPath}");

        // Omitting the brownfield param must not change the greenfield validate result.
        var result = await CallToolAsync("twincat_validate_schema",
            new Dictionary<string, object?> { ["schemaPath"] = ValidSchemaPath });

        var text = GetText(result);
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
        Assert.StartsWith("Valid", text, StringComparison.OrdinalIgnoreCase);
    }
}
