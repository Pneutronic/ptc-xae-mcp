using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// ---- event-classes ---------------------------------------------------------------------------
//
// EventLogger event classes live in the TwinCAT type system (ITcSysManager.GetTypeSystem(), Infosys TE1000
// "ITcTypeSystem"); there is no tree item for it. Found live on 4026.22 (2026-11-06):
//   * CreateType(xml) with <Name PersistentType="true"> marks the .tsproj dirty and Save writes the class into
//     <TcSmProject><DataTypes>; without the flag the type is gone after save + reopen (only referenced types are saved).
//   * foreach over the type system (_NewEnum) is reliable; Type(n) returned a different count from call to call.
//   * LookupType/DeleteType take the type library's GUID record by value, which late binding cannot pass
//     (E_INVALIDARG for a string, a Guid and a hand-built VT_RECORD). The dual interface called through its vtable works.
//   * ITcTypeInstance.ImportXml appends EventIds instead of replacing them — an update is DeleteType + CreateType with
//     the same GUID; the PLC's global types follow at once (TC_EVENTS.<class>.<new event> compiles).
//   * "Add Global Data Type" (PLC project > Add) writes <plc>\ExternalTypes.tmc (TcModuleClass/DataTypes with a copy of
//     the DataType) and <None Include="ExternalTypes.tmc"> in the .plcproj; the tree then shows ExternalTypes.tmc (653)
//     with Global.ST_<class> (651). CreateChild(651/652/653) and ConsumeXml on that node are refused, so the pin is
//     written as files while the solution is closed and the XAE reads it on reopen.
internal sealed partial class TwinCatComSession
{
    /// <summary>ITcTypeSystem as its dual interface: the GUID parameters only arrive through the vtable.</summary>
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

    private const int ItemTypeTmcDescription = 653;
    private static readonly TimeSpan ReopenSettle = TimeSpan.FromSeconds(120);

    public IReadOnlyList<string> ReadEventClassXml() => ComBusyRetry.Run<IReadOnlyList<string>>(() =>
    {
        object typeSystem = RequireTypeSystem();
        var found = new List<string>();
        foreach (object type in TypesOf(typeSystem))
        {
            string xml = ExportXml(type);
            if (xml.Contains("<EventId>", StringComparison.Ordinal)) found.Add(xml);
        }
        return found;
    }, OpenTimeout);

    // Each COM step is its own retry scope: a busy read-back must not replay the CreateType before it.
    public string CreateTypeSystemType(string name, string dataTypeXml)
    {
        ComBusyRetry.Run(() =>
        {
            object typeSystem = RequireTypeSystem();
            try { _ = ((dynamic)typeSystem).CreateType(dataTypeXml); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                throw new InvalidOperationException($"ITcTypeSystem.CreateType refused the type '{name}': {ex.Message}", ex);
            }
        }, OpenTimeout);
        return ComBusyRetry.Run(() => ReadBackType(name), OpenTimeout);
    }

    public string ReplaceTypeSystemType(string guid, string name, string dataTypeXml, string previousXml)
    {
        if (!Guid.TryParse(guid, out Guid id))
            throw new InvalidOperationException($"'{guid}' is no GUID.");
        ComBusyRetry.Run(() =>
        {
            try { ((ITcTypeSystemVtable)RequireTypeSystem()).DeleteType(id); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                throw new InvalidOperationException($"ITcTypeSystem.DeleteType({guid}) failed, nothing was changed: {ex.Message}", ex);
            }
        }, OpenTimeout);
        ComBusyRetry.Run(() =>
        {
            var vtable = (ITcTypeSystemVtable)RequireTypeSystem();
            try { _ = vtable.CreateType(dataTypeXml); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                string restored;
                try { _ = vtable.CreateType(previousXml); restored = "the previous definition was created again"; }
                catch (Exception again) when (ComCall.NotBusy(again))
                {
                    restored = $"restoring the previous definition failed too ({again.Message}) — close the solution without saving";
                }
                throw new InvalidOperationException($"ITcTypeSystem.CreateType refused the new definition of '{name}' ({ex.Message}); {restored}.", ex);
            }
        }, OpenTimeout);
        return ComBusyRetry.Run(() => ReadBackType(name), OpenTimeout);
    }

