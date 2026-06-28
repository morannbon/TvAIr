namespace TvAIrPlugin;

/// <summary>プラグイン種別。TvAIr本体は種別に応じて安全な範囲だけ呼び出す。</summary>

/// <summary>録画開始・終了通知でプラグインへ渡す情報。</summary>
public sealed class PluginRecordingInfo
{
    public int ReservationId { get; init; }
    public string Title { get; init; } = string.Empty;
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset ActualStartTime { get; init; }
    public DateTimeOffset? ActualEndTime { get; init; }
    public string OutputFilePath { get; init; } = string.Empty;
}

/// <summary>TSファイル再生開始・停止通知でプラグインへ渡す情報。</summary>
public sealed class PluginPlaybackInfo
{
    public string FilePath { get; init; } = string.Empty;
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset BroadcastStartTime { get; init; }
    public TimeSpan Duration { get; init; }
    public nint WindowHandle { get; init; }
}

/// <summary>TSファイル再生位置通知でプラグインへ渡す情報。</summary>
public sealed class PluginPlaybackPosition
{
    public string FilePath { get; init; } = string.Empty;
    public TimeSpan Position { get; init; }
    public DateTimeOffset BroadcastTime { get; init; }
}


/// <summary>NicoJkPlugin等が本体UIへ渡すライブコメントイベント。</summary>
public sealed class LiveCommentEvent
{
    public string PluginId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public int JkChannel { get; init; }
    public long UnixTime { get; init; }
    public int? Vpos { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>プラグインから本体へライブコメントを通知する任意拡張契約。</summary>
public interface ILiveCommentPublisher
{
    void PublishLiveComment(LiveCommentEvent comment);
}

public enum TvAIrPluginKind
{
    Unknown = 0,
    Analysis = 1,
    Viewer = 2,
    Utility = 3,
    UI = 4,
    Companion = 5,
    Remote = 6,
    Headless = 7
}

public enum PluginLogLevel { Debug, Info, Warning, Error }

public enum PluginPermission
{
    // v0.10.22: 既存値の順序は互換性維持のため変更しない。新権限は末尾に追加する。
    ReadEpg,
    ReadReservations,
    WriteReservations,
    PreviewAllocation,
    ReadTunerStatus,
    UsePluginStorage,
    ShowUi,
    ShowNotification,
    LaunchExternalProcess,
    ControlViewer,

    // 読み取り系。ローカルパスや環境情報を返すものは必要時だけ個別許可する。
    ReadChannels,
    ReadRecordingStatus,
    ReadRecordingHistory,
    ReadWakePlan,
    ReadLogs,
    ReadTheme,
    ReadPluginStorage,
    WritePluginStorage,
    ReadSafePaths,

    // 操作要求系。プラグインが直接チューナー/Wake/DBを触らず、本体共通ルートへ要求するための権限。
    ManageReservations,
    ManageAutoSearch,
    ControlRecording,
    ControlEpg,
    ControlWake,
    ShowNotifications,

    // v0.10.23 読み取りAPI拡張。既存値を壊さず末尾に追加する。
    ReadSystemStatus,
    ReadEpgStatus,
    ReadKeywordRules,
    ReadProgramRules,
    ReadRecordingQuality,

    // v0.10.39 番組表投影API。既存値を壊さず末尾に追加する。
    ReadProgramGuideProjection,
    ReadViewerSessions,

    // v0.10.75 ViewerControl/API投影拡張。既存値を壊さず末尾に追加する。
    ReadViewerTuners,
    ReadViewerControlContracts,

    // v0.11.16 リリース前API面の明文化。TVTestヘッダ由来の概念をTvAIr抽象契約として読むための権限。
    ReadHostContracts,

    // v0.11.315: 汎用Plugin Host Contract。新規プラグインはkindだけでなくcapability/permissionで明示する。
    OpenPage,
    OpenToolWindow,
    UseActionApi,
    UseWindowApi,
    UseAssetApi,
    UseSafeEvent,
    UseRemoteAccess,
    UsePairing,
    UseLocalNetwork
}

/// <summary>Plugin Manifest。正式版では権限確認・署名/ハッシュ許可制と連動する。</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>v0.11.315: 配布元/作者名。権限判定ではなく表示・監査用。</summary>
    public string Vendor { get; set; } = string.Empty;
    /// <summary>v0.11.315: プラグインが前提にするTvAIr Plugin Host Contract。例: 0.11.315。</summary>
    public string HostContractVersion { get; set; } = string.Empty;
    /// <summary>v0.11.315: kindとは別の機能宣言。新規拡張はここに追加し、kind固定にしない。</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
    /// <summary>v0.11.315: 汎用manifestの任意タグ。TvAIr本体は未知タグを拒否しない。</summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    /// <summary>v0.11.15: host-managed tool window の Form.Icon 用 .ico 名。HTML表示用assetとは別契約。</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>v0.10.75: プラグイン一覧から直接開く推奨モード。page / toolWindow / browser。</summary>
    public string PreferredOpenMode { get; set; } = string.Empty;
    public string DefaultRoute { get; set; } = string.Empty;
    public int ToolWindowWidth { get; set; } = 620;
    public int ToolWindowHeight { get; set; } = 760;
    /// <summary>v0.11.58: host-managed tool window の最小幅。fallback menu 経路でも本体が強制する。</summary>
    public int ToolWindowMinWidth { get; set; } = 0;
    /// <summary>v0.11.58: host-managed tool window の最小高。fallback menu 経路でも本体が強制する。</summary>
    public int ToolWindowMinHeight { get; set; } = 0;
    public bool ToolWindowReuseExisting { get; set; } = true;
    public bool ToolWindowActivateExisting { get; set; } = true;

    /// <summary>v0.11.30: ハンバーガー/タスクトレイ共通プラグインメニューで実行する既定アクション。toolWindow / page / settings / versionDialog / statusDialog / none。</summary>
    public string DefaultMenuActionKind { get; set; } = string.Empty;
    public string DefaultMenuActionLabel { get; set; } = string.Empty;
    public int DefaultMenuActionPriority { get; set; } = 1000;
    public bool ToolWindowShowInTaskbar { get; set; } = false;

    public IReadOnlyList<string> Kind { get; set; } = Array.Empty<string>();
    public IReadOnlyList<PluginPermission> Permissions { get; set; } = Array.Empty<PluginPermission>();
}

/// <summary>Manifest提供プラグイン用の任意契約。</summary>
public interface IManifestPlugin : ITvAIrPlugin
{
    PluginManifest Manifest { get; }
}


/// <summary>
/// v0.10.22以降の拡張API方針。IPluginContext本体は破壊的変更せず、
/// 新機能は追加インターフェースとして提供する。
/// </summary>
public interface IPluginExtendedContextV1 : IPluginContext
{
    IReadOnlyList<PluginChannelInfo> GetChannels(PluginChannelQuery? query = null);
    IReadOnlyList<PluginRecordingSessionInfo> GetRecordingSessions();
    IReadOnlyList<PluginWakePlanItem> GetWakePlan(PluginWakePlanQuery? query = null);
    PluginViewerOperationResult RequestViewerStart(PluginViewerStartRequest request);
    PluginViewerOperationResult RequestViewerStop(PluginViewerStopRequest request);
}

public sealed class PluginChannelQuery
{
    public string? Group { get; set; }
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
    public string? Keyword { get; set; }
}

public sealed class PluginChannelInfo
{
    public string Group { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    /// <summary>互換alias。公式fieldは NetworkId / TransportStreamId / ServiceId。</summary>
    public ushort Nid { get; set; }
    public ushort Tsid { get; set; }
    public ushort Sid { get; set; }
    public int ChannelSpace { get; set; }
    public int ChannelIndex { get; set; }
    public string ChannelArgument { get; set; } = string.Empty;
    public bool IsEnabledInUserChannelSet { get; set; }
}

public sealed class PluginRecordingSessionInfo
{
    public int ReservationId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string TunerName { get; set; } = string.Empty;
    public int? ProcessId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime PlannedEnd { get; set; }
}

public sealed class PluginWakePlanQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class PluginWakePlanItem
{
    public DateTime At { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int? ReservationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
}

public sealed class PluginViewerStartRequest
{
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string ChannelArgument { get; set; } = string.Empty;
    public string BroadcastGroup { get; set; } = string.Empty;
    public string TunerGroup { get; set; } = string.Empty;
    public int? ChannelSpace { get; set; }
    public int? ChannelIndex { get; set; }
    public string PreferredTunerName { get; set; } = string.Empty;
    public string PreferredDid { get; set; } = string.Empty;
    public int? PreferredSlot { get; set; }
}

public sealed class PluginViewerStopRequest
{
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
}

public sealed class PluginViewerOperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}


/// <summary>
/// v0.10.23: Phase 2 読み取りAPI拡張。
/// IPluginContext/IPluginExtendedContextV1は破壊せず、読み取り専用の追加契約として提供する。
/// ローカルパスやOS操作は返さず、TvAIrが安全に正規化した情報だけを渡す。
/// </summary>
public interface IPluginReadContextV2 : IPluginExtendedContextV1
{
    IReadOnlyList<PluginEpgEvent> GetCurrentPrograms(PluginCurrentProgramQuery? query = null);
    IReadOnlyList<PluginKeywordRuleInfo> GetKeywordRules(PluginRuleQuery? query = null);
    IReadOnlyList<PluginProgramRuleInfo> GetProgramRules(PluginRuleQuery? query = null);
    IReadOnlyList<PluginLogItem> GetLogs(PluginLogQuery? query = null);
    PluginEpgRunStateInfo GetEpgRunState();
    PluginSystemStatusInfo GetSystemStatus();
    PluginThemeInfo GetThemeInfo();
}

/// <summary>
/// v0.10.39: 本体番組表投影API。
/// GetChannels の独自ソートとは切り離し、TvAIr本体番組表が使うチャンネル投影順をそのまま公開する。
/// AIrCon等の視聴UIは局順・Now/Next・視聴状態を推定せず、この契約を正とする。
/// </summary>
public interface IPluginReadContextV3 : IPluginReadContextV2
{
    PluginProgramGuideSnapshot GetProgramGuideSnapshot(PluginProgramGuideQuery? query = null);
    IReadOnlyList<PluginProgramGuideChannel> GetProgramGuideChannels(PluginProgramGuideChannelQuery? query = null);
    IReadOnlyList<PluginProgramGuideNowNext> GetProgramGuideNowNext(PluginProgramGuideNowNextQuery? query = null);
    IReadOnlyList<PluginViewerSessionInfo> GetViewerSessions(PluginViewerSessionQuery? query = null);
}

/// <summary>
/// v0.10.75: ViewerControl/常駐視聴パネル向け。本体完成済みモジュールの公式投影API。
/// 番組表フィルタ分類、視聴用チューナー候補、行イベント契約をプラグイン側推定なしで使う。
/// </summary>
public interface IPluginReadContextV4 : IPluginReadContextV3
{
    IReadOnlyList<PluginViewerTunerInfo> GetViewerTuners(PluginViewerTunerQuery? query = null);
    /// <summary>v0.10.88: ViewerControl局行用の公式identity投影。viewerStart用tripletはこのAPIを正とする。</summary>
    IReadOnlyList<PluginViewerControlChannelInfo> GetViewerControlChannels(PluginProgramGuideChannelQuery? query = null);
    IReadOnlyList<PluginProgramGuideWaveFilterInfo> GetProgramGuideWaveFilters();
    PluginViewerControlHostContract GetViewerControlHostContract();
}

/// <summary>
/// v0.11.16: リリース前のプラグインAPI面確認契約。
/// TVTestPlugin.h の概念をそのまま生の MESSAGE_* ブリッジとして公開せず、
/// TvAIr本体が安全に抽象化したホスト能力・公開/非公開境界だけを返す。
/// </summary>
public interface IPluginReadContextV5 : IPluginReadContextV4
{
    PluginHostContractInfo GetHostContractInfo();
}

public sealed class PluginHostContractInfo
{
    public string ContractVersion { get; set; } = "0.11.315";
    public string Source { get; set; } = "TvAIrPluginHostContract";
    public string Purpose { get; set; } = "generic_plugin_host_contract";
    public IReadOnlyList<string> StableReadContracts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ControlledActionContracts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedKinds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedCapabilities { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedUiModes { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ManifestRequiredFields { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ManifestOptionalFields { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> OfficialContextFields { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CompatibilityAliases { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> NotExposedByDesign { get; set; } = Array.Empty<string>();
    public PluginTvTestHeaderReferenceInfo TvTestHeaderReference { get; set; } = new();
}

public sealed class PluginTvTestHeaderReferenceInfo
{
    public string SourceHeader { get; set; } = "TVTestPlugin.h ver.0.0.15-pre";
    public IReadOnlyList<string> AdoptedConcepts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> IntentionallyNotMirrored { get; set; } = Array.Empty<string>();
    public string Reason { get; set; } = string.Empty;
}


public sealed class PluginProgramGuideQuery
{
    public string? DisplayGroup { get; set; }
    /// <summary>v0.10.75: 番組表の放送波フィルタ分類。DisplayGroup互換だが、番組表ボタンの公式投影名。</summary>
    public string? ProgramGuideFilterGroup { get; set; }
    public string? AllocationGroup { get; set; }
    public bool IncludeNowNext { get; set; } = true;
    public int Limit { get; set; } = 500;
}

public class PluginProgramGuideChannelQuery
{
    public string? DisplayGroup { get; set; }
    /// <summary>v0.10.75: 番組表の放送波フィルタ分類。DisplayGroup互換だが、番組表ボタンの公式投影名。</summary>
    public string? ProgramGuideFilterGroup { get; set; }
    public string? AllocationGroup { get; set; }
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
    public int Limit { get; set; } = 500;
}

public sealed class PluginProgramGuideNowNextQuery : PluginProgramGuideChannelQuery
{
    public DateTime? At { get; set; }
}

public sealed class PluginViewerSessionQuery
{
    public string? DisplayGroup { get; set; }
    /// <summary>v0.10.75: 番組表の放送波フィルタ分類。DisplayGroup互換だが、番組表ボタンの公式投影名。</summary>
    public string? ProgramGuideFilterGroup { get; set; }
    public string? AllocationGroup { get; set; }
    public string? ClientId { get; set; }
}

public sealed class PluginProgramGuideSnapshot
{
    public DateTime SnapshotAt { get; set; }
    public long Revision { get; set; }
    public IReadOnlyList<PluginProgramGuideChannel> Channels { get; set; } = Array.Empty<PluginProgramGuideChannel>();
    public IReadOnlyList<PluginProgramGuideNowNext> NowNext { get; set; } = Array.Empty<PluginProgramGuideNowNext>();
}

public sealed class PluginProgramGuideChannel
{
    public int ProgramGuideOrder { get; set; }
    public string DisplayGroup { get; set; } = string.Empty; // GR / BS / CS
    /// <summary>v0.10.75: 番組表放送波フィルタボタンの分類をそのまま投影する公式field。</summary>
    public string ProgramGuideFilterGroup { get; set; } = string.Empty; // GR / BS / CS
    public string ProgramGuideFilterKey { get; set; } = string.Empty;
    public string ProgramGuideFilterLabel { get; set; } = string.Empty;
    /// <summary>UI分類用の互換alias。ProgramGuideFilterGroupと同値。</summary>
    public string BroadcastGroup { get; set; } = string.Empty;
    public string AllocationGroup { get; set; } = string.Empty; // GR / BSCS
    /// <summary>チューナー割当用の互換alias。AllocationGroupと同値。</summary>
    public string TunerGroup { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    /// <summary>v0.10.88: NetworkId互換alias。公式fieldはNetworkId。</summary>
    public ushort Nid { get; set; }
    /// <summary>v0.10.88: TransportStreamId互換alias。公式fieldはTransportStreamId。</summary>
    public ushort Tsid { get; set; }
    /// <summary>v0.10.88: ServiceId互換alias。公式fieldはServiceId。</summary>
    public ushort Sid { get; set; }
    public int ChannelSpace { get; set; }
    public int ChannelIndex { get; set; }
    public string ChannelArgument { get; set; } = string.Empty;
    public bool IsProgramGuideVisible { get; set; } = true;
    public bool IsEnabledInUserChannelSet { get; set; } = true;
}

public sealed class PluginProgramGuideNowNext
{
    public PluginProgramGuideChannel Channel { get; set; } = new();
    public PluginEpgEvent? Current { get; set; }
    public PluginEpgEvent? Next { get; set; }
    public string Availability { get; set; } = string.Empty;
    public DateTime SnapshotAt { get; set; }
    public long Revision { get; set; }
}

public sealed class PluginViewerSessionInfo
{
    public string LeaseId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string DisplayGroup { get; set; } = string.Empty;
    public string ProgramGuideFilterGroup { get; set; } = string.Empty;
    public string AllocationGroup { get; set; } = string.Empty;
    public string TunerGroup { get; set; } = string.Empty;
    public string TunerName { get; set; } = string.Empty;
    public string BonDriverFileName { get; set; } = string.Empty;
    public string Did { get; set; } = string.Empty;
    public bool Current { get; set; } = true;
    public string ViewerState { get; set; } = "active";
    public string ServiceName { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public DateTime AcquiredAt { get; set; }
    public int? ProcessId { get; set; }
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
    public int? ChannelSpace { get; set; }
    public int? ChannelIndex { get; set; }
    public string State { get; set; } = "leaseOnly";
    public string LaunchResult { get; set; } = "notStarted";
    public string TuneResult { get; set; } = "notStarted";
    public string ActivateResult { get; set; } = "notStarted";
    public string RollbackResult { get; set; } = "notNeeded";
    public string ChannelArgument { get; set; } = string.Empty;
    public string ViewerProfile { get; set; } = "auto";
    public string ViewerProfileName { get; set; } = "自動";
    public string TvTestPathKey { get; set; } = string.Empty;
}


/// <summary>
/// v0.10.88: ViewerControl局行用の公式identity投影。
/// AIrCon等は viewerStart safe event payload をこの投影から生成する。
/// </summary>
public sealed class PluginViewerControlChannelInfo
{
    public int ProgramGuideOrder { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort Nid { get; set; }
    public ushort Tsid { get; set; }
    public ushort Sid { get; set; }
    public string ProgramGuideFilterGroup { get; set; } = string.Empty;
    public string ProgramGuideFilterKey { get; set; } = string.Empty;
    public string ProgramGuideFilterLabel { get; set; } = string.Empty;
    public string BroadcastGroup { get; set; } = string.Empty;
    public string AllocationGroup { get; set; } = string.Empty;
    public string TunerGroup { get; set; } = string.Empty;
    public int ChannelSpace { get; set; }
    public int ChannelIndex { get; set; }
    public string ChannelArgument { get; set; } = string.Empty;
    public string IdentitySource { get; set; } = "ProgramGuideProjection";
    public string ViewerStartPayloadNetworkIdAttribute { get; set; } = "data-tvair-payload-networkId";
    public string ViewerStartPayloadTransportStreamIdAttribute { get; set; } = "data-tvair-payload-transportStreamId";
    public string ViewerStartPayloadServiceIdAttribute { get; set; } = "data-tvair-payload-serviceId";
    public bool ViewerStartIdentityReady { get; set; }
}

public sealed class PluginViewerTunerQuery
{
    public string? ProgramGuideFilterGroup { get; set; }
    public string? AllocationGroup { get; set; }
    public bool IncludeBusy { get; set; } = true;
    public bool IncludeRecordingRole { get; set; } = false;
}

public sealed class PluginViewerTunerInfo
{
    public string Name { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public string Did { get; set; } = string.Empty;
    public string ProgramGuideFilterGroup { get; set; } = string.Empty;
    public string DisplayGroup { get; set; } = string.Empty;
    public string AllocationGroup { get; set; } = string.Empty;
    public string TunerGroup { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string UsageKind { get; set; } = string.Empty;
    public bool Busy { get; set; }
    public bool IsViewingRole { get; set; }
    public bool IsSelectableForViewer { get; set; }
    public string BonDriverFileName { get; set; } = string.Empty;
    public string CurrentLeaseId { get; set; } = string.Empty;

    // v0.10.88: Viewer availability projection for expected-denied handling.
    // External TVTest occupation is surfaced as data, not as a plugin action error.
    public string Availability { get; set; } = string.Empty;
    public string OccupiedBy { get; set; } = string.Empty;
    public int? ExternalPid { get; set; }
    public string ExternalBusyReason { get; set; } = string.Empty;
    public string AvailabilityMessage { get; set; } = string.Empty;

    // v0.10.88: Last viewerStart result for tool-window state rendering.
    public string LastViewerActionState { get; set; } = string.Empty;
    public string LastViewerActionErrorCode { get; set; } = string.Empty;
    public string LastViewerActionMessage { get; set; } = string.Empty;

    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
}

public sealed class PluginProgramGuideWaveFilterInfo
{
    public string Key { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool IsProgramGuideFilter { get; set; } = true;
}

public sealed class PluginViewerControlHostContract
{
    public bool Success { get; set; } = true;
    public string ContractVersion { get; set; } = string.Empty;
    public bool ToolWindowOnlySafeEvents { get; set; }
    public bool PluginScriptAllowed { get; set; }
    public string SafeEventAttributePrefix { get; set; } = "data-tvair-";
    public IReadOnlyList<string> SupportedEvents { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedActions { get; set; } = Array.Empty<string>();
    public string ViewerStartPayloadFields { get; set; } = string.Empty;
    public string ViewerStartPreferredTunerFields { get; set; } = string.Empty;
    public string ProgramGuideFilterSource { get; set; } = string.Empty;
    public string ProgramGuideFilterField { get; set; } = string.Empty;
    public string TunerGroupField { get; set; } = string.Empty;
    public string ViewerTunersEndpoint { get; set; } = string.Empty;
    public string ViewerControlChannelsEndpoint { get; set; } = string.Empty;
    public string ViewerControlIdentitySource { get; set; } = string.Empty;
    public string ViewerControlIdentityFields { get; set; } = string.Empty;
    public string WaveFiltersEndpoint { get; set; } = string.Empty;
    public string AlwaysOnTopAction { get; set; } = string.Empty;
    public string RefreshReloadScopeDirectContent { get; set; } = string.Empty;
    public bool PreferredOpenModeToolWindowSupported { get; set; }
    public bool ToolWindowContentOnly { get; set; }
    public string ViewerActionRefreshAfter { get; set; } = string.Empty;
    public string ViewerStartWindowStateContract { get; set; } = string.Empty;
    public string ViewerStartRetuneExistingContract { get; set; } = string.Empty;
    public string ViewerSessionCurrentSource { get; set; } = string.Empty;
    public string ViewerProfilesEndpoint { get; set; } = string.Empty;
    public string ViewerProfilePayloadField { get; set; } = string.Empty;
    public string ViewerProfileReuseContract { get; set; } = string.Empty;
}

public sealed class PluginCurrentProgramQuery
{
    public string? Group { get; set; }
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
    public int WindowMinutesBefore { get; set; } = 1;
    public int WindowMinutesAfter { get; set; } = 1;
    public int Limit { get; set; } = 200;
}

public sealed class PluginRuleQuery
{
    public bool? Enabled { get; set; }
    public string? Keyword { get; set; }
    public int Limit { get; set; } = 500;
}

public sealed class PluginKeywordRuleInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string ExcludePattern { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool Enabled { get; set; }
    public bool UseAllChannels { get; set; }
    public string TargetServices { get; set; } = string.Empty;
    public string TargetGenres { get; set; } = string.Empty;
    public string TargetDays { get; set; } = string.Empty;
    public bool UseTimeRange { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PluginProgramRuleInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DayOfWeek { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string ExpiresOn { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PluginLogQuery
{
    public int Count { get; set; } = 200;
    public string? Event { get; set; }
    public string? Keyword { get; set; }
}

public sealed class PluginLogItem
{
    public string Event { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class PluginEpgRunStateInfo
{
    public bool IsRunning { get; set; }
    public bool CanStart { get; set; }
    public bool CanCancel { get; set; }
    public string Source { get; set; } = string.Empty;
    public string TargetScope { get; set; } = string.Empty;
    public bool Silent { get; set; }
    public string UiMode { get; set; } = string.Empty;
    public string CancelRoute { get; set; } = string.Empty;
}

public sealed class PluginSystemStatusInfo
{
    public string Version { get; set; } = string.Empty;
    public DateTime Now { get; set; }
    public int ReservationCount { get; set; }
    public int ActiveRecordingCount { get; set; }
    public int TunerCount { get; set; }
    public int FreeTunerCount { get; set; }
    public int WakePlanCount { get; set; }
    public DateTime? NextWakeAt { get; set; }
}

public sealed class PluginThemeInfo
{
    public string Appearance { get; set; } = "system";
    public string AccentColor { get; set; } = string.Empty;
    public string CssScopeRoot { get; set; } = "tvair";
}

/// <summary>読み取り専用の分析系プラグイン契約。</summary>
public interface IAnalysisPlugin : ITvAIrPlugin
{
    AnalysisResult Analyze(AnalysisContext context);
}

/// <summary>AIrConなど、将来の視聴系プラグインを想定した契約。</summary>
public interface IViewerPlugin : ITvAIrPlugin
{
    ViewerPluginCapabilities Capabilities { get; }
}

public sealed class ViewerPluginCapabilities
{
    public bool SupportsExternalProcess { get; set; }
    public bool SupportsLiveView { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class AnalysisContext
{
    public bool IsClosedNetwork { get; set; } = true;
    public string UserNickname { get; set; } = string.Empty;
    public string AssistantNickname { get; set; } = string.Empty;
    public IReadOnlyList<AnalysisReservationInfo> Reservations { get; set; } = Array.Empty<AnalysisReservationInfo>();
    public IReadOnlyList<AnalysisProgramInfo> Programs { get; set; } = Array.Empty<AnalysisProgramInfo>();
}

public sealed class AnalysisReservationInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsConflicted { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public sealed class AnalysisProgramInfo
{
    public string Title { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public sealed class AnalysisResult
{
    public string PluginName { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Summary { get; set; } = string.Empty;
    public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
    public IReadOnlyList<AnalysisMetric> Metrics { get; set; } = Array.Empty<AnalysisMetric>();
}

public sealed class AnalysisMetric
{
    public string Label { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
}

/// <summary>独立ページ型UIプラグイン契約。</summary>
public interface IUiPlugin : ITvAIrPlugin
{
    PluginUiDescriptor Ui { get; }
    string RenderHtml(PluginUiContext context);
}

public sealed class PluginUiDescriptor
{
    public string RouteSegment { get; set; } = string.Empty;
    public string MenuText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>v0.11.315: UI単位の任意capability。manifest側Capabilitiesが公式で、ここはUI投影補助。</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
    public string Icon { get; set; } = string.Empty;
    public int DisplayOrder { get; set; } = 1000;
    public bool Enabled { get; set; } = true;
    /// <summary>v0.10.75: plugin一覧カードの既定起動モード。空なら通常ページ。</summary>
    public string PreferredOpenMode { get; set; } = string.Empty;

    /// <summary>v0.11.30: ハンバーガー/タスクトレイ共通プラグインメニューで実行する既定アクション。toolWindow / page / settings / versionDialog / statusDialog / none。</summary>
    public string DefaultMenuActionKind { get; set; } = string.Empty;
    public string DefaultMenuActionLabel { get; set; } = string.Empty;
    public int DefaultMenuActionPriority { get; set; } = 1000;
    public bool ToolWindowShowInTaskbar { get; set; } = false;

    /// <summary>v0.11.63: host-managed tool window の推奨幅。ハンバーガー/トレイ/openWindow共通入口で参照する。</summary>
    public int ToolWindowWidth { get; set; } = 0;
    /// <summary>v0.11.63: host-managed tool window の推奨高さ。ハンバーガー/トレイ/openWindow共通入口で参照する。</summary>
    public int ToolWindowHeight { get; set; } = 0;
    /// <summary>v0.11.63: host-managed tool window の最小幅。プラグイン名に依存せず共通入口で参照する。</summary>
    public int ToolWindowMinWidth { get; set; } = 0;
    /// <summary>v0.11.63: host-managed tool window の最小高さ。プラグイン名に依存せず共通入口で参照する。</summary>
    public int ToolWindowMinHeight { get; set; } = 0;
}


public sealed class PluginViewerProfileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public int Order { get; set; }
    public bool IsAuto { get; set; }
    public string TvTestPathKey { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    /// <summary>v0.11.69: 物理チューナー番号ではなく、放送波を跨いだTVTest枠番号。0は後方互換入力のみ。</summary>
    public int TvTestFrameIndex { get; set; }

    /// <summary>v0.11.72: このTVTest枠で選局可能な放送波。例: GR,BSCS。UIは本体projectionに従う。</summary>
    public string AvailableGroups { get; set; } = string.Empty;
}

public sealed class PluginUiContext
{
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    public bool IsClosedNetwork { get; set; } = true;

    /// <summary>UIプラグインがボタン/ダブルクリック等を本体へ返すための正式Action endpoint。互換名。</summary>
    public string ActionEndpoint { get; set; } = string.Empty;

    /// <summary>UIプラグインがフォーム/FetchでPOSTする本体Action route。v0.10.32の正式名。</summary>
    public string ActionRoute { get; set; } = string.Empty;

    /// <summary>v0.10.32互換名。AIrCon等が明示的にPlugin action routeとして検出するための別名。</summary>
    public string PluginActionRoute { get; set; } = string.Empty;

    /// <summary>v0.10.32互換名。ActionEndpointと同値のPlugin action endpoint別名。</summary>
    public string PluginActionEndpoint { get; set; } = string.Empty;

    /// <summary>Action routeへ送信するHTTP method。標準はPOST。</summary>
    public string ActionMethod { get; set; } = "POST";

    /// <summary>v0.10.32互換名。ActionMethodと同値のPlugin action method別名。</summary>
    public string PluginActionMethod { get; set; } = "POST";

    /// <summary>このUIで本体が受け付ける代表Action名。プラグイン側の出し分け用。</summary>
    public IReadOnlyList<string> SupportedActions { get; set; } = Array.Empty<string>();

    /// <summary>v0.10.32互換名。SupportedActionsと同値のPlugin action一覧別名。</summary>
    public IReadOnlyList<string> PluginSupportedActions { get; set; } = Array.Empty<string>();

    /// <summary>RenderHtmlごとに発行される短寿命トークン。プラグインHTMLからAction endpointへ同送する。</summary>
    public string ActionToken { get; set; } = string.Empty;

    /// <summary>v0.10.32互換名。ActionTokenと同値のPlugin action token別名。</summary>
    public string PluginActionToken { get; set; } = string.Empty;

    /// <summary>本体が認識しているPlugin ID。manifest Idがあればそれを優先し、なければUI routeを使う。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>現在表示中のプラグインroute。ログ・Action検証用。</summary>
    public string RouteSegment { get; set; } = string.Empty;

    /// <summary>v0.10.32: HTML/JS側が名称揺れなく参照できるAction contract値。</summary>
    public IReadOnlyDictionary<string, string> ActionContract { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.10.35: UIプラグインが本体管理の独立/フロートWindowを要求するための正式route。</summary>
    public string WindowRoute { get; set; } = string.Empty;

    /// <summary>v0.10.41: WindowRoute互換名。本体管理Window要求のendpoint。</summary>
    public string WindowEndpoint { get; set; } = string.Empty;

    /// <summary>v0.10.41: host-managed window state polling endpoint template。{windowId}を実WindowIdへ置換する。</summary>
    public string WindowStateEndpointTemplate { get; set; } = string.Empty;

    /// <summary>v0.10.41: host-managed window iframe内RenderHtmlで設定される現在WindowId。</summary>
    public string CurrentWindowId { get; set; } = string.Empty;

    /// <summary>v0.10.41: CurrentWindowId互換名。HTML/JS側で名称揺れなく参照するための別名。</summary>
    public string WindowId { get; set; } = string.Empty;

    /// <summary>v0.10.41: host-managed window iframe内RenderHtmlでtrue。通常/plugin表示ではfalse。</summary>
    public bool IsHostManagedWindowContent { get; set; }

    /// <summary>v0.10.41: 現在Windowのstate endpoint。CurrentWindowIdがある場合のみ設定される。</summary>
    public string CurrentWindowStateEndpoint { get; set; } = string.Empty;

    /// <summary>v0.11.11: C# RenderHtml から直接読める現在Window stateの絶対URL。</summary>
    public string CurrentWindowStateUrl { get; set; } = string.Empty;

    /// <summary>v0.10.41: 現在Windowのhost URL。CurrentWindowIdがある場合のみ設定される。</summary>
    public string CurrentWindowUrl { get; set; } = string.Empty;

    /// <summary>v0.11.11: C# RenderHtml から参照できる現在Window host URLの絶対URL。</summary>
    public string CurrentWindowAbsoluteUrl { get; set; } = string.Empty;

    /// <summary>v0.11.13: RenderHtml中にHTTP自己呼び出しせず参照できる現在WindowのalwaysOnTop状態。</summary>
    public bool CurrentWindowAlwaysOnTop { get; set; }

    /// <summary>v0.11.13: RenderHtml中にHTTP自己呼び出しせず参照できる現在Windowのrevision。</summary>
    public int CurrentWindowRevision { get; set; }

    /// <summary>v0.11.13: RenderHtml中にHTTP自己呼び出しせず参照できる現在Windowのhost alive状態。</summary>
    public bool CurrentWindowHostAlive { get; set; }

    /// <summary>v0.10.35: Window要求HTTP method。標準はPOST。</summary>
    public string WindowMethod { get; set; } = "POST";

    /// <summary>v0.10.35: Window要求に同送する短寿命token。ActionTokenと同一レンダリング単位で発行される。</summary>
    public string WindowToken { get; set; } = string.Empty;

    /// <summary>v0.10.35: 本体が受け付けるWindow action一覧。</summary>
    public IReadOnlyList<string> SupportedWindowActions { get; set; } = Array.Empty<string>();

    /// <summary>v0.10.35: HTML/JS側が名称揺れなく参照できるWindow contract値。</summary>
    public IReadOnlyDictionary<string, string> WindowContract { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.10.68: TvAIr本体管理Plugin Tool Window hostの能力。WebView2有無、fallback、reuse/state/close同期対応をプラグインが判断するための契約。</summary>
    public IReadOnlyDictionary<string, string> ToolWindowCapabilities { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.10.75: ViewerControl mode向け。no-script制約下で本体が安全に扱う行イベント/視聴操作契約。</summary>
    public IReadOnlyDictionary<string, string> ViewerControlActionContract { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);


    /// <summary>v0.11.69: TvAIr本体がViewing role構成からTVTest枠単位で動的生成したviewer profile一覧。/api/plugins/viewer-profilesと同一projection。</summary>
    public IReadOnlyList<PluginViewerProfileInfo> ViewerProfiles { get; set; } = Array.Empty<PluginViewerProfileInfo>();

    /// <summary>v0.11.69: UIで選択可能なviewer profile一覧。autoを含み、selector可視判定と同一projection。</summary>
    public IReadOnlyList<PluginViewerProfileInfo> SelectableViewerProfiles { get; set; } = Array.Empty<PluginViewerProfileInfo>();

    /// <summary>v0.11.69: 既定viewer profile。通常はauto。</summary>
    public string DefaultViewerProfile { get; set; } = "auto";

    /// <summary>v0.11.69: auto以外の有効TVTest枠profile数。</summary>
    public int EnabledRealViewerProfileCount { get; set; }

    /// <summary>v0.11.69: viewer profile selectorを表示すべきかの本体推奨。</summary>
    public bool SelectorVisibleRecommended { get; set; }

    /// <summary>v0.11.69: SelectorVisibleRecommendedの明示alias。</summary>
    public bool ViewerProfileSelectorVisibleRecommended { get; set; }

    /// <summary>v0.11.69: selector非表示でも最大構成基準の最小幅を維持すべきか。</summary>
    public bool MinWidthInvariantRequired { get; set; } = true;

    /// <summary>v0.10.97: RenderHtmlを呼び出した現在のHTTP request path。通常ページ/host-managed tool window directContent共通。</summary>
    public string CurrentRequestPath { get; set; } = string.Empty;

    /// <summary>v0.10.97: RenderHtmlを呼び出した現在のHTTP query string。先頭の?を含む。queryなしは空文字。</summary>
    public string CurrentRequestQueryString { get; set; } = string.Empty;

    /// <summary>v0.10.97: CurrentRequestPath + CurrentRequestQueryString。</summary>
    public string CurrentRequestPathAndQuery { get; set; } = string.Empty;

    /// <summary>v0.10.97: RenderHtml呼び出し時のquery key/value。wave等のUIフィルタ契約用。</summary>
    public IReadOnlyDictionary<string, string> CurrentRequestQuery { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);


    /// <summary>v0.10.99: RenderHtml内で同梱小型画像等を参照するための同一オリジンasset base URL。例: /plugin-assets/aircon。</summary>
    public string PluginAssetBaseUrl { get; set; } = string.Empty;

    /// <summary>v0.10.99: asset配信の詳細契約。許可拡張子、URL形式、sanitizer制約など。</summary>
    public IReadOnlyDictionary<string, string> PluginAssetContract { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.10.99: plugin asset URLを安全に組み立てる。assetNameはファイル名のみを想定し、TvAIr本体側のasset endpointへ解決する。</summary>
    public string ResolveAssetUrl(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(PluginAssetBaseUrl)) return string.Empty;
        var name = assetName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") return string.Empty;
        return PluginAssetBaseUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(name);
    }
}

/// <summary>
/// v0.10.32: UIプラグインからTvAIr本体へ安全な操作要求を返すためのAction契約。
/// プラグインはTVTest起動・DID決定・BonDriver決定を行わず、希望する操作と局IDだけを渡す。
/// </summary>
public sealed class PluginActionRequest
{
    public string PluginId { get; set; } = string.Empty;
    public string RouteSegment { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ActionToken { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;

    /// <summary>v0.10.42: form POSTでJSON画面へ遷移しないための応答モード。json / refreshWindow / html / noContent。</summary>
    public string ResponseMode { get; set; } = "json";

    /// <summary>v0.10.42: responseMode=refreshWindow時に再描画するhost-managed window。</summary>
    public string WindowId { get; set; } = string.Empty;

    /// <summary>v0.10.42: responseMode=refreshWindow時の更新対象。標準はcontent。</summary>
    public string RefreshTarget { get; set; } = "content";

    /// <summary>v0.10.42: responseMode=refreshWindow時にスクロール位置維持を要求する。</summary>
    public bool PreserveScroll { get; set; } = true;

    /// <summary>v0.11.27: host-managed tool window再描画後に可視化したい要素ID。CSS selectorではなくidのみ。</summary>
    public string RefreshScrollTarget { get; set; } = string.Empty;

    /// <summary>v0.11.27: refreshScrollTargetのスクロール方法。center / nearest / top。</summary>
    public string RefreshScrollMode { get; set; } = "center";
}

public sealed class PluginActionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Diagnostics { get; set; } = string.Empty;
}

/// <summary>
/// 将来、プラグイン自身が本体からのActionを受ける場合の拡張点。
/// v0.10.32では本体host dispatchを主系とし、このinterfaceはSDK上の契約名公開に留める。
/// </summary>
public interface IUiActionPlugin
{
    Task<PluginActionResult> HandleActionAsync(PluginActionRequest request);
}


/// <summary>
/// v0.10.35: UIプラグインがTvAIr本体管理の独立/フロートWindowを要求するためのWindow契約。
/// プラグインは外部プロセスや独自WebViewを直接起動せず、本体へWindow意図だけを渡す。
/// </summary>
public sealed class PluginWindowRequest
{
    public string PluginId { get; set; } = string.Empty;
    public string RouteSegment { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string WindowId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Width { get; set; } = 420;
    public int Height { get; set; } = 640;
    public int MinWidth { get; set; } = 320;
    public int MinHeight { get; set; } = 360;
    public bool Resizable { get; set; } = true;
    public bool Movable { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = false;
    public string ContentRoute { get; set; } = string.Empty;
    public string RefreshTarget { get; set; } = "content";
    public string Target { get; set; } = "content";
    public bool PreserveScroll { get; set; } = true;

    /// <summary>v0.11.27: refreshAfter/refreshWindow後にhost側で可視化したい要素ID。CSS selectorではなくidのみ。</summary>
    public string RefreshScrollTarget { get; set; } = string.Empty;

    /// <summary>v0.11.27: refreshScrollTargetのスクロール方法。center / nearest / top。</summary>
    public string RefreshScrollMode { get; set; } = "center";

    public bool ForceReload { get; set; } = true;

    /// <summary>v0.11.15: updateWindow成功後に同一host-managed windowのcontent再描画を本体へ要求する限定契約。</summary>
    public bool RefreshAfter { get; set; } = false;

    /// <summary>v0.10.60: 同一 pluginId + routeSegment の既存ツールウィンドウを再利用する。</summary>
    public bool ReuseExisting { get; set; } = false;

    /// <summary>v0.10.60: 既存ツールウィンドウ再利用時に前面化する。</summary>
    public bool ActivateExisting { get; set; } = false;

    /// <summary>v0.10.68: form POSTでJSON/白紙画面へ遷移しないための応答モード。json / redirect / redirectBack / hostHandled / toolWindow / toolWindowRedirectBack / hostWindow / html / noContent。</summary>
    public string ResponseMode { get; set; } = "json";

    /// <summary>v0.10.68: responseMode=toolWindow後にPOST元画面へ戻すための相対URL。未指定時はRefererまたは /plugin/{routeSegment}。</summary>
    public string ReturnUrl { get; set; } = string.Empty;

    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string WindowToken { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public sealed class PluginWindowResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Diagnostics { get; set; } = string.Empty;
    public string WindowId { get; set; } = string.Empty;
    public string WindowUrl { get; set; } = string.Empty;
    public string ContentRoute { get; set; } = string.Empty;
    public bool RefreshRequested { get; set; }
    public string RefreshTarget { get; set; } = string.Empty;
    public bool PreserveScroll { get; set; }
    public string RefreshScrollTarget { get; set; } = string.Empty;
    public string RefreshScrollMode { get; set; } = "center";
    public int Revision { get; set; }
}

/// <summary>v0.10.68: /api/plugins/window/capabilities の応答契約。プラグインはtoolWindowを使うかredirect互換に落とすか判断できる。</summary>
public sealed class PluginToolWindowHostCapabilities
{
    public bool Success { get; set; }
    public string ContractVersion { get; set; } = string.Empty;
    public bool ToolWindowSupported { get; set; }
    public bool HostWindowSupported { get; set; }
    public bool WebView2RuntimeAvailable { get; set; }
    public string HostKind { get; set; } = string.Empty;
    public string FallbackHostKind { get; set; } = string.Empty;
    public bool FallbackToBrowserRedirectSupported { get; set; }
    public bool JsonScreenSuppressed { get; set; }
    public bool SupportsAlwaysOnTop { get; set; }
    public bool SupportsSize { get; set; }
    public bool SupportsMinSize { get; set; }
    public bool SupportsPositionPersistence { get; set; }
    public bool SupportsStatePersistence { get; set; }
    public bool SupportsReuseExisting { get; set; }
    public bool SupportsActivateExisting { get; set; }
    public string ReuseKey { get; set; } = string.Empty;
    public string RefreshTarget { get; set; } = "content";
    public string RefreshReloadScope { get; set; } = "iframe-content-only";
    public bool SupportsRefreshScrollTarget { get; set; }
    public string RefreshScrollModes { get; set; } = "center|nearest|top";
    public bool ScriptExecutionAllowed { get; set; }
    public IReadOnlyList<string> OpenWindowModes { get; set; } = Array.Empty<string>();
}

/// <summary>v0.10.68: /plugin-window/{windowId}/state の拡張応答契約。</summary>
public sealed class PluginWindowStateInfo
{
    public bool Success { get; set; }
    public string WindowId { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string RouteSegment { get; set; } = string.Empty;
    public string ContentRoute { get; set; } = string.Empty;
    public int Revision { get; set; }
    public bool RefreshRequested { get; set; }
    public string RefreshTarget { get; set; } = "content";
    public string ReloadScope { get; set; } = "iframe-content-only";
    public bool PreserveScroll { get; set; }
    public string RefreshScrollTarget { get; set; } = string.Empty;
    public string RefreshScrollMode { get; set; } = "center";
    public string Title { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int MinWidth { get; set; }
    public int MinHeight { get; set; }
    public bool Resizable { get; set; }
    public bool Movable { get; set; }
    public bool AlwaysOnTop { get; set; }
    public string ReuseKey { get; set; } = "pluginId+routeSegment";
    public int? Left { get; set; }
    public int? Top { get; set; }
    public bool JsonScreenSuppressed { get; set; }
    public string CloseSync { get; set; } = string.Empty;
    public bool IsClosed { get; set; }
    public bool HostAlive { get; set; }
    public string HostKind { get; set; } = string.Empty;
    public bool WebView2RuntimeAvailable { get; set; }
    public bool ToolWindowSupported { get; set; }
    public bool PositionPersistenceSupported { get; set; }
    public bool StatePersistenceSupported { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 将来、プラグイン自身が本体Window要求を処理する場合の拡張点。
/// v0.10.35では本体host-managed window contractを主系とし、このinterfaceはSDK上の契約名公開に留める。
/// </summary>
public interface IPluginWindowPlugin
{
    Task<PluginWindowResult> HandleWindowAsync(PluginWindowRequest request);
}


public sealed class PluginEpgQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Days { get; set; } = 7;
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
    public string? Keyword { get; set; }
    public string? Genre { get; set; }
    public int Limit { get; set; } = 2000;
}

public sealed class PluginEpgEvent
{
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string ProgramId => $"{NetworkId}:{TransportStreamId}:{ServiceId}:{EventId}";
    public string ServiceName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ExtendedDescription { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string GenreCodes { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int DurationSeconds { get; set; }
}

public sealed class PluginReservationQuery
{
    public bool IncludeEpgSystemEntries { get; set; } = false;
    public bool? IsEnabled { get; set; }
    public bool? IsConflicted { get; set; }
    public string? Source { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public sealed class PluginReservationHistoryQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Limit { get; set; } = 500;
}

public sealed class PluginReservation
{
    public int Id { get; set; }
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string ProgramId => $"{NetworkId}:{TransportStreamId}:{ServiceId}:{EventId}";
    public string Title { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsConflicted { get; set; }
    public string TunerName { get; set; } = string.Empty;
    public string ActualTunerName { get; set; } = string.Empty;
    public bool IsUserChain { get; set; }
    public int? UserChainPreviousId { get; set; }
    public int? UserChainRootId { get; set; }
    public string CreatedByPlugin { get; set; } = string.Empty;
}

public sealed class PluginTunerStatus
{
    public string Name { get; set; } = string.Empty;
    public string BonDriverFileName { get; set; } = string.Empty;
    public string Did { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public string UsageKind { get; set; } = string.Empty;
    public int? ReservationId { get; set; }
    public int? ProcessId { get; set; }
    public DateTime? PlannedEndTime { get; set; }
}

public sealed class PluginConflictInfo
{
    public int ReservationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class PluginReservationDraft
{
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string ChannelArgument { get; set; } = string.Empty;
    public int Priority { get; set; } = 0;
    public bool AllowChain { get; set; } = false;
    public int PreMarginMinutes { get; set; } = 0;
    public int PostMarginMinutes { get; set; } = 0;
    public int? ChainPreviousReservationId { get; set; }
}

public sealed class PluginReservationUpdate
{
    public int ReservationId { get; set; }
    public bool? IsEnabled { get; set; }
    public int? Priority { get; set; }
    public bool? AllowChain { get; set; }
    public int? PreMarginMinutes { get; set; }
    public int? PostMarginMinutes { get; set; }
}

public sealed class PluginReservationPreview
{
    public bool CanReserve { get; set; }
    public bool HasConflict { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string SuggestedTunerName { get; set; } = string.Empty;
    public IReadOnlyList<PluginConflictInfo> Conflicts { get; set; } = Array.Empty<PluginConflictInfo>();
    public IReadOnlyList<PluginChainInfo> ChainCandidates { get; set; } = Array.Empty<PluginChainInfo>();
}

public sealed class PluginReservationOperationResult
{
    public bool Success { get; set; }
    public int? ReservationId { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class PluginChainQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public ushort? ServiceId { get; set; }
}

public sealed class PluginChainInfo
{
    public int? PreviousReservationId { get; set; }
    public int? CurrentReservationId { get; set; }
    public string CurrentProgramId { get; set; } = string.Empty;
    public bool SameTuner { get; set; }
    public string LossTarget { get; set; } = "previous";
    public string LossPart { get; set; } = "end";
    public string LossDescription { get; set; } = "前番組後半がカットされます";
    public bool IsAllowed { get; set; }
}

public sealed class PluginChainPreview
{
    public bool CanChain { get; set; }
    public string Message { get; set; } = string.Empty;
    public PluginChainInfo? ChainInfo { get; set; }
}
