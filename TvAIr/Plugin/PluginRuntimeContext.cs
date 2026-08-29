using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TvAIrPlugin;
using TvAIrPlugin.Assets;
using TvAIrPlugin.Bridge;
using TvAIrPlugin.Data;
using TvAIrPlugin.Events;
using TvAIrPlugin.Notifications;
using TvAIrPlugin.Overlay;
using TvAIrPlugin.Pickers;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Storage;
using TvAIrPlugin.Surfaces;
using TvAIrPlugin.Windows;
using TvAIrPlugin.WebRuntime;
using TvAIr.Plugin.RuntimeHost;

namespace TvAIr.Plugin;

/// <summary>
/// Runtime SDK context. Runtime, window, surface, and host-surface ownership is delegated to
/// independent host services. Host capability APIs are projected into the Runtime contract by dedicated adapters owned by this context.
/// </summary>
internal sealed class PluginRuntimeContext : ITvAirPluginRuntimeContext, IDisposable
{
    public PluginRuntimeContext(
        ITvAirPluginContext hostApis,
        TvAirPluginRuntimeDescriptor descriptor,
        Assembly pluginAssembly,
        TvAIr.Core.LogRepository log,
        PluginScopedServiceFactory scopedServices,
        IReadOnlyCollection<PluginPermission> permissions,
        PluginPathPickerHostService pathPickerHost,
        Func<string, VideoOverlayViewerTarget?>? resolveViewerTarget = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var pluginId = scopedServices.NormalizePluginId(descriptor.PluginId, descriptor.DisplayName);
        _runtimeManager = new PluginRuntimeManager(pluginId, descriptor.SdkContractVersion);
        _surfaceManager = new PluginSurfaceManager(_runtimeManager, descriptor.Surfaces);
        _windowManager = new PluginWindowManager(_runtimeManager, descriptor.Windows, hostApis.Windows);
        _hostSurfaceRegistry = new HostSurfaceRegistry(_runtimeManager, _surfaceManager, _windowManager, resolveViewerTarget ?? (_ => null));

        Runtime = _runtimeManager;
        Windows = _windowManager;
        Surfaces = _surfaceManager;
        HostSurfaces = _hostSurfaceRegistry;
        _assetCatalog = new PluginAssetCatalog(pluginId, pluginAssembly, descriptor.Assets);
        Assets = _assetCatalog;
        _bridgeManager = new PluginBridgeManager(_runtimeManager, descriptor.RequiredCapabilities);
        Bridge = _bridgeManager;
        _eventChannel = new PluginEventChannel(_runtimeManager, hostApis.Events, log);
        Events = _eventChannel;
        _eventChannel.PublishRuntimeConnected();
        _webRuntimeManager = new PluginWebRuntimeManager(_runtimeManager, _surfaceManager, _hostSurfaceRegistry, _windowManager, _assetCatalog, _bridgeManager, _eventChannel, log);
        WebRuntime = _webRuntimeManager;
        _videoOverlayHost = new VideoOverlayHost(pluginId, resolveViewerTarget ?? (_ => null), log, permissions);
        VideoOverlay = _videoOverlayHost;
        Data = new DataApi(hostApis.Reservations, hostApis.Recordings, hostApis.ProgramGuide, hostApis.Channels, hostApis.Tuners);
        Notifications = new NotificationHost(hostApis.Notifications);
        PathPicker = new RuntimePathPickerApi(pluginId, permissions, pathPickerHost);
        Storage = scopedServices.CreateRuntimeStorage(pluginId);
        RegisterBridgeMethods();

        Reservations = hostApis.Reservations;
        Rules = hostApis.Rules;
        Recordings = hostApis.Recordings;
        PlaybackProgress = hostApis.PlaybackProgress;
        MediaInsights = hostApis.MediaInsights;
        ContentDiscovery = hostApis.ContentDiscovery;
        ProgramGuide = hostApis.ProgramGuide;
        ExternalProgramSource = hostApis.ExternalProgramSource;
        Channels = hostApis.Channels;
        Tuners = hostApis.Tuners;
        Viewers = new RuntimeViewersApi(hostApis.Viewers);
        TimedTextStreams = hostApis.TimedTextStreams;
        Backup = hostApis.Backup;
        Settings = hostApis.Settings;
        System = hostApis.System;
        Logs = hostApis.Logs;
        Plugins = hostApis.Plugins;
    }

    private readonly PluginRuntimeManager _runtimeManager;
    private readonly PluginSurfaceManager _surfaceManager;
    private readonly PluginWindowManager _windowManager;
    private readonly HostSurfaceRegistry _hostSurfaceRegistry;
    private readonly PluginAssetCatalog _assetCatalog;
    private readonly PluginBridgeManager _bridgeManager;
    private readonly PluginWebRuntimeManager _webRuntimeManager;
    private readonly PluginEventChannel _eventChannel;
    private readonly VideoOverlayHost _videoOverlayHost;
    private readonly TvAIr.Core.LogRepository _log;

