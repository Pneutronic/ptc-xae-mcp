using System.Xml;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- Runtime control (feature/runtime-control) ---------------------------
    //
    // BroadcastSearch and AddRoute use the TIRR (Route Settings) tree item via
    // the ConsumeXml/ProduceXml pattern on ITcSmTreeItem.
    // SetTarget uses ITcSysManager.SetTargetNetId() and ITcSysManager7.ConfigurationManager.
    // RestartRuntime calls ITcSysManager.StartRestartTwinCAT().
    //
    // Infosys refs:
    //   https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242941323.html (broadcast search)
    //   https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242939787.html (add route)
    //   https://infosys.beckhoff.com/content/1033/tc3_automationinterface/650882187.html (set target platform)
    //   https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242929035.html (activate + restart)

    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(15);

    // Broadcast discovery is inherently a *local-network* operation: the TIRR
    // ConsumeXml route search is only serviced when the active project target
    // is the local system — and specifically the local-system AmsNetId, NOT
    // the loopback "127.0.0.1.1.1" (verified live on 4026.22: a loopback
    // target still fails ConsumeXml 'TIRR'). Rather than push a set-target →
    // search → restore sequence onto the caller (issue #44), self-scope: read
    // the current target, swap to the real local AmsNetId only if needed, and
    // restore it in a finally even on failure. Single-XAE constraint still
    // holds — this is a transient swap within one session, not a parallel attach.
    // The local-system AmsNetId is the local AMS router's (issue #202): the
    // registry value this used to read can be stale. Only the loopback
    // fallback is useless here, so it counts as "not resolved".
    public IReadOnlyList<BroadcastTarget> BroadcastSearch(string? searchTarget) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysObj = RequireSystemManager(dte);
            dynamic sysMan = sysObj;
            dynamic routes = sysMan.LookupTreeItem("TIRR");

            LocalAmsNetId local = LocalAmsNetIdResolver.Shared.Resolve();
            string? localNetId = local.NonLoopbackNetId;

            string? originalNetId = null;
            try { originalNetId = (string)sysMan.GetTargetNetId(); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* read unsupported: skip restore */ }

            bool swapped = false;
            if (localNetId is not null
                && !string.IsNullOrEmpty(originalNetId)
                && !string.Equals(originalNetId, localNetId, StringComparison.OrdinalIgnoreCase))
            {
                try { sysMan.SetTargetNetId(localNetId); swapped = true; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Broadcast search requires the project target to be the local system, " +
                        $"but switching from '{originalNetId}' to the local AmsNetId {local.Describe()} " +
                        $"failed: {ex.Message}. Call twincat_set_target with the local NetId before searching.", ex);
                }
            }

            try
            {
                string xml = searchTarget is not null
                    ? $"<TreeItem><RoutePrj><TargetList><Search>{EscapeXml(searchTarget)}</Search></TargetList></RoutePrj></TreeItem>"
                    : "<TreeItem><RoutePrj><TargetList><BroadcastSearch>true</BroadcastSearch></TargetList></RoutePrj></TreeItem>";

                try
                {
                    routes.ConsumeXml(xml);
                }
                catch (Exception ex) when (ex.Message.Contains("local target system", StringComparison.OrdinalIgnoreCase)
                                         || ex.Message.Contains("TIRR", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Broadcast search requires the project target to be the local system. " +
                        (localNetId is null
                            ? $"The local AmsNetId could not be resolved: the local AMS router did not answer ({local.RouterError}) "
                              + "and the TwinCAT registry has no usable AmsNetId — "
                              + "call twincat_set_target with the local NetId before searching."
                            : $"Self-scoping to the local AmsNetId {local.Describe()} did not satisfy the "
                              + $"route engine (original target '{originalNetId}', swapped={swapped})."), ex);
                }

                // TC collects broadcast replies asynchronously over the network;
                // ProduceXml() must be called AFTER the scan window closes.
                // A directed-host search is typically faster (< 500 ms) whereas a
                // full broadcast needs up to 3 s depending on subnet size.
                Thread.Sleep(searchTarget is null ? 3000 : 500);

                string rawXml = (string)routes.ProduceXml();
                var (parsedTargets, _) = ParseBroadcastResult(rawXml);
                if (parsedTargets.Count == 0)
                {
                    // Capture raw XML for diagnostic: if the XPath missed the real
                    // elements, the raw XML tells us the actual schema TC returned.
                    // Truncated to avoid flooding callers; use twincat_broadcast_search
                    // with a diagnostic flag to see the full XML.
                    string diag = rawXml.Length > 500 ? rawXml[..500] + "…" : rawXml;
                    throw new InvalidOperationException(
                        $"Broadcast search completed but returned 0 targets. " +
                        $"Swap: {(swapped ? $"local={local.Describe()}" : $"no (local={local.Describe()}, original={originalNetId ?? "null"})")}. " +
                        $"Raw XML: {diag}");
                }
                return parsedTargets;
            }
            finally
            {
                if (swapped)
                    try { sysMan.SetTargetNetId(originalNetId); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* best effort restore */ }
            }
        }, BroadcastTimeout);

    public string AddRoute(string netId, string? ipAddr, string? hostName, string? userName, string? password, bool projectRoute) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysObj = RequireSystemManager(dte);
            dynamic routes = ((dynamic)sysObj).LookupTreeItem("TIRR");

            string routeXml = projectRoute
                ? BuildProjectRouteXml(netId, ipAddr, hostName)
                : BuildSystemRouteXml(netId, ipAddr, hostName, userName, password);

            string xml = $"<TreeItem><ItemName>Route Settings</ItemName><PathName>TIRR</PathName>" +
                         $"<RoutePrj><TargetList><BroadcastSearch>false</BroadcastSearch></TargetList>" +
                         $"{routeXml}</RoutePrj></TreeItem>";

            routes.ConsumeXml(xml);
            string routeType = projectRoute ? "project route" : "system route";
            return $"Added {routeType} to {netId}.";
        }, RouteTimeout);

    public string SetTarget(string netId, string? platform) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysObj = RequireSystemManager(dte);
            dynamic sysMan = sysObj;

            sysMan.SetTargetNetId(netId);

            // #171 audit (issue #152): everything after SetTargetNetId runs in its own busy scope,
            // and the NetId is read back — a setter that returns is not proof.
            string? readBack = AfterMutation(() => ComCall.ReadProp(() => (string?)((dynamic)sysObj).GetTargetNetId(), null));
            // Compared only when both are literal AMS NetIds (a symbolic target may read back as its NetId).
            if (readBack is not null && IsLiteralNetId(readBack) && IsLiteralNetId(netId) &&
                !string.Equals(readBack.Trim(), netId.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SetTargetNetId('{netId}') did not land: GetTargetNetId reads back '{readBack}'.");
            string confirmed = readBack is null ? " (read-back unavailable on this XAE)"
                : IsLiteralNetId(readBack) && IsLiteralNetId(netId) ? " (read-back confirmed)"
                : $" (reads back as '{readBack}')";

            if (platform is null)
                return $"Target NetId set to {netId}{confirmed}.";

            return AfterMutation(() =>
            {
                try
                {
                    dynamic configManager = ((dynamic)sysObj).ConfigurationManager;
                    string previous = (string)configManager.ActiveTargetPlatform;
                    if (!string.Equals(previous, platform, StringComparison.OrdinalIgnoreCase))
                        configManager.ActiveTargetPlatform = platform;
                    string now = (string)configManager.ActiveTargetPlatform;
                    return string.Equals(now, platform, StringComparison.OrdinalIgnoreCase)
                        ? $"Target set to {netId}{confirmed} (platform: {previous} → {platform}, read back)."
                        : $"[Error] Target NetId set to {netId}{confirmed}, but the platform change to '{platform}' did not land " +
                          $"(ActiveTargetPlatform reads back '{now}').";
                }
                catch (Exception ex) when (ComCall.NotBusy(ex)) {
                    return $"Target NetId set to {netId}{confirmed}. Platform change to '{platform}' failed: {ex.Message}";
                }
            });
        }, RouteTimeout);

    // Issue #58: the platform the XAE reports (ConfigurationManager.ActiveTargetPlatform, the toolbar's
    // solution platform) and the target NetId. Read-only; a value the XAE refuses to give stays null.
    public ProjectTargetInfo ReadProjectTarget() =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysObj = RequireSystemManager(dte);
            string? netId = ComCall.ReadProp(() => (string?)((dynamic)sysObj).GetTargetNetId(), null);
            string? platform = ComCall.ReadProp(() => (string?)((dynamic)sysObj).ConfigurationManager.ActiveTargetPlatform, null);
            if (string.IsNullOrWhiteSpace(platform))
            {
                // The active solution configuration is named 'Configuration|Platform'.
                string? cfg = ComCall.ReadProp(() => (string?)dte.Solution.SolutionBuild.ActiveConfiguration.Name, null);
                int bar = cfg?.IndexOf('|') ?? -1;
                platform = bar >= 0 ? cfg![(bar + 1)..].Trim() : null;
            }

            IReadOnlyList<string> umr;
            using (var umrManager = new UmrProcessManager()) umr = umrManager.InstanceNetIds();
            // The resolver is used without a guard by BroadcastSearch above; it falls back to the loopback.
            var local = new List<string> { LocalAmsNetId.LoopbackNetId, LocalAmsNetIdResolver.Shared.Resolve().NetId };
            return new ProjectTargetInfo(platform, netId, umr, local);
        }, RouteTimeout);

    public string RestartRuntime() =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysObj = RequireSystemManager(dte);
            ((dynamic)sysObj).StartRestartTwinCAT();
            return "Runtime restart initiated on target.";
        }, RouteTimeout);

    // ---- Helpers ---------------------------------------------------------------

    private static bool IsLiteralNetId(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^\d{1,3}(\.\d{1,3}){5}$");

    // Returns (parsed targets, raw XML for diagnostics when list is empty).
    internal static (List<BroadcastTarget> Targets, string RawXml) ParseBroadcastResult(string xml)
    {
        var targets = new List<BroadcastTarget>();
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            // Try the full path first, then progressively broader selectors to
            // handle schema variations across TC3 versions (e.g. 4026.22 may use
            // SearchTarget instead of Target or a different nesting level).
            //
            // C-11 (issue #152): XmlNode.SelectNodes never returns null for a well-formed
            // XPath — it returns an empty (non-null) XmlNodeList when nothing matches. The
            // previous "?? ?? ??" chain therefore only advanced past the first selector when
            // the XPath itself threw, never merely because it matched 0 nodes, so the 2nd/3rd/
            // 4th selectors were dead code for any real document (pinned down by
            // ParseBroadcastResultTests.FallbackSelectors_NeverEngage_OnlyFirstSelectorMatches,
            // written against the unfixed method under #174). Chain on Count instead so a
            // document shaped for a narrower selector is actually reached.
            XmlNodeList nodes = doc.SelectNodes("//TreeItem/RoutePrj/TargetList/Target")!;
            if (nodes.Count == 0) nodes = doc.SelectNodes("//TargetList/Target")!;
            if (nodes.Count == 0) nodes = doc.SelectNodes("//TargetList/SearchTarget")!;
            if (nodes.Count == 0) nodes = doc.SelectNodes("//Target")!;
            foreach (XmlNode node in nodes)
            {
                targets.Add(new BroadcastTarget(
                    Name:    node.SelectSingleNode("Name")?.InnerText    ?? node.Attributes?["Name"]?.Value    ?? "",
                    NetId:   node.SelectSingleNode("NetId")?.InnerText   ?? node.Attributes?["NetId"]?.Value   ?? "",
                    IpAddr:  node.SelectSingleNode("IpAddr")?.InnerText  ?? node.Attributes?["IpAddr"]?.Value  ?? "",
                    Version: node.SelectSingleNode("Version")?.InnerText ?? node.Attributes?["Version"]?.Value ?? "",
                    Os:      node.SelectSingleNode("OS")?.InnerText      ?? node.Attributes?["OS"]?.Value      ?? ""));
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return (targets, xml);
    }

    private static string BuildProjectRouteXml(string netId, string? ipAddr, string? hostName)
    {
        string addr = hostName is not null
            ? $"<HostName>{EscapeXml(hostName)}</HostName>"
            : $"<IpAddr>{EscapeXml(ipAddr ?? "")}</IpAddr>";
        return $"<AddProjectRoute><Name>Route-{netId}</Name><NetId>{EscapeXml(netId)}</NetId>{addr}</AddProjectRoute>";
    }

    private static string BuildSystemRouteXml(string netId, string? ipAddr, string? hostName, string? userName, string? password)
    {
        string addr = hostName is not null
            ? $"<RemoteHostName>{EscapeXml(hostName)}</RemoteHostName>"
            : $"<RemoteIpAddr>{EscapeXml(ipAddr ?? "")}</RemoteIpAddr>";
        string creds = userName is not null
            ? $"<UserName>{EscapeXml(userName)}</UserName><Password>{EscapeXml(password ?? "")}</Password><NoEncryption/>"
            : "<NoEncryption/>";
        return $"<AddRoute><RemoteName>Route-{netId}</RemoteName><RemoteNetId>{EscapeXml(netId)}</RemoteNetId>" +
               $"{addr}{creds}</AddRoute>";
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");
}
