using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat.Schema;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Coverage for the pure CSV rendering extracted from NativeTwinCatRunner.WriteCsv/Csv
/// (Hardening 9, step 1).
/// </summary>
public class CsvExportTests
{
    [Fact]
    public void BuildMappingCsv_EmptyRows_ProducesHeaderOnly()
    {
        string csv = CsvExport.BuildMappingCsv([]);

        csv.TrimEnd().Should().Be("symbol;varName;direction;type;terminal;channel;group;description;plcPath;ioPath");
    }

    [Fact]
    public void BuildMappingCsv_OneRow_QuotesEveryTextField()
    {
        var row = new LinkResult
        {
            Symbol = "MAIN.aCyl[1].bIn",
            VarName = "x",
            Direction = "I",
            Type = "BOOL",
            Terminal = "T1",
            Channel = 2,
            Group = "G",
            Description = "d",
            PlcPath = "p",
            IoPath = "i"
        };

        string csv = CsvExport.BuildMappingCsv([row]);

        csv.Should().Contain("\"MAIN.aCyl[1].bIn\";\"x\";\"I\";\"BOOL\";\"T1\";2;\"G\";\"d\";\"p\";\"i\"");
    }

    [Fact]
    public void BuildMappingCsv_SymbolIsTheFirstColumn_VarNameTheSecond()
    {
        string csv = CsvExport.BuildMappingCsv([new LinkResult { Symbol = "MAIN.fbCyl.bIn", VarName = "Cyl 1 in" }]);

        string[] lines = csv.TrimEnd().Split(Environment.NewLine);
        lines[0].Split(';')[..2].Should().Equal("symbol", "varName");
        lines[1].Split(';')[..2].Should().Equal("\"MAIN.fbCyl.bIn\"", "\"Cyl 1 in\"");
    }

    [Fact]
    public void Escape_EmbeddedQuote_IsDoubled()
    {
        CsvExport.Escape("a\"b").Should().Be("\"a\"\"b\"");
    }

    [Fact]
    public void Escape_Null_TreatedAsEmptyString()
    {
        CsvExport.Escape(null).Should().Be("\"\"");
    }
}
