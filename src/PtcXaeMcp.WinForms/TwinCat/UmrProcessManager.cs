using System.Diagnostics;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Manages the lifecycle of a TwinCAT 3 Usermode Runtime (UMR) instance.
///
/// On TC3 4026 a UMR is a *registered instance* living under
/// <c>%ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes\&lt;name&gt;\</c> with its own
/// config (<c>3.1\TcRegistry.xml</c>, AmsNetId, boot dir). It must be launched
/// the way Beckhoff's per-instance <c>Start.bat</c> does — working directory =
/// the instance folder, args <c>-t bin -i path -n "&lt;name&gt;" -c .\3.1</c>
/// (<c>-i path</c> = derive the AmsNetId from the instance config; a literal NetId
/// pinned in the instance's own <c>Start.bat</c> is passed instead). Hand-rolling
/// <c>TcSystemServiceUm.exe -c 3.1</c> from an arbitrary working directory points
/// it at a folder with no valid instance behind it and always yields
/// <c>TcSysSrvUm: init system failed</c> (issues #54, #61).
///
/// Once running the process is controlled via stdin commands:
///   'c' = Config mode, 'r' = Run mode, 's' = query state, 'x' = exit.
/// </summary>
internal sealed class UmrProcessManager : IDisposable
{
    // Fallback used when TwinCatPaths.GetInstallDir() can't resolve an actual
    // install (e.g. not installed on this machine) — same default as before.
    private const string FallbackInstallDir = @"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1";

    public static readonly string DefaultExePath =
        Path.Combine(TwinCatPaths.GetInstallDir() ?? FallbackInstallDir, "Runtimes", "bin", "TcSystemServiceUm.exe");

    /// <summary>Roots searched (in order) for registered UMR instance sub-directories.</summary>
    public static readonly string[] RuntimesRoots =
    {
        // Beckhoff-shipped template instances (e.g. UmRT_Template) live here —
        // derived from the single TwinCatPaths install-dir resolution (issue
        // #157 item 9) instead of a hardcoded duplicate.
        Path.Combine(TwinCatPaths.GetInstallDir() ?? FallbackInstallDir, "Runtimes"),
        // User-created instances are placed here by TcSysUI. Not under the
        // install dir (it's runtime data, not program files), so it stays fixed.
        @"C:\ProgramData\Beckhoff\TwinCAT\3.1\Runtimes",
    };

    /// <summary>Legacy single-root alias; first entry of <see cref="RuntimesRoots"/>.</summary>
    public static string RuntimesRoot => RuntimesRoots[0];

    /// <summary>Instance name preferred when the caller doesn't specify one and multiple exist.</summary>
    private const string DefaultInstanceName = "UmRT_Template";

    /// <summary>
    /// How long <see cref="Start"/> watches early stdout for an init failure
    /// before declaring the runtime up. The failure markers print within a few
    /// hundred ms; a successful start has no distinct "ready" line (both the
    /// good and bad paths print "TcSysSrvUm: started"), so we must wait out
    /// this grace window to be sure no failure followed.
    /// </summary>
    private const int StartupGraceMs = 4000;

    // Lines that indicate TcSysSrvUm came up but failed to initialize the
    // system and is now blocked on a "press any key to exit" prompt.
    private static readonly string[] FailureMarkers =
    {
        "init system failed",
        "press any key to exit",
    };

    private Process? _process;
    private readonly object _outputLock = new();
    private readonly StringBuilder _output = new();
    private volatile bool _initFailed;

    /// <summary>Guards <see cref="_process"/> and <see cref="State"/> — both are
    /// read/written from the WinForms UI thread AND from MCP tool threads
    /// (issue #154, A-7). Every method that reads or mutates either now holds
    /// this for the duration of its process-state decision; the actual
    /// blocking process I/O (Start/Kill/WaitForExit) still happens outside the
    /// lock so a slow OS call cannot stall an unrelated status read.</summary>
    private readonly object _processLock = new();

    public string State { get; private set; } = "Stopped";

    /// <summary>The AMS NetId the running instance was started with (fixed in Start.bat, or from its
    /// TcRegistry.xml for -i path); null when unknown. Guarded by <see cref="_processLock"/>.</summary>
    private string? _effectiveNetId;

    public bool IsRunning
    {
        get { lock (_processLock) return _process is not null && !_process.HasExited; }
    }

    public event Action? StateChanged;

