using System.Globalization;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// SoE parameter number (IDN) as 16-bit value: bit 15 = P (else S), bits 12-14 = parameter set, bits 0-11 = number.
/// Notation <c>S-0-0015</c> / <c>P-0-3231</c> (type-set-number).
/// </summary>
internal static class SoeIdn
{
    private const int PBit = 0x8000;

    private static readonly Regex Notation = new(@"^([SP])-([0-7])-(\d{1,4})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static int ParameterSet(int raw) => (raw >> 12) & 0x7;

    public static string Format(int raw)
    {
        raw &= 0xFFFF;
        return string.Create(CultureInfo.InvariantCulture,
            $"{((raw & PBit) != 0 ? 'P' : 'S')}-{ParameterSet(raw)}-{raw & 0xFFF:D4}");
    }

    /// <summary>Parses the notation ("S-0-0015", "P-0-3231"), a decimal or a 0x/#x hex number (0..65535).</summary>
    public static bool TryParse(string? text, out int raw)
    {
        raw = 0;
        string s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return false;

        var m = Notation.Match(s);
        if (m.Success)
        {
            int number = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (number > 0xFFF) return false;
            int set = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            raw = (char.ToUpperInvariant(m.Groups[1].Value[0]) == 'P' ? PBit : 0) | (set << 12) | number;
            return true;
        }

        if (EtherCatProjection.ParseNumber(s) is { } n and >= 0 and <= 0xFFFF)
        {
            raw = (int)n;
            return true;
        }
        return false;
    }
}

/// <summary>The element bits of a SoE access (Beckhoff FB_EcSoeRead nElement): 0x01 data status ... 0x80 default.</summary>
internal static class SoeElement
{
    public const int DataStatus = 0x01, ElementName = 0x02, Attribute = 0x04, Unit = 0x08, Min = 0x10, Max = 0x20, Value = 0x40, Default = 0x80;

    private static readonly (int Bit, string Name)[] Names =
    [
        (DataStatus, "dataStatus"), (ElementName, "name"), (Attribute, "attribute"), (Unit, "unit"),
        (Min, "min"), (Max, "max"), (Value, "value"), (Default, "default"),
    ];

    /// <summary>"value" for 0x40; several bits are joined with a comma, no bit gives "none".</summary>
    public static string Name(int mask)
    {
        var names = Names.Where(n => (mask & n.Bit) != 0).Select(n => n.Name).ToList();
        return names.Count == 0 ? "none" : string.Join(',', names);
    }

    public static bool TryParse(string? text, out int mask)
    {
        string s = (text ?? string.Empty).Trim();
        foreach (var (bit, name) in Names)
            if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) { mask = bit; return true; }
        mask = 0;
        return false;
    }
}
