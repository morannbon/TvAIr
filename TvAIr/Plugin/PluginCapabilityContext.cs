using System.Globalization;
using System.Text.Json;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Epg.Projection;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;

namespace TvAIr.Plugin;

/// <summary>
/// New generic plugin capability context. This is not a plugin-specific API surface.
/// Runtime plugins receive this capability surface through ITvAirPluginContext.
/// </summary>
internal sealed class PluginCapabilityContext : ITvAirPluginContext
{
    public PluginCapabilityContext(
        LogRepository log,
        PluginScopedServiceFactory scopedServices,
        ExternalEpgSourceStore externalEpgSources,
        ProgramProjectionReservationSyncService projectionReservationSync,
        LogPresentationStore logPresentationStore,
        PluginReadModelSource readModels,
        PluginReservationOperationService reservationOperations,
        PluginReservationPlanningService reservationPlanning,
        PluginSystemReadService systemReads,
        PluginPresentationReadService presentationReads,
        PluginOperationalReadService operationalReads,
        ExternalTunerLeaseService externalTuners,
        ViewerSessionRegistry viewerSessions,
        ViewerOperationService viewerOperations,
        TimedTextStreamStore timedTextStreams,
        TvTestSettings tvTestSettings,
        IReadOnlyList<TunerProfile> tunerProfiles,
        EpgScheduler epgScheduler,
        PluginRegistry registry,
        IniSettingsService ini,
        PluginWindowSessionStore windowSessions,
        PluginToolWindowHostService toolWindows,
        PluginTypedEventHub typedEvents,
        RecordingResultStore recordingResults,
        PlaybackProgressStore playbackProgress,
        ReservationScheduler reservationScheduler,
        string pluginId,
        string pluginDisplayName,
        string pluginsDirectory,
        IReadOnlyCollection<PluginPermission> resolvedPermissions)
    {
        var normalizedPluginId = scopedServices.NormalizePluginId(pluginId);
        var permissionGate = new CapabilityPermissionGate(normalizedPluginId, pluginDisplayName, log, resolvedPermissions);
        Logs = new LogsApi(scopedServices.CreateLog(normalizedPluginId, pluginDisplayName), operationalReads, permissionGate);
        LogPresentation = new LogPresentationApi(normalizedPluginId, logPresentationStore, log, permissionGate);
        RecordingQualityPresentation = new RecordingQualityPresentationApi(normalizedPluginId, logPresentationStore, log, permissionGate);
        Reservations = new ReservationsApi(normalizedPluginId, readModels, reservationOperations, reservationPlanning, permissionGate);
        Rules = new RulesApi(readModels, permissionGate);
        Recordings = new RecordingsApi(readModels, recordingResults, reservationScheduler, permissionGate);
        RecordingFiles = new RecordingFilesApi(readModels, recordingResults, permissionGate);
        RecordingInspection = new RecordingInspectionApi(recordingResults, permissionGate);
        PlaybackProgress = new PlaybackProgressApi(playbackProgress, permissionGate);
        MediaInsights = new MediaInsightsApi(readModels, recordingResults, playbackProgress, permissionGate);
        ContentDiscovery = new ContentDiscoveryApi(readModels, recordingResults, playbackProgress, permissionGate);
        ExternalProgramSource = new ExternalProgramSourceApi(externalEpgSources, projectionReservationSync, typedEvents, normalizedPluginId, log, permissionGate);
        ProgramGuide = new ProgramGuideApi(readModels, ExternalProgramSource, permissionGate);
        ProgramGuideEvents = new ProgramGuideEventsApi(operationalReads, permissionGate);
        Epg = new EpgApi(epgScheduler, operationalReads, permissionGate);
        Channels = new ChannelsApi(readModels, permissionGate);
        ServiceMetadata = new ServiceMetadataApi(readModels, permissionGate);
        Tuners = new TunersApi(readModels, permissionGate);
        Viewers = new ViewersApi(normalizedPluginId, pluginDisplayName, externalTuners, viewerSessions, viewerOperations, readModels, tvTestSettings, ini, tunerProfiles, typedEvents, permissionGate);
        TimedTextStreams = new TimedTextStreamsApi(normalizedPluginId, timedTextStreams, log);
        Backup = UnavailableBackupApi.Instance;
        var pluginDataDirectory = scopedServices.CreateFiles(normalizedPluginId).RootDirectory;
        Settings = new SettingsApi(
            presentationReads,
            ini,
            tunerProfiles,
            AppContext.BaseDirectory,
            scopedServices.DataDirectory,
            pluginDataDirectory,
            pluginsDirectory,
            permissionGate);
        System = new SystemApi(systemReads, permissionGate);
        Notifications = new NotificationsApi(normalizedPluginId, log, permissionGate);
        Windows = new WindowsApi(normalizedPluginId, pluginDisplayName, ini, registry, windowSessions, toolWindows, log, permissionGate);
        Storage = new PluginStorageApi(
            scopedServices.CreateRuntimeStorage(normalizedPluginId),
            permissionGate);
        Events = new EventsApi(normalizedPluginId, log, typedEvents, permissionGate);
        ExternalJobs = new ExternalJobsApi(permissionGate);
        Hosts = new HostsApi(permissionGate);
        Plugins = new PluginsApi(registry, permissionGate);
    }

    public ITvAirLogsApi Logs { get; }
    public ITvAirLogPresentationApi LogPresentation { get; }
    public ITvAirRecordingQualityPresentationApi RecordingQualityPresentation { get; }
    public ITvAirReservationsApi Reservations { get; }
    public ITvAirRulesApi Rules { get; }
    public ITvAirRecordingsApi Recordings { get; }
    public ITvAirRecordingFilesApi RecordingFiles { get; }
    public ITvAirRecordingInspectionApi RecordingInspection { get; }
    public ITvAirPlaybackProgressApi PlaybackProgress { get; }
    public ITvAirMediaInsightsApi MediaInsights { get; }
    public ITvAirContentDiscoveryApi ContentDiscovery { get; }
    public ITvAirProgramGuideApi ProgramGuide { get; }
    public ITvAirExternalProgramSourceApi ExternalProgramSource { get; }
    public ITvAirProgramGuideEventsApi ProgramGuideEvents { get; }
    public ITvAirEpgApi Epg { get; }
    public ITvAirChannelsApi Channels { get; }
    public ITvAirServiceMetadataApi ServiceMetadata { get; }
    public ITvAirTunersApi Tuners { get; }
    public ITvAirViewersApi Viewers { get; }
    public ITvAirTimedTextStreamsApi TimedTextStreams { get; }
    public ITvAirBackupApi Backup { get; }
    public ITvAirSettingsApi Settings { get; }
    public ITvAirSystemApi System { get; }
    public ITvAirNotificationsApi Notifications { get; }
    public ITvAirWindowsApi Windows { get; }
    public ITvAirPluginStorageApi Storage { get; }
    public ITvAirEventsApi Events { get; }
    public ITvAirExternalJobsApi ExternalJobs { get; }
    public ITvAirHostsApi Hosts { get; }
    public ITvAirPluginsApi Plugins { get; }


    private sealed class CapabilityPermissionGate
    {
        private readonly string _pluginId;
        private readonly string _pluginDisplayName;
        private readonly LogRepository _log;
        private readonly HashSet<PluginPermission> _permissions;

        public CapabilityPermissionGate(string pluginId, string pluginDisplayName, LogRepository log, IReadOnlyCollection<PluginPermission> permissions)
        {
            _pluginId = pluginId;
            _pluginDisplayName = string.IsNullOrWhiteSpace(pluginDisplayName) ? _pluginId : pluginDisplayName.Trim();
            _log = log;
            _permissions = new HashSet<PluginPermission>(permissions ?? Array.Empty<PluginPermission>());
        }

        public void Require(string apiName, params PluginPermission[] requiredAny)
        {
            if (requiredAny.Length == 0) return;
            if (requiredAny.Any(p => _permissions.Contains(p))) return;
            var required = string.Join("|", requiredAny.Select(p => p.ToString()));
            _log.Add("PLUGIN_BOUNDARY_GATE", _pluginDisplayName,
                $"boundary=capability api={SafePluginLog(apiName)} result=DENIED reason=missing_permission required={SafePluginLog(required)} pluginId={SafePluginLog(_pluginId)} rule=plugin_boundary_gate");
            throw new UnauthorizedAccessException($"Plugin capability permission is required: {required}");
        }

        public bool Has(PluginPermission permission) => _permissions.Contains(permission);
    }


    private sealed class LogPresentationApi(string pluginId, LogPresentationStore store, LogRepository log, CapabilityPermissionGate permissions) : ITvAirLogPresentationApi
    {
        public TvAirLogPresentationReplaceResultDto ReplaceSnapshot(TvAirLogPresentationSnapshotDto snapshot)
        {
            permissions.Require(nameof(LogPresentationApi), PluginPermission.WriteLogPresentation);
            var result = store.ReplaceLogSnapshot(pluginId, snapshot);
            log.Add("PLUGIN_LOG_PRESENTATION", pluginId,
                $"result={(result.Accepted ? "OK" : "NG")} viewKey={result.ViewKey} entries={result.EntryCount} rule=log_presentation_capability");
            return result;
        }

        public TvAirLogPresentationReplaceResultDto SetPolicy(TvAirLogPresentationPolicyDto policy)
        {
            permissions.Require(nameof(LogPresentationApi), PluginPermission.WriteLogPresentation);
            var result = store.SetLogPolicy(pluginId, policy);
            log.Add("PLUGIN_LOG_PRESENTATION_POLICY", pluginId,
                $"result={(result.Accepted ? "OK" : "NG")} viewKey={result.ViewKey} enabled={policy.Enabled} detailKeys={policy.DetailKeys?.Count ?? 0} layout={policy.Layout} priority={policy.Priority} rule=user_operation_log_projection_policy");
            return result;
        }

        public void ClearPolicy(string? viewKey = null)
        {
            permissions.Require(nameof(LogPresentationApi), PluginPermission.WriteLogPresentation);
            store.ClearLogPolicy(pluginId, viewKey);
            log.Add("PLUGIN_LOG_PRESENTATION_POLICY", pluginId,
                $"result=CLEARED viewKey={(string.IsNullOrWhiteSpace(viewKey) ? "*" : viewKey)} rule=user_operation_log_projection_policy");
        }

        public void ClearSnapshot(string? viewKey = null)
        {
            permissions.Require(nameof(LogPresentationApi), PluginPermission.WriteLogPresentation);
            store.ClearLogSnapshot(pluginId, viewKey);
            log.Add("PLUGIN_LOG_PRESENTATION", pluginId,
                $"result=CLEARED viewKey={(string.IsNullOrWhiteSpace(viewKey) ? "*" : viewKey)} rule=log_presentation_capability");
        }

        public TvAirLogPresentationSnapshotDto? GetActiveSnapshot(string viewKey)
        {
            permissions.Require(nameof(LogPresentationApi), PluginPermission.WriteLogPresentation);
            return store.GetActiveLogSnapshot(viewKey)?.Snapshot;
        }

        public TvAirLogPresentationPolicyDto? GetActivePolicy(string viewKey)
        {
            permissions.Require(nameof(LogPresentationApi), PluginPermission.WriteLogPresentation);
            return store.GetActiveLogPolicy(viewKey)?.Policy;
        }
    }

    private sealed class RecordingQualityPresentationApi(string pluginId, LogPresentationStore store, LogRepository log, CapabilityPermissionGate permissions) : ITvAirRecordingQualityPresentationApi
    {
        public TvAirRecordingQualityReplaceResultDto ReplaceSnapshot(TvAirRecordingQualitySnapshotDto snapshot)
        {
            permissions.Require(nameof(RecordingQualityPresentationApi), PluginPermission.WriteRecordingQualityPresentation);
            var result = store.ReplaceRecordingQualitySnapshot(pluginId, snapshot);
            log.Add("PLUGIN_RECORDING_QUALITY_PRESENTATION", pluginId,
                $"result={(result.Accepted ? "OK" : "NG")} items={result.ItemCount} rule=recording_quality_presentation_capability");
            return result;
        }

        public void ClearSnapshot()
        {
            permissions.Require(nameof(RecordingQualityPresentationApi), PluginPermission.WriteRecordingQualityPresentation);
            store.ClearRecordingQualitySnapshot(pluginId);
            log.Add("PLUGIN_RECORDING_QUALITY_PRESENTATION", pluginId,
                "result=CLEARED rule=recording_quality_presentation_capability");
        }

        public TvAirRecordingQualitySnapshotDto? GetActiveSnapshot()
        {
            permissions.Require(nameof(RecordingQualityPresentationApi), PluginPermission.WriteRecordingQualityPresentation);
            return store.GetActiveRecordingQualitySnapshot()?.Snapshot;
        }
    }

    private sealed class LogsApi(PluginLogService pluginLog, PluginOperationalReadService operationalReads, CapabilityPermissionGate permissions) : ITvAirLogsApi
    {
        public IReadOnlyList<TvAirLogEntryDto> Query(TvAirLogQueryDto? query = null)
        {
            permissions.Require(nameof(LogsApi), PluginPermission.ReadLogs);
            query ??= new TvAirLogQueryDto();
            var limit = Math.Clamp(query.Limit ?? 500, 1, 5000);
            IEnumerable<LogEntry> entries = operationalReads.GetAllLogs();
            if (query.From.HasValue) entries = entries.Where(e => e.CreatedAt >= query.From.Value.LocalDateTime);
            if (query.To.HasValue) entries = entries.Where(e => e.CreatedAt <= query.To.Value.LocalDateTime);
            if (!string.IsNullOrWhiteSpace(query.Category)) entries = entries.Where(e => Contains(e.Event, query.Category));
            if (!string.IsNullOrWhiteSpace(query.ResultCode)) entries = entries.Where(e => Contains(e.Message, query.ResultCode));
            if (!string.IsNullOrWhiteSpace(query.ReservationId)) entries = entries.Where(e => Contains(e.Title, query.ReservationId) || Contains(e.Message, query.ReservationId));
            return entries.OrderByDescending(e => e.CreatedAt).Take(limit).Select(e => new TvAirLogEntryDto
            {
                Timestamp = new DateTimeOffset(e.CreatedAt),
                Level = "INFO",
                Category = e.Event,
                Target = e.Title,
                Message = e.Message,
                ResultCode = ExtractToken(e.Message, "result=") ?? ExtractToken(e.Message, "resultCode="),
                OperationId = ExtractToken(e.Message, "operationId="),
                Details = ParseKeyValueDetails(e.Message)
            }).ToList();
        }

        public void Write(TvAirLogWriteDto entry)
        {
            permissions.Require(nameof(LogsApi), PluginPermission.WriteLogs);
            ArgumentNullException.ThrowIfNull(entry);
            pluginLog.Write(entry.Level, entry.Category, entry.Message);
        }

        public void AddTimeline(string title, string message)
        {
            permissions.Require(nameof(LogsApi), PluginPermission.WriteLogs);
            pluginLog.AddTimeline(title, message);
        }

        public void AddAudit(string action, string message)
        {
            permissions.Require(nameof(LogsApi), PluginPermission.WriteLogs);
            pluginLog.AddAudit(action, message);
        }
    }


    private sealed class RulesApi(PluginReadModelSource readModels, CapabilityPermissionGate permissions) : ITvAirRulesApi
    {
        public IReadOnlyList<TvAirKeywordRuleDto> ListKeywordRules(TvAirRuleQueryDto? query = null)
        {
            permissions.Require(nameof(RulesApi), PluginPermission.ReadKeywordRules);
            query ??= new TvAirRuleQueryDto();
            IEnumerable<KeywordRule> rules = readModels.GetKeywordRules();
            if (query.Enabled.HasValue) rules = rules.Where(rule => rule.Enabled == query.Enabled.Value);
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                var keyword = query.Keyword.Trim();
                rules = rules.Where(rule => Contains(rule.Name, keyword) || Contains(rule.Pattern, keyword) || Contains(rule.ExcludePattern, keyword));
            }
            var limit = Math.Clamp(query.Limit ?? 500, 1, 5000);
            return rules.OrderBy(rule => rule.SortOrder).ThenBy(rule => rule.Id).Take(limit).Select(rule => new TvAirKeywordRuleDto
            {
                RuleId = rule.Id,
                Name = rule.Name,
                Pattern = rule.Pattern,
                ExcludePattern = rule.ExcludePattern,
                UseRegex = rule.UseRegex,
                SearchTitle = rule.SearchTitle,
                SearchOutline = rule.SearchOutline,
                SearchDetail = rule.SearchDetail,
                SearchCast = rule.SearchCast,
                Enabled = rule.Enabled,
                UseAllChannels = rule.UseAllChannels,
                TargetServices = rule.TargetServices,
                TargetGenres = rule.TargetGenres,
                TargetDays = rule.TargetDays,
                UseTimeRange = rule.UseTimeRange,
                StartTime = rule.StartTime,
                EndTime = rule.EndTime,
                ExpiresOn = rule.ExpiresOn,
                SortOrder = rule.SortOrder,
                CreatedAt = new DateTimeOffset(rule.CreatedAt),
                UpdatedAt = new DateTimeOffset(rule.UpdatedAt)
            }).ToList();
        }

