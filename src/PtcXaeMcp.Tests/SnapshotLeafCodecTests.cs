using System.Text.Json;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// persistent-snapshot: raw ADS bytes <-> leaf JSON, round trip for every supported type.
public class SnapshotLeafCodecTests
{
    private static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Theory]
    [InlineData("BOOL", "true", 1)]
    [InlineData("BOOL", "false", 1)]
    [InlineData("BYTE", "255", 1)]
    [InlineData("SINT", "-128", 1)]
    [InlineData("UINT", "65535", 2)]
    [InlineData("INT", "-32768", 2)]
    [InlineData("UDINT", "4294967295", 4)]
    [InlineData("DINT", "-2147483648", 4)]
    [InlineData("ULINT", "18446744073709551615", 8)]
    [InlineData("LINT", "-9223372036854775808", 8)]
    [InlineData("REAL", "3.5", 4)]
    [InlineData("LREAL", "0.1", 8)]
    [InlineData("TIME", "1500", 4)]
    [InlineData("LTIME", "123456789012", 8)]
    [InlineData("DT", "1700000000", 4)]
    public void Fixed_size_types_round_trip(string type, string json, int size)
    {
        byte[] raw = SnapshotLeafCodec.Encode(type, J(json), size);
        raw.Length.Should().Be(size);

        var back = SnapshotLeafCodec.Decode(type, raw);

        SnapshotValues.ValueEquals(back, J(json)).Should().BeTrue($"{type} {json} -> {back.GetRawText()}");
    }

    [Fact]
    public void Strings_round_trip_including_non_ascii_and_wide()
    {
        byte[] s = SnapshotLeafCodec.Encode("STRING", J("\"Grüße\""), 81);
        s.Length.Should().Be(81);
        SnapshotLeafCodec.Decode("STRING", s).GetString().Should().Be("Grüße");

        byte[] w = SnapshotLeafCodec.Encode("WSTRING", J("\"日本語\""), 22);
        w.Length.Should().Be(22);
        SnapshotLeafCodec.Decode("WSTRING", w).GetString().Should().Be("日本語");
    }

    [Fact]
    public void Non_finite_floats_are_text_and_come_back()
    {
        byte[] raw = SnapshotLeafCodec.Encode("LREAL", J("\"NaN\""), 8);
        SnapshotLeafCodec.Decode("LREAL", raw).GetString().Should().Be("NaN");
        byte[] inf = SnapshotLeafCodec.Encode("REAL", J("\"-Infinity\""), 4);
        SnapshotLeafCodec.Decode("REAL", inf).GetString().Should().Be("-Infinity");
    }

    [Theory]
    [InlineData("SINT", "128")]
    [InlineData("USINT", "-1")]
    [InlineData("UINT", "70000")]
    [InlineData("INT", "1.5")]
    [InlineData("BOOL", "1")]
    [InlineData("STRING", "5")]
    [InlineData("DINT", "\"x\"")]
    public void Values_the_type_cannot_take_are_refused(string type, string json)
        => FluentActions.Invoking(() => SnapshotLeafCodec.Encode(type, J(json), 8)).Should().Throw<FormatException>();

    [Fact]
    public void Too_long_text_is_refused_for_wstring()
        => FluentActions.Invoking(() => SnapshotLeafCodec.Encode("WSTRING", J("\"abcdef\""), 8))
            .Should().Throw<FormatException>();

    [Theory]
    [InlineData("TIME", "1500", "T#1s500ms")]
    [InlineData("TIME", "0", "T#0ms")]
    [InlineData("TIME", "90061001", "T#1d1h1m1s1ms")]
    [InlineData("DT", "1700000000", "DT#2023-11-14-22:13:20")]
    [InlineData("INT", "5", null)]
    public void Display_forms(string type, string json, string? expected)
        => SnapshotLeafCodec.Display(type, J(json)).Should().Be(expected);
}
