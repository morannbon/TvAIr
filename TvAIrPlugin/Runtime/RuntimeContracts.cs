using TvAIrPlugin.Assets;
using TvAIrPlugin.Bridge;
using TvAIrPlugin.Data;
using TvAIrPlugin.Events;
using TvAIrPlugin.Notifications;
using TvAIrPlugin.Overlay;
using TvAIrPlugin.Pickers;
using TvAIrPlugin.Storage;
using TvAIrPlugin.Surfaces;
using TvAIrPlugin.Windows;
using TvAIrPlugin.WebRuntime;

namespace TvAIrPlugin.Runtime;

public enum TvAirErrorCode
{
    None,
    InvalidRequest,
    UnsupportedMethod,
    CapabilityUnavailable,
    PermissionDenied,
    RuntimeDisconnected,
    OperationCancelled,
    EntityNotFound,
    RevisionConflict,
    StaleViewerGeneration,
    HostSurfaceUnavailable,
    PayloadTooLarge,
    ViewerFocusPreserveFailed,
    InternalError
}

public sealed record TvAirError(TvAirErrorCode Code, string Message);

public class TvAirOperationResult
{
    public bool Succeeded { get; init; }
    public TvAirError? Error { get; init; }
    public static TvAirOperationResult Ok() => new() { Succeeded = true };
    public static TvAirOperationResult Fail(TvAirErrorCode code, string message) => new() { Error = new(code, message) };
}

public sealed class TvAirOperationResult<T> : TvAirOperationResult
{
    public T? Value { get; init; }
    public static TvAirOperationResult<T> Ok(T value) => new() { Succeeded = true, Value = value };
    public new static TvAirOperationResult<T> Fail(TvAirErrorCode code, string message) => new() { Error = new(code, message) };
    public static TvAirOperationResult<T> Fail(TvAirErrorCode code, string message, T value)
        => new() { Error = new(code, message), Value = value };
}


public static class TvAirRuntimeCapabilities
{
    public const string BridgeRuntimeInfo = "Bridge.RuntimeInfo";
    public const string BridgeRpc = "Bridge.Rpc";
    public const string BridgeEvents = "Bridge.Events";
    public const string DataRead = "Data.Read";
    public const string DataSnapshotRead = "Data.Snapshot.Read";
    public const string StorageRead = "Storage.Read";
    public const string StorageWrite = "Storage.Write";
    public const string NotificationsRead = "Notifications.Read";
    public const string NotificationsWrite = "Notifications.Write";
    public const string VideoOverlayRead = "VideoOverlay.Read";
    public const string VideoOverlayWrite = "VideoOverlay.Write";
    public const string PluginsRead = "Plugins.Read";
}

public static class TvAirRuntimeBridgeMethods
{
    public const string RuntimeGetInfo = "runtime.getInfo";
    public const string RuntimeListMethods = "runtime.listMethods";
    public const string RuntimeReportStatus = "runtime.reportStatus";
    public const string DataListSources = "data.listSources";
    public const string DataOpenSnapshot = "data.openSnapshot";
    public const string DataReadSnapshot = "data.readSnapshot";
    public const string DataCloseSnapshot = "data.closeSnapshot";
    public const string StorageGet = "storage.get";
    public const string StorageImportJsonFileOnce = "storage.importJsonFileOnce";
    public const string StorageSet = "storage.set";
    public const string StorageDelete = "storage.delete";
    public const string StorageListKeys = "storage.listKeys";
    public const string NotificationsCreate = "notifications.create";
    public const string NotificationsUpdate = "notifications.update";
    public const string NotificationsList = "notifications.list";
    public const string NotificationsClose = "notifications.close";
    public const string VideoOverlayCreateScene = "videoOverlay.createScene";
    public const string VideoOverlayAddElements = "videoOverlay.addElements";
    public const string VideoOverlayClearLayer = "videoOverlay.clearLayer";
    public const string VideoOverlayGetScene = "videoOverlay.getScene";
    public const string VideoOverlayListScenes = "videoOverlay.listScenes";
    public const string VideoOverlayCloseScene = "videoOverlay.closeScene";
    public const string PluginsListLoaded = "plugins.listLoaded";
    public const string PluginsListAnalysis = "plugins.listAnalysis";
    public const string PluginsAnalyze = "plugins.analyze";
}


public enum RuntimeUiKind
{
    Page,
    ToolWindow
}

