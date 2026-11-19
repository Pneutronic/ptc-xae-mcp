using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// The Profinet device (RT) on an EL6631-0010 and the sync task of an I/O device:
/// twincat_profinet_device_add (I/O device SubType 118 bound to the EtherCAT box, with its GSDML box),
/// twincat_profinet_list_modules (the module catalog of the device's DAP and the modules placed per slot),
/// twincat_profinet_module_add (one module at a slot) and twincat_io_device_sync_task (read or set the sync task).
/// Refusals come before the logout and before anything changes; every write is read back.
/// The sync task is not taken by ConsumeXml, so it is written by export → patch → delete → import (links reconnected).
/// </summary>
internal sealed class ProfinetCoordinator(ITwinCatSession session, string? profinetDirectory = null)
{
    private const string Tiid = "TIID";
    // The XAE names the Profinet box itself ("boxxx…", its station name) and ignores a rename through the
    // Automation Interface (Name setter and ConsumeXml, live on 3.1.4026.22), so the box keeps that name.
    private const string BoxNameHint = "Box 1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private string? ProfinetDirectory =>
        profinetDirectory ?? (TwinCatPaths.GetInstallDir() is string dir ? Path.Combine(dir, "Config", "Io", "Profinet") : null);

    // ---- twincat_profinet_device_add --------------------------------------------------------

    public string AddDevice(string name, string boxPath, int? dap, string? syncTask)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "[Error] name is required: the device name under I/O > Devices, e.g. 'PN Device'. Nothing was changed.";
        name = name.Trim();
        if (name.Contains('^'))
            return $"[Error] name '{name}' must not contain '^'. Nothing was changed.";
        if (string.IsNullOrWhiteSpace(boxPath))
            return "[Error] boxPath is required: the tree path of the EL6631-0010 in the EtherCAT network, from twincat_get_fieldbus_topology. Nothing was changed.";
        boxPath = boxPath.Trim();

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        // ---- the EL6631-0010 ----
        int boxItemId;
        try
        {
            var (itemId, vendor, product, revision) = ProfinetXml.ReadEtherCatBox(session.ProduceTreeItemXml(boxPath));
            if (vendor != ProfinetXml.Beckhoff || product != ProfinetXml.El6631_0010ProductCode)
                return $"[Error] '{boxPath}' is not an EL6631-0010 (it reports {revision ?? $"vendor {Show(vendor)}, product code {Hex(product)}"}). " +
                       "Pass the EtherCAT box of the EL6631-0010 Profinet device terminal. Nothing was changed.";
            if (itemId is null)
                return $"[Error] The ItemId of '{boxPath}' is not readable, so the device cannot be bound to it. Nothing was changed.";
            boxItemId = itemId.Value;
        }
        catch (Exception ex) { return McpErrors.EnsureError($"{ComCall.Describe(ex).Replace("[Error] ", "")} Nothing was changed."); }

        // ---- name and binding are free ----
        List<(string Name, string Path, string Xml)> devices;
        try { devices = ReadDevices(); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        var sameName = devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sameName != default)
            return $"[Error] An I/O device '{sameName.Name}' already exists ({sameName.Path}). Choose another name. Nothing was changed.";
        var bound = devices.FirstOrDefault(d =>
        {
            var (_, id, boundName) = ProfinetXml.ReadDevice(d.Xml);
            return string.Equals(boundName, boxPath, StringComparison.OrdinalIgnoreCase) || (boundName is null && id == boxItemId && id != 0);
        });
        if (bound != default)
            return $"[Error] The EL6631-0010 '{boxPath}' is already used by the I/O device '{bound.Path}'. Nothing was changed.";

        // ---- GSDML and DAP ----
        GsdmlCatalog catalog;
        try { catalog = LoadNewestGsdml(); }
        catch (Exception ex) { return $"[Error] {ex.Message} Nothing was changed."; }
        var chosenDap = catalog.Dap(dap);
        if (chosenDap is null)
            return $"[Error] dap {dap} does not exist in {catalog.FileName} ({DescribeDaps(catalog)}). Nothing was changed.";

