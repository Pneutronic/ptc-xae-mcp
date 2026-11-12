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
/// The element types and properties of the PLC visualization that the visu tools write, with the member ids the .TcVIS
/// format keys them by. The format is not documented. A member id is the CRC-32 of the member's path in the element's
/// function block (e.g. CRC-32("m_StaticPosition.iX") = 1649127785, "m_StaticTexts.pstText" = 390574330); for the
/// dynamic properties the path is not known and the id comes from sample files. Every property a type offers here was
/// seen working in the integrated visualization of XAE 3.1.4026.22 on a running PLC (screenshots, see CHANGELOG); what
/// did not show is not offered. Members the catalog does not know are never touched: they stay in the file as they are.
/// </summary>
internal static class VisuCatalog
{
    // ---- member ids (format constants) ----------------------------------------------
    public const uint X = 1649127785;                    // m_StaticPosition.iX
    public const uint Y = 357335551;                     // m_StaticPosition.iY
    public const uint Width = 2422045748;                // m_StaticPosition.iWidth
    public const uint Height = 2134141914;               // m_StaticPosition.iHeight
    public const uint CenterX = 550940142;               // m_StaticCenter.iX
    public const uint CenterY = 1473355128;              // m_StaticCenter.iY
    public const uint ShapeMember = 564465120;           // m_StaticType
    public const uint TextMember = 390574330;            // m_StaticTexts.pstText
    /// <summary>Text-list id of the static text (_pstTextId). XAE files it; a Button text without one is refused.</summary>
    public const uint TextId = 823443203;
    public const uint TextVariable = 2477733581;
    public const uint FrameColor = 494569607;
    public const uint FillColor = 2812299069;
    public const uint AlarmFrameColor = 135947015;
    public const uint AlarmFillColor = 493260384;
    /// <summary>Toggle colour. Sample files store the placeholder "&lt;toggle/tap variable&gt;" here (= follow the input variable).</summary>
    public const uint ToggleColorVariable = 1999528970;
    public const uint InvisibleVariable = 2880254039;
    public const uint FillColorVariable = 401380312;
    public const uint FrameColorVariable = 3450524324;
    public const uint TextColorVariable = 55702900;
    public const uint MoveXVariable = 3560616238;
    public const uint MoveYVariable = 2738717112;
    public const uint RotationVariable = 2858320360;
    /// <summary>Colour of a line (m_StaticColors.dwNormalColor); frameColor has no effect on a line.</summary>
    public const uint LineColor = 2341735680;
    /// <summary>The point list of a line (m_StaticPositionPolygon), a type-node tree. Without it the line is not drawn.</summary>
    public const uint LinePoints = 1051212449;
    public const uint LineAngle = 3553112287;            // m_iAngle
    // The two points as XAE's own type-node tree names them: point[0] = (iX, iY), point[1] = (iX, iY).
    public const uint LineStartX = 1357360684;
    public const uint LineStartY = 669032122;
    public const uint LineEndX = 1837598620;
    public const uint LineEndY = 444643082;
    /// <summary>The referenced visualizations of a frame (m_References), a type-node tree.</summary>
    public const uint FrameReferences = 363316305;
    /// <summary>The first reference with its parameter values (m_References.pReferences.[0]).</summary>
    public const uint FrameFirstReference = 2473092364;

