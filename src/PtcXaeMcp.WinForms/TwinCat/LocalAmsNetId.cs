using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// Issue #202: the local AmsNetId used to come from the TwinCAT registry key (…\Beckhoff\TwinCAT3\System, AmsNetId).
// That value can be stale: on the dev VM (TC 4026) it named a NetId the local AMS router no longer had, and an ADS
// ReadState on it timed out, while the router answered under its real NetId and the loopback. The router is the
// authority on its own NetId, so it is asked first. This file is the pure part: the resolution order and the brief
// cache, over injected sources, unit-tested without a router. The real sources (an ADS port opened on the router,
// the registry key) are in LocalAmsNetIdProbes.cs.

/// <summary>Where a <see cref="LocalAmsNetId"/> came from, in the order <see cref="LocalAmsNetIdResolver"/> asks.</summary>
internal enum LocalNetIdSource
{
    /// <summary>The local AMS router: the NetId it gave an ADS port opened on it.</summary>
    Router,

    /// <summary>The router did not answer: the AmsNetId in the TwinCAT registry key. It can be stale.</summary>
    Registry,

    /// <summary>Neither gave a NetId: the loopback, which the local router serves as the local system.</summary>
    Loopback,
}

/// <summary>The local system's AMS NetId and where it came from (issue #202).</summary>
/// <param name="NetId">The NetId, "a.b.c.d.e.f".</param>
/// <param name="Source">Where it came from.</param>
/// <param name="RouterError">Why the router gave no NetId; null when it did.</param>
internal sealed record LocalAmsNetId(string NetId, LocalNetIdSource Source, string? RouterError = null)
{
    /// <summary>Loopback AMS NetId: the local router always serves it as the local system.</summary>
    public const string LoopbackNetId = "127.0.0.1.1.1";

    /// <summary>True when the local AMS router itself gave the NetId.</summary>
    public bool IsFromRouter => Source == LocalNetIdSource.Router;

    /// <summary>
    /// The NetId, unless only the loopback fallback is known. The XAE route engine does not accept the loopback as
    /// the local system (broadcast search), so such a caller needs a real NetId or none.
    /// </summary>
    public string? NonLoopbackNetId => Source == LocalNetIdSource.Loopback ? null : NetId;

    /// <summary>The NetId with where it came from, for results and error texts.</summary>
    public string Describe() => Source switch
    {
        LocalNetIdSource.Router => $"{NetId} (local AMS router)",
        LocalNetIdSource.Registry =>
            $"{NetId} (TwinCAT registry, may be stale; the local AMS router did not answer: {RouterError})",
        _ => $"{NetId} (loopback; the local AMS router did not answer: {RouterError}; " +
             "the TwinCAT registry has no usable AmsNetId)",
    };
}

/// <summary>
/// Resolves the local system's AMS NetId (issue #202). Every caller that means "the local system" asks this one
/// resolver instead of reading the registry itself.
/// </summary>
internal interface ILocalAmsNetIdResolver
{
    /// <summary>The local AMS router's NetId; the registry's only when the router does not answer; else the loopback.
    /// Never throws.</summary>
    LocalAmsNetId Resolve();
}

/// <summary>The sources <see cref="LocalAmsNetIdResolver"/> asks. The real ones are <see cref="LocalAmsNetIdProbes"/>.</summary>
internal interface ILocalAmsNetIdSources
{
    /// <summary>The NetId the local AMS router reports. Throws when the router cannot be reached.</summary>
    string? ReadRouterNetId();

    /// <summary>The AmsNetId in the TwinCAT registry key, or null when there is none.</summary>
    string? ReadRegistryNetId();
}

/// <summary>
/// The local AMS NetId in the order of issue #202: the router first, the registry only when the router does not give
/// a NetId, the loopback last. A router answer is cached for <see cref="DefaultCacheFor"/>, never for good: the
/// router's NetId can change when TcSysSrv restarts. A fallback is not cached at all, so a router that answers again
/// is used on the very next call.
/// </summary>
internal sealed class LocalAmsNetIdResolver : ILocalAmsNetIdResolver
{
    /// <summary>How long a router answer is reused. Brief: it only spares the ADS port per call of a busy caller
    /// (the IBN heartbeat writes every 500 ms).</summary>
    public static readonly TimeSpan DefaultCacheFor = TimeSpan.FromSeconds(5);

    private static readonly Stopwatch ProcessClock = Stopwatch.StartNew();

    /// <summary>The process-wide resolver over the real router and registry: one cache for every caller.</summary>
    public static ILocalAmsNetIdResolver Shared { get; } = new LocalAmsNetIdResolver(new LocalAmsNetIdProbes());

    private readonly ILocalAmsNetIdSources _sources;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _cacheFor;
    private readonly object _gate = new();
    private LocalAmsNetId? _cached;
    private TimeSpan _cachedAt;

