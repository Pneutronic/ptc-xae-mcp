namespace PtcXaeMcp.WinForms.TwinCat;

// The `kind` values of the generic twincat_plc_* tools.
// A kind is the Elementart; the subtype is its Ausprägung inside the kind (FUNCTION_BLOCK, STRUCT …).
internal static class PlcKind
{
    public const string Pou       = "POU";
    public const string Interface = "INTERFACE";
    public const string Dut       = "DUT";
    public const string Gvl       = "GVL";
    public const string Member    = "MEMBER";
    public const string Folder    = "FOLDER";
    public const string TextList  = "TEXTLIST";
    public const string ImagePool = "IMAGEPOOL";
    public const string Visu      = "VISU";
    public const string Recipe    = "RECIPE";
    public const string Trend     = "TREND";
    public const string Uml       = "UML";
    public const string TaskRef   = "TASKREF";
    public const string Library   = "LIBRARY";

    public static readonly IReadOnlyList<string> All =
    [
        Pou, Interface, Dut, Gvl, Member, Folder, TextList, ImagePool, Visu, Recipe, Trend, Uml, TaskRef, Library,
    ];

    public static string Expected => string.Join(", ", All);
}

// What a catalog element carries as content (spec column "Inhalt").
internal enum PlcContent
{
    /// <summary>Pure structure (folder).</summary>
    Structure,
    /// <summary>Declaration text only (DUT, GVL, INTERFACE, interface property).</summary>
    Declaration,
    /// <summary>Implementation only (ACTION, TRANSITION).</summary>
    Implementation,
    /// <summary>Declaration and implementation (POU, METHOD, Get/Set).</summary>
    DeclarationAndImplementation,
    /// <summary>Table content (text lists, image pools, recipes) — reached via XML, risk R4.</summary>
    Table,
    /// <summary>XML content (visualisations, trends, UML) — reached via XML, risk R4.</summary>
    Xml,
    /// <summary>Reference/association (task ↔ PROGRAM, libraries).</summary>
    Reference,
    /// <summary>System node; never edited or deleted through the plc_* tools.</summary>
    System,
    /// <summary>
    /// A code element without any text of its own: the Get/Set accessors of an INTERFACE property. Its
    /// DeclarationText must never be touched — reading it crashed TcXaeShell (live, TC 3.1.4026.22, issue #66).
    /// </summary>
    None,
}

/// <param name="ItemType">TREEITEMTYPES value (TCatSysManagerLib).</param>
/// <param name="Constant">TREEITEMTYPE_&lt;Constant&gt; name in the type library.</param>
/// <param name="Kind">kind of the plc_* tools; null for system nodes.</param>
/// <param name="Subtype">subtype within the kind; null when the kind has none.</param>
/// <param name="Content">what the element carries.</param>
/// <param name="OnInterface">true when the row is the INTERFACE variant of a member (610/612/654/655).</param>
internal sealed record PlcCatalogEntry(
    int ItemType,
    string Constant,
    string? Kind,
    string? Subtype,
    PlcContent Content,
    bool OnInterface = false)
{
    public bool IsSystem => Kind is null;
    public bool HasDeclaration => Content is PlcContent.Declaration or PlcContent.DeclarationAndImplementation;
    public bool HasImplementation => Content is PlcContent.Implementation or PlcContent.DeclarationAndImplementation;
    /// <summary>A code element whose text must never be read or written (<see cref="PlcContent.None"/>).</summary>
    public bool HasNoText => Content is PlcContent.None;
}

// Every PLC tree element of the 600–699 TREEITEMTYPES range, each as one kind/subtype row. The numbers are
// authoritative from the installed type library (PlcElementCatalogTests compares them against it, which keeps
// a mix-up like 629 = parameter list, not persistent list, from coming back). Content per Infosys TE1000
// "Deklaration und Implementierung einer POU auslesen und setzen"; the interface member rows (610/612/654/655)
// are not in that table. Live (issue #66): the interface PROPERTY (612) reads its declaration; the interface Get/Set
// accessors (654/655) have no text, and reading their DeclarationText crashes TcXaeShell.
internal static class PlcElementCatalog
{
    private const PlcContent Decl = PlcContent.Declaration;
    private const PlcContent Impl = PlcContent.Implementation;
    private const PlcContent Both = PlcContent.DeclarationAndImplementation;

