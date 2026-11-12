using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>A refused visualization request (bad JSON, unknown property, wrong value); nothing was changed.</summary>
internal sealed class VisuRequestException(string message) : Exception(message);

/// <summary>
/// Applies the JSON requests of twincat_visu_apply and twincat_visu_edit_elements to a <see cref="VisuDocument"/>.
/// Pure and COM-free: everything is validated before the first change, so a refused request leaves the document as it
/// was. Elements the request does not name — including ones the catalog does not know — are not touched.
/// </summary>
internal static class VisuEditor
{
    private const short MinCoordinate = short.MinValue;
    private const short MaxCoordinate = short.MaxValue;

    private static readonly string[] Geometry = ["x", "y", "width", "height"];

    // ---- format gate ------------------------------------------------------------------

    /// <summary>
    /// Null when the file is in a format the tools have verified for writing, else why not. Verified: TcPlcObject
    /// Version 1.1.0.1 as written by TwinCAT 3.1.4024 and 4026 (XAE 4026.22 loads both and writes them back with the same
    /// member ids), each element with a member list, every archive type declared.
    /// </summary>
    public static string? WriteProblem(VisuDocument doc)
    {
        if (doc.ObjectVersion != "1.1.0.1")
            return $"the file format version is '{doc.ObjectVersion}', verified is only 1.1.0.1";
        if (doc.ProductVersion is { } pv && !pv.StartsWith("3.1.4024.", StringComparison.Ordinal) && !pv.StartsWith("3.1.4026.", StringComparison.Ordinal))
            return $"the file was written by TwinCAT {pv}; verified are files from TwinCAT 3.1.4024 and 3.1.4026";
        if (doc.InterfaceText is null)
            return "the file has no interface field (TextBlobForSerialisation or TextLines)";
        if (VisuDocument.Field(doc.VisualObject, "UniqueIdGenerator") is null)
            return "the file has no UniqueIdGenerator";
        foreach (var e in doc.Elements)
        {
            if (VisuElements.MemberList(e) is null || VisuDocument.Field(e, "VisualElementId") is null)
                return $"element '{VisuDocument.StringValue(VisuDocument.Field(e, "VisualElementIdentifier"))}' has an element structure the tools do not know";
        }
        var duplicate = doc.Elements.GroupBy(e => ElementId(e)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return $"element id {duplicate.Key?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"} occurs {duplicate.Count()} times";
        var undeclared = doc.UndeclaredTypes();
        if (undeclared.Count > 0)
            return $"the archive uses types its TypeList does not declare ({string.Join(", ", undeclared)})";
        foreach (var (name, value) in VisuCatalog.ElementTypes)
        {
            var existing = doc.TypeList.Elements("Type").FirstOrDefault(t => (string?)t.Attribute("n") == name);
            if (existing is not null && existing.Value != value)
                return $"the TypeList maps '{name}' to {existing.Value}, expected {value}";
        }
        return null;
    }

    // ---- requests -------------------------------------------------------------------------

    /// <summary>One validated element of a request: a new element, a copy, or the changes for an existing one.</summary>
    internal sealed record ElementSpec(
        VisuElementTypeDef? Def,
        int? CopyFrom,
        IReadOnlyList<KeyValuePair<string, object?>> Properties,
        VisuReferenceTarget? Visualization = null,
        IReadOnlyList<KeyValuePair<string, string>>? FrameParameters = null)
    {
        public IReadOnlyList<KeyValuePair<string, string>> Parameters => FrameParameters ?? [];

        /// <summary>The names of what the spec changes, for the tool result.</summary>
        public IEnumerable<string> ChangedNames =>
            Properties.Select(p => p.Key)
                .Concat(Visualization is null ? [] : ["visualization"])
                .Concat(Parameters.Select(p => "parameters." + p.Key));
    }

    /// <summary>Finds a visualization of the project by name for a frame; null when there is none.</summary>
    internal delegate VisuReferenceTarget? VisuResolver(string name);

    internal sealed record Operation(string Op, int? Id, ElementSpec? Element);

    /// <summary>What a request did, for the tool result.</summary>
    internal sealed record Outcome(IReadOnlyList<string> Lines, IReadOnlyList<int> AddedIds);

