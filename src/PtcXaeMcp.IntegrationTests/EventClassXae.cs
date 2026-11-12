using System.Collections;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Removes an event class from the running XAE's type system for test cleanup — no MCP tool deletes one. DeleteType takes
/// the GUID record by value, which only the dual interface passes (through its vtable).
/// </summary>
internal static class EventClassXae
{
    [ComImport, Guid("fd56539d-c78a-4a22-9a69-1a7ba9e59e19"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface ITcTypeSystemVtable
    {
        [DispId(-4)] object NewEnum { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
        [DispId(1)] int TypeCount { get; }
        [DispId(2)] [return: MarshalAs(UnmanagedType.IDispatch)] object TypeAt(int n);
        [DispId(3)] [return: MarshalAs(UnmanagedType.IDispatch)] object LookupType(Guid guid);
        [DispId(4)] [return: MarshalAs(UnmanagedType.IDispatch)] object CreateType([MarshalAs(UnmanagedType.BStr)] string xml);
        [DispId(5)] void DeleteType(Guid guid);
    }

    /// <summary>
    /// ITcTypeSystem.CreateType(<paramref name="dataTypeXml"/>) in the open solution — how a STRUCT/ENUM/ALIAS gets into the
    /// type system for the pin test (no MCP tool creates one). Returns the GUID of <paramref name="name"/> read back. Does not save.
    /// </summary>
    public static string CreateType(string name, string dataTypeXml)
    {
        dynamic dte = XaeDte.TryGet() ?? throw new InvalidOperationException("No running XAE (TcXaeShell.DTE) in the ROT.");
        WhileLoading(() => XaeDte.Retry(() =>
        {
            object typeSystem = TypeSystemOf(dte);
            _ = ((dynamic)typeSystem).CreateType(dataTypeXml);
            return true;
        }));
        return GuidOf(name) ?? throw new InvalidOperationException($"CreateType did not leave a type '{name}' with a GUID.");
    }

    /// <summary>The GUID ("{…}") of the type <paramref name="name"/> in the open solution's type system; null when there is none.</summary>
    public static string? GuidOf(string name)
    {
        dynamic dte = XaeDte.TryGet() ?? throw new InvalidOperationException("No running XAE (TcXaeShell.DTE) in the ROT.");
        return WhileLoading(() => XaeDte.Retry(() =>
        {
            foreach (object? type in (IEnumerable)TypeSystemOf(dte))
            {
                if (type is null || !string.Equals((string)((dynamic)type).Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                var guid = Regex.Match((string)((dynamic)type).ExportXml(false), "GUID=\"(\\{[^\"]+\\})\"");
                if (guid.Success) return guid.Groups[1].Value;
            }
            return (string?)null;
        }));
    }

    /// <summary>
    /// Right after a solution was opened the XAE may still load (a fixture with a visualization manager loads its
    /// libraries a moment longer): the type system is not there yet or does not hand out its enumerator. Retried for 30 s.
    /// </summary>
    private static T WhileLoading<T>(Func<T> call)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return call(); }
            catch (Exception ex) when (ex is InvalidCastException
                                       || (ex is InvalidOperationException && ex.Message.StartsWith("The open solution has no TwinCAT project", StringComparison.Ordinal)))
            {
                if (DateTime.UtcNow >= deadline) throw;
                Thread.Sleep(1000);
            }
        }
    }

    private static object TypeSystemOf(dynamic dte)
    {
        // A busy XAE (RPC_E_CALL_REJECTED / RETRYLATER) must reach XaeDte.Retry: swallowed here it read as "no type
        // system" right after a fixture with a visualization manager was opened (it loads its libraries a moment longer).
        dynamic projects = dte.Solution.Projects;
        Exception? last = null;
        for (int i = 1; i <= (int)projects.Count; i++)
        {
            object? sysMan = null;
            try { sysMan = projects.Item(i).Object; } catch (COMException ex) when (!IsBusy(ex)) { last = ex; }
            if (sysMan is null) continue;
            try { return ((dynamic)sysMan).GetTypeSystem(); } catch (COMException ex) when (!IsBusy(ex)) { last = ex; }
        }
        throw new InvalidOperationException("The open solution has no TwinCAT project with a type system." +
                                            (last is null ? "" : $" Last error: {last.Message} (0x{last.HResult:X8})"));
    }

    private static bool IsBusy(COMException ex) =>
        ex.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A);

    /// <summary>Deletes the type <paramref name="name"/> from the type system of the open solution; returns what happened. Does not save.</summary>
    public static string DeleteType(string name)
    {
        dynamic dte = XaeDte.TryGet() ?? throw new InvalidOperationException("No running XAE (TcXaeShell.DTE) in the ROT.");
        return XaeDte.Retry(() =>
        {
            dynamic projects = dte.Solution.Projects;
            for (int i = 1; i <= (int)projects.Count; i++)
            {
                dynamic project = projects.Item(i);
                object? sysMan = null;
                try { sysMan = project.Object; } catch (COMException) { }
                if (sysMan is null) continue;
                object typeSystem;
                try { typeSystem = ((dynamic)sysMan).GetTypeSystem(); } catch (COMException) { continue; }
                foreach (object? type in (IEnumerable)typeSystem)
                {
                    if (type is null || !string.Equals((string)((dynamic)type).Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    var guid = Regex.Match((string)((dynamic)type).ExportXml(false), "GUID=\"\\{([^\"]+)\\}\"");
                    if (!guid.Success) return $"type '{name}' has no readable GUID";
                    ((ITcTypeSystemVtable)typeSystem).DeleteType(Guid.Parse(guid.Groups[1].Value));
                    return $"deleted type '{name}' {{{guid.Groups[1].Value}}}";
                }
            }
            return $"type '{name}' not found";
        });
    }
}
