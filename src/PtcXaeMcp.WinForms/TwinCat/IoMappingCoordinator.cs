namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed class IoMappingCoordinator(ITwinCatSession session)
{
    public string LinkVariables(string v1, string v2, int offs1 = 0, int offs2 = 0, int size = 0)
    {
        if (string.IsNullOrWhiteSpace(v1))
            return "[Error] variable1TreePath is required.";
        if (string.IsNullOrWhiteSpace(v2))
            return "[Error] variable2TreePath is required.";

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try
        {
            session.LinkVariables(v1, v2, offs1, offs2, size);
            return $"Linked '{v1}' ↔ '{v2}'.";
        }
        catch (InvalidOperationException ex) { return $"[Error] {ex.Message}"; }
    }

    public string UnlinkVariables(string v1, string v2 = "")
    {
        if (string.IsNullOrWhiteSpace(v1))
            return "[Error] variable1TreePath is required.";

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try
        {
            session.UnlinkVariables(v1, v2);
            string target = string.IsNullOrWhiteSpace(v2) ? "all links" : $"link to '{v2}'";
            return $"Unlinked {target} from '{v1}'.";
        }
        catch (InvalidOperationException ex) { return $"[Error] {ex.Message}"; }
    }

    // outputPath: null keeps the original default (Mappings.xml next to the .sln)
    // for backward compatibility. That default unconditionally writes into the
    // solution directory, which — unlike every other tool that touches the
    // filesystem — is not opt-in and previously had no way to redirect it
    // (e.g. to a scratch dir for inspection without touching the working tree).
    public string GetMappingInfo(string? outputPath = null)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        string xml;
        try { xml = session.GetMappingInfo(); }
        catch (InvalidOperationException ex) { return $"[Error] {ex.Message}"; }

        string outPath;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            outPath = outputPath;
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                return $"[Error] Target directory does not exist: {dir}";
        }
        else
        {
            string? slnPath = session.ActiveSolutionPath();
            string? slnDir = slnPath is not null ? Path.GetDirectoryName(slnPath) : null;
            if (slnDir is null)
                return "[Error] Cannot determine output directory: no solution path available.";
            outPath = Path.Combine(slnDir, "Mappings.xml");
        }

        try { File.WriteAllText(outPath, xml, System.Text.Encoding.UTF8); }
        catch (Exception ex) { return $"[Error] Could not write {outPath}: {ex.Message}"; }

        return $"Mappings written to {outPath}";
    }

    public string SetDeviceDisabled(string treePath, bool disabled)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return "[Error] treePath is required.";

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try
        {
            session.SetDeviceDisabled(treePath, disabled);
            string state = disabled ? "disabled" : "enabled";
            return $"Device '{treePath}' {state}.";
        }
        catch (InvalidOperationException ex) { return $"[Error] {ex.Message}"; }
    }
}
