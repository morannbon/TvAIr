using TvAIr.Core;

namespace TvAIr.Plugin;

internal sealed class PluginAllowListService
{
    public PluginAllowListService(LogRepository log) { }

    public PluginValidationResult Validate(string pluginFilePath, string pluginsDirectory)
    {
        try
        {
            var fullPluginPath = Path.GetFullPath(pluginFilePath);
            var fullPluginsDirectory = Path.GetFullPath(pluginsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!fullPluginPath.StartsWith(fullPluginsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return PluginValidationResult.Blocked("Pluginsフォルダ外のDLLです。", string.Empty);
            }

            var fileName = Path.GetFileName(fullPluginPath);
            return PluginValidationResult.Allowed($"Pluginsフォルダ内を確認しました file={fileName}", string.Empty);
        }
        catch (Exception ex)
        {
            return PluginValidationResult.Blocked($"検証失敗: {ex.Message}", string.Empty);
        }
    }


}

internal sealed record PluginValidationResult(bool IsAllowed, string Message, string Sha256)
{
    public static PluginValidationResult Allowed(string name, string sha256)
        => new(true, string.IsNullOrWhiteSpace(name) ? "許可済み" : $"許可済み: {name}", sha256);

    public static PluginValidationResult Blocked(string message, string sha256)
        => new(false, message, sha256);
}
