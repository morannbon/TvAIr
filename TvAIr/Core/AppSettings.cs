namespace TvAIr.Core;

/// <summary>
/// アプリ全体の設定。appsettings.json の各セクションにバインドされる。
/// </summary>
public sealed class AppSettings
{
    /// <summary>Webサーバーのリッスンポート</summary>
    public int Port { get; set; } = 55884;

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
    public bool UseMinOption { get; set; } = true;

    /// <summary>
    /// /nodshow オプション: DirectShow無効化（CPU負荷軽減）。
    /// true の場合 /nodshow を付加する（デフォルト: true）。
    /// 視聴用映像表示を必要としない起動での負荷軽減に使う。
    /// </summary>
    public bool UseNodshowOption { get; set; } = true;
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
        return normalized is "GR" or "BSCS" or "HYBRID";
    }

    public static string GroupLabel(string? group)
        => NormalizeGroup(group) switch
        {
            "GR" => "地上波",
            "BSCS" => "BS/CS",
            "HYBRID" => "地デジ/BS/CS",
            "" => "未設定",
            var raw => raw
        };

    public static string Build(string? group, string? did)
    {
        var label = GroupLabel(group);
        var id = (did ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(id) ? label : $"{label}-{id}";
    }

    public static string ForUi(string? name, string? group, string? did)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n)) return Build(group, did);

        // 旧版由来の内部名・BonDriver名などは表示名へ正規化する。
        var upper = n.ToUpperInvariant();
        if (upper.Contains("BONDRIVER") || upper is "GR" or "BSCS" or "HYBRID")
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

    /// <summary>グループ (GR / BSCS / HYBRID)</summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// LogicalTunerIdentity。空の場合は /DID オプションなしで起動する。
    /// </summary>
    public string Did { get; set; } = "";

    /// <summary>用途。Recording=録画/EPG用、Viewing=視聴用。</summary>
    public string Role { get; set; } = "";
}

/// <summary>
/// EPGキャプチャの設定
/// </summary>
public sealed class EpgSettings
{
    /// <summary>EPG取得機能の有効/無効</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>毎日自動取得する時刻（時）</summary>
    public int DailyRefreshHour { get; set; } = 3;

    /// <summary>毎日自動取得する時刻（分）</summary>
    public int DailyRefreshMinute { get; set; } = 0;

    /// <summary>TSファイルの一時保存ディレクトリ。空の場合はDataDirectory配下のts-recを使用。</summary>
    public string TsRecordDirectory { get; set; } = "";

    /// <summary>
    /// EPG取得深度。shallow=120秒/TS、medium=180秒/TS、deep=240秒/TS、deeper=300秒/TS。
    /// この値から PerChannelWaitSeconds が算出される。
    /// </summary>
    public string EpgDepth { get; set; } = "medium";
    /// <summary>
    /// 1TSあたりの録画待機秒数。EpgDepthから自動算出される（読み取り専用）。
    /// shallow=120、medium=180、deep=240、deeper=300。
    /// </summary>
    public int PerChannelWaitSeconds => EpgDurationPolicy.BaseSecondsForDepth(EpgDepth);

    /// <summary>全体の取得時間上限（分）</summary>
    public int TotalTimeLimitMinutes { get; set; } = 180;

    /// <summary>TSファイルのパース最大パケット数（0=EOFまで全量）</summary>
    public int MaxPacketsToScan { get; set; } = 0;

    /// <summary>
    /// 診断モード。通常運用では false 推奨。
    /// release_contract以降、EPG汚染系の診断はタグ別カウント中心に限定し、
    /// 汚染本文・rawHex・サンプル文字列は通常ログへ出さない。
    /// </summary>
    public bool DiagnosticMode { get; set; } = false;

    /// <summary>
    /// 同一TS内サービス数によるEPG取得秒数の自動延長設定。
    /// release_contractでは通常EPGの実行時間には使わない。EPGが薄い場合は秒数延長ではなく、ch2由来のNID/TSID/SID束ねを優先して確認する。
    /// </summary>
    public int MultiServiceExtraSeconds { get; set; } = 0;

    /// <summary>
    /// 録画開始何分前に直前EPG確認を行うか。時間追従（延長・繰り上げ対応）に使用。
    /// デフォルト10分。設定メニューから変更可能。
    /// </summary>
    public int EpgPreRecordMinutes { get; set; } = 15;
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

/// <summary>
/// 予約・録画の設定
/// </summary>
public sealed class ReservationSettings
{
    /// <summary>録画開始マージン（秒）。番組開始時刻のN秒前にTVTestを起動する。</summary>
    public int PreStartMarginSeconds { get; set; } = 30;

    /// <summary>録画終了マージン（秒）。番組終了時刻のN秒後にTVTestを停止する。</summary>
    public int PostEndMarginSeconds { get; set; } = 30;

    /// <summary>チャンネルロック待ち秒数（/recdelay）。CSのサービス確定待ちを考慮し既定10秒。</summary>
    public int RecDelaySeconds { get; set; } = 10;

    /// <summary>スリープ復帰のためにタスクスケジューラーへ登録する録画開始前の分数。(現在は内部未使用、互換のため残置)</summary>
    public int WakeMinutesBefore { get; set; } = 10;

    /// <summary>
    /// スリープ復帰の余裕秒数(新設)。EPG確認起床と録画起床の両方に加算される。
    /// Wake①時刻 = StartTime − EpgPreRecordMinutes分 − WakeAdditionalSeconds秒
    /// Wake②時刻 = StartTime − PreStartMarginSeconds秒 − WakeAdditionalSeconds秒
    /// </summary>
    public int WakeAdditionalSeconds { get; set; } = 30;

    /// <summary>Windowsスタートアップ登録（起動時に自動起動）。</summary>
    public bool StartupEnabled { get; set; } = false;

    /// <summary>
    /// チューナー競合時の優先設定。
    /// false = 前番組優先（後番組を failed にする）
    /// true  = 後番組優先（前番組を終了させて後番組を録画する）
    /// </summary>
    public bool LaterProgramPriority { get; set; } = false;

    /// <summary>
    /// 疑似チューナー引き継ぎ機能の有効/無効。
    /// 同局の連続番組を1チューナーで録画する。前番組を前倒し終了し、
    /// 同じチューナーで後番組を通常の前マージンから録画開始する。
    /// 番組間に PseudoContinuousMarginSeconds 分の空白が生じる。
    /// </summary>
    public bool PseudoContinuousRecording { get; set; } = false;

    /// <summary>
    /// 疑似チューナー引き継ぎ時の前番組前倒し終了マージン（秒）。
    /// 後番組開始時刻のN秒前に前番組を終了させる。
    /// TVTest終了＋再起動の余裕を確保するため PreStartMarginSeconds より大きい値を推奨。
    /// デフォルト60秒。
    /// </summary>
    public int PseudoContinuousMarginSeconds { get; set; } = 60;

    /// <summary>最後の録画が正常終了した後のWindows電源操作。none/sleep/shutdown。</summary>
    public string RecordingAfterAction { get; set; } = "none";

    /// <summary>録画プロセス終了後、録画終了後アクションを実行するまでの待機分数。1〜5分。</summary>
    public int RecordingAfterActionDelayMinutes { get; set; } = 1;
}
