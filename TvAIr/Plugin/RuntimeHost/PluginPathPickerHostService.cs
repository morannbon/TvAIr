using System.Windows.Forms;
using TvAIr.Core;
using TvAIrPlugin.Pickers;

namespace TvAIr.Plugin.RuntimeHost;

/// <summary>
/// Runtime UI共通のHost-owned Windows Path Picker。
/// File PickerとFolder Pickerは別契約とし、Pluginが用途に応じて明示的に選択する。
/// PluginはWinForms型・ブラウザfile input・環境推測を所有しない。
/// </summary>
internal sealed class PluginPathPickerHostService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogRepository _log;
    private readonly PluginToolWindowHostService _toolWindows;

    public PluginPathPickerHostService(LogRepository log, PluginToolWindowHostService toolWindows)
    {
        _log = log;
        _toolWindows = toolWindows;
    }

    public Task<PluginPathPickerResult> PickFileAsync(
        string pluginId,
        PluginFilePickerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            pluginId,
            "File",
            request.OwnerWindowId,
            owner => PickFile(request, owner),
            "ファイルを選択できませんでした。",
            cancellationToken);
    }

    public Task<PluginPathPickerResult> PickFolderAsync(
        string pluginId,
        PluginFolderPickerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            pluginId,
            "Folder",
            request.OwnerWindowId,
            owner => PickFolder(request, owner),
            "フォルダを選択できませんでした。",
            cancellationToken);
    }

    private async Task<PluginPathPickerResult> ExecuteAsync(
        string pluginId,
        string kind,
        string? ownerWindowId,
        Func<IWin32Window, PluginPathPickerResult> execute,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_toolWindows.TryExecuteOwnedDialog(ownerWindowId, execute, out PluginPathPickerResult? ownedResult)
                && ownedResult is not null)
            {
                LogResult(pluginId, kind, ownerWindowId, "toolwindow", ownedResult);
                return ownedResult;
            }

            var tcs = new TaskCompletionSource<PluginPathPickerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                NativeWindow? owner = null;
                try
                {
                    owner = new NativeWindow();
                    owner.CreateHandle(new CreateParams { ExStyle = 0x00000008 }); // WS_EX_TOPMOST
                    tcs.TrySetResult(execute(owner));
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(PluginPathPickerResult.Fail("picker_failed", failureMessage));
                    _log.Add("PLUGIN_PATH_PICKER", pluginId,
                        $"result=FAILED kind={kind} exception={ex.GetType().Name} ownerWindowId={Safe(ownerWindowId)} rule=runtime_path_picker_contract");
                }
                finally
                {
                    try { owner?.DestroyHandle(); } catch { }
                }
            })
            {
                IsBackground = true,
                Name = $"TvAIr Plugin {kind} Picker"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // Once a native modal picker is shown, the Host keeps ownership until that dialog closes.
            // Releasing the gate only because the caller token was cancelled would permit overlapping OS pickers.
            var result = await tcs.Task.ConfigureAwait(false);
            LogResult(pluginId, kind, ownerWindowId, "standalone_host", result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LogResult(string pluginId, string kind, string? ownerWindowId, string owner, PluginPathPickerResult result)
    {
        _log.Add("PLUGIN_PATH_PICKER", pluginId,
            $"result={(result.Accepted ? "ACCEPTED" : result.Cancelled ? "CANCELLED" : "FAILED")} kind={kind} selected={(result.Accepted ? "present" : "none")} ownerWindowId={Safe(ownerWindowId)} owner={owner} environmentGuessing=False browserFileInput=False rule=runtime_path_picker_contract");
    }

    private static PluginPathPickerResult PickFile(PluginFilePickerRequest request, IWin32Window owner)
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "ファイルを選択" : request.Title.Trim(),
            Filter = BuildFilter(request.Filters)
        };
        ApplyInitialPath(dialog, request.InitialPath);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? PluginPathPickerResult.Accept(Path.GetFullPath(dialog.FileName))
            : PluginPathPickerResult.Cancel();
    }

    private static PluginPathPickerResult PickFolder(PluginFolderPickerRequest request, IWin32Window owner)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = string.IsNullOrWhiteSpace(request.Title) ? "フォルダを選択" : request.Title.Trim(),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = ResolveInitialFolder(request.InitialPath)
        };
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? PluginPathPickerResult.Accept(Path.GetFullPath(dialog.SelectedPath))
            : PluginPathPickerResult.Cancel();
    }

    private static void ApplyInitialPath(OpenFileDialog dialog, string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath)) return;
        var candidate = initialPath.Trim();
        try
        {
            if (File.Exists(candidate))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(candidate)) ?? string.Empty;
                dialog.FileName = Path.GetFileName(candidate);
            }
            else if (Directory.Exists(candidate))
            {
                dialog.InitialDirectory = Path.GetFullPath(candidate);
            }
        }
        catch { }
    }

    private static string ResolveInitialFolder(string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath)) return string.Empty;
        var candidate = initialPath.Trim();
        try
        {
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            if (File.Exists(candidate)) return Path.GetDirectoryName(Path.GetFullPath(candidate)) ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string BuildFilter(IReadOnlyList<PluginFileFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
            return "すべてのファイル (*.*)|*.*";

        var parts = new List<string>();
        foreach (var filter in filters)
        {
            var patterns = (filter.Patterns ?? Array.Empty<string>())
                .Select(NormalizePattern)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (patterns.Length == 0) continue;
            var label = (string.IsNullOrWhiteSpace(filter.Label) ? string.Join(", ", patterns) : filter.Label.Trim()).Replace("|", "/");
            parts.Add($"{label} ({string.Join(";", patterns)})");
            parts.Add(string.Join(";", patterns));
        }
        if (parts.Count == 0) return "すべてのファイル (*.*)|*.*";
        parts.Add("すべてのファイル (*.*)");
        parts.Add("*.*");
        return string.Join("|", parts);
    }

    private static string NormalizePattern(string? value)
    {
        var pattern = (value ?? string.Empty).Trim();
        if (pattern.Length == 0 || pattern.Contains('|')) return string.Empty;
        if (pattern.StartsWith("*.", StringComparison.Ordinal) || pattern == "*.*") return pattern;
        if (pattern.StartsWith(".", StringComparison.Ordinal)) return "*" + pattern;
        if (!pattern.Contains('*') && !pattern.Contains('?') && !pattern.Contains('\\') && !pattern.Contains('/'))
            return "*." + pattern.TrimStart('.');
        return pattern.Contains('\\') || pattern.Contains('/') ? string.Empty : pattern;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
}
