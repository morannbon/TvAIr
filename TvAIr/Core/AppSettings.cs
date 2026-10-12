namespace TvAIr.Core;

/// <summary>
/// アプリ全体の設定。appsettings.json の各セクションにバインドされる。
/// </summary>
public sealed class AppSettings
{
    /// <summary>Webサーバーのリッスンポート</summary>
    public int Port { get; set; } = SettingsDefaults.Port;

    /// <summary>データファイル（DB・ログ等）の保存先ディレクトリ。未設定時は実行ファイルと同じ場所の data フォルダ。</summary>
    public string DataDirectory { get; set; } = "data";
}

/// <summary>
/// TVTest関連の設定
/// </summary>
public sealed class TvTestSettings
{
    /// <summary>TVTest.exeのフルパス</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>BonDriverのディレクトリ</summary>
    public string BonDriverDirectory { get; set; } = "";

    /// <summary>視聴用TVTest.exeのフルパス。</summary>
    public string ViewingTvTestExecutablePath { get; set; } = "";

    /// <summary>ドライラン（実際にTVTestを起動しない）</summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// /min オプション: タスクバー最小化起動。
    /// true の場合 /min を付加する（デフォルト: true）。
    /// </summary>
    public bool UseMinOption { get; set; } = SettingsDefaults.UseMinOption;

    /// <summary>
    /// /nodshow オプション: DirectShow無効化（CPU負荷軽減）。
    /// true の場合 /nodshow を付加する（デフォルト: true）。
    /// 視聴用映像表示を必要としない起動での負荷軽減に使う。
    /// </summary>
    public bool UseNodshowOption { get; set; } = SettingsDefaults.UseNodshowOption;
}

/// <summary>
/// チューナー名の表示用ユーティリティ。
/// BonDriverなどの内部名はUIに出さず、波種 + 識別子に統一する。
/// </summary>
public static class TunerDisplayName
{
    public static string NormalizeGroup(string? group)
    {
        var raw = (group ?? string.Empty).Trim();
        var g = raw.ToUpperInvariant();
        return g switch
        {
            "GR" or "地上波" or "地デジ" or "GROUND" => "GR",
            "BS" or "CS" or "BSCS" or "BS/CS" => "BSCS",
            "HYBRID" or "GRBSCS" or "GR/BSCS" or "GR/BS/CS" or "地/BS/CS" or "地デジ/BS/CS" or "地上波/BS/CS" => "HYBRID",
            _ => raw
        };
    }

    public static bool IsKnownGroup(string? group)
    {
        var normalized = NormalizeGroup(group);
        return normalized is "GR" or "BSCS" or "CATV" or "HYBRID";
    }

    public static string GroupLabel(string? group)
        => NormalizeGroup(group) switch
        {
            "GR" => "地上波",
            "BSCS" => "BS/CS",
            "CATV" => "CATV",
            "HYBRID" => "地デジ/BS/CS",
            "" => "未設定",
            var raw => raw
        };

    public static string PrioritySlotName(string? group, int ordinal)
    {
        var index = Math.Max(1, ordinal);
        return NormalizeGroup(group) switch
        {
            "GR" => $"T{index}",
            "BSCS" => $"S{index}",
            "CATV" => $"C{index}",
            "HYBRID" => $"H{index}",
            _ => $"T{index}"
        };
    }

    public static string Build(string? group, string? did)
    {
        var normalized = NormalizeGroup(group);
        var id = (did ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(id) && id.Length == 1 && id[0] >= 'A' && id[0] <= 'Z')
        {
            var ordinal = normalized switch
            {
                "BSCS" => id[0] >= 'E' ? id[0] - 'E' + 1 : id[0] - 'A' + 1,
                "HYBRID" => id[0] - 'A' + 1,
                _ => id[0] - 'A' + 1
            };
            return PrioritySlotName(normalized, ordinal);
        }
        var label = GroupLabel(normalized);
        return string.IsNullOrWhiteSpace(id) ? label : $"{label}-{id}";
    }

    public static string ForUi(string? name, string? group, string? did)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n)) return Build(group, did);

        // 内部名・BonDriver名・物理名などは、
        // 予約リストの優先度表示正本である T/S/H の仮想枠名へ戻す。
        var upper = n.ToUpperInvariant();
        if (upper.Contains("BONDRIVER") || upper is "GR" or "BSCS" or "HYBRID"
            || upper.StartsWith("地上波-", StringComparison.OrdinalIgnoreCase)
            || upper.StartsWith("BS/CS-", StringComparison.OrdinalIgnoreCase))
            return Build(group, did);

        return n;
    }
}

/// <summary>
/// チューナープロファイル。物理チューナー1本を1エントリで表す。
/// </summary>
public sealed class TunerProfile
{
    /// <summary>表示名。未設定時は LogicalTunerDisplayName で生成する。</summary>
    public string Name { get; set; } = "";

    /// <summary>BonDriverのDLLファイル名。未設定の場合は実行候補にしない。</summary>
    public string BonDriverFileName { get; set; } = "";

    /// <summary>Optional .ch2 produced for this BonDriver. Empty means legacy channel resolution.</summary>
    public string ChannelCh2Path { get; set; } = "";

    /// <summary>グループ (GR / BSCS / HYBRID)</summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// LogicalTunerIdentity。空の場合は /DID オプションなしで起動する。
    /// </summary>
    public string Did { get; set; } = "";

    /// <summary>用途。Recording=録画/EPG用、Viewing=視聴用。</summary>
    public string Role { get; set; } = "";

    /// <summary>本体設定画面で各放送波内に割り当てたデバイス番号。地上波1/BS・CS4等の番号をそのまま保持する。</summary>
    public int DeviceNumber { get; set; }

    /// <summary>設定行ごとに一度発行して保持する永続論理ID。名称・順序・BonDriver変更では変えない。</summary>
    public string LogicalViewerSlotId { get; set; } = "";
}

/// <summary>
/// EPGキャプチャの設定
/// </summary>
public sealed class EpgSettings
{
    /// <summary>TSファイルの一時保存ディレクトリ。空の場合はDataDirectory配下のts-recを使用。</summary>
    public string TsRecordDirectory { get; set; } = "";

    /// <summary>全体の取得時間上限（分）</summary>
    public int TotalTimeLimitMinutes { get; set; } = 180;

    /// <summary>TSファイルのパース最大パケット数（0=EOFまで全量）</summary>
    public int MaxPacketsToScan { get; set; } = 0;
}

/// <summary>
/// チャンネルマップの設定
/// </summary>
public sealed class ChannelMapSettings
{
    /// <summary>地上波 ch2 の ConfiguredCh2Path</summary>
    public string GrChannelFilePath { get; set; } = "";

    /// <summary>地上波 ChSet.txt の明示パス</summary>
    public string GrChSetFilePath { get; set; } = "";

    /// <summary>BS/CS ch2 の ConfiguredCh2Path</summary>
    public string BscsChannelFilePath { get; set; } = "";

    /// <summary>BS/CS ChSet.txt の明示パス</summary>
    public string BscsChSetFilePath { get; set; } = "";
}