    public ITvAirRuntimeApi Runtime { get; }
    public ITvAirPluginBridgeApi Bridge { get; }
    public ITvAirPluginWebRuntimeApi WebRuntime { get; }
    public ITvAirPluginWindowsApi Windows { get; }
    public ITvAirPluginSurfacesApi Surfaces { get; }
    public ITvAirHostSurfacesApi HostSurfaces { get; }
    public ITvAirPluginAssetsApi Assets { get; }
    public ITvAirPluginEventsApi Events { get; }
    public ITvAirVideoOverlayApi VideoOverlay { get; }
    public ITvAirDataApi Data { get; }
    public ITvAirPluginNotificationsApi Notifications { get; }
    public ITvAirPathPickerApi PathPicker { get; }
    public TvAIrPlugin.Storage.ITvAirPluginStorageApi Storage { get; }
    public ITvAirReservationsApi Reservations { get; }
    public ITvAirRulesApi Rules { get; }
    public ITvAirRecordingsApi Recordings { get; }
    public ITvAirPlaybackProgressApi PlaybackProgress { get; }
    public ITvAirMediaInsightsApi MediaInsights { get; }
    public ITvAirContentDiscoveryApi ContentDiscovery { get; }
    public ITvAirProgramGuideApi ProgramGuide { get; }
    public ITvAirExternalProgramSourceApi ExternalProgramSource { get; }
    public ITvAirChannelsApi Channels { get; }
    public ITvAirTunersApi Tuners { get; }
    public TvAIrPlugin.Viewers.ITvAirViewersApi Viewers { get; }
    public ITvAirTimedTextStreamsApi TimedTextStreams { get; }
    public ITvAirBackupApi Backup { get; }
    public ITvAirSettingsApi Settings { get; }
    public ITvAirSystemApi System { get; }
    public ITvAirLogsApi Logs { get; }
    public ITvAirPluginsApi Plugins { get; }


    private sealed class RuntimePathPickerApi : ITvAirPathPickerApi
    {
        private readonly string _pluginId;
        private readonly HashSet<PluginPermission> _permissions;
        private readonly PluginPathPickerHostService _host;

        public RuntimePathPickerApi(string pluginId, IReadOnlyCollection<PluginPermission> permissions, PluginPathPickerHostService host)
        {
            _pluginId = pluginId;
            _permissions = new HashSet<PluginPermission>(permissions ?? Array.Empty<PluginPermission>());
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public Task<PluginPathPickerResult> PickFileAsync(PluginFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!_permissions.Contains(PluginPermission.UsePathPicker))
                return Task.FromResult(PluginPathPickerResult.Fail("permission_denied", "Path Pickerの利用権限がありません。"));
            return _host.PickFileAsync(_pluginId, request, cancellationToken);
        }