    // Tap and toggle are not members of the element but entries of its ConfiguredComplexInputs list (Visu_TapInput,
    // Visu_ToggleInput), each with the variable in its own member pVariable (1186196937). These two ids only key the
    // properties in the catalog; they never appear in a file.
    public const uint TapInput = 1;
    public const uint ToggleInput = 2;
    /// <summary>pVariable: the variable of a tap or toggle input.</summary>
    public const uint InputVariable = 1186196937;

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
        new("frameColor", VisuValueKind.Color, "Frame colour in normal state, '#RRGGBB' or '#AARRGGBB'."),
        new("fillColor", VisuValueKind.Color, "Fill colour in normal state."),
        new("alarmFrameColor", VisuValueKind.Color, "Frame colour while toggleColorVariable is TRUE."),
        new("alarmFillColor", VisuValueKind.Color, "Fill colour while toggleColorVariable is TRUE."),
        new("toggleColorVariable", VisuValueKind.Expression, "BOOL expression; TRUE switches to the alarm colours."),
        new("invisibleVariable", VisuValueKind.Expression, "BOOL expression; TRUE hides the element."),
        new("fillColorVariable", VisuValueKind.Expression, "DWORD expression giving the fill colour as 16#AARRGGBB, e.g. 'SEL(MAIN.bOn, 16#FFC0C0C0, 16#FF00C000)'; overrides the fill colours."),
        new("frameColorVariable", VisuValueKind.Expression, "DWORD expression giving the frame colour as 16#AARRGGBB; overrides the frame colours."),
        new("textColorVariable", VisuValueKind.Expression, "DWORD expression giving the text colour as 16#AARRGGBB."),
        new("moveXVariable", VisuValueKind.Expression, "Integer expression that moves the element horizontally (pixels, absolute movement)."),
        new("moveYVariable", VisuValueKind.Expression, "Integer expression that moves the element vertically (pixels, absolute movement)."),
        new("rotationVariable", VisuValueKind.Expression, "Numeric expression: rotation angle in degrees around the element's centre."),
        new("lineColor", VisuValueKind.Color, "Colour of a line."),
        new("tapVariable", VisuValueKind.Expression, "BOOL variable that is TRUE while the element is pressed (input 'Tap')."),
        new("toggleVariable", VisuValueKind.Expression, "BOOL variable that each click inverts (input 'Toggle')."),
    ];

    private static readonly Dictionary<string, VisuPropertyDef> PropertyByName =
        Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public static VisuPropertyDef? Property(string name) => PropertyByName.GetValueOrDefault(name);

    private static Dictionary<string, uint> Map(params (string Name, uint Id)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Id, StringComparer.Ordinal);

    private static readonly (string, uint)[] Geometry =
        [("x", X), ("y", Y), ("width", Width), ("height", Height), ("invisibleVariable", InvisibleVariable)];

    private static readonly (string, uint)[] TextProps =
        [("text", TextMember), ("textVariable", TextVariable)];

    private static readonly (string, uint)[] Colors =
        [("frameColor", FrameColor), ("fillColor", FillColor), ("alarmFrameColor", AlarmFrameColor), ("alarmFillColor", AlarmFillColor),
         ("toggleColorVariable", ToggleColorVariable), ("fillColorVariable", FillColorVariable), ("frameColorVariable", FrameColorVariable)];

    private static readonly (string, uint)[] Move =
        [("moveXVariable", MoveXVariable), ("moveYVariable", MoveYVariable)];

    private static readonly (string, uint)[] Inputs =
        [("tapVariable", TapInput), ("toggleVariable", ToggleInput)];

    private static VisuElementTypeDef Simple(string type, string elementName, string shape) => new(
        type, elementName, [SimpleTypeName, "VISUFBELEMSIMPLE"], shape, CanCreate: true, IsRectangle: true,
        Map([.. Geometry, .. TextProps, .. Colors, ("textColorVariable", TextColorVariable), .. Move, ("rotationVariable", RotationVariable), .. Inputs]),
        "");

    public static readonly IReadOnlyList<VisuElementTypeDef> Types =
    [
        Simple("Rectangle", "Rectangle", "VISU_ST_RECTANGLE"),
        Simple("RoundedRectangle", "Rounded Rectangle", "VISU_ST_ROUNDRECT"),
        Simple("Ellipse", "Ellipse", "VISU_ST_CIRCLE"),
        new("Line", "Line", ["VisuFbElemLine", "VISUFBELEMLINE"], null, CanCreate: true, IsRectangle: false,
            Map([.. Geometry, ("lineColor", LineColor), .. Move]),
            "x/y is the start point, x+width/y+height the end point."),
        new("Label", "Label", ["VisuFbLabel", "VISUFBLABEL"], null, CanCreate: true, IsRectangle: true,
            Map([.. Geometry, ("text", TextMember)]),
            "Static text only: a label shows no text variable, colours or movement."),
        new("TextField", "Textfield", ["VisuFbElemTextfield", "VISUFBELEMTEXTFIELD"], null, CanCreate: true, IsRectangle: true,
            Map([.. Geometry, .. TextProps, .. Colors, ("textColorVariable", TextColorVariable)]),
            "Shows textVariable through the format in text, e.g. text '%.2f'. No movement or rotation."),
        new("Button", "Button", ["VisuFbElemButton", "VISUFBELEMBUTTON"], null, CanCreate: true, IsRectangle: true,
            Map([.. Geometry, .. TextProps, ("textColorVariable", TextColorVariable), .. Move, .. Inputs]),
            "Drawn in the button style: no fill, frame or alarm colours. Drawn pressed while its tap or toggle variable is TRUE."),
        new("Polygon", "Polygon", ["VisuFbElemPolygon", "VISUFBELEMPOLYGON"], null, CanCreate: false, IsRectangle: false,
            Map([.. Geometry]),
            "Read only: its points live in a type-node structure the tools do not write."),
        new("Frame", "Frame", ["VisuFbFrame", "VISUFBFRAME"], null, CanCreate: true, IsRectangle: true,
            Map([.. Geometry]),
            "Shows another visualization of the project: \"visualization\": \"<name>\" and \"parameters\": { \"<interface variable>\": " +
            "\"<expression>\" } for every VAR_IN_OUT of its interface (other interface sections are not supported). One reference per frame."),
    ];

    private static readonly Dictionary<string, VisuElementTypeDef> TypeByName =
        Types.ToDictionary(t => t.Type, StringComparer.OrdinalIgnoreCase);

    public static VisuElementTypeDef? TypeByApiName(string name) => TypeByName.GetValueOrDefault(name);

    public static string TypeNamesList => string.Join(", ", Types.Select(t => t.Type));

    /// <summary>The properties that are stored as complex inputs, not as members.</summary>
    public static bool IsInput(uint id) => id is TapInput or ToggleInput;

    public static string CreatableTypeNamesList => string.Join(", ", Types.Where(t => t.CanCreate).Select(t => t.Type));

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
