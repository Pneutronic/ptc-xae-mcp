using System.Globalization;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
#if !TWINCAT
    public string ScopeProbe(string action, string? nodePath, string? name, int? elementType,
        string? key, string? value, string? projectName) => throw TwinCatNotBuilt.Exception();
#else
    // ===================================================================
    // Scope research probe (issue #153, diagnostics only). The Scope View
    // property keys and trigger element ids are not documented on Infosys;
    // this probe makes them observable on a live TE130x instead of guessing:
    //   dump   — every node under nodePath (default: the scope root) with
    //            every EnvDTE Property (key, value, CLR value type);
    //   create — IMeasurementScope.CreateChild(name, elementType) on nodePath,
    //            reports the HRESULT, the created node's name and properties;
    //   set    — assign value to the property whose key equals `key`
    //            exactly on nodePath, then read it back from a fresh node;
    //   delete — ProjectItem.Delete()/Remove() on nodePath.
    // nodePath is a '^'-separated chain of child names from the scope root.
    // Never saves.
    // ===================================================================
    public string ScopeProbe(string action, string? nodePath, string? name, int? elementType,
        string? key, string? value, string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        var sb = new StringBuilder();

        dynamic node = ProbeResolve(root, nodePath);
        switch ((action ?? "dump").Trim().ToLowerInvariant())
        {
            case "dump":
                ProbeDump(node, sb, 0, 6);
                return sb.ToString();

            case "create":
            {
                if (string.IsNullOrWhiteSpace(name) || elementType is null)
                    throw new InvalidOperationException("create needs name and elementType.");
                TwinCAT.Measurement.AutomationInterface.IMeasurementScope ims = Ims(node);
                int hr;
                object? child;
                try { hr = ims.CreateChild(out child, name!, elementType.Value); }
                catch (Exception ex) when (ComCall.NotBusy(ex))
                {
                    return $"CreateChild('{name}', {elementType}) THREW hr=0x{ex.HResult:X8}: {ex.Message}";
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"CreateChild('{name}', {elementType}) returned hr=0x{hr:X8}, child={(child is null ? "<null>" : "object")}");
                if (child is not null)
                    AfterMutation<object?>(() => { ProbeDump(child, sb, 0, 2); return null; });
                return sb.ToString();
            }

            case "set":
            {
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException("set needs key.");
                dynamic? prop = null;
                foreach (dynamic p in AcqProperties(node))
                    if (string.Equals(SafePropName(p), key, StringComparison.Ordinal)) { prop = p; break; }
                if (prop is null) return $"No property with key '{key}' on '{nodePath}'.";

                object? before = ComCall.ReadProp<object?>(() => (object?)prop.Value, null);
                sb.AppendLine(CultureInfo.InvariantCulture, $"before: {Describe(before)}");
                object? typed = ProbeCoerce(before, value);
                try { prop.Value = typed; sb.AppendLine(CultureInfo.InvariantCulture, $"assigned {Describe(typed)}: no throw"); }
                catch (Exception ex) when (ComCall.NotBusy(ex))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"assign {Describe(typed)} THREW hr=0x{ex.HResult:X8}: {ex.Message}");
                }
                AfterMutation<object?>(() =>
                {
                    dynamic fresh = ProbeResolve(RequireScopeRootItem(GetActiveTcDte(), projectName), nodePath);
                    foreach (dynamic p in AcqProperties(fresh))
                        if (string.Equals(SafePropName(p), key, StringComparison.Ordinal))
                            sb.AppendLine(CultureInfo.InvariantCulture, $"read-back (fresh node): {Describe(ComCall.ReadProp<object?>(() => (object?)p.Value, null))}");
                    return null;
                });
                return sb.ToString();
            }

            case "delete":
            {
                if (string.IsNullOrWhiteSpace(nodePath))
                    throw new InvalidOperationException("delete needs nodePath (never the root).");
                try { node.Delete(); return $"Deleted '{nodePath}' (ProjectItem.Delete)."; }
                catch (Exception ex) when (ComCall.NotBusy(ex)) { sb.AppendLine(CultureInfo.InvariantCulture, $"Delete threw: {ex.Message}"); }
                try { node.Remove(); return $"Removed '{nodePath}' (ProjectItem.Remove)."; }
                catch (Exception ex) when (ComCall.NotBusy(ex)) { sb.AppendLine(CultureInfo.InvariantCulture, $"Remove threw: {ex.Message}"); }
                return sb.ToString();
            }

            case "remove":
            {
                // #59: ProjectItem.Delete() on a Scope node returns without error and removes nothing; try Remove() alone.
                if (string.IsNullOrWhiteSpace(nodePath))
                    throw new InvalidOperationException("remove needs nodePath (never the root).");
                try { node.Remove(); return $"Remove() on '{nodePath}' returned without error."; }
                catch (Exception ex) when (ComCall.NotBusy(ex)) { return $"Remove threw hr=0x{ex.HResult:X8}: {ex.Message}"; }
            }

            default:
                throw new InvalidOperationException($"Unknown action '{action}'. Expected dump, create, set, delete or remove.");
        }
    }, OpenTimeout);

    private static dynamic ProbeResolve(dynamic root, string? nodePath)
    {
        dynamic node = root;
        if (string.IsNullOrWhiteSpace(nodePath)) return node;
        foreach (string seg in nodePath!.Split('^', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            dynamic? next = null;
            foreach (dynamic c in EnumerateChildren(node))
            {
                string cn = ComCall.ReadProp(() => (string)c.Name, string.Empty) ?? string.Empty;
                if (string.Equals(cn, seg, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            }
            node = next ?? throw new InvalidOperationException($"Node '{seg}' not found under path '{nodePath}'.");
        }
        return node;
    }

    private static void ProbeDump(dynamic node, StringBuilder sb, int depth, int maxDepth)
    {
        string indent = new(' ', depth * 2);
        string nm = ComCall.ReadProp(() => (string)node.Name, "<?>") ?? "<null>";
        string kind = ComCall.ReadProp(() => (string)node.Kind, "") ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}- '{nm}' kind={kind}");
        foreach (dynamic p in AcqProperties(node))
        {
            string pn = SafePropName(p);
            object? v = ComCall.ReadProp<object?>(() => (object?)p.Value, "<unreadable>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    [{pn}] = {Describe(v)}");
        }
        if (depth >= maxDepth) return;
        foreach (dynamic c in EnumerateChildren(node))
            ProbeDump(c, sb, depth + 1, maxDepth);
    }

    private static string Describe(object? v) => v switch
    {
        null => "<null>",
        string s => $"\"{s}\" (String)",
        IFormattable f => $"{f.ToString(null, CultureInfo.InvariantCulture)} ({v.GetType().Name})",
        _ => $"{v} ({v.GetType().Name})",
    };

    // Coerce the probe's string value to the CLR type the property currently holds.
    private static object? ProbeCoerce(object? current, string? value)
    {
        if (value is null) return null;
        return current switch
        {
            bool => bool.Parse(value),
            int => int.Parse(value, CultureInfo.InvariantCulture),
            uint => uint.Parse(value, CultureInfo.InvariantCulture),
            long => long.Parse(value, CultureInfo.InvariantCulture),
            double => double.Parse(value, CultureInfo.InvariantCulture),
            float => float.Parse(value, CultureInfo.InvariantCulture),
            short => short.Parse(value, CultureInfo.InvariantCulture),
            byte => byte.Parse(value, CultureInfo.InvariantCulture),
            Enum e => Enum.Parse(e.GetType(), value, ignoreCase: true),
            _ => value,
        };
    }
#endif
}
