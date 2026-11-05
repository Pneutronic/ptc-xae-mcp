using PtcXaeMcp.WinForms.TwinCat.HostSelection;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Maps a project's host class to (ProgID, exePath). The registry probe is
/// injected so these stay pure (no dependency on this box's registry).
/// ProgIDs are the Infosys-confirmed constants; exe path comes from the
/// registry probe, falling back to the documented well-known path per class.
/// </summary>
public class XaeHostResolverTests
{
    // Well-known fallbacks (Infosys + empirical, recorded in the spec).
    private const string Exe64 =
        @"C:\Program Files\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe";
    private const string Exe32 =
        @"C:\Program Files (x86)\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe";

    [Fact]
    public void Shell64_UsesProgId170_AndRegistryExe()
    {
        var resolver = new XaeHostResolver(
            probeExeForProgId: id => id == "TcXaeShell.DTE.17.0" ? @"D:\custom\Tc64.exe" : null);

        var r = resolver.Resolve(XaeHostClass.Shell64);

        Assert.Equal("TcXaeShell.DTE.17.0", r.ProgId);
        Assert.Equal(@"D:\custom\Tc64.exe", r.ExePath);
        Assert.True(r.ExeFromRegistry);
    }

    [Fact]
    public void Shell32_UsesProgId150_AndRegistryExe()
    {
        var resolver = new XaeHostResolver(
            probeExeForProgId: id => id == "TcXaeShell.DTE.15.0" ? @"D:\custom\Tc32.exe" : null);

        var r = resolver.Resolve(XaeHostClass.Shell32);

        Assert.Equal("TcXaeShell.DTE.15.0", r.ProgId);
        Assert.Equal(@"D:\custom\Tc32.exe", r.ExePath);
        Assert.True(r.ExeFromRegistry);
    }

    [Fact]
    public void Shell64_NoRegistryEntry_FallsBackToWellKnown64Path()
    {
        var resolver = new XaeHostResolver(probeExeForProgId: _ => null);

        var r = resolver.Resolve(XaeHostClass.Shell64);

        Assert.Equal("TcXaeShell.DTE.17.0", r.ProgId);
        Assert.Equal(Exe64, r.ExePath);
        Assert.False(r.ExeFromRegistry);
    }

    [Fact]
    public void Shell32_NoRegistryEntry_FallsBackToWellKnown32Path()
    {
        // Mirrors this box: 15.0 class has no LocalServer32 but the exe exists.
        var resolver = new XaeHostResolver(probeExeForProgId: _ => null);

        var r = resolver.Resolve(XaeHostClass.Shell32);

        Assert.Equal("TcXaeShell.DTE.15.0", r.ProgId);
        Assert.Equal(Exe32, r.ExePath);
        Assert.False(r.ExeFromRegistry);
    }

    [Fact]
    public void ResolveFromTsproj_4026_PicksShell64()
    {
        var resolver = new XaeHostResolver(probeExeForProgId: _ => null);

        Assert.True(resolver.TryResolveFromTsproj(
            "<TcSmProject TcVersion=\"3.1.4026.22\"/>", out var r, out var err));
        Assert.Null(err);
        Assert.Equal("TcXaeShell.DTE.17.0", r!.ProgId);
        Assert.Equal("3.1.4026.22", r.TcVersion!.ToString());
        Assert.Equal(XaeHostClass.Shell64, r.HostClass);
    }

    [Fact]
    public void ResolveFromTsproj_4024_PicksShell32()
    {
        var resolver = new XaeHostResolver(probeExeForProgId: _ => null);

        Assert.True(resolver.TryResolveFromTsproj(
            "<TcSmProject TcVersion=\"3.1.4024.55\"/>", out var r, out _));
        Assert.Equal("TcXaeShell.DTE.15.0", r!.ProgId);
        Assert.Equal(XaeHostClass.Shell32, r.HostClass);
    }

    [Fact]
    public void ResolveFromTsproj_Malformed_FailsWithReason()
    {
        var resolver = new XaeHostResolver(probeExeForProgId: _ => null);

        Assert.False(resolver.TryResolveFromTsproj("not xml", out var r, out var err));
        Assert.Null(r);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void FallbackProbeOrder_IsResolvedProgIdFirstThenLegacyChain()
    {
        var resolver = new XaeHostResolver(probeExeForProgId: _ => null);

        // 64-bit project: resolved 17.0 must be tried first, but the legacy
        // probe order is preserved after it so behaviour is never worse than
        // today when attach by the resolved ProgID fails.
        var order = resolver.AttachProbeOrder(XaeHostClass.Shell64);

        Assert.Equal("TcXaeShell.DTE.17.0", order[0]);
        Assert.Contains("TcXaeShell.DTE.16.0", order);
        Assert.Contains("TcXaeShell.DTE.15.0", order);
        Assert.Equal(order.Distinct().Count(), order.Count); // no dupes
    }

    [Fact]
    public void AttachProbeOrder_Shell32_PutsProgId150First()
    {
        var resolver = new XaeHostResolver(probeExeForProgId: _ => null);

        var order = resolver.AttachProbeOrder(XaeHostClass.Shell32);

        Assert.Equal("TcXaeShell.DTE.15.0", order[0]);
        Assert.Equal(order.Distinct().Count(), order.Count);
    }

    [Theory]
    [InlineData(@"""C:\Program Files (x86)\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe"" -Embedding", Exe32)]
    [InlineData(@"C:\Program Files\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe -Embedding", Exe64)]
    [InlineData(@"  C:\Program Files\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe  ", Exe64)]
    public void ExeFromLocalServer32_StripsQuotesAndArguments(string raw, string expected)
    {
        Assert.Equal(expected, XaeHostResolver.ExeFromLocalServer32(raw));
    }

    // The 32-bit shell registers its LocalServer32 only in the 32-bit registry
    // view; a 64-bit host reading the default view alone never saw it.
    [SkippableFact]
    public void RegistryProbe_FindsShellRegisteredOnlyInThe32BitView()
    {
        using var root32 = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.ClassesRoot, Microsoft.Win32.RegistryView.Registry32);
        using var progKey = root32.OpenSubKey($@"{XaeHostResolver.ProgId32}\CLSID");
        string? clsid = progKey?.GetValue(null) as string;
        using var ls = clsid is null ? null : root32.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
        Skip.If(ls?.GetValue(null) is not string, "The 32-bit XAE shell is not registered on this machine.");

        string? exe = XaeHostResolver.ProbeExeFromRegistry(XaeHostResolver.ProgId32);

        Assert.NotNull(exe);
        Assert.EndsWith("TcXaeShell.exe", exe, StringComparison.OrdinalIgnoreCase);
    }
}