    /// <summary>
    /// Replaces every element of the page with <paramref name="page"/>'s "elements" and, when given, the interface.
    /// A copyFrom refers to an element of the page as it was before.
    /// </summary>
    public static Outcome ApplyPage(VisuDocument doc, JsonElement page, VisuResolver? resolve = null)
    {
        if (page.ValueKind != JsonValueKind.Object)
            throw new VisuRequestException("The page must be a JSON object: { \"interface\": \"VAR_IN_OUT … END_VAR\", \"elements\": [ … ] }.");
        foreach (var p in page.EnumerateObject())
            if (p.Name is not ("interface" or "elements"))
                throw new VisuRequestException($"Unknown page field '{p.Name}'. A page has 'interface' (optional) and 'elements'.");

        string? iface = ReadInterface(page);
        if (!page.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            throw new VisuRequestException("The page needs an 'elements' array (it may be empty).");

        var before = doc.Elements.ToDictionary(e => ElementId(e) ?? -1);
        var specs = new List<ElementSpec>();
        int index = 0;
        foreach (var e in elements.EnumerateArray())
        {
            string where = $"elements[{index++}]";
            specs.Add(ParseElement(e, where, before, forUpdate: null, resolve, doc.Name));
        }

        var lines = new List<string>();
        var added = new List<int>();
        int removed = before.Count;
        // Copies are taken before the old elements go.
        var sources = specs.Where(s => s.CopyFrom is not null).Select(s => s.CopyFrom!.Value).Distinct()
            .ToDictionary(id => id, id => new XElement(before[id]));
        doc.RemoveAllElements();
        if (iface is not null) doc.SetInterfaceText(iface);
        foreach (var spec in specs)
            added.Add(AddElement(doc, spec, sources));

        lines.Add($"Replaced {removed} element{(removed == 1 ? "" : "s")} with {added.Count}" +
                  (added.Count == 0 ? "." : $" (ids {string.Join(", ", added)})."));
        if (iface is not null) lines.Add("Interface written.");
        return new Outcome(lines, added);
    }

    /// <summary>
    /// Runs the operations of twincat_visu_edit_elements in order: add (new or copyFrom), update (set), remove. The
    /// whole list is validated against the page first; ids refer to the page as it was before the call.
    /// </summary>
    public static Outcome ApplyOperations(VisuDocument doc, JsonElement operations, string? interfaceText, VisuResolver? resolve = null)
    {
        if (operations.ValueKind != JsonValueKind.Array)
            throw new VisuRequestException("operations must be a JSON array, e.g. [{\"op\":\"update\",\"id\":3,\"set\":{\"x\":40}}].");

        var before = doc.Elements.ToDictionary(e => ElementId(e) ?? -1);
        var ops = new List<Operation>();
        var touched = new HashSet<int>();
        int index = 0;
        foreach (var op in operations.EnumerateArray())
        {
            string where = $"operations[{index++}]";
            if (op.ValueKind != JsonValueKind.Object) throw new VisuRequestException($"{where} must be an object.");
            string kind = RequiredString(op, "op", where).ToLowerInvariant();
            foreach (var p in op.EnumerateObject())
            {
                bool allowed = kind switch
                {
                    "add" => p.Name is "op" or "element",
                    "update" => p.Name is "op" or "id" or "set",
                    "remove" => p.Name is "op" or "id",
                    _ => true,
                };
                if (!allowed) throw new VisuRequestException($"{where}: '{p.Name}' does not belong to op '{kind}'.");
            }
            switch (kind)
            {
                case "add":
                    if (!op.TryGetProperty("element", out var el)) throw new VisuRequestException($"{where}: op 'add' needs 'element'.");
                    ops.Add(new Operation("add", null, ParseElement(el, $"{where}.element", before, forUpdate: null, resolve, doc.Name)));
                    break;
                case "update":
                {
                    int id = ExistingId(op, where, before);
                    if (!touched.Add(id)) throw new VisuRequestException($"{where}: element {id} is named twice; put all its changes in one operation.");
                    if (!op.TryGetProperty("set", out var set)) throw new VisuRequestException($"{where}: op 'update' needs 'set'.");
                    var view = VisuElements.Read(before[id]);
                    if (view.Def is null)
                        throw new VisuRequestException($"{where}: element {id} is a '{view.ElementName}' ({view.TypeName}), which the tools do not support; it can only be removed.");
                    if (!view.Def.CanCreate)
                        throw new VisuRequestException($"{where}: element {id} is a {view.Def.Type}, which the tools read but do not change ({view.Def.Note}); it can only be removed.");
                    var spec = ParseElement(set, $"{where}.set", before, forUpdate: view.Def, resolve, doc.Name, before[id]);
                    if (spec.Properties.Count == 0 && spec.Visualization is null && spec.Parameters.Count == 0)
                        throw new VisuRequestException($"{where}: 'set' names no property.");
                    ops.Add(new Operation("update", id, spec));
                    break;
                }
                case "remove":
                {
                    int id = ExistingId(op, where, before);
                    if (!touched.Add(id)) throw new VisuRequestException($"{where}: element {id} is named twice.");
                    ops.Add(new Operation("remove", id, null));
                    break;
                }
                default:
                    throw new VisuRequestException($"{where}: unknown op '{kind}'. Expected add, update or remove.");
            }
        }
        if (interfaceText is not null) ValidateText(interfaceText, "interface");
        if (ops.Count == 0 && interfaceText is null)
            throw new VisuRequestException("Nothing to do: operations is empty and no interface was given.");

        var sources = ops.Where(o => o.Element?.CopyFrom is not null).Select(o => o.Element!.CopyFrom!.Value).Distinct()
            .ToDictionary(id => id, id => new XElement(before[id]));
        var lines = new List<string>();
        var added = new List<int>();
        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case "add":
                    int newId = AddElement(doc, op.Element!, sources);
                    added.Add(newId);
                    lines.Add(op.Element!.CopyFrom is int src
                        ? $"Added element {newId} as a copy of {src}."
                        : $"Added {op.Element.Def!.Type} {newId}.");
                    break;
                case "update":
                    var element = before[op.Id!.Value];
                    var def = VisuElements.Read(element).Def!;
                    Change(doc, element, def, op.Element!);
                    lines.Add($"Updated element {op.Id}: {string.Join(", ", op.Element!.ChangedNames)}.");
                    break;
                case "remove":
                    doc.RemoveElement(before[op.Id!.Value]);
                    lines.Add($"Removed element {op.Id}.");
                    break;
            }
        }
        if (interfaceText is not null)
        {
            doc.SetInterfaceText(interfaceText);
            lines.Add("Interface written.");
        }
        return new Outcome(lines, added);
    }

    /// <summary>
    /// The checks that need no page: the shape of the request and every new element that is not a copy (type,
    /// properties, values). Runs before the tools touch the XAE; the full validation against the page follows.
    /// </summary>
    public static void Precheck(JsonElement request, bool isPage)
    {
        var none = new Dictionary<int, XElement>();
        if (isPage)
        {
            if (request.ValueKind != JsonValueKind.Object)
                throw new VisuRequestException("The page must be a JSON object: { \"interface\": \"VAR_IN_OUT … END_VAR\", \"elements\": [ … ] }.");
            foreach (var p in request.EnumerateObject())
                if (p.Name is not ("interface" or "elements"))
                    throw new VisuRequestException($"Unknown page field '{p.Name}'. A page has 'interface' (optional) and 'elements'.");
            ReadInterface(request);
            if (!request.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                throw new VisuRequestException("The page needs an 'elements' array (it may be empty).");
            int i = 0;
            foreach (var e in elements.EnumerateArray())
            {
                string where = $"elements[{i++}]";
                if (e.ValueKind == JsonValueKind.Object && !e.TryGetProperty("copyFrom", out _))
                    ParseElement(e, where, none, forUpdate: null, resolve: null, pageName: null);
            }
            return;
        }

        if (request.ValueKind != JsonValueKind.Array)
            throw new VisuRequestException("operations must be a JSON array, e.g. [{\"op\":\"update\",\"id\":3,\"set\":{\"x\":40}}].");
        int index = 0;
        foreach (var op in request.EnumerateArray())
        {
            string where = $"operations[{index++}]";
            if (op.ValueKind != JsonValueKind.Object) throw new VisuRequestException($"{where} must be an object.");
            string kind = RequiredString(op, "op", where).ToLowerInvariant();
            if (kind is not ("add" or "update" or "remove"))
                throw new VisuRequestException($"{where}: unknown op '{kind}'. Expected add, update or remove.");
            if (kind == "add" && op.TryGetProperty("element", out var el) && el.ValueKind == JsonValueKind.Object && !el.TryGetProperty("copyFrom", out _))
                ParseElement(el, $"{where}.element", none, forUpdate: null, resolve: null, pageName: null);
        }
    }

    // ---- building -------------------------------------------------------------------------

    private static int AddElement(VisuDocument doc, ElementSpec spec, IReadOnlyDictionary<int, XElement> sources)
    {
        doc.EnsureTypes(VisuCatalog.ElementTypes);
        int id = NextId(doc);
        XElement element;
        VisuElementTypeDef? def;
        if (spec.CopyFrom is int src)
        {
            element = VisuElements.Clone(sources[src], id, doc.ObjectId);
            def = VisuElements.Read(element).Def;
        }
        else
        {
            def = spec.Def!;
            element = VisuElements.CreateSkeleton(def, id, doc.ObjectId);
        }
        doc.AddElement(element);
        doc.UniqueIdGenerator = Math.Max(doc.UniqueIdGenerator, id + 1);
        if (spec.CopyFrom is null) VisuElements.SetShape(doc, element, def!);
        if (def is not null) Change(doc, element, def, spec);
        return id;
    }

    private static void Change(VisuDocument doc, XElement element, VisuElementTypeDef def, ElementSpec spec)
    {
        foreach (var (name, value) in spec.Properties)
            VisuElements.Set(doc, element, def, name, value);
        if (spec.Properties.Any(p => Geometry.Contains(p.Key)))
            VisuElements.UpdateDerivedGeometry(doc, element, def);
        // A line is drawn from its point list; one written without it stays invisible on the PLC.
        if (def.Type == "Line") VisuElements.EnsureLinePoints(doc, element);
        if (spec.Visualization is { } target)
        {
            var existing = VisuElements.Read(element).Frame?.Parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var given = spec.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var assignments = target.Parameters.Select(v => new VisuFrameAssignment(v.Name, v.Type,
                given.TryGetValue(v.Name, out var g) ? g : existing.GetValueOrDefault(v.Name)
                    ?? throw new VisuRequestException($"The frame parameter '{v.Name}' of {target.Name} needs a value."))).ToList();
            VisuFrames.SetReference(doc, element, target.Name, assignments);
        }
        else if (spec.Parameters.Count > 0)
        {
            VisuFrames.SetParameterValues(doc, element, spec.Parameters);
        }
    }

    private static int NextId(VisuDocument doc)
    {
        int max = doc.Elements.Select(e => ElementId(e) ?? -1).DefaultIfEmpty(-1).Max();
        return Math.Max(max + 1, doc.UniqueIdGenerator);
    }

    public static int? ElementId(XElement element) =>
        VisuDocument.NumberValue(VisuDocument.Field(element, "VisualElementId")) is long n ? (int)n : null;

    // ---- parsing / validation ----------------------------------------------------------------

    private static string? ReadInterface(JsonElement page)
    {
        if (!page.TryGetProperty("interface", out var iface) || iface.ValueKind == JsonValueKind.Null) return null;
        if (iface.ValueKind != JsonValueKind.String) throw new VisuRequestException("'interface' must be a string (the VAR_IN_OUT … END_VAR text).");
        string text = iface.GetString()!;
        ValidateText(text, "interface");
        return text;
    }

    private static int ExistingId(JsonElement op, string where, IReadOnlyDictionary<int, XElement> before)
    {
        if (!op.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id))
            throw new VisuRequestException($"{where}: 'id' (the element id from twincat_visu_read) is required.");
        if (!before.ContainsKey(id))
            throw new VisuRequestException($"{where}: the page has no element {id}. Ids: {IdList(before)}.");
        return id;
    }

    private static string IdList(IReadOnlyDictionary<int, XElement> elements) =>
        elements.Count == 0 ? "none" : string.Join(", ", elements.Keys.Order());

    private static string RequiredString(JsonElement obj, string name, string where)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new VisuRequestException($"{where}: '{name}' is required.");
        return v.GetString()!.Trim();
    }

    /// <param name="forUpdate">The type of the existing element for an update; null for a new element or a copy.</param>
    /// <param name="resolve">Finds the visualization a frame references; null in the precheck (then frames are checked for shape only).</param>
    /// <param name="existing">The element an update changes.</param>
    private static ElementSpec ParseElement(JsonElement e, string where, IReadOnlyDictionary<int, XElement> before, VisuElementTypeDef? forUpdate,
        VisuResolver? resolve, string? pageName, XElement? existing = null)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new VisuRequestException($"{where} must be an object.");

        VisuElementTypeDef? def = forUpdate;
        int? copyFrom = null;
        if (forUpdate is null)
        {
            if (e.TryGetProperty("copyFrom", out var cf))
            {
                if (!cf.TryGetInt32(out int src)) throw new VisuRequestException($"{where}: copyFrom must be an element id.");
                if (!before.TryGetValue(src, out var source))
                    throw new VisuRequestException($"{where}: copyFrom {src}: the page has no element {src}. Ids: {IdList(before)}.");
                copyFrom = src;
                def = VisuElements.Read(source).Def;
                if (def is not { CanCreate: true })
                    throw new VisuRequestException($"{where}: copyFrom {src}: element {src} is a {def?.Type ?? "type the tools do not support"}; the tools copy only the types they create ({VisuCatalog.CreatableTypeNamesList}).");
                if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    && !string.Equals(t.GetString(), def?.Type, StringComparison.OrdinalIgnoreCase))
                    throw new VisuRequestException($"{where}: type '{t.GetString()}' does not match the copied element {src} ({def?.Type ?? "unsupported"}).");
            }
            else
            {
                string type = RequiredString(e, "type", where);
                def = VisuCatalog.TypeByApiName(type)
                      ?? throw new VisuRequestException($"{where}: unknown element type '{type}'. Expected {VisuCatalog.TypeNamesList}.");
                if (!def.CanCreate)
                    throw new VisuRequestException($"{where}: a {def.Type} cannot be created by the tools. {def.Note}");
            }
        }
        else if (e.TryGetProperty("type", out _) || e.TryGetProperty("copyFrom", out _))
        {
            throw new VisuRequestException($"{where}: 'type' and 'copyFrom' cannot be changed by an update; remove the element and add a new one.");
        }

        if (existing is null && copyFrom is int copied) existing = before[copied];
        var props = new List<KeyValuePair<string, object?>>();
        string? visualization = null;
        List<KeyValuePair<string, string>>? parameters = null;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name is "type" or "copyFrom") continue;
            if (p.Name is "visualization" or "parameters")
            {
                if (def?.Type != "Frame")
                    throw new VisuRequestException($"{where}: '{p.Name}' belongs to a Frame only.");
                if (p.Name == "visualization")
                {
                    if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString()))
                        throw new VisuRequestException($"{where}.visualization must be the name of a visualization of the project.");
                    visualization = p.Value.GetString()!.Trim();
                }
                else
                {
                    parameters = ParseFrameParameters(p.Value, $"{where}.parameters");
                }
                continue;
            }
            var prop = VisuCatalog.Property(p.Name);
            if (prop is null || !def!.Has(p.Name))
                throw new VisuRequestException($"{where}: {def.Type} has no property '{p.Name}'. Its properties: {string.Join(", ", def.Members.Keys)}.");
            props.Add(new(p.Name, ParseValue(prop, p.Value, $"{where}.{p.Name}")));
        }

        if (forUpdate is null && copyFrom is null)
        {
            var missing = Geometry.Where(g => !props.Any(p => p.Key == g)).ToList();
            if (def!.Type == "Frame" && visualization is null) missing.Add("visualization");
            if (missing.Count > 0)
                throw new VisuRequestException($"{where}: a new {def!.Type} needs {string.Join(", ", missing)}.");
        }

        VisuReferenceTarget? target = null;
        if (visualization is not null && resolve is not null)
        {
            if (string.Equals(visualization, pageName, StringComparison.OrdinalIgnoreCase))
                throw new VisuRequestException($"{where}: a frame cannot show the visualization it is on ({visualization}).");
            target = resolve(visualization)
                     ?? throw new VisuRequestException($"{where}.visualization: the project has no visualization '{visualization}'.");
            var names = target.Parameters.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, _) in parameters ?? [])
                if (!names.Contains(name))
                    throw new VisuRequestException($"{where}.parameters: {target.Name} has no VAR_IN_OUT '{name}'. Its parameters: {(names.Count == 0 ? "none" : string.Join(", ", target.Parameters.Select(v => v.Name)))}.");
            var keep = existing is null ? [] : VisuElements.Read(existing).Frame?.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var missingParams = target.Parameters.Where(v => !(parameters ?? []).Any(g => string.Equals(g.Key, v.Name, StringComparison.OrdinalIgnoreCase)) && !keep.Contains(v.Name)).ToList();
            if (missingParams.Count > 0)
                throw new VisuRequestException($"{where}.parameters: {target.Name} needs a value for {string.Join(", ", missingParams.Select(v => v.Name))} (VAR_IN_OUT).");
        }
        else if (visualization is null && parameters is { Count: > 0 } && resolve is not null)
        {
            // Changing values only: the names must be parameters the frame (or the copied frame) already has.
            var frameElement = existing ?? (copyFrom is int src2 ? before[src2] : null);
            var known = frameElement is null ? [] : VisuElements.Read(frameElement).Frame?.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var (name, _) in parameters)
                if (!known.Contains(name))
                    throw new VisuRequestException($"{where}.parameters: the frame has no parameter '{name}'. Its parameters: {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
        }
        foreach (var g in Geometry)
            if (props.FirstOrDefault(p => p.Key == g) is { Key: not null, Value: null })
                throw new VisuRequestException($"{where}: {g} cannot be null.");

        return new ElementSpec(def, copyFrom, props, target, parameters);
    }

    private static List<KeyValuePair<string, string>> ParseFrameParameters(JsonElement value, string where)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new VisuRequestException($"{where} must be an object {{ \"<interface variable>\": \"<expression>\" }}.");
        var result = new List<KeyValuePair<string, string>>();
        foreach (var p in value.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString()))
                throw new VisuRequestException($"{where}.{p.Name}: the value must be a non-empty expression, e.g. 'MAIN.fbStation1'.");
            string expression = p.Value.GetString()!.Trim();
            ValidateText(expression, $"{where}.{p.Name}");
            if (result.Any(r => string.Equals(r.Key, p.Name, StringComparison.OrdinalIgnoreCase)))
                throw new VisuRequestException($"{where}: '{p.Name}' is given twice.");
            result.Add(new(p.Name, expression));
        }
        return result;
    }

    private static object? ParseValue(VisuPropertyDef prop, JsonElement value, string where)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        switch (prop.Kind)
        {
            case VisuValueKind.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int n))
                    throw new VisuRequestException($"{where} must be a whole number.");
                if (n < MinCoordinate || n > MaxCoordinate)
                    throw new VisuRequestException($"{where} must be between {MinCoordinate} and {MaxCoordinate}.");
                return n;
            case VisuValueKind.Text:
            case VisuValueKind.Expression:
                if (value.ValueKind != JsonValueKind.String) throw new VisuRequestException($"{where} must be a string (or null to reset it).");
                string s = value.GetString()!;
                ValidateText(s, where);
                return prop.Kind == VisuValueKind.Expression ? s.Trim() : s;
            case VisuValueKind.Color:
                if (value.ValueKind != JsonValueKind.String || VisuElements.ParseColor(value.GetString()!) is not uint c)
                    throw new VisuRequestException($"{where} must be a colour '#RRGGBB' or '#AARRGGBB' (or null to reset it).");
                return c;
            default:
                throw new VisuRequestException($"{where}: unsupported value kind.");
        }
    }

    /// <summary>
    /// The archive stores a string between double quotes with no escaping that the tools have verified, so a double quote
    /// inside a value is refused (IEC string literals use single quotes).
    /// </summary>
    private static void ValidateText(string text, string where)
    {
        if (text.Contains('"'))
            throw new VisuRequestException($"{where} contains a double quote (\"), which the .TcVIS format cannot hold in a way the tools have verified. Use single quotes.");
        if (text.Any(ch => ch < ' ' && ch is not ('\t' or '\n' or '\r')))
            throw new VisuRequestException($"{where} contains a control character.");
    }

    /// <summary>The integer value as stored, for the tool output (invariant culture).</summary>
    public static string FormatInt(long n) => n.ToString(CultureInfo.InvariantCulture);
}