    public IReadOnlyList<PlcExternalTypesInfo> ReadPlcExternalTypes() => ComBusyRetry.Run<IReadOnlyList<PlcExternalTypesInfo>>(() =>
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        var result = new List<PlcExternalTypesInfo>();
        foreach (var project in EnumeratePlcSourceProjects(dte))
        {
            object? plcNode = LookupTreeItemDirect(sysMan, project.Path);
            string plcXml = plcNode is null ? string.Empty : ComCall.ReadProp(() => (string?)((dynamic)plcNode).ProduceXml(false) ?? string.Empty, string.Empty);
            var match = Regex.Match(plcXml, "<ProjectPath>([^<]+)</ProjectPath>");
            if (!match.Success) continue; // a referenced stand-alone PLC: no .plcproj of its own here

            var pinned = new List<string>();
            foreach (object child in Children(project.Nested))
            {
                if (ItemTypeOf(child) != ItemTypeTmcDescription) continue;
                if (!string.Equals(NameOf(child), EventClassXml.ExternalTypesFile, StringComparison.OrdinalIgnoreCase)) continue;
                pinned.AddRange(NamesOf(Children(child)));
            }

            string plcproj = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            string tmcPath = Path.Combine(Path.GetDirectoryName(plcproj) ?? string.Empty, EventClassXml.ExternalTypesFile);
            string? tmc = File.Exists(tmcPath) ? File.ReadAllText(tmcPath) : null;
            result.Add(new PlcExternalTypesInfo(project.Name, plcproj, pinned, tmc));
        }
        return result;
    }, OpenTimeout);

    /// <summary>
    /// Save + close the solution, rewrite the files, reopen and wait until the PLC projects are back. The reopen runs even
    /// when an edit fails. COM references of the open solution are collected before the close: an RCW released after it
    /// shows the XAE's modal "System Manager already disposed!".
    /// </summary>
    public void EditFilesWithSolutionClosed(IReadOnlyList<SolutionFileEdit> edits)
    {
        string sln = ActiveSolutionPath() ?? throw new InvalidOperationException("No solution is open.");
        int plcCount = ComBusyRetry.Run(() => EnumeratePlcSourceProjects(GetActiveTcDte()).Count, OpenTimeout);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var close = CloseSolution(saveFirst: true);
        if (HasOpenSolution())
            throw new InvalidOperationException(
                "The solution did not close" + (close.SavePrompt is null ? "" : $" (save prompt: {close.SavePrompt})") + "; no file was changed.");
        try
        {
            var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            foreach (var edit in edits)
            {
                string? current = File.Exists(edit.Path) ? File.ReadAllText(edit.Path) : null;
                string next = edit.Transform(current);
                if (current is not null && string.Equals(current, next, StringComparison.Ordinal)) continue;
                File.WriteAllText(edit.Path, next, utf8Bom);
            }
        }
        finally
        {
            OpenSolution(sln);
            WaitForPlcProjects(plcCount);
        }
    }

    private static void WaitForPlcProjects(int expected)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int count = 0;
            try { count = ComBusyRetry.Run(() => EnumeratePlcSourceProjects(GetActiveTcDte()).Count, ProbeTimeout); }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { /* still loading */ }
            if (count >= expected) return;
            if (sw.Elapsed >= ReopenSettle)
                throw new InvalidOperationException(
                    $"The solution was reopened but only {count} of {expected} PLC project(s) loaded within {ReopenSettle.TotalSeconds:F0} s.");
            Thread.Sleep(1000);
        }
    }

    private static object RequireTypeSystem()
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        object? typeSystem;
        try { typeSystem = ((dynamic)sysMan).GetTypeSystem(); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException($"ITcSysManager.GetTypeSystem failed: {ex.Message}", ex);
        }
        return typeSystem ?? throw new InvalidOperationException("ITcSysManager.GetTypeSystem returned nothing.");
    }

    private static List<object> TypesOf(object typeSystem)
    {
        if (typeSystem is not IEnumerable sequence)
            throw new InvalidOperationException("The type system cannot be enumerated (_NewEnum).");
        var types = new List<object>();
        foreach (object? t in sequence)
            if (t is not null) types.Add(t);
        return types;
    }

    private static string ExportXml(object type) =>
        ComCall.ReadProp(() => (string?)((dynamic)type).ExportXml(false) ?? string.Empty, string.Empty);

    // The type of that name from a fresh enumeration (not the instance CreateType returned).
    private static string ReadBackType(string name)
    {
        object typeSystem = RequireTypeSystem();
        foreach (object type in TypesOf(typeSystem))
        {
            string typeName = ComCall.ReadProp(() => (string?)((dynamic)type).Name ?? string.Empty, string.Empty);
            if (string.Equals(typeName, name, StringComparison.OrdinalIgnoreCase)) return ExportXml(type);
        }
        throw new InvalidOperationException($"The type '{name}' is not in the type system after the call.");
    }
}
