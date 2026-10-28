namespace TvAIrPlugin;

/// <summary>
/// TvAIr本体能力をプラグインへ公開する新しい汎用Capability APIの入口。
/// プラグイン名・用途名ではなく、本体能力単位で公開する。
/// </summary>
public interface ITvAirPluginContext
{
    ITvAirLogsApi Logs { get; }
    ITvAirLogPresentationApi LogPresentation { get; }
    ITvAirRecordingQualityPresentationApi RecordingQualityPresentation { get; }
    ITvAirReservationsApi Reservations { get; }
    ITvAirRulesApi Rules { get; }
    ITvAirRecordingsApi Recordings { get; }
    ITvAirRecordingFilesApi RecordingFiles { get; }
    ITvAirRecordingInspectionApi RecordingInspection { get; }
    ITvAirPlaybackProgressApi PlaybackProgress { get; }
    ITvAirMediaInsightsApi MediaInsights { get; }
    ITvAirContentDiscoveryApi ContentDiscovery { get; }
    ITvAirProgramGuideApi ProgramGuide { get; }
    ITvAirExternalProgramSourceApi ExternalProgramSource { get; }
    ITvAirProgramGuideEventsApi ProgramGuideEvents { get; }
    ITvAirEpgApi Epg { get; }
    ITvAirChannelsApi Channels { get; }
    ITvAirServiceMetadataApi ServiceMetadata { get; }
    ITvAirTunersApi Tuners { get; }
    ITvAirViewersApi Viewers { get; }
    ITvAirViewerReservationsApi ViewerReservations { get; }
    ITvAirTimedTextStreamsApi TimedTextStreams { get; }
    ITvAirBackupApi Backup { get; }
    ITvAirSettingsApi Settings { get; }
    ITvAirSystemApi System { get; }
    ITvAirNotificationsApi Notifications { get; }
    ITvAirWindowsApi Windows { get; }
    ITvAirPluginStorageApi Storage { get; }
    ITvAirEventsApi Events { get; }
    ITvAirExternalJobsApi ExternalJobs { get; }
    ITvAirPowerInhibitionApi PowerInhibition => throw new NotSupportedException("PowerInhibition requires Host Contract 1.1.17.");
    ITvAirHostsApi Hosts { get; }
    ITvAirPluginsApi Plugins { get; }
    ITvAirPluginSelectionsApi PluginSelections { get; }
    ITvAirExternalLookupApi ExternalLookup { get; }
    ITvAirInternetAccessApi InternetAccess { get; }
}

// Runtime plugin implementations receive ITvAirPluginContext through
// ITvAirRuntimeCapabilityPlugin.Initialize. Plugin identity, permissions, lifecycle, UI,
// assets, windows, surfaces and menu declarations are owned exclusively by
// TvAirPluginRuntimeDescriptor.
public interface ITvAirLogsApi
{
    IReadOnlyList<TvAirLogEntryDto> Query(TvAirLogQueryDto? query = null);
    void Write(TvAirLogWriteDto entry);
    void AddTimeline(string title, string message);
    void AddAudit(string action, string message);
}

