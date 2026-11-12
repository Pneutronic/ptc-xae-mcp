namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>How a property value is written in the archive and passed in JSON.</summary>
internal enum VisuValueKind
{
    /// <summary>Whole number (position, size): JSON number, archive plain number.</summary>
    Integer,
    /// <summary>Static text: JSON string, archive quoted string.</summary>
    Text,
    /// <summary>An IEC expression or variable path evaluated by the visualization: JSON string, archive quoted string.</summary>
    Expression,
    /// <summary>Colour: JSON "#RRGGBB" or "#AARRGGBB", archive UInt32 (ARGB).</summary>
    Color,
}

/// <summary>One property the tools read and write.</summary>
internal sealed record VisuPropertyDef(string Name, VisuValueKind Kind, string Description);

/// <summary>
/// One element type. <paramref name="ElementName"/> and <paramref name="TypeNames"/> are what the file stores
/// (VisualElementName, VisualElementTypeName — XAE writes the type name in two spellings, both are accepted);
/// <paramref name="Shape"/> tells the shapes of the simple element apart. <paramref name="Members"/> maps each
/// property this type has to its member id in the file.
/// </summary>
internal sealed record VisuElementTypeDef(
    string Type,
    string ElementName,
    IReadOnlyList<string> TypeNames,
    string? Shape,
    bool CanCreate,
    bool IsRectangle,
    IReadOnlyDictionary<string, uint> Members,
    string Note)
{
    public bool Has(string property) => Members.ContainsKey(property);
}

/// <summary>
/// The element types and properties of the PLC visualization that the visu tools understand, with the member ids the
/// .TcVIS format keys them by. The format is not documented; every id here was confirmed on files that XAE 3.1.4026.22
/// itself wrote back after loading them, and the expressions compiled against typed variables (see CHANGELOG). Members
/// the catalog does not know are never touched: they stay in the file as they are.
/// </summary>
internal static class VisuCatalog
{
    // ---- member ids (format constants) ----------------------------------------------
    public const uint X = 1649127785;
    public const uint Y = 357335551;
    public const uint Width = 2422045748;
    public const uint Height = 2134141914;
    public const uint CenterX = 550940142;
    public const uint CenterY = 1473355128;
    public const uint ShapeMember = 564465120;
    public const uint TextMember = 390574330;
    /// <summary>Text-list id of the static text. XAE (re)assigns it; a text without one is refused for some types.</summary>
    public const uint TextId = 823443203;
    public const uint TextVariable = 2477733581;
    public const uint Tooltip = 571893170;
    /// <summary>Text-list id of the tooltip, (re)assigned by XAE.</summary>
    public const uint TooltipId = 3438453433;
    public const uint FrameColor = 494569607;
    public const uint FillColor = 2812299069;
    public const uint AlarmFrameColor = 135947015;
    public const uint AlarmFillColor = 493260384;
    public const uint ToggleColorVariable = 2880254039;
    public const uint InvisibleVariable = 2496894244;
    public const uint MoveXVariable = 3560616238;
    public const uint MoveYVariable = 2738717112;
    public const uint RotationVariable = 2858320360;
    public const uint TapVariable = 1999528970;
    public const uint ButtonTapVariable = 1647042231;
    public const uint LineStartX = 1357360684;
    public const uint LineStartY = 444643082;
    public const uint LineEndX = 1837598620;
    public const uint LineEndY = 669032122;

    public const string SimpleTypeName = "VisuFbElemSimple";

    // ---- properties -------------------------------------------------------------------
    public static readonly IReadOnlyList<VisuPropertyDef> Properties =
    [
        new("x", VisuValueKind.Integer, "Left edge in pixels (for a line: the start point)."),
        new("y", VisuValueKind.Integer, "Top edge in pixels (for a line: the start point)."),
        new("width", VisuValueKind.Integer, "Width in pixels (for a line: end point x minus start point x; may be negative)."),
        new("height", VisuValueKind.Integer, "Height in pixels (for a line: end point y minus start point y; may be negative)."),
        new("text", VisuValueKind.Text, "Static text. A format placeholder such as %d, %s or %.2f shows the textVariable. No double quotes."),
        new("textVariable", VisuValueKind.Expression, "Variable whose value the text shows through its format placeholder, e.g. 'MAIN.fbAxis.fPosition'."),
        new("tooltip", VisuValueKind.Text, "Tooltip text. No double quotes."),
        new("frameColor", VisuValueKind.Color, "Frame colour in normal state, '#RRGGBB' or '#AARRGGBB'."),
        new("fillColor", VisuValueKind.Color, "Fill colour in normal state."),
        new("alarmFrameColor", VisuValueKind.Color, "Frame colour while toggleColorVariable is TRUE."),
        new("alarmFillColor", VisuValueKind.Color, "Fill colour while toggleColorVariable is TRUE."),
        new("toggleColorVariable", VisuValueKind.Expression, "BOOL expression; TRUE switches to the alarm colours."),
        new("invisibleVariable", VisuValueKind.Expression, "BOOL expression; TRUE hides the element."),
        new("moveXVariable", VisuValueKind.Expression, "Integer expression that moves the element horizontally (pixels, absolute movement)."),
        new("moveYVariable", VisuValueKind.Expression, "Integer expression that moves the element vertically (pixels, absolute movement)."),
        new("rotationVariable", VisuValueKind.Expression, "Numeric expression: rotation angle in degrees (absolute movement)."),
        new("tapVariable", VisuValueKind.Expression, "BOOL variable that is TRUE while the element is pressed (input 'Tap')."),
    ];

