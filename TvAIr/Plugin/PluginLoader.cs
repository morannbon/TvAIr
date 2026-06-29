using System.Reflection;
using System.Text.Json;
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// Plugins/ ディレクトリの DLL を起動時に探索し、
/// ITvAIrPlugin 実装を Initialize → OnStart の順で呼び出す。
/// シャットダウン時に OnStop を呼び出す。
/// 例外はログ出力のみ。本体の動作には影響しない。
/// </summary>
internal sealed class PluginLoader : IHostedService
{
    private static int _sdkResolverRegistered;

    private readonly LogRepository _log;
    private readonly UserEventLogService _userEvents;
    private readonly string _dataDirectory;
    private readonly PluginRegistry _registry;
    private readonly PluginAllowListService _allowList;
    private readonly EpgStore _epgStore;
    private readonly ReservationStore _reservationStore;
    private readonly TunerPool _tunerPool;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly ChannelFileLoader _channelLoader;
    private readonly TaskSchedulerService _taskScheduler;
    private readonly EpgScheduler _epgScheduler;
    private readonly LiveCommentStore _liveComments;
    private readonly ExternalTunerLeaseService _externalTuners;
    private readonly List<ITvAIrPlugin> _loaded = new();

    // Plugins/ ディレクトリは実行ファイルの隣に固定
    private static string PluginsDirectory
        => Path.Combine(AppContext.BaseDirectory, "Plugins");

    public PluginLoader(
        LogRepository log,
        UserEventLogService userEvents,
        IOptions<AppSettings> appSettings,
        PluginRegistry registry,
        PluginAllowListService allowList,
        EpgStore epgStore,
        ReservationStore reservationStore,
        TunerPool tunerPool,
        ReservationAllocationRouteService allocationRoute,
        ChannelFileLoader channelLoader,
        TaskSchedulerService taskScheduler,
        EpgScheduler epgScheduler,
        LiveCommentStore liveComments,
        ExternalTunerLeaseService externalTuners)
    {
        EnsurePluginSdkResolver();
        _log = log;
        _userEvents = userEvents;
        _registry = registry;
        _allowList = allowList;
        _epgStore = epgStore;
        _reservationStore = reservationStore;
        _tunerPool = tunerPool;
        _allocationRoute = allocationRoute;
        _channelLoader = channelLoader;
        _taskScheduler = taskScheduler;
        _epgScheduler = epgScheduler;
        _liveComments = liveComments;
        _externalTuners = externalTuners;
        var dataDir = string.IsNullOrWhiteSpace(appSettings.Value.DataDirectory) ? "data" : appSettings.Value.DataDirectory.Trim();
        _dataDirectory = Path.GetFullPath(Path.IsPathRooted(dataDir) ? dataDir : Path.Combine(AppContext.BaseDirectory, dataDir));
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
            return typeof(ITvAIrPlugin).Assembly;
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

        var dllPaths = Directory.EnumerateFiles(PluginsDirectory, "*.dll", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasNewAirrhythm = dllPaths.Any(IsNewAirrhythmDll);
        foreach (var dllPath in dllPaths)
        {
            if (IsPluginSdkContractDll(dllPath))
            {
                _log.Add("Plugin", Path.GetFileName(dllPath), $"[Plugin] SDK ignored: use host TvAIrPlugin.dll stable contract rule={TvAIrVersionContract.PublicContractName}");
                continue;
            }
            if (hasNewAirrhythm && IsLegacyAirrithmDll(dllPath))
            {
                _log.Add("Plugin", Path.GetFileName(dllPath), $"[Plugin] Compatibility alias ignored: AIrhythm.BasicPlugin.dll exists, skip AIrithm.BasicPlugin.dll rule={TvAIrVersionContract.PublicContractName}");
                continue;
            }
            LoadFromFile(dllPath);
        }
    }

    private static bool IsNewAirrhythmDll(string path)
        => string.Equals(Path.GetFileName(path), "AIrhythm.BasicPlugin.dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyAirrithmDll(string path)
        => string.Equals(Path.GetFileName(path), "AIrithm.BasicPlugin.dll", StringComparison.OrdinalIgnoreCase);

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

            var externalManifestContract = LoadExternalManifestContract(dllPath);
            if (IsLegacyPluginContract(externalManifestContract))
            {
                var message = $"旧SDKプラグインのため読み込みませんでした。TvAIrPlugin SDK {TvAIrVersionContract.PluginSdkVersion} 以降で再ビルドしてください。";
                _userEvents.AddPluginLoadFailed(fileName, message);
                _log.Add("Plugin", fileName, $"[Plugin] Rejected: legacy_sdk requiredSdk={TvAIrVersionContract.PluginSdkVersion} rule={TvAIrVersionContract.PublicContractName}");
                return;
            }

            if (IsUnsupportedPluginContract(externalManifestContract))
            {
                var message = $"対応外のPlugin Host契約です。TvAIrPlugin SDK {TvAIrVersionContract.PluginSdkVersion} 系で再ビルドしてください。";
                _userEvents.AddPluginLoadFailed(fileName, message);
                _log.Add("Plugin", fileName, $"[Plugin] Rejected: unsupported_plugin_contract supportedMajor={TvAIrVersionContract.PluginCompatibilityMajor} minimum={TvAIrVersionContract.MinimumSupportedPluginHostContractVersion} rule={TvAIrVersionContract.PublicContractName}");
                return;
            }

            var assembly = Assembly.LoadFrom(dllPath);
            var pluginTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract &&
                            typeof(ITvAIrPlugin).IsAssignableFrom(t));

            foreach (var type in pluginTypes)
            {
                LoadType(type, fileName, dllPath, externalManifestContract);
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaderMessages = string.Join(" | ", ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct().Take(5));
            _userEvents.AddPluginLoadFailed(fileName, "プラグインの読み込みに失敗しました。");
            _log.Add("Plugin", fileName, $"[Plugin] Error: load_failed message={SafePluginLog(ex.Message)} loader={SafePluginLog(loaderMessages)} sdk={typeof(ITvAIrPlugin).Assembly.GetName().Version} rule={TvAIrVersionContract.PublicContractName}");
        }
        catch (Exception ex)
        {
            var inner = UnwrapPluginException(ex);
            _userEvents.AddPluginLoadFailed(fileName, "プラグインの読み込みに失敗しました。");
            _log.Add("Plugin", fileName, $"[Plugin] Error: load_failed type={SafePluginLog(inner.GetType().Name)} message={SafePluginLog(inner.Message)} sdk={typeof(ITvAIrPlugin).Assembly.GetName().Version} rule={TvAIrVersionContract.PublicContractName}");
        }
    }

