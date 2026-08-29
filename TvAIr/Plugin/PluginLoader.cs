using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Epg.Projection;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIr.Plugin.RuntimeHost;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Assets;
using TvAIrPlugin.Surfaces;
using TvAIrPlugin.Windows;

namespace TvAIr.Plugin;

/// <summary>
/// Plugins/ ディレクトリの DLL を起動時に探索し、Runtime descriptorを正本として登録する。
/// Runtime capability typeだけを生成・登録し、descriptorとRuntime契約を唯一のロード境界とする。
/// Runtime契約を実装しない型はロード対象外とし、例外はログ出力のみで本体動作へ波及させない。
/// </summary>
internal sealed class PluginLoader : IHostedService
{
    private static int _sdkResolverRegistered;

    private readonly LogRepository _log;
    private readonly UserEventLogService _userEvents;
    private readonly PluginRegistry _registry;
    private readonly PluginAllowListService _allowList;
    private readonly ExternalEpgSourceStore _externalEpgSources;
    private readonly LogPresentationStore _logPresentationStore;
    private readonly PluginScopedServiceFactory _pluginScopedServices;
    private readonly PluginReadModelSource _pluginReadModels;
    private readonly PluginReservationOperationService _pluginReservationOperations;
    private readonly PluginReservationPlanningService _pluginReservationPlanning;
    private readonly TvTestSettings _tvTestSettings;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly ProgramProjectionReservationSyncService _projectionReservationSync;
    private readonly PluginSystemReadService _pluginSystemReads;
    private readonly PluginPresentationReadService _pluginPresentationReads;
    private readonly PluginOperationalReadService _pluginOperationalReads;
    private readonly EpgScheduler _epgScheduler;
    private readonly TimedTextStreamStore _timedTextStreams;
    private readonly ExternalTunerLeaseService _externalTuners;
    private readonly ViewerSessionRegistry _viewerSessions;
    private readonly ViewerOperationService _viewerOperations;
    private readonly IniSettingsService _ini;
    private readonly PluginWindowSessionStore _windowSessions;
    private readonly PluginToolWindowHostService _toolWindows;
    private readonly PluginPathPickerHostService _pathPickerHost;
    private readonly PluginTypedEventHub _typedEvents;
    private readonly RecordingResultStore _recordingResults;
    private readonly PlaybackProgressStore _playbackProgress;
    private readonly ReservationScheduler _reservationScheduler;
    private readonly List<(ITvAirRuntimeCapabilityPlugin Plugin, string PluginId)> _loadedRuntimeCapabilities = new();
    private readonly List<PluginRuntimeContext> _runtimeContexts = new();

    // Plugins/ ディレクトリは実行ファイルの隣に固定
    private static string PluginsDirectory
        => Path.Combine(AppContext.BaseDirectory, "Plugins");