public sealed class TvAirLogWriteDto
{
    public string Level { get; init; } = "Info";
    public string Category { get; init; } = "Plugin";
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Provides a plugin-owned presentation snapshot for TvAIr log surfaces.
/// This is a host capability, not a PLST-specific API.
/// </summary>
public interface ITvAirLogPresentationApi
{
    TvAirLogPresentationReplaceResultDto ReplaceSnapshot(TvAirLogPresentationSnapshotDto snapshot);
    TvAirLogPresentationReplaceResultDto SetPolicy(TvAirLogPresentationPolicyDto policy);
    void ClearPolicy(string? viewKey = null);
    void ClearSnapshot(string? viewKey = null);
    TvAirLogPresentationSnapshotDto? GetActiveSnapshot(string viewKey);
    TvAirLogPresentationPolicyDto? GetActivePolicy(string viewKey);
}

public enum TvAirLogDetailLayout
{
    Inline,
    Multiline
}

public static class TvAirLogDetailKeys
{
    public const string ProgramTitle = "program.title";
    public const string ReservationId = "reservation.id";
    public const string ReservationSource = "reservation.source";
    public const string RecordingId = "recording.id";
    public const string ScheduleStart = "schedule.start";
    public const string ScheduleEnd = "schedule.end";
    public const string RecordingActualStart = "recording.actualStart";
    public const string RecordingActualEnd = "recording.actualEnd";
    public const string RecordingQualityDrop = "recording.quality.drop";
    public const string RecordingQualityError = "recording.quality.error";
    public const string RecordingQualityScramble = "recording.quality.scramble";
    public const string RecordingFilePath = "recording.filePath";
    public const string StateBefore = "state.before";
    public const string StateAfter = "state.after";
    public const string Reason = "reason";
}

public sealed class TvAirLogPresentationPolicyDto
{
    public string ViewKey { get; init; } = "reservation-log";
    public string Title { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> DetailKeys { get; init; } = Array.Empty<string>();
    public TvAirLogDetailLayout Layout { get; init; } = TvAirLogDetailLayout.Inline;
    public bool HideEmptyDetails { get; init; } = true;
    public int Priority { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    // Compatibility input only. The host normalizes these flags into DetailKeys.
    public bool ShowQuality { get; init; }
    public bool ShowFilePath { get; init; }
    public bool ShowReservationId { get; init; }
    public bool ShowRecordingId { get; init; }
    public bool ShowSchedule { get; init; }
    public bool MultilineOnDetail { get; init; }
}

public sealed class TvAirLogPresentationSnapshotDto
{
    public string ViewKey { get; init; } = "reservation-log";
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public bool ReplaceHostDefault { get; init; } = true;
    public int Priority { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<TvAirLogPresentationEntryDto> Entries { get; init; } = Array.Empty<TvAirLogPresentationEntryDto>();
}

public sealed class TvAirLogPresentationEntryDto
{
    public string EntryId { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Severity { get; init; } = "Info";
    public string Category { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    /// <summary>Host-owned target column. Policies cannot replace or reconstruct this value.</summary>
    public string Target { get; init; } = string.Empty;
    /// <summary>Display mode for the composed target cell. Supported values: singleline, multiline, auto.</summary>
    public string TargetTextMode { get; init; } = string.Empty;
    /// <summary>Display mode for the result cell. Supported values: singleline, multiline, auto.</summary>
    public string ResultTextMode { get; init; } = string.Empty;
    /// <summary>Display mode for the message/content cell. Supported values: singleline, multiline, auto.</summary>
    public string MessageTextMode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public long? DropCount { get; init; }
    public long? ErrorCount { get; init; }
    public long? ScrambleCount { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

public sealed class TvAirLogPresentationReplaceResultDto
{
    public bool Accepted { get; init; }
    public string ViewKey { get; init; } = string.Empty;
    public int EntryCount { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Provides recording-quality results gathered by plugins. TvAIr stores the snapshot separately
/// from reservations and EPG data and does not write it into epg_events.
/// </summary>
public interface ITvAirRecordingQualityPresentationApi
{
    TvAirRecordingQualityReplaceResultDto ReplaceSnapshot(TvAirRecordingQualitySnapshotDto snapshot);
    void ClearSnapshot();
    TvAirRecordingQualitySnapshotDto? GetActiveSnapshot();
}

public sealed class TvAirRecordingQualitySnapshotDto
{
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public int Priority { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<TvAirRecordingQualityDto> Items { get; init; } = Array.Empty<TvAirRecordingQualityDto>();
}

public sealed class TvAirRecordingQualityDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string State { get; init; } = string.Empty;
    public long? DropCount { get; init; }
    public long? ErrorCount { get; init; }
    public long? ScrambleCount { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

public sealed class TvAirRecordingQualityReplaceResultDto
{
    public bool Accepted { get; init; }
    public int ItemCount { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class TvAirLogQueryDto
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int? Limit { get; init; }
    public string? Category { get; init; }
    public string? ResultCode { get; init; }
    public string? ReservationId { get; init; }
}

public sealed class TvAirLogEntryDto
{
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ResultCode { get; init; }
    public string? OperationId { get; init; }
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}


public interface ITvAirRulesApi
{
    IReadOnlyList<TvAirKeywordRuleDto> ListKeywordRules(TvAirRuleQueryDto? query = null);
    IReadOnlyList<TvAirProgramRuleDto> ListProgramRules(TvAirRuleQueryDto? query = null);
}

public sealed class TvAirRuleQueryDto
{
    public bool? Enabled { get; init; }
    public string? Keyword { get; init; }
    public int? Limit { get; init; }
}

/// <summary>Plugin-owned user-selectable option. Host does not interpret its meaning.</summary>
public sealed class TvAirPluginSelectionOptionDto
{
    public string SelectionId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>Generic binding fixed to a reservation at creation time.</summary>
public sealed class TvAirPluginSelectionBindingDto
{
    public string PluginId { get; init; } = string.Empty;
    public string FeatureId { get; init; } = string.Empty;
    public string SelectionId { get; init; } = string.Empty;
}

/// <summary>Current selection catalog published by the owning Plugin.</summary>
public sealed class TvAirPluginSelectionFeatureDto
{
    public string PluginId { get; init; } = string.Empty;
    public string FeatureId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public IReadOnlyList<TvAirPluginSelectionOptionDto> Options { get; init; } = Array.Empty<TvAirPluginSelectionOptionDto>();
}

/// <summary>Owner-scoped publication; PluginId is assigned by the Host, not by the caller.</summary>
public interface ITvAirPluginSelectionsApi
{
    void Publish(string featureId, string displayName, bool enabled, IReadOnlyList<TvAirPluginSelectionOptionDto> options);
    void Remove(string featureId);
}

public sealed class TvAirKeywordRuleDto
{
    public int RuleId { get; init; }
    public IReadOnlyList<TvAirPluginSelectionBindingDto> PluginBindings { get; init; } = Array.Empty<TvAirPluginSelectionBindingDto>();
    public string Name { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public string ExcludePattern { get; init; } = string.Empty;
    public bool UseRegex { get; init; }
    public bool SearchTitle { get; init; }
    public bool SearchOutline { get; init; }
    public bool SearchDetail { get; init; }
    public bool SearchCast { get; init; }
    public bool RecordCurrentServiceOnly { get; init; } = true;
    public bool RecordSubtitles { get; init; } = true;
    public bool Enabled { get; init; }
    public bool UseAllChannels { get; init; }
    /// <summary>Comma-separated exact service identities in NID:TSID:SID form. SID-only values are legacy Host data and must not be newly authored.</summary>
    public string TargetServices { get; init; } = string.Empty;
    public string TargetGenres { get; init; } = string.Empty;
    public string TargetDays { get; init; } = string.Empty;
    public bool UseTimeRange { get; init; }
    public string StartTime { get; init; } = string.Empty;
    public string EndTime { get; init; } = string.Empty;
    public string ExpiresOn { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class TvAirProgramRuleDto
{
    public int RuleId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int DayOfWeek { get; init; }
    public string StartTime { get; init; } = string.Empty;
    public string EndTime { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public string ExpiresOn { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public interface ITvAirReservationsApi
{
    IReadOnlyList<TvAirReservationDto> List(TvAirReservationQueryDto? query = null);
    /// <summary>Returns terminal reservation history (Completed, Failed, Cancelled). Active reservations remain in List().</summary>
    IReadOnlyList<TvAirReservationDto> ListHistory(TvAirReservationHistoryQueryDto? query = null);
    TvAirReservationDto? Get(string reservationId);
    IReadOnlyList<TvAirReservationConflictDto> ListConflicts();
    TvAirReservationPreviewDto Preview(TvAirReservationPreviewRequestDto request);
    IReadOnlyList<TvAirChainCandidateDto> ListChainCandidates(TvAirChainCandidateQueryDto? query = null);
    TvAirChainPreviewDto PreviewChain(TvAirReservationPreviewRequestDto request);
    TvAirReservationOperationResultDto Add(TvAirReservationCreateDto request);
    TvAirReservationOperationResultDto Update(TvAirReservationUpdateDto request);
    TvAirReservationOperationResultDto Delete(TvAirReservationDeleteDto request);
}


public sealed class TvAirReservationPreviewRequestDto
{
    /// <summary>Stable service identity component. Supply the exact NetworkId + TransportStreamId + ServiceId triplet.</summary>
    public int NetworkId { get; init; }
    /// <summary>Stable service identity component. Supply the exact NetworkId + TransportStreamId + ServiceId triplet.</summary>
    public int TransportStreamId { get; init; }
    /// <summary>Stable service identity component. Supply the exact NetworkId + TransportStreamId + ServiceId triplet.</summary>
    public int ServiceId { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string? ChainPreviousReservationId { get; init; }
}

public sealed class TvAirChainCandidateQueryDto
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>Optional stable service identity filter. When one component is supplied, all three must be supplied.</summary>
    public int? NetworkId { get; init; }
    /// <summary>Optional stable service identity filter. When one component is supplied, all three must be supplied.</summary>
    public int? TransportStreamId { get; init; }
    /// <summary>Optional stable service identity filter. When one component is supplied, all three must be supplied.</summary>
    public int? ServiceId { get; init; }
}

public sealed class TvAirReservationConflictDto
{
    public string ReservationId { get; init; } = string.Empty;
    /// <summary>Stable service identity component. Use NetworkId + TransportStreamId + ServiceId as the key.</summary>
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public string ProgramTitle { get; init; } = string.Empty;
    /// <summary>Current mutable display name resolved by the Host. Never use as service identity.</summary>
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class TvAirChainCandidateDto
{
    public string PreviousReservationId { get; init; } = string.Empty;
    public string CurrentReservationId { get; init; } = string.Empty;
    public string CurrentProgramId { get; init; } = string.Empty;
    public bool SameTuner { get; init; }
    public string LossTarget { get; init; } = string.Empty;
    public string LossPart { get; init; } = string.Empty;
    public string LossDescription { get; init; } = string.Empty;
    public bool IsAllowed { get; init; }
}

public sealed class TvAirReservationPreviewDto
{
    public bool CanReserve { get; init; }
    public bool HasConflict { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string SuggestedTunerName { get; init; } = string.Empty;
    public IReadOnlyList<TvAirReservationConflictDto> Conflicts { get; init; } = Array.Empty<TvAirReservationConflictDto>();
    public IReadOnlyList<TvAirChainCandidateDto> ChainCandidates { get; init; } = Array.Empty<TvAirChainCandidateDto>();
}

public sealed class TvAirChainPreviewDto
{
    public bool CanChain { get; init; }
    public string Message { get; init; } = string.Empty;
    public TvAirChainCandidateDto? ChainInfo { get; init; }
}

public enum TvAirReservationIntent
{
    Unspecified,
    InteractiveProgramEvent,
    ProgramTimeSlot,
    AutomaticSearch,
    KeywordRule,
    System
}

public sealed class TvAirReservationCreateDto
{
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int EventId { get; init; }
    public string ProgramTitle { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public int PreMarginMinutes { get; init; }
    public int PostMarginMinutes { get; init; }
    public string? ChannelArgument { get; init; }
    public TvAirReservationIntent Intent { get; init; } = TvAirReservationIntent.Unspecified;
    public bool AllowChain { get; init; }
    public string? ChainPreviousReservationId { get; init; }
}

public sealed class TvAirReservationUpdateDto
{
    public string ReservationId { get; init; } = string.Empty;
    public bool? Enabled { get; init; }
}

public sealed class TvAirReservationDeleteDto
{
    public string ReservationId { get; init; } = string.Empty;
    public bool Force { get; init; }
}

public sealed class TvAirReservationOperationResultDto
{
    public bool Success { get; init; }
    public string? ReservationId { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class TvAirReservationQueryDto
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ServiceName { get; init; }
    public string? Source { get; init; }
    public string? Status { get; init; }
    public bool? Enabled { get; init; }
    public bool IncludeSystemEntries { get; init; }
    public bool? Conflicted { get; init; }
}

public sealed class TvAirReservationHistoryQueryDto
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ReservationId { get; init; }
    public string? ServiceName { get; init; }
    public string? Source { get; init; }
    public string? Status { get; init; }
    public bool IncludeSystemEntries { get; init; }
    /// <summary>Maximum rows after End descending sort. Defaults to 1000 and is clamped to 1..10000.</summary>
    public int? Limit { get; init; }
}

public sealed class TvAirReservationDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public TvAirReservationIntent Intent { get; init; } = TvAirReservationIntent.Unspecified;
    public string CreatedThrough { get; init; } = string.Empty;
    public string CreatedByPluginId { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public DateTimeOffset? ScheduledStart { get; init; }
    public string? TunerName { get; init; }
    public string? PredecessorReservationId { get; init; }
    public string? ChainRootReservationId { get; init; }
    public bool IsUserChain { get; init; }
    public bool IsEnabled { get; init; }
    /// <summary>Hostのサービス操作可否SSOTで現在操作可能な予約局か。</summary>
    public bool IsServiceOperational { get; init; } = true;
    /// <summary>IsServiceOperational=false の場合のHost正規理由。Plugin独自の.ch2/ChSet再解釈は禁止。</summary>
    public string ServiceAccessReason { get; init; } = string.Empty;
    public bool HasConflict { get; init; }
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int EventNumber { get; init; }
    public string? PlannedTunerName { get; init; }
    public string? ActualTunerName { get; init; }
    public DateTimeOffset? RecordingStartedAt { get; init; }
    public DateTimeOffset? RecordingFinishedAt { get; init; }
    public int? SourceRuleId { get; init; }
    public string SourceRuleName { get; init; } = string.Empty;
    public int ReservationNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public interface ITvAirRecordingsApi
{
    IReadOnlyList<TvAirRecordingSessionDto> ListActive();
    /// <summary>Returns terminal recording history. Active recording sessions are exposed only by ListActive().</summary>
    IReadOnlyList<TvAirRecordingHistoryDto> ListHistory(TvAirRecordingHistoryQueryDto? query = null);
    TvAirRecordingSessionDto? GetActiveByReservationId(string reservationId);
}

public sealed class TvAirRecordingHistoryQueryDto
{
    /// <summary>Inclusive lower overlap boundary. A row matches when End &gt;= From.</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>Exclusive upper overlap boundary. A row matches when Start &lt; To.</summary>
    public DateTimeOffset? To { get; init; }
    public string? ReservationId { get; init; }
    public string? ServiceName { get; init; }
    /// <summary>Includes system-generated EPG entries when true. Defaults to false.</summary>
    public bool IncludeSystemEntries { get; init; }
    /// <summary>Maximum rows after End descending sort. Defaults to 1000 and is clamped to 1..10000.</summary>
    public int? Limit { get; init; }
}

public sealed class TvAirRecordingSessionDto
{
    public string RecordingSessionId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset ScheduledEnd { get; init; }
    public string TunerName { get; init; } = string.Empty;
    public string? Did { get; init; }
    public string State { get; init; } = string.Empty;
    public string? OutputFilePath { get; init; }
}

public sealed class TvAirRecordingHistoryDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string RecordingId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public ushort EventId { get; init; }
    public DateTimeOffset ScheduledStartTime { get; init; }
    public string Genre { get; init; } = string.Empty;
    public string GenreCodes { get; init; } = string.Empty;
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public DateTimeOffset? ActualStart { get; init; }
    public DateTimeOffset? ActualEnd { get; init; }
    public string Result { get; init; } = string.Empty;
    public string EndReason { get; init; } = string.Empty;
    public string? OutputFilePath { get; init; }
    public bool? FileCreated { get; init; }
    public long? DropCount { get; init; }
    public long? ErrorCount { get; init; }
    public long? ScrambleCount { get; init; }
    public bool QualityDataAvailable { get; init; }
    public string QualityCompleteness { get; init; } = string.Empty;
    public string QualitySource { get; init; } = string.Empty;
    public string ResourceReleaseState { get; init; } = string.Empty;
    /// <summary>
    /// true の場合、このRecordingIdに対するTvAIr側の録画結果は確定済み。
    /// FileCreated == true かつ有効なファイルパスがある場合、TvAIrの録画処理による
    /// 当該ファイルへの書き込み・使用も終了しており、外部後処理は読み取りを開始できる。
    /// </summary>
    public bool ResultFinalized { get; init; }
    public bool Succeeded { get; init; }
}



public interface ITvAirPlaybackProgressApi
{
    TvAirPlaybackProgressSnapshotDto GetSnapshot();
    TvAirPlaybackProgressDto? Get(string recordingId);
    TvAirPlaybackProgressDto Update(TvAirPlaybackProgressUpdateDto update);
    bool Remove(string recordingId);
}

public sealed class TvAirPlaybackProgressSnapshotDto
{
    public string SnapshotId { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<TvAirPlaybackProgressDto> Items { get; init; } = Array.Empty<TvAirPlaybackProgressDto>();
}

public sealed class TvAirPlaybackProgressDto
{
    public string RecordingId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public long PositionSeconds { get; init; }
    public long DurationSeconds { get; init; }
    public double CompletionRatio { get; init; }
    public bool IsCompleted { get; init; }
    public int PlayCount { get; init; }
    public DateTimeOffset? FirstPlayedAt { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class TvAirPlaybackProgressUpdateDto
{
    public string RecordingId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public long PositionSeconds { get; init; }
    public long DurationSeconds { get; init; }
    public bool? IsCompleted { get; init; }
    public bool IncrementPlayCount { get; init; }
    public DateTimeOffset? PlayedAt { get; init; }
}

public interface ITvAirMediaInsightsApi
{
    TvAirMediaContextSnapshotDto GetContextSnapshot(TvAirMediaContextQueryDto? query = null);
}

public sealed class TvAirMediaContextQueryDto
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

public sealed class TvAirMediaContextSnapshotDto
{
    public string SnapshotId { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public int RecordingCount { get; init; }
    public int ReservedCount { get; init; }
    public int UnwatchedRecordingCount { get; init; }
    public long UnwatchedDurationSeconds { get; init; }
    public double CompletionRate { get; init; }
    public IReadOnlyList<TvAirMediaBucketDto> Genres { get; init; } = Array.Empty<TvAirMediaBucketDto>();
    public IReadOnlyList<TvAirMediaBucketDto> Weekdays { get; init; } = Array.Empty<TvAirMediaBucketDto>();
    public IReadOnlyList<TvAirMediaBucketDto> TimeBands { get; init; } = Array.Empty<TvAirMediaBucketDto>();
}

public sealed class TvAirMediaBucketDto
{
    public string Key { get; init; } = string.Empty;
    public int Count { get; init; }
    public long DurationSeconds { get; init; }
}

public interface ITvAirContentDiscoveryApi
{
    TvAirContentDiscoveryResultDto SearchAvailable(TvAirContentDiscoveryQueryDto query);
}

public sealed class TvAirContentDiscoveryQueryDto
{
    public DateTimeOffset? Now { get; init; }
    public int MaximumAvailableMinutes { get; init; } = 30;
    public bool IncludeLive { get; init; } = true;
    public bool IncludeRecordings { get; init; } = true;
    public bool UnwatchedOnly { get; init; }
    public bool ResumableOnly { get; init; }
    public IReadOnlyList<string> ExcludedGenres { get; init; } = Array.Empty<string>();
    public int Limit { get; init; } = 100;
}

public sealed class TvAirContentDiscoveryResultDto
{
    public string SnapshotId { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<TvAirAvailableContentDto> Items { get; init; } = Array.Empty<TvAirAvailableContentDto>();
}

public sealed class TvAirAvailableContentDto
{
    public string ContentId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    /// <summary>Current display name for the service. Mutable metadata; never use as service identity.</summary>
    public string ServiceName { get; init; } = string.Empty;
    /// <summary>Stable service identity component. Service identity is NetworkId + TransportStreamId + ServiceId.</summary>
    public int NetworkId { get; init; }
    /// <summary>Stable service identity component. Service identity is NetworkId + TransportStreamId + ServiceId.</summary>
    public int TransportStreamId { get; init; }
    /// <summary>Stable service identity component. Service identity is NetworkId + TransportStreamId + ServiceId.</summary>
    public int ServiceId { get; init; }
    public string Genre { get; init; } = string.Empty;
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public long TotalSeconds { get; init; }
    public long RemainingSeconds { get; init; }
    public long ResumePositionSeconds { get; init; }
    public bool IsUnwatched { get; init; }
    public bool CanWatchLive { get; init; }
    public bool CanPlayRecording { get; init; }
    public bool CanResume { get; init; }
    public string MatchReason { get; init; } = string.Empty;
}

public interface ITvAirRecordingFilesApi
{
    IReadOnlyList<TvAirRecordingFileDto> List(TvAirRecordingFileQueryDto? query = null);
    TvAirRecordingFileDto? GetByReservationId(string reservationId);
    /// <summary>1.1.16: durable file-release journal. Persist NextSequence only after processing the page.
    /// Delivery is repeatable; consumers deduplicate by RecordingFileId. No size threshold is applied.</summary>
    TvAirFinalizedRecordingFilePageDto ListFinalized(TvAirFinalizedRecordingFileQueryDto? query = null)
        => throw new NotSupportedException("Requires Host Contract 1.1.16.");
}

public sealed class TvAirFinalizedRecordingFileQueryDto
{
    public string? JournalId { get; init; }
    public string? AfterRecordingFileId { get; init; }
    public long AfterSequence { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record TvAirFinalizedRecordingFileDto
{
    public long Sequence { get; init; }
    public string RecordingFileId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public string? RecoveryParentReservationId { get; init; }
    public string RecoveryChainId { get; init; } = string.Empty;
    public string ReservationSource { get; init; } = string.Empty;
    public string ProgramTitle { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    /// <summary>Confirmed size, or null when unavailable. The consumer's size policy must check the actual readable file.</summary>
    public long? SizeBytes { get; init; }
    public bool FileReleased { get; init; }
    /// <summary>Physical file state: Released (sink close proof), or WriterExited (writer exited without per-segment close proof; may also be a normal stop).
    /// This is not reservation success. Existing RecordingResultFinalized.Succeeded remains authoritative.</summary>
    public string Result { get; init; } = string.Empty;
    public string EndReason { get; init; } = string.Empty;
    public DateTimeOffset ReleasedAt { get; init; }
    public IReadOnlyList<TvAirPluginSelectionBindingDto> PluginBindings { get; init; }
        = Array.Empty<TvAirPluginSelectionBindingDto>();
}

public sealed class TvAirFinalizedRecordingFilePageDto
{
    public string JournalId { get; init; } = string.Empty;
    public string? NextRecordingFileId { get; init; }
    public IReadOnlyList<TvAirFinalizedRecordingFileDto> Items { get; init; }
        = Array.Empty<TvAirFinalizedRecordingFileDto>();
    public long NextSequence { get; init; }
    public bool HasMore { get; init; }
}

public sealed class TvAirRecordingFileQueryDto
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ReservationId { get; init; }
}

public sealed class TvAirRecordingFileDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastWriteAt { get; init; }
}

public interface ITvAirRecordingInspectionApi
{
    TvAirRecordingInspectionResultDto? GetByReservationId(string reservationId);
    TvAirRecordingInspectionJobDto RequestInspection(string reservationId);
}

public sealed class TvAirRecordingInspectionResultDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public long? DropCount { get; init; }
    public long? ErrorCount { get; init; }
    public long? ScrambleCount { get; init; }
    public string? Summary { get; init; }
}

public sealed class TvAirRecordingInspectionJobDto
{
    public string JobId { get; init; } = string.Empty;
    public string ReservationId { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public bool Accepted { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Host projected programme surface for operationally available services only.
/// Services disabled in TVTest .ch2, absent from .ch2, or without a current Host route are not exposed here.
/// Raw/canonical EPG retention is a Host concern and is intentionally broader than this Plugin surface.
/// </summary>
public interface ITvAirProgramGuideApi
{
    IReadOnlyList<TvAirProgramGuideWaveFilterDto> ListWaveFilters();
    IReadOnlyList<TvAirProgramEventDto> ListEvents(TvAirProgramGuideQueryDto? query = null);
    TvAirProgramEventDto? GetEvent(TvAirProgramEventKeyDto key);
    TvAirProgramEventDto? GetEventByProjectedId(string projectedEventId);

    // Compatibility entry point for existing plugins. New integrations use ExternalProgramSource.
    TvAirExternalProgramGuideReplaceResultDto ReplaceExternalEvents(IReadOnlyList<TvAirExternalProgramEventDto> events);
    void ClearExternalEvents();
}

/// <summary>
/// Owns this plugin's external program snapshot used by runtime program-guide projection.
/// The snapshot is never written back to epg_events.
/// </summary>
public interface ITvAirExternalProgramSourceApi
{
    TvAirExternalProgramGuideReplaceResultDto ReplaceSnapshot(IReadOnlyList<TvAirExternalProgramEventDto> events);
    void ClearSnapshot();
}

public sealed class TvAirProgramGuideWaveFilterDto
{
    public string Key { get; init; } = string.Empty;
    public string BroadcastType { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public int Order { get; init; }
    public bool IsProgramGuideFilter { get; init; }
}

public sealed class TvAirProgramGuideQueryDto
{
    /// <summary>Exclusive lower overlap boundary. A row matches when End &gt; From.</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>Exclusive upper overlap boundary. A row matches when Start &lt; To.</summary>
    public DateTimeOffset? To { get; init; }
    public int? NetworkId { get; init; }
    public int? TransportStreamId { get; init; }
    public int? ServiceId { get; init; }
    public string? ServiceName { get; init; }
    public string? Keyword { get; init; }
    public string? Genre { get; init; }
    /// <summary>Optional maximum rows after Start/display-order sorting. When specified, it is clamped to 1..20000. Null returns all matching rows.</summary>
    public int? Limit { get; init; }
}

public sealed class TvAirProgramEventKeyDto
{
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int EventNumber { get; init; }
}

public sealed class TvAirProgramEventDto
{
    /// <summary>Broadcast event identifier in NID:TSID:SID:EID form.</summary>
    public string EventId { get; init; } = string.Empty;
    /// <summary>Exact projected-event key, including projection source identity where applicable.</summary>
    public string ProjectionEventKey { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int EventNumber { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? Detail { get; init; }
    public string? Genre { get; init; }
    public string? GenreCodes { get; init; }
    public int DurationSeconds { get; init; }
    public string? ExtendedItems { get; init; }
    /// <summary>Projected event update time.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>Raw TvAIr DB event update time. Null for overlay-only events.</summary>
    public DateTimeOffset? DbUpdatedAt { get; init; }
    public bool IsSafeForSpecialProjection { get; init; }
    public string SpecialProjectionUnsafeReason { get; init; } = string.Empty;
    public string ProjectionState { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string SourcePluginId { get; init; } = string.Empty;
    public string SourceEventKey { get; init; } = string.Empty;
    public bool DbEventExists { get; init; }
}

public sealed class TvAirExternalProgramEventDto
{
    public string SourceKind { get; init; } = "ExternalEpg";
    public string SourceEventKey { get; init; } = string.Empty;

    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int EventNumber { get; init; }

    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }

    public string ServiceName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? Detail { get; init; }
    public string? ExtendedItems { get; init; }
    public string? Genre { get; init; }
    public string? GenreCodes { get; init; }
}

public sealed class TvAirExternalProgramGuideReplaceResultDto
{
    public bool Accepted { get; init; }
    public int AcceptedCount { get; init; }
    public int RejectedCount { get; init; }
    public int RejectedInvalidCount { get; init; }
    public int RejectedDuplicateCount { get; init; }
    public bool Changed { get; init; }
    public int PreviousCount { get; init; }
    public int CurrentCount { get; init; }
    public string OperationId { get; init; } = string.Empty;
    public string SourceOwnerId { get; init; } = string.Empty;
    public long OwnerRevision { get; init; }
    public long ProjectedRevision { get; init; }
    public string SynchronizationState { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public interface ITvAirProgramGuideEventsApi
{
    IReadOnlyList<TvAirProgramGuideChangeDto> ListChanges(TvAirProgramGuideChangeQueryDto? query = null);
}

public sealed class TvAirProgramGuideChangeQueryDto
{
    public DateTimeOffset? Since { get; init; }
    /// <summary>Optional stable service identity filter. Reserved for keyed change projections; when used, all three components must be supplied.</summary>
    public int? NetworkId { get; init; }
    /// <summary>Optional stable service identity filter. Reserved for keyed change projections; when used, all three components must be supplied.</summary>
    public int? TransportStreamId { get; init; }
    /// <summary>Optional stable service identity filter. Reserved for keyed change projections; when used, all three components must be supplied.</summary>
    public int? ServiceId { get; init; }
}

public sealed class TvAirProgramGuideChangeDto
{
    public DateTimeOffset Timestamp { get; init; }
    public string ChangeKind { get; init; } = string.Empty;
    public TvAirProgramEventKeyDto? EventKey { get; init; }
}

public interface ITvAirEpgApi
{
    TvAirEpgStatusDto GetStatus();
    TvAirEpgRunResultDto RequestRun(TvAirEpgRunRequestDto request);
}

public enum TvAirEpgRunScope
{
    All,
    Ground,
    BsCs
}

public sealed class TvAirEpgRunRequestDto
{
    public TvAirEpgRunScope Scope { get; init; }
}

public sealed class TvAirEpgStatusDto
{
    public bool IsRunning { get; init; }
    public bool CanStart { get; init; }
    public bool CanCancel { get; init; }
    public string? Source { get; init; }
    public string? Scope { get; init; }
    public bool Silent { get; init; }
    public string? UiMode { get; init; }
    public string? CancelRoute { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? LastResult { get; init; }
}

public sealed class TvAirEpgRunResultDto
{
    public bool Accepted { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? Message { get; init; }
}

/// <summary>Operational Host services by default. Set Enabled=false only when the plugin explicitly needs disabled .ch2 service metadata; disabled services never appear in ProgramGuide or operational Viewer/Reservation routes.</summary>
public interface ITvAirChannelsApi
{
    /// <summary>
    /// Enabled rows use the Host programme-guide channel inventory, independent of EPG presence
    /// and available viewer profiles. BroadcastType is the displayed wave (GR/BS/CS/CATV),
    /// not physical tuner capability. DisplayOrder is the global inventory position.
    /// IsEnabled does not guarantee a selectable route for a requested viewer profile.
    /// </summary>
    IReadOnlyList<TvAirServiceDto> ListServices(TvAirServiceQueryDto? query = null);
}

public sealed class TvAirServiceQueryDto
{
    /// <summary>Displayed wave GR/BS/CS/CATV. Legacy BSCS (BS/CS) and HYBRID (CATV) filter aliases are accepted.</summary>
    public string? BroadcastType { get; init; }
    public bool? Enabled { get; init; }
}

public sealed class TvAirServiceDto
{
    public string ServiceName { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public string BroadcastType { get; init; } = string.Empty;
    public int? RemoteControlKeyId { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsEnabled { get; init; }
}

public interface ITvAirServiceMetadataApi
{
    TvAirServiceMetadataDto? Get(int networkId, int transportStreamId, int serviceId);
    IReadOnlyList<TvAirServiceMetadataDto> List(TvAirServiceMetadataQueryDto? query = null);
    TvAirChannelLoadInfoDto GetLoadInfo();
}

public sealed class TvAirServiceMetadataQueryDto
{
    public string? BroadcastType { get; init; }
}

public sealed class TvAirServiceMetadataDto
{
    public string ServiceName { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public string BroadcastType { get; init; } = string.Empty;
    public byte[]? LogoBytes { get; init; }
    public string? LogoMimeType { get; init; }
    public int DisplayOrder { get; init; }
    public string ChannelArgument { get; init; } = string.Empty;
}

public sealed class TvAirChannelLoadInfoDto
{
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public int RawSkippedCount { get; init; }
}

public interface ITvAirTunersApi
{
    IReadOnlyList<TvAirTunerStatusDto> ListTuners();
}

public interface ITvAirViewersApi
{
    TvAirViewerSnapshotDto GetSnapshot();
    IReadOnlyList<TvAirViewerProfileDto> ListProfiles();
    IReadOnlyList<TvAirViewerSessionDto> ListSessions(TvAirViewerSessionQueryDto? query = null);
    TvAirViewerOperationResultDto Start(TvAirViewerStartRequestDto request);
    TvAirViewerOperationResultDto EnsureTuned(TvAirViewerEnsureTunedRequestDto request);
    TvAirViewerOperationResultDto Retune(TvAirViewerRetuneRequestDto request);
    /// <summary>Explicitly regenerates the current managed Viewer Session using its existing Host-owned identity.</summary>
    TvAirViewerOperationResultDto Restart(TvAirViewerRestartRequestDto request);
    TvAirViewerOperationResultDto Activate(TvAirViewerActivateRequestDto request);
    TvAirViewerOperationResultDto Stop(TvAirViewerStopRequestDto request);
    TvAirViewerOperationResultDto StopCompatible(TvAirViewerCompatibleStopRequestDto request);
    /// <summary>Host-owned explicit Viewer Operation直前の同期preemption境界。handler内でViewer Operationを再入させない。</summary>
    IDisposable SubscribeOperationPreempting(Action<TvAirViewerOperationPreemptingDto> handler);
}

/// <summary>
/// Host-owned one-shot Viewer Reservation contract. Reservations conflict only when ViewerProfileId and ScheduledStart are both equal.
/// ScheduledEnd remains programme metadata and is not a ViewerProfile occupancy boundary.
/// Create accepts only services that are operationally available under the Host .ch2/route policy.
/// </summary>
public interface ITvAirViewerReservationsApi
{
    TvAirViewerReservationMutationResultDto Create(TvAirViewerReservationCreateRequestDto request);
    TvAirViewerReservationMutationResultDto Cancel(string reservationId);
    IReadOnlyList<TvAirViewerReservationDto> List(TvAirViewerReservationQueryDto? query = null);
}

/// <summary>One future Viewer Operation request; ViewerProfileId + ScheduledStart identifies its execution slot.</summary>
public sealed class TvAirViewerReservationCreateRequestDto
{
    public string ViewerProfileId { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int EventId { get; init; }
    public DateTimeOffset ScheduledStart { get; init; }
    public DateTimeOffset? ScheduledEnd { get; init; }
}

public sealed class TvAirViewerReservationQueryDto
{
    public string? ViewerProfileId { get; init; }
    public bool IncludeTerminal { get; init; }
}

public sealed class TvAirViewerReservationDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string OwnerPluginId { get; init; } = string.Empty;
    public string ViewerProfileId { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public int EventId { get; init; }
    public DateTimeOffset ScheduledStart { get; init; }
    public DateTimeOffset? ScheduledEnd { get; init; }
    public string State { get; init; } = string.Empty;
    public string FailureReason { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class TvAirViewerReservationMutationResultDto
{
    public bool Success { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public TvAirViewerReservationDto? Reservation { get; init; }
}

public sealed class TvAirViewerOperationPreemptingDto
{
    public string ViewerProfileId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string SourceOwnerId { get; init; } = string.Empty;
    public string ViewerReservationId { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
}


public sealed class TvAirViewerSnapshotDto
{
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<TvAirViewerProfileDto> Profiles { get; init; } = Array.Empty<TvAirViewerProfileDto>();
    public IReadOnlyList<TvAirViewerSessionDto> Sessions { get; init; } = Array.Empty<TvAirViewerSessionDto>();
    public string DefaultViewerProfile { get; init; } = string.Empty;
    public bool SelectorVisibleRecommended { get; init; }
    public string Source { get; init; } = "tvair_viewer_profile_and_session_registry";
    public string UnmanagedExternalTvTest { get; init; } = "out_of_scope";
}

public sealed class TvAirViewerSessionQueryDto
{
    public string? ViewerProfileId { get; init; }
    public string? AllocationGroup { get; init; }
    public string? DisplayGroup { get; init; }
    public string? ClientId { get; init; }
}

public sealed class TvAirViewerStartRequestDto
{
    public string ViewerProfileId { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public string? ServiceName { get; init; }
    public string? GroupHint { get; init; }
    public bool PreserveViewerWindowState { get; init; }
    /// <summary>Foreground policy. "activate" explicitly activates the owned Viewer; "preserve" or null never changes foreground state, including same-service no-op operations.</summary>
    public string? ViewerActivation { get; init; }
}

public sealed class TvAirViewerEnsureTunedRequestDto
{
    public string ViewerProfileId { get; init; } = string.Empty;
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public string? ServiceName { get; init; }
    public string? GroupHint { get; init; }
    public bool PreserveViewerWindowState { get; init; }
    /// <summary>Foreground policy. "activate" explicitly activates the owned Viewer; "preserve" or null never changes foreground state, including same-service no-op operations.</summary>
    public string? ViewerActivation { get; init; }
}

public sealed class TvAirViewerRetuneRequestDto
{
    public string ViewerProfileId { get; init; } = string.Empty;
    public string ViewerSessionId { get; init; } = string.Empty;
    public long ExpectedGeneration { get; init; }
    public int NetworkId { get; init; }
    public int TransportStreamId { get; init; }
    public int ServiceId { get; init; }
    public string? ServiceName { get; init; }
    public string? GroupHint { get; init; }
    public bool PreserveViewerWindowState { get; init; }
    /// <summary>Foreground policy. "activate" explicitly activates the owned Viewer; "preserve" or null never changes foreground state, including same-service no-op operations.</summary>
    public string? ViewerActivation { get; init; }
}

/// <summary>
/// Capability request for explicit Viewer Session regeneration. Viewer and service identity
/// are resolved from the current session and are not supplied by the caller.
/// </summary>
public sealed class TvAirViewerRestartRequestDto
{
    public string ViewerSessionId { get; init; } = string.Empty;
    public long ExpectedGeneration { get; init; }
    public bool PreserveViewerWindowState { get; init; }
    public string? ViewerActivation { get; init; }
    public string? Reason { get; init; }
}

public sealed class TvAirViewerActivateRequestDto
{
    public string ViewerSessionId { get; init; } = string.Empty;
    public long ExpectedGeneration { get; init; }
}

public sealed class TvAirViewerStopRequestDto
{
    public string ViewerSessionId { get; init; } = string.Empty;
    public long? ExpectedGeneration { get; init; }
    public string? ViewerProfileId { get; init; }
    public string? Reason { get; init; }
}


public sealed class TvAirViewerCompatibleStopRequestDto
{
    public string? LeaseId { get; init; }
    public string? ViewerProfileId { get; init; }
    public string? ViewerSessionId { get; init; }
    public long? ExpectedGeneration { get; init; }
    public string? Reason { get; init; }
}

public sealed class TvAirViewerOperationResultDto
{
    public bool Success { get; init; }
    public string State { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string ViewerProfileId { get; init; } = string.Empty;
    public string ViewerSessionId { get; init; } = string.Empty;
    public long Generation { get; init; }
    public string LeaseId { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public int? NetworkId { get; init; }
    public int? TransportStreamId { get; init; }
    public int? ServiceId { get; init; }
    public string LogicalViewerSlotId { get; init; } = string.Empty;
    public DateTimeOffset? AcquiredAt { get; init; }
    public int? ChannelSpace { get; init; }
    public int? ChannelIndex { get; init; }
    public string ViewerState { get; init; } = string.Empty;
    public string Diagnostics { get; init; } = string.Empty;
    // SDK 1.1.2 binary-compatibility surface. Values are compatibility diagnostics only; preserve retunes do not restore foreground.
    public string FocusPolicyRequested { get; init; } = string.Empty;
    public string FocusPolicyApplied { get; init; } = string.Empty;
    public long? ForegroundBeforeHwnd { get; init; }
    public int? ForegroundBeforePid { get; init; }
    public long? ForegroundAfterRetuneHwnd { get; init; }
    public int? ForegroundAfterRetunePid { get; init; }
    public long? ForegroundFinalHwnd { get; init; }
    public int? ForegroundFinalPid { get; init; }
    /// <summary>Legacy diagnostic. False is not evidence of unchanged focus when foreground handles are null.</summary>
    public bool ForegroundChanged { get; init; }
    public bool ChangedToTargetViewer { get; init; }
    public bool RestorationAttempted { get; init; }
    public bool RestorationSucceeded { get; init; }
    /// <summary>Legacy diagnostic. True requires an observed preservation result. The current Host returns false with not_measured; this does not mean observed focus loss.</summary>
    public bool FocusPreserved { get; init; }
    /// <summary>not_measured means focus was not observed. It is not a Viewer operation error; use OperationCompleted, HasWarning and ContinuationRecommended for operation outcomes.</summary>
    public string FocusPreserveFailureReason { get; init; } = string.Empty;
    /// <summary>True when the requested Viewer transition completed authoritatively.</summary>
    public bool OperationCompleted { get; init; }
    /// <summary>True when the transition completed with a non-fatal quality warning.</summary>
    public bool HasWarning { get; init; }
    /// <summary>True when timer-driven or other automatic workflows may continue.</summary>
    public bool ContinuationRecommended { get; init; }
}

public sealed class TvAirViewerProfileDto
{
    public string ViewerProfileId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsAuto { get; init; }
    public string TvTestPathKey { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    /// <summary>TvAIr設定でこのViewerに割り当てられた各放送波内の実デバイス番号。列挙順や名前から再採番しない。</summary>
    public int TvTestFrameIndex { get; init; }
    public string LogicalViewerSlotId { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedGroups { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AvailableGroups { get; init; } = Array.Empty<string>();
    public bool IsShared { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public int ActiveSessionCount { get; init; }
    public bool IsRunning { get; init; }
    public string CurrentViewerSessionId { get; init; } = string.Empty;
    public long CurrentGeneration { get; init; }
    public string CurrentViewerState { get; init; } = "inactive";
    public int? CurrentNetworkId { get; init; }
    public int? CurrentTransportStreamId { get; init; }
    public int? CurrentServiceId { get; init; }
}

public sealed class TvAirViewerSessionDto
{
    public string ViewerSessionId { get; init; } = string.Empty;
    public string ViewerProfileId { get; init; } = string.Empty;
    public string LogicalViewerSlotId { get; init; } = string.Empty;
    public long Generation { get; init; }
    public string LeaseId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ProgramGuideFilterGroup { get; init; } = string.Empty;
    public string TunerGroup { get; init; } = string.Empty;
    public string DisplayGroup { get; init; } = string.Empty;
    public string AllocationGroup { get; init; } = string.Empty;
    public string TunerName { get; init; } = string.Empty;
    public string BonDriverFileName { get; init; } = string.Empty;
    public string Did { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public DateTimeOffset AcquiredAt { get; init; }
    public int? ProcessId { get; init; }
    public int? NetworkId { get; init; }
    public int? TransportStreamId { get; init; }
    public int? ServiceId { get; init; }
    public int? ChannelSpace { get; init; }
    public int? ChannelIndex { get; init; }
    public int SlotIndex { get; init; }
    public string LaunchResult { get; init; } = string.Empty;
    public string TuneResult { get; init; } = string.Empty;
    public string ActivateResult { get; init; } = string.Empty;
    public string RollbackResult { get; init; } = string.Empty;
    public string ChannelArgument { get; init; } = string.Empty;
    public string ViewerProfileName { get; init; } = string.Empty;
    public string TvTestPathKey { get; init; } = string.Empty;
}

public interface ITvAirTimedTextStreamsApi
{
    void Publish(TvAirTimedTextPublishDto item);
    IReadOnlyList<TvAirTimedTextItemDto> ReadRecent(TvAirTimedTextQueryDto? query = null);
    IReadOnlyList<TvAirTimedTextGroupDto> ReadGroups(TvAirTimedTextGroupQueryDto? query = null);
}

public sealed class TvAirTimedTextPublishDto
{
    public string StreamId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string SourceOwnerId { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public long? PositionMilliseconds { get; init; }
    public string AuthorId { get; init; } = string.Empty;
    public string Style { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset? OccurredAt { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class TvAirTimedTextQueryDto
{
    public string? StreamId { get; init; }
    public string? GroupId { get; init; }
    public int? Count { get; init; }
}

public sealed class TvAirTimedTextGroupQueryDto
{
    public string? StreamId { get; init; }
    public int? CountPerGroup { get; init; }
}

public sealed class TvAirTimedTextItemDto
{
    public string StreamId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string SourceOwnerId { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public long? PositionMilliseconds { get; init; }
    public string AuthorId { get; init; } = string.Empty;
    public string Style { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class TvAirTimedTextGroupDto
{
    public string StreamId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTimeOffset? LatestOccurredAt { get; init; }
    public IReadOnlyList<TvAirTimedTextItemDto> Items { get; init; } = Array.Empty<TvAirTimedTextItemDto>();
}

public sealed class TvAirTunerStatusDto
{
    public string TunerName { get; init; } = string.Empty;
    public string BroadcastType { get; init; } = string.Empty;
    public string UsageKind { get; init; } = string.Empty;
    public bool IsInUse { get; init; }
    public bool IsFree { get; init; }
    public string? ReservationId { get; init; }
    public int? ReservationNumber { get; init; }
    public string? ServiceName { get; init; }
    public string? ProgramTitle { get; init; }
    public string? BonDriverFileName { get; init; }
    public string? Did { get; init; }
    public string? Role { get; init; }
    public int SlotIndex { get; init; }
    public int? ProcessId { get; init; }
    public DateTimeOffset? PlannedEndTime { get; init; }
}


public interface ITvAirBackupApi
{
    TvAirBackupInfoDto GetInfo();
    TvAirBackupInfoDto CreateSnapshot();
    TvAirBackupInfoDto CreateSnapshot(TvAirBackupSnapshotRequestDto request);
}

public sealed class TvAirBackupSnapshotRequestDto
{
    public string HandoverTitle { get; init; } = "TvAIr バックアップメモ";
    public IReadOnlyList<TvAirBackupHandoverEntryDto> HandoverEntries { get; init; } = Array.Empty<TvAirBackupHandoverEntryDto>();
    public IReadOnlyList<string> PurposeEntries { get; init; } = Array.Empty<string>();
}

public sealed class TvAirBackupHandoverEntryDto
{
    public string Section { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class TvAirBackupInfoDto
{
    public string DataDirectory { get; init; } = string.Empty;
    public string SnapshotDirectory { get; init; } = string.Empty;
    public string LatestBackupPath { get; init; } = string.Empty;
    public DateTime? LatestBackupAt { get; init; }
}

public interface ITvAirSettingsApi
{
    TvAirRecordingSettingsDto GetRecordingSettings();
    TvAirEpgSettingsDto GetEpgSettings();
    TvAirUiSettingsDto GetUiSettings();
    TvAirPluginHostSettingsDto GetPluginHostSettings();
    TvAirPathSettingsDto GetPaths();
}

public sealed class TvAirRecordingSettingsDto
{
    public int? GroundRecordingLimit { get; init; }
    public int? BsCsRecordingLimit { get; init; }
    public int? PreMarginSeconds { get; init; }
    public int? PostMarginSeconds { get; init; }
}

public sealed class TvAirEpgSettingsDto
{
    public string? GroundChannelFilePath { get; init; }
    public string? BsCsChannelFilePath { get; init; }
}

public sealed class TvAirUiSettingsDto
{
    public string? Theme { get; init; }
    public string? Appearance { get; init; }
    public string? AccentColor { get; init; }
    public string? CssScopeRoot { get; init; }
}

public sealed class TvAirPluginHostSettingsDto
{
    public string PluginsDirectory { get; init; } = string.Empty;
}

public sealed class TvAirPathSettingsDto
{
    public string AppDirectory { get; init; } = string.Empty;
    public string DataDirectory { get; init; } = string.Empty;
    public string PluginDataDirectory { get; init; } = string.Empty;
    public string PluginsDirectory { get; init; } = string.Empty;
}


public interface ITvAirSystemApi
{
    TvAirSystemStatusDto GetStatus();
    IReadOnlyList<TvAirWakePlanItemDto> ListWakePlan(TvAirWakePlanQueryDto? query = null);
}

public sealed class TvAirWakePlanQueryDto
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int? Limit { get; init; }
}

public sealed class TvAirWakePlanItemDto
{
    public DateTimeOffset At { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string? ReservationId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string TaskName { get; init; } = string.Empty;
}

public sealed class TvAirSystemStatusDto
{
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset Now { get; init; }
    public int ReservationCount { get; init; }
    public int ActiveRecordingCount { get; init; }
    public int TunerCount { get; init; }
    public int FreeTunerCount { get; init; }
    public int WakePlanCount { get; init; }
    public DateTimeOffset? NextWakeAt { get; init; }
}

public interface ITvAirNotificationsApi
{
    void Show(TvAirNotificationDto notification);
    TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState> Create(TvAIrPlugin.Notifications.CreatePluginNotificationRequest request);
    TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState> Update(TvAIrPlugin.Notifications.UpdatePluginNotificationRequest request);
    TvAIrPlugin.Runtime.TvAirOperationResult Close(string notificationInstanceId, long? expectedRevision = null);
    IReadOnlyList<TvAIrPlugin.Notifications.PluginNotificationState> List(bool includeClosed = false);
}

public sealed class TvAirNotificationDto
{
    public string Level { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public interface ITvAirWindowsApi
{
    void ShowToolWindow(TvAirToolWindowRequestDto request);
    TvAIrPlugin.Runtime.TvAirOperationResult RefreshToolWindow(TvAirToolWindowRefreshRequestDto request);
    TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowStatePatchResultDto> PatchToolWindow(TvAirToolWindowStatePatchRequestDto request);
    TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowPlacementPersistenceResultDto> SetToolWindowPlacementPersistence(TvAirToolWindowPlacementPersistenceRequestDto request);
    void CloseToolWindow(string windowId);
    IReadOnlyList<TvAirToolWindowStateDto> ListToolWindows();
}
public sealed class TvAirToolWindowRequestDto
{
    public string WindowDefinitionId { get; init; } = string.Empty;
    public string WindowId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ContentRoute { get; init; } = string.Empty;
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool ReuseExisting { get; init; } = true;
    public bool ActivateExisting { get; init; } = true;
}

public sealed class TvAirToolWindowRefreshRequestDto
{
    public string WindowId { get; init; } = string.Empty;
    public string? ContentRoute { get; init; }
}

/// <summary>
/// Host管理ToolWindowへ宣言的Runtime UI StatePatchを適用する要求。
/// StateRevisionはイベントsequence等、Plugin側の正本世代を単調増加値で渡す。
/// Hostは同一Windowで古いrevisionのpatchを後勝ちさせない。
/// </summary>
public sealed class TvAirToolWindowStatePatchRequestDto
{
    public string WindowId { get; init; } = string.Empty;
    public IReadOnlyList<TvAIrPlugin.Runtime.RuntimeUiPatch> UiPatches { get; init; } = Array.Empty<TvAIrPlugin.Runtime.RuntimeUiPatch>();
    public long StateRevision { get; init; }
}

public sealed class TvAirToolWindowStatePatchResultDto
{
    public string Outcome { get; init; } = string.Empty;
    public int RequestedPatchCount { get; init; }
    public int AppliedPatchCount { get; init; }
    public long StateRevision { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class TvAirToolWindowPlacementPersistenceRequestDto
{
    public string WindowDefinitionId { get; init; } = string.Empty;
    public bool RememberPlacement { get; init; }
    public bool ClearSavedPlacement { get; init; }
}

public sealed class TvAirToolWindowPlacementPersistenceResultDto
{
    public bool RememberPlacementApplied { get; init; }
    public bool SavedPlacementCleared { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}

public sealed class TvAirToolWindowStateDto
{
    public string WindowId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool IsOpen { get; init; }
}

public interface ITvAirPluginStorageApi
{
    string? ReadString(string section, string key);
    void WriteString(string section, string key, string value);
    int? ReadInt(string section, string key);
    void WriteInt(string section, string key, int value);
    bool? ReadBool(string section, string key);
    void WriteBool(string section, string key, bool value);
    IReadOnlyDictionary<string, string> ReadSection(string section);
}

public interface ITvAirEventsApi
{
    IDisposable Subscribe(TvAirEventType eventType, Action<TvAirEventDto> handler);
    IDisposable SubscribeAll(Action<TvAirEventDto> handler);
}

public enum TvAirEventType
{
    ReservationAdded = 0,
    ReservationUpdated = 1,
    ReservationRemoved = 2,
    RecordingStarted = 3,
    RecordingCompleted = 4,
    RecordingFailed = 5,
    EpgStarted = 6,
    EpgCompleted = 7,
    EpgCancelled = 8,
    EpgFailed = 9,
    TunerChanged = 10,
    ProgramGuideUpdated = 11,
    LogAdded = 12,
    SettingsChanged = 13,
    ReservationEnabled = 14,
    ReservationDisabled = 15,
    ReservationConflictChanged = 16,
    /// <summary>
    /// 録画結果確定と、存在する録画ファイルのTvAIr側使用終了・外部読取開始可能を示す境界。
    /// </summary>
    RecordingResultFinalized = 17,
    ViewerSessionChanged = 18,
    /// <summary>Host-managed Runtime ToolWindowのShown/Hidden/Closing/Closed確定事実。</summary>
    RuntimeWindowLifecycleChanged = 19,
    PluginPermissionChanged = 20,
    ViewerReservationChanged = 21,
    ExternalJobChanged = 22
}

/// <summary>共通イベント包絡。追加プロパティは既存プラグインとのバイナリ互換を維持する。</summary>
public sealed class TvAirEventDto
{
    /// <summary>同一事実の再配送で維持される一意ID。</summary>
    public string EventInstanceId { get; init; } = string.Empty;
    /// <summary>TvAIr本体内で事実が確定した時刻。</summary>
    public DateTimeOffset OccurredAt { get; init; }
    /// <summary>本体プロセス内で単調増加する配送順序。</summary>
    public long Sequence { get; init; }
    /// <summary>同一EntityId内で単調増加する版。</summary>
    public long EntityVersion { get; init; }
    public string? EntityId { get; init; }
    public string? OperationId { get; init; }
    public string? SourceOwnerId { get; init; }
    public long? DataRevision { get; init; }
    public string? ChangeKind { get; init; }
    /// <summary>OccurredAtと同じ値を返す互換プロパティ。</summary>
    public DateTimeOffset Timestamp { get; init; }
    public TvAirEventType EventType { get; init; }
    public string? ReservationId { get; init; }
    public string? ServiceName { get; init; }
    public string? ProgramTitle { get; init; }
    public TvAirReservationSnapshotDto? Reservation { get; init; }
    public TvAirReservationSnapshotDto? BeforeReservation { get; init; }
    public TvAirReservationSnapshotDto? AfterReservation { get; init; }
    public IReadOnlyList<string> ChangedFields { get; init; } = Array.Empty<string>();
    public TvAirRecordingResultDto? RecordingResult { get; init; }
    public IReadOnlyList<TvAirPluginSelectionBindingDto> PluginBindings { get; init; } = Array.Empty<TvAirPluginSelectionBindingDto>();
    /// <summary>ExternalJobChanged のときだけ設定されるHost管理Jobの現在状態。</summary>
    public TvAirExternalJobDto? ExternalJob { get; init; }
    /// <summary>Host-managed Runtime ToolWindow のライフサイクル確定事実。RuntimeWindowLifecycleChanged のときだけ設定される。</summary>
    public TvAirRuntimeWindowLifecycleDto? RuntimeWindowLifecycle { get; init; }
    /// <summary>Host-owned future Viewer Operation reservation state. Set for ViewerReservationChanged.</summary>
    public TvAirViewerReservationDto? ViewerReservation { get; init; }
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}


/// <summary>
/// Hostが正本として観測したRuntime ToolWindowのライフサイクル事実。
/// Hide/PreserveSessionで非表示になった場合はHidden、再表示はShown、Dispose終端はClosing→Closedとして配送される。
/// Windowの表示状態とBackgroundExecutionの継続/停止は別契約であり、PluginはCloseBehavior/BackgroundExecutionと合わせて解釈する。
/// </summary>
public sealed class TvAirRuntimeWindowLifecycleDto
{
    public string PluginId { get; init; } = string.Empty;
    public string WindowInstanceId { get; init; } = string.Empty;
    public string WindowDefinitionId { get; init; } = string.Empty;
    public string RouteSegment { get; init; } = string.Empty;
    public TvAIrPlugin.Windows.PluginWindowLifecycleState State { get; init; }
    public TvAIrPlugin.Windows.PluginWindowCloseBehavior CloseBehavior { get; init; }
    public TvAIrPlugin.Windows.PluginWindowBackgroundExecution BackgroundExecution { get; init; }
    public string Source { get; init; } = string.Empty;
    public bool SessionDisposed { get; init; }
}

public sealed class TvAirReservationSnapshotDto
{
    public string ReservationId { get; init; } = string.Empty;
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public ushort EventId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string EventTitle { get; init; } = string.Empty;
    public DateTimeOffset ScheduledStartTime { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public bool Enabled { get; init; }
    public bool HasConflict { get; init; }
    public string ReservationSource { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class TvAirRecordingResultDto
{
    public string ReservationId { get; init; } = string.Empty;
    public string RecordingId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string EventTitle { get; init; } = string.Empty;
    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public ushort EventId { get; init; }
    public DateTimeOffset ScheduledStartTime { get; init; }
    public string Genre { get; init; } = string.Empty;
    public string GenreCodes { get; init; } = string.Empty;
    public DateTimeOffset? ActualStartTime { get; init; }
    public DateTimeOffset? ActualEndTime { get; init; }
    public string Result { get; init; } = string.Empty;
    public string EndReason { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public bool? FileCreated { get; init; }
    public long? Drop { get; init; }
    public long? Error { get; init; }
    public long? Scramble { get; init; }
    public bool QualityDataAvailable { get; init; }
    public string QualityCompleteness { get; init; } = string.Empty;
    public string QualitySource { get; init; } = string.Empty;
    public string ResourceReleaseState { get; init; } = string.Empty;
    /// <summary>
    /// true の場合、このRecordingIdに対するTvAIr側の録画結果は確定済み。
    /// FileCreated == true かつ有効なFilePathがある場合、TvAIrの録画処理による
    /// 当該ファイルへの書き込み・使用も終了しており、外部後処理は読み取りを開始できる。
    /// </summary>
    public bool ResultFinalized { get; init; }
    /// <summary>TvAIr本体がこの録画を正常完了として確定した型付き正本値。</summary>
    public bool Succeeded { get; init; }
}

/// <summary>
/// Host管理の外部ジョブ。実行プロセスと実行中の子プロセスの停止はHostが所有する。
/// Host通常終了・設定再起動では受付を閉じ、実行中ジョブを取消し、終了と出力読取の完了を
/// 上限付きで待機する。PluginはHost終了のためだけにCancelを呼ぶ必要はない。
/// CancelはPlugin自身による個別取消要求であり、trueは停止完了ではなく要求受付を示す。
/// OSによる強制終了・クラッシュ時の自動復旧/再投入はこの契約に含まない。
/// </summary>
public interface ITvAirExternalJobsApi
{
    TvAirExternalJobDto Enqueue(TvAirExternalJobRequestDto request);
    TvAirExternalJobDto? Get(string jobId);
    IReadOnlyList<TvAirExternalJobDto> List(TvAirExternalJobQueryDto? query = null);
    bool Cancel(string jobId);
}

public enum TvAirExternalJobKind
{
    EncodeRecording,
    InspectRecording,
    ExportReport
}

public enum TvAirExternalJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed class TvAirExternalProcessSpecDto
{
    public string ExecutablePath { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
}

public sealed class TvAirExternalJobRequestDto
{
    public TvAirExternalJobKind Kind { get; init; }
    public string? ReservationId { get; init; }
    public string? RecordingId { get; init; }
    public string ClientRequestId { get; init; } = string.Empty;
    public TvAirExternalProcessSpecDto? Process { get; init; }
}

public sealed class TvAirExternalJobQueryDto
{
    public TvAirExternalJobKind? Kind { get; init; }
    public TvAirExternalJobState? StateCode { get; init; }
    /// <summary>互換投影。新規コードはStateCodeを使用する。</summary>
    public string? State { get; init; }
    public string? RecordingId { get; init; }
    public string? ClientRequestId { get; init; }
}

public sealed class TvAirExternalJobDto
{
    public string JobId { get; init; } = string.Empty;
    public TvAirExternalJobKind Kind { get; init; }
    public TvAirExternalJobState StateCode { get; init; }
    /// <summary>StateCodeから生成される互換投影。</summary>
    public string State { get; init; } = string.Empty;
    public string? ReservationId { get; init; }
    public string? RecordingId { get; init; }
    public string? ClientRequestId { get; init; }
    public bool Accepted { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? FailureCode { get; init; }
    public string? Message { get; init; }
    public string? StandardOutputTail { get; init; }
    public string? StandardErrorTail { get; init; }
}

public interface ITvAirHostsApi
{
    TvAirHostInfoDto GetSelf();
    IReadOnlyList<TvAirHostInfoDto> ListPeers();
    TvAirHostStatusDto? GetPeerStatus(string hostId);
}

public sealed class TvAirHostInfoDto
{
    public string HostId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsSelf { get; init; }
}

public sealed class TvAirHostStatusDto
{
    public string HostId { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public string? State { get; init; }
}

public interface ITvAirPluginsApi
{
    IReadOnlyList<TvAirLoadedPluginDto> ListLoaded();
    IReadOnlyList<TvAirAnalysisPluginDto> ListAnalysisPlugins();
    IReadOnlyList<TvAirAnalysisExecutionDto> Analyze(AnalysisContext context);
}

public sealed class TvAirAnalysisPluginDto
{
    public string PluginName { get; init; } = string.Empty;
    public string PluginVersion { get; init; } = string.Empty;
}

public sealed class TvAirAnalysisExecutionDto
{
    public string PluginName { get; init; } = string.Empty;
    public string PluginVersion { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public AnalysisResult Result { get; init; } = new();
    public string? Error { get; init; }
}

public sealed class TvAirLoadedPluginDto
{
    public string PluginId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool IsLoaded { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> ContractKinds { get; init; } = Array.Empty<string>();
    public string? SdkContractVersion { get; init; }
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PluginPermission> Permissions { get; init; } = Array.Empty<PluginPermission>();
    public bool UsesRuntimeContext { get; init; }
}
