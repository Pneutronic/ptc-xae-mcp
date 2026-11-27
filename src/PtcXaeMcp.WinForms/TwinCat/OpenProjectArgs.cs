namespace PtcXaeMcp.WinForms.TwinCat;

internal static class OpenProjectArgs
{
    public static string? Validate(string? slnPath)
    {
        if (string.IsNullOrWhiteSpace(slnPath))
            return "slnPath is required.";
        if (!Path.IsPathFullyQualified(slnPath))
            return "slnPath must be an absolute path.";
        if (!SolutionFile.IsSolution(slnPath) && !SolutionFile.IsProjectFile(slnPath))
            return "slnPath must end with .sln, .tsproj, .tspproj, .hmiproj or .plcproj.";
        if (!File.Exists(slnPath))
            return $"File not found: {slnPath}";
        return null;
    }
}
