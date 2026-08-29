namespace TvAIr.Plugin;

using System.Collections.Concurrent;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using TvAIr.Core;
using TvAIr.Plugin.RuntimeHost;
using TvAIrPlugin.Windows;

/// <summary>
/// Host-managed ToolWindow placement persistence.
/// The persistence owner is the existing per-plugin Runtime Storage under
/// DataDirectory/PluginStorage/{pluginId}.json. No executable-directory,
/// plugin-binary-directory, or standalone placement-file fallback is allowed.
/// </summary>
public sealed class PluginWindowPlacementStore
{
    private const string StorageNamespace = "__host.windows";
    private const string PolicyKeyPrefix = "placement-policy.";
    private const string PlacementKeyPrefix = "placement.";

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, PersistentPluginStorage> _storageByPlugin =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory;
    private readonly LogRepository _log;

    public PluginWindowPlacementStore(Database database, LogRepository log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dataDirectory = database?.DataDirectory ?? throw new ArgumentNullException(nameof(database));
    }

    public PluginWindowPlacementPersistencePolicy? GetPolicy(string pluginId, string windowDefinitionId)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var normalizedDefinitionId = NormalizeWindowDefinitionId(windowDefinitionId);
        if (normalizedPluginId.Length == 0 || normalizedDefinitionId.Length == 0) return null;

