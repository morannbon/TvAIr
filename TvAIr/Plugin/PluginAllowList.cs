using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TvAIr.Core;

namespace TvAIr.Plugin;

internal sealed class PluginAllowList
{
    public List<PluginAllowItem> Plugins { get; set; } = new();
}

internal sealed class PluginAllowItem
{
    public string Name { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

internal sealed class PluginAllowListService
{
    // Alpha 1.0.0: 開発中はプラグインDLLの更新頻度が高いため、
    // SHA-256許可リスト検証は土台だけ残して実行時OFFにする。
    // 正式リリース段階で true 化、または設定ファイル制御へ移行する想定。
    private static readonly bool EnableHashAllowListCheck = false;

    private readonly LogRepository _log;
    private readonly string _configFilePath;
    private PluginAllowList _allowList = new();

    public PluginAllowListService(LogRepository log)
    {
        _log = log;
        var configDir = Path.Combine(AppContext.BaseDirectory, "Config");
        Directory.CreateDirectory(configDir);
        _configFilePath = Path.Combine(configDir, "allowed-plugins.json");
        EnsureAllowListFile();
        Reload();
    }

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

            if (!EnableHashAllowListCheck)
            {
                return PluginValidationResult.Allowed($"開発モード: ハッシュ検証OFF file={fileName}", string.Empty);
            }

            var hash = ComputeSha256(fullPluginPath);
            var match = _allowList.Plugins.FirstOrDefault(p =>
                p.Enabled &&
                string.Equals(p.File, fileName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeHash(p.Sha256), hash, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return PluginValidationResult.Blocked($"未許可またはハッシュ不一致: file={fileName} sha256={hash}", hash);
            }

            return PluginValidationResult.Allowed(match.Name, hash);
        }
        catch (Exception ex)
        {
            return PluginValidationResult.Blocked($"検証失敗: {ex.Message}", string.Empty);
        }
    }

    public void Reload()
    {
        try
        {
            var json = File.ReadAllText(_configFilePath);
            _allowList = JsonSerializer.Deserialize<PluginAllowList>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new PluginAllowList();
        }
        catch (Exception ex)
        {
            _allowList = new PluginAllowList();
            _log.Add("Plugin", "AllowList", $"[Plugin] AllowList read failed: {ex.Message}");
        }
    }

    private void EnsureAllowListFile()
    {
        if (File.Exists(_configFilePath)) return;

        var initialJson = """
{
  "plugins": [
    { "name": "AIrhythm.BasicPlugin", "file": "AIrhythm.BasicPlugin.dll", "sha256": "", "enabled": false },
    { "name": "AIrithm.BasicPlugin (legacy alias)", "file": "AIrithm.BasicPlugin.dll", "sha256": "", "enabled": false }
  ]
}
""";
        File.WriteAllText(_configFilePath, initialJson);
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string NormalizeHash(string value)
        => (value ?? string.Empty).Replace("-", string.Empty).Trim().ToLowerInvariant();
}

internal sealed record PluginValidationResult(bool IsAllowed, string Message, string Sha256)
{
    public static PluginValidationResult Allowed(string name, string sha256)
        => new(true, string.IsNullOrWhiteSpace(name) ? "許可済み" : $"許可済み: {name}", sha256);

    public static PluginValidationResult Blocked(string message, string sha256)
        => new(false, message, sha256);
}