    private void LoadType(Type type, string fileName, string dllPath, PluginExternalManifestContract? externalManifestContract)
    {
        try
        {
            if (Activator.CreateInstance(type) is not ITvAIrPlugin plugin)
            {
                _userEvents.AddPluginLoadFailed(fileName, $"インスタンス生成失敗 ({type.FullName})");
                _log.Add("Plugin", fileName, $"[Plugin] Error: インスタンス生成失敗 ({type.FullName})");
                return;
            }

            var context = new PluginContext(_log, _epgStore, _reservationStore, _tunerPool, _allocationRoute, _channelLoader, _taskScheduler, _epgScheduler, _liveComments, _externalTuners, plugin.Name, _dataDirectory);

            try
            {
                plugin.Initialize(context);
            }
            catch (Exception ex)
            {
                _userEvents.AddPluginLoadFailed(plugin.Name, ex.Message);
                _log.Add("Plugin", plugin.Name, $"[Plugin] Error: initialize_failed type={SafePluginLog(UnwrapPluginException(ex).GetType().Name)} message={SafePluginLog(UnwrapPluginException(ex).Message)} rule={TvAIrVersionContract.PublicContractName}");
                return; // Initialize 失敗のプラグインは OnStart しない
            }

            ApplyExternalManifestContract(plugin, externalManifestContract);

            _loaded.Add(plugin);
            _registry.Register(plugin, externalManifestContract);
            var kind = plugin is IAnalysisPlugin ? "Analysis" : plugin is IViewerPlugin ? "Viewer" : plugin is IUiPlugin ? "UI" : "Utility";
            var manifest = plugin is IManifestPlugin mp ? $" manifestId={mp.Manifest.Id} permissions={string.Join(",", mp.Manifest.Permissions)}" : string.Empty;
            _log.Add("Plugin", plugin.Name, $"[Plugin] Loaded: {plugin.Name} v{plugin.Version} kind={kind}{manifest}");
        }
        catch (Exception ex)
        {
            _userEvents.AddPluginLoadFailed(fileName, ex.Message);
            _log.Add("Plugin", fileName, $"[Plugin] Error: load_type_failed type={SafePluginLog(UnwrapPluginException(ex).GetType().Name)} message={SafePluginLog(UnwrapPluginException(ex).Message)} rule={TvAIrVersionContract.PublicContractName}");
        }
    }



