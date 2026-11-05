using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #202: the local AMS NetId comes from the local AMS router; from the TwinCAT registry only when the router
/// does not answer; the loopback last. The registry value can be stale (on the dev VM an ADS ReadState on it timed
/// out), so it is ignored while the router answers. <see cref="LocalAmsNetIdResolver"/> runs here against scripted
/// sources and a fake clock. No router, no registry.
/// </summary>
public class LocalAmsNetIdTests
{
    private const string RouterNetId = "10.1.2.3.1.1";
    private const string StaleRegistryNetId = "172.16.0.9.1.1";

    private sealed class FakeSources : ILocalAmsNetIdSources
    {
        public Func<string?> Router { get; set; } = () => null;

        public Func<string?> Registry { get; set; } = () => null;

        public int RouterCalls { get; private set; }

        public int RegistryCalls { get; private set; }

        public string? ReadRouterNetId()
        {
            RouterCalls++;
            return Router();
        }

        public string? ReadRegistryNetId()
        {
            RegistryCalls++;
            return Registry();
        }
    }

    private sealed class FakeClock
    {
        public TimeSpan Now { get; set; } = TimeSpan.FromMinutes(1);
    }

    /// <summary>A resolver that always answers <see cref="Answer"/> and counts the calls.</summary>
    private sealed class FixedResolver(LocalAmsNetId answer) : ILocalAmsNetIdResolver
    {
        public LocalAmsNetId Answer { get; } = answer;

        public int Calls { get; private set; }

        public LocalAmsNetId Resolve()
        {
            Calls++;
            return Answer;
        }
    }

    private static Func<string?> Unreachable(string why) => () => throw new InvalidOperationException(why);

    private static (LocalAmsNetIdResolver Resolver, FakeSources Sources, FakeClock Clock) Create(
        Func<string?> router, Func<string?> registry)
    {
        var sources = new FakeSources { Router = router, Registry = registry };
        var clock = new FakeClock();
        return (new LocalAmsNetIdResolver(sources, () => clock.Now), sources, clock);
    }

    // ---- resolution order ------------------------------------------------------------------------------