        public IReadOnlyList<TvAirProgramRuleDto> ListProgramRules(TvAirRuleQueryDto? query = null)
        {
            permissions.Require(nameof(RulesApi), PluginPermission.ReadProgramRules);
            query ??= new TvAirRuleQueryDto();
            IEnumerable<ProgramRule> rules = readModels.GetProgramRules();
            if (query.Enabled.HasValue) rules = rules.Where(rule => rule.Enabled == query.Enabled.Value);
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                var keyword = query.Keyword.Trim();
                rules = rules.Where(rule => Contains(rule.Name, keyword));
            }
            var limit = Math.Clamp(query.Limit ?? 500, 1, 5000);
            return rules.OrderBy(rule => rule.DayOfWeek).ThenBy(rule => rule.StartTime).ThenBy(rule => rule.Id).Take(limit).Select(rule => new TvAirProgramRuleDto
            {
                RuleId = rule.Id,
                Name = rule.Name,
                DayOfWeek = rule.DayOfWeek,
                StartTime = rule.StartTime,
                EndTime = rule.EndTime,
                NetworkId = rule.NetworkId,
                TransportStreamId = rule.TransportStreamId,
                ServiceId = rule.ServiceId,
                ExpiresOn = rule.ExpiresOn,
                Enabled = rule.Enabled,
                CreatedAt = new DateTimeOffset(rule.CreatedAt),
                UpdatedAt = new DateTimeOffset(rule.UpdatedAt)
            }).ToList();
        }
    }

    private static bool IsTerminalReservationHistoryRow(Reservation reservation)
        => reservation.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed;

    private sealed class ReservationsApi(string pluginId, PluginReadModelSource readModels, PluginReservationOperationService reservationOperations, PluginReservationPlanningService reservationPlanning, CapabilityPermissionGate permissions) : ITvAirReservationsApi
    {
        public IReadOnlyList<TvAirReservationDto> List(TvAirReservationQueryDto? query = null)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.ReadReservations);
            query ??= new TvAirReservationQueryDto();
            IEnumerable<Reservation> rows = readModels.GetReservations();
            if (!query.IncludeSystemEntries) rows = rows.Where(r => r.Source != ReservationSource.Epg);
            if (query.From.HasValue) rows = rows.Where(r => r.EndTime > query.From.Value.LocalDateTime);
            if (query.To.HasValue) rows = rows.Where(r => r.StartTime < query.To.Value.LocalDateTime);
            if (!string.IsNullOrWhiteSpace(query.ServiceName)) rows = rows.Where(r => Contains(readModels.ResolveCurrentServiceName(r), query.ServiceName));
            if (!string.IsNullOrWhiteSpace(query.Source)) rows = rows.Where(r => string.Equals(r.Source.ToString(), query.Source.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.Status)) rows = rows.Where(r => string.Equals(r.Status.ToString(), query.Status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (query.Enabled.HasValue) rows = rows.Where(r => r.IsEnabled == query.Enabled.Value);
            if (query.Conflicted.HasValue) rows = rows.Where(r => r.IsConflicted == query.Conflicted.Value);
            return rows.OrderBy(r => r.StartTime).Select(r => ToDto(r, readModels)).ToList();
        }

        public IReadOnlyList<TvAirReservationDto> ListHistory(TvAirReservationHistoryQueryDto? query = null)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.ReadReservations);
            query ??= new TvAirReservationHistoryQueryDto();
            IEnumerable<Reservation> rows = readModels.GetReservations()
                .Where(IsTerminalReservationHistoryRow);
            if (!query.IncludeSystemEntries) rows = rows.Where(r => r.Source != ReservationSource.Epg);
            if (query.From.HasValue) rows = rows.Where(r => r.EndTime >= query.From.Value.LocalDateTime);
            if (query.To.HasValue) rows = rows.Where(r => r.StartTime < query.To.Value.LocalDateTime);
            if (!string.IsNullOrWhiteSpace(query.ReservationId))
            {
                var id = ParseReservationId(query.ReservationId);
                rows = id.HasValue ? rows.Where(r => r.Id == id.Value) : Enumerable.Empty<Reservation>();
            }
            if (!string.IsNullOrWhiteSpace(query.ServiceName)) rows = rows.Where(r => Contains(readModels.ResolveCurrentServiceName(r), query.ServiceName));
            if (!string.IsNullOrWhiteSpace(query.Source)) rows = rows.Where(r => string.Equals(r.Source.ToString(), query.Source.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.Status)) rows = rows.Where(r => string.Equals(r.Status.ToString(), query.Status.Trim(), StringComparison.OrdinalIgnoreCase));
            var take = Math.Clamp(query.Limit ?? 1000, 1, 10000);
            return rows.OrderByDescending(r => r.EndTime).Take(take).Select(r => ToDto(r, readModels)).ToList();
        }

        public TvAirReservationDto? Get(string reservationId)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.ReadReservations);
            var id = ParseReservationId(reservationId);
            if (!id.HasValue) return null;
            return readModels.GetReservations().FirstOrDefault(r => r.Id == id.Value) is { } r ? ToDto(r, readModels) : null;
        }

        public IReadOnlyList<TvAirReservationConflictDto> ListConflicts()
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.ReadReservations);
            return reservationPlanning.GetConflicts().Select(ToConflictDto).ToList();
        }

        public TvAirReservationPreviewDto Preview(TvAirReservationPreviewRequestDto request)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.PreviewAllocation);
            ArgumentNullException.ThrowIfNull(request);
            if (!TryToServiceIdentity(request.NetworkId, request.TransportStreamId, request.ServiceId, out var networkId, out var transportStreamId, out var serviceId))
            {
                return new TvAirReservationPreviewDto
                {
                    CanReserve = false,
                    HasConflict = true,
                    Reason = "NID/TSID/SIDは0～65535の完全な局identityで指定してください。"
                };
            }
            var preview = reservationPlanning.PreviewReservation(new PluginReservationPlanningDraft(
                networkId,
                transportStreamId,
                serviceId,
                request.Start.LocalDateTime,
                request.End.LocalDateTime,
                ParseReservationId(request.ChainPreviousReservationId)));
            return new TvAirReservationPreviewDto
            {
                CanReserve = preview.CanReserve,
                HasConflict = preview.HasConflict,
                Reason = preview.Reason,
                SuggestedTunerName = preview.SuggestedTunerName,
                Conflicts = preview.Conflicts.Select(ToConflictDto).ToList(),
                ChainCandidates = preview.ChainCandidates.Select(ToChainDto).ToList()
            };
        }

        public IReadOnlyList<TvAirChainCandidateDto> ListChainCandidates(TvAirChainCandidateQueryDto? query = null)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.ReadReservations);
            ushort? networkId = null;
            ushort? transportStreamId = null;
            ushort? serviceId = null;
            var hasAnyIdentityFilter = query?.NetworkId.HasValue == true || query?.TransportStreamId.HasValue == true || query?.ServiceId.HasValue == true;
            if (hasAnyIdentityFilter)
            {
                if (query?.NetworkId is not int rawNetworkId
                    || query.TransportStreamId is not int rawTransportStreamId
                    || query.ServiceId is not int rawServiceId
                    || !TryToServiceIdentity(rawNetworkId, rawTransportStreamId, rawServiceId, out var parsedNetworkId, out var parsedTransportStreamId, out var parsedServiceId))
                {
                    return Array.Empty<TvAirChainCandidateDto>();
                }
                networkId = parsedNetworkId;
                transportStreamId = parsedTransportStreamId;
                serviceId = parsedServiceId;
            }
            return reservationPlanning.GetChainCandidates(new PluginChainCandidateQuery(
                    query?.From?.LocalDateTime,
                    query?.To?.LocalDateTime,
                    networkId,
                    transportStreamId,
                    serviceId))
                .Select(ToChainDto)
                .ToList();
        }

        public TvAirChainPreviewDto PreviewChain(TvAirReservationPreviewRequestDto request)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.PreviewAllocation);
            ArgumentNullException.ThrowIfNull(request);
            if (!TryToServiceIdentity(request.NetworkId, request.TransportStreamId, request.ServiceId, out var networkId, out var transportStreamId, out var serviceId))
            {
                return new TvAirChainPreviewDto { CanChain = false, Message = "NID/TSID/SIDは0～65535の完全な局identityで指定してください。" };
            }
            var preview = reservationPlanning.PreviewChain(new PluginReservationPlanningDraft(
                networkId,
                transportStreamId,
                serviceId,
                request.Start.LocalDateTime,
                request.End.LocalDateTime,
                ParseReservationId(request.ChainPreviousReservationId)));
            return new TvAirChainPreviewDto
            {
                CanChain = preview.CanChain,
                Message = preview.Message,
                ChainInfo = preview.ChainInfo is null ? null : ToChainDto(preview.ChainInfo)
            };
        }

        public TvAirReservationOperationResultDto Add(TvAirReservationCreateDto request)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.WriteReservations, PluginPermission.ManageReservations);
            ArgumentNullException.ThrowIfNull(request);
            if (!TryToUShort(request.NetworkId, out var networkId)
                || !TryToUShort(request.TransportStreamId, out var transportStreamId)
                || !TryToUShort(request.ServiceId, out var serviceId)
                || !TryToUShort(request.EventId, out var eventId))
            {
                return new TvAirReservationOperationResultDto
                {
                    Success = false,
                    Message = "NID、TSID、SID、EIDは0～65535で指定してください。"
                };
            }

            var previousId = ParseReservationId(request.ChainPreviousReservationId);
            var result = reservationOperations.Add(pluginId, new PluginReservationMutationDraft(
                networkId,
                transportStreamId,
                serviceId,
                eventId,
                request.ProgramTitle,
                request.ServiceName,
                request.Start.LocalDateTime,
                request.End.LocalDateTime,
                request.PreMarginMinutes,
                request.PostMarginMinutes,
                request.ChannelArgument,
                request.AllowChain,
                previousId,
                NormalizeReservationIntent(request.Intent)));
            return ToOperationResult(result);
        }

        public TvAirReservationOperationResultDto Update(TvAirReservationUpdateDto request)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.WriteReservations, PluginPermission.ManageReservations);
            ArgumentNullException.ThrowIfNull(request);
            var id = ParseReservationId(request.ReservationId);
            if (!id.HasValue)
                return new TvAirReservationOperationResultDto { Success = false, ReservationId = request.ReservationId, Message = "予約IDが正しくありません。" };
            return ToOperationResult(reservationOperations.Update(pluginId, id.Value, request.Enabled));
        }

        public TvAirReservationOperationResultDto Delete(TvAirReservationDeleteDto request)
        {
            permissions.Require(nameof(ReservationsApi), PluginPermission.WriteReservations, PluginPermission.ManageReservations);
            ArgumentNullException.ThrowIfNull(request);
            if (request.Force)
                permissions.Require(nameof(ReservationsApi), PluginPermission.ManageReservations);
            var id = ParseReservationId(request.ReservationId);
            if (!id.HasValue)
                return new TvAirReservationOperationResultDto { Success = false, ReservationId = request.ReservationId, Message = "予約IDが正しくありません。" };
            return ToOperationResult(reservationOperations.Delete(pluginId, id.Value, request.Force));
        }

        private static TvAirReservationConflictDto ToConflictDto(PluginReservationConflict conflict)
            => new()
            {
                ReservationId = $"R{conflict.ReservationId}",
                NetworkId = conflict.NetworkId,
                TransportStreamId = conflict.TransportStreamId,
                ServiceId = conflict.ServiceId,
                ProgramTitle = conflict.Title,
                ServiceName = conflict.ServiceName,
                Start = new DateTimeOffset(conflict.StartTime),
                End = new DateTimeOffset(conflict.EndTime),
                Reason = conflict.Reason
            };

        private static TvAirChainCandidateDto ToChainDto(PluginChainCandidate candidate)
            => new()
            {
                PreviousReservationId = $"R{candidate.PreviousReservationId}",
                CurrentReservationId = $"R{candidate.CurrentReservationId}",
                CurrentProgramId = candidate.CurrentProgramId,
                SameTuner = candidate.SameTuner,
                LossTarget = candidate.LossTarget,
                LossPart = candidate.LossPart,
                LossDescription = candidate.LossDescription,
                IsAllowed = candidate.IsAllowed
            };

        private static TvAirReservationOperationResultDto ToOperationResult(PluginReservationMutationResult result)
            => new()
            {
                Success = result.Success,
                ReservationId = result.ReservationId.HasValue ? FormatReservationId(result.ReservationId.Value) : null,
                Message = result.Message
            };
    }

    private sealed class RecordingsApi(PluginReadModelSource readModels, RecordingResultStore recordingResults, ReservationScheduler reservationScheduler, CapabilityPermissionGate permissions) : ITvAirRecordingsApi
    {
        public IReadOnlyList<TvAirRecordingSessionDto> ListActive()
        {
            permissions.Require(nameof(RecordingsApi), PluginPermission.ReadRecordingStatus);
            var reservations = readModels.GetReservations().ToDictionary(r => r.Id);
            return reservationScheduler.GetActiveRecordingSessions()
                .Select(session =>
                {
                    reservations.TryGetValue(session.ReservationId, out var reservation);
                    return new TvAirRecordingSessionDto
                    {
                        RecordingSessionId = $"recording:{session.ReservationId}:{session.OperationId:N}",
                        ReservationId = FormatReservationId(session.ReservationId),
                        OperationId = session.OperationId.ToString("D"),
                        ProcessId = session.ProcessId,
                        ServiceName = reservation is null ? string.Empty : readModels.ResolveCurrentServiceName(reservation),
                        ProgramTitle = reservation?.Title ?? string.Empty,
                        NetworkId = reservation?.NetworkId ?? 0,
                        TransportStreamId = reservation?.TransportStreamId ?? 0,
                        ServiceId = reservation?.ServiceId ?? 0,
                        // RECORDING_LIFECYCLE_PROJECTION_SINGLE_SOURCE:
                        // Active sessionの表示時刻をprojection時刻(DateTime.Now)で捏造しない。
                        // lifecycle正本のRecordingStartedAtを使い、旧データ等で欠落時だけ予約StartTimeへ退避する。
                        Start = new DateTimeOffset(reservation?.RecordingStartedAt ?? reservation?.StartTime ?? session.PlannedEndTime),
                        ScheduledEnd = new DateTimeOffset(session.PlannedEndTime),
                        TunerName = session.TunerName,
                        Did = session.Did,
                        State = session.State,
                        OutputFilePath = session.RecordingFilePath
                    };
                })
                .OrderBy(session => session.Start)
                .ToList();
        }

        public IReadOnlyList<TvAirRecordingHistoryDto> ListHistory(TvAirRecordingHistoryQueryDto? query = null)
        {
            permissions.Require(nameof(RecordingsApi), PluginPermission.ReadRecordingHistory);
            query ??= new TvAirRecordingHistoryQueryDto();
            IEnumerable<Reservation> rows = readModels.GetReservations()
                .Where(IsTerminalReservationHistoryRow);
            if (!query.IncludeSystemEntries) rows = rows.Where(r => r.Source != ReservationSource.Epg);
            if (query.From.HasValue) rows = rows.Where(r => r.EndTime >= query.From.Value.LocalDateTime);
            if (query.To.HasValue) rows = rows.Where(r => r.StartTime < query.To.Value.LocalDateTime);
            if (!string.IsNullOrWhiteSpace(query.ReservationId))
            {
                var id = ParseReservationId(query.ReservationId);
                rows = id.HasValue ? rows.Where(r => r.Id == id.Value) : Enumerable.Empty<Reservation>();
            }
            if (!string.IsNullOrWhiteSpace(query.ServiceName)) rows = rows.Where(r => Contains(readModels.ResolveCurrentServiceName(r), query.ServiceName));
            var take = Math.Clamp(query.Limit ?? 1000, 1, 10000);
            var history = rows.OrderByDescending(r => r.EndTime).Take(take).Select(r =>
            {
                var reservationId = FormatReservationId(r.Id);
                var metadata = ResolveRecordingProgramMetadata(readModels, recordingResults, r);
                var result = metadata.Result;
                return new TvAirRecordingHistoryDto
                {
                    ReservationId = reservationId,
                    ServiceName = readModels.ResolveCurrentServiceName(r),
                    ProgramTitle = r.Title,
                    NetworkId = result?.NetworkId ?? r.NetworkId,
                    TransportStreamId = result?.TransportStreamId ?? r.TransportStreamId,
                    ServiceId = result?.ServiceId ?? r.ServiceId,
                    EventId = result?.EventId ?? r.EventId,
                    ScheduledStartTime = result?.ScheduledStartTime ?? new DateTimeOffset(r.ScheduledStartTime ?? r.StartTime),
                    Genre = metadata.Genre,
                    GenreCodes = metadata.GenreCodes,
                    Start = new DateTimeOffset(r.StartTime),
                    End = new DateTimeOffset(r.EndTime),
                    RecordingId = result?.RecordingId ?? reservationId,
                    Result = result?.Result ?? r.Status.ToString(),
                    ActualStart = result?.ActualStartTime ?? (r.RecordingStartedAt.HasValue ? new DateTimeOffset(r.RecordingStartedAt.Value) : null),
                    ActualEnd = result?.ActualEndTime ?? (r.RecordingFinishedAt.HasValue ? new DateTimeOffset(r.RecordingFinishedAt.Value) : null),
                    EndReason = result?.EndReason ?? string.Empty,
                    OutputFilePath = result?.FilePath,
                    FileCreated = result?.FileCreated,
                    DropCount = result?.Drop,
                    ErrorCount = result?.Error,
                    ScrambleCount = result?.Scramble,
                    QualityDataAvailable = result?.QualityDataAvailable ?? false,
                    QualityCompleteness = result?.QualityCompleteness ?? string.Empty,
                    QualitySource = result?.QualitySource ?? string.Empty,
                    ResourceReleaseState = result?.ResourceReleaseState ?? string.Empty,
                    ResultFinalized = result?.ResultFinalized ?? false
                };
            }).ToList();
            return history;
        }

        public TvAirRecordingSessionDto? GetActiveByReservationId(string reservationId)
        {
            permissions.Require(nameof(RecordingsApi), PluginPermission.ReadRecordingStatus);
            var id = ParseReservationId(reservationId);
            if (!id.HasValue) return null;
            return ListActive().FirstOrDefault(s => string.Equals(s.ReservationId, FormatReservationId(id.Value), StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class RecordingFilesApi(PluginReadModelSource readModels, RecordingResultStore recordingResults, CapabilityPermissionGate permissions) : ITvAirRecordingFilesApi
    {
        public IReadOnlyList<TvAirRecordingFileDto> List(TvAirRecordingFileQueryDto? query = null)
        {
            permissions.Require(nameof(RecordingFilesApi), PluginPermission.ReadRecordingHistory, PluginPermission.ReadRecordingStatus);
            query ??= new TvAirRecordingFileQueryDto();
            IEnumerable<Reservation> rows = readModels.GetReservations()
                .Where(r => r.Source != ReservationSource.Epg);
            if (query.From.HasValue) rows = rows.Where(r => r.EndTime >= query.From.Value.LocalDateTime);
            if (query.To.HasValue) rows = rows.Where(r => r.StartTime <= query.To.Value.LocalDateTime);
            if (!string.IsNullOrWhiteSpace(query.ReservationId))
            {
                var id = ParseReservationId(query.ReservationId);
                rows = id.HasValue ? rows.Where(r => r.Id == id.Value) : Enumerable.Empty<Reservation>();
            }
            var recordings = TvAirManagedProcessRegistry.GetRecordings()
                .Where(p => p.ReservationId.HasValue && !string.IsNullOrWhiteSpace(p.RecordingFilePath))
                .GroupBy(p => p.ReservationId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.RegisteredAt).First());

            // RECORDING_FILE_DISCOVERY_SINGLE_SOURCE:
            // RecordingFiles is a file-presence projection, not a reservation-lifecycle classifier.
            // Use the finalized RecordingResult path for terminal recordings and the managed-process
            // registry path for an active recording. Do not infer file existence from Completed/Failed
            // status, and do not hide a retained partial file merely because its reservation ended as
            // Cancelled. Rows without canonical file-path evidence are not recording-file entries.
            var result = new List<TvAirRecordingFileDto>();
            foreach (var r in rows)
            {
                var storedPath = recordingResults.Get(FormatReservationId(r.Id))?.FilePath;
                var activePath = recordings.TryGetValue(r.Id, out var active) ? active.RecordingFilePath : null;
                var filePath = !string.IsNullOrWhiteSpace(storedPath) ? storedPath : activePath;
                if (string.IsNullOrWhiteSpace(filePath)) continue;
                result.Add(ToFileDto(r, filePath));
            }
            return result;
        }

        public TvAirRecordingFileDto? GetByReservationId(string reservationId)
            => List(new TvAirRecordingFileQueryDto { ReservationId = reservationId }).FirstOrDefault();
    }

    private sealed class RecordingInspectionApi(RecordingResultStore recordingResults, CapabilityPermissionGate permissions) : ITvAirRecordingInspectionApi
    {
        public TvAirRecordingInspectionResultDto? GetByReservationId(string reservationId)
        {
            permissions.Require(nameof(RecordingInspectionApi), PluginPermission.ReadRecordingQuality);
            var result = recordingResults.Get(reservationId);
            return result is null ? null : new TvAirRecordingInspectionResultDto
            {
                ReservationId = result.ReservationId,
                State = result.ResultFinalized ? "Finalized" : "Pending",
                DropCount = result.Drop,
                ErrorCount = result.Error,
                ScrambleCount = result.Scramble,
                Summary = result.EndReason
            };
        }

        public TvAirRecordingInspectionJobDto RequestInspection(string reservationId)
        {
            permissions.Require(nameof(RecordingInspectionApi), PluginPermission.ReadRecordingQuality);
            var existing = recordingResults.Get(reservationId);
            return new TvAirRecordingInspectionJobDto
            {
                JobId = string.Empty,
                ReservationId = reservationId,
                State = existing?.ResultFinalized == true ? "AlreadyFinalized" : "NotAvailable",
                Accepted = false,
                Message = existing?.ResultFinalized == true ? "The recording result is already finalized." : "On-demand inspection is not available."
            };
        }
    }

    private sealed class PlaybackProgressApi(PlaybackProgressStore store, CapabilityPermissionGate permissions) : ITvAirPlaybackProgressApi
    {
        public TvAirPlaybackProgressSnapshotDto GetSnapshot()
        {
            permissions.Require(nameof(PlaybackProgressApi), PluginPermission.ReadPlaybackProgress);
            return store.GetSnapshot();
        }

        public TvAirPlaybackProgressDto? Get(string recordingId)
        {
            permissions.Require(nameof(PlaybackProgressApi), PluginPermission.ReadPlaybackProgress);
            return store.Get(recordingId);
        }

        public TvAirPlaybackProgressDto Update(TvAirPlaybackProgressUpdateDto update)
        {
            permissions.Require(nameof(PlaybackProgressApi), PluginPermission.WritePlaybackProgress);
            return store.Update(update);
        }

        public bool Remove(string recordingId)
        {
            permissions.Require(nameof(PlaybackProgressApi), PluginPermission.WritePlaybackProgress);
            return store.Remove(recordingId);
        }
    }

    private sealed class MediaInsightsApi(
        PluginReadModelSource readModels,
        RecordingResultStore recordingResults,
        PlaybackProgressStore playbackProgress,
        CapabilityPermissionGate permissions) : ITvAirMediaInsightsApi
    {
        public TvAirMediaContextSnapshotDto GetContextSnapshot(TvAirMediaContextQueryDto? query = null)
        {
            permissions.Require(nameof(MediaInsightsApi), PluginPermission.ReadMediaInsights, PluginPermission.ReadRecordingHistory, PluginPermission.ReadReservations, PluginPermission.ReadPlaybackProgress);
            var capturedAt = DateTimeOffset.Now;
            var to = query?.To ?? capturedAt;
            var from = query?.From ?? to.AddDays(-90);
            if (from > to) throw new ArgumentException("From must not be later than To.");

            var reservations = readModels.GetReservations()
                .Where(x => x.Source != ReservationSource.Epg)
                .Where(x => x.EndTime >= from.LocalDateTime && x.StartTime < to.LocalDateTime)
                .ToArray();
            // MEDIA_INSIGHTS_LIFECYCLE_SINGLE_SOURCE:
            // recording history / future reservation の所属は時刻フィールドから再推測しない。
            // Reservation lifecycleの確定Statusをそのまま分類正本として使う。
            // Failed attemptは録画履歴、Cancelledは録画履歴でも将来予約でもない。
            var recordingHistory = reservations
                .Where(x => x.Status is ReservationStatus.Completed or ReservationStatus.Failed)
                .ToArray();
            var future = reservations
                .Where(x => x.Status == ReservationStatus.Scheduled)
                .Where(x => x.StartTime >= capturedAt.LocalDateTime && x.IsEnabled)
                .ToArray();
            var progress = playbackProgress.GetSnapshot().Items.ToDictionary(x => x.RecordingId, StringComparer.OrdinalIgnoreCase);

            var historyRows = recordingHistory.Select(r =>
            {
                var reservationId = FormatReservationId(r.Id);
                var metadata = ResolveRecordingProgramMetadata(readModels, recordingResults, r);
                var recordingId = metadata.Result?.RecordingId ?? reservationId;
                progress.TryGetValue(recordingId, out var state);
                var duration = state?.DurationSeconds > 0 ? state.DurationSeconds : Math.Max(0, (long)(r.EndTime - r.StartTime).TotalSeconds);
                return new
                {
                    Reservation = r,
                    Result = metadata.Result,
                    Progress = state,
                    Duration = duration,
                    metadata.Genre,
                    metadata.GenreCodes
                };
            }).ToArray();

            var playableRows = historyRows.Where(x => IsPlayableCompletedRecording(x.Reservation, x.Result)).ToArray();
            var watched = playableRows.Count(x => x.Progress?.IsCompleted == true);
            var knownProgress = playableRows.Count(x => x.Progress is not null);
            var unwatched = playableRows.Where(x => x.Progress is null || (!x.Progress.IsCompleted && x.Progress.PositionSeconds == 0)).ToArray();

            static string TimeBand(DateTime value) => value.Hour switch
            {
                < 6 => "late-night",
                < 12 => "morning",
                < 18 => "daytime",
                < 22 => "evening",
                _ => "night"
            };

            return new TvAirMediaContextSnapshotDto
            {
                SnapshotId = $"media-context:{capturedAt:O}:{historyRows.Length}:{future.Length}",
                CapturedAt = capturedAt,
                From = from,
                To = to,
                RecordingCount = historyRows.Length,
                ReservedCount = future.Length,
                UnwatchedRecordingCount = unwatched.Length,
                UnwatchedDurationSeconds = unwatched.Sum(x => x.Duration),
                CompletionRate = knownProgress == 0 ? 0 : (double)watched / knownProgress,
                Genres = historyRows
                    .Where(x => !string.IsNullOrWhiteSpace(x.Genre))
                    .GroupBy(x => x.Genre.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new TvAirMediaBucketDto { Key = g.Key, Count = g.Count(), DurationSeconds = g.Sum(x => x.Duration) })
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Weekdays = historyRows.GroupBy(x => x.Reservation.StartTime.DayOfWeek.ToString())
                    .Select(g => new TvAirMediaBucketDto { Key = g.Key, Count = g.Count(), DurationSeconds = g.Sum(x => x.Duration) })
                    .OrderByDescending(x => x.Count).ToArray(),
                TimeBands = historyRows.GroupBy(x => TimeBand(x.Reservation.StartTime))
                    .Select(g => new TvAirMediaBucketDto { Key = g.Key, Count = g.Count(), DurationSeconds = g.Sum(x => x.Duration) })
                    .OrderByDescending(x => x.Count).ToArray()
            };
        }
    }

    private sealed class ContentDiscoveryApi(
        PluginReadModelSource readModels,
        RecordingResultStore recordingResults,
        PlaybackProgressStore playbackProgress,
        CapabilityPermissionGate permissions) : ITvAirContentDiscoveryApi
    {
        public TvAirContentDiscoveryResultDto SearchAvailable(TvAirContentDiscoveryQueryDto query)
        {
            permissions.Require(nameof(ContentDiscoveryApi), PluginPermission.ReadContentDiscovery, PluginPermission.ReadProgramGuideProjection, PluginPermission.ReadRecordingHistory, PluginPermission.ReadPlaybackProgress);
            ArgumentNullException.ThrowIfNull(query);
            var now = query.Now ?? DateTimeOffset.Now;
            var maximumSeconds = Math.Clamp(query.MaximumAvailableMinutes, 1, 24 * 60) * 60L;
            var limit = Math.Clamp(query.Limit, 1, 1000);
            var excluded = new HashSet<string>(query.ExcludedGenres.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
            var items = new List<TvAirAvailableContentDto>();

            if (query.IncludeLive)
            {
                foreach (var e in readModels.GetProgramEvents(now.LocalDateTime, now.AddSeconds(maximumSeconds).LocalDateTime)
                    .Where(x => x.Start <= now.LocalDateTime && x.End > now.LocalDateTime))
                {
                    var remaining = Math.Max(0, (long)(e.End - now.LocalDateTime).TotalSeconds);
                    if (remaining > maximumSeconds || (!string.IsNullOrWhiteSpace(e.Genre) && excluded.Contains(e.Genre))) continue;
                    items.Add(new TvAirAvailableContentDto
                    {
                        ContentId = $"live:{e.Key.Value}", Kind = "live", Title = e.Title, ServiceName = readModels.ResolveCurrentServiceName(e.NetworkId, e.TransportStreamId, e.ServiceId, e.ServiceName),
                        NetworkId = e.NetworkId, TransportStreamId = e.TransportStreamId, ServiceId = e.ServiceId,
                        Genre = e.Genre ?? string.Empty, Start = new DateTimeOffset(e.Start), End = new DateTimeOffset(e.End),
                        TotalSeconds = Math.Max(0, (long)(e.End - e.Start).TotalSeconds), RemainingSeconds = remaining,
                        IsUnwatched = true, CanWatchLive = true, MatchReason = "available_now"
                    });
                }
            }

            if (query.IncludeRecordings)
            {
                var progress = playbackProgress.GetSnapshot().Items.ToDictionary(x => x.RecordingId, StringComparer.OrdinalIgnoreCase);
                // ContentDiscovery exposes only user-playable content. System EPG / PreRec rows are
                // internal scheduler work, and failed/cancelled recording terminals are not playable.
                // Keep the boundary on structured reservation/result state, never title or rule-name inference.
                foreach (var r in readModels.GetReservations().Where(x =>
                    x.Source != ReservationSource.Epg
                    && x.Status == ReservationStatus.Completed))
                {
                    var reservationId = FormatReservationId(r.Id);
                    var recordingResult = recordingResults.Get(reservationId);
                    if (!IsPlayableCompletedRecording(r, recordingResult)) continue;
                    var recordingId = recordingResult?.RecordingId ?? reservationId;
                    progress.TryGetValue(recordingId, out var state);
                    var total = state?.DurationSeconds > 0 ? state.DurationSeconds : Math.Max(0, (long)(r.EndTime - r.StartTime).TotalSeconds);
                    var position = state?.PositionSeconds ?? 0;
                    var remaining = Math.Max(0, total - position);
                    var unwatched = state is null || (!state.IsCompleted && position == 0);
                    var resumable = state is not null && !state.IsCompleted && position > 0;
                    if (remaining <= 0 || remaining > maximumSeconds || (query.UnwatchedOnly && !unwatched) || (query.ResumableOnly && !resumable)) continue;
                    items.Add(new TvAirAvailableContentDto
                    {
                        ContentId = $"recording:{recordingId}", Kind = "recording", Title = r.Title, ServiceName = readModels.ResolveCurrentServiceName(r),
                        NetworkId = r.NetworkId, TransportStreamId = r.TransportStreamId, ServiceId = r.ServiceId,
                        Start = new DateTimeOffset(r.StartTime), End = new DateTimeOffset(r.EndTime), TotalSeconds = total,
                        RemainingSeconds = remaining, ResumePositionSeconds = position, IsUnwatched = unwatched,
                        CanPlayRecording = true, CanResume = resumable, MatchReason = resumable ? "resume_within_time" : "recording_within_time"
                    });
                }
            }

            return new TvAirContentDiscoveryResultDto
            {
                SnapshotId = $"content-discovery:{now:O}:{items.Count}", CapturedAt = now,
                Items = items.OrderBy(x => x.RemainingSeconds).ThenBy(x => x.Title).Take(limit).ToArray()
            };
        }
    }

    private sealed class ProgramGuideApi(
        PluginReadModelSource readModels,
        ITvAirExternalProgramSourceApi externalProgramSource,
        CapabilityPermissionGate permissions) : ITvAirProgramGuideApi
    {
        public IReadOnlyList<TvAirProgramGuideWaveFilterDto> ListWaveFilters()
        {
            permissions.Require(nameof(ProgramGuideApi), PluginPermission.ReadProgramGuideProjection);
            return readModels.GetProgramGuideWaveFilters()
                .OrderBy(item => item.Order)
                .Select(item => new TvAirProgramGuideWaveFilterDto
                {
                    Key = item.Key,
                    BroadcastType = item.Group,
                    Label = item.Label,
                    Order = item.Order,
                    IsProgramGuideFilter = item.IsProgramGuideFilter
                })
                .ToList();
        }


        public IReadOnlyList<TvAirProgramEventDto> ListEvents(TvAirProgramGuideQueryDto? query = null)
        {
            permissions.Require(nameof(ProgramGuideApi), PluginPermission.ReadProgramGuideProjection, PluginPermission.ReadEpg);
            query ??= new TvAirProgramGuideQueryDto();
            IEnumerable<ProjectedProgramEvent> rows = readModels.GetAllProgramEvents();
            if (query.From.HasValue) rows = rows.Where(e => e.End > query.From.Value.LocalDateTime);
            if (query.To.HasValue) rows = rows.Where(e => e.Start < query.To.Value.LocalDateTime);
            if (query.NetworkId.HasValue) rows = rows.Where(e => e.NetworkId == query.NetworkId.Value);
            if (query.TransportStreamId.HasValue) rows = rows.Where(e => e.TransportStreamId == query.TransportStreamId.Value);
            if (query.ServiceId.HasValue) rows = rows.Where(e => e.ServiceId == query.ServiceId.Value);
            if (!string.IsNullOrWhiteSpace(query.ServiceName)) rows = rows.Where(e => Contains(readModels.ResolveCurrentServiceName(e.NetworkId, e.TransportStreamId, e.ServiceId, e.ServiceName), query.ServiceName));
            if (!string.IsNullOrWhiteSpace(query.Keyword)) rows = rows.Where(e => Contains(e.Title, query.Keyword) || Contains(e.ShortText, query.Keyword) || Contains(e.ExtendedText, query.Keyword));
            if (!string.IsNullOrWhiteSpace(query.Genre)) rows = rows.Where(e => Contains(e.Genre, query.Genre) || Contains(e.GenreCodes, query.Genre));
            var displayOrder = readModels.GetChannelLoad().Targets
                .Select((channel, index) => new
                {
                    Key = (channel.OriginalNetworkId, channel.TransportStreamId, channel.ServiceId),
                    Index = index
                })
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.First().Index);
            rows = rows
                .OrderBy(e => e.Start)
                .ThenBy(e => displayOrder.TryGetValue((e.NetworkId, e.TransportStreamId, e.ServiceId), out var index) ? index : int.MaxValue)
                .ThenBy(e => e.NetworkId)
                .ThenBy(e => e.TransportStreamId)
                .ThenBy(e => e.ServiceId)
                .ThenBy(e => e.EventId);
            if (query.Limit.HasValue)
            {
                rows = rows.Take(Math.Clamp(query.Limit.Value, 1, 20000));
            }
            return rows.Select(e => ToDto(e, readModels)).ToList();
        }

        public TvAirProgramEventDto? GetEvent(TvAirProgramEventKeyDto key)
        {
            permissions.Require(nameof(ProgramGuideApi), PluginPermission.ReadProgramGuideProjection, PluginPermission.ReadEpg);
            if (!TryToUShort(key.NetworkId, out var networkId)) return null;
            if (!TryToUShort(key.TransportStreamId, out var transportStreamId)) return null;
            if (!TryToUShort(key.ServiceId, out var serviceId)) return null;
            if (!TryToUShort(key.EventNumber, out var eventId)) return null;
            return readModels.GetProgramEvent(networkId, transportStreamId, serviceId, eventId) is { } e ? ToDto(e, readModels) : null;
        }

        public TvAirProgramEventDto? GetEventByProjectedId(string projectedEventId)
        {
            permissions.Require(nameof(ProgramGuideApi), PluginPermission.ReadProgramGuideProjection, PluginPermission.ReadEpg);
            if (string.IsNullOrWhiteSpace(projectedEventId)) return null;
            return readModels.GetAllProgramEvents()
                .FirstOrDefault(e => string.Equals(e.Key.Value, projectedEventId.Trim(), StringComparison.Ordinal)) is { } e
                ? ToDto(e, readModels)
                : null;
        }

        public TvAirExternalProgramGuideReplaceResultDto ReplaceExternalEvents(IReadOnlyList<TvAirExternalProgramEventDto> events)
            => externalProgramSource.ReplaceSnapshot(events);

        public void ClearExternalEvents()
            => externalProgramSource.ClearSnapshot();

        private static bool TryToUShort(int value, out ushort result)
        {
            if (value is < ushort.MinValue or > ushort.MaxValue)
            {
                result = 0;
                return false;
            }
            result = (ushort)value;
            return true;
        }
    }

    private sealed class ExternalProgramSourceApi(
        ExternalEpgSourceStore externalEpgSources,
        ProgramProjectionReservationSyncService projectionReservationSync,
        PluginTypedEventHub typedEvents,
        string pluginId,
        LogRepository log,
        CapabilityPermissionGate permissions) : ITvAirExternalProgramSourceApi
    {
        private readonly object _externalSourceMutationGate = new();
        private bool _externalSourceMutationInProgress;

        public TvAirExternalProgramGuideReplaceResultDto ReplaceSnapshot(IReadOnlyList<TvAirExternalProgramEventDto> events)
        {
            permissions.Require(nameof(ExternalProgramSourceApi), PluginPermission.WriteProgramGuideProjection);
            events ??= Array.Empty<TvAirExternalProgramEventDto>();

            var accepted = new List<ExternalEpgEvent>(events.Count);
            var rejected = 0;
            foreach (var dto in events)
            {
                if (TryMapExternalEvent(dto, out var e))
                {
                    accepted.Add(e);
                }
                else
                {
                    rejected++;
                }
            }

            lock (_externalSourceMutationGate)
            {
                if (_externalSourceMutationInProgress)
                    throw new InvalidOperationException("Reentrant external program source mutation is not allowed.");
                _externalSourceMutationInProgress = true;
                try
                {
                var operationId = Guid.NewGuid().ToString("N");
                var replace = externalEpgSources.ReplaceSnapshot(pluginId, accepted);
                var totalRejected = rejected + replace.RejectedDuplicateCount;
                log.Add("EXTERNAL_EPG_SOURCE_REGISTER", pluginId, $"result=OK accepted={replace.CurrentCount} rejected={totalRejected} rejectedInvalid={rejected} rejectedDuplicateSourceEventKey={replace.RejectedDuplicateCount} changed={replace.Changed} previous={replace.PreviousCount} current={replace.CurrentCount} sourcePluginId={pluginId} writeBack=none target=runtime_projection synchronization=serialized_per_source rule=external_epg_projection_contract");

                var syncRequired = replace.Changed || projectionReservationSync.HasPendingExternalSourceChange(pluginId);
                var syncCompleted = !syncRequired;
                var synchronizationState = syncRequired ? "Running" : "Completed";
                if (syncRequired)
                {
                    try
                    {
                        var sync = projectionReservationSync.ApplyExternalSourceChange(pluginId, "ReplaceSnapshot", runKeywordMatcher: true);
                        syncCompleted = true;
                        synchronizationState = sync.AllocationDeferred ? "Deferred" : "Completed";
                        log.Add("PROGRAM_GUIDE_UPDATED", "ExternalEpg",
                            $"result={(sync.AllocationDeferred ? "UPDATED_SYNC_DEFERRED" : "UPDATED")} sourcePluginId={pluginId} change=ReplaceSnapshot promotedReservations={sync.Promoted} reboundReservations={sync.Reconciled.Rebound} sourceMissingReservations={sync.Reconciled.SourceMissing} ambiguousReservations={sync.Reconciled.Ambiguous} previous={replace.PreviousCount} current={replace.CurrentCount} target=runtime_projection reservationSynchronization={(sync.AllocationDeferred ? "deferred_by_stop_phase" : "completed")} synchronization=serialized_per_source rule=program_guide_projection_contract");
                    }
                    catch (Exception ex)
                    {
                        synchronizationState = "PendingRetry";
                        log.Add("PROGRAM_GUIDE_UPDATE_SYNC", "ExternalEpg",
                            $"result=PENDING sourcePluginId={pluginId} change=ReplaceSnapshot snapshotCommitted=True retryOnNextRegistration=True error={SafePluginLog(ex.Message)} target=runtime_projection synchronization=serialized_per_source rule=program_guide_projection_contract");
                    }
                }
                typedEvents.Publish(new TvAirEventDto
                {
                    EventType = TvAirEventType.ProgramGuideUpdated,
                    EntityId = $"external-program-source:{pluginId}",
                    OperationId = operationId,
                    SourceOwnerId = pluginId,
                    DataRevision = replace.StoreRevision,
                    ChangeKind = replace.CurrentCount == 0 ? "ExternalSnapshotCleared" : "ExternalSnapshotReplaced",
                    Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ownerRevision"] = replace.OwnerRevision.ToString(CultureInfo.InvariantCulture),
                        ["projectedRevision"] = replace.StoreRevision.ToString(CultureInfo.InvariantCulture),
                        ["synchronizationState"] = synchronizationState
                    }
                });
                return new TvAirExternalProgramGuideReplaceResultDto
                {
                    Accepted = true,
                    AcceptedCount = replace.CurrentCount,
                    RejectedCount = totalRejected,
                    RejectedInvalidCount = rejected,
                    RejectedDuplicateCount = replace.RejectedDuplicateCount,
                    Changed = replace.Changed,
                    PreviousCount = replace.PreviousCount,
                    CurrentCount = replace.CurrentCount,
                    OperationId = operationId,
                    SourceOwnerId = pluginId,
                    OwnerRevision = replace.OwnerRevision,
                    ProjectedRevision = replace.StoreRevision,
                    SynchronizationState = synchronizationState,
                    Message = syncCompleted
                        ? (replace.CurrentCount == 0 ? "External EPG snapshot cleared." : "External EPG snapshot replaced.")
                        : "External EPG snapshot replaced; reservation synchronization is pending retry."
                };
                }
                finally
                {
                    _externalSourceMutationInProgress = false;
                }
            }
        }

        public void ClearSnapshot()
        {
            permissions.Require(nameof(ExternalProgramSourceApi), PluginPermission.WriteProgramGuideProjection);
            lock (_externalSourceMutationGate)
            {
                if (_externalSourceMutationInProgress)
                    throw new InvalidOperationException("Reentrant external program source mutation is not allowed.");
                _externalSourceMutationInProgress = true;
                try
                {
                var replace = externalEpgSources.Clear(pluginId);
                log.Add("EXTERNAL_EPG_SOURCE_REGISTER", pluginId, $"result=CLEARED accepted=0 rejected=0 rejectedInvalid=0 rejectedDuplicateSourceEventKey=0 changed={replace.Changed} previous={replace.PreviousCount} current={replace.CurrentCount} sourcePluginId={pluginId} writeBack=none target=runtime_projection synchronization=serialized_per_source rule=external_epg_projection_contract");

                var syncRequired = replace.Changed || projectionReservationSync.HasPendingExternalSourceChange(pluginId);
                if (syncRequired)
                {
                    try
                    {
                        var sync = projectionReservationSync.ApplyExternalSourceChange(pluginId, "ClearSnapshot", runKeywordMatcher: false);
                        log.Add("PROGRAM_GUIDE_UPDATED", "ExternalEpg",
                            $"result={(sync.AllocationDeferred ? "UPDATED_SYNC_DEFERRED" : "UPDATED")} sourcePluginId={pluginId} change=ClearSnapshot promotedReservations={sync.Promoted} reboundReservations={sync.Reconciled.Rebound} sourceMissingReservations={sync.Reconciled.SourceMissing} ambiguousReservations={sync.Reconciled.Ambiguous} previous={replace.PreviousCount} current={replace.CurrentCount} target=runtime_projection existingReservations=preserved reservationSynchronization={(sync.AllocationDeferred ? "deferred_by_stop_phase" : "completed")} synchronization=serialized_per_source rule=program_guide_projection_contract");
                    }
                    catch (Exception ex)
                    {
                        log.Add("PROGRAM_GUIDE_UPDATE_SYNC", "ExternalEpg",
                            $"result=PENDING sourcePluginId={pluginId} change=ClearSnapshot snapshotCommitted=True retryOnNextRegistration=True existingReservations=preserved error={SafePluginLog(ex.Message)} target=runtime_projection synchronization=serialized_per_source rule=program_guide_projection_contract");
                    }
                }
                }
                finally
                {
                    _externalSourceMutationInProgress = false;
                }
            }
        }

        private static bool TryMapExternalEvent(TvAirExternalProgramEventDto dto, out ExternalEpgEvent result)
        {
            result = null!;
            if (dto is null) return false;
            if (!TryToUShort(dto.NetworkId, out var networkId)) return false;
            if (!TryToUShort(dto.TransportStreamId, out var transportStreamId)) return false;
            if (!TryToUShort(dto.ServiceId, out var serviceId)) return false;
            if (!TryToUShort(dto.EventNumber, out var eventId)) return false;

            var start = dto.Start.LocalDateTime;
            var end = dto.End.LocalDateTime;
            if (start >= end) return false;

            if (string.IsNullOrWhiteSpace(dto.Title)
                && string.IsNullOrWhiteSpace(dto.Summary)
                && string.IsNullOrWhiteSpace(dto.Detail)
                && string.IsNullOrWhiteSpace(dto.ExtendedItems))
            {
                return false;
            }

            result = new ExternalEpgEvent
            {
                SourceKind = string.IsNullOrWhiteSpace(dto.SourceKind) ? "ExternalEpg" : dto.SourceKind.Trim(),
                SourceEventKey = dto.SourceEventKey?.Trim() ?? string.Empty,
                NetworkId = networkId,
                TransportStreamId = transportStreamId,
                ServiceId = serviceId,
                EventId = eventId,
                Start = start,
                End = end,
                ServiceName = dto.ServiceName?.Trim() ?? string.Empty,
                Title = dto.Title?.Trim() ?? string.Empty,
                ShortText = dto.Summary?.Trim() ?? string.Empty,
                ExtendedText = dto.Detail?.Trim() ?? string.Empty,
                ExtendedItems = dto.ExtendedItems?.Trim() ?? string.Empty,
                Genre = dto.Genre?.Trim() ?? string.Empty,
                GenreCodes = dto.GenreCodes?.Trim() ?? string.Empty,
                UpdatedAt = DateTime.UtcNow
            };
            return true;
        }

        private static bool TryToUShort(int value, out ushort result)
        {
            if (value is < ushort.MinValue or > ushort.MaxValue)
            {
                result = 0;
                return false;
            }
            result = (ushort)value;
            return true;
        }
    }

    private sealed class ProgramGuideEventsApi(PluginOperationalReadService operationalReads, CapabilityPermissionGate permissions) : ITvAirProgramGuideEventsApi
    {
        public IReadOnlyList<TvAirProgramGuideChangeDto> ListChanges(TvAirProgramGuideChangeQueryDto? query = null)
        {
            permissions.Require(nameof(ProgramGuideEventsApi), PluginPermission.ReadProgramGuideProjection, PluginPermission.ReadEpgStatus);
            query ??= new TvAirProgramGuideChangeQueryDto();
            IEnumerable<LogEntry> rows = operationalReads.GetProgramGuideUpdateLogs();
            if (query.Since.HasValue) rows = rows.Where(e => e.CreatedAt >= query.Since.Value.LocalDateTime);
            return rows.OrderByDescending(e => e.CreatedAt).Select(e => new TvAirProgramGuideChangeDto
            {
                Timestamp = new DateTimeOffset(e.CreatedAt),
                ChangeKind = ResolveProgramGuideChangeKind(e),
                EventKey = null
            }).ToList();
        }
    }

    private sealed class EpgApi(EpgScheduler scheduler, PluginOperationalReadService operationalReads, CapabilityPermissionGate permissions) : ITvAirEpgApi
    {
        public TvAirEpgStatusDto GetStatus()
        {
            permissions.Require(nameof(EpgApi), PluginPermission.ReadEpgStatus);
            var state = operationalReads.GetEpgRunState();
            return new TvAirEpgStatusDto
            {
                IsRunning = state.IsRunning,
                CanStart = state.CanStart,
                CanCancel = state.CanCancel,
                Source = state.Source,
                Scope = state.TargetScope,
                Silent = state.Silent,
                UiMode = state.UiMode,
                CancelRoute = state.CancelRoute,
                LastResult = state.IsRunning ? "Running" : "Idle"
            };
        }

        public TvAirEpgRunResultDto RequestRun(TvAirEpgRunRequestDto request)
        {
            permissions.Require(nameof(EpgApi), PluginPermission.ControlEpg);
            var scope = request.Scope switch
            {
                TvAirEpgRunScope.Ground => "GR",
                TvAirEpgRunScope.BsCs => "BSCS",
                _ => "All"
            };
            var accepted = scheduler.TriggerNow("PluginApi", silent: false, targetScope: scope);
            return new TvAirEpgRunResultDto { Accepted = accepted, Result = accepted ? "Accepted" : "Rejected" };
        }
    }

    private sealed class ChannelsApi(PluginReadModelSource readModels, CapabilityPermissionGate permissions) : ITvAirChannelsApi
    {
        public IReadOnlyList<TvAirServiceDto> ListServices(TvAirServiceQueryDto? query = null)
        {
            permissions.Require(nameof(ChannelsApi), PluginPermission.ReadChannels);
            query ??= new TvAirServiceQueryDto();
            IEnumerable<(ChannelTarget Target, int DisplayOrder)> targets = readModels.GetChannelLoad().Targets.Select((target, index) => (target, index));
            if (!string.IsNullOrWhiteSpace(query.BroadcastType)) targets = targets.Where(x => Contains(x.Target.Group, query.BroadcastType));
            if (query.Enabled.HasValue) targets = targets.Where(_ => query.Enabled.Value);
            return targets.Select(x => new TvAirServiceDto
            {
                ServiceName = x.Target.Name,
                NetworkId = x.Target.OriginalNetworkId,
                TransportStreamId = x.Target.TransportStreamId,
                ServiceId = x.Target.ServiceId,
                BroadcastType = x.Target.Group,
                RemoteControlKeyId = null,
                DisplayOrder = x.DisplayOrder,
                IsEnabled = true
            }).ToList();
        }
    }

    private sealed class ServiceMetadataApi(PluginReadModelSource readModels, CapabilityPermissionGate permissions) : ITvAirServiceMetadataApi
    {
        public TvAirServiceMetadataDto? Get(int networkId, int transportStreamId, int serviceId)
            => List(null).FirstOrDefault(s => s.NetworkId == networkId && s.TransportStreamId == transportStreamId && s.ServiceId == serviceId);

        public IReadOnlyList<TvAirServiceMetadataDto> List(TvAirServiceMetadataQueryDto? query = null)
        {
            permissions.Require(nameof(ServiceMetadataApi), PluginPermission.ReadChannels);
            query ??= new TvAirServiceMetadataQueryDto();
            IEnumerable<ChannelTarget> targets = readModels.GetChannelLoad().Targets;
            if (!string.IsNullOrWhiteSpace(query.BroadcastType)) targets = targets.Where(t => Contains(t.Group, query.BroadcastType));
            return targets.Select((t, i) => new TvAirServiceMetadataDto
            {
                ServiceName = t.Name,
                NetworkId = t.OriginalNetworkId,
                TransportStreamId = t.TransportStreamId,
                ServiceId = t.ServiceId,
                BroadcastType = t.Group,
                DisplayOrder = i,
                ChannelArgument = t.ChannelArgument
            }).ToList();
        }

        public TvAirChannelLoadInfoDto GetLoadInfo()
        {
            permissions.Require(nameof(ServiceMetadataApi), PluginPermission.ReadChannels);
            var result = readModels.GetChannelLoad();
            return new TvAirChannelLoadInfoDto
            {
                Message = result.Message,
                Files = result.Files.ToList(),
                Warnings = result.Warnings.ToList(),
                RawSkippedCount = result.RawSkippedCount
            };
        }
    }

    private sealed class TunersApi(PluginReadModelSource readModels, CapabilityPermissionGate permissions) : ITvAirTunersApi
    {
        public IReadOnlyList<TvAirTunerStatusDto> ListTuners()
        {
            permissions.Require(nameof(TunersApi), PluginPermission.ReadTunerStatus);
            return readModels.GetTunerStatus().OrderBy(s => s.Group).ThenBy(s => s.SlotIndex).Select(s => new TvAirTunerStatusDto
            {
                TunerName = s.Name,
                BroadcastType = s.Group,
                UsageKind = s.UsageKind.ToString(),
                IsInUse = s.UsageKind != TunerUsageKind.Free,
                IsFree = s.UsageKind == TunerUsageKind.Free,
                ReservationId = s.ReservationId.HasValue ? FormatReservationId(s.ReservationId.Value) : null,
                ReservationNumber = s.ReservationId,
                ServiceName = null,
                ProgramTitle = null,
                BonDriverFileName = s.BonDriverFileName,
                Did = s.Did,
                Role = s.Role,
                SlotIndex = s.SlotIndex,
                ProcessId = s.ProcessId,
                PlannedEndTime = s.PlannedEndTime.HasValue ? new DateTimeOffset(s.PlannedEndTime.Value) : null
            }).ToList();
        }
    }

    private sealed class TimedTextStreamsApi(string pluginId, TimedTextStreamStore store, LogRepository log) : ITvAirTimedTextStreamsApi
    {
        private static readonly TimeSpan Retention = TimeSpan.FromHours(6);
        public void Publish(TvAirTimedTextPublishDto item)
        {
            try { store.Add(pluginId, item); }
            catch (Exception ex) { log.Add("PLUGIN_TIMED_TEXT_ERROR", pluginId, $"streamId={item?.StreamId} groupId={item?.GroupId} message={ex.Message}"); }
        }
        public IReadOnlyList<TvAirTimedTextItemDto> ReadRecent(TvAirTimedTextQueryDto? query = null)
        {
            query ??= new(); store.PruneOlderThan(Retention);
            return store.GetRecent(query.StreamId, query.GroupId, Math.Clamp(query.Count.GetValueOrDefault(100),1,300));
        }
        public IReadOnlyList<TvAirTimedTextGroupDto> ReadGroups(TvAirTimedTextGroupQueryDto? query = null)
        {
            query ??= new(); store.PruneOlderThan(Retention);
            return store.GetGroups(query.StreamId, Math.Clamp(query.CountPerGroup.GetValueOrDefault(50),1,300)).Select(g=>new TvAirTimedTextGroupDto { StreamId=g.StreamId, GroupId=g.GroupId, Title=g.Title, Subtitle=g.Subtitle, Count=g.Count, LatestOccurredAt=g.LatestOccurredAt, Items=g.Items }).ToList();
        }
    }

    private sealed class ViewersApi(
        string pluginId,
        string pluginDisplayName,
        ExternalTunerLeaseService externalTuners,
        ViewerSessionRegistry viewerSessions,
        ViewerOperationService viewerOperations,
        PluginReadModelSource readModels,
        TvTestSettings tvTestSettings,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        PluginTypedEventHub typedEvents,
        CapabilityPermissionGate permissions) : ITvAirViewersApi
    {
        public TvAirViewerSnapshotDto GetSnapshot()
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ReadViewerSessions, PluginPermission.ReadViewerTuners);
            var capturedAt = DateTimeOffset.Now;
            var leases = externalTuners.GetActiveLeases();
            var sessionStates = viewerSessions.Synchronize(leases);
            var sessionDtos = BuildSessionDtos(leases, sessionStates, null);
            var profiles = ViewerProfileContract.BuildProfiles(tvTestSettings, ini, tunerProfiles)
                .OrderBy(x => x.Order)
                .ToList();
            var byProfile = sessionStates
                .Where(x => !string.IsNullOrWhiteSpace(x.ViewerProfileId))
                .GroupBy(x => x.ViewerProfileId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.AcquiredAt).ToList(), StringComparer.OrdinalIgnoreCase);
            var profileDtos = profiles.Select(x => ToProfileDto(x, byProfile)).ToList();
            var selectable = profiles.Where(x => x.Enabled && !x.IsAuto).OrderBy(x => x.Order).ToList();
            var defaultProfile = selectable.FirstOrDefault(x => x.IsDefault)?.Id
                ?? selectable.FirstOrDefault()?.Id
                ?? "tvtest1";
            return new TvAirViewerSnapshotDto
            {
                CapturedAt = capturedAt,
                Profiles = profileDtos,
                Sessions = sessionDtos,
                DefaultViewerProfile = defaultProfile,
                SelectorVisibleRecommended = selectable.Count >= 2
            };
        }

        public IReadOnlyList<TvAirViewerProfileDto> ListProfiles()
            => GetSnapshot().Profiles;

        public IReadOnlyList<TvAirViewerSessionDto> ListSessions(TvAirViewerSessionQueryDto? query = null)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ReadViewerSessions);
            var leases = externalTuners.GetActiveLeases();
            var sessionStates = viewerSessions.Synchronize(leases);
            return BuildSessionDtos(leases, sessionStates, query);
        }

        private IReadOnlyList<TvAirViewerSessionDto> BuildSessionDtos(
            IReadOnlyList<ExternalTunerLeaseDto> leases,
            IReadOnlyList<ViewerSessionState> sessionStates,
            TvAirViewerSessionQueryDto? query)
        {
            query ??= new TvAirViewerSessionQueryDto();
            var sessions = sessionStates.ToDictionary(x => x.LeaseId, StringComparer.OrdinalIgnoreCase);
            IEnumerable<ExternalTunerLeaseDto> rows = leases;
            if (!string.IsNullOrWhiteSpace(query.ViewerProfileId))
                rows = rows.Where(x => string.Equals(x.ViewerProfileId, query.ViewerProfileId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.AllocationGroup))
                rows = rows.Where(x => string.Equals(NormalizeAllocationGroup(x.Group), NormalizeAllocationGroup(query.AllocationGroup), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.DisplayGroup))
                rows = rows.Where(x => string.Equals(ToDisplayGroup(x.Group, x.NetworkId), NormalizeDisplayGroup(query.DisplayGroup), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.ClientId))
                rows = rows.Where(x => string.Equals(x.ClientId, query.ClientId.Trim(), StringComparison.OrdinalIgnoreCase));

            var result = rows.OrderBy(x => x.AcquiredAt).Select(x =>
            {
                sessions.TryGetValue(x.LeaseId, out var session);
                return new TvAirViewerSessionDto
                {
                    ViewerSessionId = session?.ViewerSessionId ?? string.Empty,
                    ViewerProfileId = x.ViewerProfileId ?? string.Empty,
                    LogicalViewerSlotId = x.LogicalViewerSlotId ?? string.Empty,
                    Generation = session?.Generation ?? 0,
                    LeaseId = x.LeaseId,
                    Source = x.Source,
                    ClientId = x.ClientId ?? string.Empty,
                    ProgramGuideFilterGroup = ToDisplayGroup(x.Group, x.NetworkId),
                    TunerGroup = x.Group,
                    DisplayGroup = ToDisplayGroup(x.Group, x.NetworkId),
                    AllocationGroup = NormalizeAllocationGroup(x.Group),
                    TunerName = x.TunerName,
                    BonDriverFileName = x.BonDriverFileName,
                    Did = x.Did,
                    State = string.IsNullOrWhiteSpace(x.ViewerState) ? "active" : x.ViewerState,
                    ServiceName = ResolveServiceName(readModels, x.NetworkId, x.TransportStreamId, x.ServiceId),
                    AcquiredAt = new DateTimeOffset(x.AcquiredAt),
                    ProcessId = x.ProcessId,
                    NetworkId = x.NetworkId,
                    TransportStreamId = x.TransportStreamId,
                    ServiceId = x.ServiceId,
                    ChannelSpace = x.ChannelSpace,
                    ChannelIndex = x.ChannelIndex,
                    SlotIndex = x.SlotIndex,
                    LaunchResult = x.LaunchResult,
                    TuneResult = x.TuneResult,
                    ActivateResult = x.ActivateResult,
                    RollbackResult = x.RollbackResult,
                    ChannelArgument = x.ChannelArgument ?? string.Empty,
                    ViewerProfileName = x.ViewerProfileName,
                    TvTestPathKey = x.TvTestPathKey ?? string.Empty
                };
            }).ToList();

            AppendDispatchClosedSession(result, query);
            return result;
        }


        private void AppendDispatchClosedSession(List<TvAirViewerSessionDto> result, TvAirViewerSessionQueryDto query)
        {
            var currentEvent = typedEvents.CurrentDispatchEvent;
            if (currentEvent?.EventType != TvAirEventType.ViewerSessionChanged
                || currentEvent.Details is null
                || !currentEvent.Details.TryGetValue("change", out var change)
                || !string.Equals(change, "Closed", StringComparison.OrdinalIgnoreCase))
                return;

            if (!currentEvent.Details.TryGetValue("viewerSessionId", out var viewerSessionId)
                || string.IsNullOrWhiteSpace(viewerSessionId)
                || result.Any(x => string.Equals(x.ViewerSessionId, viewerSessionId, StringComparison.OrdinalIgnoreCase)))
                return;

            currentEvent.Details.TryGetValue("viewerProfileId", out var viewerProfileId);
            currentEvent.Details.TryGetValue("logicalViewerSlotId", out var logicalViewerSlotId);
            currentEvent.Details.TryGetValue("leaseId", out var leaseId);
            currentEvent.Details.TryGetValue("generation", out var generationText);
            currentEvent.Details.TryGetValue("networkId", out var networkIdText);
            currentEvent.Details.TryGetValue("transportStreamId", out var transportStreamIdText);
            currentEvent.Details.TryGetValue("serviceId", out var serviceIdText);

            if (!string.IsNullOrWhiteSpace(query.ViewerProfileId)
                && !string.Equals(query.ViewerProfileId.Trim(), viewerProfileId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.IsNullOrWhiteSpace(query.AllocationGroup)
                || !string.IsNullOrWhiteSpace(query.DisplayGroup)
                || !string.IsNullOrWhiteSpace(query.ClientId))
                return;

            _ = long.TryParse(generationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation);
            int? networkId = int.TryParse(networkIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nid) ? nid : null;
            int? transportStreamId = int.TryParse(transportStreamIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tsid) ? tsid : null;
            int? serviceId = int.TryParse(serviceIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid) ? sid : null;

            result.Add(new TvAirViewerSessionDto
            {
                ViewerSessionId = viewerSessionId.Trim(),
                ViewerProfileId = viewerProfileId?.Trim() ?? string.Empty,
                LogicalViewerSlotId = logicalViewerSlotId?.Trim() ?? string.Empty,
                Generation = generation,
                LeaseId = leaseId?.Trim() ?? string.Empty,
                Source = "viewer_session_changed_closed_event",
                State = "closed",
                AcquiredAt = currentEvent.OccurredAt,
                NetworkId = networkId,
                TransportStreamId = transportStreamId,
                ServiceId = serviceId,
                ServiceName = ResolveServiceName(readModels,
                    networkId.HasValue ? (ushort?)networkId.Value : null,
                    transportStreamId.HasValue ? (ushort?)transportStreamId.Value : null,
                    serviceId.HasValue ? (ushort?)serviceId.Value : null)
            });
        }

        private static TvAirViewerProfileDto ToProfileDto(
            ViewerProfileContractDto profile,
            IReadOnlyDictionary<string, List<ViewerSessionState>> byProfile)
        {
            byProfile.TryGetValue(profile.Id, out var sessions);
            var current = sessions?.FirstOrDefault();
            return new TvAirViewerProfileDto
            {
                ViewerProfileId = profile.Id,
                DisplayName = profile.Name,
                Enabled = profile.Enabled,
                IsDefault = profile.IsDefault,
                DisplayOrder = profile.Order,
                IsAuto = profile.IsAuto,
                TvTestPathKey = profile.TvTestPathKey,
                Source = profile.Source,
                Note = profile.Note,
                TvTestFrameIndex = profile.TvTestFrameIndex,
                LogicalViewerSlotId = profile.LogicalViewerSlotId,
                SupportedGroups = profile.AvailableGroups ?? Array.Empty<string>(),
                AvailableGroups = profile.AvailableGroups ?? Array.Empty<string>(),
                IsShared = profile.IsShared,
                ErrorCode = profile.ErrorCode,
                ActiveSessionCount = sessions?.Count ?? 0,
                IsRunning = current is not null,
                CurrentViewerSessionId = current?.ViewerSessionId ?? string.Empty,
                CurrentGeneration = current?.Generation ?? 0,
                CurrentViewerState = string.IsNullOrWhiteSpace(current?.ViewerState) ? (current is null ? "inactive" : "active") : current!.ViewerState,
                CurrentNetworkId = current?.NetworkId,
                CurrentTransportStreamId = current?.TransportStreamId,
                CurrentServiceId = current?.ServiceId
            };
        }

        public TvAirViewerOperationResultDto Start(TvAirViewerStartRequestDto request)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ControlViewer);
            ArgumentNullException.ThrowIfNull(request);
            if (!TryTriplet(request.NetworkId, request.TransportStreamId, request.ServiceId, out var nid, out var tsid, out var sid))
                return InvalidTarget();
            return ToDto(viewerOperations.Start(new ViewerOperationStartRequest(
                pluginId,
                request.ViewerProfileId,
                nid, tsid, sid,
                request.ServiceName,
                request.GroupHint,
                request.PreserveViewerWindowState,
                request.ViewerActivation)));
        }

        public TvAirViewerOperationResultDto EnsureTuned(TvAirViewerEnsureTunedRequestDto request)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ControlViewer);
            ArgumentNullException.ThrowIfNull(request);
            if (!TryTriplet(request.NetworkId, request.TransportStreamId, request.ServiceId, out var nid, out var tsid, out var sid))
                return InvalidTarget();
            return ToDto(viewerOperations.EnsureTuned(new ViewerOperationEnsureTunedRequest(
                pluginId,
                request.ViewerProfileId,
                nid, tsid, sid,
                request.ServiceName,
                request.GroupHint,
                request.PreserveViewerWindowState,
                request.ViewerActivation)));
        }

        public TvAirViewerOperationResultDto Retune(TvAirViewerRetuneRequestDto request)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ControlViewer);
            ArgumentNullException.ThrowIfNull(request);
            if (!TryTriplet(request.NetworkId, request.TransportStreamId, request.ServiceId, out var nid, out var tsid, out var sid))
                return InvalidTarget();
            return ToDto(viewerOperations.Retune(new ViewerOperationRetuneRequest(
                pluginId,
                request.ViewerProfileId,
                request.ViewerSessionId,
                request.ExpectedGeneration,
                nid, tsid, sid,
                request.ServiceName,
                request.GroupHint,
                request.PreserveViewerWindowState,
                request.ViewerActivation)));
        }

        public TvAirViewerOperationResultDto Restart(TvAirViewerRestartRequestDto request)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ControlViewer);
            ArgumentNullException.ThrowIfNull(request);
            return ToDto(viewerOperations.Restart(new ViewerOperationRestartRequest(
                pluginId,
                request.ViewerSessionId,
                request.ExpectedGeneration,
                request.PreserveViewerWindowState,
                request.ViewerActivation,
                request.Reason)));
        }

        public TvAirViewerOperationResultDto Activate(TvAirViewerActivateRequestDto request)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ControlViewer);
            ArgumentNullException.ThrowIfNull(request);
            return ToDto(viewerOperations.Activate(new ViewerOperationActivateRequest(
                request.ViewerSessionId,
                request.ExpectedGeneration)));
        }

        public TvAirViewerOperationResultDto Stop(TvAirViewerStopRequestDto request)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ControlViewer);
            ArgumentNullException.ThrowIfNull(request);
            return ToDto(viewerOperations.Stop(new ViewerOperationStopRequest(
                request.ViewerSessionId,
                request.ExpectedGeneration,
                request.ViewerProfileId,
                request.Reason)));
        }

        public TvAirViewerOperationResultDto StopCompatible(TvAirViewerCompatibleStopRequestDto request)
        {
            permissions.Require(nameof(ViewersApi), PluginPermission.ControlViewer);
            ArgumentNullException.ThrowIfNull(request);
            return ToDto(viewerOperations.StopCompatible(new ViewerOperationCompatibleStopRequest(
                pluginId,
                pluginDisplayName,
                request.LeaseId,
                request.ViewerProfileId,
                request.ViewerSessionId,
                request.ExpectedGeneration,
                request.Reason)));
        }

        private static bool TryTriplet(int networkId, int transportStreamId, int serviceId, out ushort nid, out ushort tsid, out ushort sid)
        {
            nid = tsid = sid = 0;
            if (networkId is <= 0 or > ushort.MaxValue ||
                transportStreamId is <= 0 or > ushort.MaxValue ||
                serviceId is <= 0 or > ushort.MaxValue)
                return false;
            nid = (ushort)networkId;
            tsid = (ushort)transportStreamId;
            sid = (ushort)serviceId;
            return true;
        }

        private static TvAirViewerOperationResultDto InvalidTarget()
            => new()
            {
                Success = false,
                State = "denied",
                ErrorCode = "missingViewerPayload",
                Message = "Viewer target triplet is incomplete or out of range."
            };

        private static TvAirViewerOperationResultDto ToDto(ViewerOperationResult result)
            => new()
            {
                Success = result.Success,
                State = result.State,
                ErrorCode = result.ErrorCode,
                Message = result.Message,
                ViewerProfileId = result.ViewerProfileId,
                ViewerSessionId = result.ViewerSessionId,
                Generation = result.Generation,
                LeaseId = result.LeaseId,
                ProcessId = result.ProcessId,
                NetworkId = result.NetworkId,
                TransportStreamId = result.TransportStreamId,
                ServiceId = result.ServiceId,
                LogicalViewerSlotId = result.LogicalViewerSlotId,
                AcquiredAt = result.AcquiredAt.HasValue ? new DateTimeOffset(result.AcquiredAt.Value) : null,
                ChannelSpace = result.ChannelSpace,
                ChannelIndex = result.ChannelIndex,
                ViewerState = result.ViewerState,
                Diagnostics = result.Diagnostics,
                // SDK 1.1.2 compatibility only. Foreground restore is intentionally not performed by Host.
                FocusPolicyRequested = "not_requested",
                FocusPolicyApplied = "not_requested",
                ForegroundBeforeHwnd = null,
                ForegroundBeforePid = null,
                ForegroundAfterRetuneHwnd = null,
                ForegroundAfterRetunePid = null,
                ForegroundFinalHwnd = null,
                ForegroundFinalPid = null,
                ForegroundChanged = false,
                ChangedToTargetViewer = false,
                RestorationAttempted = false,
                RestorationSucceeded = false,
                FocusPreserved = true,
                FocusPreserveFailureReason = string.Empty,
                OperationCompleted = result.OperationCompleted,
                HasWarning = result.HasWarning,
                ContinuationRecommended = result.ContinuationRecommended
            };

        private static string ResolveServiceName(PluginReadModelSource readModels, ushort? nid, ushort? tsid, ushort? sid)
        {
            if (!nid.HasValue || !tsid.HasValue || !sid.HasValue) return string.Empty;
            try
            {
                return readModels.GetChannelLoad().Targets.FirstOrDefault(x => x.OriginalNetworkId == nid.Value && x.TransportStreamId == tsid.Value && x.ServiceId == sid.Value)?.Name ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string NormalizeAllocationGroup(string? value)
        {
            var raw = (value ?? string.Empty).Trim().ToUpperInvariant();
            return raw switch { "BS" or "CS" or "BS/CS" => "BSCS", "GROUND" or "地上波" or "地デジ" => "GR", _ => raw };
        }

        private static string NormalizeDisplayGroup(string? value)
        {
            var raw = (value ?? string.Empty).Trim().ToUpperInvariant();
            return raw switch { "GROUND" or "地上波" or "地デジ" => "GR", "BSCS" or "BS/CS" => "BS", _ => raw };
        }

        private static string ToDisplayGroup(string? allocationGroup, ushort? networkId)
        {
            var group = NormalizeAllocationGroup(allocationGroup);
            if (group == "GR") return "GR";
            if (group == "BSCS") return networkId == 4 ? "BS" : "CS";
            return NormalizeDisplayGroup(group);
        }
    }

    private sealed class UnavailableBackupApi : ITvAirBackupApi
    {
        public static UnavailableBackupApi Instance { get; } = new();

        private UnavailableBackupApi() { }

        public TvAirBackupInfoDto GetInfo() => new();

        public TvAirBackupInfoDto CreateSnapshot()
            => throw new NotSupportedException("Backup capability is not provided by this host.");

        public TvAirBackupInfoDto CreateSnapshot(TvAirBackupSnapshotRequestDto request)
            => throw new NotSupportedException("Backup capability is not provided by this host.");
    }

    private sealed class SettingsApi(
        PluginPresentationReadService presentationReads,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> runtimeTunerProfiles,
        string appDirectory,
        string dataDirectory,
        string pluginDataDirectory,
        string pluginsDirectory,
        CapabilityPermissionGate permissions) : ITvAirSettingsApi
    {
        public TvAirRecordingSettingsDto GetRecordingSettings()
        {
            permissions.Require(nameof(SettingsApi), PluginPermission.ReadHostContracts);
            // SETTINGS_RUNTIME_TUNER_TOPOLOGY_CONTRACT
            // Pluginへ返す録画可能本数も、保存済みINI行ではなく起動時確定Runtime topologyから投影する。
            // 再起動待ちTopologyを先取りせず、TunerPool/Allocation/Wakeと同じ稼働中構成を返す。
            var recordingTuners = runtimeTunerProfiles
                .Where(t => string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Recording", StringComparison.OrdinalIgnoreCase))
                .Where(t => TunerDisplayName.IsKnownGroup(t.Group))
                .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
                .ToList();
            var gr = recordingTuners.Count(t =>
                string.Equals(TunerDisplayName.NormalizeGroup(t.Group), "GR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(TunerDisplayName.NormalizeGroup(t.Group), "HYBRID", StringComparison.OrdinalIgnoreCase));
            var bscs = recordingTuners.Count(t =>
                string.Equals(TunerDisplayName.NormalizeGroup(t.Group), "BSCS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(TunerDisplayName.NormalizeGroup(t.Group), "HYBRID", StringComparison.OrdinalIgnoreCase));
            return new TvAirRecordingSettingsDto
            {
                GroundRecordingLimit = gr,
                BsCsRecordingLimit = bscs,
                PreMarginSeconds = ini.PreStartMarginSeconds,
                PostMarginSeconds = ini.PostEndMarginSeconds
            };
        }

        public TvAirEpgSettingsDto GetEpgSettings()
        {
            permissions.Require(nameof(SettingsApi), PluginPermission.ReadSafePaths, PluginPermission.ReadHostContracts);
            return new()
        {
            GroundChannelFilePath = ini.GrChannelFilePath,
            BsCsChannelFilePath = ini.BscsChannelFilePath
        };
        }

        public TvAirUiSettingsDto GetUiSettings()
        {
            permissions.Require(nameof(SettingsApi), PluginPermission.ReadTheme, PluginPermission.ReadHostContracts);
            var theme = presentationReads.GetTheme();
            return new()
            {
                Theme = theme.Appearance,
                Appearance = theme.Appearance,
                AccentColor = theme.AccentColor,
                CssScopeRoot = theme.CssScopeRoot
            };
        }
        public TvAirPluginHostSettingsDto GetPluginHostSettings()
        {
            permissions.Require(nameof(SettingsApi), PluginPermission.ReadSafePaths, PluginPermission.ReadHostContracts);
            return new() { PluginsDirectory = pluginsDirectory };
        }

        public TvAirPathSettingsDto GetPaths()
        {
            permissions.Require(nameof(SettingsApi), PluginPermission.ReadSafePaths);
            return new()
            {
                AppDirectory = appDirectory,
                DataDirectory = dataDirectory,
                PluginDataDirectory = pluginDataDirectory,
                PluginsDirectory = pluginsDirectory
            };
        }
    }

    private sealed class SystemApi : ITvAirSystemApi
    {
        private readonly PluginSystemReadService _reads;
        private readonly CapabilityPermissionGate _permissions;

        public SystemApi(PluginSystemReadService reads, CapabilityPermissionGate permissions)
        {
            _reads = reads;
            _permissions = permissions;
        }

        public TvAirSystemStatusDto GetStatus()
        {
            _permissions.Require(nameof(SystemApi), PluginPermission.ReadHostContracts);
            var status = _reads.GetStatus();
            return new TvAirSystemStatusDto
            {
                Version = status.Version,
                Now = new DateTimeOffset(status.Now),
                ReservationCount = status.ReservationCount,
                ActiveRecordingCount = status.ActiveRecordingCount,
                TunerCount = status.TunerCount,
                FreeTunerCount = status.FreeTunerCount,
                WakePlanCount = status.WakePlanCount,
                NextWakeAt = status.NextWakeAt.HasValue ? new DateTimeOffset(status.NextWakeAt.Value) : null
            };
        }

        public IReadOnlyList<TvAirWakePlanItemDto> ListWakePlan(TvAirWakePlanQueryDto? query = null)
        {
            _permissions.Require(nameof(SystemApi), PluginPermission.ReadHostContracts);
            var from = query?.From?.LocalDateTime;
            var to = query?.To?.LocalDateTime;
            var limit = Math.Clamp(query?.Limit ?? 100, 1, 500);
            return _reads.GetWakePlan(from, to, limit)
                .Select(item => new TvAirWakePlanItemDto
                {
                    At = new DateTimeOffset(item.At),
                    Kind = item.Kind,
                    ReservationId = item.ReservationId?.ToString(CultureInfo.InvariantCulture),
                    Title = item.Title,
                    TaskName = item.TaskName
                })
                .ToArray();
        }
    }

    private sealed class NotificationsApi : ITvAirNotificationsApi
    {
        private readonly string _pluginId;
        private readonly LogRepository _log;
        private readonly CapabilityPermissionGate _permissions;
        private const int ClosedNotificationRetentionLimit = 128;
        private readonly Dictionary<string, TvAIrPlugin.Notifications.PluginNotificationState> _states = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public NotificationsApi(string pluginId, LogRepository log, CapabilityPermissionGate permissions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
            _pluginId = pluginId;
            _log = log;
            _permissions = permissions;
        }

        public void Show(TvAirNotificationDto notification)
        {
            _permissions.Require(nameof(NotificationsApi), PluginPermission.ShowNotifications, PluginPermission.ShowNotification);
            notification ??= new TvAirNotificationDto();
            _log.Add("PLUGIN_NOTIFICATION", notification.Title, notification.Message);
        }

        public TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState> Create(TvAIrPlugin.Notifications.CreatePluginNotificationRequest request)
        {
            _permissions.Require(nameof(NotificationsApi), PluginPermission.ShowNotifications, PluginPermission.ShowNotification);
            if (request is null || string.IsNullOrWhiteSpace(request.NotificationDefinitionId))
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState>.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest, "Notification definition id is required.");

            var definitionId = request.NotificationDefinitionId.Trim();
            var instanceKey = string.IsNullOrWhiteSpace(request.InstanceKey) ? "default" : request.InstanceKey.Trim();
            var id = BuildId(definitionId, instanceKey);
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var created = !_states.TryGetValue(id, out var previous);
                var state = created
                    ? new TvAIrPlugin.Notifications.PluginNotificationState(id, definitionId, instanceKey, request.Title ?? string.Empty, request.Message ?? string.Empty, request.Severity, false, false, 1, now, now)
                    : previous! with { Title = request.Title ?? string.Empty, Message = request.Message ?? string.Empty, Severity = request.Severity, IsClosed = false, Revision = previous!.Revision + 1, UpdatedAtUtc = now };
                _states[id] = state;
                Publish(state, created ? "CREATED" : "UPSERTED");
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState>.Ok(state);
            }
        }

        public TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState> Update(TvAIrPlugin.Notifications.UpdatePluginNotificationRequest request)
        {
            _permissions.Require(nameof(NotificationsApi), PluginPermission.ShowNotifications, PluginPermission.ShowNotification);
            if (request is null || string.IsNullOrWhiteSpace(request.NotificationInstanceId))
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState>.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest, "Notification instance id is required.");

            lock (_gate)
            {
                if (!_states.TryGetValue(request.NotificationInstanceId, out var previous))
                    return TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState>.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.EntityNotFound, "Notification was not found.");
                if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != previous.Revision)
                    return TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState>.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.RevisionConflict, "Notification revision does not match.");

                var state = previous with { Title = request.Title ?? string.Empty, Message = request.Message ?? string.Empty, Severity = request.Severity, IsClosed = false, Revision = previous.Revision + 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
                _states[state.NotificationInstanceId] = state;
                Publish(state, "UPDATED");
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAIrPlugin.Notifications.PluginNotificationState>.Ok(state);
            }
        }

        public TvAIrPlugin.Runtime.TvAirOperationResult Close(string notificationInstanceId, long? expectedRevision = null)
        {
            _permissions.Require(nameof(NotificationsApi), PluginPermission.ShowNotifications, PluginPermission.ShowNotification);
            if (string.IsNullOrWhiteSpace(notificationInstanceId))
                return TvAIrPlugin.Runtime.TvAirOperationResult.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest, "Notification instance id is required.");

            lock (_gate)
            {
                if (!_states.TryGetValue(notificationInstanceId, out var previous))
                    return TvAIrPlugin.Runtime.TvAirOperationResult.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.EntityNotFound, "Notification was not found.");
                if (expectedRevision.HasValue && expectedRevision.Value != previous.Revision)
                    return TvAIrPlugin.Runtime.TvAirOperationResult.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.RevisionConflict, "Notification revision does not match.");

                var state = previous with { IsClosed = true, Revision = previous.Revision + 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
                _states[notificationInstanceId] = state;
                PruneClosedStatesLocked();
                _log.Add("PLUGIN_NOTIFICATION_HOST", _pluginId, $"result=CLOSED notificationInstanceId={notificationInstanceId} revision={state.Revision} ui=none sound=none rule=plugin_notification_host_contract");
                return TvAIrPlugin.Runtime.TvAirOperationResult.Ok();
            }
        }

        public IReadOnlyList<TvAIrPlugin.Notifications.PluginNotificationState> List(bool includeClosed = false)
        {
            _permissions.Require(nameof(NotificationsApi), PluginPermission.ShowNotifications, PluginPermission.ShowNotification);
            lock (_gate)
            {
                return _states.Values
                    .Where(x => includeClosed || !x.IsClosed)
                    .OrderByDescending(x => x.UpdatedAtUtc)
                    .ToArray();
            }
        }

        private string BuildId(string definitionId, string instanceKey)
        {
            var raw = $"{_pluginId}\n{definitionId}\n{instanceKey}";
            return Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
        private void PruneClosedStatesLocked()
        {
            var overflow = _states.Values
                .Where(x => x.IsClosed)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Skip(ClosedNotificationRetentionLimit)
                .Select(x => x.NotificationInstanceId)
                .ToArray();
            foreach (var id in overflow)
                _states.Remove(id);
        }


        private void Publish(TvAIrPlugin.Notifications.PluginNotificationState state, string result)
        {
            Show(new TvAirNotificationDto { Level = state.Severity.ToString(), Title = state.Title, Message = state.Message });
            _log.Add("PLUGIN_NOTIFICATION_HOST", _pluginId, $"result={result} notificationInstanceId={state.NotificationInstanceId} definitionId={state.NotificationDefinitionId} instanceKey={state.InstanceKey} severity={state.Severity} revision={state.Revision} ui=host_policy sound=none rule=plugin_notification_host_contract");
        }
    }

    private sealed class WindowsApi : ITvAirWindowsApi
    {
        private readonly string _pluginId;
        private readonly string _pluginDisplayName;
        private readonly IniSettingsService _ini;
        private readonly PluginRegistry _registry;
        private readonly PluginWindowSessionStore _sessions;
        private readonly PluginToolWindowHostService _toolWindows;
        private readonly LogRepository _log;
        private readonly CapabilityPermissionGate _permissions;

        public WindowsApi(string pluginId, string pluginDisplayName, IniSettingsService ini, PluginRegistry registry, PluginWindowSessionStore sessions, PluginToolWindowHostService toolWindows, LogRepository log, CapabilityPermissionGate permissions)
        {
            _pluginId = pluginId;
            _pluginDisplayName = string.IsNullOrWhiteSpace(pluginDisplayName) ? _pluginId : pluginDisplayName.Trim();
            _ini = ini;
            _registry = registry;
            _sessions = sessions;
            _toolWindows = toolWindows;
            _log = log;
            _permissions = permissions;
        }

        public void ShowToolWindow(TvAirToolWindowRequestDto request)
        {
            _permissions.Require(nameof(WindowsApi), PluginPermission.ShowUi);
            _permissions.Require(nameof(WindowsApi), PluginPermission.OpenToolWindow);
            request ??= new TvAirToolWindowRequestDto();
            if (request.Width.HasValue && request.Width.Value <= 0) throw new ArgumentOutOfRangeException(nameof(request.Width));
            if (request.Height.HasValue && request.Height.Value <= 0) throw new ArgumentOutOfRangeException(nameof(request.Height));

            var route = $"capability-{_pluginId}";
            var contentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? "/" : request.ContentRoute.Trim();
            var windowRequest = new PluginWindowRequest
            {
                PluginId = _pluginId,
                RouteSegment = route,
                WindowId = request.WindowId ?? string.Empty,
                WindowDefinitionId = string.IsNullOrWhiteSpace(request.WindowDefinitionId)
                    ? (string.IsNullOrWhiteSpace(request.WindowId) ? "main" : request.WindowId.Trim())
                    : request.WindowDefinitionId.Trim(),
                Title = string.IsNullOrWhiteSpace(request.Title) ? _pluginDisplayName : request.Title.Trim(),
                Width = ToPositiveInt(request.Width, 620),
                Height = ToPositiveInt(request.Height, 760),
                MinWidth = 0,
                MinHeight = 0,
                Resizable = true,
                Movable = true,
                ContentRoute = contentRoute,
                ResponseMode = "hostHandled",
                ReuseExisting = request.ReuseExisting,
                ActivateExisting = request.ActivateExisting
            };

            var session = _sessions.OpenOrReuse(_pluginDisplayName, _pluginId, route, windowRequest, request.ReuseExisting, out var reused);
            var windowUrl = $"/plugin-window/{Uri.EscapeDataString(session.WindowId)}";
            var navigationUrl = BuildToolWindowNavigationUrl(windowUrl, session);
            var absoluteUrl = BuildAbsoluteLocalUrl(_ini.Port, navigationUrl);
            var result = _toolWindows.OpenOrActivate(session, absoluteUrl, activate: request.ActivateExisting);
            _log.Add("PLUGIN_CAPABILITY_WINDOW", _pluginDisplayName, $"result={SafePluginLog(result.Result)} pluginId={SafePluginLog(_pluginId)} windowId={SafePluginLog(session.WindowId)} reusedSession={reused} contentRoute={SafePluginLog(contentRoute)} rule=capability_windows_api");
        }

        public TvAIrPlugin.Runtime.TvAirOperationResult RefreshToolWindow(TvAirToolWindowRefreshRequestDto request)
        {
            _permissions.Require(nameof(WindowsApi), PluginPermission.ShowUi);
            _permissions.Require(nameof(WindowsApi), PluginPermission.OpenToolWindow);
            request ??= new TvAirToolWindowRefreshRequestDto();
            var windowId = (request.WindowId ?? string.Empty).Trim();
            if (windowId.Length == 0)
                return TvAIrPlugin.Runtime.TvAirOperationResult.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest, "windowId is required.");

            var session = _sessions.Get(windowId);
            if (session is null || !string.Equals(session.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase))
                return TvAIrPlugin.Runtime.TvAirOperationResult.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.EntityNotFound, "Tool window was not found.");
            if (!_toolWindows.IsHostAlive(windowId))
                return TvAIrPlugin.Runtime.TvAirOperationResult.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.EntityNotFound, "Tool window is not open.");

            var contentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? session.ContentRoute : request.ContentRoute.Trim();
            var navigationUrl = BuildToolWindowNavigationUrl(contentRoute, session);
            var absoluteUrl = BuildAbsoluteLocalUrl(_ini.Port, navigationUrl);
            var result = _toolWindows.RefreshExisting(session, absoluteUrl);
            var succeeded = result.Result.Equals("REFRESHED", StringComparison.OrdinalIgnoreCase);
            _log.Add("PLUGIN_CAPABILITY_WINDOW_REFRESH", _pluginDisplayName, $"result={(succeeded ? "OK" : SafePluginLog(result.Result))} pluginId={SafePluginLog(_pluginId)} windowId={SafePluginLog(windowId)} contentRoute={SafePluginLog(contentRoute)} activation=none rule=capability_windows_api");
            return succeeded
                ? TvAIrPlugin.Runtime.TvAirOperationResult.Ok()
                : TvAIrPlugin.Runtime.TvAirOperationResult.Fail(TvAIrPlugin.Runtime.TvAirErrorCode.InternalError, result.Diagnostics);
        }

        public TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowStatePatchResultDto> PatchToolWindow(TvAirToolWindowStatePatchRequestDto request)
        {
            _permissions.Require(nameof(WindowsApi), PluginPermission.ShowUi);
            _permissions.Require(nameof(WindowsApi), PluginPermission.OpenToolWindow);
            request ??= new TvAirToolWindowStatePatchRequestDto();
            var windowId = (request.WindowId ?? string.Empty).Trim();
            var requestedCount = request.UiPatches?.Count ?? 0;
            if (windowId.Length == 0)
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowStatePatchResultDto>.Fail(
                    TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest, "windowId is required.");
            if (request.StateRevision <= 0)
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowStatePatchResultDto>.Fail(
                    TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest, "stateRevision must be greater than zero.");

            var session = _sessions.Get(windowId);
            if (session is null || !string.Equals(session.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase))
            {
                var skipped = new TvAirToolWindowStatePatchResultDto
                {
                    Outcome = "SKIPPED", RequestedPatchCount = requestedCount, AppliedPatchCount = 0,
                    StateRevision = request.StateRevision, Reason = "window_closed"
                };
                _log.Add("PLUGIN_CAPABILITY_WINDOW_STATEPATCH", _pluginDisplayName,
                    $"result=SKIPPED pluginId={SafePluginLog(_pluginId)} windowId={SafePluginLog(windowId)} requestedPatches={requestedCount} appliedPatches=0 stateRevision={request.StateRevision} reason=window_closed navigation=False activation=False focus=False rule=runtime_toolwindow_statepatch_contract");
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowStatePatchResultDto>.Ok(skipped);
            }

            var host = _toolWindows.ApplyStatePatch(windowId, request.UiPatches, request.StateRevision);
            var outcome = host.Applied ? "APPLIED" : host.Skipped ? "SKIPPED" : "FAILED";
            var dto = new TvAirToolWindowStatePatchResultDto
            {
                Outcome = outcome,
                RequestedPatchCount = host.RequestedPatchCount,
                AppliedPatchCount = host.AppliedPatchCount,
                StateRevision = host.StateRevision,
                Reason = host.Diagnostics
            };
            _log.Add("PLUGIN_CAPABILITY_WINDOW_STATEPATCH", _pluginDisplayName,
                $"result={outcome} pluginId={SafePluginLog(_pluginId)} windowId={SafePluginLog(windowId)} requestedPatches={host.RequestedPatchCount} appliedPatches={host.AppliedPatchCount} stateRevision={host.StateRevision} reason={SafePluginLog(host.Diagnostics)} navigation=False activation=False focus=False positionChange=False sizeChange=False scrollChange=False rule=runtime_toolwindow_statepatch_contract");

            if (host.Applied || host.Skipped)
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowStatePatchResultDto>.Ok(dto);
            return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowStatePatchResultDto>.Fail(
                TvAIrPlugin.Runtime.TvAirErrorCode.InternalError, host.Diagnostics, dto);
        }

        public TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowPlacementPersistenceResultDto> SetToolWindowPlacementPersistence(TvAirToolWindowPlacementPersistenceRequestDto request)
        {
            _permissions.Require(nameof(WindowsApi), PluginPermission.UseWindowApi);
            request ??= new TvAirToolWindowPlacementPersistenceRequestDto();
            var definitionId = (request.WindowDefinitionId ?? string.Empty).Trim();
            if (definitionId.Length == 0)
            {
                var invalid = new TvAirToolWindowPlacementPersistenceResultDto
                {
                    FailureReason = "invalid_window_definition_id"
                };
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowPlacementPersistenceResultDto>.Fail(
                    TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest,
                    "windowDefinitionId is required.",
                    invalid);
            }

            var resolution = ResolvePlacementWindowDefinition(definitionId);
            if (!resolution.Success)
            {
                var missing = new TvAirToolWindowPlacementPersistenceResultDto
                {
                    FailureReason = "window_definition_not_found"
                };
                _log.Add("PLUGIN_WINDOW_PLACEMENT_PERSISTENCE", _pluginDisplayName,
                    $"result=FAILED pluginId={SafePluginLog(_pluginId)} requestedWindowDefinitionId={SafePluginLog(definitionId)} resolvedWindowDefinitionId=- rememberPlacement={request.RememberPlacement} clearSavedPlacement={request.ClearSavedPlacement} savedPlacementCleared=False failureReason=window_definition_not_found rule=plugin_window_definition_normalization_contract");
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowPlacementPersistenceResultDto>.Fail(
                    TvAIrPlugin.Runtime.TvAirErrorCode.EntityNotFound,
                    "Window definition was not found.",
                    missing);
            }

            var update = _sessions.SetPlacementPersistence(
                _pluginId,
                resolution.CanonicalWindowDefinitionId,
                request.RememberPlacement,
                request.ClearSavedPlacement);
            var value = new TvAirToolWindowPlacementPersistenceResultDto
            {
                RememberPlacementApplied = update.RememberPlacementApplied,
                SavedPlacementCleared = update.SavedPlacementCleared,
                FailureReason = update.FailureReason
            };

            _log.Add("PLUGIN_WINDOW_PLACEMENT_PERSISTENCE", _pluginDisplayName,
                $"result={(update.Success ? "OK" : "FAILED")} pluginId={SafePluginLog(_pluginId)} requestedWindowDefinitionId={SafePluginLog(definitionId)} resolvedWindowDefinitionId={SafePluginLog(resolution.CanonicalWindowDefinitionId)} resolutionSource={SafePluginLog(resolution.Source)} rememberPlacement={request.RememberPlacement} clearSavedPlacement={request.ClearSavedPlacement} savedPlacementCleared={update.SavedPlacementCleared} failureReason={SafePluginLog(update.FailureReason)} rule=plugin_window_definition_normalization_contract");

            if (update.Success)
                return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowPlacementPersistenceResultDto>.Ok(value);

            var errorCode = update.FailureReason switch
            {
                "invalid_window_definition_id" => TvAIrPlugin.Runtime.TvAirErrorCode.InvalidRequest,
                "permission_denied" => TvAIrPlugin.Runtime.TvAirErrorCode.PermissionDenied,
                "placement_store_unavailable" => TvAIrPlugin.Runtime.TvAirErrorCode.CapabilityUnavailable,
                _ => TvAIrPlugin.Runtime.TvAirErrorCode.InternalError
            };
            return TvAIrPlugin.Runtime.TvAirOperationResult<TvAirToolWindowPlacementPersistenceResultDto>.Fail(
                errorCode,
                update.FailureReason,
                value);
        }

        public void CloseToolWindow(string windowId)
        {
            _permissions.Require(nameof(WindowsApi), PluginPermission.ShowUi);
            _permissions.Require(nameof(WindowsApi), PluginPermission.OpenToolWindow);
            var session = _sessions.Get(windowId);
            if (session is null || !string.Equals(session.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase)) return;
            _toolWindows.Close(windowId);
        }

        public IReadOnlyList<TvAirToolWindowStateDto> ListToolWindows()
        {
            _permissions.Require(nameof(WindowsApi), PluginPermission.ShowUi);
            return _sessions.ListByPluginId(_pluginId)
                .Select(s => new TvAirToolWindowStateDto
                {
                    WindowId = s.WindowId,
                    Title = s.Title,
                    IsOpen = !s.IsClosed && _toolWindows.IsHostAlive(s.WindowId)
                })
                .ToList();
        }

        private PlacementWindowDefinitionResolution ResolvePlacementWindowDefinition(string requestedDefinitionId)
        {
            var requested = (requestedDefinitionId ?? string.Empty).Trim();
            var runtimeDefinitions = _registry.GetRuntimePlugins()
                .Where(plugin => string.Equals(PluginIdentity.Normalize(plugin.Descriptor.PluginId), _pluginId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(plugin => plugin.Descriptor.Windows)
                .Select(definition => (definition.WindowDefinitionId ?? string.Empty).Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var pluginRoutes = _registry.GetRuntimePlugins()
                .Where(plugin => string.Equals(PluginIdentity.Normalize(plugin.Descriptor.PluginId), _pluginId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(plugin => plugin.Descriptor.UiDefinitions.Select(ui => (ui.Route ?? string.Empty).Trim().Trim('/')))
                .Where(route => route.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var sessionDefinitionIds = _sessions.ListByPluginId(_pluginId)
                .Where(session => !session.IsClosed)
                .Select(session => (session.WindowDefinitionId ?? string.Empty).Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var directSession = sessionDefinitionIds.FirstOrDefault(id => string.Equals(id, requested, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(directSession))
                return new PlacementWindowDefinitionResolution(true, directSession, "active_session_exact");

            var declared = runtimeDefinitions.Any(id => string.Equals(id, requested, StringComparison.OrdinalIgnoreCase));
            var routeAlias = pluginRoutes.FirstOrDefault(route => string.Equals(route, requested, StringComparison.OrdinalIgnoreCase));
            var conventionalMain = requested.Equals("main", StringComparison.OrdinalIgnoreCase)
                && runtimeDefinitions.Length == 0
                && pluginRoutes.Length == 1;

            if (!declared && string.IsNullOrWhiteSpace(routeAlias) && !conventionalMain)
                return new PlacementWindowDefinitionResolution(false, string.Empty, "not_found");

            if (sessionDefinitionIds.Length == 1)
                return new PlacementWindowDefinitionResolution(true, sessionDefinitionIds[0], "active_session_single");

            if (!string.IsNullOrWhiteSpace(routeAlias))
                return new PlacementWindowDefinitionResolution(true, routeAlias, "route_exact");

            if (pluginRoutes.Length == 1)
                return new PlacementWindowDefinitionResolution(true, pluginRoutes[0], declared ? "declared_to_single_route" : "default_main_to_single_route");

            return declared
                ? new PlacementWindowDefinitionResolution(true, requested, "declared_definition")
                : new PlacementWindowDefinitionResolution(false, string.Empty, "ambiguous");
        }

        private sealed record PlacementWindowDefinitionResolution(bool Success, string CanonicalWindowDefinitionId, string Source);

        private static int ToPositiveInt(double? value, int fallback)
        {
            if (!value.HasValue) return fallback;
            if (value.Value <= 0) return fallback;
            if (value.Value >= int.MaxValue) return int.MaxValue;
            return (int)Math.Round(value.Value);
        }

        private static string BuildAbsoluteLocalUrl(int port, string relativeOrAbsolute)
        {
            var target = string.IsNullOrWhiteSpace(relativeOrAbsolute) ? "/" : relativeOrAbsolute.Trim();
            if (Uri.TryCreate(target, UriKind.Absolute, out _)) return target;
            if (!target.StartsWith('/')) target = "/" + target;
            return $"http://127.0.0.1:{port}{target}";
        }

        private static string BuildToolWindowNavigationUrl(string windowUrl, PluginWindowSession session)
        {
            var target = string.IsNullOrWhiteSpace(windowUrl) ? session.ContentRoute : windowUrl.Trim();
            var fragmentIndex = target.IndexOf('#');
            var fragment = fragmentIndex >= 0 ? target[fragmentIndex..] : string.Empty;
            if (fragmentIndex >= 0) target = target[..fragmentIndex];

            var sep = target.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return target + sep
                + "__tvairWindowId=" + Uri.EscapeDataString(session.WindowId)
                + "&__tvairHostWindow=1"
                + "&__tvairToolHostContent=1"
                + "&_tvairWindowRevision=" + session.Revision.ToString(CultureInfo.InvariantCulture)
                + "&__tvairToolHost=1"
                + fragment;
        }
    }

    private sealed class PluginStorageApi : ITvAirPluginStorageApi
    {
        private readonly TvAIr.Plugin.RuntimeHost.PersistentPluginStorage _storage;
        private readonly CapabilityPermissionGate _permissions;

        public PluginStorageApi(
            TvAIr.Plugin.RuntimeHost.PersistentPluginStorage storage,
            CapabilityPermissionGate permissions)
        {
            _storage = storage;
            _permissions = permissions;
        }

        public string? ReadString(string section, string key)
        {
            _permissions.Require(nameof(PluginStorageApi), PluginPermission.ReadPluginStorage, PluginPermission.UsePluginStorage);
            var result = _storage.Get(NormalizeSection(section), NormalizeKey(key));
            if (!result.Succeeded)
            {
                if (result.Error?.Code == TvAIrPlugin.Runtime.TvAirErrorCode.EntityNotFound) return null;
                throw new InvalidOperationException(result.Error?.Message ?? "Plugin storage could not be read.");
            }
            return ToStringValue(result.Value!.Value);
        }

        public void WriteString(string section, string key, string value)
        {
            _permissions.Require(nameof(PluginStorageApi), PluginPermission.WritePluginStorage, PluginPermission.UsePluginStorage);
            RequireSucceeded(_storage.Set(NormalizeSection(section), NormalizeKey(key), value ?? string.Empty));
        }

        public int? ReadInt(string section, string key)
            => int.TryParse(ReadString(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

        public void WriteInt(string section, string key, int value)
            => WriteString(section, key, value.ToString(CultureInfo.InvariantCulture));

        public bool? ReadBool(string section, string key)
            => bool.TryParse(ReadString(section, key), out var value) ? value : null;

        public void WriteBool(string section, string key, bool value)
            => WriteString(section, key, value.ToString());

        public IReadOnlyDictionary<string, string> ReadSection(string section)
        {
            _permissions.Require(nameof(PluginStorageApi), PluginPermission.ReadPluginStorage, PluginPermission.UsePluginStorage);
            var normalizedSection = NormalizeSection(section);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in _storage.ListKeys(normalizedSection))
            {
                var entry = _storage.Get(normalizedSection, key);
                if (!entry.Succeeded) continue;
                result[key] = ToStringValue(entry.Value!.Value) ?? string.Empty;
            }
            return result;
        }

        private static string NormalizeSection(string section)
            => SanitizeStorageSection(section);

        private static string NormalizeKey(string key)
            => string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("Storage key is required.", nameof(key)) : key;

        private static string? ToStringValue(object? value)
        {
            if (value is null) return null;
            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetRawText(),
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => element.GetRawText()
                };
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static void RequireSucceeded(TvAIrPlugin.Runtime.TvAirOperationResult result)
        {
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Error?.Message ?? "Plugin storage operation failed.");
        }

        private static void RequireSucceeded<T>(TvAIrPlugin.Runtime.TvAirOperationResult<T> result)
        {
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Error?.Message ?? "Plugin storage operation failed.");
        }
    }

    private sealed class EventsApi : ITvAirEventsApi
    {
        private readonly string pluginId;
        private readonly LogRepository log;
        private readonly PluginTypedEventHub typedEvents;
        private readonly CapabilityPermissionGate permissions;

        public EventsApi(string pluginId, LogRepository log, PluginTypedEventHub typedEvents, CapabilityPermissionGate permissions)
        {
            this.pluginId = pluginId;
            this.log = log;
            this.typedEvents = typedEvents;
            this.permissions = permissions;
        }

        public IDisposable Subscribe(TvAirEventType eventType, Action<TvAirEventDto> handler)
        {
            if (handler is null) return new Subscription(() => { });
            RequireEventSubscription(eventType);
            if (eventType == TvAirEventType.LogAdded)
            {
                // Developer Diagnostics is intentionally absent from public builds.
#if TVAIR_DEVELOPER_DIAGNOSTICS
                void OnLog(LogEntry entry) => handler(ToLogAddedEventDto(entry));
                log.EntryAdded += OnLog;
                return new Subscription(() => log.EntryAdded -= OnLog);
#else
                return new Subscription(() => { });
#endif
            }
            return typedEvents.Subscribe(pluginId, eventType, handler);
        }

        public IDisposable SubscribeAll(Action<TvAirEventDto> handler)
        {
            if (handler is null) return new Subscription(() => { });
            permissions.Require(nameof(EventsApi), PluginPermission.UseSafeEvent);
            var typed = typedEvents.Subscribe(pluginId, null, handler);
#if TVAIR_DEVELOPER_DIAGNOSTICS
            if (!permissions.Has(PluginPermission.ReadLogs)) return typed;
            void OnLog(LogEntry entry) => handler(ToLogAddedEventDto(entry));
            log.EntryAdded += OnLog;
            return new Subscription(() => { typed.Dispose(); log.EntryAdded -= OnLog; });
#else
            return typed;
#endif
        }

        private void RequireEventSubscription(TvAirEventType eventType)
        {
            if (eventType == TvAirEventType.LogAdded) permissions.Require(nameof(EventsApi), PluginPermission.ReadLogs);
            else permissions.Require(nameof(EventsApi), PluginPermission.UseSafeEvent);
        }
    }

    private sealed class ExternalJobsApi(CapabilityPermissionGate permissions) : ITvAirExternalJobsApi
    {
        public TvAirExternalJobDto Enqueue(TvAirExternalJobRequestDto request)
        {
            permissions.Require(nameof(ExternalJobsApi), PluginPermission.LaunchExternalProcess);
            return new TvAirExternalJobDto
            {
                JobId = string.Empty,
                Kind = request.Kind,
                State = "NotAvailable",
                ReservationId = request.ReservationId,
                Accepted = false,
                Message = "External job execution is not connected yet."
            };
        }

        public TvAirExternalJobDto? Get(string jobId)
        {
            permissions.Require(nameof(ExternalJobsApi), PluginPermission.LaunchExternalProcess);
            return null;
        }

        public IReadOnlyList<TvAirExternalJobDto> List(TvAirExternalJobQueryDto? query = null)
        {
            permissions.Require(nameof(ExternalJobsApi), PluginPermission.LaunchExternalProcess);
            return Array.Empty<TvAirExternalJobDto>();
        }

        public bool Cancel(string jobId)
        {
            permissions.Require(nameof(ExternalJobsApi), PluginPermission.LaunchExternalProcess);
            return false;
        }
    }

    private sealed class HostsApi(CapabilityPermissionGate permissions) : ITvAirHostsApi
    {
        public TvAirHostInfoDto GetSelf() { permissions.Require(nameof(HostsApi), PluginPermission.ReadSystemStatus); return new() { HostId = Environment.MachineName, DisplayName = Environment.MachineName, IsSelf = true }; }
        public IReadOnlyList<TvAirHostInfoDto> ListPeers() { permissions.Require(nameof(HostsApi), PluginPermission.ReadSystemStatus); return new[] { GetSelf() }; }
        public TvAirHostStatusDto? GetPeerStatus(string hostId) { permissions.Require(nameof(HostsApi), PluginPermission.ReadSystemStatus); return string.Equals(hostId, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? new TvAirHostStatusDto { HostId = Environment.MachineName, IsOnline = true, State = "Self" }
            : null; }
    }

    private sealed class PluginsApi(PluginRegistry registry, CapabilityPermissionGate permissions) : ITvAirPluginsApi
    {
        public IReadOnlyList<TvAirAnalysisPluginDto> ListAnalysisPlugins()
        {
            permissions.Require(nameof(PluginsApi), PluginPermission.ReadHostContracts);
            return registry.GetRuntimePlugins()
                .Where(plugin => plugin is ITvAirRuntimeAnalysisPlugin)
                .Select(plugin => new TvAirAnalysisPluginDto
                {
                    PluginName = plugin.Descriptor.DisplayName,
                    PluginVersion = plugin.Descriptor.Version
                })
                .ToList();
        }

        public IReadOnlyList<TvAirAnalysisExecutionDto> Analyze(AnalysisContext context)
        {
            permissions.Require(nameof(PluginsApi), PluginPermission.ReadHostContracts);
            ArgumentNullException.ThrowIfNull(context);

            var results = new List<TvAirAnalysisExecutionDto>();
            foreach (var runtimePlugin in registry.GetRuntimePlugins())
            {
                if (runtimePlugin is not ITvAirRuntimeAnalysisPlugin plugin)
                    continue;

                var descriptor = runtimePlugin.Descriptor;
                try
                {
                    var raw = plugin.Analyze(context) ?? new AnalysisResult();
                    var normalized = new AnalysisResult
                    {
                        PluginName = string.IsNullOrWhiteSpace(raw.PluginName) ? descriptor.DisplayName : raw.PluginName,
                        PluginVersion = string.IsNullOrWhiteSpace(raw.PluginVersion) ? descriptor.Version : raw.PluginVersion,
                        Score = Math.Clamp(raw.Score, 0, 100),
                        Summary = raw.Summary ?? string.Empty,
                        Reasons = raw.Reasons?.ToList() ?? new List<string>(),
                        Metrics = raw.Metrics?.Select(m => new AnalysisMetric
                        {
                            Label = m.Label ?? string.Empty,
                            Value = m.Value,
                            Unit = m.Unit ?? string.Empty
                        }).ToList() ?? new List<AnalysisMetric>()
                    };
                    results.Add(new TvAirAnalysisExecutionDto
                    {
                        PluginName = descriptor.DisplayName,
                        PluginVersion = descriptor.Version,
                        Succeeded = true,
                        Result = normalized
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new TvAirAnalysisExecutionDto
                    {
                        PluginName = descriptor.DisplayName,
                        PluginVersion = descriptor.Version,
                        Succeeded = false,
                        Error = ex.Message,
                        Result = new AnalysisResult
                        {
                            PluginName = descriptor.DisplayName,
                            PluginVersion = descriptor.Version,
                            Score = 0,
                            Summary = "分析プラグインの実行に失敗しました。",
                            Reasons = new List<string> { "本体処理は継続しています。プラグイン側の更新または削除で復旧できます。" },
                            Metrics = Array.Empty<AnalysisMetric>()
                        }
                    });
                }
            }
            return results;
        }

        public IReadOnlyList<TvAirLoadedPluginDto> ListLoaded()
        {
            permissions.Require(nameof(PluginsApi), PluginPermission.ReadHostContracts);
            var rows = new List<(string Id, string DisplayName, string Version, string Kind, string? Sdk, IReadOnlyList<string> Capabilities, IReadOnlyList<PluginPermission> Permissions)>();
            foreach (var p in registry.GetRuntimePlugins())
                rows.Add((p.Descriptor.PluginId, p.Descriptor.DisplayName, p.Descriptor.Version, "Runtime", p.Descriptor.SdkContractVersion, p.Descriptor.RequiredCapabilities, p.Descriptor.RequiredPermissions));

            return rows.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => new TvAirLoadedPluginDto
            {
                PluginId = g.Key,
                DisplayName = g.Select(x => x.DisplayName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? g.Key,
                Version = g.Select(x => x.Version).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
                IsLoaded = true,
                ContractKinds = g.Select(x => x.Kind).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
                SdkContractVersion = g.Select(x => x.Sdk).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                RequiredCapabilities = g.SelectMany(x => x.Capabilities).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
                Permissions = g.SelectMany(x => x.Permissions).Distinct().OrderBy(x => x).ToArray(),
                UsesRuntimeContext = g.Any(x => string.Equals(x.Kind, "Runtime", StringComparison.OrdinalIgnoreCase))
            }).OrderBy(x => x.PluginId, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private static TvAirEventDto ToLogAddedEventDto(LogEntry entry)
    {
        var details = ParseKeyValueDetails(entry.Message);
        details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
        {
            ["event"] = entry.Event ?? string.Empty,
            ["title"] = entry.Title ?? string.Empty,
            ["message"] = entry.Message ?? string.Empty
        };
        return new TvAirEventDto
        {
            Timestamp = new DateTimeOffset(entry.CreatedAt),
            EventType = TvAirEventType.LogAdded,
            ReservationId = ResolveReservationId(entry, details),
            ServiceName = ResolveDetail(details, "service", "serviceName"),
            ProgramTitle = ResolveDetail(details, "programTitle", "title", "successorTitle"),
            Details = details
        };
    }

    private static string ResolveProgramGuideChangeKind(LogEntry entry)
    {
        var text = $"{entry.Event} {entry.Title} {entry.Message}";
        if (ContainsAny(text, "EPG_RUN_PARTIAL")) return "PartiallyUpdated";
        if (ContainsAny(text, "EPG_RUN_OK", "EPG_IMPORTED", "EPG_COMPLETION")) return "Updated";
        return "Changed";
    }

    private static string? ResolveReservationId(LogEntry entry, IReadOnlyDictionary<string, string> details)
    {
        var raw = ResolveDetail(details, "reservationId", "reservation", "predecessor", "successor");
        if (!string.IsNullOrWhiteSpace(raw)) return raw;
        var token = ExtractToken(entry.Message, "reservationId=") ?? ExtractToken(entry.Message, "reservation=");
        return token;
    }

    private static string? ResolveDetail(IReadOnlyDictionary<string, string> details, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static bool ContainsAny(string? source, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        return values.Any(v => source.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    private static ReservationIntent NormalizeReservationIntent(TvAirReservationIntent intent)
        => intent switch
        {
            TvAirReservationIntent.InteractiveProgramEvent => ReservationIntent.InteractiveProgramEvent,
            TvAirReservationIntent.ProgramTimeSlot => ReservationIntent.ProgramTimeSlot,
            TvAirReservationIntent.AutomaticSearch => ReservationIntent.AutomaticSearch,
            TvAirReservationIntent.KeywordRule => ReservationIntent.KeywordRule,
            TvAirReservationIntent.System => ReservationIntent.System,
            _ => ReservationIntent.Unspecified
        };

    private static TvAirReservationDto ToDto(Reservation r, PluginReadModelSource readModels) => new()
    {
        ReservationId = FormatReservationId(r.Id),
        ServiceName = readModels.ResolveCurrentServiceName(r),
        ProgramTitle = r.Title,
        Source = r.Source.ToString(),
        Intent = ReservationIntentContract.IsSystem(r.Intent)
            ? TvAirReservationIntent.System
            : Enum.TryParse<TvAirReservationIntent>(r.Intent.ToString(), out var intent) ? intent : TvAirReservationIntent.Unspecified,
        CreatedThrough = r.CreatedThrough,
        CreatedByPluginId = r.CreatedByPluginId,
        Route = r.IsUserChain ? "user_chain" : r.Source.ToString(),
        Status = r.Status.ToString(),
        Start = new DateTimeOffset(r.StartTime),
        End = new DateTimeOffset(r.EndTime),
        ScheduledStart = r.ScheduledStartTime.HasValue ? new DateTimeOffset(r.ScheduledStartTime.Value) : null,
        TunerName = string.IsNullOrWhiteSpace(r.TunerName) ? null : r.TunerName,
        PredecessorReservationId = r.UserChainPreviousId.HasValue ? FormatReservationId(r.UserChainPreviousId.Value) : null,
        ChainRootReservationId = r.UserChainRootId.HasValue ? FormatReservationId(r.UserChainRootId.Value) : null,
        IsUserChain = r.IsUserChain,
        IsEnabled = r.IsEnabled,
        HasConflict = r.IsConflicted,
        NetworkId = r.NetworkId,
        TransportStreamId = r.TransportStreamId,
        ServiceId = r.ServiceId,
        EventNumber = r.EventId,
        PlannedTunerName = string.IsNullOrWhiteSpace(r.TunerName) ? null : r.TunerName,
        ActualTunerName = string.IsNullOrWhiteSpace(r.ActualTunerName) ? null : r.ActualTunerName,
        RecordingStartedAt = r.RecordingStartedAt.HasValue ? new DateTimeOffset(r.RecordingStartedAt.Value) : null,
        RecordingFinishedAt = r.RecordingFinishedAt.HasValue ? new DateTimeOffset(r.RecordingFinishedAt.Value) : null,
        SourceRuleId = r.SourceRuleId,
        SourceRuleName = r.SourceRuleName ?? string.Empty,
        ReservationNumber = r.Id,
        CreatedAt = new DateTimeOffset(r.CreatedAt),
        UpdatedAt = new DateTimeOffset(r.UpdatedAt)
    };

    private static TvAirRecordingFileDto ToFileDto(Reservation reservation, string? filePath)
    {
        FileInfo? info = null;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            info = new FileInfo(filePath);
        }
        return new TvAirRecordingFileDto
        {
            ReservationId = FormatReservationId(reservation.Id),
            FilePath = filePath ?? string.Empty,
            Exists = info is not null,
            SizeBytes = info?.Length,
            CreatedAt = info is null ? null : new DateTimeOffset(info.CreationTime),
            LastWriteAt = info is null ? null : new DateTimeOffset(info.LastWriteTime)
        };
    }


    private static TvAirProgramEventDto ToDto(ProjectedProgramEvent e, PluginReadModelSource readModels)
    {
        var projectionSafe = EpgTitleProjectionGuard.IsSafeForSpecialProjection(e.ToEpgEvent(), out var unsafeReason);
        return new TvAirProgramEventDto
        {
            EventId = string.Create(CultureInfo.InvariantCulture, $"{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}"),
            ProjectionEventKey = e.Key.Value,
            ServiceName = readModels.ResolveCurrentServiceName(e.NetworkId, e.TransportStreamId, e.ServiceId, e.ServiceName),
            NetworkId = e.NetworkId,
            TransportStreamId = e.TransportStreamId,
            ServiceId = e.ServiceId,
            EventNumber = e.EventId,
            Start = new DateTimeOffset(e.Start),
            End = new DateTimeOffset(e.End),
            Title = e.Title,
            Summary = e.ShortText,
            Detail = e.ExtendedText,
            Genre = e.Genre,
            GenreCodes = e.GenreCodes,
            DurationSeconds = e.DurationSeconds,
            ExtendedItems = e.ExtendedItems,
            UpdatedAt = e.UpdatedAt == default ? null : new DateTimeOffset(e.UpdatedAt),
            DbUpdatedAt = e.DbEvent?.UpdatedAt is { } dbUpdatedAt ? new DateTimeOffset(dbUpdatedAt) : null,
            IsSafeForSpecialProjection = projectionSafe,
            SpecialProjectionUnsafeReason = unsafeReason ?? string.Empty,
            ProjectionState = e.ProjectionState,
            SourceKind = e.SourceKind,
            SourcePluginId = e.SourcePluginId,
            SourceEventKey = e.SourceEventKey,
            DbEventExists = e.DbEventExists
        };
    }

    private static (TvAirRecordingResultDto? Result, string Genre, string GenreCodes) ResolveRecordingProgramMetadata(
        PluginReadModelSource readModels,
        RecordingResultStore recordingResults,
        Reservation reservation)
    {
        var reservationId = FormatReservationId(reservation.Id);
        var result = recordingResults.Get(reservationId);
        var genre = result?.Genre?.Trim() ?? string.Empty;
        var genreCodes = result?.GenreCodes?.Trim() ?? string.Empty;
        var needsIdentity = result is null
            || result.NetworkId == 0
            || result.TransportStreamId == 0
            || result.ServiceId == 0
            || result.EventId == 0;
        var needsGenre = string.IsNullOrWhiteSpace(genre) && string.IsNullOrWhiteSpace(genreCodes);

        ProjectedProgramEvent? program = null;
        if ((needsIdentity || needsGenre) && reservation.EventId != 0)
        {
            program = readModels.GetProgramEvent(
                reservation.NetworkId,
                reservation.TransportStreamId,
                reservation.ServiceId,
                reservation.EventId);
            if (program is not null)
            {
                genre = string.IsNullOrWhiteSpace(genre) ? EpgProjection.GenreLabel(program.Genre, program.GenreCodes) : genre;
                genreCodes = string.IsNullOrWhiteSpace(genreCodes) ? program.GenreCodes?.Trim() ?? string.Empty : genreCodes;
            }
        }

        // RECORDING_RESULT_IMMUTABLE_AFTER_FINALIZE:
        // Plugin read-model projection must never mutate RecordingResultStore. A finalized recording
        // result is immutable outcome evidence; missing legacy program metadata is completed only in
        // this read projection from the Reservation / ProgramEvent authorities. This keeps reads
        // side-effect free and prevents later EPG changes from rewriting historical recording results.
        return (result, genre, genreCodes);
    }

    // RECORDING_PLAYABILITY_SINGLE_SOURCE:
    // CanPlayRecording is a present-tense capability, so do not infer it from Reservation.Completed alone.
    // The finalized RecordingResult is the recording outcome authority, and the current filesystem is the
    // file-availability authority. Legacy/missing/pending results therefore remain history but are not
    // advertised as playable until canonical evidence exists. Failed/Cancelled/System EPG rows are never
    // promoted to user-playable content by this projection.
    private static bool IsPlayableCompletedRecording(Reservation reservation, TvAirRecordingResultDto? result)
    {
        if (reservation.Source == ReservationSource.Epg || reservation.Status != ReservationStatus.Completed) return false;
        if (result?.ResultFinalized != true) return false;
        if (!string.Equals(result.Result, ReservationStatus.Completed.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (result.FileCreated != true || string.IsNullOrWhiteSpace(result.FilePath)) return false;
        return File.Exists(result.FilePath);
    }

    private static bool Contains(string? source, string? value)
        => !string.IsNullOrWhiteSpace(value) && (source ?? string.Empty).Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string FormatReservationId(int id) => $"R{id}";

    private static bool TryToServiceIdentity(int networkId, int transportStreamId, int serviceId, out ushort nid, out ushort tsid, out ushort sid)
    {
        nid = 0;
        tsid = 0;
        sid = 0;
        return TryToUShort(networkId, out nid)
            && TryToUShort(transportStreamId, out tsid)
            && TryToUShort(serviceId, out sid);
    }

    private static bool TryToUShort(int value, out ushort result)
    {
        if (value is < ushort.MinValue or > ushort.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (ushort)value;
        return true;
    }

    private static int? ParseReservationId(string? reservationId)
    {
        if (string.IsNullOrWhiteSpace(reservationId)) return null;
        var v = reservationId.Trim();
        if (v.StartsWith('R') || v.StartsWith('r')) v = v[1..];
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static string? ExtractToken(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + key.Length;
        var end = text.IndexOf(' ', start);
        return (end < 0 ? text[start..] : text[start..end]).Trim(';', ',');
    }

    private static IReadOnlyDictionary<string, string> ParseKeyValueDetails(string? text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return dict;
        foreach (var token in text.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = token.IndexOf('=');
            if (idx <= 0) continue;
            var key = token[..idx].Trim();
            var value = token[(idx + 1)..].Trim().Trim(',');
            if (key.Length > 0) dict[key] = value;
        }
        return dict;
    }

    private static string SanitizeStorageSection(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "Plugin" : value.Trim();
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_').ToArray();
        return new string(chars);
    }

    private static string SafePluginLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var s = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= 160 ? s : s[..160] + "…";
    }
}