    private PluginExternalManifestContract? LoadExternalManifestContract(string dllPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(dllPath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(dllPath);
            var candidates = new[]
            {
                Path.Combine(dir, "plugin.json"),
                Path.Combine(dir, $"{baseName}.plugin.json"),
                Path.Combine(dir, $"{baseName}.json")
            };

            var jsonPath = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(jsonPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            var manifest = TryGetObject(root, "manifest", "Manifest");
            var ui = TryGetObject(root, "ui", "Ui", "descriptor", "Descriptor");
            var window = TryGetObject(root, "window", "Window", "toolWindow", "ToolWindow");
            var menu = TryGetObject(root, "menu", "Menu", "defaultMenuAction", "DefaultMenuAction");
            var compatibility = TryGetObject(root, "compatibility", "Compatibility");
            var metadata = TryGetObject(root, "metadata", "Metadata");

            var contract = new PluginExternalManifestContract
            {
                SourcePath = jsonPath,
                Id = ReadStringAny(new[] { "id", "Id", "pluginId", "PluginId" }, root, manifest, metadata),
                Name = ReadStringAny(new[] { "name", "Name", "displayName", "DisplayName" }, root, manifest, metadata),
                Version = ReadStringAny(new[] { "version", "Version" }, root, manifest, metadata),
                Route = ReadStringAny(new[] { "route", "Route", "routeSegment", "RouteSegment" }, root, manifest, ui),
                Entry = ReadStringAny(new[] { "entry", "Entry", "entryPoint", "EntryPoint" }, root, manifest),
                Description = ReadStringAny(new[] { "description", "Description" }, root, manifest, ui, metadata),
                Vendor = ReadStringAny(new[] { "vendor", "Vendor", "author", "Author", "publisher", "Publisher" }, root, manifest, metadata),
                Icon = ReadStringAny(new[] { "icon", "Icon", "iconPath", "IconPath" }, root, manifest, ui, metadata),
                HostContractVersion = ReadStringAny(new[] { "hostContractVersion", "HostContractVersion", "contractVersion", "ContractVersion" }, root, manifest, compatibility),
                SdkVersion = ReadStringAny(new[] { "sdkVersion", "SdkVersion", "pluginSdkVersion", "PluginSdkVersion", "tvairPluginSdkVersion", "TvAIrPluginSdkVersion" }, root, manifest, compatibility, metadata),
                Kind = ReadStringArrayAny(new[] { "kind", "Kind", "kinds", "Kinds" }, root, manifest),
                Capabilities = ReadStringArrayAny(new[] { "capabilities", "Capabilities" }, root, manifest, ui),
                Permissions = ReadStringArrayAny(new[] { "permissions", "Permissions" }, root, manifest),
                Tags = ReadStringArrayAny(new[] { "tags", "Tags" }, root, manifest, metadata),
                ToolWindowWidth = ReadIntAny(new[] { "toolWindowWidth", "ToolWindowWidth", "width", "Width" }, root, manifest, ui, window),
                ToolWindowHeight = ReadIntAny(new[] { "toolWindowHeight", "ToolWindowHeight", "height", "Height" }, root, manifest, ui, window),
                ToolWindowMinWidth = ReadIntAny(new[] { "toolWindowMinWidth", "ToolWindowMinWidth", "minWidth", "MinWidth", "toolWindowMinWidthPx", "ToolWindowMinWidthPx" }, root, manifest, ui, window),
                ToolWindowMinHeight = ReadIntAny(new[] { "toolWindowMinHeight", "ToolWindowMinHeight", "minHeight", "MinHeight", "toolWindowMinHeightPx", "ToolWindowMinHeightPx" }, root, manifest, ui, window),
                ToolWindowTitle = FirstNonEmpty(
                    ReadStringAny(new[] { "toolWindowTitle", "ToolWindowTitle" }, ui, manifest, root),
                    ReadStringAny(new[] { "title", "Title" }, window)),
                DefaultMenuActionKind = FirstNonEmpty(
                    ReadStringAny(new[] { "defaultMenuActionKind", "DefaultMenuActionKind" }, root, manifest, ui),
                    ReadStringAny(new[] { "kind", "Kind" }, menu)),
                DefaultMenuActionLabel = FirstNonEmpty(
                    ReadStringAny(new[] { "defaultMenuActionLabel", "DefaultMenuActionLabel" }, root, manifest, ui),
                    ReadStringAny(new[] { "label", "Label" }, menu)),
                DefaultMenuActionPriority = ReadIntAny(new[] { "defaultMenuActionPriority", "DefaultMenuActionPriority", "priority", "Priority" }, root, manifest, ui, menu),
                ToolWindowShowInTaskbar = ReadBoolAny(new[] { "toolWindowShowInTaskbar", "ToolWindowShowInTaskbar", "showInTaskbar", "ShowInTaskbar" }, root, manifest, ui, window)
            };

            if (string.IsNullOrWhiteSpace(contract.Id) && string.IsNullOrWhiteSpace(contract.Name) && string.IsNullOrWhiteSpace(contract.Version)
                && string.IsNullOrWhiteSpace(contract.Route) && string.IsNullOrWhiteSpace(contract.Entry) && string.IsNullOrWhiteSpace(contract.Description)
                && string.IsNullOrWhiteSpace(contract.Vendor) && string.IsNullOrWhiteSpace(contract.Icon) && string.IsNullOrWhiteSpace(contract.HostContractVersion) && string.IsNullOrWhiteSpace(contract.SdkVersion)
                && contract.Kind.Count == 0 && contract.Capabilities.Count == 0 && contract.Permissions.Count == 0 && contract.Tags.Count == 0
                && contract.ToolWindowWidth <= 0 && contract.ToolWindowHeight <= 0 && contract.ToolWindowMinWidth <= 0 && contract.ToolWindowMinHeight <= 0
                && string.IsNullOrWhiteSpace(contract.ToolWindowTitle)
                && string.IsNullOrWhiteSpace(contract.DefaultMenuActionKind) && string.IsNullOrWhiteSpace(contract.DefaultMenuActionLabel)
                && contract.DefaultMenuActionPriority <= 0 && contract.ToolWindowShowInTaskbar is null)
            {
                return null;
            }

            return contract;
        }
        catch (Exception ex)
        {
            _log.Add("PLUGIN_EXTERNAL_MANIFEST", Path.GetFileName(dllPath), $"result=FAILED message={SafePluginLog(ex.Message)} rule={TvAIrVersionContract.PublicContractName}");
            return null;
        }
    }

    private void ApplyExternalManifestContract(ITvAIrPlugin plugin, PluginExternalManifestContract? external)
    {
        if (external is null) return;

        var manifest = (plugin as IManifestPlugin)?.Manifest;
        var ui = (plugin as IUiPlugin)?.Ui;
        var applied = new List<string>();

        if (manifest is not null)
        {
            if (string.IsNullOrWhiteSpace(manifest.Id) && !string.IsNullOrWhiteSpace(external.Id)) { manifest.Id = external.Id; applied.Add("manifest.id"); }
            if (string.IsNullOrWhiteSpace(manifest.Name) && !string.IsNullOrWhiteSpace(external.Name)) { manifest.Name = external.Name; applied.Add("manifest.name"); }
            if (string.IsNullOrWhiteSpace(manifest.Version) && !string.IsNullOrWhiteSpace(external.Version)) { manifest.Version = external.Version; applied.Add("manifest.version"); }
            if (string.IsNullOrWhiteSpace(manifest.Route) && !string.IsNullOrWhiteSpace(external.Route)) { manifest.Route = external.Route; applied.Add("manifest.route"); }
            if (string.IsNullOrWhiteSpace(manifest.Entry) && !string.IsNullOrWhiteSpace(external.Entry)) { manifest.Entry = external.Entry; applied.Add("manifest.entry"); }
            if (string.IsNullOrWhiteSpace(manifest.Description) && !string.IsNullOrWhiteSpace(external.Description)) { manifest.Description = external.Description; applied.Add("manifest.description"); }
            if (string.IsNullOrWhiteSpace(manifest.Vendor) && !string.IsNullOrWhiteSpace(external.Vendor)) { manifest.Vendor = external.Vendor; applied.Add("manifest.vendor"); }
            if (string.IsNullOrWhiteSpace(manifest.Icon) && !string.IsNullOrWhiteSpace(external.Icon)) { manifest.Icon = external.Icon; applied.Add("manifest.icon"); }
            if (string.IsNullOrWhiteSpace(manifest.ToolWindowTitle) && !string.IsNullOrWhiteSpace(external.ToolWindowTitle)) { manifest.ToolWindowTitle = external.ToolWindowTitle; applied.Add("manifest.toolWindowTitle"); }
            if (string.IsNullOrWhiteSpace(manifest.HostContractVersion) && !string.IsNullOrWhiteSpace(external.HostContractVersion)) { manifest.HostContractVersion = external.HostContractVersion; applied.Add("manifest.hostContractVersion"); }
            if (string.IsNullOrWhiteSpace(manifest.SdkVersion) && !string.IsNullOrWhiteSpace(external.SdkVersion)) { manifest.SdkVersion = external.SdkVersion; applied.Add("manifest.sdkVersion"); }
            if (manifest.Kind.Count == 0 && external.Kind.Count > 0) { manifest.Kind = external.Kind; applied.Add("manifest.kind"); }
            if (manifest.Capabilities.Count == 0 && external.Capabilities.Count > 0) { manifest.Capabilities = external.Capabilities; applied.Add("manifest.capabilities"); }
            if (manifest.Tags.Count == 0 && external.Tags.Count > 0) { manifest.Tags = external.Tags; applied.Add("manifest.tags"); }
            if (manifest.Permissions.Count == 0 && external.Permissions.Count > 0)
            {
                var permissions = ParsePermissions(external.Permissions);
                if (permissions.Count > 0) { manifest.Permissions = permissions; applied.Add("manifest.permissions"); }
            }
            if (manifest.ToolWindowWidth <= 0 && external.ToolWindowWidth > 0) { manifest.ToolWindowWidth = external.ToolWindowWidth; applied.Add("manifest.width"); }
            if (manifest.ToolWindowHeight <= 0 && external.ToolWindowHeight > 0) { manifest.ToolWindowHeight = external.ToolWindowHeight; applied.Add("manifest.height"); }
            if (manifest.ToolWindowMinWidth <= 0 && external.ToolWindowMinWidth > 0) { manifest.ToolWindowMinWidth = external.ToolWindowMinWidth; applied.Add("manifest.minWidth"); }
            if (manifest.ToolWindowMinHeight <= 0 && external.ToolWindowMinHeight > 0) { manifest.ToolWindowMinHeight = external.ToolWindowMinHeight; applied.Add("manifest.minHeight"); }
            if (string.IsNullOrWhiteSpace(manifest.DefaultMenuActionKind) && !string.IsNullOrWhiteSpace(external.DefaultMenuActionKind)) { manifest.DefaultMenuActionKind = external.DefaultMenuActionKind; applied.Add("manifest.defaultActionKind"); }
            if (string.IsNullOrWhiteSpace(manifest.DefaultMenuActionLabel) && !string.IsNullOrWhiteSpace(external.DefaultMenuActionLabel)) { manifest.DefaultMenuActionLabel = external.DefaultMenuActionLabel; applied.Add("manifest.defaultActionLabel"); }
            if (manifest.DefaultMenuActionPriority == 1000 && external.DefaultMenuActionPriority > 0) { manifest.DefaultMenuActionPriority = external.DefaultMenuActionPriority; applied.Add("manifest.defaultActionPriority"); }
            if (!manifest.ToolWindowShowInTaskbar && external.ToolWindowShowInTaskbar == true) { manifest.ToolWindowShowInTaskbar = true; applied.Add("manifest.showInTaskbar"); }
            if (string.IsNullOrWhiteSpace(manifest.ToolWindowTitle) && !string.IsNullOrWhiteSpace(external.ToolWindowTitle)) { manifest.ToolWindowTitle = external.ToolWindowTitle; applied.Add("manifest.toolWindowTitle"); }
        }

        if (ui is not null)
        {
            if (string.IsNullOrWhiteSpace(ui.RouteSegment) && !string.IsNullOrWhiteSpace(external.Route)) { ui.RouteSegment = external.Route; applied.Add("ui.route"); }
            if (string.IsNullOrWhiteSpace(ui.MenuText) && !string.IsNullOrWhiteSpace(external.Name)) { ui.MenuText = external.Name; applied.Add("ui.menuText"); }
            if (string.IsNullOrWhiteSpace(ui.Description) && !string.IsNullOrWhiteSpace(external.Description)) { ui.Description = external.Description; applied.Add("ui.description"); }
            if (string.IsNullOrWhiteSpace(ui.Icon) && !string.IsNullOrWhiteSpace(external.Icon)) { ui.Icon = external.Icon; applied.Add("ui.icon"); }
            if (ui.Capabilities.Count == 0 && external.Capabilities.Count > 0) { ui.Capabilities = external.Capabilities; applied.Add("ui.capabilities"); }
            if (ui.ToolWindowWidth <= 0 && external.ToolWindowWidth > 0) { ui.ToolWindowWidth = external.ToolWindowWidth; applied.Add("ui.width"); }
            if (ui.ToolWindowHeight <= 0 && external.ToolWindowHeight > 0) { ui.ToolWindowHeight = external.ToolWindowHeight; applied.Add("ui.height"); }
            if (ui.ToolWindowMinWidth <= 0 && external.ToolWindowMinWidth > 0) { ui.ToolWindowMinWidth = external.ToolWindowMinWidth; applied.Add("ui.minWidth"); }
            if (ui.ToolWindowMinHeight <= 0 && external.ToolWindowMinHeight > 0) { ui.ToolWindowMinHeight = external.ToolWindowMinHeight; applied.Add("ui.minHeight"); }
            if (string.IsNullOrWhiteSpace(ui.DefaultMenuActionKind) && !string.IsNullOrWhiteSpace(external.DefaultMenuActionKind)) { ui.DefaultMenuActionKind = external.DefaultMenuActionKind; applied.Add("ui.defaultActionKind"); }
            if (string.IsNullOrWhiteSpace(ui.DefaultMenuActionLabel) && !string.IsNullOrWhiteSpace(external.DefaultMenuActionLabel)) { ui.DefaultMenuActionLabel = external.DefaultMenuActionLabel; applied.Add("ui.defaultActionLabel"); }
            if (ui.DefaultMenuActionPriority == 1000 && external.DefaultMenuActionPriority > 0) { ui.DefaultMenuActionPriority = external.DefaultMenuActionPriority; applied.Add("ui.defaultActionPriority"); }
            if (!ui.ToolWindowShowInTaskbar && external.ToolWindowShowInTaskbar == true) { ui.ToolWindowShowInTaskbar = true; applied.Add("ui.showInTaskbar"); }
            if (string.IsNullOrWhiteSpace(ui.ToolWindowTitle) && !string.IsNullOrWhiteSpace(external.ToolWindowTitle)) { ui.ToolWindowTitle = external.ToolWindowTitle; applied.Add("ui.toolWindowTitle"); }
        }

        _log.Add("PLUGIN_EXTERNAL_MANIFEST", plugin.Name,
            $"result=MERGED source={Path.GetFileName(external.SourcePath)} contract=release_contract fields=id|name|version|route|kind|capabilities|permissions|ui|menu|window toolWindowSize={external.ToolWindowWidth}x{external.ToolWindowHeight} toolWindowMinSize={external.ToolWindowMinWidth}x{external.ToolWindowMinHeight} applied={(applied.Count == 0 ? "none" : string.Join(',', applied))} rule={TvAIrVersionContract.PublicContractName}");
    }

    private static JsonElement? TryGetObject(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }
        return null;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static int ReadIntAny(string[] names, params JsonElement?[] elements)
    {
        foreach (var element in elements)
        {
            if (element is null || element.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
            {
                if (!element.Value.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
            }
        }
        return 0;
    }

    private static string ReadStringAny(string[] names, params JsonElement?[] elements)
    {
        foreach (var element in elements)
        {
            if (element is null || element.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
            {
                if (element.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
        }
        return string.Empty;
    }

    private static IReadOnlyList<string> ReadStringArrayAny(string[] names, params JsonElement?[] elements)
    {
        foreach (var element in elements)
        {
            if (element is null || element.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
            {
                if (!element.Value.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Array)
                {
                    return value.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? string.Empty) : x.ToString())
                        .Select(x => x.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString() ?? string.Empty;
                    return text.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
        }
        return Array.Empty<string>();
    }

    private static bool? ReadBoolAny(string[] names, params JsonElement?[] elements)
    {
        foreach (var element in elements)
        {
            if (element is null || element.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
            {
                if (!element.Value.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
            }
        }
        return null;
    }

    private static IReadOnlyList<PluginPermission> ParsePermissions(IReadOnlyList<string> values)
    {
        var parsed = new List<PluginPermission>();
        foreach (var value in values)
        {
            if (Enum.TryParse<PluginPermission>(value, ignoreCase: true, out var permission) && !parsed.Contains(permission))
            {
                parsed.Add(permission);
            }
        }
        return parsed;
    }

    private static int ReadInt(JsonElement root, JsonElement? manifest, JsonElement? ui, params string[] names)
    {
        foreach (var element in new JsonElement?[] { root, manifest, ui })
        {
            if (element is null || element.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
            {
                if (!element.Value.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
            }
        }
        return 0;
    }

    private static string ReadString(JsonElement root, JsonElement? manifest, JsonElement? ui, params string[] names)
    {
        foreach (var element in new JsonElement?[] { root, manifest, ui })
        {
            if (element is null || element.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
            {
                if (element.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
        }
        return string.Empty;
    }

    private static bool? ReadBool(JsonElement root, JsonElement? manifest, JsonElement? ui, params string[] names)
    {
        foreach (var element in new JsonElement?[] { root, manifest, ui })
        {
            if (element is null || element.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
            {
                if (!element.Value.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
            }
        }
        return null;
    }



    private static bool IsLegacyPluginContract(PluginExternalManifestContract? contract)
        => contract is not null && (
            TvAIrVersionContract.IsLegacyZeroMajor(contract.HostContractVersion) ||
            TvAIrVersionContract.IsLegacyZeroMajor(contract.SdkVersion));

    private static bool IsUnsupportedPluginContract(PluginExternalManifestContract? contract)
    {
        if (contract is null) return false;
        return IsUnsupportedPluginVersion(contract.HostContractVersion)
            || IsUnsupportedPluginVersion(contract.SdkVersion);
    }

    private static bool IsUnsupportedPluginVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        return !TvAIrVersionContract.IsLegacyZeroMajor(version)
            && !TvAIrVersionContract.IsSupportedPluginContract(version);
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
        foreach (var plugin in _loaded)
        {
            try
            {
                plugin.OnStart();
            }
            catch (Exception ex)
            {
                _log.Add("Plugin", plugin.Name, $"[Plugin] Error: OnStart 失敗 - {ex.Message}");
            }
        }
    }

    /// <summary>ロード済み全プラグインの OnStop を呼び出す。</summary>
    private void StopAll()
    {
        foreach (var plugin in _loaded)
        {
            try
            {
                plugin.OnStop();
            }
            catch (Exception ex)
            {
                _log.Add("Plugin", plugin.Name, $"[Plugin] Error: OnStop 失敗 - {ex.Message}");
            }
        }
    }
}