    /// <summary>
    /// Names of the UMR instances registered on this machine (sub-directories of any
    /// <see cref="RuntimesRoots"/> entry that contain both <c>Start.bat</c> and
    /// <c>3.1\TcRegistry.xml</c>). Empty if none are installed.
    /// </summary>
    public IReadOnlyList<string> EnumerateInstances()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var root in RuntimesRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, "Start.bat")) &&
                    File.Exists(Path.Combine(dir, "3.1", "TcRegistry.xml")))
                {
                    var name = Path.GetFileName(dir)!;
                    if (seen.Add(name))
                        names.Add(name);
                }
            }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Returns the full instance directory for <paramref name="name"/>, searching
    /// all <see cref="RuntimesRoots"/> in order.
    /// </summary>
    /// <summary>
    /// The AMS NetIds of every registered instance (fixed in Start.bat, else from TcRegistry.xml), for the
    /// access policy's <c>local</c>. An instance whose NetId cannot be read is left out. Never throws.
    /// </summary>
    public IReadOnlyList<string> InstanceNetIds()
    {
        var ids = new List<string>();
        try
        {
            foreach (var name in EnumerateInstances())
            {
                string dir = FindInstanceDir(name);
                string? id = TryReadFile(Path.Combine(dir, "Start.bat")) is { } bat ? ParseStartBatNetId(bat) : null;
                id ??= TryReadFile(Path.Combine(dir, "3.1", "TcRegistry.xml")) is { } reg ? ParseRegistryNetId(reg) : null;
                if (id is not null) ids.Add(id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return ids;
    }

    private static string FindInstanceDir(string name)
    {
        foreach (var root in RuntimesRoots)
        {
            var dir = Path.Combine(root, name);
            if (Directory.Exists(dir))
                return dir;
        }
        throw new InvalidOperationException(
            $"Instance directory for '{name}' not found in any known runtime root.");
    }

    /// <summary>
    /// Resolve which instance to start: the caller's <paramref name="instanceName"/>
    /// if given, else the single registered instance, else <c>UmRT_Default</c>.
    /// Throws with the available list when the choice is ambiguous or absent.
    /// </summary>
    private string ResolveInstance(string? instanceName)
    {
        var available = EnumerateInstances();
        if (available.Count == 0)
            throw new InvalidOperationException(
                $"No registered UMR instance found under: {string.Join(", ", RuntimesRoots)}. " +
                "Create one via the TwinCAT setup / TcSysUI before starting the Usermode Runtime.");

        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            var match = available.FirstOrDefault(
                n => n.Equals(instanceName, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new InvalidOperationException(
                $"UMR instance '{instanceName}' not found. Available: {string.Join(", ", available)}.");
        }

        if (available.Count == 1)
            return available[0];

        var def = available.FirstOrDefault(
            n => n.Equals(DefaultInstanceName, StringComparison.OrdinalIgnoreCase));
        return def ?? throw new InvalidOperationException(
            $"Multiple UMR instances exist ({string.Join(", ", available)}); " +
            "specify which one to start.");
    }

    /// <summary>
    /// Start a registered UMR instance the documented way (replicating its
    /// <c>Start.bat</c>: working dir = instance folder, args
    /// <c>-t bin -i path -n "&lt;name&gt;" -c .\3.1</c>), while still redirecting
    /// stdout so an <c>init system failed</c> is detected and surfaced as an
    /// error instead of a false <c>State: Config</c>.
    /// </summary>
    /// <param name="instanceName">Registered instance to start; null = auto-resolve.</param>
    /// <param name="exePath">Override for TcSystemServiceUm.exe; null = default install path.</param>
    public string Start(string? instanceName = null, string? exePath = null)
    {
        lock (_processLock)
        {
            if (_process is not null && !_process.HasExited)
                throw new InvalidOperationException("UMR process is already running. Call Stop first.");
        }

        string exe = exePath ?? DefaultExePath;
        if (!File.Exists(exe))
            throw new InvalidOperationException($"TcSystemServiceUm.exe not found at: {exe}");

        string instance = ResolveInstance(instanceName);
        string instanceDir = FindInstanceDir(instance);

        // An instance's own Start.bat may pin the AmsNetId (-i "192.168.250.20.1.1")
        // instead of Beckhoff's default -i path; honour it, otherwise the UMR comes
        // up with the NetId from its TcRegistry.xml and nothing addressing the
        // pinned NetId reaches it (issue #4).
        string? fixedNetId = TryReadFile(Path.Combine(instanceDir, "Start.bat")) is { } bat
            ? ParseStartBatNetId(bat)
            : null;
        string netIdArg = fixedNetId is null ? "path" : $"\"{fixedNetId}\"";
        string? effectiveNetId = fixedNetId
            ?? (TryReadFile(Path.Combine(instanceDir, "3.1", "TcRegistry.xml")) is { } reg
                ? ParseRegistryNetId(reg)
                : null);

        _initFailed = false;
        lock (_outputLock) _output.Clear();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                // Mirrors Start.bat: -t bin -i <path|NetId> -n "<name>" -c .\3.1
                Arguments = $"-t bin -i {netIdArg} -n \"{instance}\" -c .\\3.1",
                WorkingDirectory = instanceDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = false,
            },
            EnableRaisingEvents = true,
        };
        process.Exited += (_, _) =>
        {
            lock (_processLock) State = "Stopped";
            StateChanged?.Invoke();
        };
        process.OutputDataReceived += OnOutputData;

        lock (_processLock) _process = process;
        process.Start();
        process.BeginOutputReadLine();

        // Watch early stdout: TcSysSrvUm prints "started" on both the good and
        // the failed path, so the only reliable signal is the *absence* of a
        // failure marker (and a fast exit) within the grace window. Deliberately
        // NOT holding _processLock across this blocking wait — a concurrent
        // status read (IsRunning/State) must not stall for the whole grace
        // window (issue #154, A-7).
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < StartupGraceMs)
        {
            if (process.HasExited || _initFailed)
                break;
            Thread.Sleep(100);
        }

        if (process.HasExited || _initFailed)
        {
            string captured = SnapshotOutput();
            try { process.CancelOutputRead(); } catch { }
            try { if (!process.HasExited) process.Kill(); } catch { }
            process.Dispose();
            lock (_processLock)
            {
                if (ReferenceEquals(_process, process)) _process = null;
                State = "Stopped";
            }
            StateChanged?.Invoke();
            throw new InvalidOperationException(
                $"TcSysSrvUm (instance '{instance}') started but failed to initialize the runtime. " +
                "Output:\n" + (captured.Length == 0 ? "(no output captured)" : captured));
        }

        lock (_processLock)
        {
            State = "Config";
            _effectiveNetId = effectiveNetId;
        }
        StateChanged?.Invoke();
        string netIdInfo = effectiveNetId is null
            ? "NetId unknown (-i path, no AmsNetId in TcRegistry.xml)"
            : fixedNetId is null
                ? $"NetId {effectiveNetId} (-i path, from TcRegistry.xml)"
                : $"NetId {effectiveNetId} (fixed in Start.bat)";
        return $"UMR instance '{instance}' started (PID={process.Id}), {netIdInfo}. State: Config.";
    }

    private static string? TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static readonly System.Text.RegularExpressions.Regex StartBatNetIdArg = new(
        @"(?:^|\s)-i\s+(?:""(?<v>[^""]*)""|(?<v>\S+))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex AmsNetIdFormat = new(
        @"^\d{1,3}(\.\d{1,3}){5}$");

    /// <summary>
    /// The fixed AmsNetId an instance's <c>Start.bat</c> passes as <c>-i</c> to
    /// TcSystemServiceUm.exe, or null when it uses <c>-i path</c>, passes no
    /// <c>-i</c>, or passes something that is not a literal NetId (e.g. a
    /// <c>%VAR%</c>) — the caller then keeps Beckhoff's default <c>-i path</c>.
    /// </summary>
    internal static string? ParseStartBatNetId(string startBat)
    {
        foreach (var raw in startBat.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::", StringComparison.Ordinal))
                continue;
            if (!line.Contains("TcSystemServiceUm", StringComparison.OrdinalIgnoreCase))
                continue;
            var m = StartBatNetIdArg.Match(line);
            if (!m.Success) continue;
            var value = m.Groups["v"].Value.Trim();
            return AmsNetIdFormat.IsMatch(value) ? value : null;
        }
        return null;
    }

    /// <summary>
    /// The AmsNetId stored in an instance's <c>3.1\TcRegistry.xml</c>
    /// (<c>&lt;Value Name="AmsNetId" Type="BIN"&gt;C0A8FA140101&lt;/Value&gt;</c>),
    /// formatted as dotted NetId; null when absent or malformed.
    /// </summary>
    internal static string? ParseRegistryNetId(string tcRegistryXml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            tcRegistryXml,
            @"<Value\s+Name=""AmsNetId""[^>]*>\s*(?<hex>[0-9A-Fa-f]{12})\s*</Value>");
        if (!m.Success) return null;
        var hex = m.Groups["hex"].Value;
        return string.Join('.', Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)));
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (_outputLock) _output.AppendLine(e.Data);
        foreach (var marker in FailureMarkers)
        {
            if (e.Data.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                _initFailed = true;
                break;
            }
        }
    }

    private string SnapshotOutput()
    {
        lock (_outputLock) return _output.ToString().TrimEnd();
    }

    /// <summary>
    /// Switches the running instance to Config or Run over ADS on its effective NetId and reports
    /// success only once ReadState confirms it (issue #15). The stdin commands 'r' / 'c' were
    /// accepted silently while the system stayed in Config, so they are no longer used for this.
    /// </summary>
    public string SetMode(string mode, TimeSpan timeout)
    {
        string? netId;
        lock (_processLock)
        {
            if (_process is null || _process.HasExited)
                throw new InvalidOperationException("UMR process is not running.");
            netId = _effectiveNetId;
        }

        string result = SwitchVerified(netId, mode, id => new AdsSystemControl(id), timeout);
        if (!McpErrors.IsOk(result))
            return result;

        lock (_processLock) State = mode.Trim().Equals("run", StringComparison.OrdinalIgnoreCase) ? "Run" : "Config";
        StateChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// The decision behind <see cref="SetMode"/>, testable without a process: refuses when the
    /// instance's NetId is unknown (nothing could confirm the switch), otherwise switches with
    /// <see cref="SystemModeSwitch"/>, whose result is decided by ReadState only.
    /// </summary>
    internal static string SwitchVerified(string? netId, string mode, Func<string, ISystemControl> openControl,
        TimeSpan timeout, Func<TimeSpan>? elapsed = null, Action<TimeSpan>? sleep = null)
    {
        if (!SystemModeSwitch.TryParseMode(mode, out var m))
            return $"[Error] mode must be 'config' or 'run' (got '{mode}').";
        if (netId is null)
            return "[Error] The UMR instance's AMS NetId is unknown (no literal -i NetId in its Start.bat, no AmsNetId " +
                   "in its 3.1\\TcRegistry.xml), so a mode switch could not be confirmed. Nothing was sent. " +
                   "Use twincat_set_system_mode with the instance's NetId.";
        using var control = openControl(netId);
        return SystemModeSwitch.Switch(control, netId, m, timeout, elapsed, sleep);
    }

    /// <summary>
    /// Stops the UMR process. Unlike the previous version, a failed kill/wait
    /// is reported honestly (issue #154, A-14) instead of always returning
    /// "UMR stopped." — a caller (GUI or MCP tool) that gets that string back
    /// must be able to trust the process is actually gone.
    /// </summary>
    public string Stop()
    {
        Process process;
        lock (_processLock)
        {
            if (_process is null || _process.HasExited)
                return "UMR process is not running.";
            process = _process;
        }

        bool exitedGracefully = false;
        string? killError = null;
        try
        {
            process.StandardInput.WriteLine("x");
            process.StandardInput.Flush();
            exitedGracefully = process.WaitForExit(5000);
            if (!exitedGracefully)
            {
                process.Kill();
                exitedGracefully = process.WaitForExit(2000); // ensure OS reclaims the process before Dispose
            }
        }
        catch (Exception ex)
        {
            killError = ex.Message;
        }

        try { process.CancelOutputRead(); } catch { }

        bool actuallyExited = exitedGracefully || SafeHasExited(process);
        process.Dispose();
        lock (_processLock)
        {
            if (ReferenceEquals(_process, process)) _process = null;
            State = "Stopped";
        }
        StateChanged?.Invoke();

        if (actuallyExited)
            return "UMR stopped.";

        // Honest failure: the process could still be alive despite Kill().
        return killError is null
            ? "[Error] UMR stop: process did not confirm exit after Kill()."
            : $"[Error] UMR stop: process did not confirm exit — {killError}";
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; } // process object invalid/gone == exited
    }

    public void Dispose()
    {
        Process? process;
        lock (_processLock) process = _process;
        if (process is null) return;

        try { process.CancelOutputRead(); } catch { }
        if (!SafeHasExited(process))
        {
            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            catch { /* best-effort on teardown */ }
        }
        try { process.Dispose(); } catch { }
        lock (_processLock)
        {
            if (ReferenceEquals(_process, process)) _process = null;
        }
    }
}
