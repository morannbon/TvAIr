using Microsoft.Win32;

namespace TvAIr.Core;

/// <summary>
/// Windows起動時にTvAIrを自動起動するためのレジストリ Run キーを管理するサービス。
///
/// 登録先: HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// 値名:   TvAIr
/// 値:     TvAIr.exe のフルパス（ダブルクォート付き）
///
/// 現在ユーザー範囲（HKCU）のため管理者権限不要。
/// 設定保存時および起動時に StartupEnabled の値に応じて Set(true/false) を呼び出す。
/// </summary>
public sealed class StartupRegistryService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "TvAIr";

    private readonly LogRepository _log;

    public StartupRegistryService(LogRepository log)
    {
        _log = log;
    }

    /// <summary>
    /// enabled=true のとき Run キーに自身の実行ファイルパスを登録する。
    /// false のとき値を削除する。
    /// </summary>
    public bool Set(bool enabled)
    {
        try
        {
            // STARTUP_RUN_KEY_EXISTENCE_CONTRACT
            // 有効化時はRunキー自体が未作成でも登録できるようCreateSubKeyを使う。
            // 無効化時はRunキーが存在しないこと自体が「既に無効」の正常状態であり、失敗扱いにしない。
            using var key = enabled
                ? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                : Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                if (!enabled)
                    return true;

                _log.Add("Startup", "Registry", $"Run キーを作成できませんでした: HKCU\\{RunKeyPath}");
                return false;
            }

            if (enabled)
            {
                var exe = GetExecutablePath();
                var quoted = $"\"{exe}\"";
                var existing = key.GetValue(ValueName) as string;
                if (string.Equals(existing, quoted, StringComparison.OrdinalIgnoreCase))
                {
                    // 既に同一値で登録済み。ログを抑止して何もしない。
                    return true;
                }

                key.SetValue(ValueName, quoted, RegistryValueKind.String);
                _log.Add("Startup", "Registry", $"自動起動を有効化しました: {exe}");
                return true;
            }

            if (key.GetValue(ValueName) is null)
            {
                // 既に未登録。ログを抑止して何もしない。
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            _log.Add("Startup", "Registry", "自動起動を無効化しました。");
            return true;
        }
        catch (Exception ex)
        {
            _log.Add("Startup", "Registry", $"自動起動設定の更新に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>現在の登録状態を返す。読み取りのみ。</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string GetExecutablePath()
    {
        var path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(path))
            return path;
        return Path.Combine(AppContext.BaseDirectory, "TvAIr.exe");
    }
}