    public PluginLoader(
        LogRepository log,
        UserEventLogService userEvents,
        PluginRegistry registry,
        PluginAllowListService allowList,
        ExternalEpgSourceStore externalEpgSources,
        LogPresentationStore logPresentationStore,
        PluginScopedServiceFactory pluginScopedServices,
        PluginReadModelSource pluginReadModels,
        PluginReservationOperationService pluginReservationOperations,
        PluginReservationPlanningService pluginReservationPlanning,
        PluginSystemReadService pluginSystemReads,
        PluginPresentationReadService pluginPresentationReads,
        PluginOperationalReadService pluginOperationalReads,
        IOptions<TvTestSettings> tvTestOptions,
        IReadOnlyList<TunerProfile> tunerProfiles,
        ReservationAllocationRouteService allocationRoute,
        ProgramProjectionReservationSyncService projectionReservationSync,
        EpgScheduler epgScheduler,
        TimedTextStreamStore timedTextStreams,
        ExternalTunerLeaseService externalTuners,
        ViewerSessionRegistry viewerSessions,
        ViewerOperationService viewerOperations,
        IniSettingsService ini,
        PluginWindowSessionStore windowSessions,
        PluginToolWindowHostService toolWindows,
        PluginPathPickerHostService pathPickerHost,
        PluginTypedEventHub typedEvents,
        RecordingResultStore recordingResults,
        PlaybackProgressStore playbackProgress,
        ReservationScheduler reservationScheduler)
    {
        EnsurePluginSdkResolver();
        _log = log;
        _userEvents = userEvents;
        _registry = registry;
        _allowList = allowList;
        _externalEpgSources = externalEpgSources;
        _logPresentationStore = logPresentationStore;
        _pluginScopedServices = pluginScopedServices;
        _pluginReadModels = pluginReadModels;
        _pluginReservationOperations = pluginReservationOperations;
        _pluginReservationPlanning = pluginReservationPlanning;
        _pluginSystemReads = pluginSystemReads;
        _pluginPresentationReads = pluginPresentationReads;
        _pluginOperationalReads = pluginOperationalReads;
        _tvTestSettings = tvTestOptions.Value;
        _tunerProfiles = tunerProfiles;
        _allocationRoute = allocationRoute;
        _projectionReservationSync = projectionReservationSync;
        _epgScheduler = epgScheduler;
        _timedTextStreams = timedTextStreams;
        _externalTuners = externalTuners;
        _viewerSessions = viewerSessions;
        _viewerOperations = viewerOperations;
        _ini = ini;
        _windowSessions = windowSessions;
        _toolWindows = toolWindows;
        _pathPickerHost = pathPickerHost;
        _typedEvents = typedEvents;
        _recordingResults = recordingResults;
        _playbackProgress = playbackProgress;
        _reservationScheduler = reservationScheduler;
    }