        public Task<PluginPathPickerResult> PickFolderAsync(PluginFolderPickerRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!_permissions.Contains(PluginPermission.UsePathPicker))
                return Task.FromResult(PluginPathPickerResult.Fail("permission_denied", "Path Pickerの利用権限がありません。"));
            return _host.PickFolderAsync(_pluginId, request, cancellationToken);
        }
    }

    private sealed class RuntimeViewersApi : TvAIrPlugin.Viewers.ITvAirViewersApi
    {
        private readonly global::TvAIrPlugin.ITvAirViewersApi _hostApi;

        public RuntimeViewersApi(global::TvAIrPlugin.ITvAirViewersApi hostApi)
        {
            _hostApi = hostApi ?? throw new ArgumentNullException(nameof(hostApi));
        }

        public IReadOnlyList<TvAIrPlugin.Viewers.TvAirViewerProfileDto> ListProfiles()
            => _hostApi.ListProfiles().Select(ToRuntimeProfile).ToList();

        public IReadOnlyList<TvAIrPlugin.Viewers.TvAirViewerSessionDto> ListSessions()
            => _hostApi.ListSessions().Select(ToRuntimeSession).ToList();

        public TvAIrPlugin.Viewers.TvAirViewerSessionDto? GetSession(string viewerSessionId)
        {
            if (string.IsNullOrWhiteSpace(viewerSessionId)) return null;
            return _hostApi.ListSessions()
                .Where(x => string.Equals(x.ViewerSessionId, viewerSessionId.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(ToRuntimeSession)
                .FirstOrDefault();
        }

        public Task<TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto>> StartAsync(
            TvAIrPlugin.Viewers.TvAirViewerStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            global::TvAIrPlugin.TvAirViewerOperationResultDto result;
            if (request.RetuneExistingViewer)
            {
                if (string.IsNullOrWhiteSpace(request.ViewerSessionId) || !request.ExpectedGeneration.HasValue)
                {
                    return Task.FromResult(TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto>.Fail(
                        TvAirErrorCode.InvalidRequest,
                        "ViewerSessionId and ExpectedGeneration are required when RetuneExistingViewer is true."));
                }

                result = _hostApi.Retune(new global::TvAIrPlugin.TvAirViewerRetuneRequestDto
                {
                    ViewerProfileId = request.ViewerProfileId,
                    ViewerSessionId = request.ViewerSessionId,
                    ExpectedGeneration = request.ExpectedGeneration.Value,
                    NetworkId = request.Service.NetworkId,
                    TransportStreamId = request.Service.TransportStreamId,
                    ServiceId = request.Service.ServiceId,
                    ServiceName = request.Service.ServiceName,
                    PreserveViewerWindowState = request.PreserveViewerWindowState,
                    ViewerActivation = request.ViewerActivation
                });
            }
            else
            {
                result = _hostApi.Start(new global::TvAIrPlugin.TvAirViewerStartRequestDto
                {
                    ViewerProfileId = request.ViewerProfileId,
                    NetworkId = request.Service.NetworkId,
                    TransportStreamId = request.Service.TransportStreamId,
                    ServiceId = request.Service.ServiceId,
                    ServiceName = request.Service.ServiceName,
                    PreserveViewerWindowState = request.PreserveViewerWindowState,
                    ViewerActivation = request.ViewerActivation
                });
            }

            return Task.FromResult(ToRuntimeOperation(result));
        }

        public Task<TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto>> RestartAsync(
            TvAIrPlugin.Viewers.TvAirViewerRestartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _hostApi.Restart(new global::TvAIrPlugin.TvAirViewerRestartRequestDto
            {
                ViewerSessionId = request.ViewerSessionId,
                ExpectedGeneration = request.ExpectedGeneration,
                PreserveViewerWindowState = request.PreserveViewerWindowState,
                ViewerActivation = request.ViewerActivation,
                Reason = request.Reason
            });
            return Task.FromResult(ToRuntimeOperation(result));
        }

        public Task<TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto>> ActivateAsync(
            TvAIrPlugin.Viewers.TvAirViewerActivateRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _hostApi.Activate(new global::TvAIrPlugin.TvAirViewerActivateRequestDto
            {
                ViewerSessionId = request.ViewerSessionId,
                ExpectedGeneration = request.ExpectedGeneration
            });
            return Task.FromResult(ToRuntimeOperation(result));
        }

        public Task<TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto>> StopAsync(
            TvAIrPlugin.Viewers.TvAirViewerStopRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _hostApi.Stop(new global::TvAIrPlugin.TvAirViewerStopRequestDto
            {
                ViewerSessionId = request.ViewerSessionId,
                ExpectedGeneration = request.ExpectedGeneration,
                Reason = "runtime_viewer_stop"
            });
            return Task.FromResult(ToRuntimeOperation(result));
        }

        private TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto> ToRuntimeOperation(
            global::TvAIrPlugin.TvAirViewerOperationResultDto result)
        {
            var service = result.NetworkId.HasValue && result.TransportStreamId.HasValue && result.ServiceId.HasValue
                ? new TvAIrPlugin.Viewers.TvAirServiceIdentityDto
                {
                    NetworkId = checked((ushort)result.NetworkId.Value),
                    TransportStreamId = checked((ushort)result.TransportStreamId.Value),
                    ServiceId = checked((ushort)result.ServiceId.Value)
                }
                : null;
            var session = string.IsNullOrWhiteSpace(result.ViewerSessionId)
                ? null
                : new TvAIrPlugin.Viewers.TvAirViewerSessionDto
                {
                    ViewerSessionId = result.ViewerSessionId,
                    ViewerProfileId = result.ViewerProfileId,
                    LogicalViewerSlotId = result.LogicalViewerSlotId,
                    Generation = result.Generation,
                    State = string.IsNullOrWhiteSpace(result.ViewerState) ? result.State : result.ViewerState,
                    CreatedAt = result.AcquiredAt ?? DateTimeOffset.MinValue,
                    ProcessId = result.ProcessId,
                    CurrentService = service,
                    ChannelSpace = result.ChannelSpace,
                    ChannelIndex = result.ChannelIndex
                };
            var value = new TvAIrPlugin.Viewers.TvAirViewerOperationDto
            {
                State = result.State ?? string.Empty,
                ErrorCode = result.ErrorCode ?? string.Empty,
                Message = result.Message ?? string.Empty,
                ViewerProfileId = result.ViewerProfileId ?? string.Empty,
                ViewerSessionId = result.ViewerSessionId ?? string.Empty,
                Generation = result.Generation,
                LeaseId = result.LeaseId ?? string.Empty,
                ProcessId = result.ProcessId,
                CurrentService = service,
                Session = session,
                Diagnostics = result.Diagnostics ?? string.Empty,
                FocusPolicyRequested = result.FocusPolicyRequested ?? string.Empty,
                FocusPolicyApplied = result.FocusPolicyApplied ?? string.Empty,
                ForegroundBeforeHwnd = result.ForegroundBeforeHwnd,
                ForegroundBeforePid = result.ForegroundBeforePid,
                ForegroundAfterRetuneHwnd = result.ForegroundAfterRetuneHwnd,
                ForegroundAfterRetunePid = result.ForegroundAfterRetunePid,
                ForegroundFinalHwnd = result.ForegroundFinalHwnd,
                ForegroundFinalPid = result.ForegroundFinalPid,
                ForegroundChanged = result.ForegroundChanged,
                ChangedToTargetViewer = result.ChangedToTargetViewer,
                RestorationAttempted = result.RestorationAttempted,
                RestorationSucceeded = result.RestorationSucceeded,
                FocusPreserved = result.FocusPreserved,
                FocusPreserveFailureReason = result.FocusPreserveFailureReason ?? string.Empty,
                OperationCompleted = result.OperationCompleted,
                HasWarning = result.HasWarning,
                ContinuationRecommended = result.ContinuationRecommended
            };
            if (result.Success)
                return TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto>.Ok(value);

            return TvAirOperationResult<TvAIrPlugin.Viewers.TvAirViewerOperationDto>.Fail(
                MapErrorCode(result.ErrorCode),
                string.IsNullOrWhiteSpace(result.Message) ? "Viewer operation failed." : result.Message,
                value);
        }

        private static TvAirErrorCode MapErrorCode(string? errorCode)
        {
            var value = (errorCode ?? string.Empty).Trim();
            return value.ToLowerInvariant() switch
            {
                "staleviewergeneration" => TvAirErrorCode.StaleViewerGeneration,
                "channelnotfound" or
                "viewersessionnotfound" or
                "viewerleasenotfoundorprofilemismatch" or
                "viewerprocessexited" => TvAirErrorCode.EntityNotFound,
                "viewerwindowunavailable" => TvAirErrorCode.HostSurfaceUnavailable,
                "foregroundactivationrejected" => TvAirErrorCode.OperationCancelled,
                "permissiondenied" => TvAirErrorCode.PermissionDenied,
                "missingviewerpayload" or
                "viewersessionrequired" or
                "viewersessionprofilemismatch" or
                "viewersessionleasemismatch" or
                "viewerrestartidentitymismatch" or
                "viewerrestartpreflightidentitymismatch" or
                "viewerrestartresultidentitymismatch" => TvAirErrorCode.InvalidRequest,
                _ => TvAirErrorCode.InternalError
            };
        }

        private static TvAIrPlugin.Viewers.TvAirViewerProfileDto ToRuntimeProfile(global::TvAIrPlugin.TvAirViewerProfileDto source)
            => new()
            {
                ViewerProfileId = source.ViewerProfileId,
                DisplayName = source.DisplayName,
                IsAvailable = source.Enabled,
                IsDefault = source.IsDefault,
                BroadcastGroups = ToRuntimeBroadcastGroups(source.AvailableGroups),
                LogicalViewerSlotId = source.LogicalViewerSlotId,
                TvTestFrameIndex = source.TvTestFrameIndex,
                CurrentViewerSessionId = string.IsNullOrWhiteSpace(source.CurrentViewerSessionId) ? null : source.CurrentViewerSessionId,
                ErrorCode = string.IsNullOrWhiteSpace(source.ErrorCode) ? null : source.ErrorCode
            };

        private static IReadOnlyList<string> ToRuntimeBroadcastGroups(IReadOnlyList<string>? allocationGroups)
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in allocationGroups ?? Array.Empty<string>())
            {
                var group = (raw ?? string.Empty).Trim().ToUpperInvariant();
                switch (group)
                {
                    case "GR":
                        groups.Add("GR");
                        break;
                    case "BSCS":
                        groups.Add("BS");
                        groups.Add("CS");
                        break;
                    case "BS":
                        groups.Add("BS");
                        break;
                    case "CS":
                        groups.Add("CS");
                        break;
                }
            }

            return new[] { "GR", "BS", "CS" }.Where(groups.Contains).ToArray();
        }

        private static TvAIrPlugin.Viewers.TvAirViewerSessionDto ToRuntimeSession(global::TvAIrPlugin.TvAirViewerSessionDto source)
            => new()
            {
                ViewerSessionId = source.ViewerSessionId,
                ViewerProfileId = source.ViewerProfileId,
                LogicalViewerSlotId = source.LogicalViewerSlotId,
                Generation = source.Generation,
                State = source.State,
                CreatedAt = source.AcquiredAt,
                ProcessId = source.ProcessId,
                CurrentService = source.NetworkId.HasValue && source.TransportStreamId.HasValue && source.ServiceId.HasValue
                    ? new TvAIrPlugin.Viewers.TvAirServiceIdentityDto
                    {
                        NetworkId = checked((ushort)source.NetworkId.Value),
                        TransportStreamId = checked((ushort)source.TransportStreamId.Value),
                        ServiceId = checked((ushort)source.ServiceId.Value),
                        ServiceName = source.ServiceName
                    }
                    : null,
                ChannelSpace = source.ChannelSpace,
                ChannelIndex = source.ChannelIndex
            };
    }

    private void RegisterBridgeMethods()
    {
        _bridgeManager.Register<EmptyBridgeRequest, RuntimeBridgeResponse>(
            TvAirRuntimeBridgeMethods.RuntimeGetInfo,
            (_, _) => Task.FromResult(new RuntimeBridgeResponse(Runtime.PluginId, Runtime.RuntimeSessionId, Runtime.SdkContractVersion)));
        _bridgeManager.Register<EmptyBridgeRequest, IReadOnlyList<PluginBridgeCapabilityDescriptor>>(
            TvAirRuntimeBridgeMethods.RuntimeListMethods,
            (_, _) => Task.FromResult(Bridge.ListMethods()));
        _bridgeManager.Register<EmptyBridgeRequest, IReadOnlyList<TvAirLoadedPluginDto>>(
            TvAirRuntimeBridgeMethods.PluginsListLoaded,
            (_, _) => Task.FromResult(Plugins.ListLoaded()),
            TvAirRuntimeCapabilities.PluginsRead);
        _bridgeManager.Register<EmptyBridgeRequest, IReadOnlyList<TvAirAnalysisPluginDto>>(
            TvAirRuntimeBridgeMethods.PluginsListAnalysis,
            (_, _) => Task.FromResult(Plugins.ListAnalysisPlugins()),
            TvAirRuntimeCapabilities.PluginsRead);
        _bridgeManager.Register<AnalysisContext, IReadOnlyList<TvAirAnalysisExecutionDto>>(
            TvAirRuntimeBridgeMethods.PluginsAnalyze,
            (request, _) => Task.FromResult(Plugins.Analyze(request)),
            TvAirRuntimeCapabilities.PluginsRead);
        _bridgeManager.Register<RuntimeStatusBridgeRequest, RuntimeStatusBridgeResponse>(
            TvAirRuntimeBridgeMethods.RuntimeReportStatus,
            (request, _) =>
            {
                var status = NormalizeStatusToken(request.Status);
                var phase = NormalizeStatusToken(request.Phase);
                var details = string.IsNullOrWhiteSpace(request.Details) ? "-" : request.Details.Trim();
                _log.Add(
                    "PLUGIN_RUNTIME_STATUS",
                    Runtime.PluginId,
                    $"result={status} phase={phase} runtimeSessionId={Runtime.RuntimeSessionId} details={details} rule=plugin_runtime_status_contract");
                return Task.FromResult(new RuntimeStatusBridgeResponse(status, phase, Runtime.RuntimeSessionId));
            },
            requiredCapability: TvAirRuntimeCapabilities.BridgeRuntimeInfo);
        _bridgeManager.Register<AssetListBridgeRequest, IReadOnlyList<PluginAssetDescriptor>>(
            "assets.list",
            (request, _) => Task.FromResult(Assets.List(request.Prefix)));
        _bridgeManager.Register<AssetDescribeBridgeRequest, PluginAssetDescriptor?>(
            "assets.describe",
            (request, _) => Task.FromResult(Assets.Describe(request.LogicalPath)));
        _bridgeManager.Register<EmptyBridgeRequest, IReadOnlyList<string>>(
            "events.listTypes",
            (_, _) => Task.FromResult(Events.ListEventTypes()));
        _bridgeManager.Register<EmptyBridgeRequest, IReadOnlyList<TvAirDataSourceDescriptor>>(
            TvAirRuntimeBridgeMethods.DataListSources,
            (_, _) => Task.FromResult(Data.ListSources()),
            requiredCapability: TvAirRuntimeCapabilities.DataSnapshotRead);
        _bridgeManager.Register<TvAirSnapshotOpenRequest, TvAirOperationResult<TvAirSnapshotDescriptor>>(
            TvAirRuntimeBridgeMethods.DataOpenSnapshot,
            (request, _) => Task.FromResult(Data.OpenSnapshot(request)),
            requiredCapability: TvAirRuntimeCapabilities.DataSnapshotRead);
        _bridgeManager.Register<TvAirSnapshotReadRequest, TvAirOperationResult<TvAirSnapshotPage>>(
            TvAirRuntimeBridgeMethods.DataReadSnapshot,
            (request, _) => Task.FromResult(Data.ReadSnapshot(request)),
            requiredCapability: TvAirRuntimeCapabilities.DataSnapshotRead);
        _bridgeManager.Register<TvAirSnapshotCloseRequest, TvAirOperationResult>(
            TvAirRuntimeBridgeMethods.DataCloseSnapshot,
            (request, _) => Task.FromResult(Data.CloseSnapshot(request.SnapshotId)),
            requiredCapability: TvAirRuntimeCapabilities.DataSnapshotRead);
        _bridgeManager.Register<CreatePluginNotificationRequest, TvAirOperationResult<PluginNotificationState>>(
            TvAirRuntimeBridgeMethods.NotificationsCreate,
            (request, _) => Task.FromResult(Notifications.Create(request)),
            requiredCapability: TvAirRuntimeCapabilities.NotificationsWrite);
        _bridgeManager.Register<UpdatePluginNotificationRequest, TvAirOperationResult<PluginNotificationState>>(
            TvAirRuntimeBridgeMethods.NotificationsUpdate,
            (request, _) => Task.FromResult(Notifications.Update(request)),
            requiredCapability: TvAirRuntimeCapabilities.NotificationsWrite);
        _bridgeManager.Register<NotificationListBridgeRequest, IReadOnlyList<PluginNotificationState>>(
            TvAirRuntimeBridgeMethods.NotificationsList,
            (request, _) => Task.FromResult(Notifications.List(request.IncludeClosed)),
            requiredCapability: TvAirRuntimeCapabilities.NotificationsRead);
        _bridgeManager.Register<NotificationCloseBridgeRequest, TvAirOperationResult>(
            TvAirRuntimeBridgeMethods.NotificationsClose,
            (request, _) => Task.FromResult(Notifications.Close(request.NotificationInstanceId, request.ExpectedRevision)),
            requiredCapability: TvAirRuntimeCapabilities.NotificationsWrite);
        _bridgeManager.Register<CreateVideoOverlaySceneRequest, TvAirOperationResult<VideoOverlaySceneState>>(
            TvAirRuntimeBridgeMethods.VideoOverlayCreateScene,
            (request, _) => Task.FromResult(VideoOverlay.Scenes.Create(request)),
            requiredCapability: TvAirRuntimeCapabilities.VideoOverlayWrite);
        _bridgeManager.Register<AddVideoOverlayElementsRequest, TvAirOperationResult>(
            TvAirRuntimeBridgeMethods.VideoOverlayAddElements,
            (request, _) => Task.FromResult(VideoOverlay.Elements.Add(request)),
            requiredCapability: TvAirRuntimeCapabilities.VideoOverlayWrite);
        _bridgeManager.Register<ClearVideoOverlayLayerRequest, TvAirOperationResult>(
            TvAirRuntimeBridgeMethods.VideoOverlayClearLayer,
            (request, _) => Task.FromResult(VideoOverlay.Elements.Clear(request)),
            requiredCapability: TvAirRuntimeCapabilities.VideoOverlayWrite);
        _bridgeManager.Register<CloseVideoOverlaySceneRequest, TvAirOperationResult>(
            TvAirRuntimeBridgeMethods.VideoOverlayCloseScene,
            (request, _) => Task.FromResult(VideoOverlay.Scenes.Close(request)),
            requiredCapability: TvAirRuntimeCapabilities.VideoOverlayWrite);
        _bridgeManager.Register<VideoOverlaySceneListRequest, IReadOnlyList<VideoOverlaySceneState>>(
            TvAirRuntimeBridgeMethods.VideoOverlayListScenes,
            (request, _) => Task.FromResult(VideoOverlay.Scenes.List(request.IncludeClosed)),
            requiredCapability: TvAirRuntimeCapabilities.VideoOverlayRead);
        _bridgeManager.Register<VideoOverlaySceneIdBridgeRequest, TvAirOperationResult<VideoOverlaySceneSnapshot>>(
            TvAirRuntimeBridgeMethods.VideoOverlayGetScene,
            (request, _) => Task.FromResult(VideoOverlay.Scenes.Get(request.SceneInstanceId)),
            requiredCapability: TvAirRuntimeCapabilities.VideoOverlayRead);
        _bridgeManager.Register<StorageGetBridgeRequest, TvAirOperationResult<PluginStorageEntry>>(
            TvAirRuntimeBridgeMethods.StorageGet,
            (request, _) => Task.FromResult(Storage.Get(request.Namespace, request.Key)),
            requiredCapability: TvAirRuntimeCapabilities.StorageRead);
        _bridgeManager.Register<StorageSetBridgeRequest, TvAirOperationResult<PluginStorageEntry>>(
            TvAirRuntimeBridgeMethods.StorageSet,
            (request, _) => Task.FromResult(Storage.Set(request.Namespace, request.Key, request.Value, request.ExpectedRevision)),
            requiredCapability: TvAirRuntimeCapabilities.StorageWrite);
        _bridgeManager.Register<StorageImportJsonFileOnceBridgeRequest, TvAirOperationResult<PluginStorageImportResult>>(
            TvAirRuntimeBridgeMethods.StorageImportJsonFileOnce,
            (request, _) => Task.FromResult(Storage.ImportJsonFileOnce(request.Namespace, request.Key, request.LegacyFileName)),
            requiredCapability: TvAirRuntimeCapabilities.StorageWrite);
        _bridgeManager.Register<StorageDeleteBridgeRequest, TvAirOperationResult>(
            TvAirRuntimeBridgeMethods.StorageDelete,
            (request, _) => Task.FromResult(Storage.Delete(request.Namespace, request.Key, request.ExpectedRevision)),
            requiredCapability: TvAirRuntimeCapabilities.StorageWrite);
        _bridgeManager.Register<StorageListKeysBridgeRequest, IReadOnlyList<string>>(
            TvAirRuntimeBridgeMethods.StorageListKeys,
            (request, _) => Task.FromResult(Storage.ListKeys(request.Namespace)),
            requiredCapability: TvAirRuntimeCapabilities.StorageRead);
        _bridgeManager.Register<CreatePluginWindowRequest, TvAirOperationResult<PluginWindowState>>(
            "windows.create",
            (request, _) => Task.FromResult(Windows.Create(request)));
        _bridgeManager.Register<WindowIdBridgeRequest, TvAirOperationResult>(
            "windows.show",
            (request, _) => Task.FromResult(Windows.Show(request.WindowInstanceId, request.Activate)));
        _bridgeManager.Register<WindowIdBridgeRequest, TvAirOperationResult>(
            "windows.close",
            (request, _) => Task.FromResult(Windows.Close(request.WindowInstanceId)));
        _bridgeManager.Register<CreatePluginSurfaceRequest, TvAirOperationResult<PluginSurfaceState>>(
            "surfaces.create",
            (request, _) => Task.FromResult(Surfaces.Create(request)));
        _bridgeManager.Register<SurfaceIdBridgeRequest, TvAirOperationResult>(
            "surfaces.start",
            (request, _) => Task.FromResult(Surfaces.Start(request.SurfaceInstanceId)));
        _bridgeManager.Register<AttachPluginSurfaceRequest, TvAirOperationResult<PluginSurfaceAttachment>>(
            "hostSurfaces.attach",
            (request, _) => Task.FromResult(HostSurfaces.Attach(request)));
        _bridgeManager.Register<AttachmentIdBridgeRequest, TvAirOperationResult>(
            "hostSurfaces.detach",
            (request, _) => Task.FromResult(HostSurfaces.Detach(request.AttachmentId)));
        _bridgeManager.Register<StartPluginWebRuntimeRequest, TvAirOperationResult<PluginWebRuntimeState>>(
            "webRuntime.start",
            (request, _) => Task.FromResult(WebRuntime.Start(request)));
    }

    private sealed record EmptyBridgeRequest;
    private sealed record RuntimeBridgeResponse(string PluginId, string RuntimeSessionId, string SdkContractVersion);
    private sealed record RuntimeStatusBridgeRequest(string Phase, string Status, string? Details);
    private sealed record RuntimeStatusBridgeResponse(string Status, string Phase, string RuntimeSessionId);
    private sealed record AssetListBridgeRequest(string? Prefix);
    private sealed record AssetDescribeBridgeRequest(string LogicalPath);
    private sealed record NotificationCloseBridgeRequest(string NotificationInstanceId, long? ExpectedRevision = null);
    private sealed record NotificationListBridgeRequest(bool IncludeClosed = false);
    private sealed record StorageGetBridgeRequest(string Namespace, string Key);
    private sealed record StorageSetBridgeRequest(string Namespace, string Key, object? Value, long? ExpectedRevision);
    private sealed record StorageImportJsonFileOnceBridgeRequest(string Namespace, string Key, string LegacyFileName);
    private sealed record StorageDeleteBridgeRequest(string Namespace, string Key, long? ExpectedRevision);
    private sealed record StorageListKeysBridgeRequest(string Namespace);
    private sealed record WindowIdBridgeRequest(string WindowInstanceId, bool Activate = false);
    private sealed record SurfaceIdBridgeRequest(string SurfaceInstanceId);
    private sealed record AttachmentIdBridgeRequest(string AttachmentId);

    private static string NormalizeStatusToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNKNOWN";
        var chars = value.Trim().ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars);
    }

    private sealed class NotificationHost : ITvAirPluginNotificationsApi
    {
        private readonly ITvAirNotificationsApi _hostApi;

        public NotificationHost(ITvAirNotificationsApi hostApi)
        {
            _hostApi = hostApi ?? throw new ArgumentNullException(nameof(hostApi));
        }

        public TvAirOperationResult<PluginNotificationState> Create(CreatePluginNotificationRequest request)
            => _hostApi.Create(request);

        public TvAirOperationResult<PluginNotificationState> Update(UpdatePluginNotificationRequest request)
            => _hostApi.Update(request);

        public TvAirOperationResult Close(string notificationInstanceId, long? expectedRevision = null)
            => _hostApi.Close(notificationInstanceId, expectedRevision);

        public IReadOnlyList<PluginNotificationState> List(bool includeClosed = false)
            => _hostApi.List(includeClosed);
    }

    private sealed record VideoOverlaySceneIdBridgeRequest(string SceneInstanceId);

    private sealed class DataApi : ITvAirDataApi
    {
        private static readonly TvAirDataSourceDescriptor[] Sources =
        [
            new("reservations", "Reservation", "1"),
            new("reservation-history", "Reservation", "1"),
            new("recording-active", "RecordingSession", "1"),
            new("recording-history", "RecordingHistory", "1"),
            new("program-guide", "ProgramGuideEvent", "1"),
            new("channels", "Channel", "1"),
            new("tuners", "Tuner", "1")
        ];

        private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5);
        private const int MaxSnapshots = 8;
        private const int StableCaptureAttempts = 3;
        private readonly ITvAirReservationsApi _reservations;
        private readonly ITvAirRecordingsApi _recordings;
        private readonly ITvAirProgramGuideApi _programGuide;
        private readonly ITvAirChannelsApi _channels;
        private readonly ITvAirTunersApi _tuners;
        private readonly ConcurrentDictionary<string, SnapshotState> _snapshots = new(StringComparer.Ordinal);
        private readonly object _captureGate = new();

        public DataApi(
            ITvAirReservationsApi reservations,
            ITvAirRecordingsApi recordings,
            ITvAirProgramGuideApi programGuide,
            ITvAirChannelsApi channels,
            ITvAirTunersApi tuners)
        {
            _reservations = reservations;
            _recordings = recordings;
            _programGuide = programGuide;
            _channels = channels;
            _tuners = tuners;
        }

        public IReadOnlyList<TvAirDataSourceDescriptor> ListSources() => Sources;

        public TvAirOperationResult<TvAirSnapshotDescriptor> OpenSnapshot(TvAirSnapshotOpenRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            CleanupExpired();
            var selectedSources = ResolveSources(request);
            if (selectedSources.Count == 0)
                return TvAirOperationResult<TvAirSnapshotDescriptor>.Fail(TvAirErrorCode.EntityNotFound, "Data source was not found.");

            StableCapture? capture;
            lock (_captureGate)
            {
                capture = CaptureStable(selectedSources);
            }
            if (capture is null)
                return TvAirOperationResult<TvAirSnapshotDescriptor>.Fail(TvAirErrorCode.RevisionConflict, "Data changed while the snapshot was being captured.");

            var capturedAt = capture.CapturedAtUtc;
            var expiresAt = capturedAt.Add(SnapshotLifetime);
            var compositeRevision = ComputeCompositeRevision(selectedSources, capture.SourceRevisions);
            var descriptorSourceId = selectedSources.Count == 1 ? selectedSources[0].SourceId : "multi";
            var descriptorEntityType = selectedSources.Count == 1 ? selectedSources[0].EntityType : "SnapshotItem";
            var descriptorSchemaVersion = selectedSources.Count == 1 ? selectedSources[0].SchemaVersion : "2";

            if (!string.IsNullOrWhiteSpace(request.KnownRevision) && string.Equals(request.KnownRevision, compositeRevision, StringComparison.Ordinal))
            {
                return TvAirOperationResult<TvAirSnapshotDescriptor>.Ok(new TvAirSnapshotDescriptor(
                    null, descriptorSourceId, descriptorEntityType, descriptorSchemaVersion, compositeRevision,
                    capturedAt, capturedAt, capture.Items.Length, true)
                {
                    SourceIds = selectedSources.Select(x => x.SourceId).ToArray(),
                    SourceRevisions = capture.SourceRevisions,
                    SourceItemCounts = capture.SourceItemCounts
                });
            }

            while (_snapshots.Count >= MaxSnapshots)
            {
                var oldest = _snapshots.Values.OrderBy(x => x.CapturedAtUtc).FirstOrDefault();
                if (oldest is null || !_snapshots.TryRemove(oldest.SnapshotId, out _)) break;
            }

            var id = Guid.NewGuid().ToString("N");
            var state = new SnapshotState(
                id,
                descriptorSourceId,
                descriptorEntityType,
                descriptorSchemaVersion,
                compositeRevision,
                capturedAt,
                expiresAt,
                capture.Items,
                selectedSources.Select(x => x.SourceId).ToArray(),
                capture.SourceRevisions,
                capture.SourceItemCounts);
            _snapshots[id] = state;
            return TvAirOperationResult<TvAirSnapshotDescriptor>.Ok(state.ToDescriptor(false));
        }

        public TvAirOperationResult<TvAirSnapshotPage> ReadSnapshot(TvAirSnapshotReadRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            CleanupExpired();
            if (string.IsNullOrWhiteSpace(request.SnapshotId) || !_snapshots.TryGetValue(request.SnapshotId, out var state))
                return TvAirOperationResult<TvAirSnapshotPage>.Fail(TvAirErrorCode.EntityNotFound, "Snapshot was not found or has expired.");

            var offsetResult = DecodeSnapshotCursor(request.Cursor, state.SnapshotId);
            if (!offsetResult.Succeeded)
                return TvAirOperationResult<TvAirSnapshotPage>.Fail(TvAirErrorCode.InvalidRequest, offsetResult.Error ?? "Invalid snapshot cursor.");

            var limit = Math.Clamp(request.Limit, 1, 1000);
            var offset = Math.Min(offsetResult.Offset, state.Items.Length);
            var items = state.Items.Skip(offset).Take(limit).ToArray();
            var nextOffset = offset + items.Length;
            var hasMore = nextOffset < state.Items.Length;
            return TvAirOperationResult<TvAirSnapshotPage>.Ok(new TvAirSnapshotPage(
                state.SnapshotId,
                items,
                hasMore ? EncodeSnapshotCursor(state.SnapshotId, nextOffset) : null,
                hasMore,
                state.Revision,
                state.SchemaVersion,
                state.CapturedAtUtc,
                state.Items.Length)
            {
                SourceIds = state.SourceIds,
                SourceRevisions = state.SourceRevisions,
                SourceItemCounts = state.SourceItemCounts
            });
        }

        public TvAirOperationResult CloseSnapshot(string snapshotId)
        {
            if (string.IsNullOrWhiteSpace(snapshotId))
                return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Snapshot id is required.");
            return _snapshots.TryRemove(snapshotId, out _)
                ? TvAirOperationResult.Ok()
                : TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Snapshot was not found.");
        }

        private IReadOnlyList<TvAirDataSourceDescriptor> ResolveSources(TvAirSnapshotOpenRequest request)
        {
            var requested = request.SourceIds is { Count: > 0 }
                ? request.SourceIds
                : string.IsNullOrWhiteSpace(request.SourceId) ? Array.Empty<string>() : new[] { request.SourceId };
            var result = new List<TvAirDataSourceDescriptor>();
            foreach (var sourceId in requested.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var source = FindSource(sourceId);
                if (source is null)
                    return Array.Empty<TvAirDataSourceDescriptor>();
                result.Add(source);
            }
            return result;
        }

        private StableCapture? CaptureStable(IReadOnlyList<TvAirDataSourceDescriptor> sources)
        {
            CapturedSources? previous = null;
            for (var attempt = 0; attempt < StableCaptureAttempts; attempt++)
            {
                var current = CaptureSources(sources);
                if (previous is not null && RevisionsEqual(previous.SourceRevisions, current.SourceRevisions))
                    return current.ToStableCapture();
                previous = current;
            }
            return null;
        }

        private CapturedSources CaptureSources(IReadOnlyList<TvAirDataSourceDescriptor> sources)
        {
            var sourceItems = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
            var sourceRevisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                var items = CaptureItems(source.SourceId);
                sourceItems[source.SourceId] = items;
                sourceCounts[source.SourceId] = items.Length;
                sourceRevisions[source.SourceId] = ComputeSourceRevision(source, items);
            }
            return new CapturedSources(DateTimeOffset.UtcNow, sources, sourceItems, sourceRevisions, sourceCounts);
        }

        private static bool RevisionsEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
            => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

        private TvAirDataSourceDescriptor? FindSource(string? sourceId)
            => Sources.FirstOrDefault(x => string.Equals(x.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        private object[] CaptureItems(string sourceId) => sourceId switch
        {
            "reservations" => _reservations.List().Cast<object>().ToArray(),
            "reservation-history" => _reservations.ListHistory().Cast<object>().ToArray(),
            "recording-active" => _recordings.ListActive().Cast<object>().ToArray(),
            "recording-history" => _recordings.ListHistory().Cast<object>().ToArray(),
            "program-guide" => _programGuide.ListEvents().Cast<object>().ToArray(),
            "channels" => _channels.ListServices().Cast<object>().ToArray(),
            "tuners" => _tuners.ListTuners().Cast<object>().ToArray(),
            _ => Array.Empty<object>()
        };

        private static string ComputeSourceRevision(TvAirDataSourceDescriptor source, object[] items)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(source.SourceId));
            hash.AppendData(Encoding.UTF8.GetBytes(source.SchemaVersion));
            foreach (var item in items)
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item, item.GetType()));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string ComputeCompositeRevision(
            IReadOnlyList<TvAirDataSourceDescriptor> sources,
            IReadOnlyDictionary<string, string> sourceRevisions)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var source in sources.OrderBy(x => x.SourceId, StringComparer.OrdinalIgnoreCase))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(source.SourceId));
                hash.AppendData(Encoding.UTF8.GetBytes(sourceRevisions[source.SourceId]));
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private void CleanupExpired()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _snapshots)
                if (pair.Value.ExpiresAtUtc <= now)
                    _snapshots.TryRemove(pair.Key, out _);
        }

        private static string EncodeSnapshotCursor(string snapshotId, int offset)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snapshotId}:{offset}"));

        private static CursorDecodeResult DecodeSnapshotCursor(string? cursor, string expectedSnapshotId)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return new(true, 0, null);
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                var split = raw.LastIndexOf(':');
                if (split <= 0 || !string.Equals(raw[..split], expectedSnapshotId, StringComparison.Ordinal))
                    return new(false, 0, "Cursor does not belong to this snapshot.");
                if (!int.TryParse(raw[(split + 1)..], out var offset) || offset < 0)
                    return new(false, 0, "Cursor offset is invalid.");
                return new(true, offset, null);
            }
            catch (FormatException)
            {
                return new(false, 0, "Cursor encoding is invalid.");
            }
        }

        private sealed record CapturedSources(
            DateTimeOffset CapturedAtUtc,
            IReadOnlyList<TvAirDataSourceDescriptor> Sources,
            IReadOnlyDictionary<string, object[]> ItemsBySource,
            IReadOnlyDictionary<string, string> SourceRevisions,
            IReadOnlyDictionary<string, int> SourceItemCounts)
        {
            public StableCapture ToStableCapture()
            {
                object[] items = Sources.Count == 1
                    ? ItemsBySource[Sources[0].SourceId]
                    : Sources.SelectMany(source => ItemsBySource[source.SourceId].Select(value => (object)new TvAirSnapshotItem(source.SourceId, value))).ToArray();
                return new StableCapture(CapturedAtUtc, items, SourceRevisions, SourceItemCounts);
            }
        }

        private sealed record StableCapture(
            DateTimeOffset CapturedAtUtc,
            object[] Items,
            IReadOnlyDictionary<string, string> SourceRevisions,
            IReadOnlyDictionary<string, int> SourceItemCounts);

        private sealed record SnapshotState(
            string SnapshotId,
            string SourceId,
            string EntityType,
            string SchemaVersion,
            string Revision,
            DateTimeOffset CapturedAtUtc,
            DateTimeOffset ExpiresAtUtc,
            object[] Items,
            IReadOnlyList<string> SourceIds,
            IReadOnlyDictionary<string, string> SourceRevisions,
            IReadOnlyDictionary<string, int> SourceItemCounts)
        {
            public TvAirSnapshotDescriptor ToDescriptor(bool notModified)
                => new(SnapshotId, SourceId, EntityType, SchemaVersion, Revision,
                    CapturedAtUtc, ExpiresAtUtc, Items.Length, notModified)
                {
                    SourceIds = SourceIds,
                    SourceRevisions = SourceRevisions,
                    SourceItemCounts = SourceItemCounts
                };
        }

        private sealed record CursorDecodeResult(bool Succeeded, int Offset, string? Error);
    }

    public void Dispose()
    {
        _videoOverlayHost.Dispose();
        _webRuntimeManager.Dispose();
        _eventChannel.Dispose();
        _bridgeManager.Dispose();
        _assetCatalog.Dispose();
        _hostSurfaceRegistry.Dispose();
        _windowManager.Dispose();
        _surfaceManager.Dispose();
        _runtimeManager.Dispose();
    }

}