    public static readonly IReadOnlyList<PlcCatalogEntry> Entries =
    [
        new(PlcItemType.Folder,           "PLCFOLDER",        PlcKind.Folder,    null,          PlcContent.Structure),

        new(PlcItemType.PouProgram,       "PLCPOUPROG",       PlcKind.Pou,       "PROGRAM",        Both),
        new(PlcItemType.PouFunction,      "PLCPOUFUNC",       PlcKind.Pou,       "FUNCTION",       Both),
        new(PlcItemType.PouFunctionBlock, "PLCPOUFB",         PlcKind.Pou,       "FUNCTION_BLOCK", Both),

        new(PlcItemType.Dut,              "PLCDUTENUM",       PlcKind.Dut,       "ENUM",           Decl),
        new(PlcItemType.DutStruct,        "PLCDUTSTRUCT",     PlcKind.Dut,       "STRUCT",         Decl),
        new(PlcItemType.DutUnion,         "PLCDUTUNION",      PlcKind.Dut,       "UNION",          Decl),
        new(PlcItemType.DutAlias,         "PLCDUTALIAS",      PlcKind.Dut,       "ALIAS",          Decl),

        new(PlcItemType.Gvl,              "PLCGVL",           PlcKind.Gvl,       "NORMAL",         Decl),
        new(PlcItemType.GvlParamList,     "PLCGVLPARAMLIST",  PlcKind.Gvl,       "PARAMLIST",      Decl),

        new(PlcItemType.Interface,        "PLCITF",           PlcKind.Interface, null,             Decl),

        new(PlcItemType.Action,           "PLCACTION",        PlcKind.Member,    "ACTION",         Impl),
        new(PlcItemType.Method,           "PLCMETHOD",        PlcKind.Member,    "METHOD",         Both),
        new(PlcItemType.InterfaceMethod,  "PLCITFMETH",       PlcKind.Member,    "METHOD",         Decl, OnInterface: true),
        new(PlcItemType.Property,         "PLCPROP",          PlcKind.Member,    "PROPERTY",       Decl),
        new(PlcItemType.InterfaceProperty,"PLCITFPROP",       PlcKind.Member,    "PROPERTY",       Decl, OnInterface: true),
        new(PlcItemType.PropertyGet,      "PLCPROPGET",       PlcKind.Member,    "GET",            Both),
        new(PlcItemType.PropertySet,      "PLCPROPSET",       PlcKind.Member,    "SET",            Both),
        // Interface accessors carry no text at all: the interface declares the property, not its accessors.
        // Reading the DeclarationText of one crashed TcXaeShell (live, TC 3.1.4026.22, issue #66).
        new(PlcItemType.InterfaceGet,     "PLCITFPROPGET",    PlcKind.Member,    "GET",            PlcContent.None, OnInterface: true),
        new(PlcItemType.InterfaceSet,     "PLCITFPROPSET",    PlcKind.Member,    "SET",            PlcContent.None, OnInterface: true),
        new(PlcItemType.Transition,       "PLCTRANSITION",    PlcKind.Member,    "TRANSITION",     Impl),

        new(PlcItemType.GlobalTextList,   "PLCGLOBALTEXTLIST",        PlcKind.TextList,  "GLOBAL",      PlcContent.Table),
        new(PlcItemType.TextList,         "PLCTEXTLIST",              PlcKind.TextList,  "LOCAL",       PlcContent.Table),
        new(PlcItemType.TextListEnumeration, "PLCTEXTLISTENUMERATION", PlcKind.TextList, "ENUMERATION", PlcContent.Table),

        new(PlcItemType.GlobalImagePool,  "PLCGLOBALIMAGEPOOL", PlcKind.ImagePool, "GLOBAL",        PlcContent.Table),
        new(PlcItemType.ImagePool,        "PLCIMAGEPOOL",       PlcKind.ImagePool, "LOCAL",         PlcContent.Table),

        new(PlcItemType.VisObj,           "PLCVISOBJ",        PlcKind.Visu,      "VISUALIZATION",  PlcContent.Xml),
        new(PlcItemType.VisMan,           "PLCVISMAN",        PlcKind.Visu,      "MANAGER",        PlcContent.Xml),
        new(PlcItemType.TargetVisu,       "PLCTARGETVISU",    PlcKind.Visu,      "TARGET",         PlcContent.Xml),
        new(PlcItemType.WebVisu,          "PLCWEBVISU",       PlcKind.Visu,      "WEB",            PlcContent.Xml),

        new(PlcItemType.RecipeManager,    "PLCRECIPEMANAGER",    PlcKind.Recipe, "MANAGER",        PlcContent.Table),
        new(PlcItemType.RecipeDefinition, "PLCRECIPEDEFINITION", PlcKind.Recipe, "DEFINITION",     PlcContent.Table),

        new(PlcItemType.TrendObject,      "PLCTRENDOBJECT",   PlcKind.Trend,     "OBJECT",         PlcContent.Xml),
        new(PlcItemType.TrendManager,     "PLCTRENDMANAGER",  PlcKind.Trend,     "MANAGER",        PlcContent.Xml),

        new(PlcItemType.UmlClassDiagram,  "PLCUMLCLASSDIAGRAM", PlcKind.Uml,     "CLASSDIAGRAM",   PlcContent.Xml),

        new(PlcItemType.Task,             "PLCTASK",          PlcKind.TaskRef,   "TASK",           PlcContent.Reference),
        new(PlcItemType.ProgRef,          "PLCPROGREF",       PlcKind.TaskRef,   "PROGRAM",        PlcContent.Reference),

        new(PlcItemType.LibMan,           "PLCLIBMAN",        PlcKind.Library,   "MANAGER",        PlcContent.Reference),
        new(PlcItemType.LibRef,           "PLCLIBREF",        PlcKind.Library,   "REFERENCE",      PlcContent.Reference),
        new(PlcItemType.LibPlaceholder,   "PLCLIBPLACEHOLDER", PlcKind.Library,  "PLACEHOLDER",    PlcContent.Reference),

        new(PlcItemType.PlcApp,           "PLCAPP",           null,              null,          PlcContent.System),
        new(PlcItemType.ProjectInfo,      "PLCPROJECTINFO",   null,              null,          PlcContent.System),
        new(PlcItemType.ExtDataType,      "PLCEXTDATATYPE",   null,              null,          PlcContent.System),
        new(PlcItemType.ExtDataTypeCont,  "PLCEXTDATATYPECONT", null,            null,          PlcContent.System),
        new(PlcItemType.TmcDescription,   "PLCTMCDESCRIPTION", null,             null,          PlcContent.System),
    ];

