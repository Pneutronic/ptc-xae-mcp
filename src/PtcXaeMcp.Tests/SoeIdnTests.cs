using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class SoeIdnTests
{
    [Theory]
    [InlineData(15, "S-0-0015")]
    [InlineData(1, "S-0-0001")]
    [InlineData(4095, "S-0-4095")]
    [InlineData(0x8000 | 3231, "P-0-3231")]
    [InlineData(32778, "P-0-0010")]
    [InlineData(0x1000 | 7, "S-1-0007")]
    [InlineData(0x8000 | 0x3000 | 12, "P-3-0012")]
    [InlineData(0xFFFF, "P-7-4095")]
    public void Format_RendersSetAndNumber(int raw, string expected) =>
        Assert.Equal(expected, SoeIdn.Format(raw));

    [Theory]
    [InlineData("S-0-0015", 15)]
    [InlineData("s-0-0015", 15)]
    [InlineData("P-0-3231", 0x8000 | 3231)]
    [InlineData("P-0-10", 0x8000 | 10)]
    [InlineData("S-2-0100", 0x2000 | 100)]
    [InlineData("P-7-4095", 0xFFFF)]
    [InlineData("32778", 32778)]
    [InlineData("0x800A", 0x800A)]
    [InlineData(" S-0-0001 ", 1)]
    public void TryParse_AcceptsNotationDecimalAndHex(string text, int expected)
    {
        Assert.True(SoeIdn.TryParse(text, out int raw));
        Assert.Equal(expected, raw);
    }

    [Theory]
    [InlineData("S-0-99999")]
    [InlineData("S-0-4096")]
    [InlineData("X-0-1")]
    [InlineData("S-8-0001")]
    [InlineData("S-0-")]
    [InlineData("70000")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsInvalid(string? text) =>
        Assert.False(SoeIdn.TryParse(text, out _));

    [Fact]
    public void Format_And_TryParse_RoundTrip_AllValues()
    {
        for (int raw = 0; raw <= 0xFFFF; raw += 7)
        {
            Assert.True(SoeIdn.TryParse(SoeIdn.Format(raw), out int back));
            Assert.Equal(raw, back);
        }
    }

    [Theory]
    [InlineData(15, 0)]
    [InlineData(0x8000 | 3231, 0)]
    [InlineData(0x3000 | 5, 3)]
    [InlineData(0xBFFF, 3)]
    public void ParameterSet_IsBits12To14(int raw, int expected) =>
        Assert.Equal(expected, SoeIdn.ParameterSet(raw));

    [Theory]
    [InlineData(0x01, "dataStatus")]
    [InlineData(0x02, "name")]
    [InlineData(0x04, "attribute")]
    [InlineData(0x08, "unit")]
    [InlineData(0x10, "min")]
    [InlineData(0x20, "max")]
    [InlineData(0x40, "value")]
    [InlineData(0x80, "default")]
    [InlineData(0x44, "attribute,value")]
    [InlineData(0, "none")]
    public void ElementName_NamesEveryBit(int mask, string expected) =>
        Assert.Equal(expected, SoeElement.Name(mask));

    [Theory]
    [InlineData("value", 0x40)]
    [InlineData("Value", 0x40)]
    [InlineData("attribute", 0x04)]
    [InlineData("default", 0x80)]
    [InlineData("dataStatus", 0x01)]
    public void ElementTryParse_ByName(string text, int expected)
    {
        Assert.True(SoeElement.TryParse(text, out int mask));
        Assert.Equal(expected, mask);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData(null)]
    public void ElementTryParse_RejectsUnknown(string? text) =>
        Assert.False(SoeElement.TryParse(text, out _));
}
