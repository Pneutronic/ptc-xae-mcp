using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class LinkFailureDiagnosisTests
{
    private const string IoInput1 = "TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 2 (EL1008)^Channel 1^Input";
    private const string IoInput2 = "TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 2 (EL1008)^Channel 2^Input";
    private const string IoAnalog = "TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 3 (EL3174)^AI Standard Channel 1^Value";
    private const string PlcVar = "TIPC^FixturePlc^FixturePlc Instance^PlcTask Inputs^GVL_Fixture.bInputSignal";

    private static LinkEndpoint Var(string path, string type, int bits, int inOut) =>
        new(path, true, LinkFailureDiagnosis.VariableItemType, new VariableInfo(type, bits, inOut));

    // ── VariableInfo.TryParse (XML shape read live from a TwinCAT 3.1.4026 tree) ──────────

    [Fact]
    public void TryParse_ReadsTypeSizeAndDirection()
    {
        const string xml = "<?xml version=\"1.0\"?><TreeItem><ItemName>Value</ItemName><ItemType>7</ItemType>" +
            "<VarDef><VarType GUID=\"{18071995-0000-0000-0000-000000000006}\">INT</VarType><VarBitSize>16</VarBitSize>" +
            "<VarBitAddr>0</VarBitAddr><VarInOut>0</VarInOut><VarFlags>#x0</VarFlags></VarDef></TreeItem>";

        var info = VariableInfo.TryParse(xml);

        Assert.Equal(new VariableInfo("INT", 16, 0), info);
        Assert.Equal("input", info!.Direction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<TreeItem><ItemName>Term 1</ItemName></TreeItem>")]
    [InlineData("<TreeItem><VarDef><VarType>INT</VarType></VarDef></TreeItem>")]
    public void TryParse_NoUsableVarDef_ReturnsNull(string? xml) =>
        Assert.Null(VariableInfo.TryParse(xml));

    // ── MISMATCHINGITEMS ───────────────────────────────────────────────────────────────

    [Fact]
    public void Mismatch_DifferentTypes_NamesBothSidesAndPdoHint()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.MismatchingItems, "do not match",
            Var(IoAnalog, "REAL32", 32, 0), Var(PlcVar, "INT", 16, 0));

        Assert.StartsWith("LinkVariables refused the link:", text);
        Assert.Contains($"'{IoAnalog}' is REAL32 (32 bit, input)", text);
        Assert.Contains($"'{PlcVar}' is INT (16 bit, input)", text);
        Assert.Contains("The terminal maps this channel as REAL32", text);
        Assert.Contains("PDO", text);
        Assert.DoesNotContain("inputs link to outputs only", text);
    }

    [Fact]
    public void Mismatch_TwoInputsOnTheSameSide_ExplainsDirection()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.MismatchingItems, "do not match",
            Var(IoInput1, "BIT", 1, 0), Var(IoInput2, "BIT", 1, 0));

        Assert.Contains("is BIT (1 bit, input)", text);
        Assert.Contains("Both are inputs: inputs link to outputs only.", text);
        Assert.DoesNotContain("PDO", text);
    }

    [Fact]
    public void Mismatch_SameDirectionAcrossIoAndTask_MakesNoDirectionClaim()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.MismatchingItems, "do not match",
            Var(IoInput1, "BIT", 1, 0), Var(PlcVar, "BOOL", 8, 0));

        Assert.DoesNotContain("inputs link to outputs only", text);
        Assert.Contains("is BIT (1 bit, input)", text);
        Assert.Contains("is BOOL (8 bit, input)", text);
    }

    [Fact]
    public void Mismatch_TypeNotReadable_KeepsTodaysMessageWithCodeName()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.MismatchingItems, "'a' and 'b' do not match",
            new LinkEndpoint(IoInput1, true, 7, null), Var(PlcVar, "BOOL", 8, 0));

        Assert.Equal("LinkVariables failed: 'a' and 'b' do not match (TSM_E_MISMATCHINGITEMS 0x98510004)", text);
    }

    // ── ITEMNOTFOUND / INVALIDITEMTYPE ─────────────────────────────────────────────────

    [Fact]
    public void ItemNotFound_NamesOnlyTheUnresolvedPath()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.ItemNotFound, "not found",
            Var(PlcVar, "BOOL", 8, 0), LinkEndpoint.Missing("TIID^Nope^Nope"));

        Assert.Contains("'TIID^Nope^Nope' does not resolve", text);
        Assert.DoesNotContain($"'{PlcVar}' does not resolve", text);
        Assert.Contains("TSM_E_ITEMNOTFOUND", text);
    }

    [Fact]
    public void ItemNotFound_BothMissing_NamesBoth()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.ItemNotFound, "not found",
            LinkEndpoint.Missing("TIID^A"), LinkEndpoint.Missing("TIPC^B"));

        Assert.Contains("'TIID^A' does not resolve", text);
        Assert.Contains("'TIPC^B' does not resolve", text);
    }

    [Fact]
    public void InvalidItemType_NamesThePathThatIsNoVariable()
    {
        const string terminal = "TIID^Device 1 (EtherCAT)^Term 1 (EK1100)";
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.InvalidItemType, "not a variable",
            new LinkEndpoint(terminal, true, 5, null), Var(PlcVar, "BOOL", 8, 0));

        Assert.Contains($"'{terminal}' is not a variable (item type 5", text);
        Assert.DoesNotContain($"'{PlcVar}' is not a variable", text);
        Assert.Contains("TSM_E_INVALIDITEMTYPE", text);
    }

    // ── no usable probe result: the code name is added, nothing is guessed ───────────

    [Fact]
    public void ItemNotFound_NothingMissingInProbe_FallsBackToCodeNamedMessage()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.ItemNotFound, "not found",
            Var(IoInput1, "BIT", 1, 0), Var(PlcVar, "BOOL", 8, 0));

        Assert.Equal("LinkVariables failed: not found (TSM_E_ITEMNOTFOUND 0x98510001)", text);
    }

    [Fact]
    public void InvalidArg_FallsBackToCodeNamedMessage()
    {
        string text = LinkFailureDiagnosis.Describe(LinkFailureDiagnosis.InvalidArg, "bad argument",
            LinkEndpoint.Missing("x"), LinkEndpoint.Missing("y"));

        Assert.Equal("LinkVariables failed: bad argument (E_INVALIDARG 0x80070057)", text);
    }

    [Fact]
    public void UnknownHResult_FallsBackWithHexOnly() =>
        Assert.Equal("LinkVariables failed: boom (0x80004005)",
            LinkFailureDiagnosis.Fallback(unchecked((int)0x80004005), "boom"));

    [Fact]
    public void HResultOf_UnwrapsTargetInvocationException()
    {
        var inner = new System.Runtime.InteropServices.COMException("x", LinkFailureDiagnosis.MismatchingItems);
        var wrapped = new System.Reflection.TargetInvocationException(inner);

        Assert.Equal(LinkFailureDiagnosis.MismatchingItems, LinkFailureDiagnosis.HResultOf(wrapped));
        Assert.Equal(LinkFailureDiagnosis.MismatchingItems, LinkFailureDiagnosis.HResultOf(inner));
    }
}
