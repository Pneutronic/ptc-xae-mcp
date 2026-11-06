using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Issue #148 — PLC write path: delete whitelist, fail-closed offline guard, target validation before logout, unknown implLanguage, strict UTF-8 file input, verified METHOD header.</summary>
public class PlcWriteSafetyTests
{
    private const string PouPath = "TIPC^Plc^Plc Project^POUs^FB_A";

    private static FakeTwinCatSession OpenFake() => new() { IsConnected = true, SolutionOpen = true };

    // =====================================================================
    // Delete whitelist (LIVE-8 / B-4, P0)
    // =====================================================================

    [Theory]
    [InlineData(PlcItemType.PlcApp, "PLC project root")]
    [InlineData(PlcItemType.Folder, "folder")]
    [InlineData(617, "References")]
    [InlineData(9001, "ItemType 9001")] // e.g. the PLC instance / any non-source node
    [InlineData(635, "ItemType 635")]   // trend manager — never created/deleted live, not whitelisted
    public void Delete_RejectedKind_IsRefused_WithoutLogoutOrDelete(int itemType, string expectedFragment)
    {
        var fake = OpenFake();
        fake.PlcNodeItemTypes["TIPC^Plc^Plc Project"] = itemType;

        string r = new PouMembersCoordinator(fake).DeletePlcObject("TIPC^Plc^Plc Project");

        Assert.StartsWith("[Error] Refusing to delete", r);
        Assert.Contains(expectedFragment, r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("DeletePlcObject", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_UnreadableItemType_IsRefused()
    {
        var fake = OpenFake();
        fake.PlcNodeItemTypes[PouPath] = null;

        string r = new PouMembersCoordinator(fake).DeletePlcObject(PouPath);

        Assert.StartsWith("[Error] Refusing to delete", r);
        Assert.Contains("could not be read", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("DeletePlcObject", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_PathNotFound_NoLogout()
    {
        var fake = OpenFake();
        fake.MissingPlcPaths.Add(PouPath);

        string r = new PouMembersCoordinator(fake).DeletePlcObject(PouPath);

        Assert.StartsWith("[Error] PLC tree path not found", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    [Theory]
    [InlineData(PlcItemType.PouProgram)]
    [InlineData(PlcItemType.PouFunction)]
    [InlineData(PlcItemType.PouFunctionBlock)]
    [InlineData(PlcItemType.Dut)]
    [InlineData(PlcItemType.DutStruct)]
    [InlineData(PlcItemType.DutAlias)]
    [InlineData(PlcItemType.Gvl)]
    [InlineData(PlcItemType.GvlParamList)]
    [InlineData(PlcItemType.Interface)]
    [InlineData(PlcItemType.Method)]
    [InlineData(PlcItemType.Action)]
    [InlineData(PlcItemType.Property)]
    [InlineData(PlcItemType.PropertyGet)]
    [InlineData(PlcItemType.InterfaceMethod)]
    [InlineData(PlcDeletePolicy.Transition)]
    public void Delete_WhitelistedKind_ValidatesThenLogsOutThenDeletes(int itemType)
    {
        var fake = OpenFake();
        fake.PlcNodeItemTypes[PouPath] = itemType;

        string r = new PouMembersCoordinator(fake).DeletePlcObject(PouPath);

        Assert.StartsWith("Deleted ", r);
        int probe = fake.CallLog.IndexOf($"ProbePlcNode({PouPath})");
        int logout = fake.CallLog.IndexOf("LogoutPlc()");
        int delete = fake.CallLog.IndexOf($"DeletePlcObject({PouPath})");
        Assert.True(probe >= 0 && probe < logout && logout < delete, string.Join(" | ", fake.CallLog));
    }

    [Fact]
    public void DeletePolicy_Check_Messages()
    {
        Assert.Null(PlcDeletePolicy.Check("a^b", new PlcNodeInfo(true, PlcItemType.PouFunctionBlock)));
        Assert.StartsWith("[Error] PLC tree path not found", PlcDeletePolicy.Check("a^b", PlcNodeInfo.Missing));
        Assert.Contains("PLC project root", PlcDeletePolicy.Check("a^b", new PlcNodeInfo(true, 600)));
    }

    // =====================================================================
    // Fail-closed offline guard (B-1) + validation before logout (B-15)
    // =====================================================================

    [Fact]
    public void Guard_IsOnlineNowNull_Refuses()
    {
        var fake = OpenFake();
        fake.FakeLogoutPlcResult = new LogoutPlcResult(false, null, "state unknown (fake)");

        Assert.StartsWith("[Error] Could not confirm the XAE is Offline",
            SessionGuards.EnsureOfflineForWrite(fake, TimeSpan.Zero));
        Assert.Equal(2, fake.CallLog.Count(c => c == "LogoutPlc()"));
    }

    [Fact]
    public void Guard_UnknownThenConfirmedOffline_Passes()
    {
        // Issue #190: a momentarily unreadable state is probed again; a confirmed Offline then passes.
        var fake = OpenFake();
        fake.LogoutPlcResults.Enqueue(new LogoutPlcResult(false, null, "No active PLC login found (fake)."));
        fake.LogoutPlcResults.Enqueue(new LogoutPlcResult(false, false, "PLC already offline (fake)."));

        Assert.Null(SessionGuards.EnsureOfflineForWrite(fake, TimeSpan.Zero));
        Assert.Equal(2, fake.CallLog.Count(c => c == "LogoutPlc()"));
    }

    [Fact]
    public void Guard_UnknownThenOnline_Refuses()
    {
        var fake = OpenFake();
        fake.LogoutPlcResults.Enqueue(new LogoutPlcResult(false, null, "unknown (fake)"));
        fake.LogoutPlcResults.Enqueue(new LogoutPlcResult(true, true, "still online (fake)"));

        Assert.StartsWith("[Error] XAE is Online", SessionGuards.EnsureOfflineForWrite(fake, TimeSpan.Zero));
    }

    [Fact]
    public void Guard_ConfirmedOffline_ProbesOnce()
    {
        var fake = OpenFake();
        Assert.Null(SessionGuards.EnsureOfflineForWrite(fake, TimeSpan.Zero));
        Assert.Equal(1, fake.CallLog.Count(c => c == "LogoutPlc()"));
    }

    [Fact]
    public void Guard_BusyLogout_Refuses_NeverOffline()
    {
        var fake = OpenFake();
        fake.LogoutPlcThrows = new COMException("busy", unchecked((int)0x8001010A)); // RPC_E_SERVERCALL_RETRYLATER

        string? r = SessionGuards.EnsureOfflineForWrite(fake);
        Assert.NotNull(r);
        Assert.StartsWith("[Error] Could not confirm the XAE is Offline", r);
    }

    [Fact]
    public void Guard_LogoutThrows_Refuses()
    {
        var fake = OpenFake();
        fake.LogoutPlcThrows = new InvalidOperationException("boom");

        string? r = SessionGuards.EnsureOfflineForWrite(fake);
        Assert.StartsWith("[Error] Could not confirm the XAE is Offline", r);
        Assert.Contains("boom", r);
    }

    [Fact]
    public void Guard_ConfirmedOffline_Passes() =>
        Assert.Null(SessionGuards.EnsureOfflineForWrite(OpenFake()));

    [Fact]
    public void WritePouBody_UnknownOnlineState_DoesNotWrite()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[PouPath] = new PouBody("FUNCTION_BLOCK FB_A", "", "ST");
        fake.FakeLogoutPlcResult = new LogoutPlcResult(false, null, "state unknown (fake)");

        string r = new PlcCodeCoordinator(fake).WritePouBody(PouPath, "FUNCTION_BLOCK FB_A\nVAR END_VAR", null);

        Assert.StartsWith("[Error]", r);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("WritePouBody", StringComparison.Ordinal));
    }

    [Fact]
    public void WritePouBody_PathNotFound_NoLogout()
    {
        var fake = OpenFake(); // PouBodiesByPath empty → ReadPou throws "not found"

        string r = new PlcCodeCoordinator(fake).WritePouBody(PouPath, "FUNCTION_BLOCK FB_A", null);

        Assert.StartsWith("[Error]", r);
        Assert.Contains("not found", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    [Fact]
    public void WritePouBody_NonSt_NoLogout()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[PouPath] = new PouBody("", "", "FBD");

        string r = new PlcCodeCoordinator(fake).WritePouBody(PouPath, "x", null);

        Assert.StartsWith("[Error]", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    // F4 (fix/interface-write-guard): ReadPou reports an INTERFACE as language "ST" with an empty
    // implementation, so the language check passed and an implementation write only failed at the
    // ImplementationText setter — AFTER EnsureOfflineForWrite had already logged out a running plant.
    [Fact]
    public void WritePouBody_ImplementationOnInterface_RefusedWithoutLogout()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[PouPath] = new PouBody("INTERFACE I_A", "", "ST");
        fake.PlcNodeItemTypes[PouPath] = PlcItemType.Interface;

        string r = new PlcCodeCoordinator(fake).WritePouBody(PouPath, null, "x := 1;");

        Assert.StartsWith("[Error]", r);
        Assert.Contains("INTERFACE", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("WritePouBody(", StringComparison.Ordinal));
    }

    [Fact]
    public void WritePouBody_DeclarationOnlyOnInterface_StillWrites()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[PouPath] = new PouBody("INTERFACE I_A", "", "ST");
        fake.PlcNodeItemTypes[PouPath] = PlcItemType.Interface;

        string r = new PlcCodeCoordinator(fake).WritePouBody(PouPath, "INTERFACE I_A EXTENDS I_B", null);

        Assert.StartsWith("Wrote POU", r);
        Assert.Contains(fake.CallLog, c => c.StartsWith("WritePouBody(", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteGvl_PathNotFound_NoLogout()
    {
        var fake = OpenFake();
        string r = new PlcCodeCoordinator(fake).WriteGvl("TIPC^Plc^Plc Project^GVLs^GVL_X", "VAR_GLOBAL END_VAR");
        Assert.StartsWith("[Error]", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    [Fact]
    public void WritePouMember_PathNotFound_NoLogout()
    {
        var fake = OpenFake();
        string r = new PouMembersCoordinator(fake).WritePouMember(PouPath + "^M_X", "METHOD M_X : BOOL", null);
        Assert.StartsWith("[Error]", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    [Theory]
    [InlineData("pou")]
    [InlineData("dut")]
    [InlineData("gvl")]
    [InlineData("itf")]
    public void Create_ParentNotFound_NoLogout(string what)
    {
        var fake = OpenFake();
        const string parent = "TIPC^Plc^Plc Project^NoSuchFolder";
        fake.MissingPlcPaths.Add(parent);
        var c = new PlcCodeCoordinator(fake);

        string r = what switch
        {
            "pou" => c.CreatePou(parent, "FB_New", "FUNCTION_BLOCK", null, null),
            "dut" => c.CreateDut(parent, "ST_New", "STRUCT", null),
            "gvl" => c.CreateGvl(parent, "GVL_New", false, null),
            _     => c.CreateInterface(parent, "I_New", null),
        };

        Assert.StartsWith($"[Error] Parent path not found: {parent}\nNext: twincat_plc_list", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    [Fact]
    public void AddPouMember_PouNotFound_NoLogout()
    {
        var fake = OpenFake();
        fake.MissingPlcPaths.Add(PouPath);
        string r = new PouMembersCoordinator(fake).AddPouMember(PouPath, "METHOD", "M_Run", "INT", null);
        Assert.StartsWith($"[Error] POU not found: {PouPath}\nNext: ", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    [Fact]
    public void Rename_PathNotFound_NoLogout()
    {
        var fake = OpenFake();
        fake.MissingPlcPaths.Add(PouPath);
        string r = new PouMembersCoordinator(fake).RenamePlcObject(PouPath, "FB_B");
        Assert.StartsWith("[Error] PLC object not found", r);
        Assert.DoesNotContain("LogoutPlc()", fake.CallLog);
    }

    // =====================================================================
    // Strict UTF-8 file input + Note surfaced (B-9)
    // =====================================================================

    private static string TempFile(byte[] content)
    {
        string p = Path.Combine(Path.GetTempPath(), $"ptc148-{Guid.NewGuid():N}.st");
        File.WriteAllBytes(p, content);
        return p;
    }

    [Fact]
    public void SourceFile_AnsiUmlaut_IsError_WithByteOffset()
    {
        // "VAR x : STRING := '" + 0xE4 (Windows-1252 'ä') + "'; END_VAR"
        byte[] prefix = Encoding.ASCII.GetBytes("VAR x : STRING := '");
        byte[] bytes = [.. prefix, 0xE4, .. Encoding.ASCII.GetBytes("'; END_VAR")];
        string p = TempFile(bytes);
        try
        {
            var r = SourceTextInput.Resolve("declaration", null, p);
            Assert.Null(r.Text);
            Assert.NotNull(r.Error);
            Assert.StartsWith("[Error] declarationFile", r.Error);
            Assert.Contains("not valid UTF-8", r.Error);
            Assert.Contains($"byte offset {prefix.Length}", r.Error);
            Assert.Contains("0xE4", r.Error);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void SourceFile_ValidUtf8WithBom_StripsBom_AndHasNote()
    {
        string p = TempFile([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x := 'ä';\nEND")]);
        try
        {
            var r = SourceTextInput.Resolve("implementation", null, p);
            Assert.Null(r.Error);
            Assert.Equal("x := 'ä';\nEND", r.Text);
            Assert.Contains("implementation from", r.Note);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void SourceFile_BomAndInvalidByte_OffsetCountsTheBom()
    {
        string p = TempFile([0xEF, 0xBB, 0xBF, (byte)'a', 0xFF, (byte)'b']);
        try
        {
            var r = SourceTextInput.Resolve("declaration", null, p);
            Assert.Contains("byte offset 4", r.Error);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void SourceFile_Utf16LeBom_Decodes()
    {
        string p = TempFile([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("ä := 1;")]);
        try
        {
            var r = SourceTextInput.Resolve("declaration", null, p);
            Assert.Null(r.Error);
            Assert.Equal("ä := 1;", r.Text);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void WritePouBody_FromFile_SuccessIncludesNote()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[PouPath] = new PouBody("FUNCTION_BLOCK FB_A", "", "ST");
        string p = TempFile(Encoding.UTF8.GetBytes("x := 1;\ny := 2;"));
        try
        {
            string r = new PlcCodeCoordinator(fake).WritePouBody(PouPath, null, null, implementationFile: p);
            Assert.StartsWith($"Wrote POU '{PouPath}'. Read implementation from {Path.GetFileName(p)} (15 chars, 2 lines).", r);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void WritePouBody_Inline_HasNoNote()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[PouPath] = new PouBody("FUNCTION_BLOCK FB_A", "", "ST");
        Assert.Equal($"Wrote POU '{PouPath}'.", new PlcCodeCoordinator(fake).WritePouBody(PouPath, null, "x := 1;"));
    }

    // =====================================================================
    // METHOD header (B-7)
    // =====================================================================

    [Fact]
    public void MethodHeader_RewritesLanguageCodeReturn()
    {
        const string created = "METHOD M_Run : 1\nVAR_INPUT\nEND_VAR";
        Assert.False(MethodHeader.Matches(created, "M_Run", "INT"));
        string fixedDecl = MethodHeader.Rewrite(created, "M_Run", "INT");
        Assert.Equal("METHOD M_Run : INT\nVAR_INPUT\nEND_VAR", fixedDecl);
        Assert.True(MethodHeader.Matches(fixedDecl, "M_Run", "INT"));
    }

    [Theory]
    [InlineData("METHOD PUBLIC M_Run : INT", true)]
    [InlineData("  method m_run   :   int ; // comment", true)]
    [InlineData("METHOD M_Run : 1", false)]
    [InlineData("METHOD M_Other : INT", false)]
    [InlineData("VAR END_VAR", false)]
    [InlineData("", false)]
    public void MethodHeader_Matches(string decl, bool expected) =>
        Assert.Equal(expected, MethodHeader.Matches(decl, "M_Run", "INT"));

    [Fact]
    public void MethodHeader_MultiWordReturnType()
    {
        Assert.True(MethodHeader.Matches("METHOD M : ARRAY[0..9] OF  INT", "M", "ARRAY[0..9] OF INT"));
        Assert.Equal("METHOD M : 1", MethodHeader.HeaderLine("  METHOD M : 1\nVAR END_VAR"));
    }
}
