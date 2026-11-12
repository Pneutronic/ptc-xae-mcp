using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>JSON views of a visualization and of the catalog for the tool results. Pure.</summary>
internal static class VisuJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>16 hex digits of SHA-256 over the file text; twincat_visu_apply/edit_elements refuse a stale one.</summary>
    public static string Revision(string fileText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileText)), 0, 8).ToLowerInvariant();

    public static string Page(VisuDocument doc, string treePath, string filePath, string revision)
    {
        var elements = doc.Elements.Select(VisuElements.Read).ToList();
        string? problem = VisuEditor.WriteProblem(doc);
        var root = new JsonObject
        {
            ["path"] = treePath,
            ["name"] = doc.Name,
            ["file"] = filePath,
            ["objectId"] = doc.ObjectId,
            ["productVersion"] = doc.ProductVersion,
            ["writable"] = problem is null,
        };
        if (problem is not null) root["writeProblem"] = problem;
        root["revision"] = revision;
        root["interface"] = doc.InterfaceText;
        var (w, h) = Extent(elements);
        root["extent"] = new JsonObject { ["width"] = w, ["height"] = h };
        root["elementCount"] = elements.Count;
        root["unsupportedCount"] = elements.Count(e => !e.Supported);
        var list = new JsonArray();
        foreach (var e in elements) list.Add(Element(e));
        root["elements"] = list;
        return root.ToJsonString(Options);
    }

    /// <summary>Right and bottom edge of the elements that state their geometry (the page size XAE auto-detects from them).</summary>
    private static (long Width, long Height) Extent(IEnumerable<VisuElementView> elements)
    {
        long right = 0, bottom = 0;
        foreach (var e in elements)
        {
            long? Get(string n) => e.Properties.FirstOrDefault(p => p.Key == n).Value as long?;
            if (Get("x") is long x && Get("width") is long w) right = Math.Max(right, Math.Max(x, x + w));
            if (Get("y") is long y && Get("height") is long h) bottom = Math.Max(bottom, Math.Max(y, y + h));
        }
        return (right, bottom);
    }

    public static JsonObject Element(VisuElementView e)
    {
        var o = new JsonObject
        {
            ["id"] = e.Id,
            ["identifier"] = e.Identifier,
            ["type"] = e.Def?.Type ?? e.ElementName,
        };
        if (!e.Supported)
        {
            o["supported"] = false;
            o["elementName"] = e.ElementName;
            o["typeName"] = e.TypeName;
        }
        foreach (var (name, value) in e.Properties)
            o[name] = value switch
            {
                long n => JsonValue.Create(n),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(value.ToString()),
            };
        if (e.Frame is { } f)
        {
            var frame = new JsonObject { ["visualizations"] = new JsonArray(f.Visualizations.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
            var ps = new JsonObject();
            foreach (var p in f.Parameters) ps[p.Name] = p.Value;
            frame["parameters"] = ps;
            o["frame"] = frame;
        }
        if (e.InputActions.Count > 0)
            o["inputActions"] = new JsonArray(e.InputActions.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        if (e.OtherMembers > 0) o["otherMembers"] = e.OtherMembers;
        return o;
    }

    public static string Catalog()
    {
        var types = new JsonArray();
        foreach (var t in VisuCatalog.Types)
        {
            var o = new JsonObject
            {
                ["type"] = t.Type,
                ["create"] = t.CanCreate,
                ["properties"] = new JsonArray(t.Members.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            };
            if (t.Type == "Frame") o["frameParameters"] = "object { \"<parameter>\": \"<expression>\" } — the parameters of the referenced visualization (see twincat_visu_read)";
            if (t.Note.Length > 0) o["note"] = t.Note;
            types.Add(o);
        }
        var props = new JsonArray();
        foreach (var p in VisuCatalog.Properties)
            props.Add(new JsonObject { ["name"] = p.Name, ["kind"] = p.Kind.ToString().ToLowerInvariant(), ["description"] = p.Description });
        var root = new JsonObject
        {
            ["format"] = "PLC visualization (.TcVIS). Written files are verified for TcPlcObject format 1.1.0.1 from TwinCAT 3.1.4024 and " +
                         "3.1.4026; other files are read only. Elements and members the tools do not know stay in the file unchanged.",
            ["types"] = types,
            ["properties"] = props,
            ["notSupported"] = new JsonArray(
                "creating a Polygon or a Frame from scratch (copy an existing one with copyFrom)",
                "scaling and internal rotation by variable",
                "the input action 'toggle a variable' (tapVariable sets a variable while the element is pressed)",
                "bars, tables, trends, images and the other element types (they are read as unsupported and kept as they are)"),
        };
        return root.ToJsonString(Options);
    }

    /// <summary>The ids and catalog properties of every element, to compare what was meant with what is in the file.</summary>
    public static string Fingerprint(VisuDocument doc) =>
        string.Join("\n", doc.Elements.Select(VisuElements.Read).Select(e =>
            $"{e.Id}|{e.Def?.Type ?? e.ElementName}|{string.Join(";", e.Properties.Select(p => p.Key + "=" + p.Value))}|" +
            string.Join(";", e.Frame?.Parameters.Select(p => p.Name + "=" + p.Value) ?? [])))
        + "\n#" + doc.InterfaceText;

    /// <summary>For tests and diagnostics: the element node of id <paramref name="id"/>.</summary>
    public static XElement? ElementById(VisuDocument doc, int id) => doc.Elements.FirstOrDefault(e => VisuEditor.ElementId(e) == id);
}