    private static readonly Dictionary<int, PlcCatalogEntry> ByType = Entries.ToDictionary(e => e.ItemType);

    public static PlcCatalogEntry? ForItemType(int itemType) =>
        ByType.TryGetValue(itemType, out var e) ? e : null;

    /// <summary>
    /// The sections a text access to the node with <paramref name="itemType"/> may touch. A section the element does
    /// not have is never requested: touching the text of an INTERFACE Get/Set accessor crashed TcXaeShell with an
    /// unhandled C++ exception instead of returning a COM error (issue #66). Unknown or unreadable types, and
    /// non-code elements, keep the best-effort access to both sections.
    /// </summary>
    public static (bool Declaration, bool Implementation) ReadableSections(int? itemType) =>
        itemType is int it && ForItemType(it) is { } entry && (entry.HasDeclaration || entry.HasImplementation || entry.HasNoText)
            ? (entry.HasDeclaration, entry.HasImplementation)
            : (true, true);

    /// <summary>The refusal for a text access to a node without text (<see cref="PlcContent.None"/>).</summary>
    public static string NoTextReason(string path) =>
        $"'{path}' is an accessor of an INTERFACE property: it has no declaration or implementation of its own " +
        "(the interface declares only the property; read the property node instead).";

    /// <summary>Subtypes the catalog knows for <paramref name="kind"/> (distinct, catalog order).</summary>
    public static IReadOnlyList<string> SubtypesOf(string kind) =>
        Entries.Where(e => e.Kind == kind && e.Subtype is not null)
               .Select(e => e.Subtype!)
               .Distinct()
               .ToList();
}
