namespace TvAIr.Channel;

/// <summary>Resolve only the selected driver's channel table. No wave or filename guessing.</summary>
public static class BonDriverChannelSourceResolver
{
    public static string Resolve(string tvTestExecutablePath, string bonDriverDirectory, string driver)
    {
        driver = (driver ?? "").Trim();
        if (driver.Length == 0 || driver != Path.GetFileName(driver)
            || !driver.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return "";
        var name = Path.ChangeExtension(driver, ".ch2");
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(bonDriverDirectory)) roots.Add(Path.GetFullPath(bonDriverDirectory));
        if (!string.IsNullOrWhiteSpace(tvTestExecutablePath))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(tvTestExecutablePath));
            if (!string.IsNullOrEmpty(folder)) roots.Add(folder);
        }
        var paths = roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => Path.Combine(p, name)).ToArray();
        // The configured BonDriver directory takes precedence over the TVTest folder.
        // Retain a missing expected path so an absent source rejects this driver,
        // rather than falling back to another driver's common ch2.
        return paths.FirstOrDefault(File.Exists) ?? paths.FirstOrDefault() ?? "";
    }
}