public sealed record RuntimeUiDefinition
{
    public required string UiDefinitionId { get; init; }
    public required string Route { get; init; }
    public RuntimeUiKind Kind { get; init; }
    public string WindowDefinitionId { get; init; } = string.Empty;
    public string SurfaceDefinitionId { get; init; } = string.Empty;
    public IReadOnlyList<string> AssetPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedActions { get; init; } = Array.Empty<string>();
}

public sealed class RuntimeUiRenderContext
{
    public const string RuntimeHoverEventName = "tvair-runtime-hover";
    public const string RuntimeHoverKeyAttribute = "data-tvair-hover-key";

    public required string PluginId { get; init; }
    public required string UiDefinitionId { get; init; }
    public required string Route { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.Now;
    public bool IsClosedNetwork { get; init; } = true;
    public string HostSelectedTheme { get; init; } = "current";
    public string HostEffectiveTheme { get; init; } = "light";
    // Host-owned generic theme roles for Runtime UI. Plugins must consume semantic state pairs
    // (normal/hover, selected/selectedHover, disabled, primary/secondary/danger actions) rather
    // than invent plugin-specific colors. A generic hover must never replace selected, checked,
    // disabled, danger, recording, reservation, or other semantic state styling. Existing keys
    // remain available for backward compatibility; contractVersion identifies additive expansion.
    public IReadOnlyDictionary<string, string> ThemeContract { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string ActionEndpoint { get; init; } = string.Empty;
    public string ActionMethod { get; init; } = "POST";
    public string ActionToken { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ActionContract { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    // RuntimeHover is intentionally presentation-agnostic. Host only normalizes hover enter/leave
    // for explicitly opted-in Plugin DOM elements. Popup, marquee, expansion, highlight, overflow
    // detection, and all other reactions remain Plugin-owned behavior.
    public IReadOnlyDictionary<string, string> HoverContract { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> RequestQuery { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string RequestPathAndQuery { get; init; } = string.Empty;
    public string AssetBaseUrl { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> AssetContract { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string WindowRoute { get; init; } = string.Empty;
    public string WindowEndpoint { get; init; } = string.Empty;
    public string WindowMethod { get; init; } = "POST";
    public string WindowToken { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedWindowActions { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> WindowContract { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> ToolWindowCapabilities { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string CurrentWindowId { get; init; } = string.Empty;
    public bool IsHostManagedWindowContent { get; init; }
    public string CurrentWindowStateEndpoint { get; init; } = string.Empty;
    public string CurrentWindowStateUrl { get; init; } = string.Empty;
    public string CurrentWindowUrl { get; init; } = string.Empty;
    public string CurrentWindowAbsoluteUrl { get; init; } = string.Empty;
    public bool CurrentWindowAlwaysOnTop { get; init; }
    public int CurrentWindowRevision { get; init; }
    public bool CurrentWindowHostAlive { get; init; }

    /// <summary>Runtime UI用の正規Action属性を生成する。任意JavaScriptを必要としない。</summary>
    public string BuildPluginActionAttributes(
        IReadOnlyDictionary<string, string?>? payload = null,
        string eventName = "click",
        string responseMode = "hostHandled",
        string formCapture = "",
        string repeatPolicy = "",
        int burstWindowMs = 0,
        string acceptedLabel = "")
    {
        static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
        static string NormalizeDataKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            var chars = key.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray();
            return new string(chars);
        }

        var endpoint = !string.IsNullOrWhiteSpace(ActionEndpoint)
            ? ActionEndpoint
            : (ActionContract.TryGetValue("endpoint", out var contractEndpoint) ? contractEndpoint : "/api/plugins/action");
        var attrs = new List<string>
        {
            $"data-tvair-event=\"{Encode(string.IsNullOrWhiteSpace(eventName) ? "click" : eventName)}\"",
            "data-tvair-action=\"pluginOwnedAction\"",
            $"data-tvair-endpoint=\"{Encode(endpoint)}\"",
            $"data-tvair-plugin-id=\"{Encode(PluginId)}\"",
            $"data-tvair-route-segment=\"{Encode(Route)}\"",
            $"data-tvair-action-token=\"{Encode(ActionToken)}\"",
            $"data-tvair-response-mode=\"{Encode(string.IsNullOrWhiteSpace(responseMode) ? "hostHandled" : responseMode)}\""
        };

        if (payload is not null)
        {
            foreach (var pair in payload)
            {
                var key = NormalizeDataKey(pair.Key);
                if (string.IsNullOrWhiteSpace(key) || pair.Value is null) continue;
                var lowerKey = key.ToLowerInvariant();
                if (lowerKey.StartsWith("payload-", StringComparison.Ordinal)
                    || lowerKey is "action" or "pluginid" or "routesegment" or "route" or "token" or "actiontoken" or "responsemode" or "windowid")
                    continue;
                if (lowerKey == "refreshtarget")
                    attrs.Add($"data-tvair-refresh-target=\"{Encode(pair.Value)}\"");
                else if (lowerKey == "preservescroll")
                    attrs.Add($"data-tvair-preserve-scroll=\"{Encode(pair.Value)}\"");
                else
                    attrs.Add($"data-tvair-payload-{key}=\"{Encode(pair.Value)}\"");
            }
        }
        if (!string.IsNullOrWhiteSpace(formCapture)) attrs.Add($"data-tvair-form-capture=\"{Encode(formCapture)}\"");
        if (!string.IsNullOrWhiteSpace(repeatPolicy)) attrs.Add($"data-tvair-repeat-policy=\"{Encode(repeatPolicy)}\"");
        if (burstWindowMs > 0) attrs.Add($"data-tvair-burst-window-ms=\"{burstWindowMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"");
        if (!string.IsNullOrWhiteSpace(acceptedLabel)) attrs.Add($"data-tvair-accepted-label=\"{Encode(acceptedLabel)}\"");
        return string.Join(" ", attrs);
    }

    /// <summary>Actionの開始・完了表示をHostへ宣言するRuntime UI契約。</summary>
    public string BuildPluginActionAttributes(
        IReadOnlyDictionary<string, string?>? payload,
        PluginActionFeedbackOptions feedback,
        string eventName = "click",
        string responseMode = "hostHandled",
        string formCapture = "",
        string repeatPolicy = "",
        int burstWindowMs = 0)
    {
        var baseAttributes = BuildPluginActionAttributes(payload, eventName, responseMode, formCapture, repeatPolicy, burstWindowMs);
        static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
        var attrs = new List<string> { baseAttributes, "data-tvair-feedback=\"true\"" };
        if (feedback.DisableWhileRunning) attrs.Add("data-tvair-feedback-disable-running=\"true\"");
        if (feedback.KeepDisabledOnSuccess) attrs.Add("data-tvair-feedback-keep-disabled-success=\"true\"");
        if (feedback.RestoreOnFailure) attrs.Add("data-tvair-feedback-restore-failure=\"true\"");
        if (feedback.KeepUntilRefresh) attrs.Add("data-tvair-feedback-keep-until-refresh=\"true\"");
        if (!string.IsNullOrWhiteSpace(feedback.PendingLabel)) attrs.Add($"data-tvair-feedback-pending-label=\"{Encode(feedback.PendingLabel)}\"");
        if (!string.IsNullOrWhiteSpace(feedback.SuccessLabel)) attrs.Add($"data-tvair-feedback-success-label=\"{Encode(feedback.SuccessLabel)}\"");
        if (!string.IsNullOrWhiteSpace(feedback.NoChangeLabel)) attrs.Add($"data-tvair-feedback-nochange-label=\"{Encode(feedback.NoChangeLabel)}\"");
        if (!string.IsNullOrWhiteSpace(feedback.FailureLabel)) attrs.Add($"data-tvair-feedback-failure-label=\"{Encode(feedback.FailureLabel)}\"");
        if (!string.IsNullOrWhiteSpace(feedback.ConfirmationMessage)) attrs.Add($"data-tvair-confirm-message=\"{Encode(feedback.ConfirmationMessage)}\"");
        return string.Join(" ", attrs.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>
    /// Host共通のRuntime hover enter/leave契約へ要素をopt-inする属性を生成する。
    /// Hostはhover状態だけを通知し、表示方法や見切れ判定はPluginが所有する。
    /// </summary>
    public string BuildRuntimeHoverAttributes(string hoverKey)
    {
        static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
        var key = (hoverKey ?? string.Empty).Trim();
        if (key.Length == 0) throw new ArgumentException("Hover key is required.", nameof(hoverKey));

        return string.Join(" ", new[]
        {
            $"{RuntimeHoverKeyAttribute}=\"{Encode(key)}\"",
            $"data-tvair-plugin-id=\"{Encode(PluginId)}\"",
            $"data-tvair-route-segment=\"{Encode(Route)}\""
        });
    }

    /// <summary>Runtime UIが宣言するHost管理フローティングボタン。</summary>
    public IList<PluginFloatingButton> FloatingButtons { get; } = new List<PluginFloatingButton>();

    /// <summary>descriptor Asset route上の安全な同一オリジンURLを構築する。</summary>
    public string ResolveAssetUrl(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(AssetBaseUrl)) return string.Empty;
        var name = assetName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") return string.Empty;
        return AssetBaseUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(name);
    }
}

public sealed class RuntimeUiActionContext
{
    public required string PluginId { get; init; }
    public required string UiDefinitionId { get; init; }
    public required string Route { get; init; }
    public required string ActionName { get; init; }
    public string CurrentWindowId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Payload { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string CorrelationId { get; init; } = string.Empty;
    public DateTime RequestedAt { get; init; } = DateTime.Now;
}

public sealed class RuntimeUiPatch
{
    public required string ElementId { get; init; }
    public string? TextContent { get; init; }
    public string? ClassName { get; init; }
    public IReadOnlyList<string> AddClasses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemoveClasses { get; init; } = Array.Empty<string>();
    public bool? Disabled { get; init; }
    public bool? Hidden { get; init; }
    public bool? Checked { get; init; }
    public string? Value { get; init; }
    public IReadOnlyDictionary<string, string?> Attributes { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public static RuntimeUiPatch Text(string elementId, string textContent) => new() { ElementId = elementId, TextContent = textContent };
    public static RuntimeUiPatch Visibility(string elementId, bool visible) => new() { ElementId = elementId, Hidden = !visible };
    public static RuntimeUiPatch Enabled(string elementId, bool enabled) => new() { ElementId = elementId, Disabled = !enabled };
    public static RuntimeUiPatch Check(string elementId, bool isChecked) => new() { ElementId = elementId, Checked = isChecked };
    public static RuntimeUiPatch Classes(string elementId, IReadOnlyList<string>? add = null, IReadOnlyList<string>? remove = null) => new()
    {
        ElementId = elementId,
        AddClasses = add ?? Array.Empty<string>(),
        RemoveClasses = remove ?? Array.Empty<string>()
    };
}

public sealed class RuntimeUiActionResult
{
    public bool Succeeded { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string Diagnostics { get; set; } = string.Empty;
    public IReadOnlyList<RuntimeUiPatch> UiPatches { get; set; } = Array.Empty<RuntimeUiPatch>();
    /// <summary>Runtime UI Action後に、RuntimeUiDefinition.Kindで特定される現在の表示surfaceを再描画する正式な要求。PageはWindowInstanceIdを要求せず、ToolWindowは現在のWindowContentを対象とする。</summary>
    public bool RefreshRequested { get; set; }
    /// <summary>再描画対象。contentのみを正式値とする。</summary>
    public string RefreshTarget { get; set; } = "content";
    /// <summary>再描画前のスクロール位置を保持するか。</summary>
    public bool PreserveScroll { get; set; } = true;
    /// <summary>再描画に使用するPlugin content route。空なら現在のrouteを維持。</summary>
    public string ContentRoute { get; set; } = string.Empty;
    /// <summary>Host管理Action feedback。Runtime UI結果の正式な応答契約。</summary>
    public PluginActionFeedback? Feedback { get; set; }
    /// <summary>Host管理の短時間フローティングラベル。</summary>
    public PluginFloatingLabel? FloatingLabel { get; set; }

    public static RuntimeUiActionResult Ok(string message = "") => new() { Succeeded = true, Message = message };
    public static RuntimeUiActionResult Fail(string errorCode, string message, string diagnostics = "")
        => new() { ErrorCode = errorCode, Message = message, Diagnostics = diagnostics };
}

public enum PluginMenuActionKind
{
    None,
    Page,
    ToolWindow,
    Settings,
    VersionDialog,
    StatusDialog
}

public sealed record PluginMenuActionDefinition
{
    public required string ActionId { get; init; }
    public required string Label { get; init; }
    public PluginMenuActionKind Kind { get; init; }
    public int Priority { get; init; }
    public string Route { get; init; } = string.Empty;
    public string WindowDefinitionId { get; init; } = string.Empty;
    public string SurfaceDefinitionId { get; init; } = string.Empty;
    public bool ShowInTaskbar { get; init; }
    public bool ShowInMenu { get; init; } = true;
}

public sealed record PluginLifecycleDefinition
{
    public bool StartAutomatically { get; init; } = true;
    public bool StopOnHostShutdown { get; init; } = true;
}

public sealed record PluginRuntimeDefinition
{
    public required string RuntimeDefinitionId { get; init; }
    public bool StartAutomatically { get; init; }
    public bool KeepAliveWithoutSurface { get; init; }
}

public sealed record TvAirPluginRuntimeDescriptor
{
    public required string PluginId { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string SdkContractVersion { get; init; }
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<global::TvAIrPlugin.PluginPermission> RequiredPermissions { get; init; } = Array.Empty<global::TvAIrPlugin.PluginPermission>();
    public IReadOnlyList<PluginWindowDefinition> Windows { get; init; } = Array.Empty<PluginWindowDefinition>();
    public IReadOnlyList<PluginSurfaceDefinition> Surfaces { get; init; } = Array.Empty<PluginSurfaceDefinition>();
    public IReadOnlyList<PluginRuntimeDefinition> Runtimes { get; init; } = Array.Empty<PluginRuntimeDefinition>();
    public IReadOnlyList<PluginAssetDefinition> Assets { get; init; } = Array.Empty<PluginAssetDefinition>();
    public IReadOnlyList<PluginMenuActionDefinition> MenuActions { get; init; } = Array.Empty<PluginMenuActionDefinition>();
    public IReadOnlyList<RuntimeUiDefinition> UiDefinitions { get; init; } = Array.Empty<RuntimeUiDefinition>();
    public PluginLifecycleDefinition Lifecycle { get; init; } = new();
}

public interface ITvAirRuntimeApi
{
    string PluginId { get; }
    string RuntimeSessionId { get; }
    string SdkContractVersion { get; }
}

public interface ITvAirPluginRuntimeContext
{
    ITvAirRuntimeApi Runtime { get; }
    ITvAirPluginBridgeApi Bridge { get; }
    ITvAirPluginWebRuntimeApi WebRuntime { get; }
    ITvAirPluginWindowsApi Windows { get; }
    ITvAirPluginSurfacesApi Surfaces { get; }
    ITvAirHostSurfacesApi HostSurfaces { get; }
    ITvAirPluginAssetsApi Assets { get; }
    ITvAirPluginEventsApi Events { get; }
    ITvAirVideoOverlayApi VideoOverlay { get; }
    ITvAirDataApi Data { get; }
    ITvAirPluginNotificationsApi Notifications { get; }
    ITvAirPathPickerApi PathPicker { get; }
    global::TvAIrPlugin.Storage.ITvAirPluginStorageApi Storage { get; }

    global::TvAIrPlugin.ITvAirReservationsApi Reservations { get; }
    global::TvAIrPlugin.ITvAirRulesApi Rules { get; }
    global::TvAIrPlugin.ITvAirRecordingsApi Recordings { get; }
    global::TvAIrPlugin.ITvAirPlaybackProgressApi PlaybackProgress { get; }
    global::TvAIrPlugin.ITvAirMediaInsightsApi MediaInsights { get; }
    global::TvAIrPlugin.ITvAirContentDiscoveryApi ContentDiscovery { get; }
    global::TvAIrPlugin.ITvAirProgramGuideApi ProgramGuide { get; }
    global::TvAIrPlugin.ITvAirExternalProgramSourceApi ExternalProgramSource { get; }
    global::TvAIrPlugin.ITvAirChannelsApi Channels { get; }
    global::TvAIrPlugin.ITvAirTunersApi Tuners { get; }
    global::TvAIrPlugin.Viewers.ITvAirViewersApi Viewers { get; }
    global::TvAIrPlugin.Viewers.ITvAirViewerReservationsApi ViewerReservations { get; }
    global::TvAIrPlugin.ITvAirTimedTextStreamsApi TimedTextStreams { get; }
    global::TvAIrPlugin.ITvAirBackupApi Backup { get; }
    global::TvAIrPlugin.ITvAirSettingsApi Settings { get; }
    global::TvAIrPlugin.ITvAirSystemApi System { get; }
    global::TvAIrPlugin.ITvAirLogsApi Logs { get; }
    global::TvAIrPlugin.ITvAirPluginsApi Plugins { get; }
    global::TvAIrPlugin.ITvAirExternalLookupApi ExternalLookup { get; }
}

public interface ITvAirRuntimeCapabilityPlugin
{
    TvAirPluginRuntimeDescriptor Descriptor { get; }
    void Initialize(ITvAirPluginRuntimeContext context);
}

public interface ITvAirRuntimeAnalysisPlugin
{
    AnalysisResult Analyze(AnalysisContext context);
}

public interface ITvAirRuntimeUiPlugin
{
    string RenderHtml(RuntimeUiRenderContext context);
    Task<RuntimeUiActionResult> HandleActionAsync(
        RuntimeUiActionContext context,
        CancellationToken cancellationToken);
}

public interface ITvAirRuntimeLifecyclePlugin
{
    void OnStart();
    void OnStop();
}
