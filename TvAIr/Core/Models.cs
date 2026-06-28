namespace TvAIr.Core;

// ─── EPGイベント ─────────────────────────────────────────────────

/// <summary>
/// EITから取得した生の番組情報。DB保存・API応答の基本単位。
/// </summary>
public sealed class EpgEvent
{
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string ServiceName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Genre { get; set; } = "";
    public string GenreCodes { get; set; } = "";
    // EPG raw-first store: these fields are captured/decoded facts, not UI projection state.
    public byte TableId { get; set; }
    public byte SectionNumber { get; set; }
    public byte VersionNumber { get; set; }
    public string RawDescriptorLoopHex { get; set; } = "";
    public string RawShortEventDescriptorHex { get; set; } = "";
    public string RawExtendedEventDescriptorHex { get; set; } = "";
    public string RawContentDescriptorHex { get; set; } = "";
    public int DurationSeconds { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public DateTime UpdatedAt { get; set; }

}

// ─── チャンネル ──────────────────────────────────────────────────

/// <summary>
/// EPGキャプチャ対象の1サービス（局）
/// </summary>
public sealed class ChannelTarget
{
    public string Group { get; set; } = "";        // "GR" / "BSCS"
    public ushort ServiceId { get; set; }
    public ushort OriginalNetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public string Name { get; set; } = "";
    public string ChannelArgument { get; set; } = ""; // "/ch 14" / "/chspace 0 /chi 3" 等

