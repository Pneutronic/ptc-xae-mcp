using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// hmi-server write path: "ask every time; it should be possible". The invariant every
/// test guards: nothing reaches MapHmiSymbol/UnmapHmiSymbol unless the confirmer said
/// Confirmed/AutoConfirmed, and success is only reported after a read-back.
/// </summary>
public class HmiWriteTests
{
    private const string PlantHmi = "PlantHmi";
    private const string Dir = @"C:\Repos\PlantHmi\PlantHmi_HMI\PlantHmi";

    private static FakeTwinCatSession Session() => new()
    {
        IsConnected = true,
        SolutionOpen = true,
        HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(PlantHmi, Dir, true, false)], null),
    };

    private static bool Wrote(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c.StartsWith("MapHmiSymbol", StringComparison.Ordinal)
                           || c.StartsWith("UnmapHmiSymbol", StringComparison.Ordinal));

    private static (string Summary, string Json) Map(FakeTwinCatSession fake, IHmiWriteConfirmer? confirmer,
        string? plcPath = "MAIN.fbHmi.bStart", string? mapName = null, string? internalName = null,
        string? domain = null, string? runtime = null, string? sub = null, int timeout = 120) =>
        new HmiCoordinator(fake, confirmer).MapSymbol(null, plcPath, mapName, internalName, domain, runtime, sub, timeout);

    // ── HmiSymbolNames (convention of 203 real mappings) ─────────────────────

    [Theory]
    [InlineData("MAIN.fbHmi.bStart", null, "ADS.PLC1.MAIN.fbHmi.bStart", "PLC1::MAIN::fbHmi::bStart")]
    [InlineData("GVL_HMI.HmiOpened", "PLC1", "ADS.PLC1.GVL_HMI.HmiOpened", "PLC1::GVL_HMI::HmiOpened")]
    [InlineData(" MAIN.x ", "PLC2", "ADS.PLC2.MAIN.x", "PLC2::MAIN::x")]
    public void SymbolNames_DeriveTheVerifiedConvention(string plcPath, string? runtime, string mapName, string internalName)
    {
        Assert.True(HmiSymbolNames.TryFromPlcPath(plcPath, runtime, out var map, out var target, out var error), error);
        Assert.Equal(mapName, map);
        Assert.Equal(internalName, target);
    }

    [Theory]
    [InlineData("bStart")]                       // single segment
    [InlineData("MAIN.aValues[1]")]              // arrays need explicit names
    [InlineData("ADS.PLC1.MAIN.x")]              // already a mapped name
    [InlineData("MAIN..x")]
    [InlineData("")]
    [InlineData(null)]
    public void SymbolNames_RejectNonPlainPaths(string? plcPath)
    {
        Assert.False(HmiSymbolNames.TryFromPlcPath(plcPath, null, out _, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void SymbolNames_RejectInvalidRuntime() =>
        Assert.False(HmiSymbolNames.TryFromPlcPath("MAIN.x", "PLC 1", out _, out _, out _));

    // ── auto-confirm policy (the only way around the popup) ─────────────────

    private static string Identity(string p) => p;

    [Fact]
    public void AutoConfirm_NeedsTheEnvironmentVariable()
    {
        Assert.False(HmiAutoConfirmPolicy.Applies(null, @"C:\T\fixture\Hmi", @"C:\T", Identity));
        Assert.False(HmiAutoConfirmPolicy.Applies("0", @"C:\T\fixture\Hmi", @"C:\T", Identity));
        Assert.False(HmiAutoConfirmPolicy.Applies("true", @"C:\T\fixture\Hmi", @"C:\T", Identity));
        Assert.True(HmiAutoConfirmPolicy.Applies("1", @"C:\T\fixture\Hmi", @"C:\T", Identity));
    }

    [Theory]
    [InlineData(@"C:\Repos\PlantHmi\Hmi")] // a real repo: never
    [InlineData(@"C:\T")]                                          // TEMP root itself
    [InlineData(@"C:\T\..\Repos\Hmi")]                             // traversal out of TEMP
    [InlineData(@"C:\TX\Hmi")]                                     // prefix, not a child
    [InlineData("")]
    public void AutoConfirm_OnlyStrictlyBelowTemp(string projectDirectory) =>
        Assert.False(HmiAutoConfirmPolicy.Applies("1", projectDirectory, @"C:\T\", Identity));

    [Fact]
    public void AutoConfirm_ComparesLongPaths_ShortTempStillMatches()
    {
        // Live: %TEMP% = C:\Users\SOMEUS~1\AppData\Local\Temp, XAE reports the long name.
        static string Expand(string p) => p.Replace(@"\SOMEUS~1\", @"\some.user\", StringComparison.OrdinalIgnoreCase);
        Assert.True(HmiAutoConfirmPolicy.Applies("1",
            @"C:\Users\some.user\AppData\Local\Temp\ptc-xae-mcp-hmi-fixture\Hmi",
            @"C:\Users\SOMEUS~1\AppData\Local\Temp\", Expand));
    }

    [Fact]
    public void TempAutoConfirmer_SkipsThePopupOnlyWhenThePolicyApplies()
    {
        var popup = new FakeHmiConfirmer(HmiConfirmOutcome.Rejected);
        var inTemp = new HmiWriteRequest(HmiWriteKind.MapSymbol, "P", @"C:\T\fixture", "S", "ADS", "x", null, false);
        var inRepo = inTemp with { ProjectDirectory = @"C:\Repos\P" };

        var onEnv = new HmiTempAutoConfirmer(popup, () => "1", () => @"C:\T", Identity);
        Assert.Equal(HmiConfirmOutcome.AutoConfirmed, onEnv.Confirm(inTemp, TimeSpan.FromSeconds(1)));
        Assert.Empty(popup.Requests);
        Assert.Equal(HmiConfirmOutcome.Rejected, onEnv.Confirm(inRepo, TimeSpan.FromSeconds(1)));
        Assert.Single(popup.Requests);

        var offEnv = new HmiTempAutoConfirmer(popup, () => null, () => @"C:\T", Identity);
        Assert.Equal(HmiConfirmOutcome.Rejected, offEnv.Confirm(inTemp, TimeSpan.FromSeconds(1)));
    }

    // ── MapSymbol: validation happens before anything else ───────────────────

    [Theory]
    [InlineData(null, null, null)]                              // nothing
    [InlineData(null, "ADS.PLC1.MAIN.x", null)]                 // mapName without internalName
    [InlineData("MAIN.x", "ADS.PLC1.MAIN.x", "PLC1::MAIN::x")]  // both styles
    [InlineData("MAIN.a[1]", null, null)]                       // underivable path
    public void Map_InvalidInput_NoCallsNoPopup(string? plcPath, string? mapName, string? internalName)
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = Map(fake, popup, plcPath, mapName, internalName);

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(fake.CallLog);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void Map_PlcPathWithForeignDomain_Rejected()
    {
        var (summary, _) = Map(Session(), new FakeHmiConfirmer(), domain: "TcHmiSrv");
        Assert.Contains("derives ADS names only", summary);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    public void Map_ConfirmTimeoutOutOfRange_Rejected(int seconds)
    {
        var fake = Session();
        Assert.Contains("confirmTimeoutSeconds", Map(fake, new FakeHmiConfirmer(), timeout: seconds).Summary);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Map_AlreadyMapped_RefusedWithoutPopup()
    {
        var fake = Session();
        fake.HmiSymbolValues["ADS.PLC1.MAIN.fbHmi.bStart"] = "False"; // the live server knows it
        var popup = new FakeHmiConfirmer();

        var (summary, _) = Map(fake, popup);

        Assert.Contains("already mapped", summary);
        Assert.Contains("never re-mapped silently", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Map_MappedButPlcUnreachable_StillCountsAsMapped()
    {
        // Live: a mapped ADS symbol without a reachable PLC reads as HMI_ADS_E_SYNC_TIMEOUT.
        var fake = Session();
        fake.HmiSymbolErrors["ADS.PLC1.MAIN.fbHmi.bStart"] =
            "server request: Command Error: Code:1048597, Message: HMI_ADS_E_SYNC_TIMEOUT, Reason: …";

        var (summary, _) = Map(fake, new FakeHmiConfirmer());

        Assert.Contains("already mapped", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Map_StaleListSaysMapped_LiveServerWins()
    {
        // Live 2026-09-25: the Automation Interface list kept an unmapped symbol even with refresh=true.
        var fake = Session();
        fake.HmiMappedSymbols.Add(new HmiMappedSymbol("ADS.PLC1.MAIN.fbHmi.bStart", "ADS", "BOOL", "BOOL", false, false));

        var (summary, _) = Map(fake, new FakeHmiConfirmer());

        Assert.StartsWith("Mapped 'ADS.PLC1.MAIN.fbHmi.bStart'", summary);
    }

    [Fact]
    public void Map_StateUnknown_FailsClosedWithoutPopup()
    {
        var fake = Session();
        fake.HmiSymbolErrors["ADS.PLC1.MAIN.fbHmi.bStart"] = "server request: Command Error: Code:9, Message: SOMETHING_ELSE";
        var popup = new FakeHmiConfirmer();

        var (summary, _) = Map(fake, popup);

        Assert.StartsWith("[Error] Cannot determine whether", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    // ── MapSymbol: the confirmation gate ─────────────────────────────────────

    [Theory]
    [InlineData(nameof(HmiConfirmOutcome.Rejected), "rejected")]
    [InlineData(nameof(HmiConfirmOutcome.TimedOut), "within 120 s")]
    [InlineData(nameof(HmiConfirmOutcome.Unavailable), "No confirmation channel")]
    public void Map_NotConfirmed_NothingWritten(string outcome, string expected)
    {
        var fake = Session();
        var (summary, _) = Map(fake, new FakeHmiConfirmer(Enum.Parse<HmiConfirmOutcome>(outcome)));

        Assert.StartsWith("[Error]", summary);
        Assert.Contains(expected, summary);
        Assert.Contains("Nothing was written", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Map_WithoutAnyConfirmer_FailsClosed()
    {
        var fake = Session();
        var (summary, _) = Map(fake, confirmer: null);
        Assert.Contains("No confirmation channel", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Map_PopupShowsWhatWillBeWritten()
    {
        var popup = new FakeHmiConfirmer();
        Map(Session(), popup, timeout: 45);

        var (request, timeout) = Assert.Single(popup.Requests);
        Assert.Equal(HmiWriteKind.MapSymbol, request.Kind);
        Assert.Equal(PlantHmi, request.HmiProject);
        Assert.Equal(Dir, request.ProjectDirectory);
        Assert.Equal("ADS.PLC1.MAIN.fbHmi.bStart", request.Symbol);
        Assert.Equal("PLC1::MAIN::fbHmi::bStart", request.NewValue);
        Assert.False(request.WritesToPlc);
        Assert.Equal(TimeSpan.FromSeconds(45), timeout);
    }

    [Fact]
    public void Map_Confirmed_MapsThenReadsBack()
    {
        var fake = Session();
        var (summary, json) = Map(fake, new FakeHmiConfirmer());

        Assert.StartsWith("Mapped 'ADS.PLC1.MAIN.fbHmi.bStart' → 'PLC1::MAIN::fbHmi::bStart'", summary);
        Assert.Contains("confirmed by the engineer, verified by a live read-back", summary);
        var calls = fake.CallLog.Where(c => c.StartsWith("MapHmiSymbol", StringComparison.Ordinal)
                                         || c.StartsWith("ReadHmiSymbols", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            $"ReadHmiSymbols({PlantHmi}, [ADS.PLC1.MAIN.fbHmi.bStart])",
            $"MapHmiSymbol({PlantHmi}, ADS.PLC1.MAIN.fbHmi.bStart, PLC1::MAIN::fbHmi::bStart, ADS, sub=<null>)",
            $"ReadHmiSymbols({PlantHmi}, [ADS.PLC1.MAIN.fbHmi.bStart])",
        ], calls);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("confirmedByEngineer", doc.RootElement.GetProperty("confirmation").GetString());
    }

    [Fact]
    public void Map_ExplicitNamesAndSubSymbol_PassedThrough()
    {
        var fake = Session();
        Map(fake, new FakeHmiConfirmer(), plcPath: null, mapName: "ADS.PLC1.GVL.a", internalName: "PLC1::GVL::a", sub: "x");
        Assert.Contains($"MapHmiSymbol({PlantHmi}, ADS.PLC1.GVL.a, PLC1::GVL::a, ADS, sub=x)", fake.CallLog);
    }

    [Fact]
    public void Map_AutoConfirmed_IsDeclaredInTheResult()
    {
        var (summary, json) = Map(Session(), new FakeHmiConfirmer(HmiConfirmOutcome.AutoConfirmed));
        Assert.Contains("auto-confirmed (TEMP fixture", summary);
        Assert.Contains("autoConfirmedTempFixture", json);
    }

    [Fact]
    public void Map_ServerRefusesWithBareServerRequest_AddsUnverifiedNote()
    {
        // Live: MapSymbol against an unreachable PLC runtime → false, LastError "server request".
        var fake = Session();
        fake.HmiMapResult = new HmiOperationResult(false, "server request");

        var (summary, _) = Map(fake, new FakeHmiConfirmer());

        Assert.StartsWith("[Error] MapSymbol(", summary);
        Assert.Contains("refused by the HMI server: server request", summary);
        Assert.Contains("Not verified", summary);
    }

    [Fact]
    public void Map_ReportsTrueButReadBackMissing_IsAnError()
    {
        var fake = Session();
        fake.HmiMapSkipsReadBack = true;

        var (summary, _) = Map(fake, new FakeHmiConfirmer());

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("does not know 'ADS.PLC1.MAIN.fbHmi.bStart' on read-back", summary);
        Assert.Contains("not treated as success", summary);
    }

    // ── UnmapSymbol ──────────────────────────────────────────────────────────

    private static FakeTwinCatSession WithMapping()
    {
        var fake = Session();
        fake.HmiMappedSymbols.Add(new HmiMappedSymbol("ADS.PLC1.MAIN.fbHmi.bStart", "ADS", "BOOL", "BOOL", false, false));
        fake.HmiSymbolValues["ADS.PLC1.MAIN.fbHmi.bStart"] = "False";
        return fake;
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ADS.PLC1.MAIN.x", "MAIN.x")]
    public void Unmap_NeedsExactlyOneName(string? mapName, string? plcPath)
    {
        var fake = WithMapping();
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer())
            .UnmapSymbol(null, plcPath, mapName, null, null, false, 120);
        Assert.StartsWith("[Error] Pass exactly one", summary);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Unmap_NotMapped_NothingToDo_NoPopup()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).UnmapSymbol(null, "MAIN.fbHmi.bStart", null, null, null, false, 120);

        Assert.StartsWith("Nothing to do", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Unmap_StateUnknown_FailsClosedWithoutPopup()
    {
        var fake = Session();
        fake.HmiSymbolErrors["ADS.PLC1.MAIN.fbHmi.bStart"] = "RPC server unavailable";
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup)
            .UnmapSymbol(null, null, "ADS.PLC1.MAIN.fbHmi.bStart", null, null, false, 120);

        Assert.StartsWith("[Error] Cannot determine whether", summary);
        Assert.Empty(popup.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Unmap_Rejected_NothingWritten()
    {
        var fake = WithMapping();
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected))
            .UnmapSymbol(null, null, "ADS.PLC1.MAIN.fbHmi.bStart", null, null, false, 120);

        Assert.Contains("Nothing was written", summary);
        Assert.False(Wrote(fake));
        Assert.Single(fake.HmiMappedSymbols);
    }

    [Fact]
    public void Unmap_Confirmed_RemovesAndVerifies()
    {
        var fake = WithMapping();
        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer())
            .UnmapSymbol(null, "MAIN.fbHmi.bStart", null, null, null, true, 120);

        Assert.StartsWith("Unmapped 'ADS.PLC1.MAIN.fbHmi.bStart'", summary);
        Assert.Contains("incl. historized data", summary);
        Assert.Contains($"UnmapHmiSymbol({PlantHmi}, ADS.PLC1.MAIN.fbHmi.bStart, ADS, historized=True)", fake.CallLog);
        Assert.Empty(fake.HmiMappedSymbols);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public void Unmap_ReportsTrueButStillMapped_IsAnError()
    {
        var fake = WithMapping();
        fake.HmiUnmapSkipsReadBack = true;

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer())
            .UnmapSymbol(null, null, "ADS.PLC1.MAIN.fbHmi.bStart", null, null, false, 120);

        Assert.Contains("still knows 'ADS.PLC1.MAIN.fbHmi.bStart' on read-back", summary);
    }

    // ── WriteSymbol ──────────────────────────────────────────────────────────

    private const string Bit = "ADS.PLC1.MAIN.fbHmi.bStart";

    private static bool WroteValue(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c.StartsWith("WriteHmiSymbol", StringComparison.Ordinal));

    private static FakeTwinCatSession WithBit(string current = "False")
    {
        var fake = Session();
        fake.HmiSymbolValues[Bit] = current;
        fake.HmiMappedSymbols.Add(new HmiMappedSymbol(Bit, "ADS", "tchmi:general#/definitions/BOOL", "BOOL", false, false));
        return fake;
    }

    [Theory]
    [InlineData(null, "true")]
    [InlineData("  ", "true")]
    [InlineData(Bit, null)]
    [InlineData(Bit, "")]
    [InlineData(Bit, "tru")]           // not JSON
    [InlineData(Bit, "text")]          // strings need quotes
    public void Write_InvalidInput_NoCallsNoPopup(string? symbol, string? valueJson)
    {
        var fake = WithBit();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).WriteSymbol(null, symbol, valueJson, 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(fake.CallLog);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void Write_UnmappedSymbol_RefusedWithoutPopup()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).WriteSymbol(null, "ADS.PLC1.MAIN.nope", "true", 120);

        Assert.Contains("SYMBOL_NOT_MAPPED", summary);
        Assert.Empty(popup.Requests);
        Assert.False(WroteValue(fake));
    }

    [Fact]
    public void Write_ReadOnlyMapping_RefusedWithoutPopup()
    {
        var fake = WithBit();
        fake.HmiMappedSymbols[0] = fake.HmiMappedSymbols[0] with { ReadOnly = true };
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).WriteSymbol(null, Bit, "true", 120);

        Assert.Contains("read-only", summary);
        Assert.Empty(popup.Requests);
        Assert.False(WroteValue(fake));
    }

    [Theory]
    [InlineData(nameof(HmiConfirmOutcome.Rejected))]
    [InlineData(nameof(HmiConfirmOutcome.TimedOut))]
    [InlineData(nameof(HmiConfirmOutcome.Unavailable))]
    public void Write_NotConfirmed_NothingWritten(string outcome)
    {
        var fake = WithBit();
        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(Enum.Parse<HmiConfirmOutcome>(outcome)))
            .WriteSymbol(null, Bit, "true", 120);

        Assert.Contains("Nothing was written", summary);
        Assert.False(WroteValue(fake));
        Assert.Equal("False", fake.HmiSymbolValues[Bit]);
    }

    [Fact]
    public void Write_PopupShowsBeforeAfterAndThePlcWarning()
    {
        var popup = new FakeHmiConfirmer();
        new HmiCoordinator(WithBit(), popup).WriteSymbol(null, Bit, " true ", 120);

        var (request, _) = Assert.Single(popup.Requests);
        Assert.Equal(HmiWriteKind.WriteSymbol, request.Kind);
        Assert.Equal("False", request.CurrentValue);
        Assert.Equal("true", request.NewValue);
        Assert.True(request.WritesToPlc);
        Assert.Equal("ADS", request.Domain);
    }

    [Fact]
    public void Write_ServerSymbol_IsNotFlaggedAsPlcWrite()
    {
        var fake = Session();
        fake.HmiSymbolValues["UserSelectType"] = "0";
        var popup = new FakeHmiConfirmer();

        new HmiCoordinator(fake, popup).WriteSymbol(null, "UserSelectType", "0", 120);

        Assert.False(Assert.Single(popup.Requests).Request.WritesToPlc);
    }

    [Theory]
    [InlineData("ADS.Config::RUNTIMES::PLC1::TMC")]
    [InlineData("ADS.Config::RUNTIMES::PLC1::NETID")]
    [InlineData("ads.config")]
    [InlineData("TcHmiSrv.Config::SYMBOLS::X")]
    public void Write_ServerConfigSymbol_IsNotFlaggedAsPlcWrite(string symbol)
    {
        // Live 2026-11-18: a write to ADS.Config::… was reported as "through to the PLC".
        var fake = Session();
        fake.HmiSymbolValues[symbol] = "{}";
        var popup = new FakeHmiConfirmer();

        var (summary, json) = new HmiCoordinator(fake, popup).WriteSymbol(null, symbol, "{\"USE_TMC\":true}", 120);

        Assert.False(Assert.Single(popup.Requests).Request.WritesToPlc);
        Assert.DoesNotContain("through to the PLC", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("writesToPlc").GetBoolean());
    }

    [Theory]
    [InlineData("ADS.Config", null, false)]
    [InlineData("ADS.Config::RUNTIMES", "ADS", false)]
    [InlineData("ADS.PLC1.MAIN.bStart", null, true)]
    [InlineData("ADS.PLC1.GVL.Config", null, true)]
    [InlineData("MyServerSymbol", "ADS", true)]
    [InlineData("UserSelectType", null, false)]
    public void WritesToPlc_ConfigNeverPlc(string symbol, string? domain, bool expected) =>
        Assert.Equal(expected, HmiSymbolNames.WritesToPlc(symbol, domain));

    [Fact]
    public void Write_Confirmed_WritesCompactJsonAndVerifies()
    {
        var fake = WithBit();
        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteSymbol(null, Bit, " true ", 120);

        Assert.Contains($"WriteHmiSymbol({PlantHmi}, {Bit}, true)", fake.CallLog);
        Assert.StartsWith($"Wrote true to '{Bit}'", summary);
        Assert.Contains("(through to the PLC)", summary);
        Assert.Contains("verified by read-back (True)", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal("False", doc.RootElement.GetProperty("before").GetString());
        Assert.Equal("True", doc.RootElement.GetProperty("after").GetString());
    }

    [Fact]
    public void Write_ReadBackDiffers_IsAWarningNotAnError()
    {
        var fake = WithBit();
        fake.HmiWriteReadBackOverride = "False"; // PLC resets the pulse bit immediately

        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteSymbol(null, Bit, "true", 120);

        Assert.False(summary.StartsWith("[Error]", StringComparison.Ordinal), summary);
        Assert.Contains("WARNING: read-back shows False", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public void Write_ServerRefuses_ReportsLastError()
    {
        var fake = WithBit();
        fake.HmiWriteResult = new HmiOperationResult(false, "Unexpected end while parsing unquoted property name.");

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer()).WriteSymbol(null, Bit, "true", 120);

        Assert.StartsWith("[Error] WriteSymbolJson(", summary);
        Assert.Contains("Unexpected end", summary);
    }

    [Theory]
    [InlineData("true", "True", true)]
    [InlineData("false", "True", false)]
    [InlineData("42", "42", true)]
    [InlineData("42", "42.0", true)]
    [InlineData("3.5", "3.4", false)]
    [InlineData("\"TcHmiUserManagement\"", "TcHmiUserManagement", true)]
    [InlineData("\"a\"", "b", false)]
    [InlineData("{\"x\":1,\"y\":[1,2]}", "{\"x\":1,\"y\":[1,2]}", true)]
    [InlineData("{\"x\":1}", "{\"x\":2}", false)]
    [InlineData("null", "null", true)]
    public void ValuesEquivalent_HandlesTheServersScalarText(string written, string readBack, bool expected) =>
        Assert.Equal(expected, HmiCoordinator.ValuesEquivalent(written, readBack));

    [Fact]
    public void ValuesEquivalent_NoReadBack_IsNotComparable() =>
        Assert.Null(HmiCoordinator.ValuesEquivalent("true", null));

    // ── popup text ───────────────────────────────────────────────────────────

    [Fact]
    public void PopupText_NamesProjectSymbolAndBothValues()
    {
        var request = new HmiWriteRequest(HmiWriteKind.WriteSymbol, PlantHmi, Dir, "ADS.PLC1.MAIN.bStart", "ADS", "true", "False", true);

        string details = HmiConfirmText.Details(request);

        Assert.Contains(PlantHmi, details);
        Assert.Contains(Dir, details);
        Assert.Contains("ADS.PLC1.MAIN.bStart", details);
        Assert.Contains("Current     : False", details);
        Assert.Contains("New         : true", details);
        Assert.Contains("write a value", HmiConfirmText.Headline(request));
    }
}
