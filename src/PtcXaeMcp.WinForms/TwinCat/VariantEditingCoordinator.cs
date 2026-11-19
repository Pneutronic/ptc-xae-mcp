using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Create and delete project variants (read the <c>ProjectVariantConfig</c> XML, edit it, write it back, read it
/// back). The XAE takes the document as a whole, so every call reads right before editing and verifies after
/// writing; a write that does not land is rolled back to the XML that was read.
/// </summary>
internal sealed class VariantEditingCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record EditResult(
        string Action,
        string Variant,
        string? Group,
        string ActiveVariant,
        IReadOnlyList<string> AvailableVariants,
        IReadOnlyList<VariantGroupEntry> Groups,
        IReadOnlyList<string> RemovedFromGroups,
        IReadOnlyList<string> Notes);

    public string Create(string variantName, string? group)
    {
        string? invalid = VariantConfigEditor.ValidateName(variantName);
        if (invalid is not null) return invalid;
        string name = variantName.Trim();

        string? err = SessionGuards.RequireOpenSolution(session);
        if (err is not null) return err;

        string original;
        VariantStatusResult before;
        try
        {
            original = session.ReadProjectVariantConfig();
            before = session.GetVariantStatus();
        }
        catch (Exception ex) { return McpErrors.EnsureError(ex.Message); }

        var (xml, editError) = VariantConfigEditor.AddVariant(original, name, group);
        if (xml is null) return McpErrors.EnsureError(editError ?? "Could not edit ProjectVariantConfig.");

        var notes = new List<string>();
        string? writeError = WriteAndVerify(xml, original, after =>
            after.AvailableVariants.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(group) || after.Groups.Any(g =>
                string.Equals(g.Name, group.Trim().Trim('[', ']').Trim(), StringComparison.OrdinalIgnoreCase) &&
                g.Members.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase)))),
            $"variant '{name}'");
        if (writeError is not null) return writeError;

        // The first variant of a project: its current state becomes this variant.
        bool firstVariant = before.AvailableVariants.Count == 0 && string.IsNullOrEmpty(before.ActiveVariant);
        if (firstVariant)
        {
            try { session.SetActiveVariant(name); notes.Add($"'{name}' is the first variant of the project and was made the active variant."); }
            catch (Exception ex) { notes.Add($"'{name}' is the first variant but could not be made active: {ex.Message}"); }
        }

        return Report("created", name, group, [], notes);
    }

    public string Delete(string variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName)) return "[Error] variantName is required.";
        string name = variantName.Trim();

        string? err = SessionGuards.RequireOpenSolution(session);
        if (err is not null) return err;

        string original;
        VariantStatusResult before;
        try
        {
            original = session.ReadProjectVariantConfig();
            before = session.GetVariantStatus();
        }
        catch (Exception ex) { return McpErrors.EnsureError(ex.Message); }

        var (xml, editError, groups) = VariantConfigEditor.RemoveVariant(original, name);
        if (xml is null) return McpErrors.EnsureError(editError ?? "Could not edit ProjectVariantConfig.");

        string canonical = before.AvailableVariants.FirstOrDefault(v =>
            string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (IsActive(before, canonical))
            return McpErrors.EnsureError(
                $"Variant '{canonical}' is the active variant (CurrentProjectVariant = '{before.ActiveVariant}') and cannot be " +
                "deleted. Switch first with twincat_set_active_variant. Nothing was changed.");

        string? writeError = WriteAndVerify(xml, original, after =>
            !after.AvailableVariants.Any(v => string.Equals(v, canonical, StringComparison.OrdinalIgnoreCase)),
            $"deletion of variant '{canonical}'");
        if (writeError is not null) return writeError;

        var notes = new List<string>();
        string items = SafeReadItems();
        if (items.Contains(canonical, StringComparison.OrdinalIgnoreCase))
            notes.Add($"ProjectVariantItems still mentions '{canonical}': per-variant settings of tree items may remain " +
                      "and are not removed by this call.");
        return Report("deleted", canonical, null, groups, notes);
    }

    /// <summary>True when the variant is the active one, or a member of the active variant group.</summary>
    private static bool IsActive(VariantStatusResult status, string variant)
    {
        string active = status.ActiveVariant;
        if (string.Equals(active, variant, StringComparison.OrdinalIgnoreCase)) return true;
        if (active.Length > 2 && active[0] == '[' && active[^1] == ']')
        {
            string group = active[1..^1].Trim();
            return status.Groups.Any(g =>
                string.Equals(g.Name, group, StringComparison.OrdinalIgnoreCase) &&
                g.Members.Any(m => string.Equals(m, variant, StringComparison.OrdinalIgnoreCase)));
        }
        return false;
    }

    // Writes the edited XML, reads the variant list back, and rolls back to the original when the edit is not
    // confirmed. Returns an [Error] text or null.
    private string? WriteAndVerify(string xml, string original, Func<VariantStatusResult, bool> confirmed, string what)
    {
        try { session.WriteProjectVariantConfig(xml); }
        catch (Exception ex)
        {
            return McpErrors.EnsureError($"Writing ProjectVariantConfig failed: {ex.Message}{Rollback(original)}");
        }

        VariantStatusResult after;
        try { after = session.GetVariantStatus(); }
        catch (Exception ex)
        {
            return McpErrors.EnsureError(
                $"ProjectVariantConfig was written but could not be read back, so the {what} is not confirmed: {ex.Message}{Rollback(original)}");
        }
        if (!confirmed(after))
            return McpErrors.EnsureError(
                $"The {what} did not take effect: the variant list reads back as " +
                $"[{string.Join(", ", after.AvailableVariants)}].{Rollback(original)}");
        return null;
    }

    private string Rollback(string original)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(original)) return " The previous (empty) configuration could not be restored by writing; check the project.";
            session.WriteProjectVariantConfig(original);
            return " The previous variant configuration was written back.";
        }
        catch (Exception ex)
        {
            return $" Restoring the previous variant configuration FAILED: {ex.Message}";
        }
    }

    private string SafeReadItems()
    {
        try { return session.ReadProjectVariantItems(); }
        catch { return string.Empty; }
    }

    private string Report(string action, string variant, string? group,
        IReadOnlyList<string> removedFromGroups, IReadOnlyList<string> notes)
    {
        VariantStatusResult status;
        try { status = session.GetVariantStatus(); }
        catch (Exception ex) { return McpErrors.EnsureError($"Variant {action} but the status could not be read: {ex.Message}"); }
        return JsonSerializer.Serialize(
            new EditResult(action, variant, string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
                status.ActiveVariant, status.AvailableVariants, status.Groups, removedFromGroups, notes),
            JsonOpts);
    }
}