        // ---- sync task (checked now, written after the device exists) ----
        TaskCoordinator.TaskEntry? syncTarget = null;
        if (!string.IsNullOrWhiteSpace(syncTask))
        {
            error = ResolveSyncTask(syncTask.Trim(), out syncTarget, out _);
            if (error is not null) return error;
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        var created = new List<string>();
        string devicePath;
        try { devicePath = session.CreateTreeChild(Tiid, name, ProfinetXml.DeviceSubType, null); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        created.Add($"I/O device '{devicePath}'");

        // ---- bind to the EL6631-0010, read back ----
        string? boundTo;
        try
        {
            session.ConsumeTreeItemXml(devicePath, ProfinetXml.BindXml(boxItemId));
            boundTo = ProfinetXml.ReadDevice(session.ProduceTreeItemXml(devicePath)).EtherCatDeviceName;
        }
        catch (Exception ex) { return Partial(created, $"binding '{devicePath}' to '{boxPath}'", ComCall.Describe(ex)); }
        if (!string.Equals(boundTo, boxPath, StringComparison.OrdinalIgnoreCase))
            return Partial(created, $"binding '{devicePath}' to '{boxPath}'", $"the read-back shows '{boundTo ?? "<none>"}'");

        // ---- the GSDML box with its DAP ----
        string profinetBox;
        try
        {
            string vInfo = $"{catalog.FilePath}#0x{chosenDap.IdentNumber.ToString("X", CultureInfo.InvariantCulture)}";
            profinetBox = session.CreateTreeChild(devicePath, BoxNameHint, ProfinetXml.BoxSubType, vInfo);
            created.Add($"Profinet box '{profinetBox}'");
        }
        catch (Exception ex) { return Partial(created, $"creating the Profinet box under '{devicePath}'", ComCall.Describe(ex)); }

        string syncLine = "Sync task: default (via mapping). Set one with twincat_io_device_sync_task.";
        if (syncTarget is not null)
        {
            string? syncError = WriteSyncTask(devicePath, syncTarget.Settings.AmsPort, out var state);
            if (syncError is not null) return Partial(created, $"setting the sync task of '{devicePath}'", syncError);
            syncLine = $"Sync task: '{syncTarget.Settings.Path}' (AMS port {Show(state!.TaskPort)}, read back).";
        }

        string save;
        try { session.SaveAll(); save = "saved"; }
        catch (Exception ex) { save = $"save failed: {ComCall.Describe(ex)} — call twincat_save_project"; }

        return string.Join(Environment.NewLine,
            $"Created Profinet device (RT) '{devicePath}' on the EL6631-0010 '{boxPath}' (read-back).",
            $"Profinet box '{profinetBox}' from {catalog.FileName}, DAP {chosenDap.Number} '{chosenDap.Name}'.",
            syncLine,
            "Add modules with twincat_profinet_module_add; the catalog shows twincat_profinet_list_modules.",
            $"Project {save}. Takes effect with the next Activate Configuration (twincat_activate_config).");
    }

    // ---- twincat_profinet_list_modules --------------------------------------------------------

    public string ListModules(string device, string? filter)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        ProfinetDevice pn;
        try { pn = ReadProfinetDevice(device); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        var offered = pn.Catalog.ModulesFor(pn.Dap)
            .Where(m => string.IsNullOrWhiteSpace(filter)
                        || m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || m.Names.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        || (m.Category ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int placedCount = pn.Modules.Count(m => m.Slot != 0);
        string summary = $"Profinet device '{pn.DevicePath}': DAP {pn.Dap.Number} '{pn.Dap.Name}' ({pn.Catalog.FileName}) in slot 0, " +
                         $"{placedCount} module(s) placed, {offered.Count} module type(s) offered" +
                         (string.IsNullOrWhiteSpace(filter) ? "." : $" matching '{filter}'.");
        string json = JsonSerializer.Serialize(new
        {
            device = pn.DevicePath,
            box = pn.BoxPath,
            api = pn.ApiPath,
            gsdml = pn.Catalog.FileName,
            dap = new { number = pn.Dap.Number, name = pn.Dap.Name, maxSlot = pn.Dap.MaxSlot },
            placed = pn.Modules.OrderBy(m => m.Slot ?? int.MaxValue).Select(m => new
            {
                slot = m.Slot,
                name = m.Name,
                module = m.Slot == 0 && m.IdentNumber == pn.Dap.IdentNumber
                    ? pn.Dap.Id
                    : m.IdentNumber is uint id ? pn.Catalog.Modules.FirstOrDefault(c => c.IdentNumber == id)?.Id : null,
                identNumber = m.IdentNumber is uint i ? Hex(i) : null,
                path = m.Path,
            }),
            catalog = offered.Select(m => new
            {
                module = m.Id,
                name = m.Name,
                category = m.Category,
                identNumber = Hex(m.IdentNumber),
                inputBytes = m.InputBytes,
                outputBytes = m.OutputBytes,
            }),
        }, JsonOpts);
        return summary + Environment.NewLine + json;
    }

    // ---- twincat_profinet_module_add ----------------------------------------------------------

    public string AddModule(string device, string module, int slot)
    {
        if (string.IsNullOrWhiteSpace(module))
            return "[Error] module is required: its GSDML ID (e.g. BYTE_I4), name or ident number, from twincat_profinet_list_modules. Nothing was changed.";
        if (slot < 1)
            return $"[Error] slot {slot} is not allowed: slot 0 holds the DAP, modules start at slot 1. Nothing was changed.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        ProfinetDevice pn;
        try { pn = ReadProfinetDevice(device); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        var item = pn.Catalog.Find(module);
        if (item is null)
            return $"[Error] {pn.Catalog.FileName} has no module '{module.Trim()}'. List the offered ones with twincat_profinet_list_modules. Nothing was changed.";
        if (!pn.Dap.UseableModuleIds.Contains(item.Id))
            return $"[Error] Module '{item.Id}' ({item.Name}) is not allowed with DAP {pn.Dap.Number} '{pn.Dap.Name}'. Nothing was changed.";
        if (item.Modular)
            return $"[Error] Module '{item.Id}' ({item.Name}) is a modular module with submodules; this tool places plain modules only. Nothing was changed.";
        if (slot > pn.Dap.MaxSlot)
            return $"[Error] slot {slot} is beyond the last slot {pn.Dap.MaxSlot} of DAP {pn.Dap.Number}. Nothing was changed.";
        var occupied = pn.Modules.FirstOrDefault(m => m.Slot == slot);
        if (occupied is not null)
            return $"[Error] slot {slot} already holds '{occupied.Name}' ({occupied.Path}). Choose a free slot. Nothing was changed.";

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string path;
        try { path = session.CreateTreeChild(pn.ApiPath, string.Empty, item.Index, slot.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        PlacedProfinetModule placed;
        try { placed = ProfinetXml.ReadModule(session.ProduceTreeItemXml(path), path); }
        catch (Exception ex)
        {
            return $"[Error] The new module '{path}' could not be read back: {ComCall.Describe(ex).Replace("[Error] ", "")} " +
                   "The project was not saved; check it in the XAE.";
        }
        if (placed.Slot != slot || placed.IdentNumber != item.IdentNumber)
            return $"[Error] The XAE placed '{placed.Path}' in slot {Show(placed.Slot)} with ident {(placed.IdentNumber is uint u ? Hex(u) : "<unreadable>")}, " +
                   $"not '{item.Id}' ({Hex(item.IdentNumber)}) in slot {slot}. The project was not saved; delete it in the XAE.";

        string save;
        try { session.SaveAll(); save = "saved"; }
        catch (Exception ex) { save = $"save failed: {ComCall.Describe(ex)} — call twincat_save_project"; }

        return string.Join(Environment.NewLine,
            $"Placed module '{item.Id}' ({item.Name}, {item.InputBytes} byte(s) in, {item.OutputBytes} byte(s) out) in slot {slot}: '{placed.Path}' (read-back).",
            $"Project {save}. Takes effect with the next Activate Configuration (twincat_activate_config).");
    }

    // ---- twincat_io_device_sync_task ----------------------------------------------------------

    public string SyncTask(string device, string? task)
    {
        if (string.IsNullOrWhiteSpace(device))
            return "[Error] device is required: the tree path of the I/O device, e.g. 'TIID^PN Device'. Nothing was changed.";
        device = device.Trim();
        if (!IsDirectDevicePath(device))
            return $"[Error] '{device}' is not an I/O device path: pass 'TIID^<device>' from twincat_get_fieldbus_topology. Nothing was changed.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        SyncTaskXml.State state;
        List<TaskCoordinator.TaskEntry> tasks;
        var warnings = new List<string>();
        try
        {
            _ = session.ProduceTreeItemXml(device); // exists?
            state = SyncTaskXml.Read(ReadExport(device));
            tasks = new TaskCoordinator(session).ReadAll(warnings);
        }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        if (!state.Supported)
            return $"[Error] '{device}' has no sync task setting the Automation Interface can reach (only I/O devices with an Ethernet " +
                   "protocol section, such as the Profinet device on an EL6631-0010, are supported; the EtherCAT master follows the task " +
                   "its process image is linked to). Nothing was changed.";

        if (task is null)
            return Describe(device, state, tasks, warnings);

        int? port;
        TaskCoordinator.TaskEntry? target = null;
        if (string.Equals(task.Trim(), "default", StringComparison.OrdinalIgnoreCase))
        {
            port = null;
        }
        else
        {
            error = ResolveSyncTask(task.Trim(), out target, out _);
            if (error is not null) return error;
            port = target!.Settings.AmsPort;
        }
        if (state.Special == (port is not null) && state.TaskPort == port)
            return $"No change: {Describe(device, state, tasks, warnings)}";

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string? writeError = WriteSyncTask(device, port, out var after);
        if (writeError is not null) return writeError;

        string save;
        try { session.SaveAll(); save = "saved"; }
        catch (Exception ex) { save = $"save failed: {ComCall.Describe(ex)} — call twincat_save_project"; }

        return string.Join(Environment.NewLine,
            $"Changed the sync task (read-back): {Describe(device, after!, tasks, [])}",
            $"Project {save}. Takes effect with the next Activate Configuration (twincat_activate_config).");
    }

    // ---- shared steps -------------------------------------------------------------------------

    private sealed record ProfinetDevice(
        string DevicePath, string BoxPath, string ApiPath, GsdmlCatalog Catalog, GsdmlDap Dap, IReadOnlyList<PlacedProfinetModule> Modules);

    private ProfinetDevice ReadProfinetDevice(string device)
    {
        if (string.IsNullOrWhiteSpace(device))
            throw new InvalidOperationException("device is required: the tree path of the Profinet device, e.g. 'TIID^PN Device'.");
        device = device.Trim();
        if (!IsDirectDevicePath(device))
            throw new InvalidOperationException($"'{device}' is not an I/O device path: pass 'TIID^<device>'.");

        var (subType, _, _) = ProfinetXml.ReadDevice(session.ProduceTreeItemXml(device));
        if (subType != ProfinetXml.DeviceSubType)
            throw new InvalidOperationException(
                $"'{device}' is not a Profinet device (RT) on an EL6631-0010 (ItemSubType {Show(subType)}, expected {ProfinetXml.DeviceSubType}).");

        var box = session.ListAllTreeChildren(device).FirstOrDefault(c => c.ItemType == ProfinetXml.BoxItemType)
            ?? throw new InvalidOperationException($"'{device}' has no Profinet box. Create the device with twincat_profinet_device_add.");
        string boxPath = $"{device}^{box.Name}";
        string? gsdmlName = ProfinetXml.ReadGsdmlName(session.ProduceTreeItemXml(boxPath));
        string? dir = ProfinetDirectory;
        if (gsdmlName is null || dir is null || !File.Exists(Path.Combine(dir, gsdmlName)))
            throw new InvalidOperationException(
                $"The GSDML file of '{boxPath}' ({gsdmlName ?? "not named in the box"}) was not found under {dir ?? "<TwinCAT>\\Config\\Io\\Profinet"}.");
        var catalog = GsdmlCatalog.Load(Path.Combine(dir, gsdmlName));

        uint? dapIdent = ProfinetXml.ReadDapIdent(ReadExport(device));
        var dap = (dapIdent is uint ident ? catalog.DapByIdent(ident) : null)
            ?? throw new InvalidOperationException($"The DAP of '{boxPath}' (ident {Show(dapIdent)}) is not in {catalog.FileName}.");

        var api = session.ListAllTreeChildren(boxPath).FirstOrDefault(c => c.ItemType == ProfinetXml.ApiItemType)
            ?? throw new InvalidOperationException($"'{boxPath}' has no API node.");
        string apiPath = $"{boxPath}^{api.Name}";
        var modules = session.ListAllTreeChildren(apiPath)
            .Where(c => c.ItemType == ProfinetXml.ModuleItemType)
            .Select(c => ProfinetXml.ReadModule(session.ProduceTreeItemXml($"{apiPath}^{c.Name}"), $"{apiPath}^{c.Name}"))
            .ToList();
        return new ProfinetDevice(device, boxPath, apiPath, catalog, dap, modules);
    }

    private GsdmlCatalog LoadNewestGsdml()
    {
        string? dir = ProfinetDirectory;
        if (dir is null || !Directory.Exists(dir))
            throw new InvalidOperationException($"The TwinCAT Profinet folder ({dir ?? "<TwinCAT>\\Config\\Io\\Profinet"}) does not exist.");
        var files = GsdmlCatalog.FindEl6631Files(dir);
        if (files.Count == 0)
            throw new InvalidOperationException($"No GSDML file of the EL6631-0010 (GSDML-*-beckhoff-EL6631-*.xml) under {dir}.");
        var catalog = GsdmlCatalog.Load(files[^1]);
        if (catalog.Daps.Count == 0)
            throw new InvalidOperationException($"{catalog.FileName} has no device access point.");
        return catalog;
    }

    private List<(string Name, string Path, string Xml)> ReadDevices() =>
        session.ListAllTreeChildren(Tiid)
            .Where(c => c.ItemType == ProfinetXml.DeviceItemType)
            .Select(c => (c.Name, $"{Tiid}^{c.Name}", session.ProduceTreeItemXml($"{Tiid}^{c.Name}")))
            .ToList();

    // The task must exist, carry a process image and have a readable AMS port.
    private string? ResolveSyncTask(string task, out TaskCoordinator.TaskEntry? target, out List<TaskCoordinator.TaskEntry> tasks)
    {
        target = null;
        tasks = [];
        try { tasks = new TaskCoordinator(session).ReadAll([]); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        var found = TaskCoordinator.Resolve(tasks, task, out string? error);
        if (found is null) return error;
        if (!found.HasImage)
            return $"[Error] Task '{found.Settings.Path}' has no process image, so it cannot be a sync task. Create one with " +
                   "twincat_io_task_create (e.g. amsPort 301). Nothing was changed.";
        if (found.Settings.AmsPort is null)
            return $"[Error] The AMS port of task '{found.Settings.Path}' is not readable. Nothing was changed.";
        target = found;
        return null;
    }

    // Export → patch → delete → import at the same position (links reconnected) → export again to read back.
    // A failed import puts the original export back.
    private string? WriteSyncTask(string device, int? port, out SyncTaskXml.State? after)
    {
        after = null;
        string name = device[(device.IndexOf('^') + 1)..];
        string original;
        string folder;
        try { original = ExportDevice(device, out folder); }
        catch (Exception ex) { return McpErrors.EnsureError($"Exporting '{device}' failed: {ComCall.Describe(ex).Replace("[Error] ", "")} Nothing was changed."); }

        string patched;
        try { patched = SyncTaskXml.Patch(original, port); }
        catch (Exception ex) { return $"[Error] {ex.Message} Nothing was changed."; }
        string originalFile = Path.Combine(folder, "original.xti");
        string patchedFile = Path.Combine(folder, "patched.xti");
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(originalFile, original, utf8);
        File.WriteAllText(patchedFile, patched, utf8);

        string? before;
        try
        {
            var siblings = session.ListAllTreeChildren(Tiid).Select(c => c.Name).ToList();
            int i = siblings.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            before = i >= 0 && i + 1 < siblings.Count ? siblings[i + 1] : null;
            session.DeleteTreeChild(Tiid, name);
        }
        catch (Exception ex) { return McpErrors.EnsureError($"Removing '{device}' for the re-import failed: {ComCall.Describe(ex).Replace("[Error] ", "")} The project was not saved; check it in the XAE."); }

        try { session.ImportTreeChild(Tiid, patchedFile, before, true, name); }
        catch (Exception ex)
        {
            string restore;
            try { session.ImportTreeChild(Tiid, originalFile, before, true, name); restore = "The original device was imported again."; }
            catch (Exception ex2) { restore = $"Importing the original back failed too ({ComCall.Describe(ex2).Replace("[Error] ", "")}): import '{originalFile}' under I/O > Devices by hand."; }
            return $"[Error] Importing '{device}' with the new sync task failed: {ComCall.Describe(ex).Replace("[Error] ", "")} {restore} The project was not saved.";
        }

        try { after = SyncTaskXml.Read(ReadExport(device)); }
        catch (Exception ex) { return McpErrors.EnsureError($"The sync task of '{device}' could not be read back: {ComCall.Describe(ex).Replace("[Error] ", "")} The project was not saved."); }
        if (after.Special != (port is not null) || after.TaskPort != port)
            return $"[Error] The sync task of '{device}' did not take effect: the read-back shows {DescribePort(after)}, not {(port is int p ? $"AMS port {p}" : "the default")}. " +
                   $"The project was not saved; the original export is '{originalFile}'.";

        TryDelete(folder);
        return null;
    }

    // Exports the device and removes the temp folder again: for reads.
    private string ReadExport(string device)
    {
        string text = ExportDevice(device, out string folder);
        TryDelete(folder);
        return text;
    }

    // Exports the device (a direct child of TIID) into a fresh temp folder and returns the .xti text.
    private string ExportDevice(string device, out string folder)
    {
        folder = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp", "io-device-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "export.xti");
        session.ExportTreeChild(Tiid, device[(device.IndexOf('^') + 1)..], file);
        if (!File.Exists(file))
            throw new InvalidOperationException($"ExportChild of '{device}' wrote no file.");
        return File.ReadAllText(file);
    }

    private static string Describe(string device, SyncTaskXml.State state, List<TaskCoordinator.TaskEntry> tasks, List<string> warnings)
    {
        var lines = new List<string>();
        if (!state.Special)
        {
            lines.Add($"Sync task of '{device}': default (via mapping) — the device runs with the task its process image is linked to.");
        }
        else
        {
            var task = tasks.FirstOrDefault(t => t.Settings.AmsPort == state.TaskPort);
            lines.Add(task is null
                ? $"Sync task of '{device}': AMS port {Show(state.TaskPort)}."
                : $"Sync task of '{device}': '{task.Settings.Path}' (AMS port {Show(state.TaskPort)}).");
            if (task is null)
                lines.Add($"[Warn] No real-time task has AMS port {Show(state.TaskPort)}: activation fails with 'target port not found'. " +
                          "Create one with twincat_io_task_create or set another task.");
            else if (!task.HasImage)
                lines.Add($"[Warn] Task '{task.Settings.Path}' has no process image.");
        }
        lines.AddRange(warnings);
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribePort(SyncTaskXml.State state) =>
        state.Special ? $"AMS port {Show(state.TaskPort)}" : "the default (via mapping)";

    private static bool IsDirectDevicePath(string path) =>
        path.StartsWith(Tiid + "^", StringComparison.OrdinalIgnoreCase) && path.IndexOf('^', Tiid.Length + 1) < 0 && path.Length > Tiid.Length + 1;

    private static string DescribeDaps(GsdmlCatalog catalog) =>
        string.Join("; ", catalog.Daps.Select(d => $"{d.Number} = {d.Name}"));

    // A later step failed: name what exists now. Nothing is saved.
    private static string Partial(List<string> created, string step, string cause) =>
        $"[Error] {step} failed: {cause.Replace("[Error] ", "")} Already created: {string.Join("; ", created)}. " +
        "The project was not saved. Remove the device in the XAE (I/O > Devices, right-click > Delete) or finish the step there.";

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Show(int? value) => value is int v ? v.ToString(CultureInfo.InvariantCulture) : "<unreadable>";
    private static string Show(uint? value) => value is uint v ? v.ToString(CultureInfo.InvariantCulture) : "<unreadable>";
    private static string Hex(uint? value) => value is uint v ? "0x" + v.ToString("X", CultureInfo.InvariantCulture) : "<unreadable>";
}
