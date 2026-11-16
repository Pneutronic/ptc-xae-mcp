using System.Text.Json;
using PtcXaeMcp.WinForms.TwinCat.Schema;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Coverage for the merged schema JSON validator (Hardening 9, step 1 / R1). Both
/// NativeTwinCatRunner.LoadAndValidateSchema and Form1.GetSchemaValidationResult now delegate
/// here (was E-23: two independently maintained, lenient copies existed before).
/// </summary>
public class SchemaModelValidatorTests
{
    private const string ValidSchema = """
    {
      "twincat": { "deviceName": "Dev1", "plcProjectName": "PLC" },
      "ethercat": { "root": { "name": "Dev1", "vInfo": "EK1100" } },
      "ioPoints": [
        { "varName": "x1", "terminalName": "T1", "channel": 0, "direction": "I" }
      ]
    }
    """;

    private static List<SchemaCheckIssue> Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SchemaModelValidator.Validate(doc.RootElement, json);
    }

    [Fact]
    public void Validate_WellFormedSchema_NoIssues()
    {
        Validate(ValidSchema).Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingTwincatRoot_ReportsIssue()
    {
        string json = """{ "ethercat": {"root": {"name":"x"}}, "ioPoints": [{"varName":"a","terminalName":"t","channel":0,"direction":"I"}] }""";

        var issues = Validate(json);

        issues.Should().Contain(i => i.Message.Contains("twincat"));
    }

    [Fact]
    public void Validate_EmptyDeviceName_ReportsIssue()
    {
        string json = ValidSchema.Replace("\"deviceName\": \"Dev1\"", "\"deviceName\": \"\"");

        Validate(json).Should().Contain(i => i.Message.Contains("deviceName"));
    }

    [Fact]
    public void Validate_MissingEthercatRoot_ReportsIssue()
    {
        string json = """
        {
          "twincat": { "deviceName": "d", "plcProjectName": "p" },
          "ioPoints": [{ "varName": "a", "terminalName": "t", "channel": 0, "direction": "I" }]
        }
        """;

        Validate(json).Should().Contain(i => i.Message.Contains("ethercat.root"));
    }

    [Fact]
    public void Validate_EmptyIoPoints_ReportsIssue()
    {
        const string json = """
        {
          "twincat": { "deviceName": "Dev1", "plcProjectName": "PLC" },
          "ethercat": { "root": { "name": "Dev1", "vInfo": "EK1100" } },
          "ioPoints": []
        }
        """;

        Validate(json).Should().Contain(i => i.Message.Contains("ioPoints is empty"));
    }

    [Fact]
    public void Validate_InvalidDirection_ReportsIssue()
    {
        string json = ValidSchema.Replace("\"direction\": \"I\"", "\"direction\": \"X\"");

        Validate(json).Should().Contain(i => i.Message.Contains("direction"));
    }

    [Fact]
    public void Validate_NegativeChannel_ReportsIssue()
    {
        string json = ValidSchema.Replace("\"channel\": 0", "\"channel\": -1");

        Validate(json).Should().Contain(i => i.Message.Contains("channel"));
    }

    private static string WithPoints(string points) => $$"""
        {
          "twincat": { "deviceName": "d", "plcProjectName": "p" },
          "ethercat": { "root": { "name": "d" } },
          "ioPoints": [ {{points}} ]
        }
        """;

    [Fact]
    public void Validate_DuplicateSymbol_ReportsIssueNamingBothIndexes()
    {
        string json = WithPoints("""
            { "symbol": "MAIN.fbCyl.bIn", "terminalName": "t1", "channel": 0, "direction": "I" },
            { "symbol": "MAIN.fbCyl.bIn", "terminalName": "t2", "channel": 1, "direction": "I" }
            """);

        var issues = Validate(json);

        issues.Should().ContainSingle(i => i.Message.Contains("duplicates") && i.Message.Contains("ioPoints[1].symbol")
            && i.Path == "ioPoints[2].symbol");
    }

    [Fact]
    public void Validate_DuplicateSymbolCaseInsensitive_IsStillDetected()
    {
        string json = WithPoints("""
            { "symbol": "MAIN.fbCyl.bIn", "terminalName": "t1", "channel": 0, "direction": "I" },
            { "symbol": "main.FBCYL.bin", "terminalName": "t2", "channel": 1, "direction": "I" }
            """);

        Validate(json).Should().Contain(i => i.Message.Contains("duplicates"));
    }

    [Fact]
    public void Validate_SameVarNameOnTwoPoints_IsNotAnIssue_ItIsOnlyALabel()
    {
        string json = WithPoints("""
            { "varName": "same", "symbol": "MAIN.a.bIn", "terminalName": "t1", "channel": 0, "direction": "I" },
            { "varName": "same", "symbol": "MAIN.b.bIn", "terminalName": "t2", "channel": 1, "direction": "I" }
            """);

        Validate(json).Should().BeEmpty();
    }

    [Fact]
    public void Validate_PointWithoutVarNameAndWithoutSymbol_IsAccepted()
    {
        string json = WithPoints("""
            { "terminalName": "t1", "channel": 0, "direction": "I" },
            { "symbol": null, "terminalName": "t1", "channel": 1, "direction": "I" },
            { "symbol": "MAIN.fbCyl.bOut", "terminalName": "t2", "channel": 0, "direction": "Q" }
            """);

        Validate(json).Should().BeEmpty();
    }

    [Theory]
    [InlineData("MAIN.fbCyl.LimitSwitchWorkingPosition")]
    [InlineData("MAIN.aCyl[1].bOut")]
    [InlineData("MAIN.aCyl[0].bOut")]
    [InlineData("MAIN.aCyl[-1].bOut")]
    [InlineData("MAIN.aGrid[2,3].bOut")]
    [InlineData("MAIN.aGrid[2][3].bOut")]
    [InlineData("MAIN.a[2].b[1].c")]
    [InlineData("PRG_X.fbOne.aIn[2]")]
    public void Validate_ValidSymbol_NoIssues(string symbol)
    {
        string json = WithPoints($$"""{ "symbol": "{{symbol}}", "terminalName": "t", "channel": 0, "direction": "I" }""");

        Validate(json).Should().BeEmpty();
    }

    [Theory]
    [InlineData("MAIN.a^MAIN.b", "'^'")]
    [InlineData("MAIN.aCyl[ 1 ].bIn", "without blanks")]
    [InlineData("MAIN.aGrid[2, 1].bIn", "[2,1] without blanks")]
    [InlineData("MAIN.aCyl[01].bIn", "leading zero")]
    [InlineData("MAIN.aCyl[].bIn", "empty index")]
    [InlineData("MAIN.aCyl[n].bIn", "integer literal")]
    [InlineData("MAIN.aCyl[1..2].bIn", "range")]
    [InlineData(".MAIN.bIn", "start with '.'")]
    [InlineData("", "empty")]
    public void Validate_InvalidSymbol_IsAnErrorNamingTheRule(string symbol, string hint)
    {
        string json = WithPoints($$"""{ "symbol": "{{symbol}}", "terminalName": "t", "channel": 0, "direction": "I" }""");

        var issues = Validate(json);

        issues.Should().ContainSingle(i => i.Path == "ioPoints[1].symbol" && i.Message.Contains(hint));
    }

    [Fact]
    public void Validate_SymbolNotAString_IsAnError()
    {
        string json = WithPoints("""{ "symbol": 5, "terminalName": "t", "channel": 0, "direction": "I" }""");

        Validate(json).Should().Contain(i => i.Path == "ioPoints[1].symbol" && i.Message.Contains("must be a string"));
    }

    [Fact]
    public void Validate_SymbolIssue_CarriesTheLineOfThatPointsSymbol()
    {
        // The first point has no symbol: the line must still be the second point's.
        string json = WithPoints("""
            { "terminalName": "t1", "channel": 0, "direction": "I" },
            { "symbol": "MAIN.a^b", "terminalName": "t2", "channel": 1, "direction": "I" }
            """);

        var issue = Validate(json).Should().ContainSingle(i => i.Path == "ioPoints[2].symbol").Subject;

        json.Split('\n')[issue.Line!.Value - 1].Should().Contain("MAIN.a^b");
    }

    // ---- the removed central GVL ------------------------------------------------------------

    [Fact]
    public void Validate_LegacyGvlName_IsAnErrorWithTheMigration_NotIgnored()
    {
        string json = ValidSchema.Replace("\"plcProjectName\": \"PLC\"", "\"plcProjectName\": \"PLC\", \"gvlName\": \"IO\"");

        var issues = Validate(json);

        issues.Should().ContainSingle().Which.Should().Match<SchemaCheckIssue>(i =>
            i.Path == "twincat.gvlName" && i.Message.Contains("removed in 4.0.0") && i.Message.Contains("ioPoints[].symbol")
            && i.Line != null);
    }

    [Fact]
    public void Validate_LegacyOverwriteExistingGvl_IsAnErrorWithTheMigration_NotIgnored()
    {
        string json = ValidSchema.Replace("\"plcProjectName\": \"PLC\"", "\"plcProjectName\": \"PLC\", \"overwriteExistingGvl\": true");

        var issues = Validate(json);

        issues.Should().ContainSingle().Which.Should().Match<SchemaCheckIssue>(i =>
            i.Path == "twincat.overwriteExistingGvl" && i.Message.Contains("removed in 4.0.0") && i.Message.Contains("ioPoints[].symbol"));
    }

    [Fact]
    public void Validate_BothLegacyFields_AreBothReported()
    {
        string json = ValidSchema.Replace("\"plcProjectName\": \"PLC\"",
            "\"plcProjectName\": \"PLC\", \"gvlName\": \"IO\", \"overwriteExistingGvl\": false");

        Validate(json).Select(i => i.Path).Should().BeEquivalentTo("twincat.gvlName", "twincat.overwriteExistingGvl");
    }

    [Fact]
    public void Validate_NoGvlNameIsRequiredAnyMore()
    {
        Validate(ValidSchema).Should().NotContain(i => i.Message.Contains("gvlName"));
    }

    [Fact]
    public void Validate_MisspelledTopLevelProperty_ReportedAsUnmapped()
    {
        // "plcProjectNam" (typo) has no matching member on TwinCatConfig, so
        // UnmappedMemberHandling.Disallow must catch it even though it's not one of the
        // hand-checked required fields.
        string json = ValidSchema.Replace("\"plcProjectName\": \"PLC\"", "\"plcProjectNam\": \"PLC\"");

        Validate(json).Should().Contain(i => i.Message.Contains("Unknown or misspelled"));
    }

    [Fact]
    public void Validate_MetaTopLevelField_IsToleratedAsExtensionData()
    {
        string json = """
        {
          "twincat": { "deviceName": "d", "plcProjectName": "p" },
          "ethercat": { "root": { "name": "d" } },
          "ioPoints": [{ "varName": "a", "terminalName": "t", "channel": 0, "direction": "I" }],
          "meta": { "sourcePdf": "spec.pdf", "generatedAt": "2026-01-01" }
        }
        """;

        Validate(json).Should().BeEmpty();
    }

    [Fact]
    public void Validate_MisspelledIoPointProperty_ReportedAsUnmapped()
    {
        string json = ValidSchema.Replace("\"terminalName\": \"T1\"", "\"terminalNam\": \"T1\"");

        // Structural check still flags terminalName as missing; the unmapped-member check
        // additionally flags the typo itself.
        Validate(json).Should().Contain(i => i.Message.Contains("Unknown or misspelled") || i.Message.Contains("terminalName"));
    }

    [Fact]
    public void Validate_MisspelledProperty_IssueCarriesItsJsonPath()
    {
        string json = ValidSchema.Replace("\"plcProjectName\": \"PLC\"", "\"plcProjectNam\": \"PLC\"");

        Validate(json).Should().Contain(i => i.Message.Contains("Unknown or misspelled") && i.Path == "twincat.plcProjectNam");
    }

    [Fact]
    public void Validate_WrongValueType_IsNotCalledAMisspelling()
    {
        string json = ValidSchema.Replace("\"channel\": 0", "\"channel\": \"one\"");

        var issues = Validate(json);
        issues.Should().Contain(i => i.Message.Contains("does not match the schema model"));
        issues.Should().NotContain(i => i.Message.Contains("Unknown or misspelled"));
    }
}
