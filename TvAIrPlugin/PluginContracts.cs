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
    // release_contract: 既存値の順序は互換性維持のため変更しない。新権限は末尾に追加する。
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

    // release_contract 読み取りAPI拡張。既存値を壊さず末尾に追加する。
    ReadSystemStatus,
    ReadEpgStatus,
    ReadKeywordRules,
    ReadProgramRules,
    ReadRecordingQuality,

    // release_contract 番組表投影API。既存値を壊さず末尾に追加する。
    ReadProgramGuideProjection,
    ReadViewerSessions,

    // Runtime Viewer read permissions. The following reserved slot preserves the binary enum ordinal
    // used by already-compiled plugins; it grants no capability and has no API surface.
    ReadViewerTuners,
    ReservedViewerControlContractSlot,

    // release_contract リリース前API面の明文化。TVTestヘッダ由来の概念をTvAIr抽象契約として読むための権限。
    ReadHostContracts,

    // release_contract: 汎用Plugin Host Contract。新規プラグインはkindだけでなくcapability/permissionで明示する。
    OpenPage,
    OpenToolWindow,
    UseActionApi,
    UseWindowApi,
    UseAssetApi,
    UseSafeEvent,
    UseRemoteAccess,
    UsePairing,
    UseLocalNetwork,

    // release_contract: 外部EPGを番組表投影へ登録する。epg_eventsへの書き戻し権限ではない。
    WriteProgramGuideProjection,

    // release_contract: ログ表示/録画品質表示をプラグインが本体へ提供する。
    WriteLogPresentation,
    WriteRecordingQualityPresentation,

    // Runtime VideoOverlay Host. Read permits scene inspection; write permits scene/layer mutation.
    ReadVideoOverlay,
    WriteVideoOverlay,

    // Generic plugin-originated host log write. Appended to preserve existing enum ordinals.
    WriteLogs,
    ReadPlaybackProgress,
    WritePlaybackProgress,
    ReadMediaInsights,
    ReadContentDiscovery,

    // Runtime UI共通のHost-owned File / Folder Picker。既存enum ordinalを維持するため末尾追加。
    UsePathPicker
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
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    /// <summary>Mutable current display label. Use NetworkId + TransportStreamId + ServiceId for identity.</summary>
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

public sealed class PluginProgramGuideQuery
{
    public string? DisplayGroup { get; set; }
    /// <summary>release_contract: 番組表の放送波フィルタ分類。DisplayGroup互換だが、番組表ボタンの公式投影名。</summary>
    public string? ProgramGuideFilterGroup { get; set; }
    public string? AllocationGroup { get; set; }
    public bool IncludeNowNext { get; set; } = true;
    public int Limit { get; set; } = 500;
}