    [Fact]
    public void RouterAnswers_ItsNetIdIsTheLocalNetId_AndTheRegistryIsNotAsked()
    {
        var (resolver, sources, _) = Create(() => RouterNetId, () => StaleRegistryNetId);

        Assert.Equal(new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router), resolver.Resolve());
        Assert.Equal(1, sources.RouterCalls);
        Assert.Equal(0, sources.RegistryCalls);
    }

    [Fact]
    public void StaleRegistryValue_IsIgnored_WhileTheRouterAnswers()
    {
        // The dev-VM case: the registry names a NetId the router no longer has.
        var (resolver, _, _) = Create(() => RouterNetId, () => StaleRegistryNetId);

        LocalAmsNetId local = resolver.Resolve();

        Assert.Equal(RouterNetId, local.NetId);
        Assert.True(local.IsFromRouter);
        Assert.Null(local.RouterError);
    }

    [Fact]
    public void RouterUnreachable_FallsBackToTheRegistry_WithTheRoutersError()
    {
        var (resolver, _, _) = Create(Unreachable("ADS router not reachable"), () => StaleRegistryNetId);

        Assert.Equal(new LocalAmsNetId(StaleRegistryNetId, LocalNetIdSource.Registry, "ADS router not reachable"),
            resolver.Resolve());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.0.0.0.0.0")]
    [InlineData("not-a-netid")]
    [InlineData("10.1.2.3")]
    [InlineData("256.1.2.3.1.1")]
    public void RouterWithoutAUsableNetId_FallsBackToTheRegistry(string? fromRouter)
    {
        var (resolver, _, _) = Create(() => fromRouter, () => StaleRegistryNetId);

        LocalAmsNetId local = resolver.Resolve();

        Assert.Equal(LocalNetIdSource.Registry, local.Source);
        Assert.Equal(StaleRegistryNetId, local.NetId);
        Assert.False(string.IsNullOrWhiteSpace(local.RouterError));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.0.0.0")]
    [InlineData("garbage")]
    public void NeitherRouterNorRegistry_IsTheLoopback(string? fromRegistry)
    {
        var (resolver, _, _) = Create(Unreachable("no router"), () => fromRegistry);

        Assert.Equal(new LocalAmsNetId(LocalAmsNetId.LoopbackNetId, LocalNetIdSource.Loopback, "no router"),
            resolver.Resolve());
    }

    [Fact]
    public void AThrowingRegistry_IsTheLoopback_ResolveNeverThrows()
    {
        var (resolver, _, _) = Create(Unreachable("no router"), () => throw new UnauthorizedAccessException("denied"));

        LocalAmsNetId local = resolver.Resolve();

        Assert.Equal(LocalNetIdSource.Loopback, local.Source);
        Assert.Equal("127.0.0.1.1.1", local.NetId);
    }

    [Fact]
    public void SurroundingWhitespace_IsTrimmed()
    {
        var (resolver, _, _) = Create(() => $"  {RouterNetId} ", () => null);

        Assert.Equal(RouterNetId, resolver.Resolve().NetId);
    }

    // ---- brief cache -----------------------------------------------------------------------------------

    [Fact]
    public void ARouterAnswer_IsReused_WithinTheCacheWindow()
    {
        var (resolver, sources, clock) = Create(() => RouterNetId, () => null);

        resolver.Resolve();
        clock.Now += LocalAmsNetIdResolver.DefaultCacheFor - TimeSpan.FromMilliseconds(1);
        LocalAmsNetId again = resolver.Resolve();

        Assert.Equal(RouterNetId, again.NetId);
        Assert.Equal(1, sources.RouterCalls);
    }

    [Fact]
    public void ARouterAnswer_IsNotCachedForGood_ANewRouterNetIdIsSeenAfterTheWindow()
    {
        // TcSysSrv restarted under another NetId.
        string current = RouterNetId;
        var (resolver, sources, clock) = Create(() => current, () => null);
        Assert.Equal(RouterNetId, resolver.Resolve().NetId);

        current = "10.1.2.4.1.1";
        clock.Now += LocalAmsNetIdResolver.DefaultCacheFor;

        Assert.Equal("10.1.2.4.1.1", resolver.Resolve().NetId);
        Assert.Equal(2, sources.RouterCalls);
    }

    [Fact]
    public void AfterTheWindow_ARouterThatStoppedAnswering_FallsBackToTheRegistry()
    {
        bool up = true;
        var (resolver, _, clock) = Create(() => up ? RouterNetId : throw new InvalidOperationException("down"),
            () => StaleRegistryNetId);
        Assert.True(resolver.Resolve().IsFromRouter);

        up = false;
        clock.Now += LocalAmsNetIdResolver.DefaultCacheFor;

        Assert.Equal(new LocalAmsNetId(StaleRegistryNetId, LocalNetIdSource.Registry, "down"), resolver.Resolve());
    }

    [Fact]
    public void AFallback_IsNotCached_ARouterThatAnswersAgainIsUsedOnTheNextCall()
    {
        bool up = false;
        var (resolver, sources, _) = Create(() => up ? RouterNetId : throw new InvalidOperationException("down"),
            () => StaleRegistryNetId);
        Assert.Equal(LocalNetIdSource.Registry, resolver.Resolve().Source);

        up = true;   // same instant: nothing cached in between

        Assert.Equal(new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router), resolver.Resolve());
        Assert.Equal(2, sources.RouterCalls);
    }

    // ---- the result ------------------------------------------------------------------------------------

    [Fact]
    public void NonLoopbackNetId_IsNullOnlyForTheLoopbackFallback()
    {
        Assert.Equal(RouterNetId, new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router).NonLoopbackNetId);
        Assert.Equal(StaleRegistryNetId,
            new LocalAmsNetId(StaleRegistryNetId, LocalNetIdSource.Registry, "down").NonLoopbackNetId);
        Assert.Null(new LocalAmsNetId(LocalAmsNetId.LoopbackNetId, LocalNetIdSource.Loopback, "down").NonLoopbackNetId);
    }

    [Fact]
    public void Describe_NamesTheSource_AndWhyTheRouterGaveNoNetId()
    {
        Assert.Equal("10.1.2.3.1.1 (local AMS router)", new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router).Describe());

        string registry = new LocalAmsNetId(StaleRegistryNetId, LocalNetIdSource.Registry, "down").Describe();
        Assert.StartsWith("172.16.0.9.1.1 (TwinCAT registry, may be stale;", registry, StringComparison.Ordinal);
        Assert.Contains("did not answer: down", registry, StringComparison.Ordinal);

        string loopback = new LocalAmsNetId(LocalAmsNetId.LoopbackNetId, LocalNetIdSource.Loopback, "down").Describe();
        Assert.StartsWith("127.0.0.1.1.1 (loopback;", loopback, StringComparison.Ordinal);
        Assert.Contains("did not answer: down", loopback, StringComparison.Ordinal);
    }

    // ---- NetId text and the registry value ---------------------------------------------------------------

    [Theory]
    [InlineData("10.1.2.3.1.1", true)]
    [InlineData(" 10.1.2.3.1.1 ", true)]
    [InlineData("255.255.255.255.255.255", true)]
    [InlineData("0.0.0.0.0.0", false)]
    [InlineData("256.1.2.3.1.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("10.1.2.3.1.1.1", false)]
    [InlineData("a.b.c.d.e.f", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUsable_SixByteParts_NotTheEmptyNetId(string? text, bool usable)
    {
        Assert.Equal(usable, AmsNetIdText.IsUsable(text));
    }

    [Fact]
    public void RegistryValue_SixBytes_IsTheDottedNetId()
    {
        Assert.Equal("10.1.2.3.1.1", AmsNetIdText.FromRegistryValue(new byte[] { 10, 1, 2, 3, 1, 1 }));
    }

    [Fact]
    public void RegistryValue_ALiteralString_IsTaken_TrimmedAsWell()
    {
        Assert.Equal("10.1.2.3.1.1", AmsNetIdText.FromRegistryValue(" 10.1.2.3.1.1 "));
    }

    [Fact]
    public void RegistryValue_AnythingElse_IsNone()
    {
        Assert.Null(AmsNetIdText.FromRegistryValue(null));
        Assert.Null(AmsNetIdText.FromRegistryValue(new byte[] { 10, 1, 2, 3, 1 }));
        Assert.Null(AmsNetIdText.FromRegistryValue(new byte[] { 10, 1, 2, 3, 1, 1, 1 }));
        Assert.Null(AmsNetIdText.FromRegistryValue("not-a-netid"));
        Assert.Null(AmsNetIdText.FromRegistryValue(42));
    }

    // ---- the default NetId of the ADS tools ------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdsTools_WithoutANetId_UseTheLocalRoutersNetId(string? requested)
    {
        var local = new FixedResolver(new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router));

        Assert.Equal(RouterNetId, AdsTargetNetId.Resolve(requested, local, () => "never asked"));
        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public void AdsTools_WithoutANetId_AndNoRouter_UseTheResolversFallback()
    {
        // Through the real resolver: the registry's NetId only because the router did not answer.
        var (resolver, _, _) = Create(Unreachable("no router"), () => StaleRegistryNetId);

        Assert.Equal(StaleRegistryNetId, AdsTargetNetId.Resolve(null, resolver, () => null));
    }

    [Fact]
    public void AdsTools_WithoutANetId_IgnoreAStaleRegistry_WhileTheRouterAnswers()
    {
        var (resolver, _, _) = Create(() => RouterNetId, () => StaleRegistryNetId);

        Assert.Equal(RouterNetId, AdsTargetNetId.Resolve(null, resolver, () => null));
    }

    [Fact]
    public void AdsTools_AnExplicitNetId_IsUsedAsGiven_TheResolverIsNotAsked()
    {
        var local = new FixedResolver(new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router));

        Assert.Equal("192.168.1.5.1.1", AdsTargetNetId.Resolve("192.168.1.5.1.1", local, () => null));
        Assert.Equal(0, local.Calls);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("PROJECT")]
    public void AdsTools_Project_IsTheOpenSolutionsTarget_TheResolverIsNotAsked(string requested)
    {
        var local = new FixedResolver(new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router));

        Assert.Equal("192.168.1.5.1.1", AdsTargetNetId.Resolve(requested, local, () => "192.168.1.5.1.1"));
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public void AdsTools_Project_WithoutATargetNetId_IsRefused_NotTheLocalSystem()
    {
        var local = new FixedResolver(new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router));

        var ex = Assert.Throws<InvalidOperationException>(() => AdsTargetNetId.Resolve("project", local, () => null));

        Assert.Contains("no TargetNetId could be read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public void AdsTools_Project_WithALocalTarget_IsTheLocalSystem()
    {
        // The XAE writes no TargetNetId into the .tsproj for a Local target; seen live as a refusal of "project".
        var local = new FixedResolver(new LocalAmsNetId(RouterNetId, LocalNetIdSource.Router));

        Assert.Equal(RouterNetId, AdsTargetNetId.Resolve("project", local, () => AdsTargetNetId.ProjectTargetIsLocal));
        Assert.Equal(1, local.Calls);
    }
}