    // v0.3.77: BS/CSチャンネル取り違え調査用。録画開始直前に、.ch2/ChSet由来の
    // チャンネル同定情報をログへ出し、TVTestへ渡している /chspace /chi /sid が
    // どの行・どのTSID対応から生成されたかを追えるようにする。
    public string Ch2FileName { get; set; } = "";
    public int Ch2LineNumber { get; set; }
    public int BonDriverChannel { get; set; }
    public int ResolvedSpace { get; set; }
    public int ResolvedChannelIndex { get; set; }
    public int SameTransportServiceCount { get; set; }
    public string ChannelBuildSource { get; set; } = "";
}

// ─── 予約 ────────────────────────────────────────────────────────

public enum ReservationStatus { Scheduled, Recording, Completed, Cancelled, Failed }
public enum ReservationSource { Manual, Immediate, KeywordSearch, Keyword, Program, Epg }

public sealed class Reservation
{
    public int Id { get; set; }
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string Title { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Scheduled;
    public ReservationSource Source { get; set; } = ReservationSource.Manual;
    /// <summary>チューナー起動時のチャンネル引数（/ch 14, /chspace 0 /chi 3 等）。予約登録時にChannelFileLoaderから解決して保存する。</summary>
    public string ChannelArgument { get; set; } = "";
    /// <summary>チューナー競合フラグ。同一グループ・同時間帯でチューナー数を超えた場合 true。予約リストで赤文字表示される。</summary>
    public bool IsConflicted { get; set; } = false;
    /// <summary>ユーザーが能動的に録画ON/OFFを選択するフラグ。false=録画しない（リストには残る）。</summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>事前に割り当てたチューナー名（例: 地上波-A）。競合時は空文字。</summary>
    public string TunerName { get; set; } = "";
    /// <summary>録画開始時に実際に確保したチューナー名。ログ/完了表示ではこちらを優先する。</summary>
    public string ActualTunerName { get; set; } = "";
    /// <summary>録画開始実績。未開始の予約取消はログタブに表示しない。</summary>
    public DateTime? RecordingStartedAt { get; set; } = null;
    /// <summary>録画終了実績。</summary>
    public DateTime? RecordingFinishedAt { get; set; } = null;
    /// <summary>局名（例: NHK総合・東京）。登録時にEPG/チャンネルファイルから解決して保存。</summary>
    public string ServiceName { get; set; } = "";
    /// <summary>
    /// 元の予約開始時刻（登録時刻）。時間追従でStartTimeが更新されても元の時刻を保持する。
    /// 空の場合はStartTimeと同じと見なす。プラグイン等からの参照用。
    /// </summary>
    public DateTime? ScheduledStartTime { get; set; } = null;
    /// <summary>自動検索予約で作成された場合のルールID。手動/プログラム予約ではnull。</summary>
    public int? SourceRuleId { get; set; } = null;
    /// <summary>自動検索予約で作成された場合のルール名スナップショット。</summary>
    public string SourceRuleName { get; set; } = "";
    /// <summary>ユーザーが番組表の「チェーン」ボタンで明示的に指定したチェーン予約。</summary>
    public bool IsUserChain { get; set; } = false;
    /// <summary>ユーザー明示チェーンの直前予約ID。通常予約ではnull。</summary>
    public int? UserChainPreviousId { get; set; } = null;
    /// <summary>ユーザー明示チェーンの先頭予約ID。通常予約ではnull。</summary>
    public int? UserChainRootId { get; set; } = null;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ─── キーワードルール ────────────────────────────────────────────

/// <summary>
/// 自動予約キーワードルール。
/// pattern / exclude_pattern ともに通常文字列または正規表現（use_regex=true 時）。
/// [生][初][新][再] 等のARIBシンボルもそのまま検索可能。
/// </summary>
public sealed class KeywordRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>検索キーワード。UseRegex=true なら正規表現、false なら ; | () を使う通常検索式。</summary>
    public string Pattern { get; set; } = "";

    /// <summary>除外キーワード。UseRegex=true なら正規表現、false なら ; | () を使う通常検索式。</summary>
    public string ExcludePattern { get; set; } = "";

    /// <summary>正規表現を使うか。false の場合は ; = AND, | = OR, () = グループ。</summary>
    public bool UseRegex { get; set; } = false;

    /// <summary>後方互換用の旧検索対象。現在は各フィールド個別フラグを使用。</summary>
    public string SearchFields { get; set; } = "title";

    public bool SearchTitle { get; set; } = true;
    public bool SearchOutline { get; set; } = false;
    public bool SearchDetail { get; set; } = false;
    public bool SearchCast { get; set; } = false;

    public bool UseAllChannels { get; set; } = true;

    /// <summary>対象サービスID（カンマ区切り）。UseAllChannels=false のときのみ使用。</summary>
    public string TargetServices { get; set; } = "";

    /// <summary>対象ジャンルコード上位1桁（カンマ区切り）。空=全ジャンル。例: "6,3"</summary>
    public string TargetGenres { get; set; } = "";

    /// <summary>曜日指定。空=毎日。0=日〜6=土 のカンマ区切り。</summary>
    public string TargetDays { get; set; } = "";

    /// <summary>時間帯指定を使うか。</summary>
    public bool UseTimeRange { get; set; } = false;

    /// <summary>時間帯開始 HH:mm。</summary>
    public string StartTime { get; set; } = "00:00";

    /// <summary>時間帯終了 HH:mm。</summary>
    public string EndTime { get; set; } = "23:59";

    /// <summary>有効期限。空=無期限。</summary>
    public string ExpiresOn { get; set; } = "";

    /// <summary>表示順兼評価順。</summary>
    public int SortOrder { get; set; } = 0;

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ─── プログラム予約ルール ─────────────────────────────────────────

/// <summary>
/// 曜日・時刻指定の繰り返し予約ルール。
/// day_of_week: 0=毎日, 1=月, 2=火, 3=水, 4=木, 5=金, 6=土, 7=日
/// </summary>
public sealed class ProgramRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DayOfWeek { get; set; } = 0;
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "00:00";
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string ExpiresOn { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ─── ログエントリ ────────────────────────────────────────────────

public sealed class LogEntry
{
    public string Event { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class UserEventLogEntry
{
    public int Id { get; set; }
    public string Severity { get; set; } = "INFO";
    public string Category { get; set; } = "";
    public string Result { get; set; } = "";
    public string Target { get; set; } = "";
    public string Message { get; set; } = "";
    public string Code { get; set; } = "";
    public string TraceId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string PreviousAppVersion { get; set; } = "";
    public bool VersionChanged { get; set; }
    public string StoryContext { get; set; } = "";

    // UserOperationEvent 正本の報告用メタ属性。
    // ログタブでは原則表示せず、報告用コピーで展開する。
    public string Origin { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string StoryRole { get; set; } = "";
    public string TargetKind { get; set; } = "";
    public string Actionability { get; set; } = "";
    public string ReservationSource { get; set; } = "";
    public string ProgramTitle { get; set; } = "";
    public string Detail { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// ─── API リクエスト型 ────────────────────────────────────────────

/// <summary>スタートアップ登録ON/OFFリクエスト</summary>

/// <summary>録画ON/OFF切り替えリクエスト</summary>
public sealed class EnabledRequest
{
    public bool IsEnabled { get; set; } = true;
}

public sealed class KeywordRuleOrderRequest
{
    public int[] OrderedIds { get; set; } = Array.Empty<int>();
}

public sealed class KeywordRuleImportPayload
{
    public string Format { get; set; } = "TvAIr.KeywordRules";
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public List<KeywordRule> Rules { get; set; } = new();
}