    private static readonly Dictionary<string, VisuPropertyDef> PropertyByName =
        Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public static VisuPropertyDef? Property(string name) => PropertyByName.GetValueOrDefault(name);

    private static Dictionary<string, uint> Map(params (string Name, uint Id)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Id, StringComparer.Ordinal);

    private static readonly (string, uint)[] Geometry =
        [("x", X), ("y", Y), ("width", Width), ("height", Height)];

    private static readonly (string, uint)[] TextProps =
        [("text", TextMember), ("textVariable", TextVariable)];

    private static readonly (string, uint)[] Colors =
        [("frameColor", FrameColor), ("fillColor", FillColor), ("alarmFrameColor", AlarmFrameColor), ("alarmFillColor", AlarmFillColor)];

    private static readonly (string, uint)[] Movement =
        [("moveXVariable", MoveXVariable), ("moveYVariable", MoveYVariable), ("rotationVariable", RotationVariable)];

    private static (string, uint)[] Common(params (string, uint)[] more) =>
        [.. Geometry, ("tooltip", Tooltip), ("invisibleVariable", InvisibleVariable), .. more];

    private static VisuElementTypeDef Simple(string type, string elementName, string shape) => new(
        type, elementName, [SimpleTypeName, "VISUFBELEMSIMPLE"], shape, CanCreate: true, IsRectangle: true,
        Map(Common([.. TextProps, .. Colors, ("toggleColorVariable", ToggleColorVariable), .. Movement, ("tapVariable", TapVariable)])),
        "");

    public static readonly IReadOnlyList<VisuElementTypeDef> Types =
    [
        Simple("Rectangle", "Rectangle", "VISU_ST_RECTANGLE"),
        Simple("RoundedRectangle", "Rounded Rectangle", "VISU_ST_ROUNDRECT"),
        Simple("Ellipse", "Ellipse", "VISU_ST_CIRCLE"),
        new("Line", "Line", ["VisuFbElemLine", "VISUFBELEMLINE"], null, CanCreate: true, IsRectangle: false,
            Map(Common()),
            "x/y is the start point, x+width/y+height the end point."),
        new("Label", "Label", ["VisuFbLabel", "VISUFBLABEL"], null, CanCreate: true, IsRectangle: true,
            Map(Common([.. TextProps, .. Colors, ("toggleColorVariable", ToggleColorVariable), ("tapVariable", TapVariable)])),
            ""),
        new("TextField", "Textfield", ["VisuFbElemTextfield", "VISUFBELEMTEXTFIELD"], null, CanCreate: true, IsRectangle: true,
            Map(Common([.. TextProps, .. Colors, ("toggleColorVariable", ToggleColorVariable), ("tapVariable", TapVariable)])),
            "Shows textVariable through the format in text, e.g. text '%.2f'."),
        new("Button", "Button", ["VisuFbElemButton", "VISUFBELEMBUTTON"], null, CanCreate: true, IsRectangle: true,
            Map(Common([.. TextProps, ("toggleColorVariable", ToggleColorVariable), ("tapVariable", ButtonTapVariable)])),
            "tapVariable is TRUE while the button is pressed."),
        new("Polygon", "Polygon", ["VisuFbElemPolygon", "VISUFBELEMPOLYGON"], null, CanCreate: false, IsRectangle: false,
            Map(Common([.. Colors, ("toggleColorVariable", ToggleColorVariable), .. Movement])),
            "Read and edit only: its points live in a type-node structure the tools do not write. Create it in the XAE " +
            "visualization editor (or copy an existing one with copyFrom)."),
        new("Frame", "Frame", ["VisuFbFrame", "VISUFBFRAME"], null, CanCreate: false, IsRectangle: true,
            Map(Common()),
            "Read and edit only: XAE builds the frame's reference and parameter structure in its editor. Create one frame there, " +
            "then position copies of it with copyFrom and set their parameters with frameParameters."),
    ];

    private static readonly Dictionary<string, VisuElementTypeDef> TypeByName =
        Types.ToDictionary(t => t.Type, StringComparer.OrdinalIgnoreCase);

    public static VisuElementTypeDef? TypeByApiName(string name) => TypeByName.GetValueOrDefault(name);

    public static string TypeNamesList => string.Join(", ", Types.Select(t => t.Type));

    /// <summary>
    /// The type of a stored element: by VisualElementTypeName (either spelling) and, for the simple element, by its
    /// shape. Null for an element the catalog does not know (it is reported as unsupported and left as it is).
    /// </summary>
    public static VisuElementTypeDef? Identify(string? typeName, string? shape)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        var candidates = Types.Where(t => t.TypeNames.Any(n => string.Equals(n, typeName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.All(c => c.Shape is null)) return candidates[0];
        return candidates.FirstOrDefault(c => string.Equals(c.Shape, shape, StringComparison.Ordinal));
    }

    /// <summary>The TypeList entries a new element needs (type name → type id), all format constants.</summary>
    public static readonly IReadOnlyDictionary<string, string> ElementTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ArrayList"] = "System.Collections.ArrayList",
        ["ComplexInput"] = "{1de566f6-72a7-494c-9353-9a418172c96e}",
        ["GenericVisualElem"] = "{f86c2928-8614-4cca-824b-e819ac4d58c4}",
        ["Guid"] = "System.Guid",
        ["Hashtable"] = "System.Collections.Hashtable",
        ["VisualElemMember"] = "{c694e3a2-5c0b-4177-ab35-cb06bd5a6a02}",
        ["VisualElemMemberCollection"] = "{a4b83bea-3742-489c-9fe8-d96d68dba7ab}",
        ["VisualElemMemberList"] = "{17e26cd1-bb9b-47fe-a3d5-18fcd63b9c96}",
    };
}