public class PluginProgramGuideChannelQuery
{
    public string? DisplayGroup { get; set; }
    /// <summary>release_contract: 番組表の放送波フィルタ分類。DisplayGroup互換だが、番組表ボタンの公式投影名。</summary>
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
    /// <summary>release_contract: 番組表放送波フィルタボタンの分類をそのまま投影する公式field。</summary>
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
    /// <summary>release_contract: NetworkId互換alias。公式fieldはNetworkId。</summary>
    public ushort Nid { get; set; }
    /// <summary>release_contract: TransportStreamId互換alias。公式fieldはTransportStreamId。</summary>
    public ushort Tsid { get; set; }
    /// <summary>release_contract: ServiceId互換alias。公式fieldはServiceId。</summary>
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


public sealed class PluginProgramGuideWaveFilterInfo
{
    public string Key { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool IsProgramGuideFilter { get; set; } = true;
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
    /// <summary>Comma-separated exact service identities in NID:TSID:SID form. SID-only values are legacy compatibility data.</summary>
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
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Mutable current display label. Use NetworkId + TransportStreamId + ServiceId for identity.</summary>
    public string ServiceName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsConflicted { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public sealed class AnalysisProgramInfo
{
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Mutable current display label. Use NetworkId + TransportStreamId + ServiceId for identity.</summary>
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
public enum PluginActionFeedbackPhase
{
    Accepted,
    Running,
    Succeeded,
    Failed,
    NoChange,
    Cancelled
}

public enum PluginActionFeedbackKind
{
    Information,
    Success,
    Warning,
    Error
}

public enum PluginFloatingButtonPosition
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft
}

public enum PluginFloatingButtonVisibility
{
    Always,
    AfterScroll,
    ActionAvailable
}

/// <summary>
/// floating_button_contract: Page / ToolWindow共通のHost管理補助操作。
/// 任意HTML・任意JavaScript・内部優先順位は受け付けず、通常のSafeEvent / pluginOwnedAction経路へ接続する。
/// </summary>
public sealed class PluginFloatingButton
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
    public PluginFloatingButtonPosition Position { get; set; } = PluginFloatingButtonPosition.BottomRight;
    public PluginFloatingButtonVisibility Visibility { get; set; } = PluginFloatingButtonVisibility.Always;
    public int ScrollThresholdPixels { get; set; } = 240;
    public int Priority { get; set; }
    public bool ActionAvailable { get; set; } = true;
    public bool HideWhileRunning { get; set; }
    public IReadOnlyDictionary<string, string?> Payload { get; set; } = new Dictionary<string, string?>();
    public PluginActionFeedbackOptions? Feedback { get; set; }
    public string ResponseMode { get; set; } = "hostHandled";
}

public enum PluginFloatingLabelPosition
{
    ContentCenter,
    ViewportCenter,
    TopCenter,
    BottomCenter
}

/// <summary>floating_label_contract: Page / ToolWindow共通のHost管理短時間表示。</summary>
public sealed class PluginFloatingLabel
{
    public string CorrelationId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public PluginActionFeedbackKind Kind { get; set; } = PluginActionFeedbackKind.Information;
    public PluginFloatingLabelPosition Position { get; set; } = PluginFloatingLabelPosition.ContentCenter;
    /// <summary>0の場合はKind別のHost既定時間を使用する。</summary>
    public int DurationMilliseconds { get; set; }
}

/// <summary>action_feedback_contract: 描画時に押下元へ宣言するHost管理の操作状態。</summary>
public sealed class PluginActionFeedbackOptions
{
    public string PendingLabel { get; set; } = string.Empty;
    public string SuccessLabel { get; set; } = string.Empty;
    public string NoChangeLabel { get; set; } = string.Empty;
    public string FailureLabel { get; set; } = string.Empty;
    /// <summary>空でなければHost管理の確認ダイアログをAction開始前に表示する。</summary>
    public string ConfirmationMessage { get; set; } = string.Empty;
    public bool DisableWhileRunning { get; set; } = true;
    public bool KeepDisabledOnSuccess { get; set; }
    public bool RestoreOnFailure { get; set; } = true;
    public bool KeepUntilRefresh { get; set; }
}

/// <summary>action_feedback_contract: 実Action結果からHostが確定表示する構造化結果。</summary>
public sealed class PluginActionFeedback
{
    public string CorrelationId { get; set; } = string.Empty;
    public PluginActionFeedbackPhase Phase { get; set; } = PluginActionFeedbackPhase.Succeeded;
    public PluginActionFeedbackKind Kind { get; set; } = PluginActionFeedbackKind.Success;
    public string Message { get; set; } = string.Empty;
    public string TargetElementId { get; set; } = string.Empty;
    public string ButtonLabel { get; set; } = string.Empty;
    public bool? KeepDisabled { get; set; }
    public bool RefreshAfterFeedback { get; set; }
    /// <summary>既定true。Messageが空の場合は表示しない。</summary>
    public bool ShowFloatingLabel { get; set; } = true;
}

/// <summary>
/// release_contract: UIプラグインがTvAIr本体管理の独立/フロートWindowを要求するためのWindow契約。
/// プラグインは外部プロセスや独自WebViewを直接起動せず、本体へWindow意図だけを渡す。
/// </summary>
public sealed class PluginWindowRequest
{
    public string PluginId { get; set; } = string.Empty;
    public string RouteSegment { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string WindowId { get; set; } = string.Empty;
    public string WindowDefinitionId { get; set; } = string.Empty;
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
    public bool PreserveScroll { get; set; } = true;
    public TvAIrPlugin.Windows.PluginWindowScrollPolicy ScrollPolicy { get; set; } = TvAIrPlugin.Windows.PluginWindowScrollPolicy.Auto;
    public TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy HorizontalScrollPolicy { get; set; } = TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy.Auto;
    public TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy VerticalScrollPolicy { get; set; } = TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy.Auto;
    public TvAIrPlugin.Windows.PluginWindowSizeReference SizeReference { get; set; } = TvAIrPlugin.Windows.PluginWindowSizeReference.OuterWindow;
    public TvAIrPlugin.Windows.PluginWindowResizeMode ResizeMode { get; set; } = TvAIrPlugin.Windows.PluginWindowResizeMode.Both;
    public TvAIrPlugin.Windows.PluginWindowRefreshMode RefreshMode { get; set; } = TvAIrPlugin.Windows.PluginWindowRefreshMode.Navigate;
    public TvAIrPlugin.Windows.PluginWindowContentSizePolicy ContentSizePolicy { get; set; } = TvAIrPlugin.Windows.PluginWindowContentSizePolicy.Ignore;
    public bool PreserveInteractionState { get; set; } = true;
    public TvAIrPlugin.Windows.PluginWindowReusePolicy ReusePolicy { get; set; } = TvAIrPlugin.Windows.PluginWindowReusePolicy.PerRoute;
    public TvAIrPlugin.Windows.PluginWindowActivationPolicy ActivationPolicy { get; set; } = TvAIrPlugin.Windows.PluginWindowActivationPolicy.ManualOpenOnly;
    public TvAIrPlugin.Windows.PluginWindowCloseBehavior CloseBehavior { get; set; } = TvAIrPlugin.Windows.PluginWindowCloseBehavior.Dispose;
    public TvAIrPlugin.Windows.PluginWindowBackgroundExecution BackgroundExecution { get; set; } = TvAIrPlugin.Windows.PluginWindowBackgroundExecution.StopWithWindow;
    public TvAIrPlugin.Windows.PluginWindowStatePersistence StatePersistence { get; set; } = TvAIrPlugin.Windows.PluginWindowStatePersistence.Placement;

    public bool ForceReload { get; set; } = true;

    /// <summary>release_contract: updateWindow成功後に同一host-managed windowのcontent再描画を本体へ要求する限定契約。</summary>
    public bool RefreshAfter { get; set; } = false;

    /// <summary>release_contract: 同一 pluginId + routeSegment の既存ツールウィンドウを再利用する。</summary>
    public bool ReuseExisting { get; set; } = false;

    /// <summary>release_contract: 既存ツールウィンドウ再利用時に前面化する。</summary>
    public bool ActivateExisting { get; set; } = false;

    /// <summary>release_contract: form POSTでJSON/白紙画面へ遷移しないための応答モード。json / redirect / redirectBack / hostHandled / toolWindow / toolWindowRedirectBack / hostWindow / html / noContent。</summary>
    public string ResponseMode { get; set; } = "json";

    /// <summary>release_contract: responseMode=toolWindow後にPOST元画面へ戻すための相対URL。未指定時はRefererまたは /plugin/{routeSegment}。</summary>
    public string ReturnUrl { get; set; } = string.Empty;

    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string WindowToken { get; set; } = string.Empty;
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
    public int Revision { get; set; }
}

/// <summary>release_contract: /api/plugins/window/capabilities の応答契約。プラグインはtoolWindowを使うかredirect互換に落とすか判断できる。</summary>
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
    public bool ScriptExecutionAllowed { get; set; }
    public IReadOnlyList<string> OpenWindowModes { get; set; } = Array.Empty<string>();
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
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Mutable current display label. Use NetworkId + TransportStreamId + ServiceId for identity.</summary>
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
    /// <summary>Optional exact service identity filter. When one component is set, all three must be set.</summary>
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
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