        lock (_gate)
        {
            return ReadValue<PluginWindowPlacementPersistencePolicy>(
                normalizedPluginId,
                PolicyKeyPrefix + normalizedDefinitionId);
        }
    }

    public PluginWindowPlacementResolve GetPlacement(string pluginId, string windowDefinitionId, PluginWindowSizeReference expectedSizeReference)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var normalizedDefinitionId = NormalizeWindowDefinitionId(windowDefinitionId);
        if (normalizedPluginId.Length == 0 || normalizedDefinitionId.Length == 0)
            return new(null, "window_definition_default");

        lock (_gate)
        {
            var key = PlacementKeyPrefix + normalizedDefinitionId;
            var value = ReadValue<PluginWindowSavedPlacement>(normalizedPluginId, key);
            if (value is null) return new(null, "window_definition_default");

            if (value.SizeReference != expectedSizeReference)
            {
                DeleteValue(normalizedPluginId, key);
                _log.Add("PLUGIN_WINDOW_PLACEMENT_STORE", normalizedPluginId,
                    $"result=INVALIDATED windowDefinitionId={Safe(normalizedDefinitionId)} savedSizeReference={value.SizeReference} descriptorSizeReference={expectedSizeReference} action=use_initial_size rule=plugin_window_placement_size_reference_contract");
                return new(null, "saved_placement_size_reference_mismatch", value.SizeReference);
            }

            var visibility = ResolveVisiblePlacement(value);
            if (visibility.Placement is not null)
            {
                var resolved = visibility.Placement;
                _log.Add("PLUGIN_TOOL_WINDOW_PLACEMENT_TRACE", normalizedPluginId,
                    $"phase=resolve windowDefinitionId={Safe(normalizedDefinitionId)} sizeReference={value.SizeReference} savedX={value.Left?.ToString() ?? "-"} savedY={value.Top?.ToString() ?? "-"} savedWidth={value.Width} savedHeight={value.Height} requestedX={value.Left?.ToString() ?? "-"} requestedY={value.Top?.ToString() ?? "-"} requestedWidth={value.Width} requestedHeight={value.Height} adjustedX={resolved.Left?.ToString() ?? "-"} adjustedY={resolved.Top?.ToString() ?? "-"} adjustedWidth={resolved.Width} adjustedHeight={resolved.Height} workingArea={visibility.WorkingArea.Left},{visibility.WorkingArea.Top},{visibility.WorkingArea.Width}x{visibility.WorkingArea.Height} monitor={Safe(visibility.MonitorDeviceName)} adjustReason={visibility.AdjustReason} result={(visibility.Exact ? "EXACT" : "ADJUSTED")} rule=plugin_window_placement_roundtrip_contract");
                return new(resolved, visibility.Exact ? "saved_placement" : "saved_placement_adjusted", value.SizeReference);
            }

            DeleteValue(normalizedPluginId, key);
            return new(null, "saved_placement_invalid");
        }
    }

    public PluginWindowPlacementStoreUpdate SetPolicy(string pluginId, string windowDefinitionId, bool rememberPlacement, bool clearSavedPlacement)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var normalizedDefinitionId = NormalizeWindowDefinitionId(windowDefinitionId);
        if (normalizedPluginId.Length == 0 || normalizedDefinitionId.Length == 0)
            return new(false, false, false, "invalid_window_definition_id");

        lock (_gate)
        {
            var policy = new PluginWindowPlacementPersistencePolicy(
                normalizedPluginId,
                normalizedDefinitionId,
                rememberPlacement,
                DateTimeOffset.Now);
            if (!WriteValue(normalizedPluginId, PolicyKeyPrefix + normalizedDefinitionId, policy, out var failureReason))
                return new(false, false, false, failureReason);

            var cleared = false;
            if (clearSavedPlacement)
                cleared = DeleteValue(normalizedPluginId, PlacementKeyPrefix + normalizedDefinitionId);

            return new(true, rememberPlacement, cleared, string.Empty);
        }
    }

    public bool SavePlacement(string pluginId, string windowDefinitionId, PluginWindowHostState state, PluginWindowSizeReference sizeReference, out string failureReason)
    {
        failureReason = string.Empty;
        if (state.IsMinimized || state.Width <= 0 || state.Height <= 0) return true;

        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var normalizedDefinitionId = NormalizeWindowDefinitionId(windowDefinitionId);
        if (normalizedPluginId.Length == 0 || normalizedDefinitionId.Length == 0)
        {
            failureReason = "invalid_window_definition_id";
            return false;
        }

        lock (_gate)
        {
            var rectangle = new Rectangle(state.Left ?? 0, state.Top ?? 0, state.Width, state.Height);
            var screen = Screen.FromRectangle(rectangle);
            var placement = new PluginWindowSavedPlacement(
                normalizedPluginId,
                normalizedDefinitionId,
                state.Width,
                state.Height,
                state.Left,
                state.Top,
                DateTimeOffset.Now,
                screen.DeviceName,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height,
                sizeReference);
            var saved = WriteValue(normalizedPluginId, PlacementKeyPrefix + normalizedDefinitionId, placement, out failureReason);
            _log.Add("PLUGIN_TOOL_WINDOW_PLACEMENT_TRACE", normalizedPluginId,
                $"phase=save windowDefinitionId={Safe(normalizedDefinitionId)} sizeReference={sizeReference} actualX={state.Left?.ToString() ?? "-"} actualY={state.Top?.ToString() ?? "-"} actualWidth={state.Width} actualHeight={state.Height} persistedX={placement.Left?.ToString() ?? "-"} persistedY={placement.Top?.ToString() ?? "-"} persistedWidth={placement.Width} persistedHeight={placement.Height} workingArea={screen.WorkingArea.Left},{screen.WorkingArea.Top},{screen.WorkingArea.Width}x{screen.WorkingArea.Height} monitor={Safe(screen.DeviceName)} windowState={Safe(state.WindowState)} result={(saved ? "SAVED" : "FAILED")} rule=plugin_window_placement_roundtrip_contract");
            return saved;
        }
    }


    private PersistentPluginStorage GetStorage(string pluginId)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        return _storageByPlugin.GetOrAdd(
            normalizedPluginId,
            id => new PersistentPluginStorage(id, _dataDirectory, _log));
    }

    private T? ReadValue<T>(string pluginId, string key) where T : class
    {
        try
        {
            var result = GetStorage(pluginId).Get(StorageNamespace, key);
            if (!result.Succeeded || result.Value?.Value is null) return null;
            return ConvertValue<T>(result.Value.Value);
        }
        catch (Exception ex)
        {
            _log.Add("PLUGIN_WINDOW_PLACEMENT_STORE", pluginId,
                $"result=LOAD_FAILED key={Safe(key)} reason={Safe(ex.GetType().Name)} action=use_default rule=plugin_storage_window_placement_contract");
            return null;
        }
    }

    private bool WriteValue<T>(string pluginId, string key, T value, out string failureReason)
    {
        try
        {
            var result = GetStorage(pluginId).Set(StorageNamespace, key, value);
            if (result.Succeeded)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = result.Error?.Code.ToString() ?? "placement_store_unavailable";
            _log.Add("PLUGIN_WINDOW_PLACEMENT_STORE", pluginId,
                $"result=SAVE_FAILED key={Safe(key)} failureReason={Safe(failureReason)} storage=plugin_runtime_data rule=plugin_storage_window_placement_contract");
            return false;
        }
        catch (Exception ex)
        {
            failureReason = ex is UnauthorizedAccessException ? "permission_denied" : "placement_store_unavailable";
            _log.Add("PLUGIN_WINDOW_PLACEMENT_STORE", pluginId,
                $"result=SAVE_FAILED key={Safe(key)} reason={Safe(ex.GetType().Name)} storage=plugin_runtime_data rule=plugin_storage_window_placement_contract");
            return false;
        }
    }

    private bool DeleteValue(string pluginId, string key)
    {
        try
        {
            var storage = GetStorage(pluginId);
            if (!storage.Exists(StorageNamespace, key)) return false;
            var result = storage.Delete(StorageNamespace, key);
            if (result.Succeeded) return true;
            _log.Add("PLUGIN_WINDOW_PLACEMENT_STORE", pluginId,
                $"result=DELETE_FAILED key={Safe(key)} failureReason={Safe(result.Error?.Code.ToString())} storage=plugin_runtime_data rule=plugin_storage_window_placement_contract");
            return false;
        }
        catch (Exception ex)
        {
            _log.Add("PLUGIN_WINDOW_PLACEMENT_STORE", pluginId,
                $"result=DELETE_FAILED key={Safe(key)} reason={Safe(ex.GetType().Name)} storage=plugin_runtime_data rule=plugin_storage_window_placement_contract");
            return false;
        }
    }

    private static T? ConvertValue<T>(object value) where T : class
    {
        if (value is T typed) return typed;
        if (value is JsonElement element) return element.Deserialize<T>(JsonOptions());
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions()), JsonOptions());
    }

    private static PluginWindowPlacementVisibilityResult ResolveVisiblePlacement(PluginWindowSavedPlacement placement)
    {
        if (!placement.Left.HasValue || !placement.Top.HasValue || placement.Width <= 0 || placement.Height <= 0)
            return new(null, false, "invalid_saved_bounds", Rectangle.Empty, string.Empty);

        var requested = new Rectangle(placement.Left.Value, placement.Top.Value, placement.Width, placement.Height);
        var screens = Screen.AllScreens;

        // Exact round-trip is the primary contract. A placement that is already fully valid on
        // any current working area must not pass through clamp/rounding/metadata rewrite.
        var exactScreen = screens.FirstOrDefault(screen => screen.WorkingArea.Contains(requested));
        if (exactScreen is not null)
            return new(placement, true, "none", exactScreen.WorkingArea, exactScreen.DeviceName);

        // For a genuinely invalid/off-screen placement, retain the saved outer size as far as
        // possible and perform only the minimum adjustment required to make it visible. Prefer
        // the original monitor when it still exists; otherwise choose the screen with the largest
        // intersection, falling back to WinForms' nearest-screen resolution for fully off-screen data.
        var target = screens.FirstOrDefault(screen =>
                !string.IsNullOrWhiteSpace(placement.MonitorDeviceName)
                && string.Equals(screen.DeviceName, placement.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? screens
                .Select(screen => new { Screen = screen, Intersection = Rectangle.Intersect(screen.WorkingArea, requested) })
                .OrderByDescending(x => (long)x.Intersection.Width * x.Intersection.Height)
                .Select(x => x.Screen)
                .FirstOrDefault(screen => Rectangle.Intersect(screen.WorkingArea, requested).Width > 0
                    && Rectangle.Intersect(screen.WorkingArea, requested).Height > 0)
            ?? Screen.FromRectangle(requested);

        if (target is null)
            return new(null, false, "no_current_monitor", Rectangle.Empty, string.Empty);

        var area = target.WorkingArea;
        var width = Math.Min(requested.Width, Math.Max(1, area.Width));
        var height = Math.Min(requested.Height, Math.Max(1, area.Height));
        var left = Math.Clamp(requested.Left, area.Left, area.Right - width);
        var top = Math.Clamp(requested.Top, area.Top, area.Bottom - height);

        var reasons = new List<string>(4);
        if (requested.Width > area.Width) reasons.Add("oversize_width");
        if (requested.Height > area.Height) reasons.Add("oversize_height");
        if (requested.Left < area.Left) reasons.Add("outside_left");
        if (requested.Top < area.Top) reasons.Add("outside_top");
        if (requested.Right > area.Right) reasons.Add("outside_right");
        if (requested.Bottom > area.Bottom) reasons.Add("outside_bottom");
        if (!string.IsNullOrWhiteSpace(placement.MonitorDeviceName)
            && !screens.Any(screen => string.Equals(screen.DeviceName, placement.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)))
            reasons.Insert(0, "monitor_missing");
        if (reasons.Count == 0) reasons.Add("outside_working_area");

        var resolved = placement with
        {
            Width = width,
            Height = height,
            Left = left,
            Top = top,
            MonitorDeviceName = target.DeviceName,
            WorkingAreaWidth = area.Width,
            WorkingAreaHeight = area.Height
        };
        return new(resolved, false, string.Join('+', reasons.Distinct(StringComparer.Ordinal)), area, target.DeviceName);
    }

    private sealed record PluginWindowPlacementVisibilityResult(
        PluginWindowSavedPlacement? Placement,
        bool Exact,
        string AdjustReason,
        Rectangle WorkingArea,
        string MonitorDeviceName);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string NormalizeWindowDefinitionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "…";
    }
}

public sealed record PluginWindowSavedPlacement(
    string PluginId,
    string WindowDefinitionId,
    int Width,
    int Height,
    int? Left,
    int? Top,
    DateTimeOffset UpdatedAt,
    string MonitorDeviceName = "",
    int WorkingAreaWidth = 0,
    int WorkingAreaHeight = 0,
    PluginWindowSizeReference SizeReference = PluginWindowSizeReference.OuterWindow);

public sealed record PluginWindowPlacementResolve(
    PluginWindowSavedPlacement? Placement,
    string Source,
    PluginWindowSizeReference? SavedSizeReference = null);

public sealed record PluginWindowPlacementStoreUpdate(
    bool Success,
    bool RememberPlacementApplied,
    bool SavedPlacementCleared,
    string FailureReason);
