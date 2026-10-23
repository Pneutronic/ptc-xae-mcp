using System.Globalization;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Type, size and direction of one variable item, read from the <c>&lt;VarDef&gt;</c> of its
/// <c>ITcSmTreeItem.ProduceXml</c> (e.g. <c>&lt;VarType&gt;INT&lt;/VarType&gt;&lt;VarBitSize&gt;16&lt;/VarBitSize&gt;
/// &lt;VarInOut&gt;0&lt;/VarInOut&gt;</c>).
/// </summary>
/// <param name="TypeName">Text of <c>VarType</c> (BIT, INT, REAL32, …).</param>
/// <param name="BitSize"><c>VarBitSize</c>.</param>
/// <param name="InOut"><c>VarInOut</c>: 0 input, 1 output; other values are kept as read.</param>
internal sealed record VariableInfo(string TypeName, int BitSize, int InOut)
{
    public string Direction => InOut switch
    {
        0 => "input",
        1 => "output",
        _ => $"direction {InOut.ToString(CultureInfo.InvariantCulture)}",
    };

    /// <summary>Null when the XML has no usable <c>VarDef</c> (the node is not a variable, or the XML is unreadable).</summary>
    public static VariableInfo? TryParse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            XElement? def = XDocument.Parse(xml).Descendants("VarDef").FirstOrDefault();
            string? type = def?.Element("VarType")?.Value.Trim();
            if (string.IsNullOrEmpty(type)) return null;
            if (!int.TryParse(def!.Element("VarBitSize")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits)
                || !int.TryParse(def.Element("VarInOut")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int inOut))
                return null;
            return new VariableInfo(type, bits, inOut);
        }
        catch (System.Xml.XmlException) { return null; }
    }
}

/// <summary>What a tree lookup of one <c>LinkVariables</c> argument found.</summary>
/// <param name="ItemType">TREEITEMTYPE of the node; -1 when it was not found or not readable.</param>
/// <param name="Variable">Null when the node is not found or has no readable variable definition.</param>
internal sealed record LinkEndpoint(string Path, bool Found, int ItemType, VariableInfo? Variable)
{
    public static LinkEndpoint Missing(string path) => new(path, false, -1, null);
}

/// <summary>
/// Turns a failed <c>ITcSysManager::LinkVariables</c> into a message that names the cause
/// (issue: diagnostic hints). Only states what the probed items and the HRESULT show; whatever
/// cannot be read stays out of the message instead of being guessed.
/// </summary>
internal static class LinkFailureDiagnosis
{
    public const int ItemNotFound = unchecked((int)0x98510001);
    public const int InvalidItemType = unchecked((int)0x98510002);
    public const int MismatchingItems = unchecked((int)0x98510004);
    public const int InvalidArg = unchecked((int)0x80070057);

    /// <summary>TREEITEMTYPE_VARIABLE: the only item type LinkVariables accepts.</summary>
    public const int VariableItemType = 7;

    public static string? CodeName(int hresult) => hresult switch
    {
        ItemNotFound => "TSM_E_ITEMNOTFOUND",
        InvalidItemType => "TSM_E_INVALIDITEMTYPE",
        MismatchingItems => "TSM_E_MISMATCHINGITEMS",
        InvalidArg => "E_INVALIDARG",
        _ => null,
    };

    /// <summary>The HRESULT behind a COM call made through <c>dynamic</c> (unwraps the reflection wrapper).</summary>
    public static int HResultOf(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.HResult : ex.HResult;

    /// <summary>The message used when nothing more than the code is known.</summary>
    public static string Fallback(int hresult, string comMessage)
    {
        string code = CodeName(hresult) is { } name
            ? $"{name} 0x{(uint)hresult:X8}"
            : $"0x{(uint)hresult:X8}";
        return $"LinkVariables failed: {comMessage} ({code})";
    }

    public static string Describe(int hresult, string comMessage, LinkEndpoint first, LinkEndpoint second)
    {
        switch (hresult)
        {
            case ItemNotFound:
            {
                var missing = new[] { first, second }.Where(e => !e.Found).ToList();
                if (missing.Count == 0) break;
                return $"LinkVariables failed (TSM_E_ITEMNOTFOUND): " +
                       string.Join(" ", missing.Select(e => $"'{e.Path}' does not resolve to a tree item.")) +
                       " Get valid paths from twincat_get_fieldbus_topology (TIID^...) and twincat_list_pous (TIPC^...).";
            }
            case InvalidItemType:
            {
                var notVariables = new[] { first, second }
                    .Where(e => e.Found && e.ItemType != VariableItemType).ToList();
                if (notVariables.Count == 0) break;
                return $"LinkVariables failed (TSM_E_INVALIDITEMTYPE): " +
                       string.Join(" ", notVariables.Select(e =>
                           $"'{e.Path}' is not a variable (item type {e.ItemType.ToString(CultureInfo.InvariantCulture)}, " +
                           $"variables are {VariableItemType}).")) +
                       " Link the variable below the terminal or task node, not the node itself.";
            }
            case MismatchingItems:
            {
                if (first.Variable is not { } a || second.Variable is not { } b) break;
                string text =
                    $"LinkVariables refused the link: {Describe(first.Path, a)},{Describe(second.Path, b)}.";
                if (a.TypeName != b.TypeName || a.BitSize != b.BitSize)
                    text += $" The terminal maps this channel as {TerminalTypeOf(first, second)}: change its process data / PDO " +
                            $"assignment, or link a {TerminalTypeOf(first, second)} variable (or pass bitOffset/size for a partial link).";
                if (a.InOut == b.InOut && a.InOut is 0 or 1 && SameSide(first.Path, second.Path))
                    text += $" Both are {a.Direction}s: inputs link to outputs only.";
                return text;
            }
        }
        return Fallback(hresult, comMessage);
    }

    private static string Describe(string path, VariableInfo v) =>
        $"'{path}' is {v.TypeName} ({v.BitSize.ToString(CultureInfo.InvariantCulture)} bit, {v.Direction})";

    // The "terminal maps this channel as <type>" sentence names the IO-side type. When neither
    // side is under TIID there is no terminal, so the first variable's type is the reference.
    private static string TerminalTypeOf(LinkEndpoint first, LinkEndpoint second)
    {
        if (IsIoPath(first.Path)) return first.Variable!.TypeName;
        if (IsIoPath(second.Path)) return second.Variable!.TypeName;
        return first.Variable!.TypeName;
    }

    private static bool IsIoPath(string path) => Root(path) == "TIID";

    // Two variables on the same side (both under TIID, both under TIPC) with the same direction are an
    // input/input or output/output pair; across the I/O and the task side the same direction is the
    // normal pairing, so no direction claim is made there.
    private static bool SameSide(string a, string b) => Root(a) == Root(b);

    private static string Root(string path)
    {
        int caret = path.IndexOf('^');
        return (caret < 0 ? path : path[..caret]).Trim().ToUpperInvariant();
    }
}
