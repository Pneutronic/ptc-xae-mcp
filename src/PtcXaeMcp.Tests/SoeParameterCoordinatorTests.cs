using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class SoeParameterCoordinatorTests
{
    private const string Path = "TIID^Device 1 (EtherCAT)^Drive 1";

    private static FakeTwinCatSession Fake(SoeParameterReadResult? result = null) => new()
    {
        IsConnected = true,
        SolutionOpen = true,
        FakeSoeParameterResult = result ?? new SoeParameterReadResult(Path, "10.0.0.1.2.1", 1002, SoeParameterMode.Read, []),
    };

    private static string Read(FakeTwinCatSession fake, string? path = Path, string[]? idns = null, int? driveNo = null,
        string[]? elements = null, int? timeoutMs = null) =>
        new SoeParameterCoordinator(fake).Read(path, idns, driveNo, elements, timeoutMs);

    private static (string Summary, JsonElement Json) Split(string text)
    {
        int nl = text.IndexOf('\n');
        return (text[..nl].TrimEnd('\r'), JsonDocument.Parse(text[(nl + 1)..]).RootElement.Clone());
    }

    private static SoeParameterRecord Param(int idn, SoeData? value = null, IReadOnlyList<SoeReadError>? errors = null,
        bool? match = null, string? note = null) =>
        new(SoeIdn.Format(idn), idn, 0, Value: value, Errors: errors, Match: match, Note: note);

    // ── argument validation: no session call ────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingTreePath_IsAnError(string? path)
    {
        var fake = Fake();
        Assert.StartsWith("[Error]", Read(fake, path));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void RootPath_IsAnError()
    {
        var fake = Fake();
        Assert.StartsWith("[Error]", Read(fake, "TIID"));
        Assert.Empty(fake.CallLog);
    }

    [Theory]
    [InlineData("S-0-99999")]
    [InlineData("X-0-1")]
    [InlineData("70000")]
    [InlineData("")]
    public void InvalidIdn_IsAnErrorNamingTheEntry(string idn)
    {
        var fake = Fake();
        string text = Read(fake, idns: ["S-0-0001", idn]);
        Assert.StartsWith("[Error]", text);
        Assert.Contains($"'{idn}'", text);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void TooManyIdns_IsAnError()
    {
        var fake = Fake();
        string[] idns = Enumerable.Range(0, SoeParameterCoordinator.MaxIdns + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.StartsWith("[Error]", Read(fake, idns: idns));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void InvalidElement_IsAnError()
    {
        var fake = Fake();
        string text = Read(fake, idns: ["S-0-0001"], elements: ["value", "bogus"]);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("'bogus'", text);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void ElementsWithoutIdns_IsAnError()
    {
        var fake = Fake();
        Assert.StartsWith("[Error]", Read(fake, elements: ["value"]));
        Assert.Empty(fake.CallLog);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void DriveNoOutOfRange_IsAnError(int driveNo)
    {
        var fake = Fake();
        Assert.StartsWith("[Error]", Read(fake, driveNo: driveNo));
        Assert.Empty(fake.CallLog);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(60000)]
    public void TimeoutOutOfRange_IsAnError(int timeout)
    {
        var fake = Fake();
        Assert.StartsWith("[Error]", Read(fake, timeoutMs: timeout));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void NotConnected_IsAnError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        Assert.StartsWith("[Error]", Read(fake));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void NoSolution_IsAnError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", Read(fake));
    }

    // ── arguments handed to the session ─────────────────────────────────────

    [Fact]
    public void Idns_AreParsed_Deduplicated_AndPassedWithDefaultElementAndTimeout()
    {
        var fake = Fake();
        Read(fake, "  " + Path + " ", idns: ["S-0-0001", "P-0-3231", "1", "0x8C9F"]);
        Assert.Equal($"ReadSoeParameters({Path},S-0-0001|P-0-3231,-,value,5000)", fake.CallLog.Single());
    }

    [Fact]
    public void Elements_DriveNoAndTimeout_ArePassed()
    {
        var fake = Fake();
        Read(fake, idns: ["S-0-0001"], driveNo: 1, elements: ["attribute", "Value", "attribute", "unit"], timeoutMs: 8000);
        Assert.Equal($"ReadSoeParameters({Path},S-0-0001,1,attribute|value|unit,8000)", fake.CallLog.Single());
    }

    [Fact]
    public void WithoutIdns_ComparesTheStartupList()
    {
        var fake = Fake(new SoeParameterReadResult(Path, "10.0.0.1.2.1", 1002, SoeParameterMode.Compare, []));
        Read(fake, driveNo: 4);
        Assert.Equal($"ReadSoeParameters({Path},all,4,value,5000)", fake.CallLog.Single());
    }

    [Fact]
    public void EmptyIdnsArray_IsTheCompareMode()
    {
        var fake = Fake(new SoeParameterReadResult(Path, "10.0.0.1.2.1", 1002, SoeParameterMode.Compare, []));
        Read(fake, idns: []);
        Assert.Contains(",all,", fake.CallLog.Single());
    }

    // ── errors from the session ─────────────────────────────────────────────

    [Fact]
    public void UnreachableDrive_IsAnErrorWithTheMessage()
    {
        var fake = Fake();
        fake.ReadSoeParametersThrows = new SoeTargetUnreachableException("The SoE target 10.0.0.1.2.1:1002 did not answer: ClientSyncTimeout (1861).");
        string text = Read(fake, idns: ["S-0-0001"]);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("10.0.0.1.2.1:1002", text);
    }

    [Fact]
    public void SessionFailure_IsAnError()
    {
        var fake = Fake();
        fake.ReadSoeParametersThrows = new InvalidOperationException("Tree path not found: x");
        Assert.Contains("Tree path not found: x", Read(fake, "x", idns: ["1"]));
    }

    // ── summary ─────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_ReadMode_CountsErrors()
    {
        var ok = Param(1, new SoeData("0100", 2, 1, null, null, null, null, null));
        var bad = Param(2, errors: [new SoeReadError("value", 1793, "DeviceInvalidOffset")]);

        Assert.Equal("1 SoE parameters read",
            SoeParameterCoordinator.Summary(new("p", "n", 1, SoeParameterMode.Read, [ok])));
        Assert.Equal("2 SoE parameters read (1 with errors)",
            SoeParameterCoordinator.Summary(new("p", "n", 1, SoeParameterMode.Read, [ok, bad])));
    }

    [Fact]
    public void Summary_CompareMode_CountsMatchDifferErrorsSkipped()
    {
        var rs = new[]
        {
            Param(1, match: true), Param(2, match: true), Param(3, match: false),
            Param(4, errors: [new SoeReadError("value", 1793, "DeviceInvalidOffset")]), Param(5, note: "n"),
        };
        Assert.Equal("5 SoE parameters compared with the drive: 2 match, 1 differ, 1 errors, 1 skipped",
            SoeParameterCoordinator.Summary(new("p", "n", 1, SoeParameterMode.Compare, rs)));
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    [Fact]
    public void Json_ReadResult_Shape()
    {
        var attr = SoeAttributeDecoder.Decode(0x01120001);
        var value = SoeDataDecoder.Decode([0x64, 0, 0, 0], 80, attr);
        var rec = new SoeParameterRecord("S-0-0080", 80, 2, "Torque limit", "Nm", attr, value,
            Errors: [new SoeReadError("max", 1793, "DeviceInvalidOffset")]);
        var fake = Fake(new SoeParameterReadResult(Path, "10.0.0.1.2.1", 1002, SoeParameterMode.Read, [rec]));

        var (summary, json) = Split(Read(fake, idns: ["S-0-0080"], elements: ["value", "name", "unit", "attribute", "max"]));

        Assert.Equal("1 SoE parameters read (1 with errors)", summary);
        Assert.Equal(Path, json.GetProperty("treePath").GetString());
        Assert.Equal("10.0.0.1.2.1", json.GetProperty("netId").GetString());
        Assert.Equal(1002, json.GetProperty("port").GetInt32());
        Assert.Equal("read", json.GetProperty("mode").GetString());

        var p = json.GetProperty("parameters")[0];
        Assert.Equal("S-0-0080", p.GetProperty("idn").GetString());
        Assert.Equal(80, p.GetProperty("idnRaw").GetInt32());
        Assert.Equal(2, p.GetProperty("driveNo").GetInt32());
        Assert.Equal("Torque limit", p.GetProperty("name").GetString());
        Assert.Equal("Nm", p.GetProperty("unit").GetString());
        Assert.Equal("0x01120001", p.GetProperty("attribute").GetProperty("rawHex").GetString());
        Assert.Equal("unsignedDecimal", p.GetProperty("attribute").GetProperty("display").GetString());
        Assert.Equal(1, p.GetProperty("attribute").GetProperty("decimalPlaces").GetInt32());
        Assert.Equal("64000000", p.GetProperty("value").GetProperty("hex").GetString());
        Assert.Equal(100UL, p.GetProperty("value").GetProperty("unsigned").GetUInt64());
        Assert.Equal(10.0m, p.GetProperty("value").GetProperty("scaled").GetDecimal());
        Assert.False(p.TryGetProperty("min", out _));
        Assert.False(p.TryGetProperty("match", out _));
        var error = p.GetProperty("errors")[0];
        Assert.Equal("max", error.GetProperty("element").GetString());
        Assert.Equal(1793, error.GetProperty("code").GetInt32());
        Assert.Equal("DeviceInvalidOffset", error.GetProperty("name").GetString());
    }

    [Fact]
    public void Json_CompareResult_HasProjectedActualMatch()
    {
        var rec = new SoeParameterRecord("S-0-0015", 15, 4, Fixed: true, Comment: "Telegram type", Projected: "0300", Actual: "0100", Match: false);
        var fake = Fake(new SoeParameterReadResult(Path, "10.0.0.1.2.1", 1002, SoeParameterMode.Compare, [rec]));

        var (summary, json) = Split(Read(fake));

        Assert.StartsWith("1 SoE parameters compared", summary);
        Assert.Equal("compare", json.GetProperty("mode").GetString());
        var p = json.GetProperty("parameters")[0];
        Assert.True(p.GetProperty("fixed").GetBoolean());
        Assert.Equal("Telegram type", p.GetProperty("comment").GetString());
        Assert.Equal("0300", p.GetProperty("projected").GetString());
        Assert.Equal("0100", p.GetProperty("actual").GetString());
        Assert.False(p.GetProperty("match").GetBoolean());
    }
}
