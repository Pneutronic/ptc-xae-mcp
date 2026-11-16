using System.Text;
using PtcXaeMcp.WinForms;
using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat.Schema;

/// <summary>
/// Pure CSV rendering extracted from NativeTwinCatRunner.WriteCsv/Csv. The runner still owns
/// the actual File.WriteAllText call (and its encoding/path choice); this only builds the text.
/// </summary>
internal static class CsvExport
{
    internal static string BuildMappingCsv(IReadOnlyList<LinkResult> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("symbol;varName;direction;type;terminal;channel;group;description;plcPath;ioPath");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(";",
                Escape(row.Symbol),
                Escape(row.VarName),
                Escape(row.Direction),
                Escape(row.Type),
                Escape(row.Terminal),
                row.Channel.ToString(CultureInfo.InvariantCulture),
                Escape(row.Group),
                Escape(row.Description),
                Escape(row.PlcPath),
                Escape(row.IoPath)));
        }
        return sb.ToString();
    }

    internal static string Escape(string? value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
