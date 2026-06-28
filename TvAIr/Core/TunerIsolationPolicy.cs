namespace TvAIr.Core;

/// <summary>
/// 視聴用TVTest/LIVETestを録画・EPG系のOpen/Closeから隔離するためのチューナー役割ポリシー。
///
/// v0.3.39 方針:
///   - 不可侵判定は BonDriver 名ではなく、設定画面の Role(Viewing/Recording) を正とする。
///   - BonDriver未設定時に環境固有名を補完しない。実行側は論理リソース解決失敗として扱う。
/// </summary>
public static class TunerIsolationPolicy
{
    public static bool IsViewingRole(string? role)
        => string.Equals(IniSettingsService.NormalizeTunerRole(role), "Viewing", StringComparison.OrdinalIgnoreCase);

    public static bool IsViewingOnlyBonDriver(string? bonDriverFileName)
    {
        // BonDriver名だけでは不可侵判定しない。判定はRole側に一本化する。
        return false;
    }

    public static bool IsRecordingOnlyBonDriver(string? bonDriverFileName)
    {
        // v0.3.39: 明示されたBonDriverはそのまま尊重する。
        return !string.IsNullOrWhiteSpace(Path.GetFileName(bonDriverFileName ?? string.Empty));
    }

    public static string NormalizeBonDriverForRole(string? bonDriverFileName, string? group, string? role)
    {
        if (IsViewingRole(role))
            return ToViewingBonDriver(bonDriverFileName, group);

        return ToRecordingBonDriver(bonDriverFileName, group);
    }

    public static string ToRecordingBonDriver(string? bonDriverFileName, string? group)
    {
        var file = Path.GetFileName((bonDriverFileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(file))
            return string.Empty;

        // 明示されたBonDriver DLL名をそのまま尊重する。
        return NormalizeConfiguredBonDriverFileName(file);
    }

    public static string ToViewingBonDriver(string? bonDriverFileName, string? group)
    {
        var file = Path.GetFileName((bonDriverFileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(file))
            return string.Empty;

        // 視聴/録画の隔離は BonDriver 名ではなく Role/DID/所有PIDで行う。
        // BonDriver DLL名を勝手に別名へ変えない。
        return NormalizeConfiguredBonDriverFileName(file);
    }

    public static string NormalizeConfiguredBonDriverFileName(string? bonDriverFileName)
    {
        // v0.11.678: 環境固有BonDriver名への自動変換をしない。
        // 既存設定に書かれたDLL名は明示設定としてそのまま扱い、未設定は未解決のまま返す。
        return Path.GetFileName((bonDriverFileName ?? string.Empty).Trim());
    }

    public static string ResolveConfiguredCh2Path(string path)
    {
        // v0.11.678: ch2も明示設定されたパスだけを正とする。
        // 旧構成名・特定BonDriver名への自動rewriteは通常経路へ混ぜない。
        return path;
    }
}
