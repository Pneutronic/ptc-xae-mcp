using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using PtcXaeMcp.WinForms.TwinCat;
using Xunit;

namespace PtcXaeMcp.Tests;

/// <summary>
/// TcHmiAutomation.dll (TE2000 HMI Engineering) is optional at runtime: the release is built
/// with it, but a machine without TE2000 must still run every non-HMI tool (issue #43). The
/// runtime loads an assembly as soon as a type is loaded whose fields name one of its types.
/// The C# compiler puts the cached delegates of all non-capturing lambdas of a class into one
/// shared nested class (<c>&lt;&gt;c</c>), so a single <c>Func&lt;ITcHmiProject&gt;</c> lambda in
/// a partial file of <see cref="TwinCatComSession"/> made the first lambda of any tool (login,
/// build, …) fail with "Could not load file or assembly 'TcHmiAutomation'". Only
/// <c>TcHmiBridge</c> may name such types; the HMI tools reach it after their TE2000 check.
/// </summary>
public class HmiAssemblyIsolationTests
{
    private const string HmiAssembly = "TcHmiAutomation";
    private const string BridgeName = "TcHmiBridge";

    private static readonly Assembly Server = typeof(TwinCatComSession).Assembly;

    private static bool BuildHasHmi => Server.GetReferencedAssemblies().Any(a => a.Name == HmiAssembly);

    [SkippableFact]
    public void Every_type_outside_the_HMI_bridge_loads_without_TcHmiAutomation()
    {
        Skip.IfNot(BuildHasHmi, "This build has no HMI support (TcHmi=false).");
        // The test host has no BeckhoffAssemblyResolver and the DLL is not copied next to it
        // (Private=$(BundleBeckhoffDlls)), so here it is as missing as on a machine without TE2000.
        Skip.If(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == HmiAssembly) ||
                File.Exists(Path.Combine(AppContext.BaseDirectory, HmiAssembly + ".dll")),
            "TcHmiAutomation is resolvable in this test host; the type-load check would prove nothing.");

        var offenders = new List<string>();
        foreach (string name in TypeNames().Where(n => !IsInsideBridge(n)))
        {
            try
            {
                Type type = Server.GetType(name, throwOnError: true)!;
                // The lambda caches (<>c) are what every tool touches first; run their static
                // initialisation as the first lambda of a non-HMI tool would.
                if (type.Name == "<>c")
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    foreach (FieldInfo f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        f.GetValue(null);
                }
            }
            catch (Exception ex) when (NeedsHmiAssembly(ex))
            {
                offenders.Add($"{name}: {ex.Message}");
            }
        }

        offenders.Should().BeEmpty("a non-HMI tool must not need TE2000 (#43)");
    }

    [SkippableFact]
    public void Only_the_HMI_bridge_has_fields_of_TcHmiAutomation_types()
    {
        Skip.IfNot(BuildHasHmi, "This build has no HMI support (TcHmi=false).");

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (string name in TypeNames().Where(n => !IsInsideBridge(n)))
        {
            Type? type;
            try { type = Server.GetType(name, throwOnError: true); }
            catch (Exception ex) when (NeedsHmiAssembly(ex))
            {
                offenders.Add($"{name}: does not load ({ex.Message})");
                continue;
            }
            foreach (FieldInfo field in type!.GetFields(all))
            {
                // Without the DLL, reading the field type is exactly what throws.
                try
                {
                    if (NamesHmiType(field.FieldType))
                        offenders.Add($"{name}.{field.Name} : {field.FieldType}");
                }
                catch (Exception ex) when (NeedsHmiAssembly(ex))
                {
                    offenders.Add($"{name}.{field.Name} : needs {HmiAssembly}");
                }
                catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException)
                {
                    // Another optional assembly (EnvDTE*, resolved from the XAE at runtime): not this test's concern.
                }
            }
        }

        offenders.Should().BeEmpty($"only {BridgeName} may hold TcHmiAutomation types (#43)");
    }

    /// <summary>Reflection names (Outer+Inner) of every type defined in the server assembly,
    /// read from metadata so that no type is loaded while listing them.</summary>
    private static IEnumerable<string> TypeNames()
    {
        using var stream = File.OpenRead(Server.Location);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        var names = new List<string>();
        foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
        {
            string name = FullName(md, md.GetTypeDefinition(handle));
            if (name != "<Module>") names.Add(name);
        }
        return names;
    }

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        string name = md.GetString(type.Name);
        TypeDefinitionHandle outer = type.GetDeclaringType();
        if (!outer.IsNil) return FullName(md, md.GetTypeDefinition(outer)) + "+" + name;
        string ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>True when <paramref name="ex"/> (or an inner exception) is the missing TcHmiAutomation
    /// assembly. The test host also lacks EnvDTE*, which the server resolves from the XAE at runtime.</summary>
    private static bool NeedsHmiAssembly(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is TypeLoadException or FileNotFoundException or FileLoadException or TypeInitializationException &&
                e.Message.Contains(HmiAssembly, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool IsInsideBridge(string reflectionName) =>
        reflectionName.Split('.', '+').Contains(BridgeName);

    private static bool NamesHmiType(Type type)
    {
        if (type.Assembly.GetName().Name == HmiAssembly) return true;
        if (type.HasElementType && NamesHmiType(type.GetElementType()!)) return true;
        return type.IsGenericType && type.GetGenericArguments().Any(NamesHmiType);
    }
}
