namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed class LibraryCoordinator(ITwinCatSession session)
{
    /// <summary>
    /// Finds a byte-identical copy of an installed library file in the library repository
    /// (B-14 read-back for an overwrite, which leaves the installed list unchanged). Test seam.
    /// </summary>
    internal Func<string, string?> FindRepositoryCopy { get; init; } = LibraryRepository.FindCopy;

    public (string Summary, IReadOnlyList<LibraryReference> References) ListReferences()
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<LibraryReference>());

        IReadOnlyList<LibraryReference> refs;
        try { refs = session.ListLibraryReferences(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryReference>()); }
        if (refs.Count == 0)
            return ("No library references found.", refs);

        int mismatches = refs.Count(r => r.VersionMatchesFile == false);
        string note = mismatches > 0
            ? $" ({mismatches} XAE/.plcproj version mismatch{(mismatches == 1 ? "" : "es")})"
            : string.Empty;
        return ($"Found {refs.Count} library reference{(refs.Count == 1 ? "" : "s")}{note}.", refs);
    }

    /// <summary>
    /// <see cref="ListReferences"/> plus, per reference, the concrete library it resolves to (sbom). The manager's own
    /// resolution of each placeholder is used when readable; otherwise the result is the reference's fixed version or
    /// the newest installed one, which the warnings say.
    /// </summary>
    public (string Summary, IReadOnlyList<ResolvedLibraryReference> Items, IReadOnlyList<string> Warnings) ListReferencesResolved()
    {
        var (summary, refs) = ListReferences();
        if (McpErrors.IsError(summary) || refs.Count == 0)
            return (summary, Array.Empty<ResolvedLibraryReference>(), Array.Empty<string>());

        var warnings = new List<string>();
        IReadOnlyList<PlaceholderResolution> placeholders = Array.Empty<PlaceholderResolution>();
        IReadOnlyList<InstalledLibrary> installed = Array.Empty<InstalledLibrary>();
        try
        {
            var context = session.ReadPlcLibraryContexts(null).FirstOrDefault();
            if (context is not null)
            {
                placeholders = context.Placeholders;
                installed = context.Installed;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            warnings.Add($"The library manager could not be read ({ex.Message}); resolutions are computed from the references and the installed libraries.");
            try { installed = session.ListInstalledLibraries(); }
            catch (InvalidOperationException installedEx) { warnings.Add($"Installed libraries not readable: {installedEx.Message}"); }
        }
        if (placeholders.Count == 0 && refs.Any(r => r.IsAnyVersion))
            warnings.Add("No effective placeholder resolution available from the library manager: 'newest-installed' is a computed approximation of what the project compiles against.");

        var items = refs
            .Select(r => new ResolvedLibraryReference(r, LibraryResolver.Resolve(r, LibraryResolver.FindResolution(placeholders, r), installed)))
            .ToList();
        return (summary, items, warnings);
    }

    public (string Summary, IReadOnlyList<InstalledLibrary> Libraries) ListInstalled()
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<InstalledLibrary>());

        IReadOnlyList<InstalledLibrary> libs;
        try { libs = session.ListInstalledLibraries(); }
        catch (InvalidOperationException ex)
        {
            return ($"[Error] {ex.Message}", Array.Empty<InstalledLibrary>());
        }

        return libs.Count == 0
            ? ("No installed libraries reported by XAE.", libs)
            : ($"Found {libs.Count} installed librar{(libs.Count == 1 ? "y" : "ies")}.", libs);
    }

    // Mirrors XAE's own "missing in repository" flag on a reference (Beckhoff Infosys
    // tc3_plc_intro/41891384434359666059 "Using libraries"). The join key is the
    // resolved library Name (case-insensitive); Version "*" satisfies any installed
    // version with the same name.
    public (string Summary, IReadOnlyList<LibraryAvailability> Items) CheckAvailability()
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<LibraryAvailability>());

        IReadOnlyList<LibraryReference> refs;
        try { refs = session.ListLibraryReferences(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryAvailability>()); }
        if (refs.Count == 0)
            return ("No library references found.", Array.Empty<LibraryAvailability>());

        IReadOnlyList<InstalledLibrary> installed;
        try { installed = session.ListInstalledLibraries(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryAvailability>()); }

        var items = Classify(refs, installed);
        int missing = items.Count(i => i.Status == AvailabilityStatus.Missing);
        int mismatch = items.Count(i => i.Status == AvailabilityStatus.VersionMismatch);
        string summary;
        if (missing == 0 && mismatch == 0)
        {
            summary = $"All {items.Count} library reference{(items.Count == 1 ? "" : "s")} installed.";
        }
        else
        {
            var parts = new List<string>();
            if (missing > 0) parts.Add($"{missing} missing");
            if (mismatch > 0) parts.Add($"{mismatch} version mismatch{(mismatch == 1 ? "" : "es")}");
            summary = $"{items.Count} library reference{(items.Count == 1 ? "" : "s")} checked: {string.Join(", ", parts)}.";
        }
        return (summary, items);
    }

    /// <summary>
    /// Installed / Missing / VersionMismatch per reference; shared by twincat_check_libraries_installed and the
    /// library check before a build (<see cref="BuildCoordinator"/>).
    /// </summary>
    internal static IReadOnlyList<LibraryAvailability> Classify(
        IReadOnlyList<LibraryReference> refs, IReadOnlyList<InstalledLibrary> installed)
    {
        var byName = installed
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var items = new List<LibraryAvailability>(refs.Count);
        foreach (var r in refs)
        {
            if (!byName.TryGetValue(r.Name, out var candidates) || candidates.Count == 0)
            {
                items.Add(new LibraryAvailability(r, AvailabilityStatus.Missing, null));
                continue;
            }

            if (r.IsAnyVersion)
            {
                items.Add(new LibraryAvailability(r, AvailabilityStatus.Installed, candidates[0]));
                continue;
            }

            var exact = candidates.FirstOrDefault(c =>
                string.Equals(c.Version, r.Version, StringComparison.OrdinalIgnoreCase));
            items.Add(exact is not null
                ? new LibraryAvailability(r, AvailabilityStatus.Installed, exact)
                : new LibraryAvailability(r, AvailabilityStatus.VersionMismatch, candidates[0]));
        }
        return items;
    }

    public (string Summary, IReadOnlyList<LibraryReference> References) AddReference(
        string name, string version, string? company, bool asPlaceholder, bool force)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<LibraryReference>());

        // Install-guard: placeholders don't need to be installed; force bypasses the check.
        if (!asPlaceholder && !force)
        {
            var installed = session.ListInstalledLibraries();
            bool nameExists = installed.Any(i =>
                string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!nameExists)
                return ($"[Error] Library '{name}' is not installed. Use force=true to add an unresolvable reference.",
                    Array.Empty<LibraryReference>());

            // When a specific version (not "*") is requested, check it is present.
            if (version != "*")
            {
                bool versionExists = installed.Any(i =>
                    string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.Version, version, StringComparison.OrdinalIgnoreCase));
                if (!versionExists)
                    return ($"[Error] Library '{name}' version '{version}' is not installed. " +
                            "Use version='*' to pick any installed version, or force=true to add anyway.",
                        Array.Empty<LibraryReference>());
            }
        }

        // Idempotency: already referenced → no-op with informative message.
        IReadOnlyList<LibraryReference> existing;
        try { existing = session.ListLibraryReferences(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryReference>()); }
        if (existing.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return ($"Library '{name}' is already referenced.", existing);

        IReadOnlyList<LibraryReference> refs;
        try { refs = session.AddLibraryReference(name, version, company, asPlaceholder); }
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.Describe(ex)), Array.Empty<LibraryReference>()); }

        // B-12 (issue #152): the refreshed list (re-read after the save) must show the reference.
        var added = refs.FirstOrDefault(r => Refers(r, name));
        if (added is null)
            return ($"[Error] Add of library '{name}' did not land: the refreshed reference list " +
                    $"({refs.Count} reference{(refs.Count == 1 ? "" : "s")}) does not contain it.", refs);
        if (version != "*" && !added.IsAnyVersion && added.Version.Length > 0 &&
            !string.Equals(added.Version, version, StringComparison.OrdinalIgnoreCase))
            return ($"[Error] Library '{name}' was added, but the refreshed list shows version '{added.Version}', " +
                    $"not the requested '{version}'. Fix it with twincat_set_library_version.", refs);

        string versionLabel = version == "*" ? "(latest)" : version;
        return ($"Added '{name}' {versionLabel} (read-back: listed as {Describe(added)}).", refs);
    }

    // A reference addressed by the caller's name: its library name or its placeholder name.
    private static bool Refers(LibraryReference r, string name) =>
        string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(r.Placeholder, name, StringComparison.OrdinalIgnoreCase);

    private static string Describe(LibraryReference r) =>
        string.IsNullOrWhiteSpace(r.EffectiveResolution) ? $"{r.Name}, {r.Version}" : r.EffectiveResolution;

    public (string Summary, IReadOnlyList<LibraryReference> References) RemoveReference(string name)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<LibraryReference>());

        IReadOnlyList<LibraryReference> existing;
        try { existing = session.ListLibraryReferences(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryReference>()); }
        var target = existing.FirstOrDefault(r => Refers(r, name));
        if (target is null)
            return ($"Library '{name}' is not referenced.", existing);

        // B-12 (issue #152): ITcPlcLibraryManager.RemoveReference takes the PLACEHOLDER name; a
        // reference whose placeholder differs from the library name was not removed by name.
        string key = string.IsNullOrWhiteSpace(target.Placeholder) ? target.Name : target.Placeholder;

        IReadOnlyList<LibraryReference> refs;
        try { refs = session.RemoveLibraryReference(key); }
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.Describe(ex)), Array.Empty<LibraryReference>()); }

        if (refs.Any(r => Refers(r, target.Name) || Refers(r, key)))
            return ($"[Error] Remove of library '{name}' (placeholder '{key}') did not land: it is still in the " +
                    "refreshed reference list.", refs);
        return ($"Removed '{name}' (placeholder '{key}'; read-back: no longer referenced).", refs);
    }

    public (string Summary, IReadOnlyList<InstalledLibrary> Libraries) Install(string libraryPath, bool overwrite)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<InstalledLibrary>());
        if (!File.Exists(libraryPath))
            return ($"[Error] Library file not found: {libraryPath}", Array.Empty<InstalledLibrary>());

        IReadOnlyList<InstalledLibrary> before;
        try { before = session.ListInstalledLibraries().ToList(); } // a snapshot, not a live view
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.Describe(ex)), Array.Empty<InstalledLibrary>()); }

        IReadOnlyList<InstalledLibrary> libs;
        try { libs = session.InstallLibrary(libraryPath, overwrite); }
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.Describe(ex)), Array.Empty<InstalledLibrary>()); }

        string? evidence = InstallEvidence(libraryPath, before, libs, out string? failure);
        if (evidence is null)
            return ($"[Error] Install of '{Path.GetFileName(libraryPath)}' did not land: {failure}", libs);
        return ($"Installed '{Path.GetFileName(libraryPath)}' into the XAE library repository ({evidence}).", libs);
    }

    // B-14 (issue #152): proof that an install landed — a title/version that is new in the refreshed
    // installed list, or (an overwrite of an existing title/version leaves that list unchanged) a
    // byte-identical copy of the file in the repository. Null with a reason when neither holds.
    private string? InstallEvidence(string libraryFile, IReadOnlyList<InstalledLibrary> before,
        IReadOnlyList<InstalledLibrary> after, out string? failure)
    {
        failure = null;
        var known = new HashSet<string>(before.Select(Key), StringComparer.OrdinalIgnoreCase);
        var added = after.Where(l => !known.Contains(Key(l))).ToList();
        if (added.Count > 0)
            return "read-back: new in the installed list: " +
                   string.Join(", ", added.Select(l => $"{l.Name} {l.Version} ({l.Publisher})"));

        string? copy = FindRepositoryCopy(libraryFile);
        if (copy is not null)
            return $"read-back: the installed list is unchanged (same title/version already installed), " +
                   $"and the repository holds a byte-identical copy at {copy}";

        failure = "the refreshed installed-library list shows no new title/version and no byte-identical copy of " +
                  "the file was found in the library repository. If that title/version was already installed, " +
                  "retry with overwrite=true.";
        return null;

        static string Key(InstalledLibrary l) => $"{l.Name}|{l.Version}|{l.Publisher}";
    }

    public string SaveAsLibrary(string outputPath, bool install)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(outputPath))
            return "[Error] outputPath is required.";

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            return $"[Error] Target directory does not exist: {dir}";

        IReadOnlyList<InstalledLibrary>? before = null;
        if (install)
        {
            try { before = session.ListInstalledLibraries().ToList(); } // a snapshot, not a live view
            catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        }

        // B-14 (issue #152): the file must exist afterwards and be written by THIS call. File
        // timestamps have ~2 s resolution on some file systems; allow that much slack.
        DateTime started = DateTime.UtcNow.AddSeconds(-2);
        string message;
        try { message = session.SaveAsLibrary(outputPath, install); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        if (!File.Exists(outputPath))
            return $"[Error] Save as library did not land: '{outputPath}' does not exist after SaveAsLibrary returned.";
        DateTime written = File.GetLastWriteTimeUtc(outputPath);
        if (written < started)
            return $"[Error] Save as library did not land: '{outputPath}' was last written {written:yyyy-MM-dd HH:mm:ss}Z, " +
                   "before this call — it is an old file, SaveAsLibrary did not write it.";
        string proof = $"read-back: {new FileInfo(outputPath).Length} bytes written {written:HH:mm:ss}Z";

        if (install)
        {
            IReadOnlyList<InstalledLibrary> after;
            try { after = session.ListInstalledLibraries(); }
            catch (Exception ex)
            {
                return McpErrors.EnsureError($"The library file was written ({proof}), but the installed list could not be " +
                                             $"re-read to confirm the install: {ComCall.Describe(ex)}");
            }
            string? installed = InstallEvidence(outputPath, before!, after, out string? failure);
            if (installed is null)
                return $"[Error] The library file was written ({proof}), but the install did not land: {failure}";
            proof += "; " + installed;
        }
        return $"{message} ({proof}).";
    }

    public (string Summary, IReadOnlyList<LibraryParameter> Parameters) GetParameters(string library)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<LibraryParameter>());

        IReadOnlyList<LibraryReference> existing;
        try { existing = session.ListLibraryReferences(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryParameter>()); }
        var match = existing.FirstOrDefault(r =>
            string.Equals(r.Placeholder, library, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Name, library, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return ($"[Error] Library '{library}' is not referenced.", Array.Empty<LibraryParameter>());

        var pars = session.ListLibraryParameters(match.Placeholder);
        if (pars.Count == 0 && !string.Equals(match.Name, match.Placeholder, StringComparison.OrdinalIgnoreCase))
            pars = session.ListLibraryParameters(match.Name);
        return pars.Count == 0
            ? ($"No parameter overrides for '{match.Placeholder}' (library defaults apply).", pars)
            : ($"Found {pars.Count} parameter override{(pars.Count == 1 ? "" : "s")} for '{match.Placeholder}'.", pars);
    }

    public (string Summary, IReadOnlyList<LibraryParameter> Parameters) SetParameter(
        string library, string listName, string key, string value)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<LibraryParameter>());

        if (string.IsNullOrWhiteSpace(listName) || string.IsNullOrWhiteSpace(key))
            return ("[Error] listName and key must not be empty.", Array.Empty<LibraryParameter>());

        IReadOnlyList<LibraryReference> existing;
        try { existing = session.ListLibraryReferences(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryParameter>()); }
        var match = existing.FirstOrDefault(r =>
            string.Equals(r.Placeholder, library, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Name, library, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return ($"[Error] Library '{library}' is not referenced.", Array.Empty<LibraryParameter>());

        IReadOnlyList<LibraryParameter> pars;
        try { pars = session.SetLibraryParameter(match.Placeholder, listName, key, value); }
        catch (InvalidOperationException ex)
        {
            return ($"[Error] {ex.Message}", Array.Empty<LibraryParameter>());
        }
        return ($"Set {listName}/{key} = {value} on '{match.Placeholder}' (persisted to .plcproj).", pars);
    }

    public (string Summary, IReadOnlyList<LibraryReference> References) SetVersion(
        string name, string version, string? company)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<LibraryReference>());

        IReadOnlyList<LibraryReference> existing;
        try { existing = session.ListLibraryReferences(); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryReference>()); }
        var current = existing.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            return ($"[Error] Library '{name}' is not referenced.", Array.Empty<LibraryReference>());

        // Without an explicit company keep the current one: writing the resolution without a
        // publisher ("Tc2_Standard, *") leaves a reference XAE cannot resolve and the PLC no
        // longer compiles (seen live 2026-09-27 on the neutral fixture).
        if (string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(current.Publisher))
            company = current.Publisher;

        IReadOnlyList<LibraryReference> refs;
        try { refs = session.SetLibraryVersion(name, version, company); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", Array.Empty<LibraryReference>()); }
        return ($"Set '{name}' to version {version}.", refs);
    }
}
