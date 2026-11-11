namespace CadReviewUI;

internal static class WorkspaceConfiguration
{
    internal static string ResolveRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("CADTEST_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("CADTEST_ROOT 必须是工作区的绝对路径。");
            return Normalize(configured);
        }
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "global.json")) &&
                File.Exists(Path.Combine(folder.FullName, @"work\segmented-20261109\runner.py")))
                return Normalize(folder.FullName);
        // Keep startup possible so CheckReady can explain a missing backend.
        return Normalize(AppContext.BaseDirectory);
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static string? FindExecutable(string variable, string? bundled, params string[] names)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured)) return ResolveExecutable(configured);
        if (bundled != null && File.Exists(bundled)) return Path.GetFullPath(bundled);
        foreach (string name in names)
            if (ResolveExecutable(name) is { } found) return found;
        return null;
    }

    static string? ResolveExecutable(string value)
    {
        value = value.Trim().Trim('"');
        if (Path.IsPathFullyQualified(value)) return File.Exists(value) ? Path.GetFullPath(value) : null;
        if (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            string path = Path.GetFullPath(value, Engine.Root);
            return File.Exists(path) ? path : null;
        }
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (string name in Path.HasExtension(value) ? new[] { value } : new[] { value + ".exe", value })
            {
                string candidate = Path.Combine(directory, name);
                if (candidate.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase) &&
                    name.StartsWith("python", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }
}