    /// <summary>
    /// プラグインSDK DLLは安定契約として本体同梱の TvAIrPlugin.dll を唯一の解決先にする。
    /// プラグイン配下に古い TvAIrPlugin.dll が置かれていても、型同一性を壊さない。
    /// </summary>
    private void EnsurePluginSdkResolver()
    {
        if (Interlocked.Exchange(ref _sdkResolverRegistered, 1) != 0)
        {
            return;
        }

        Assembly ResolveTvAIrPlugin(AssemblyName name)
        {
            return typeof(ITvAirRuntimeCapabilityPlugin).Assembly;
        }

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
            string.Equals(assemblyName.Name, "TvAIrPlugin", StringComparison.OrdinalIgnoreCase)
                ? ResolveTvAIrPlugin(assemblyName)
                : null;

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var requested = new AssemblyName(args.Name);
            return string.Equals(requested.Name, "TvAIrPlugin", StringComparison.OrdinalIgnoreCase)
                ? ResolveTvAIrPlugin(requested)
                : null;
        };
    }

    // ─── IHostedService ──────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadAll();
        StartAll();
        LogRuntimeInventory();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopAll();
        return Task.CompletedTask;
    }

    // ─── 内部処理 ────────────────────────────────────────────────

    /// <summary>Plugins/ 配下の全 DLL を探索してロードする。</summary>
    private void LoadAll()
    {
        // Plugins フォルダがなければ自動生成する（初回起動時にDLLを置く場所を用意）
        Directory.CreateDirectory(PluginsDirectory);

        var dllPaths = Directory.EnumerateFiles(PluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dllPath in dllPaths)
        {
            if (IsPluginSdkContractDll(dllPath))
            {
                _log.Add("Plugin", Path.GetFileName(dllPath), $"[Plugin] SDK ignored: use host TvAIrPlugin.dll stable contract rule={TvAIrVersionContract.PublicContractName}");
                continue;
            }
            LoadFromFile(dllPath);
        }
    }


    private static bool IsPluginSdkContractDll(string path)
        => string.Equals(Path.GetFileName(path), "TvAIrPlugin.dll", StringComparison.OrdinalIgnoreCase);

    private void LoadFromFile(string dllPath)
    {
        var fileName = Path.GetFileName(dllPath);
        try
        {
            var validation = _allowList.Validate(dllPath, PluginsDirectory);
            if (!validation.IsAllowed)
            {
                _userEvents.AddPluginLoadFailed(fileName, validation.Message);
                _log.Add("Plugin", fileName, $"[Plugin] Blocked: {SafePluginLog(validation.Message)} rule={TvAIrVersionContract.PublicContractName}");
                return;
            }

            var assembly = Assembly.LoadFrom(dllPath);
            var exportedTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .ToList();

            var runtimeCapabilityTypes = exportedTypes
                .Where(t => typeof(ITvAirRuntimeCapabilityPlugin).IsAssignableFrom(t));
            foreach (var type in runtimeCapabilityTypes)
            {
                LoadRuntimeCapabilityType(type, fileName);
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaderMessages = string.Join(" | ", ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct().Take(5));
            _userEvents.AddPluginLoadFailed(fileName, "プラグインの読み込みに失敗しました。");
            _log.Add("Plugin", fileName, $"[Plugin] Error: load_failed message={SafePluginLog(ex.Message)} loader={SafePluginLog(loaderMessages)} sdk={typeof(ITvAirRuntimeCapabilityPlugin).Assembly.GetName().Version} rule={TvAIrVersionContract.PublicContractName}");
        }
        catch (Exception ex)
        {
            var inner = UnwrapPluginException(ex);
            _userEvents.AddPluginLoadFailed(fileName, "プラグインの読み込みに失敗しました。");
            _log.Add("Plugin", fileName, $"[Plugin] Error: load_failed type={SafePluginLog(inner.GetType().Name)} message={SafePluginLog(inner.Message)} sdk={typeof(ITvAirRuntimeCapabilityPlugin).Assembly.GetName().Version} rule={TvAIrVersionContract.PublicContractName}");
        }
    }

    private void LoadRuntimeCapabilityType(Type type, string fileName)
    {
        try
        {
            if (Activator.CreateInstance(type) is not ITvAirRuntimeCapabilityPlugin plugin)
            {
                _userEvents.AddPluginLoadFailed(fileName, $"インスタンス生成失敗 ({type.FullName})");
                _log.Add("Plugin", fileName, $"[Plugin] Error: runtime_capability_instance_failed ({type.FullName})");
                return;
            }

            var descriptor = plugin.Descriptor;
            ValidateRuntimeUiDescriptor(descriptor, plugin is ITvAirRuntimeUiPlugin);
            var pluginId = _pluginScopedServices.NormalizePluginId(
                descriptor.PluginId,
                Path.GetFileNameWithoutExtension(fileName));

            var resolvedPermissions = PluginPermissionResolver.Resolve(
                descriptor.RequiredPermissions ?? Array.Empty<PluginPermission>());

            var hostApiContext = new PluginCapabilityContext(
                _log,
                _pluginScopedServices,
                _externalEpgSources,
                _projectionReservationSync,
                _logPresentationStore,
                _pluginReadModels,
                _pluginReservationOperations,
                _pluginReservationPlanning,
                _pluginSystemReads,
                _pluginPresentationReads,
                _pluginOperationalReads,
                _externalTuners,
                _viewerSessions,
                _viewerOperations,
                _timedTextStreams,
                _tvTestSettings,
                _tunerProfiles,
                _epgScheduler,
                _registry,
                _ini,
                _windowSessions,
                _toolWindows,
                _typedEvents,
                _recordingResults,
                _playbackProgress,
                _reservationScheduler,
                pluginId,
                descriptor.DisplayName,
                PluginsDirectory,
                resolvedPermissions);

            var context = new PluginRuntimeContext(hostApiContext, descriptor, type.Assembly, _log, _pluginScopedServices, resolvedPermissions, _pathPickerHost,
                viewerSessionId =>
                {
                    var active = _externalTuners.GetActiveLeases();
                    var session = _viewerSessions.Synchronize(active).FirstOrDefault(x =>
                        string.Equals(x.ViewerSessionId, viewerSessionId, StringComparison.OrdinalIgnoreCase));
                    return session?.ProcessId is > 0
                        ? new VideoOverlayViewerTarget(session.ViewerSessionId, session.Generation, session.ProcessId.Value)
                        : null;
                });
            plugin.Initialize(context);
            _runtimeContexts.Add(context);
            _loadedRuntimeCapabilities.Add((plugin, pluginId));
            _registry.RegisterRuntime(plugin);
            _log.Add("PLUGIN_RUNTIME_DESCRIPTOR_REGISTRATION", pluginId,
                $"result=REGISTERED permissions={descriptor.RequiredPermissions?.Count ?? 0} assets={descriptor.Assets?.Count ?? 0} windows={descriptor.Windows?.Count ?? 0} surfaces={descriptor.Surfaces?.Count ?? 0} menuActions={descriptor.MenuActions?.Count ?? 0} uiDefinitions={descriptor.UiDefinitions?.Count ?? 0} rule=runtime_descriptor_single_source");
            _log.Add("Plugin", pluginId, $"[Plugin] Loaded runtime capability: {descriptor.DisplayName} v{descriptor.Version} contract={descriptor.SdkContractVersion}");
        }
        catch (Exception ex)
        {
            var inner = UnwrapPluginException(ex);
            _userEvents.AddPluginLoadFailed(fileName, inner.Message);
            _log.Add("Plugin", fileName, $"[Plugin] Error: runtime_capability_load_failed type={SafePluginLog(inner.GetType().Name)} message={SafePluginLog(inner.Message)} rule={TvAIrVersionContract.PublicContractName}");
        }
    }


    private static void ValidateRuntimeUiDescriptor(TvAirPluginRuntimeDescriptor descriptor, bool implementsRuntimeUi)
    {
        var uiDefinitions = descriptor.UiDefinitions ?? Array.Empty<RuntimeUiDefinition>();
        if (uiDefinitions.Count == 0)
        {
            if (implementsRuntimeUi)
                throw new InvalidOperationException($"Runtime UI plugin '{descriptor.PluginId}' must declare at least one UiDefinitions entry.");
            return;
        }

        if (!implementsRuntimeUi)
            throw new InvalidOperationException($"Runtime descriptor '{descriptor.PluginId}' declares UiDefinitions but the plugin does not implement ITvAirRuntimeUiPlugin.");

        var duplicateUiId = uiDefinitions
            .GroupBy(ui => ui.UiDefinitionId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateUiId is not null)
            throw new InvalidOperationException($"Runtime descriptor '{descriptor.PluginId}' contains an empty or duplicate UI definition id.");

        var duplicateRoute = uiDefinitions
            .GroupBy(ui => (ui.Route ?? string.Empty).Trim().Trim('/'), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateRoute is not null)
            throw new InvalidOperationException($"Runtime descriptor '{descriptor.PluginId}' contains an empty or duplicate UI route.");

        var windowIds = (descriptor.Windows ?? Array.Empty<PluginWindowDefinition>())
            .Select(window => window.WindowDefinitionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var surfaceIds = (descriptor.Surfaces ?? Array.Empty<PluginSurfaceDefinition>())
            .Select(surface => surface.SurfaceDefinitionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assetIds = (descriptor.Assets ?? Array.Empty<PluginAssetDefinition>())
            .Select(asset => asset.LogicalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var ui in uiDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(ui.WindowDefinitionId) && !windowIds.Contains(ui.WindowDefinitionId))
                throw new InvalidOperationException($"Runtime UI '{ui.UiDefinitionId}' references unknown window definition '{ui.WindowDefinitionId}'.");
            if (!string.IsNullOrWhiteSpace(ui.SurfaceDefinitionId) && !surfaceIds.Contains(ui.SurfaceDefinitionId))
                throw new InvalidOperationException($"Runtime UI '{ui.UiDefinitionId}' references unknown surface definition '{ui.SurfaceDefinitionId}'.");
            foreach (var assetPath in ui.AssetPaths ?? Array.Empty<string>())
            {
                if (!assetIds.Contains(assetPath))
                    throw new InvalidOperationException($"Runtime UI '{ui.UiDefinitionId}' references unknown asset '{assetPath}'.");
            }
        }
    }


    private void LogRuntimeInventory()
    {
        var runtimePlugins = _registry.GetRuntimePlugins();
        var runtimeIds = runtimePlugins
            .Select(x => PluginIdentity.Normalize(x.Descriptor.PluginId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();
        var runtimeUiIds = runtimePlugins
            .Where(x => x is ITvAirRuntimeUiPlugin && x.Descriptor.UiDefinitions.Count > 0)
            .Select(x => PluginIdentity.Normalize(x.Descriptor.PluginId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();
        _log.Add("PLUGIN_RUNTIME_INVENTORY", "Summary",
            $"result=OK runtimeUiRegistrationCount={runtimeUiIds.Length} runtimeCount={runtimePlugins.Count} " +
            $"runtimeUiRegistrations=[{string.Join(',', runtimeUiIds)}] runtime=[{string.Join(',', runtimeIds)}] " +
            "lifecycleOwner=runtime_descriptor registrationOwner=runtime_descriptor uiOwner=runtime_ui_contract " +
            "target=runtime_context_only rule=plugin_runtime_inventory_contract");
    }

    private static Exception UnwrapPluginException(Exception ex)
    {
        while (ex is TargetInvocationException tie && tie.InnerException is not null)
        {
            ex = tie.InnerException;
        }
        return ex;
    }

    private static string SafePluginLog(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>ロード済み全プラグインの OnStart を呼び出す。</summary>
    private void StartAll()
    {
        foreach (var entry in _loadedRuntimeCapabilities)
        {
            if (entry.Plugin is not ITvAirRuntimeLifecyclePlugin lifecycle
                || !entry.Plugin.Descriptor.Lifecycle.StartAutomatically) continue;
            try
            {
                lifecycle.OnStart();
                _log.Add("PLUGIN_RUNTIME_LIFECYCLE", entry.PluginId, "action=start result=OK source=runtime_descriptor rule=runtime_lifecycle_contract");
            }
            catch (Exception ex)
            {
                _log.Add("PLUGIN_RUNTIME_LIFECYCLE", entry.PluginId, $"action=start result=ERROR exceptionType={SafePluginLog(ex.GetType().Name)} message={SafePluginLog(ex.Message)} rule=runtime_lifecycle_contract");
            }
        }
    }

    /// <summary>ロード済み全プラグインの OnStop を呼び出す。</summary>
    private void StopAll()
    {
        for (var i = _loadedRuntimeCapabilities.Count - 1; i >= 0; i--)
        {
            var entry = _loadedRuntimeCapabilities[i];
            if (entry.Plugin is not ITvAirRuntimeLifecyclePlugin lifecycle
                || !entry.Plugin.Descriptor.Lifecycle.StopOnHostShutdown) continue;
            try
            {
                lifecycle.OnStop();
                _log.Add("PLUGIN_RUNTIME_LIFECYCLE", entry.PluginId, "action=stop result=OK source=runtime_descriptor rule=runtime_lifecycle_contract");
            }
            catch (Exception ex)
            {
                _log.Add("PLUGIN_RUNTIME_LIFECYCLE", entry.PluginId, $"action=stop result=ERROR exceptionType={SafePluginLog(ex.GetType().Name)} message={SafePluginLog(ex.Message)} rule=runtime_lifecycle_contract");
            }
        }
        for (var i = _runtimeContexts.Count - 1; i >= 0; i--)
        {
            try { _runtimeContexts[i].Dispose(); }
            catch (Exception ex)
            {
                _log.Add("PLUGIN_RUNTIME_LIFECYCLE", "runtime", $"action=dispose result=ERROR exceptionType={SafePluginLog(ex.GetType().Name)} message={SafePluginLog(ex.Message)} rule=runtime_lifecycle_contract");
            }
        }
        _runtimeContexts.Clear();
    }
}