    /// <param name="sources">The router and registry to ask.</param>
    /// <param name="clock">Monotonic time; the process stopwatch by default.</param>
    /// <param name="cacheFor">How long a router answer is reused; <see cref="DefaultCacheFor"/> by default.</param>
    public LocalAmsNetIdResolver(ILocalAmsNetIdSources sources, Func<TimeSpan>? clock = null, TimeSpan? cacheFor = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _clock = clock ?? (() => ProcessClock.Elapsed);
        _cacheFor = cacheFor ?? DefaultCacheFor;
    }

    public LocalAmsNetId Resolve()
    {
        lock (_gate)
        {
            if (_cached is not null && _clock() - _cachedAt < _cacheFor) return _cached;
        }

        // Outside the lock: asking the router opens an ADS port.
        LocalAmsNetId fresh = ResolveUncached();

        lock (_gate)
        {
            if (fresh.IsFromRouter)
            {
                _cached = fresh;
                _cachedAt = _clock();
            }
            else
            {
                _cached = null;
            }
        }
        return fresh;
    }

    private LocalAmsNetId ResolveUncached()
    {
        string routerError;
        try
        {
            string? fromRouter = _sources.ReadRouterNetId();
            if (AmsNetIdText.IsUsable(fromRouter))
                return new LocalAmsNetId(fromRouter.Trim(), LocalNetIdSource.Router);
            routerError = string.IsNullOrWhiteSpace(fromRouter)
                ? "it reported no NetId"
                : $"it reported '{fromRouter.Trim()}', which is no usable AMS NetId";
        }
        catch (Exception ex)
        {
            routerError = ex.Message;
        }

        string? fromRegistry = null;
        try { fromRegistry = _sources.ReadRegistryNetId(); }
        catch (Exception) { /* an unreadable registry key: the loopback follows */ }
        if (AmsNetIdText.IsUsable(fromRegistry))
            return new LocalAmsNetId(fromRegistry.Trim(), LocalNetIdSource.Registry, routerError);

        return new LocalAmsNetId(LocalAmsNetId.LoopbackNetId, LocalNetIdSource.Loopback, routerError);
    }
}

/// <summary>AMS NetId text checks and the registry value's format.</summary>
internal static class AmsNetIdText
{
    private static readonly Regex LiteralPattern = new(@"^\d{1,3}(\.\d{1,3}){5}$", RegexOptions.CultureInvariant);

    /// <summary>True for six dot-separated numbers ("a.b.c.d.e.f"), surrounding whitespace ignored.</summary>
    public static bool IsLiteral([NotNullWhen(true)] string? s) => s is not null && LiteralPattern.IsMatch(s.Trim());

    /// <summary>A literal NetId whose six parts are bytes and that is not the empty NetId 0.0.0.0.0.0.</summary>
    public static bool IsUsable([NotNullWhen(true)] string? s)
    {
        if (!IsLiteral(s)) return false;
        bool allZero = true;
        foreach (string part in s.Trim().Split('.'))
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte b)) return false;
            allZero &= b == 0;
        }
        return !allZero;
    }

    /// <summary>
    /// The registry's AmsNetId value as "a.b.c.d.e.f": TwinCAT writes it as a 6-byte REG_BINARY; a REG_SZ that holds
    /// a literal NetId is taken as well. Null for anything else.
    /// </summary>
    public static string? FromRegistryValue(object? value) => value switch
    {
        byte[] { Length: 6 } b => string.Join(".", b.Select(x => x.ToString(CultureInfo.InvariantCulture))),
        string s when IsLiteral(s) => s.Trim(),
        _ => null,
    };
}

/// <summary>The AMS NetId an ADS tool connects to for its optional <c>amsNetId</c> (issue #202).</summary>
internal static class AdsTargetNetId
{
    /// <summary>The <c>amsNetId</c> value that selects the open solution's own target.</summary>
    public const string ProjectKeyword = "project";

    /// <summary>
    /// What <c>projectTargetNetId</c> returns for a project whose target is Local: the XAE writes no TargetNetId
    /// into the .tsproj then. Null instead means the target could not be read at all (no solution open).
    /// </summary>
    public const string ProjectTargetIsLocal = "";

    /// <summary>
    /// <paramref name="requested"/> when it is given. "project" is the open solution's own target (the .tsproj's
    /// TargetNetId, from <paramref name="projectTargetNetId"/>; the local system for a Local target), so it need
    /// not be looked up by hand. It is deliberately
    /// NOT the default: silently redirecting a NetId-less call from the local system to a PLANT is the kind of change
    /// that is noticed one step too late. Without a value it is the local system as <paramref name="local"/> resolves
    /// it: the local AMS router's NetId (the registry's, then the loopback, only when the router does not answer).
    /// </summary>
    public static string Resolve(string? requested, ILocalAmsNetIdResolver local, Func<string?> projectTargetNetId)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(projectTargetNetId);

        if (string.Equals(requested, ProjectKeyword, StringComparison.OrdinalIgnoreCase))
        {
            string target = projectTargetNetId()
                ?? throw new InvalidOperationException(
                    "amsNetId=\"project\" requested, but no TargetNetId could be read from the " +
                    "open solution's .tsproj. Pass the NetId explicitly.");
            return target == ProjectTargetIsLocal ? local.Resolve().NetId : target;
        }

        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        return local.Resolve().NetId;
    }
}
