/* release_contract gr-cdt-data-module-logo-save-bscs-no-deep: Wakeタスク起動時は --wake-task を単一インスタンス合流シグナルとして扱い、既存TvAIrがいる場合は本体二重起動せず signal ファイルを書いて終了する。 */
/* TvAIrEpgRecの表示ON/OFFを含む起動ポリシーは共通ヘルパーで管理する。 */
/* release_contract wake-plan-hash-trigger-limit: limit Wake task rebuild triggers by in-process plan hash and periodic validation. */
/* release_contract wake-task-nochange-skip: skip full Wake task delete/register when the desired plan is unchanged and existing managed tasks match. */
/* release_contract program-guide-reservation-diff-render: reservation state refresh updates existing program cells only, avoiding full guide rerender when EPG is unchanged. */
using System.Globalization;
using System.Net;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Drawing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Epg.Projection;
using TvAIr.Plugin;
using TvAIr.Plugin.RuntimeHost;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Windows;
using Microsoft.Win32;

// ─── エンコーディング登録（ARIB文字コード用）───────────────────
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// Internal Runtime Probe was used only while rebuilding the generic plugin API.
// Release builds must remove stale probe binaries left by overlay deployment before PluginLoader scans Plugins/.
static void RemoveInternalPluginProbeResidue()
{
    try
    {
        var pluginDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
        if (!Directory.Exists(pluginDir)) return;

        var residuePaths = new[]
        {
            Path.Combine(pluginDir, "PluginRuntimeProbe.dll"),
            Path.Combine(pluginDir, "PluginRuntimeProbe.pdb"),
            Path.Combine(pluginDir, "PluginRuntimeProbe.deps.json"),
            Path.Combine(pluginDir, "PluginRuntimeProbe.runtimeconfig.json")
        };

        foreach (var path in residuePaths)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        var residueDirectory = Path.Combine(pluginDir, "PluginRuntimeProbe");
        if (Directory.Exists(residueDirectory))
            Directory.Delete(residueDirectory, recursive: true);
    }
    catch
    {
        // Cleanup failure must not prevent TvAIr startup. PluginLoader will continue with remaining plugins.
    }
}

RemoveInternalPluginProbeResidue();


static (bool IsWakeTask, string Kind, string At, string Generation, string SlotId, string ReservationId) ParseWakeTaskArgs(string[] argv)
{
    var kind = "";
    var at = "";
    var generation = "";
    var slotId = "";
    var reservationId = "";
    for (var i = 0; i < argv.Length; i++)
    {
        var a = argv[i] ?? "";
        if (a.Equals("--wake-task", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
        {
            kind = argv[++i] ?? "";
            continue;
        }
        if (a.Equals("--wake-at", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
        {
            at = argv[++i] ?? "";
            continue;
        }
        if (a.Equals("--wake-generation", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
        {
            generation = argv[++i] ?? "";
            continue;
        }
        if (a.Equals("--wake-slot-id", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
        {
            slotId = argv[++i] ?? "";
            continue;
        }
        if (a.Equals("--wake-reservation-id", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
        {
            reservationId = argv[++i] ?? "";
            continue;
        }
    }
    return (!string.IsNullOrWhiteSpace(kind), kind.Trim(), at.Trim(), generation.Trim(), slotId.Trim(), reservationId.Trim());
}


static bool IsCurrentWakeInvocation((bool IsWakeTask, string Kind, string At, string Generation, string SlotId, string ReservationId) wake)
{
    if (!wake.IsWakeTask) return true;
    try
    {
        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "runtime");
        var generationFile = Path.Combine(runtimeDir, "wake-active-generation.txt");
        if (!File.Exists(generationFile)) return true;
        var active = File.ReadAllText(generationFile).Trim();
        if (string.IsNullOrWhiteSpace(active)) return true;
        if (string.IsNullOrWhiteSpace(wake.Generation)
            || !string.Equals(active, wake.Generation.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var slotsFile = Path.Combine(runtimeDir, "wake-active-slots.txt");
        if (!File.Exists(slotsFile)) return true;
        var slot = wake.SlotId.Trim();
        if (string.IsNullOrWhiteSpace(slot)) return false;
        return File.ReadLines(slotsFile).Any(line => string.Equals(line.Trim(), slot, StringComparison.OrdinalIgnoreCase));
    }
    catch { return true; }
}

static void WriteWakeTaskSignalForExistingInstance((bool IsWakeTask, string Kind, string At, string Generation, string SlotId, string ReservationId) wake)
{
    if (!wake.IsWakeTask) return;
    try
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtime", "wake-signals");
        Directory.CreateDirectory(dir);
        var safeKind = new string((wake.Kind.Length == 0 ? "UNKNOWN" : wake.Kind).Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
        var safeAt = new string((wake.At.Length == 0 ? DateTime.Now.ToString("yyyyMMddHHmmss") : wake.At).Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
        var file = Path.Combine(dir, $"wake_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Environment.ProcessId}_{safeKind}_{safeAt}.signal");
        var payload = new
        {
            kind = wake.Kind,
            at = wake.At,
            generation = wake.Generation,
            slotId = wake.SlotId,
            reservationId = wake.ReservationId,
            createdUtc = DateTime.UtcNow.ToString("O"),
            sourcePid = Environment.ProcessId,
            action = "signal_existing_instance_and_exit",
            rule = "release_contract"
        };
        File.WriteAllText(file, JsonSerializer.Serialize(payload), Encoding.UTF8);
    }
    catch { }
}


static void WriteStartupSignalForExistingInstance(string reason)
{
    try
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtime", "wake-signals");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"startup_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Environment.ProcessId}.signal");
        var payload = new
        {
            kind = "STARTUP",
            at = DateTime.Now.ToString("O"),
            generation = "startup",
            slotId = "startup",
            reservationId = "",
            sourcePid = Environment.ProcessId,
            reason,
            action = "signal_existing_instance_and_exit",
            requestTrayRecovery = true,
            requestOpenBrowser = true,
            rule = "release_contract"
        };
        File.WriteAllText(file, JsonSerializer.Serialize(payload), Encoding.UTF8);
    }
    catch { }

    try
    {
        // 既存プロセスのトレイが見えない場合でも、二重起動側は最小限の復帰導線としてUIを開く。
        // ポート衝突や既存側未準備時は失敗しても残留しない。
        var portText = SettingsDefaults.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var ini = Path.Combine(AppContext.BaseDirectory, "TvAIr.ini");
            if (File.Exists(ini))
            {
                foreach (var line in File.ReadLines(ini))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Port", StringComparison.OrdinalIgnoreCase) && t.Contains('='))
                    {
                        var v = t[(t.IndexOf('=') + 1)..].Trim();
                        if (int.TryParse(v, out _)) { portText = v; break; }
                    }
                }
            }
        }
        catch { }
        Process.Start(new ProcessStartInfo { FileName = $"http://localhost:{portText}/", UseShellExecute = true });
    }
    catch { }
}

var wakeInvocation = ParseWakeTaskArgs(args);
if (!IsCurrentWakeInvocation(wakeInvocation))
{
    return;
}
// ─── 単一インスタンス保証（release_contract）────────────────────────────
// TvAIr が二重起動すると、予約スケジューラ・チューナー割り当て・Wakeタスク再構築が
// 別プロセスで同時に動き、同じ予約DBへ別ownerから状態遷移を書き込む危険がある。
// Password logon のWakeタスクは対話セッションとは別Windowsセッションで起動し得るため、
// セッションローカル名では単一インスタンス保証にならない。同一PC上の全セッションで1プロセスに固定する。
var singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Global\TvAIr.SingleInstance.v1", createdNew: out var singleInstanceCreated);
if (!singleInstanceCreated)
{
    // release_contract: Wakeだけでなく手動/更新後の同一インスタンス再起動も既存プロセスへ合流させる。
    // 新規プロセスは常駐しない。既存側にはsignalを残し、トレイが見えない場合の復帰導線としてUIを開く。
    if (wakeInvocation.IsWakeTask)
        WriteWakeTaskSignalForExistingInstance(wakeInvocation);
    else
        WriteStartupSignalForExistingInstance("same_instance_startup");
    return;
}

var builder = WebApplication.CreateBuilder(args);
#if TVAIR_DEVELOPER_DIAGNOSTICS
var enableRouteReplayDebugApi = builder.Configuration.GetValue<bool>("Debug:EnableRouteReplayApi");
#endif

// ─── TvAIr.ini 読み込み（appsettings.json より優先） ────────────
// SETTINGS_RUNTIME_HOST_SINGLE_SOURCE_CONTRACT
// 初回起動のHost/DB値も、INI起動時と同じIniSettingsService Runtime正本へ確定する。
// Port/DataDirectoryをProgram内で再読込・再解決する第二経路は作らない。
var firstRunAppSettings = builder.Configuration.GetSection("App").Get<AppSettings>() ?? new();
var iniSettings = new IniSettingsService(
    AppContext.BaseDirectory,
    firstRunAppSettings.DataDirectory,
    firstRunAppSettings.Port);
builder.Services.AddSingleton(iniSettings);
builder.Services.AddSingleton<NetworkAccessSecurity>();

// ─── 設定バインド（ini で上書き） ────────────────────────────────
builder.Services.Configure<AppSettings>(opt =>
{
    opt.Port          = iniSettings.Port;
    opt.DataDirectory = iniSettings.DataDirectory;
});
builder.Services.Configure<TvTestSettings>(opt =>
{
    var base_ = builder.Configuration.GetSection("TvTest").Get<TvTestSettings>() ?? new();
    opt.ExecutablePath    = iniSettings.IsFirstRun ? base_.ExecutablePath    : iniSettings.TvTestExecutablePath;
    opt.BonDriverDirectory= iniSettings.IsFirstRun ? base_.BonDriverDirectory: iniSettings.BonDriverDirectory;
    opt.ViewingTvTestExecutablePath = iniSettings.IsFirstRun ? base_.ViewingTvTestExecutablePath : iniSettings.ViewingTvTestExecutablePath;
    opt.DryRun            = base_.DryRun;
    opt.UseMinOption      = iniSettings.IsFirstRun ? base_.UseMinOption      : iniSettings.UseMinOption;
    opt.UseNodshowOption  = iniSettings.IsFirstRun ? base_.UseNodshowOption  : iniSettings.UseNodshowOption;
});
builder.Services.Configure<ChannelMapSettings>(opt =>
{
    var base_ = builder.Configuration.GetSection("ChannelMap").Get<ChannelMapSettings>() ?? new();
    opt.GrChannelFilePath   = iniSettings.IsFirstRun ? base_.GrChannelFilePath   : iniSettings.GrChannelFilePath;
    opt.GrChSetFilePath     = iniSettings.IsFirstRun ? base_.GrChSetFilePath     : iniSettings.GrChSetFilePath;
    opt.BscsChannelFilePath = iniSettings.IsFirstRun ? base_.BscsChannelFilePath : iniSettings.BscsChannelFilePath;
    opt.BscsChSetFilePath   = iniSettings.IsFirstRun ? base_.BscsChSetFilePath   : iniSettings.BscsChSetFilePath;
});
builder.Services.Configure<EpgSettings>(opt =>
{
    var base_ = builder.Configuration.GetSection("Epg").Get<EpgSettings>() ?? new();
    opt.Enabled                  = iniSettings.IsFirstRun ? base_.Enabled                  : iniSettings.EpgEnabled;
    opt.DailyRefreshHour         = iniSettings.IsFirstRun ? base_.DailyRefreshHour         : iniSettings.EpgHour;
    opt.DailyRefreshMinute       = iniSettings.IsFirstRun ? base_.DailyRefreshMinute       : iniSettings.EpgMinute;
    opt.EpgDepth                 = iniSettings.IsFirstRun ? base_.EpgDepth                 : iniSettings.EpgDepth;
    opt.EpgPreRecordMinutes      = iniSettings.IsFirstRun ? base_.EpgPreRecordMinutes      : iniSettings.EpgPreRecordMinutes;
    // release_contract: DiagnosticMode は ini 管理外のため、常に appsettings.json の値をそのまま使う。
    opt.DiagnosticMode           = base_.DiagnosticMode;
});

// TunerProfile リスト: iniが存在すればini個別設定を使用、初回起動時はappsettings.jsonから読む
List<TunerProfile> tunerProfiles;
if (!iniSettings.IsFirstRun && iniSettings.Tuners.Count > 0)
{
    // ini個別設定 → TunerProfile 1本1エントリ
    tunerProfiles = iniSettings.Tuners.Select(t =>
    {
        var group = TunerDisplayName.NormalizeGroup(t.Group);
        var role = IniSettingsService.NormalizeTunerRole(t.Role);
        return new TunerProfile
        {
            Name              = TunerDisplayName.ForUi(t.Name, group, t.Did),
            BonDriverFileName = TunerIsolationPolicy.NormalizeBonDriverForRole(t.BonDriverFileName, group, role),
            Group             = group,
            Did               = (t.Did ?? string.Empty).Trim().ToUpperInvariant(),
            Role              = role,
            DeviceNumber      = t.DeviceNumber,
            LogicalViewerSlotId = t.LogicalViewerSlotId,
        };
    })
    .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
    .ToList();
}
else
{
    // 初回起動またはiniにチューナー設定なし。
    // release_contract: 配布時の物理BonDriver/DID既定値を実行前提にしない。
    // appsettings.json に明示された行があっても、BonDriver未設定行は論理リソース未解決として除外する。
    tunerProfiles = (builder.Configuration.GetSection("Tuners").Get<List<TunerProfile>>() ?? new())
        .Select(t =>
        {
            var group = TunerDisplayName.NormalizeGroup(t.Group);
            var did = (t.Did ?? string.Empty).Trim().ToUpperInvariant();
            var role = IniSettingsService.NormalizeTunerRole(t.Role);
            return new TunerProfile
            {
                Name = TunerDisplayName.ForUi(t.Name, group, did),
                BonDriverFileName = TunerIsolationPolicy.NormalizeBonDriverForRole(t.BonDriverFileName, group, role),
                Group = group,
                Did = did,
                Role = role,
                DeviceNumber = t.DeviceNumber,
                LogicalViewerSlotId = t.LogicalViewerSlotId,
            };
        })
        .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
        .ToList();
}
// VIEWER_DEVICE_NUMBER_SETTINGS_SOURCE_CONTRACT
// DeviceNumberはRuntime Viewer側で再採番しない。初回appsettings fallbackに明示値が無い場合だけ、
// 設定画面と同じ各放送波内の行順を一度適用する。INI運用では保存済みDeviceNumberが正本となる。
var startupDeviceCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
foreach (var tuner in tunerProfiles)
{
    var group = TunerDisplayName.NormalizeGroup(tuner.Group);
    startupDeviceCounters.TryGetValue(group, out var ordinal);
    ordinal++;
    startupDeviceCounters[group] = ordinal;
    if (tuner.DeviceNumber <= 0) tuner.DeviceNumber = ordinal;
}
// release_contract: 視聴/録画/EPGの隔離はBonDriver名ではなくRoleと論理リソース解決で行う。
// BonDriver未設定行は環境固有fallbackせず、実行候補から外す。
builder.Services.AddSingleton<IReadOnlyList<TunerProfile>>(tunerProfiles.AsReadOnly());

// ─── コアサービス ────────────────────────────────────────────────
builder.Services.AddSingleton<LogRepository>();
builder.Services.AddSingleton<SettingsRuntimeState>();
builder.Services.AddSingleton<ApplicationOperationGate>();
builder.Services.AddSingleton<SettingsChangeApplicationService>();
builder.Services.AddSingleton<UserEventLogService>();
builder.Services.AddSingleton<Database>(_ =>
{
    var dataDir = iniSettings.ResolveDataDirectory();
    return new Database(dataDir);
});

// ─── チャンネル ──────────────────────────────────────────────────
builder.Services.AddSingleton<ChannelFileLoader>();
builder.Services.AddSingleton<TvTestActivityKeeper>();

// ─── チューナー ──────────────────────────────────────────────────
builder.Services.AddSingleton<TvTestLauncher>();

// TunerPool: TunerProfile リストと IniSettingsService から構築
builder.Services.AddSingleton<TunerPool>(sp =>
{
    var profiles = sp.GetRequiredService<IReadOnlyList<TunerProfile>>();
    var ini      = sp.GetRequiredService<IniSettingsService>();
    var logRepo  = sp.GetRequiredService<LogRepository>();
    return new TunerPool(profiles, ini, logRepo);
});
builder.Services.AddSingleton<ExternalTunerLeaseService>();
builder.Services.AddSingleton<ViewerSessionRegistry>();
builder.Services.AddSingleton<ViewerOwnershipService>();
builder.Services.AddSingleton<ViewerOperationService>();

// ─── 予約 ────────────────────────────────────────────────────────
builder.Services.AddSingleton<ChainDirectRecorderSessionRegistry>();
builder.Services.AddSingleton<ReservationMutationJournal>();
builder.Services.AddSingleton<ReservationMutationSideEffectProjection>();
builder.Services.AddSingleton<PluginTypedEventHub>();
builder.Services.AddSingleton<RecordingResultStore>();
builder.Services.AddSingleton<PlaybackProgressStore>();
builder.Services.AddSingleton<NormalEpgWaveOccupation>();
builder.Services.AddSingleton<ReservationStore>();
builder.Services.AddSingleton<ReservationProjectionMetadataStore>();
builder.Services.AddSingleton<ReservationProjectionPromotionService>();
builder.Services.AddSingleton<ProgramProjectionReservationSyncService>();
builder.Services.AddSingleton<ReservationAllocationRouteService>();
builder.Services.AddSingleton<SystemEpgResponsibilityPlanService>();
builder.Services.AddSingleton<ReservationPresentationService>();

// ─── EPG ────────────────────────────────────────────────────────
builder.Services.AddSingleton<EpgStore>();
builder.Services.AddSingleton<DbProgramEventSource>();
builder.Services.AddSingleton<ExternalEpgSourceStore>();
builder.Services.AddSingleton<IProgramEventSource, ProgramGuideProjectionService>();
builder.Services.AddSingleton<ServiceLogoStore>();
builder.Services.AddSingleton<EpgLogoExtractor>();
builder.Services.AddSingleton<SystemSleepInhibitionService>();
// EpgCapture: IOptionsMonitor<EpgSettings>を渡し、DiagnosticMode等を再起動後も確実に反映させる（release_contract）
builder.Services.AddSingleton<EpgCapture>(sp =>
    new EpgCapture(
        sp.GetRequiredService<IOptionsMonitor<EpgSettings>>(),
        sp.GetRequiredService<IReadOnlyList<TunerProfile>>(),
        sp.GetRequiredService<ChannelFileLoader>(),
        sp.GetRequiredService<TvTestLauncher>(),
        sp.GetRequiredService<EpgStore>(),
        sp.GetRequiredService<LogRepository>(),
        sp.GetRequiredService<TunerPool>(),
        sp.GetRequiredService<ReservationStore>(),
        sp.GetRequiredService<IniSettingsService>(),
        sp.GetRequiredService<Database>(),
        sp.GetRequiredService<TvTestActivityKeeper>(),
        sp.GetRequiredService<ServiceLogoStore>(),
        sp.GetRequiredService<EpgLogoExtractor>(),
        sp.GetRequiredService<ReservationProjectionPromotionService>(),
        sp.GetRequiredService<KeywordMatcher>()));
builder.Services.AddSingleton<KeywordMatcher>();
// EpgScheduler: AddSingleton で登録しつつ AddHostedService でバックグラウンド実行
builder.Services.AddSingleton<EpgScheduler>(sp =>
    new EpgScheduler(
        sp.GetRequiredService<IOptions<EpgSettings>>().Value,
        sp.GetRequiredService<IniSettingsService>(),
        sp.GetRequiredService<EpgCapture>(),
        sp.GetRequiredService<ReservationStore>(),
        sp.GetRequiredService<IReadOnlyList<TunerProfile>>(),
        sp.GetRequiredService<ChannelFileLoader>(),
        sp.GetRequiredService<IProgramEventSource>(),
        sp.GetRequiredService<TunerPool>(),
        sp.GetRequiredService<LogRepository>(),
        sp.GetRequiredService<UserEventLogService>(),
        sp.GetRequiredService<PluginTypedEventHub>(),
        sp.GetRequiredService<ReservationAllocationRouteService>(),
        sp.GetRequiredService<Database>(),
        sp.GetRequiredService<ApplicationOperationGate>(),
        sp.GetRequiredService<SystemSleepInhibitionService>(),
        sp.GetRequiredService<SystemEpgResponsibilityPlanService>(),
        sp.GetRequiredService<NormalEpgWaveOccupation>(),
        sp.GetRequiredService<PowerResumeSignalHub>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<EpgScheduler>());

// Wakeタスク合流シグナル監視: 既存TvAIrがいる状態で --wake-task 起動された子プロセスが残す signal を拾い、常駐TvAIr側へ処理を合流させる。
builder.Services.AddHostedService<WakeSignalMonitorService>();

// ─── タスクスケジューラーサービス ──────────────────────────────
builder.Services.AddSingleton<TaskSchedulerService>(sp =>
    new TaskSchedulerService(
        sp.GetRequiredService<ReservationStore>(),
        sp.GetRequiredService<IniSettingsService>(),
        sp.GetRequiredService<IReadOnlyList<TunerProfile>>(),
        sp.GetRequiredService<LogRepository>(),
        sp.GetRequiredService<UserEventLogService>(),
        sp.GetRequiredService<EpgScheduler>()));

// ─── スタートアップ（レジストリRunキー） ─────────────────────────
builder.Services.AddSingleton<StartupRegistryService>(sp =>
    new StartupRegistryService(
        sp.GetRequiredService<LogRepository>()));

// ─── 予約スケジューラー ─────────────────────────────────────────
// AddSingleton で登録しつつ AddHostedService でバックグラウンド実行
// （停止APIから直接参照できるようにSingletonで持つ）
builder.Services.AddSingleton<ReservationScheduler>(sp =>
    new ReservationScheduler(
        sp.GetRequiredService<ReservationStore>(),
        sp.GetRequiredService<TunerPool>(),
        sp.GetRequiredService<IniSettingsService>(),
        sp.GetRequiredService<IReadOnlyList<TunerProfile>>(),
        sp.GetRequiredService<LogRepository>(),
        sp.GetRequiredService<TaskSchedulerService>(),
        sp.GetRequiredService<ReservationAllocationRouteService>(),
        sp.GetRequiredService<ChannelFileLoader>(),
        sp.GetRequiredService<IProgramEventSource>(),
        sp.GetRequiredService<EpgCapture>(),
        sp.GetRequiredService<TvTestActivityKeeper>(),
        sp.GetRequiredService<ChainDirectRecorderSessionRegistry>(),
        sp.GetRequiredService<ServiceLogoStore>(),
        sp.GetRequiredService<UserEventLogService>(),
        sp.GetRequiredService<PluginTypedEventHub>(),
        sp.GetRequiredService<RecordingResultStore>(),
        sp.GetRequiredService<NormalEpgWaveOccupation>(),
        sp.GetRequiredService<ExternalTunerLeaseService>(),
        sp.GetRequiredService<ApplicationOperationGate>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReservationScheduler>());
// 録画due監視と同じ正本時刻でPower Requestを先取りし、EPG等の先行owner解放との隙間を作らない。
builder.Services.AddHostedService<RecordingPowerResponsibilityGuardService>();
builder.Services.AddSingleton<TunerOwnershipReconciliationCoordinator>();
builder.Services.AddSingleton<PowerResumeSignalHub>();
builder.Services.AddHostedService<PowerResumeTunerReconciliationService>();

// ─── プラグイン ──────────────────────────────────────────────────
// Plugins/ ディレクトリの DLL を起動時に自動ロード。
// プラグイン未配置時は何もしない。例外でも本体を停止させない。
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginActionTokenStore>();
builder.Services.AddSingleton<PluginWindowPlacementStore>();
builder.Services.AddSingleton<PluginWindowSessionStore>();
builder.Services.AddSingleton<PluginToolWindowHostService>();
builder.Services.AddSingleton<PluginPathPickerHostService>();
builder.Services.AddSingleton<PluginDefaultMenuActionService>();
builder.Services.AddSingleton<TimedTextStreamStore>();
builder.Services.AddSingleton<PluginAllowListService>();
builder.Services.AddSingleton<PluginBoundaryGate>();
builder.Services.AddSingleton<LogPresentationStore>();
builder.Services.AddSingleton<PluginReadModelSource>();
builder.Services.AddSingleton<PluginReservationOperationService>();
builder.Services.AddSingleton<PluginReservationPlanningService>();
builder.Services.AddSingleton<PluginSystemReadService>();
builder.Services.AddSingleton<PluginPresentationReadService>();
builder.Services.AddSingleton<PluginOperationalReadService>();
builder.Services.AddSingleton<PluginScopedServiceFactory>();
builder.Services.AddHostedService<PluginLoader>();

// ─── JSON ────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    // enumを文字列として送受信する（"scheduled"/"manual"等をそのまま扱える）
    opts.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// ─── ポート設定 ──────────────────────────────────────────────────
var port = iniSettings.Port;
// NETWORK_ACCESS_IMMEDIATE_APPLY_BINDING_CONTRACT
// LAN有効/無効を再起動なしで反映するため、待受自体は起動時から全インターフェースへ固定する。
// 非loopback要求の許可は下段middlewareがIniSettingsServiceの現在値を要求ごとに判定する。
// LAN無効時も外部要求は共通境界で403となり、設定変更でlistenerを再構築しない。
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

bool IsNetworkLanAccessActive() =>
    iniSettings.NetworkLanAccessEnabled &&
    !string.IsNullOrWhiteSpace(iniSettings.NetworkPasswordEncrypted);

static bool IsAllowedLoopbackHost(HostString host, int expectedPort)
{
    if (!host.HasValue || host.Port != expectedPort)
        return false;

    var hostName = host.Host.TrimEnd('.');
    if (hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        return true;

    return IPAddress.TryParse(hostName, out var address)
           && NetworkAccessSecurity.IsLoopback(address);
}

static bool IsAllowedLoopbackOrigin(HttpRequest request, int expectedPort)
{
    var origin = request.Headers.Origin.ToString();
    if (string.IsNullOrWhiteSpace(origin))
        return true;

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
        || !originUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
        || originUri.Port != expectedPort)
        return false;

    var hostName = originUri.Host.TrimEnd('.');
    if (hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        return true;

    return IPAddress.TryParse(hostName, out var address)
           && NetworkAccessSecurity.IsLoopback(address);
}

var app = builder.Build();
_ = app.Services.GetRequiredService<ReservationMutationSideEffectProjection>();
UserLogProjectionContractTests.Run();

if (wakeInvocation.IsWakeTask)
{
    try
    {
        app.Services.GetRequiredService<LogRepository>().Add("WAKE_TASK_INVOCATION", "PRIMARY",
            $"result=PRIMARY_INSTANCE kind={wakeInvocation.Kind} at={wakeInvocation.At} pid={Environment.ProcessId} action=continue_startup_sync rule=release_contract");
    }
    catch { }
}


// TvAIr release_contract cache guard:
// UI差分更新を維持しつつ、ブラウザが更新前のindex.html/JS状態を保持して
// チェーン候補判定だけ遅れて復帰する問題を避ける。
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";

    var requestPathBeforeRouting = context.Request.Path.Value ?? string.Empty;
    var traceRuntimeUiActionRequest = requestPathBeforeRouting.Equals("/api/plugins/action", StringComparison.OrdinalIgnoreCase)
        || requestPathBeforeRouting.Equals("/plugin-action", StringComparison.OrdinalIgnoreCase);
    if (traceRuntimeUiActionRequest)
    {
        try
        {
            context.RequestServices.GetRequiredService<LogRepository>().Add("PLUGIN_ACTION_HTTP_REQUEST", "RECEIVED",
                $"method={SafePluginActionValue(context.Request.Method)} path={SafePluginActionValue(requestPathBeforeRouting)} contentType={SafePluginActionValue(context.Request.ContentType)} contentLength={context.Request.ContentLength?.ToString() ?? "-"} hasFormContentType={context.Request.HasFormContentType} physicalEndpoint=/api/plugins/action logicalRoute=/plugin-action rule=plugin_action_http_route_contract");
        }
        catch { }
    }

    await next();

    if (traceRuntimeUiActionRequest)
    {
        try
        {
            context.RequestServices.GetRequiredService<LogRepository>().Add("PLUGIN_ACTION_HTTP_RESPONSE", "COMPLETED",
                $"method={SafePluginActionValue(context.Request.Method)} path={SafePluginActionValue(requestPathBeforeRouting)} status={context.Response.StatusCode} endpointMatched={(context.Response.StatusCode != StatusCodes.Status405MethodNotAllowed)} physicalEndpoint=/api/plugins/action logicalRoute=/plugin-action rule=plugin_action_http_route_contract");
        }
        catch { }
    }

    var path = context.Request.Path.Value ?? string.Empty;
    if (path.Equals("/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/plugin/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
        context.Response.Headers["X-TvAIr-Ui-Version"] = GetTvAIrAppVersion();
    }
});




// NETWORK_ACCESS_AUTHENTICATION_INVARIANT
// loopbackは無認証のローカルUI契約を維持するが、Hostをlocalhost/loopback IP + 現在portへ固定する。
// 状態変更要求にOriginが付く場合も同じloopback originだけを許可し、DNS rebinding/外部ページからの
// localhost操作を共通入口で拒否する。LAN要求はprivate address、保存済みパスワード、
// 短寿命HttpOnlyセッション、同一オリジン検証を共通境界で必須とする。
app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (NetworkAccessSecurity.IsLoopback(remoteAddress))
    {
        if (!IsAllowedLoopbackHost(context.Request.Host, port))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "ローカル要求の宛先を確認できませんでした。" });
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && !IsAllowedLoopbackOrigin(context.Request, port))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "ローカル操作元を確認できませんでした。" });
            return;
        }

        await next();
        return;
    }

    var path = context.Request.Path.Value ?? string.Empty;
    var anonymousNetworkPath = path.Equals("/network-login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/network-auth/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/network-auth/status", StringComparison.OrdinalIgnoreCase);

    if (!IsNetworkLanAccessActive() || !NetworkAccessSecurity.IsLocalNetworkPeer(remoteAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { message = "この接続元からは利用できません。" });
        return;
    }

    if (anonymousNetworkPath)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Pragma"] = "no-cache";
        await next();
        return;
    }

    var security = context.RequestServices.GetRequiredService<NetworkAccessSecurity>();
    context.Request.Cookies.TryGetValue(NetworkAccessSecurity.SessionCookieName, out var sessionToken);
    if (!security.ValidateSession(sessionToken, remoteAddress, iniSettings.NetworkPasswordEncrypted, out _))
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Headers.Accept.Any(x => x?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
                || path == "/"))
        {
            var requestedPath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            var returnUrl = Uri.EscapeDataString(requestedPath);
            context.Response.Redirect($"/network-login?returnUrl={returnUrl}");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = "接続用パスワードでログインしてください。" });
        return;
    }

    if (!HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            || originUri.Port != (context.Request.Host.Port ?? port))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "操作元を確認できませんでした。" });
            return;
        }
    }

    await next();
});

app.MapGet("/api/network-auth/status", (HttpContext context, NetworkAccessSecurity security) =>
{
    var local = NetworkAccessSecurity.IsLoopback(context.Connection.RemoteIpAddress);
    context.Request.Cookies.TryGetValue(NetworkAccessSecurity.SessionCookieName, out var token);
    var expiresAt = default(DateTimeOffset);
    var authenticated = local
        || security.ValidateSession(token, context.Connection.RemoteIpAddress, iniSettings.NetworkPasswordEncrypted, out expiresAt);
    return Results.Ok(new
    {
        local,
        lanAccessEnabled = IsNetworkLanAccessActive(),
        passwordConfigured = !string.IsNullOrWhiteSpace(iniSettings.NetworkPasswordEncrypted),
        authenticated,
        expiresAt = !local && authenticated ? expiresAt : (DateTimeOffset?)null
    });
});

app.MapPost("/api/network-auth/login", async (HttpContext context, NetworkAccessSecurity security) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (NetworkAccessSecurity.IsLoopback(remoteAddress))
        return Results.Ok(new { success = true, local = true });
    if (!IsNetworkLanAccessActive() || !NetworkAccessSecurity.IsLocalNetworkPeer(remoteAddress))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (security.IsLoginBlocked(remoteAddress, out var retryAfter))
        return Results.Json(new { message = "しばらくしてからもう一度お試しください。", retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) }, statusCode: StatusCodes.Status429TooManyRequests);

    NetworkLoginRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<NetworkLoginRequest>();
    }
    catch (JsonException)
    {
        request = null;
    }

    var sessionGeneration = security.CaptureSessionGeneration();
    var credentialSecret = iniSettings.NetworkPasswordEncrypted;
    var configuredPassword = CredentialProtector.Decrypt(credentialSecret);
    if (request is null || configuredPassword is null || !FixedTimePasswordEquals(request.Password ?? string.Empty, configuredPassword))
    {
        if (!security.TryRecordLoginFailure(remoteAddress, sessionGeneration))
            return Results.Json(new { message = "接続設定が変更されました。もう一度ログインしてください。" }, statusCode: StatusCodes.Status409Conflict);
        return Results.Json(new { message = "パスワードが違います。" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!security.TryClearLoginFailures(remoteAddress, sessionGeneration))
        return Results.Json(new { message = "接続設定が変更されました。もう一度ログインしてください。" }, statusCode: StatusCodes.Status409Conflict);

    var lifetime = TimeSpan.FromMinutes(SettingsDefaults.NormalizeNetworkSessionLifetimeMinutes(iniSettings.NetworkSessionLifetimeMinutes));
    if (!security.TryCreateSession(lifetime, remoteAddress, credentialSecret, sessionGeneration, out var session))
        return Results.Json(new { message = "接続設定が変更されました。もう一度ログインしてください。" }, statusCode: StatusCodes.Status409Conflict);

    context.Response.Cookies.Append(NetworkAccessSecurity.SessionCookieName, session.Token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = false,
        IsEssential = true,
        Path = "/",
        Expires = session.ExpiresAt
    });
    return Results.Ok(new { success = true, expiresAt = session.ExpiresAt });
});

app.MapPost("/api/network-auth/logout", (HttpContext context, NetworkAccessSecurity security) =>
{
    context.Request.Cookies.TryGetValue(NetworkAccessSecurity.SessionCookieName, out var token);
    security.RevokeSession(token);
    context.Response.Cookies.Delete(NetworkAccessSecurity.SessionCookieName, new CookieOptions { Path = "/" });
    return Results.Ok(new { success = true });
});

app.MapGet("/network-login", (HttpContext context) =>
{
    var returnUrl = context.Request.Query["returnUrl"].ToString();
    if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
        returnUrl = "/";
    var returnUrlJson = JsonSerializer.Serialize(returnUrl);
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'self'; base-uri 'none'";
    var html = $$$"""
<!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>TvAIr 接続</title><link rel="stylesheet" href="/tvair-generated-surfaces.css?v=1.2.0-css-final199"></head><body class="tvair-generated-login"><main><h1>TvAIrへ接続</h1><form id="login"><label for="password">接続用パスワード</label><input id="password" type="password" autocomplete="current-password" required minlength="12"><button type="submit">接続</button><div id="message" class="message" role="status"></div></form></main>
<script>
const form=document.getElementById('login'),password=document.getElementById('password'),message=document.getElementById('message');
form.addEventListener('submit',async e=>{e.preventDefault();message.textContent='確認しています…';try{const r=await fetch('/api/network-auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:password.value}),credentials:'same-origin'});const j=await r.json().catch(()=>({}));if(!r.ok){message.textContent=j.message||'接続できませんでした。';return;}location.replace({{{returnUrlJson}}});}catch{message.textContent='接続できませんでした。';}});
</script></body></html>
""";
    return Results.Content(html, "text/html; charset=utf-8");
});

// ─── 起動/終了・TVTest干渉監査 ────────────────────────────────
var lifecycleLog = app.Services.GetRequiredService<LogRepository>();
var userOperationLog = app.Services.GetRequiredService<UserEventLogService>();
// release_contract: ユーザー運用ログの起動履歴は、手動/更新後/PC起動後の実起動だけに限定する。
// Wakeタスク・録画前EPG・録画開始・Recovery 由来の --wake-task 起動は、既存/起動確認シグナルであり
// ユーザーが日常確認する「TvAIrを起動しました」にはしない。詳細監査は /api/log にだけ残す。
if (!wakeInvocation.IsWakeTask)
{
    userOperationLog.AddAppStarted(GetTvAIrAppVersion());
}
else
{
    lifecycleLog.Add("USER_OPERATION_APP_START_SUPPRESSED", "WAKE_TASK",
        $"result=SUPPRESSED kind={wakeInvocation.Kind} at={wakeInvocation.At} reservationId={wakeInvocation.ReservationId} reason=wake_task_startup_signal_not_user_visible rule=release_contract");
}
var effectiveTvTestSettings = app.Services.GetRequiredService<IOptions<TvTestSettings>>().Value;
TvTestRecordingDirectoryResolver.Initialize(effectiveTvTestSettings.ExecutablePath, lifecycleLog);
TvTestRecordFileNameTemplateResolver.Initialize(effectiveTvTestSettings.ExecutablePath, lifecycleLog);
lifecycleLog.Add("APP_LIFECYCLE", "START",
     $"TvAIr start version={GetTvAIrAppVersion()} baseDir={AppContext.BaseDirectory}");
EmitTvAIrRuntimeIdentityAudit(lifecycleLog);
EmitTvAIrEpgRecRuntimePrerequisiteAudit(lifecycleLog, effectiveTvTestSettings);
TvTestProcessAuditor.Capture(lifecycleLog, "APP_START", emitLegacyEvents: true);
// 管理外TVTestは監視・保護・割当判断の対象外。起動時監査はTvAIr管理プロセスだけを扱う。
RunTvAIrEpgRecStartupOrphanSafety(lifecycleLog);
try
{
    app.Services.GetRequiredService<ReservationProjectionPromotionService>()
        .PromotePending("Startup", runAllocationRoute: true);
}
catch (Exception ex)
{
    lifecycleLog.Add("RESERVATION_PROJECTION_PROMOTE", "Startup",
        $"result=ERROR source=Startup error={ex.Message.Replace("\r", " ").Replace("\n", " ")} rule=release_contract");
}
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { app.Services.GetRequiredService<ApplicationOperationGate>().BeginQuiescing("application_stopping"); } catch { }
    try { app.Services.GetRequiredService<TunerPool>().BeginQuiescing("application_stopping"); } catch { }
    lifecycleLog.Add("APP_LIFECYCLE", "STOPPING", "TvAIr stopping begin");
    TvTestProcessAuditor.EmitSnapshot(lifecycleLog, "APP_STOPPING");
});
app.Lifetime.ApplicationStopped.Register(() =>
{
    try { app.Services.GetRequiredService<ApplicationOperationGate>().MarkStopped("application_stopped"); } catch { }
    lifecycleLog.Add("APP_LIFECYCLE", "STOPPED", "TvAIr stopped");
});

// ─── 静的ファイル ────────────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // HTMLはキャッシュさせない（JS変更が即反映されるように）
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
    }
});

// release_contract: Plugins配下を汎用静的ファイルとして丸ごと公開しない。
// プラグインassetは下の明示endpointだけを通し、拡張子・パス境界を一元確認する。
var pluginRootForAssets = Path.Combine(AppContext.BaseDirectory, "Plugins");
Directory.CreateDirectory(pluginRootForAssets);

// release_contract: プラグイン同梱小型画像等の正式asset URL契約。
// Plugins/{route}/Assets または Plugins/{route}/wwwroot/assets 配下のPNGのみを、同一オリジンURLで返す。
// UIの小型アイコン用途に限定し、file://・外部URL・data URI依存を避ける。
app.MapGet("/plugin-assets/{routeSegment}/{assetName}", (string routeSegment, string assetName, HttpRequest http, PluginRegistry registry, PluginBoundaryGate boundaryGate, LogRepository log) =>
    ResolvePluginAssetResult(routeSegment, assetName, registry, boundaryGate, log, http.Path.Value ?? string.Empty, "route"));
app.MapGet("/api/plugins/{pluginId}/assets/{assetName}", (string pluginId, string assetName, HttpRequest http, PluginRegistry registry, PluginBoundaryGate boundaryGate, LogRepository log) =>
    ResolvePluginAssetResult(pluginId, assetName, registry, boundaryGate, log, http.Path.Value ?? string.Empty, "pluginId"));

// ─── プラグインUI/API ──────────────────────────────────────────
// プラグインが本体非依存で利用できるUIルート・Manifest・権限宣言・Context APIの正式入口。
app.MapGet("/api/plugins", (PluginRegistry registry) =>
{
    var plugins = registry.GetRuntimePlugins()
        .Select(p => new
        {
            pluginId = p.Descriptor.PluginId,
            name = p.Descriptor.DisplayName,
            version = p.Descriptor.Version,
            sdkContractVersion = p.Descriptor.SdkContractVersion,
            permissions = p.Descriptor.RequiredPermissions,
            capabilities = p.Descriptor.RequiredCapabilities,
            assets = p.Descriptor.Assets,
            windows = p.Descriptor.Windows,
            surfaces = p.Descriptor.Surfaces,
            menuActions = p.Descriptor.MenuActions,
            uiDefinitions = p.Descriptor.UiDefinitions
        })
        .ToList();
    return Results.Ok(new { plugins, source = "runtime.descriptor" });
});

app.MapGet("/api/plugins/ui", (PluginRegistry registry) =>
{
    var plugins = registry.GetRuntimePlugins()
        .SelectMany(plugin => plugin.Descriptor.UiDefinitions.Select(ui => new
        {
            pluginId = plugin.Descriptor.PluginId,
            pluginName = plugin.Descriptor.DisplayName,
            name = plugin.Descriptor.DisplayName,
            version = plugin.Descriptor.Version,
            enabled = true,
            route = NormalizePluginRouteSegment(ui.Route),
            kind = ui.Kind.ToString(),
            uiDefinitionId = ui.UiDefinitionId,
            windowDefinitionId = ui.WindowDefinitionId,
            surfaceDefinitionId = ui.SurfaceDefinitionId,
            url = $"/plugin/{NormalizePluginRouteSegment(ui.Route)}"
        }))
        .OrderBy(p => p.pluginName)
        .ThenBy(p => p.route)
        .ToList();
    return Results.Ok(new { plugins, source = "runtime.descriptor.uiDefinitions" });
});

app.MapGet("/api/plugins/manifests", (PluginRegistry registry) =>
{
    var descriptors = registry.GetRuntimePlugins()
        .Select(p => p.Descriptor)
        .ToList();
    return Results.Ok(new { descriptors, source = "runtime.descriptor", legacyManifestSupported = false });
});

app.MapGet("/api/plugins/menu-actions", (PluginDefaultMenuActionService menuActions) =>
{
    var actions = menuActions.ResolveActions("api");
    return Results.Ok(new { actions, contract = PluginDefaultMenuActionService.ContractVersion, projection = "menu_model_hamburger_context_page", legacyMenuFallbackSupported = false });
});

app.MapGet("/plugin-menu/{routeSegment}", (string routeSegment, string? source, HttpRequest http, PluginRegistry registry, PluginDefaultMenuActionService menuActions, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, PluginBoundaryGate boundaryGate, LogRepository log) =>
    DispatchPluginDefaultMenuAction(routeSegment, string.IsNullOrWhiteSpace(source) ? "hamburger" : source!, http, registry, menuActions, windows, toolWindows, boundaryGate, log));
app.MapGet("/plugin-menu-info/{routeSegment}", (string routeSegment, PluginRegistry registry, PluginDefaultMenuActionService menuActions, LogRepository log) =>
    RenderPluginDefaultMenuInfoByRoute(routeSegment, registry, menuActions, log));


app.MapGet("/api/timed-text-streams", ReadTimedTextStreams);
app.MapGet("/api/timed-text-streams/groups", ReadTimedTextStreamGroups);


static IResult ReadTimedTextStreams(string? streamId, string? groupId, int? count, TimedTextStreamStore store)
{
    store.PruneOlderThan(TimeSpan.FromHours(6));
    var items = store.GetRecent(streamId, groupId, Math.Clamp(count.GetValueOrDefault(100), 1, 300));
    return Results.Ok(new { ok = true, streamId, groupId, count = items.Count, items });
}

static IResult ReadTimedTextStreamGroups(string? streamId, int? count, TimedTextStreamStore store)
{
    store.PruneOlderThan(TimeSpan.FromHours(6));
    return Results.Ok(new { ok = true, groups = store.GetGroups(streamId, Math.Clamp(count.GetValueOrDefault(50), 1, 300)) });
}



static PluginWindowDefinition? ResolveRuntimeToolWindowDefinition(ITvAirRuntimeCapabilityPlugin runtimePlugin, string route, PluginWindowRequest request)
{
    var descriptor = runtimePlugin.Descriptor;
    var requestedDefinitionId = ReadPayload(request.Payload ?? new Dictionary<string, string>(), "windowDefinitionId", "WindowDefinitionId");
    if (!string.IsNullOrWhiteSpace(requestedDefinitionId))
    {
        var explicitDefinition = descriptor.Windows.FirstOrDefault(window =>
            string.Equals(window.WindowDefinitionId, requestedDefinitionId, StringComparison.OrdinalIgnoreCase));
        if (explicitDefinition is not null) return explicitDefinition;
    }

    var normalizedRoute = NormalizePluginRouteSegment(route);
    var uiDefinition = descriptor.UiDefinitions.FirstOrDefault(ui =>
        ui.Kind == RuntimeUiKind.ToolWindow
        && string.Equals(NormalizePluginRouteSegment(ui.Route), normalizedRoute, StringComparison.OrdinalIgnoreCase));
    if (uiDefinition is not null && !string.IsNullOrWhiteSpace(uiDefinition.WindowDefinitionId))
    {
        return descriptor.Windows.FirstOrDefault(window =>
            string.Equals(window.WindowDefinitionId, uiDefinition.WindowDefinitionId, StringComparison.OrdinalIgnoreCase));
    }

    return descriptor.Windows.Count == 1 ? descriptor.Windows[0] : null;
}

static void ApplyRuntimeToolWindowSizeContract(PluginWindowRequest request, ITvAirRuntimeCapabilityPlugin runtimePlugin, string route, LogRepository? log = null, string source = "", string entryKind = "")
{
    if (request is null) return;
    request.Payload ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var definition = ResolveRuntimeToolWindowDefinition(runtimePlugin, route, request);
    var contractWidth = definition is null ? 0 : NormalizePluginWindowDimension((int)Math.Round(definition.InitialSize.Width));
    var contractHeight = definition is null ? 0 : NormalizePluginWindowDimension((int)Math.Round(definition.InitialSize.Height));
    var contractMinWidth = definition is null ? 0 : NormalizePluginWindowDimension((int)Math.Round(definition.MinimumSize.Width));
    var contractMinHeight = definition is null ? 0 : NormalizePluginWindowDimension((int)Math.Round(definition.MinimumSize.Height));
    var oldWidth = request.Width;
    var oldHeight = request.Height;
    var oldMinWidth = request.MinWidth;
    var oldMinHeight = request.MinHeight;

    if (!HasPluginWindowPayload(request, "width", "Width") && contractWidth > 0)
    {
        request.Width = contractWidth;
        request.Payload["width"] = request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    if (!HasPluginWindowPayload(request, "height", "Height") && contractHeight > 0)
    {
        request.Height = contractHeight;
        request.Payload["height"] = request.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Runtime descriptor is the canonical lower bound. A caller may request a stricter
    // minimum, but may not weaken the plugin's declared window contract.
    if (contractMinWidth > 0 && request.MinWidth < contractMinWidth)
    {
        request.MinWidth = contractMinWidth;
        request.Payload["minWidth"] = request.MinWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    if (contractMinHeight > 0 && request.MinHeight < contractMinHeight)
    {
        request.MinHeight = contractMinHeight;
        request.Payload["minHeight"] = request.MinHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    if (log is not null && (request.Width != oldWidth || request.Height != oldHeight || request.MinWidth != oldMinWidth || request.MinHeight != oldMinHeight))
    {
        log.Add("PLUGIN_TOOL_WINDOW_CONTRACT_RESOLVE", runtimePlugin.Descriptor.DisplayName, $"result=APPLIED source={SafePluginActionValue(source)} entryKind={SafePluginActionValue(entryKind)} windowDefinitionId={SafePluginActionValue(definition?.WindowDefinitionId)} descriptorSize={contractWidth}x{contractHeight} descriptorMinSize={contractMinWidth}x{contractMinHeight} oldSize={oldWidth}x{oldHeight} oldMinSize={oldMinWidth}x{oldMinHeight} newSize={request.Width}x{request.Height} newMinSize={request.MinWidth}x{request.MinHeight} rule=runtime_descriptor_window_contract");
    }
}

static string ResolvePluginToolWindowTitle(ITvAirRuntimeCapabilityPlugin runtimePlugin, string route, PluginWindowRequest request, string fallbackTitle)
{
    static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    var definitionTitle = Clean(ResolveRuntimeToolWindowDefinition(runtimePlugin, route, request)?.Title);
    if (!string.IsNullOrWhiteSpace(definitionTitle)) return definitionTitle;
    var fallback = Clean(fallbackTitle);
    return string.IsNullOrWhiteSpace(fallback) ? runtimePlugin.Descriptor.DisplayName : fallback;
}

static PluginWindowRequest ResolvePluginToolWindowContract(ITvAirRuntimeCapabilityPlugin runtimePlugin, string pluginActionId, string route, PluginWindowRequest request, string source, string entryKind, LogRepository log)
{
    route = (route ?? string.Empty).Trim().Trim('/');
    request.Action = string.IsNullOrWhiteSpace(request.Action) ? "openWindow" : request.Action.Trim();
    request.PluginId = NormalizePluginActionId(string.IsNullOrWhiteSpace(request.PluginId) ? pluginActionId : request.PluginId);
    request.RouteSegment = string.IsNullOrWhiteSpace(request.RouteSegment) ? route : request.RouteSegment.Trim().Trim('/');
    request.Title = ResolvePluginToolWindowTitle(runtimePlugin, route, request, string.IsNullOrWhiteSpace(request.Title) ? runtimePlugin.Descriptor.DisplayName : request.Title.Trim());
    request.ContentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? $"/plugin/{Uri.EscapeDataString(route)}" : request.ContentRoute.Trim();
    request.ReuseExisting = true;
    request.ActivateExisting = true;
    request.ResponseMode = string.IsNullOrWhiteSpace(request.ResponseMode) ? "hostHandled" : request.ResponseMode.Trim();
    request.Payload ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    request.Payload["source"] = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    request.Payload["entryKind"] = string.IsNullOrWhiteSpace(entryKind) ? "unknown" : entryKind;
    request.Payload["unifiedToolWindowEntry"] = "true";
    ApplyRuntimeToolWindowSizeContract(request, runtimePlugin, route, log, source, entryKind);
    var windowDefinition = ResolveRuntimeToolWindowDefinition(runtimePlugin, route, request);
    request.ScrollPolicy = windowDefinition?.ScrollPolicy ?? TvAIrPlugin.Windows.PluginWindowScrollPolicy.Auto;
    request.HorizontalScrollPolicy = windowDefinition?.HorizontalScrollPolicy ?? TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy.Auto;
    request.VerticalScrollPolicy = windowDefinition?.VerticalScrollPolicy ?? TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy.Auto;
    request.SizeReference = windowDefinition?.SizeReference ?? TvAIrPlugin.Windows.PluginWindowSizeReference.OuterWindow;
    request.ResizeMode = windowDefinition?.ResizeMode ?? (windowDefinition?.Resizable == false
        ? TvAIrPlugin.Windows.PluginWindowResizeMode.Fixed
        : TvAIrPlugin.Windows.PluginWindowResizeMode.Both);
    request.RefreshMode = windowDefinition?.RefreshMode ?? TvAIrPlugin.Windows.PluginWindowRefreshMode.Navigate;
    request.ContentSizePolicy = windowDefinition?.ContentSizePolicy ?? TvAIrPlugin.Windows.PluginWindowContentSizePolicy.Ignore;
    request.PreserveInteractionState = windowDefinition?.PreserveInteractionState ?? true;
    request.ReusePolicy = windowDefinition?.ReusePolicy ?? TvAIrPlugin.Windows.PluginWindowReusePolicy.PerRoute;
    request.ActivationPolicy = windowDefinition?.ActivationPolicy ?? TvAIrPlugin.Windows.PluginWindowActivationPolicy.ManualOpenOnly;
    request.CloseBehavior = windowDefinition?.CloseBehavior ?? TvAIrPlugin.Windows.PluginWindowCloseBehavior.Dispose;
    request.BackgroundExecution = windowDefinition?.BackgroundExecution ?? TvAIrPlugin.Windows.PluginWindowBackgroundExecution.StopWithWindow;
    request.StatePersistence = windowDefinition?.StatePersistence ?? TvAIrPlugin.Windows.PluginWindowStatePersistence.Placement;
    request.Payload["scrollPolicy"] = request.ScrollPolicy.ToString();
    request.Payload["horizontalScrollPolicy"] = request.HorizontalScrollPolicy.ToString();
    request.Payload["verticalScrollPolicy"] = request.VerticalScrollPolicy.ToString();
    request.Payload["sizeReference"] = request.SizeReference.ToString();
    request.Payload["resizeMode"] = request.ResizeMode.ToString();
    request.Payload["refreshMode"] = request.RefreshMode.ToString();
    request.Payload["reusePolicy"] = request.ReusePolicy.ToString();
    request.Payload["activationPolicy"] = request.ActivationPolicy.ToString();
    request.Payload["closeBehavior"] = request.CloseBehavior.ToString();
    request.Payload["backgroundExecution"] = request.BackgroundExecution.ToString();
    request.Payload["statePersistence"] = request.StatePersistence.ToString();
    request.Payload["contentSizePolicy"] = request.ContentSizePolicy.ToString();
    request.Width = request.Width > 0 ? request.Width : 620;
    request.Height = request.Height > 0 ? request.Height : 760;
    log.Add("PLUGIN_TOOL_WINDOW_ENTRY", runtimePlugin.Descriptor.DisplayName, $"result=RESOLVED source={SafePluginActionValue(source)} entryKind={SafePluginActionValue(entryKind)} pluginId={SafePluginActionValue(pluginActionId)} routeSegment={SafePluginActionValue(route)} requestRoute={SafePluginActionValue(request.RouteSegment)} size={request.Width}x{request.Height} sizeReference={request.SizeReference} resizeMode={request.ResizeMode} scrollX={request.HorizontalScrollPolicy} scrollY={request.VerticalScrollPolicy} legacyScroll={request.ScrollPolicy} refreshMode={request.RefreshMode} contentSizePolicy={request.ContentSizePolicy} preserveInteractionState={request.PreserveInteractionState} reusePolicy={request.ReusePolicy} activationPolicy={request.ActivationPolicy} closeBehavior={request.CloseBehavior} backgroundExecution={request.BackgroundExecution} statePersistence={request.StatePersistence} contentRoute={SafePluginActionValue(request.ContentRoute)} reuseExisting={request.ReuseExisting} activateExisting={request.ActivateExisting} rule=runtime_descriptor_window_contract");
    return request;
}

static (PluginWindowSession Session, PluginToolWindowOpenResult HostResult, string WindowUrl, string ContentRoute, string AbsoluteUrl, PluginToolWindowIconSpec IconSpec, bool ReusedSession) OpenOrActivatePluginToolWindowUnified(
    ITvAirRuntimeCapabilityPlugin runtimePlugin,
    string pluginActionId,
    string route,
    PluginWindowRequest request,
    string source,
    string entryKind,
    HttpRequest http,
    PluginWindowSessionStore windows,
    PluginToolWindowHostService toolWindows,
    LogRepository log)
{
    request = ResolvePluginToolWindowContract(runtimePlugin, pluginActionId, route, request, source, entryKind, log);
    var descriptor = runtimePlugin.Descriptor;
    var session = windows.OpenOrReuse(descriptor.DisplayName, pluginActionId, route, request, reuseExisting: true, out var reusedWindowSession);
    var windowUrl = $"/plugin-window/{Uri.EscapeDataString(session.WindowId)}";
    var contentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
    var hostCaps = toolWindows.GetCapabilities();
    var navigationUrl = BuildToolWindowNavigationUrl(windowUrl, contentRoute, hostCaps);
    var absoluteWindowUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
    var iconSpec = ResolvePluginToolWindowIcon(runtimePlugin, route, pluginActionId, log);
    var activateRequested = request.ActivationPolicy switch
    {
        TvAIrPlugin.Windows.PluginWindowActivationPolicy.Always => true,
        TvAIrPlugin.Windows.PluginWindowActivationPolicy.Never => false,
        _ => request.ActivateExisting
    };
    var hostResult = toolWindows.OpenOrActivate(session, absoluteWindowUrl, iconSpec, activateRequested);
    log.Add("PLUGIN_TOOL_WINDOW_ENTRY", descriptor.DisplayName, $"result=OPEN_OR_ACTIVATE source={SafePluginActionValue(source)} entryKind={SafePluginActionValue(entryKind)} windowId={SafePluginActionValue(session.WindowId)} reusedSession={reusedWindowSession} hostResult={SafePluginActionValue(hostResult.Result)} hostReused={hostResult.Reused} activated={hostResult.Activated} hostKind={SafePluginActionValue(hostResult.HostKind)} size={session.Width}x{session.Height} minSize={session.MinWidth}x{session.MinHeight} contentRoute={SafePluginActionValue(contentRoute)} rule=runtime_descriptor_window_contract");
    return (session, hostResult, windowUrl, contentRoute, absoluteWindowUrl, iconSpec, reusedWindowSession);
}

static async Task<IResult> HandlePluginWindowDispatchAsync(HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, PluginBoundaryGate boundaryGate, LogRepository log)
{
    var request = await ReadPluginWindowRequestAsync(http);
    NormalizePluginWindowRequestFromPayload(request);
    request.Payload ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var pluginId = NormalizePluginActionId(!string.IsNullOrWhiteSpace(request.PluginId)
        ? request.PluginId
        : ReadPayload(request.Payload, "PluginId", "pluginId"));
    var route = !string.IsNullOrWhiteSpace(request.RouteSegment)
        ? request.RouteSegment.Trim()
        : ReadPayload(request.Payload, "RouteSegment", "routeSegment");
    var action = NormalizePluginWindowAction(string.IsNullOrWhiteSpace(request.Action) ? "openWindow" : request.Action);
    request.Action = action;
    request.Payload["action"] = action;
    var responseMode = NormalizePluginFormResponseMode(!string.IsNullOrWhiteSpace(request.ResponseMode) ? request.ResponseMode : ReadPayload(request.Payload, "responseMode", "ResponseMode"));
    responseMode = NormalizePluginWindowActionResponseMode(http, request, action, responseMode);
    var requestedWindowIdForIdentity = NormalizePluginWindowId(!string.IsNullOrWhiteSpace(request.WindowId) ? request.WindowId : ReadPayload(request.Payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId", "safeEventWindowId", "SafeEventWindowId"));
    if (string.IsNullOrWhiteSpace(request.WindowId) && !string.IsNullOrWhiteSpace(requestedWindowIdForIdentity))
    {
        request.WindowId = requestedWindowIdForIdentity;
        request.Payload["windowId"] = requestedWindowIdForIdentity;
    }
    var recoveredWindowIdentity = RecoverPluginActionIdentity(registry, windows, pluginId, route, action, requestedWindowIdForIdentity, string.Empty, request.Payload);
    if (!string.Equals(pluginId, recoveredWindowIdentity.PluginId, StringComparison.Ordinal) || !string.Equals(route, recoveredWindowIdentity.RouteSegment, StringComparison.Ordinal))
    {
        pluginId = recoveredWindowIdentity.PluginId;
        route = recoveredWindowIdentity.RouteSegment;
        request.PluginId = pluginId;
        request.RouteSegment = route;
        if (!string.IsNullOrWhiteSpace(pluginId)) request.Payload["PluginId"] = pluginId;
        if (!string.IsNullOrWhiteSpace(route)) request.Payload["RouteSegment"] = route;
        log.Add("PLUGIN_WINDOW_IDENTITY_RECOVER", string.IsNullOrWhiteSpace(pluginId) ? "-" : pluginId, $"result=APPLIED action={SafePluginActionValue(action)} reason={SafePluginActionValue(recoveredWindowIdentity.Reason)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(route)} windowId={SafePluginActionValue(requestedWindowIdForIdentity)} endpoint={SafePluginActionValue(http.Path.Value)} rule=runtime_window_identity_recovery");
    }
    var plugin = FindPluginByActionIdentity(registry, pluginId, route);
    var pluginName = plugin?.Descriptor.DisplayName ?? pluginId;

    if (plugin is null)
    {
        log.Add("PLUGIN_WINDOW", "DENY", $"plugin={SafePluginActionValue(pluginId)} action={SafePluginActionValue(action)} result=DENIED reason=plugin_not_found endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin not found.", Diagnostics = "plugin_not_found" }, responseMode, StatusCodes.Status404NotFound);
    }

    var windowBoundary = boundaryGate.CheckWindow(ResolveRuntimeBoundaryPlugin(registry, plugin), action, http.Path.Value ?? string.Empty);
    if (!windowBoundary.Allowed)
    {
        log.Add("PLUGIN_WINDOW", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason={SafePluginActionValue(windowBoundary.Reason)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_boundary_gate");
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin window request was denied by TvAIr host boundary.", Diagnostics = windowBoundary.Reason }, responseMode, StatusCodes.Status403Forbidden);
    }

    var runtimeWindowPlugin = ResolveRuntimeBoundaryPlugin(registry, plugin);
    ApplyRuntimeToolWindowSizeContract(request, runtimeWindowPlugin, route);

    var token = request.WindowToken;
    var pluginActionIdForWindowToken = GetPluginActionIdentity(plugin);
    var requestedWindowIdForToken = NormalizePluginWindowId(!string.IsNullOrWhiteSpace(request.WindowId) ? request.WindowId : ReadPayload(request.Payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId", "safeEventWindowId", "SafeEventWindowId"));
    if (string.IsNullOrWhiteSpace(request.WindowId) && !string.IsNullOrWhiteSpace(requestedWindowIdForToken))
    {
        request.WindowId = requestedWindowIdForToken;
        request.Payload["windowId"] = requestedWindowIdForToken;
    }
    if (!ValidatePluginActionTokenOrRecoverHostWindow(actionTokens, windows, token, pluginActionIdForWindowToken, route, pluginName, action, requestedWindowIdForToken, null, http.Path.Value ?? string.Empty, "window_dispatch", log, out var tokenReason))
    {
        log.Add("PLUGIN_WINDOW", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason={tokenReason} windowId={SafePluginActionValue(requestedWindowIdForToken)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Invalid plugin window token.", Diagnostics = tokenReason }, responseMode, StatusCodes.Status400BadRequest);
    }

    if (action.Equals("openWindow", StringComparison.OrdinalIgnoreCase) || action.Equals("open", StringComparison.OrdinalIgnoreCase))
    {
        var pluginActionId = GetPluginActionIdentity(plugin);
        var hostOpenMode = IsPluginWindowHostOpenMode(responseMode);
        var defaultReuseExisting = hostOpenMode;
        var reuseExisting = request.ReuseExisting || defaultReuseExisting;
        var activateExisting = request.ActivateExisting || hostOpenMode;
        var unifiedOpen = OpenOrActivatePluginToolWindowUnified(runtimeWindowPlugin, pluginActionId, route, request, "openWindow", "api_or_plugin_window", http, windows, toolWindows, log);
        var session = unifiedOpen.Session;
        var reusedWindowSession = unifiedOpen.ReusedSession;
        var windowUrl = unifiedOpen.WindowUrl;
        var selfContentRoute = unifiedOpen.ContentRoute;
        log.Add("PLUGIN_WINDOW", pluginName, $"action=openWindow result={(reusedWindowSession ? "REUSED" : "ISSUED")} windowId={SafePluginActionValue(session.WindowId)} routeSegment={SafePluginActionValue(route)} title={SafePluginActionValue(session.Title)} size={session.Width}x{session.Height} minSize={session.MinWidth}x{session.MinHeight} resizable={session.Resizable} movable={session.Movable} alwaysOnTop={session.AlwaysOnTop} hostManaged=True reuseExisting={reuseExisting} activateExisting={activateExisting} windowUrl={SafePluginActionValue(windowUrl)} contentRoute={SafePluginActionValue(selfContentRoute)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        var result = new PluginWindowResult
        {
            Success = true,
            Message = "Plugin window request accepted by TvAIr host.",
            Diagnostics = hostOpenMode ? "plugin_tool_window_host_issued" : "host_managed_window_contract_issued",
            WindowId = session.WindowId,
            WindowUrl = windowUrl,
            ContentRoute = selfContentRoute,
            RefreshRequested = false,
            RefreshTarget = "content",
            PreserveScroll = request.PreserveScroll,
            Revision = session.Revision
        };
        if (hostOpenMode)
        {
            var hostCaps = toolWindows.GetCapabilities();
            var toolWindowNavigation = BuildToolWindowNavigationUrl(windowUrl, selfContentRoute, hostCaps);
            var toolWindowNavigationMode = IsToolWindowDirectContentNavigation(toolWindowNavigation) ? "directContent" : "shellIframe";
            var absoluteWindowUrl = unifiedOpen.AbsoluteUrl;
            var iconSpec = unifiedOpen.IconSpec;
            var hostResult = unifiedOpen.HostResult;
            log.Add("PLUGIN_TOOL_WINDOW_ICON", pluginName, $"action=openWindow windowId={SafePluginActionValue(session.WindowId)} pluginId={SafePluginActionValue(pluginActionId)} routeSegment={SafePluginActionValue(route)} manifestIcon={SafePluginActionValue(iconSpec.ManifestIcon)} source={SafePluginActionValue(hostResult.IconSource)} result={(hostResult.IconApplied ? "OK" : "FALLBACK_OR_NOT_APPLIED")} formIconApplied={hostResult.IconApplied} diagnostics={SafePluginActionValue(hostResult.IconDiagnostics)} priority=EmbeddedResource>plugin_file>default_TvAIr_icon rule=release_contract");
            var logLeft = session.Left?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            var logTop = session.Top?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            var returnUrl = ResolvePluginToolWindowReturnUrl(http, request, route);
            log.Add("PLUGIN_TOOL_WINDOW", pluginName, $"action=openWindow result={hostResult.Result} windowId={SafePluginActionValue(session.WindowId)} routeSegment={SafePluginActionValue(session.RouteSegment)} mode=toolWindow hostKind={hostResult.HostKind} webView2Runtime={hostResult.WebView2RuntimeAvailable} toolWindowSupported={hostCaps.ToolWindowSupported} fallbackHostKind={SafePluginActionValue(hostCaps.FallbackHostKind)} jsonScreenSuppressed={hostCaps.JsonScreenSuppressed} reuseKey={SafePluginActionValue(hostCaps.ReuseKey)} sessionReused={reusedWindowSession} reuseExisting={reuseExisting} activateExisting={activateExisting} hostReused={hostResult.Reused} activated={hostResult.Activated} stateRestored={hostResult.StateRestored} diagnostics={SafePluginActionValue(hostResult.Diagnostics)} url={SafePluginActionValue(absoluteWindowUrl)} navigationMode={SafePluginActionValue(toolWindowNavigationMode)} contentRoute={SafePluginActionValue(selfContentRoute)} size={session.Width}x{session.Height} minSize={session.MinWidth}x{session.MinHeight} left={logLeft} top={logTop} alwaysOnTop={session.AlwaysOnTop} iconManifest={SafePluginActionValue(iconSpec.ManifestIcon)} iconSource={SafePluginActionValue(hostResult.IconSource)} iconApplied={hostResult.IconApplied} iconDiagnostics={SafePluginActionValue(hostResult.IconDiagnostics)} formIconContract=release_contract positionPersistence={hostCaps.SupportsPositionPersistence} statePersistence={hostCaps.SupportsStatePersistence} jsonScreenSuppressed={hostCaps.JsonScreenSuppressed} formResponse=redirectBack status=303 returnUrl={SafePluginActionValue(returnUrl)} sourcePreserved=True currentPageNavigationSuppressed=True rule=release_contract");
            return PluginSeeOther(returnUrl);
        }
        return BuildPluginWindowDispatchResponse(result, responseMode, windowUrl);
    }

    if (action.Equals("closeWindow", StringComparison.OrdinalIgnoreCase) || action.Equals("close", StringComparison.OrdinalIgnoreCase))
    {
        var resolvedCloseWindowId = NormalizePluginWindowId(!string.IsNullOrWhiteSpace(request.WindowId) ? request.WindowId : ReadPayload(request.Payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId", "safeEventWindowId", "SafeEventWindowId"));
        request.WindowId = resolvedCloseWindowId;
        // Hostが生存しているToolWindowは、×閉鎖と同じHost lifecycle正本へ通す。
        // SessionStoreだけを先に削除する別ルートを作らない。Host不在のstale Sessionだけ最終回収する。
        var toolWindowClosed = toolWindows.Close(resolvedCloseWindowId);
        var closedSession = toolWindowClosed ? null : windows.DeleteClosed(resolvedCloseWindowId, GetPluginActionIdentity(plugin));
        var ok = toolWindowClosed || closedSession is not null;
        var resultText = ok ? (toolWindowClosed ? "HOST_CLOSE_REQUESTED" : "STALE_SESSION_REMOVED") : "NOT_FOUND";
        log.Add("PLUGIN_WINDOW", pluginName, $"action=closeWindow result={resultText} windowId={SafePluginActionValue(resolvedCloseWindowId)} routeSegment={SafePluginActionValue(route)} toolWindowCloseRequested={toolWindowClosed} responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} navigationSuppressed=True rule=plugin_window_close_contract");
        return ok
            ? BuildPluginWindowDispatchResponse(new PluginWindowResult { Success = true, Message = "Plugin window closed.", Diagnostics = "closed", WindowId = resolvedCloseWindowId }, responseMode, ResolvePluginToolWindowReturnUrl(http, request, route))
            : BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin window not found.", Diagnostics = "window_not_found", WindowId = resolvedCloseWindowId }, responseMode, StatusCodes.Status404NotFound);
    }

    if (action.Equals("updateWindow", StringComparison.OrdinalIgnoreCase) || action.Equals("update", StringComparison.OrdinalIgnoreCase))
    {
        var requestedAlwaysOnTopPresent = HasPluginWindowPayload(request, "alwaysOnTop", "AlwaysOnTop");
        var refreshAfter = request.RefreshAfter || (TryReadBoolPayload(request.Payload, out var refreshAfterValue, "refreshAfter", "RefreshAfter", "refresh", "Refresh") && refreshAfterValue);
        var refreshTarget = NormalizePluginWindowRefreshTarget(request.RefreshTarget);
        request.RefreshTarget = refreshTarget;

        var session = windows.Update(request.WindowId, GetPluginActionIdentity(plugin), request);
        var ok = session is not null;
        var hostApply = ok ? toolWindows.ApplySession(session!.WindowId, session!) : PluginToolWindowApplyResult.NotFound(request.WindowId);
        var refreshIssued = false;
        var hostRefreshResult = "-";
        string? responseContentRoute = null;
        if (ok)
        {
            responseContentRoute = BuildHostManagedPluginContentRoute(session!.ContentRoute, session.WindowId, session.Revision);
        }

        if (ok && refreshAfter)
        {
            var refreshed = windows.Refresh(session!.WindowId, GetPluginActionIdentity(plugin), request);
            if (refreshed is not null)
            {
                session = refreshed;
                responseContentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
                var hostCaps = toolWindows.GetCapabilities();
                var navigationUrl = BuildToolWindowNavigationUrl($"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", responseContentRoute, hostCaps);
                var absoluteNavigationUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
                var hostResult = toolWindows.RefreshExisting(session, absoluteNavigationUrl);
                hostRefreshResult = hostResult.Result;
                refreshIssued = true;
            }
            else
            {
                hostRefreshResult = "REFRESH_NOT_FOUND";
            }
        }

        log.Add("PLUGIN_WINDOW", pluginName, $"action=updateWindow result={(ok ? "OK" : "NOT_FOUND")} windowId={SafePluginActionValue(request.WindowId)} revision={session?.Revision ?? 0} payloadAlwaysOnTopPresent={requestedAlwaysOnTopPresent} payloadAlwaysOnTop={request.AlwaysOnTop} sessionAlwaysOnTop={session?.AlwaysOnTop.ToString() ?? "-"} hostUpdated={hostApply.HostAccepted} hostApplied={hostApply.Applied} hostBeforeTopMost={hostApply.BeforeTopMost?.ToString() ?? "-"} hostAfterTopMost={hostApply.AfterTopMost?.ToString() ?? "-"} hostBeforeSize={FormatPluginHostSize(hostApply.BeforeWidth, hostApply.BeforeHeight)} hostAfterSize={FormatPluginHostSize(hostApply.AfterWidth, hostApply.AfterHeight)} hostDiagnostics={SafePluginActionValue(hostApply.Diagnostics)} refreshAfter={refreshAfter} refreshTarget={SafePluginActionValue(refreshTarget)} refreshIssued={refreshIssued} hostRefresh={SafePluginActionValue(hostRefreshResult)} responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return ok
            ? BuildPluginWindowDispatchResponse(new PluginWindowResult { Success = true, Message = "Plugin window updated.", Diagnostics = (hostApply.Applied ? "updated;hostApplied" : $"updated;{hostApply.Diagnostics}") + (refreshIssued ? ";refreshIssued" : string.Empty), WindowId = session!.WindowId, WindowUrl = $"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", ContentRoute = responseContentRoute ?? BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision), RefreshRequested = refreshIssued, RefreshTarget = refreshTarget, PreserveScroll = request.PreserveScroll, Revision = session.Revision }, responseMode, responseContentRoute ?? BuildHostManagedPluginContentRoute(session!.ContentRoute, session.WindowId, session.Revision))
            : BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin window not found.", Diagnostics = "window_not_found", WindowId = request.WindowId }, responseMode, StatusCodes.Status404NotFound);
    }

    if (action.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase)
        || action.Equals("rerenderWindow", StringComparison.OrdinalIgnoreCase)
        || action.Equals("refresh", StringComparison.OrdinalIgnoreCase)
        || action.Equals("rerender", StringComparison.OrdinalIgnoreCase))
    {
        var refreshTarget = NormalizePluginWindowRefreshTarget(request.RefreshTarget);
        request.RefreshTarget = refreshTarget;
        var resolvedWindowId = ResolvePluginWindowId(request);
        var session = windows.Refresh(resolvedWindowId, GetPluginActionIdentity(plugin), request);
        var ok = session is not null;
        var hostRefreshResult = "-";
        string? refreshedContentRoute = null;
        if (ok)
        {
            refreshedContentRoute = BuildHostManagedPluginContentRoute(session!.ContentRoute, session.WindowId, session.Revision);
            var hostCaps = toolWindows.GetCapabilities();
            var navigationUrl = BuildToolWindowNavigationUrl($"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", refreshedContentRoute, hostCaps);
            var absoluteNavigationUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
            var hostResult = toolWindows.RefreshExisting(session, absoluteNavigationUrl);
            hostRefreshResult = hostResult.Result;
        }
        log.Add("PLUGIN_WINDOW", pluginName, $"action=refreshWindow result={(ok ? "ISSUED" : "NOT_FOUND")} windowId={SafePluginActionValue(resolvedWindowId)} target={SafePluginActionValue(refreshTarget)} preserveScroll={request.PreserveScroll} revision={session?.Revision ?? 0} contentRoute={SafePluginActionValue(session?.ContentRoute)} hostRefresh={SafePluginActionValue(hostRefreshResult)} responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} reloadScope=toolwindow-content-document_or_iframe-content-only rule=release_contract");
        if (ok)
        {
            var result = new PluginWindowResult { Success = true, Message = "Plugin window refresh requested.", Diagnostics = "refresh_requested", WindowId = session!.WindowId, WindowUrl = $"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", ContentRoute = refreshedContentRoute ?? BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision), RefreshRequested = true, RefreshTarget = refreshTarget, PreserveScroll = request.PreserveScroll, Revision = session.Revision };
            return BuildPluginWindowDispatchResponse(result, responseMode, result.ContentRoute);
        }
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin window not found.", Diagnostics = "window_not_found", WindowId = resolvedWindowId, RefreshTarget = refreshTarget, PreserveScroll = request.PreserveScroll}, responseMode, StatusCodes.Status404NotFound);
    }

    log.Add("PLUGIN_WINDOW", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason=unsupported_window_action responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
    return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Unsupported plugin window action.", Diagnostics = "unsupported_window_action" }, responseMode, StatusCodes.Status400BadRequest);
}

static IResult RenderPluginWindowState(string windowId, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, LogRepository log)
{
    var session = windows.Get(windowId);
    if (session is null)
    {
        log.Add("PLUGIN_WINDOW", "Host", $"action=state result=STALE_WINDOW_SESSION windowId={SafePluginActionValue(windowId)} reason=window_session_not_found_after_host_restart_or_closed_window action=client_should_reopen_window rule=release_contract");
        return Results.NotFound(new { success = false, diagnostics = "stale_window_session", windowId });
    }

    var caps = toolWindows.GetCapabilities();
    var hostState = toolWindows.GetHostState(session.WindowId);
    var hostAlive = hostState?.HostAlive ?? toolWindows.IsHostAlive(session.WindowId);
    log.Add("PLUGIN_WINDOW", "State", $"action=state result=OK windowId={SafePluginActionValue(windowId)} revision={session.Revision} hostAlive={hostAlive} alwaysOnTop={hostState?.AlwaysOnTop ?? session.AlwaysOnTop} windowState={SafePluginActionValue(hostState?.WindowState ?? "unknown")} isMinimized={hostState?.IsMinimized ?? false} source=authoritative endpoint=/plugin-window/{{windowId}}/state rule=release_contract");
    return Results.Ok(new
    {
        success = true,
        contractVersion = TvAIrVersionContract.PluginHostContractVersion,
        stateSource = "authoritative",
        windowId = session.WindowId,
        pluginId = session.PluginId,
        routeSegment = session.RouteSegment,
        contentRoute = session.ContentRoute,
        revision = session.Revision,
        refreshRequested = session.RefreshRequested,
        refreshTarget = "content",
        reloadScope = "iframe-content-only",
        preserveScroll = session.PreserveInteractionState && session.PreserveScroll,
        preserveInteractionState = session.PreserveInteractionState,
        title = session.Title,
        width = hostState?.Width > 0 ? hostState.Width : session.Width,
        height = hostState?.Height > 0 ? hostState.Height : session.Height,
        minWidth = session.MinWidth,
        minHeight = session.MinHeight,
        left = hostState?.Left ?? session.Left,
        top = hostState?.Top ?? session.Top,
        resizable = session.Resizable,
        movable = session.Movable,
        alwaysOnTop = hostState?.AlwaysOnTop ?? session.AlwaysOnTop,
        windowState = hostState?.WindowState ?? "unknown",
        isMinimized = hostState?.IsMinimized ?? false,
        minimizedStatePersistenceSuppressed = true,
        reuseKey = session.ReuseKey,
        isClosed = session.IsClosed,
        hostAlive,
        hostKind = hostState?.HostKind ?? caps.HostKind,
        webView2RuntimeAvailable = hostState?.WebView2RuntimeAvailable ?? caps.WebView2RuntimeAvailable,
        toolWindowSupported = caps.ToolWindowSupported,
        positionPersistenceSupported = caps.SupportsPositionPersistence,
        statePersistenceSupported = caps.SupportsStatePersistence,
        jsonScreenSuppressed = caps.JsonScreenSuppressed,
        supportsAlwaysOnTop = caps.SupportsAlwaysOnTop,
        closeSync = "closeWindow_and_host_x_button",
        createdAt = session.CreatedAt,
        updatedAt = session.UpdatedAt
    });
}

static bool IsTruthy(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    var v = value.Trim();
    return v.Equals("1", StringComparison.OrdinalIgnoreCase)
        || v.Equals("true", StringComparison.OrdinalIgnoreCase)
        || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || v.Equals("on", StringComparison.OrdinalIgnoreCase);
}

static IResult RenderPluginWindowHostCapabilities(PluginToolWindowHostService toolWindows, LogRepository log)
{
    var caps = toolWindows.GetCapabilities();
    log.Add("PLUGIN_TOOL_WINDOW", "Capabilities", $"action=capabilities result=OK toolWindowSupported={caps.ToolWindowSupported} webView2Runtime={caps.WebView2RuntimeAvailable} hostKind={SafePluginActionValue(caps.HostKind)} fallbackHostKind={SafePluginActionValue(caps.FallbackHostKind)} jsonScreenSuppressed={caps.JsonScreenSuppressed} reuseKey={SafePluginActionValue(caps.ReuseKey)} positionPersistence={caps.SupportsPositionPersistence} statePersistence={caps.SupportsStatePersistence} rule=release_contract");
    return Results.Ok(new
    {
        success = true,
        contractVersion = caps.ContractVersion,
        toolWindowSupported = caps.ToolWindowSupported,
        hostWindowSupported = caps.HostWindowSupported,
        webView2RuntimeAvailable = caps.WebView2RuntimeAvailable,
        hostKind = caps.HostKind,
        fallbackHostKind = caps.FallbackHostKind,
        fallbackToBrowserRedirectSupported = caps.FallbackToBrowserRedirectSupported,
        jsonScreenSuppressed = caps.JsonScreenSuppressed,
        supportsAlwaysOnTop = caps.SupportsAlwaysOnTop,
        supportsSize = caps.SupportsSize,
        supportsMinSize = caps.SupportsMinSize,
        supportsPositionPersistence = caps.SupportsPositionPersistence,
        supportsStatePersistence = caps.SupportsStatePersistence,
        supportsReuseExisting = caps.SupportsReuseExisting,
        supportsActivateExisting = caps.SupportsActivateExisting,
        reuseKey = caps.ReuseKey,
        refreshTarget = caps.RefreshTarget,
        refreshReloadScope = caps.RefreshReloadScope,
        scriptExecutionAllowed = caps.ScriptExecutionAllowed,
        openWindowModes = new[] { "json", "redirect", "redirectBack", "hostHandled", "toolWindow", "toolWindowRedirectBack", "hostWindow", "auto", "html", "noContent" }
    });
}


static string ProgramGuideWaveGroupFromNetworkId(ushort networkId)
{
    // ARIB: BS uses original_network_id=4. Current Japanese CS services handled by TvAIr/TVTest
    // are NID 6/7. Terrestrial original_network_id values are much larger, so do not use
    // a simple >4 rule here.
    if (networkId == 4) return "BS";
    if (networkId == 6 || networkId == 7) return "CS";
    return "GR";
}


static string BuildHostManagedPluginContentRoute(string contentRoute, string windowId, int revision)
{
    var route = string.IsNullOrWhiteSpace(contentRoute) ? "/plugin" : contentRoute.Trim();
    var hashIndex = route.IndexOf('#');
    var fragment = hashIndex >= 0 ? route[hashIndex..] : string.Empty;
    if (hashIndex >= 0) route = route[..hashIndex];
    var separator = route.Contains('?') ? "&" : "?";
    var encodedWindowId = Uri.EscapeDataString(windowId);
    var encodedRevision = Uri.EscapeDataString(revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
    // Host-managed tool window content must be rendered as plugin content only, not through the normal TvAIr page chrome.
    return $"{route}{separator}__tvairWindowId={encodedWindowId}&__tvairHostWindow=1&__tvairToolHostContent=1&_tvairWindowRevision={encodedRevision}{fragment}";
}


static string NormalizePluginWindowId(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    foreach (var candidate in SplitPluginWindowRawValues(value))
    {
        var normalized = NormalizePluginWindowIdAtom(candidate);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;
    }
    return string.Empty;
}

static string NormalizePluginWindowAction(string? value)
{
    var fallback = string.IsNullOrWhiteSpace(value) ? "openWindow" : value;
    foreach (var candidate in SplitPluginWindowRawValues(fallback))
    {
        var action = new string(candidate.Trim().Where(ch => char.IsLetterOrDigit(ch)).ToArray());
        if (string.IsNullOrWhiteSpace(action)) continue;
        if (action.Equals("open", StringComparison.OrdinalIgnoreCase)) return "openWindow";
        if (action.Equals("close", StringComparison.OrdinalIgnoreCase)) return "closeWindow";
        if (action.Equals("update", StringComparison.OrdinalIgnoreCase)) return "updateWindow";
        if (action.Equals("refresh", StringComparison.OrdinalIgnoreCase)) return "refreshWindow";
        if (action.Equals("rerender", StringComparison.OrdinalIgnoreCase)) return "rerenderWindow";
        if (action.Equals("openWindow", StringComparison.OrdinalIgnoreCase)) return "openWindow";
        if (action.Equals("closeWindow", StringComparison.OrdinalIgnoreCase)) return "closeWindow";
        if (action.Equals("updateWindow", StringComparison.OrdinalIgnoreCase)) return "updateWindow";
        if (action.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase)) return "refreshWindow";
        if (action.Equals("rerenderWindow", StringComparison.OrdinalIgnoreCase)) return "rerenderWindow";
    }
    return string.IsNullOrWhiteSpace(value) ? "openWindow" : new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch)).ToArray());
}

static IEnumerable<string> SplitPluginWindowRawValues(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) yield break;
    foreach (var part in value.Split(new[] { ',', ';', '|', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
    {
        var trimmed = part.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            yield return trimmed;
    }
}

static string NormalizePluginWindowIdAtom(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var normalized = new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
    return CollapseRepeatedPluginWindowId(normalized);
}

static string CollapseRepeatedPluginWindowId(string value)
{
    var current = value;
    while (current.Length > 0 && current.Length % 2 == 0)
    {
        var half = current.Length / 2;
        var left = current[..half];
        var right = current[half..];
        if (!left.Equals(right, StringComparison.OrdinalIgnoreCase)) break;
        current = left;
    }
    return current;
}

static bool ValidatePluginActionTokenOrRecoverHostWindow(
    PluginActionTokenStore actionTokens,
    PluginWindowSessionStore windows,
    string? token,
    string pluginActionId,
    string routeSegment,
    string pluginName,
    string action,
    string? windowId,
    string? safeEventWindowId,
    string endpoint,
    string logEvent,
    LogRepository log,
    out string reason)
{
    if (actionTokens.Validate(token, pluginActionId, null, out reason))
        return true;

    var initialReason = reason;
    var canRecoverReason = initialReason.Equals("missing_token", StringComparison.OrdinalIgnoreCase)
        || initialReason.Equals("token_not_found", StringComparison.OrdinalIgnoreCase)
        || initialReason.Equals("token_expired", StringComparison.OrdinalIgnoreCase);
    var normalizedWindowId = NormalizePluginWindowId(windowId);
    var normalizedSafeEventWindowId = NormalizePluginWindowId(safeEventWindowId);

    if (!canRecoverReason)
    {
        log.Add("PLUGIN_SAFE_EVENT_TOKEN_REFRESH", pluginName, $"result=SKIPPED action={SafePluginActionValue(action)} reason={SafePluginActionValue(initialReason)} windowId={SafePluginActionValue(normalizedWindowId)} endpoint={SafePluginActionValue(endpoint)} rule=release_contract");
        return false;
    }
    if (string.IsNullOrWhiteSpace(normalizedWindowId))
    {
        reason = $"{initialReason}_no_window";
        log.Add("PLUGIN_SAFE_EVENT_TOKEN_REFRESH", pluginName, $"result=DENIED action={SafePluginActionValue(action)} reason={SafePluginActionValue(reason)} endpoint={SafePluginActionValue(endpoint)} rule=release_contract");
        return false;
    }
    if (!string.IsNullOrWhiteSpace(normalizedSafeEventWindowId)
        && !string.Equals(normalizedWindowId, normalizedSafeEventWindowId, StringComparison.OrdinalIgnoreCase))
    {
        reason = $"{initialReason}_safe_event_window_mismatch";
        log.Add("PLUGIN_SAFE_EVENT_TOKEN_REFRESH", pluginName, $"result=DENIED action={SafePluginActionValue(action)} reason={SafePluginActionValue(reason)} windowId={SafePluginActionValue(normalizedWindowId)} safeEventWindowId={SafePluginActionValue(normalizedSafeEventWindowId)} endpoint={SafePluginActionValue(endpoint)} rule=release_contract");
        return false;
    }

    var session = windows.Get(normalizedWindowId);
    if (session is null)
    {
        reason = $"{initialReason}_window_not_found";
        log.Add("PLUGIN_SAFE_EVENT_TOKEN_REFRESH", pluginName, $"result=DENIED action={SafePluginActionValue(action)} reason={SafePluginActionValue(reason)} windowId={SafePluginActionValue(normalizedWindowId)} endpoint={SafePluginActionValue(endpoint)} rule=release_contract");
        return false;
    }
    if (session.IsClosed || !session.HostAlive)
    {
        reason = session.IsClosed ? $"{initialReason}_window_closed" : $"{initialReason}_host_not_alive";
        log.Add("PLUGIN_SAFE_EVENT_TOKEN_REFRESH", pluginName, $"result=DENIED action={SafePluginActionValue(action)} reason={SafePluginActionValue(reason)} windowId={SafePluginActionValue(normalizedWindowId)} hostAlive={session.HostAlive} isClosed={session.IsClosed} endpoint={SafePluginActionValue(endpoint)} rule=release_contract");
        return false;
    }
    var normalizedRequestRouteSegment = (routeSegment ?? string.Empty).Trim().Trim('/');
    if (!string.Equals(session.PluginId, pluginActionId, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(session.RouteSegment, normalizedRequestRouteSegment, StringComparison.OrdinalIgnoreCase))
    {
        reason = $"{initialReason}_window_identity_mismatch";
        log.Add("PLUGIN_SAFE_EVENT_TOKEN_REFRESH", pluginName, $"result=DENIED action={SafePluginActionValue(action)} reason={SafePluginActionValue(reason)} windowId={SafePluginActionValue(normalizedWindowId)} sessionPlugin={SafePluginActionValue(session.PluginId)} requestPlugin={SafePluginActionValue(pluginActionId)} sessionRoute={SafePluginActionValue(session.RouteSegment)} requestRoute={SafePluginActionValue(normalizedRequestRouteSegment)} endpoint={SafePluginActionValue(endpoint)} rule=release_contract");
        return false;
    }

    var refreshed = actionTokens.Issue(pluginActionId, normalizedRequestRouteSegment);
    reason = "OK_RECOVERED";
    log.Add("PLUGIN_SAFE_EVENT_TOKEN_REFRESH", pluginName, $"result=OK action={SafePluginActionValue(action)} reason={SafePluginActionValue(initialReason)} windowId={SafePluginActionValue(normalizedWindowId)} routeSegment={SafePluginActionValue(routeSegment)} newTokenIssued=True tokenExpiresAt={refreshed.ExpiresAt:O} followup=next_directcontent_render_injects_latest_token logEvent={SafePluginActionValue(logEvent)} endpoint={SafePluginActionValue(endpoint)} rule=release_contract");
    return true;
}

static string BuildPluginRenderHtmlAudit(string? html)
{
    var text = html ?? string.Empty;
    var length = text.Length;
    var lower = text.Length == 0 ? string.Empty : text.ToLowerInvariant();
    return $"htmlLength={length} isNull={(html is null)} isEmpty={string.IsNullOrEmpty(text)} htmlHash={ComputePluginHtmlHash(text)} containsHtml={lower.Contains("<html")} containsHead={lower.Contains("<head")} containsBody={lower.Contains("<body")} containsStyle={lower.Contains("<style")} containsClassAttr={lower.Contains(" class=")} containsStyleAttr={lower.Contains(" style=")} containsForm={lower.Contains("<form")} containsButton={lower.Contains("<button")} containsHiddenInput={lower.Contains("type=\"hidden\"") || lower.Contains("type='hidden'")} containsDataAttr={lower.Contains(" data-")} containsAriaLabel={lower.Contains("aria-label=")} containsTitleAttr={lower.Contains(" title=")} containsScript={lower.Contains("<script")} containsOnEventAttr={System.Text.RegularExpressions.Regex.IsMatch(text, "\\son[a-zA-Z]+\\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)} sample={SafePluginActionValue(PluginHtmlSample(text, 300))}";
}

static string ComputePluginHtmlHash(string text)
{
    unchecked
    {
        uint hash = 2166136261;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
}

static string PluginHtmlSample(string text, int maxChars)
{
    if (string.IsNullOrEmpty(text)) return string.Empty;
    var sample = text.Length <= maxChars ? text : text[..maxChars];
    return sample.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}

static string BuildPluginRenderErrorBody(string pluginName, string routeSegment, string errorMessage)
{
    var safePlugin = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(pluginName) ? routeSegment : pluginName);
    var safeRoute = HtmlEncoder.Default.Encode(routeSegment);
    var safeMessage = HtmlEncoder.Default.Encode(errorMessage);
    return $"<div class=\"tvair-plugin-render-error\">" +
           $"<h2>Plugin RenderHtml error</h2>" +
           $"<p>plugin={safePlugin} route={safeRoute}</p>" +
           $"<pre>{safeMessage}</pre>" +
           $"</div>";
}

static string NormalizePluginRouteSegment(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    return new string(value.Trim().Trim('/').Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
}

static string NormalizePluginAssetName(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var name = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
    name = new string(name.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ).ToArray());
    return name;
}

static string ResolvePluginAssetRouteSegment(string pluginOrRoute, PluginRegistry registry)
{
    var normalizedRoute = NormalizePluginRouteSegment(pluginOrRoute);
    var runtime = registry.FindRuntimePlugin(pluginOrRoute);
    if (runtime is null) return normalizedRoute;
    var descriptorRoute = runtime.Descriptor.UiDefinitions
        .Select(definition => NormalizePluginRouteSegment(definition.Route))
        .FirstOrDefault(route => !string.IsNullOrWhiteSpace(route));
    return string.IsNullOrWhiteSpace(descriptorRoute) ? normalizedRoute : descriptorRoute;
}


static PluginToolWindowIconSpec ResolvePluginToolWindowIcon(ITvAirRuntimeCapabilityPlugin runtimePlugin, string? routeSegment, string pluginActionId, LogRepository log)
{
    var descriptor = runtimePlugin.Descriptor;
    var iconAsset = descriptor.Assets.FirstOrDefault(asset =>
        string.Equals(Path.GetExtension(asset.LogicalPath), ".ico", StringComparison.OrdinalIgnoreCase));
    if (iconAsset is null)
        return ResolveDefaultToolWindowIcon(string.Empty, "default_TvAIr_icon", "runtime_descriptor_icon_empty");

    var normalizedIcon = NormalizePluginAssetName(iconAsset.LogicalPath);
    try
    {
        var asm = runtimePlugin.GetType().Assembly;
        var resource = asm.GetManifestResourceNames().FirstOrDefault(name =>
            string.Equals(name, iconAsset.ResourceName, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + iconAsset.ResourceName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(resource))
        {
            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                    return new PluginToolWindowIconSpec(normalizedIcon, "runtime_descriptor_resource", "runtime_descriptor_declared_icon", bytes, null);
            }
        }
    }
    catch (Exception ex)
    {
        log.Add("PLUGIN_TOOL_WINDOW_ICON", descriptor.DisplayName, $"phase=resolve source=runtime_descriptor_resource result=ERROR asset={SafePluginActionValue(normalizedIcon)} error={SafePluginActionValue(ex.GetType().Name)} rule=runtime_descriptor_asset_contract");
    }

    return ResolveDefaultToolWindowIcon(normalizedIcon, "default_TvAIr_icon", "runtime_descriptor_icon_not_found");
}

static PluginToolWindowIconSpec ResolveDefaultToolWindowIcon(string manifestIcon, string source, string diagnostics)
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "TvAIr_Idle.ico"),
        Path.Combine(AppContext.BaseDirectory, "TvAIr_Recording.ico")
    };
    foreach (var candidate in candidates)
    {
        try
        {
            if (File.Exists(candidate))
                return new PluginToolWindowIconSpec(manifestIcon, source, diagnostics + ";default_icon", null, candidate);
        }
        catch { }
    }
    return new PluginToolWindowIconSpec(manifestIcon, source, diagnostics + ";system_default", null, null);
}

static IResult ResolvePluginAssetResult(string pluginOrRoute, string assetName, PluginRegistry registry, PluginBoundaryGate boundaryGate, LogRepository log, string endpoint, string source)
{
    var route = ResolvePluginAssetRouteSegment(pluginOrRoute, registry);
    var name = NormalizePluginAssetName(assetName);
    if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(name))
    {
        log.Add("PLUGIN_ASSET", "DENY", $"result=BAD_REQUEST source={SafePluginActionValue(source)} pluginOrRoute={SafePluginActionValue(pluginOrRoute)} asset={SafePluginActionValue(assetName)} reason=invalid_route_or_asset rule=release_contract");
        return Results.BadRequest("Invalid plugin asset request.");
    }

    var assetPlugin = FindPluginByActionIdentity(registry, pluginOrRoute, route);
    if (assetPlugin is null)
    {
        log.Add("PLUGIN_ASSET", route, $"result=DENIED asset={SafePluginActionValue(name)} reason=plugin_not_found source={SafePluginActionValue(source)} rule=plugin_boundary_gate");
        return Results.NotFound();
    }

    var assetBoundary = boundaryGate.CheckAsset(ResolveRuntimeBoundaryPlugin(registry, assetPlugin), name, endpoint);
    if (!assetBoundary.Allowed)
    {
        log.Add("PLUGIN_ASSET", route, $"result=DENIED asset={SafePluginActionValue(name)} reason={SafePluginActionValue(assetBoundary.Reason)} source={SafePluginActionValue(source)} rule=plugin_boundary_gate");
        return Results.NotFound();
    }

    var ext = Path.GetExtension(name).ToLowerInvariant();
    if (ext != ".png")
    {
        log.Add("PLUGIN_ASSET", route, $"result=DENIED asset={SafePluginActionValue(name)} reason=unsupported_extension allowed=png rule=release_contract");
        return Results.NotFound();
    }

    var pluginDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Plugins", route));
    var candidates = new[]
    {
        Path.Combine(pluginDir, "Assets", name),
        Path.Combine(pluginDir, "assets", name),
        Path.Combine(pluginDir, "wwwroot", "assets", name),
        Path.Combine(pluginDir, "wwwroot", name)
    };
    var rootPrefix = pluginDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    foreach (var candidate in candidates)
    {
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
        if (!File.Exists(full)) continue;
        log.Add("PLUGIN_ASSET", route, $"result=OK asset={SafePluginActionValue(name)} pathKind=plugin_assets contentType=image/png cache=no-cache source={SafePluginActionValue(source)} rule=release_contract");
        return Results.File(full, "image/png", enableRangeProcessing: false);
    }

    log.Add("PLUGIN_ASSET", route, $"result=NOT_FOUND asset={SafePluginActionValue(name)} searched=Assets|assets|wwwroot/assets|wwwroot rule=release_contract");
    return Results.NotFound();
}

static IResult RenderPluginWindowHost(string windowId, HttpRequest http, PluginWindowSessionStore windows, LogRepository log)
{
    var session = windows.Get(windowId);
    if (session is null)
    {
        log.Add("PLUGIN_WINDOW", "Host", $"action=render result=NOT_FOUND windowId={SafePluginActionValue(windowId)} rule=release_contract");
        return Results.NotFound("Plugin window not found.");
    }
    if (session.IsClosed)
    {
        log.Add("PLUGIN_WINDOW", session.PluginName, $"action=render result=CLOSED windowId={SafePluginActionValue(windowId)} routeSegment={SafePluginActionValue(session.RouteSegment)} rule=release_contract");
        return PluginHtmlMessage("プラグイン画面", "このプラグイン画面は閉じられています。もう一度プラグイン画面から開いてください。", StatusCodes.Status410Gone, $"/plugin/{Uri.EscapeDataString(session.RouteSegment)}", "プラグインへ戻る");
    }

    var title = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(session.Title) ? session.PluginName : session.Title);
    var contentRoute = string.IsNullOrWhiteSpace(session.ContentRoute) ? $"/plugin/{session.RouteSegment}" : session.ContentRoute;
    var content = HtmlEncoder.Default.Encode(BuildHostManagedPluginContentRoute(contentRoute, session.WindowId, session.Revision));
    var encodedWindowId = HtmlEncoder.Default.Encode(session.WindowId);
    var stateUrl = HtmlEncoder.Default.Encode($"/plugin-window/{Uri.EscapeDataString(session.WindowId)}/state");
    var initialRevision = session.Revision;
    // /plugin-window/{windowId} is always a host-managed plugin window shell.
    // It must not be treated as a normal browser page, otherwise TvAIr navigation chrome can leak into tool windows.
    var toolHost = true;
    var hostClass = "tvair-plugin-window-host tvair-plugin-window-host-tool";
    
    var html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{title}}</title>
<link rel="stylesheet" href="/tvair-generated-surfaces.css?v=1.2.0-css-final199">
</head>
<body class="tvair-plugin-window-page">
<div class="{{hostClass}}" data-window-id="{{encodedWindowId}}" data-window-revision="{{initialRevision}}" data-tool-host="{{toolHost.ToString().ToLowerInvariant()}}">
  <div class="tvair-plugin-window-title tvair-plugin-window-title-hidden">{{title}}</div>
  <iframe id="tvair-plugin-window-frame" class="tvair-plugin-window-frame" src="{{content}}" title="{{title}}" scrolling="auto"></iframe>
</div>
<script>
(() => {
  const stateUrl = "{{stateUrl}}";
  const frame = document.getElementById('tvair-plugin-window-frame');
  let lastRevision = Number(document.querySelector('.tvair-plugin-window-host')?.dataset.windowRevision || '0');
  let refreshing = false;
  let pollInFlight = false;
  let pollTimer = 0;
  const pollIntervalMs = 1000;
  function scheduleWindowStatePoll(immediate = false) {
    window.clearTimeout(pollTimer);
    if (document.hidden) return;
    pollTimer = window.setTimeout(pollWindowState, immediate ? 0 : pollIntervalMs);
  }
  // runtime_tool_window_interaction_state_contract:
  // Host refreshはouter shellを再navigateせず、revision pollをwakeしてiframe contentだけ更新する。
  window.__tvairRefreshWindowState = () => scheduleWindowStatePoll(true);
  async function pollWindowState() {
    if (pollInFlight || refreshing || !frame || document.hidden) {
      scheduleWindowStatePoll(false);
      return;
    }
    pollInFlight = true;
    try {
      const res = await fetch(stateUrl, { cache: 'no-store' });
      if (!res.ok) return;
      const state = await res.json();
      const revision = Number(state.revision || 0);
      if (revision > lastRevision) {
        refreshing = true;
        const preserveScroll = state.preserveScroll !== false;
        let scrollX = 0;
        let scrollY = 0;
        if (preserveScroll) {
          try {
            scrollX = frame.contentWindow?.scrollX || 0;
            scrollY = frame.contentWindow?.scrollY || 0;
          } catch {}
        }
        lastRevision = revision;
        const next = state.contentRoute || frame.getAttribute('src') || '';
        const url = new URL(next, window.location.origin);
        url.searchParams.set('__tvairWindowId', state.windowId || '{{encodedWindowId}}');
        url.searchParams.set('__tvairHostWindow', '1');
        url.searchParams.set('_tvairWindowRevision', String(revision));
        if (preserveScroll) {
          url.searchParams.set('__tvairPreserveScroll', '1');
          url.searchParams.set('__tvairScrollX', String(scrollX));
          url.searchParams.set('__tvairScrollY', String(scrollY));
        }
        const completeRefresh = () => {
          if (preserveScroll) {
            try { frame.contentWindow?.scrollTo(scrollX, scrollY); } catch {}
          }
          refreshing = false;
          scheduleWindowStatePoll(false);
        };
        frame.addEventListener('load', completeRefresh, { once: true });
        frame.setAttribute('src', url.pathname + url.search + url.hash);
        return;
      }
    } catch { }
    finally {
      pollInFlight = false;
      if (!refreshing) scheduleWindowStatePoll(false);
    }
  }
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) window.clearTimeout(pollTimer);
    else scheduleWindowStatePoll(true);
  });
  window.addEventListener('focus', () => scheduleWindowStatePoll(true));
  scheduleWindowStatePoll(true);
})();
</script>
</body>
</html>
""";
    log.Add("PLUGIN_WINDOW", session.PluginName, $"action=render result=OK windowId={SafePluginActionValue(session.WindowId)} routeSegment={SafePluginActionValue(session.RouteSegment)} revision={session.Revision} toolHost={toolHost} contentRoute={SafePluginActionValue(BuildHostManagedPluginContentRoute(contentRoute, session.WindowId, session.Revision))} shellMode=toolWindowContentOnly rule=release_contract");
    return Results.Content(html, "text/html; charset=utf-8");
}


static string NormalizePluginFormResponseMode(string? value)
{
    var mode = SplitPluginWindowRawValues(value).FirstOrDefault() ?? string.Empty;
    if (mode.Equals("redirect", StringComparison.OrdinalIgnoreCase)) return "redirect";
    if (mode.Equals("redirectBack", StringComparison.OrdinalIgnoreCase) || mode.Equals("redirect-back", StringComparison.OrdinalIgnoreCase)) return "redirectBack";
    if (mode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase) || mode.Equals("host-handled", StringComparison.OrdinalIgnoreCase)) return "hostHandled";
    if (mode.Equals("html", StringComparison.OrdinalIgnoreCase)) return "html";
    if (mode.Equals("noContent", StringComparison.OrdinalIgnoreCase) || mode.Equals("nocontent", StringComparison.OrdinalIgnoreCase)) return "noContent";
    if (mode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase) || mode.Equals("hostWindow", StringComparison.OrdinalIgnoreCase) || mode.Equals("toolWindowRedirectBack", StringComparison.OrdinalIgnoreCase)) return "toolWindow";
    if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "toolWindow";
    if (mode.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase) || mode.Equals("refresh", StringComparison.OrdinalIgnoreCase)) return "refreshWindow";
    if (mode.Equals("patchWindow", StringComparison.OrdinalIgnoreCase) || mode.Equals("patch", StringComparison.OrdinalIgnoreCase)) return "patchWindow";
    return "json";
}

static bool IsPluginWindowHostOpenMode(string responseMode)
    => responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase)
    || responseMode.Equals("redirectBack", StringComparison.OrdinalIgnoreCase)
    || responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase)
    || responseMode.Equals("redirect", StringComparison.OrdinalIgnoreCase);

static string NormalizePluginWindowActionResponseMode(HttpRequest http, PluginWindowRequest request, string action, string responseMode)
{
    if (action.Equals("openWindow", StringComparison.OrdinalIgnoreCase) || action.Equals("open", StringComparison.OrdinalIgnoreCase)) return responseMode;
    if (!responseMode.Equals("json", StringComparison.OrdinalIgnoreCase)) return responseMode;

    var contentType = http.ContentType ?? string.Empty;
    var isBrowserForm = contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase);
    var hasWindowContext = !string.IsNullOrWhiteSpace(request.WindowId)
        || !string.IsNullOrWhiteSpace(ReadPayload(request.Payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "safeEventWindowId", "SafeEventWindowId", "currentWindowId", "CurrentWindowId", "windowId", "WindowId"))
        || http.Headers.Referer.ToString().Contains("__tvairWindowId=", StringComparison.OrdinalIgnoreCase)
        || http.Headers.Referer.ToString().Contains("__tvairHostWindow=1", StringComparison.OrdinalIgnoreCase);

    return isBrowserForm && hasWindowContext ? "hostHandled" : responseMode;
}

static IResult PluginSeeOther(string location)
    => new PluginSeeOtherResult(string.IsNullOrWhiteSpace(location) ? "/" : location);

static string BuildAbsoluteLocalUrl(HttpRequest http, string relativeOrAbsolute)
{
    var target = string.IsNullOrWhiteSpace(relativeOrAbsolute) ? "/" : relativeOrAbsolute.Trim();
    if (Uri.TryCreate(target, UriKind.Absolute, out _)) return target;
    if (!target.StartsWith('/')) target = "/" + target;
    var scheme = string.IsNullOrWhiteSpace(http.Scheme) ? "http" : http.Scheme;
    var host = http.Host.HasValue ? http.Host.Value : "localhost";
    return $"{scheme}://{host}{target}";
}

static string BuildToolWindowHostUrl(string windowUrl)
{
    if (string.IsNullOrWhiteSpace(windowUrl)) return "/plugin-window?__tvairToolHost=1";
    var sep = windowUrl.Contains("?", StringComparison.Ordinal) ? "&" : "?";
    return windowUrl + sep + "__tvairToolHost=1";
}

static string BuildToolWindowNavigationUrl(string windowUrl, string contentRoute, TvAIr.Plugin.PluginToolWindowHostCapabilities hostCaps)
{
    // release_contract: WinForms WebBrowser fallback can render the shell itself but may fail to display the iframe content reliably.
    // In fallback mode, navigate the native ToolWindow directly to the plugin content route with the host-window context.
    // WebView2-capable environments keep the shell+iframe route so refresh polling remains available.
    if (hostCaps is not null && !hostCaps.WebView2RuntimeAvailable)
    {
        return AddToolWindowDirectContentQuery(contentRoute);
    }
    return BuildToolWindowHostUrl(windowUrl);
}

static bool IsToolWindowDirectContentNavigation(string? url)
    => (url ?? string.Empty).Contains("__tvairToolHostContent=1", StringComparison.OrdinalIgnoreCase);

static string AddToolWindowDirectContentQuery(string? route)
{
    var target = string.IsNullOrWhiteSpace(route) ? "/plugin" : route.Trim();
    var hashIndex = target.IndexOf('#');
    var fragment = hashIndex >= 0 ? target[hashIndex..] : string.Empty;
    if (hashIndex >= 0) target = target[..hashIndex];
    var sep = target.Contains("?", StringComparison.Ordinal) ? "&" : "?";
    return target + sep + "__tvairToolHost=1&__tvairToolHostContent=1" + fragment;
}

static string ResolvePluginToolWindowReturnUrl(HttpRequest http, PluginWindowRequest request, string? route)
{
    var explicitReturn = !string.IsNullOrWhiteSpace(request.ReturnUrl) ? request.ReturnUrl : ReadPayload(request.Payload, "returnUrl", "ReturnUrl");
    var normalizedExplicit = NormalizeLocalPluginReturnUrl(http, explicitReturn);
    if (!string.IsNullOrWhiteSpace(normalizedExplicit)) return normalizedExplicit;

    var referrer = http.Headers.Referer.ToString();
    var normalizedReferrer = NormalizeLocalPluginReturnUrl(http, referrer);
    if (!string.IsNullOrWhiteSpace(normalizedReferrer)
        && !normalizedReferrer.StartsWith("/api/plugins/window", StringComparison.OrdinalIgnoreCase)
        && !normalizedReferrer.StartsWith("/plugin-window/", StringComparison.OrdinalIgnoreCase))
    {
        return normalizedReferrer;
    }

    var safeRoute = (route ?? string.Empty).Trim().Trim('/');
    if (!string.IsNullOrWhiteSpace(safeRoute)) return $"/plugin/{Uri.EscapeDataString(safeRoute)}";
    return "/";
}

static string NormalizeLocalPluginReturnUrl(HttpRequest http, string? value)
{
    var text = (value ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(text)) return string.Empty;
    if (text.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0) return string.Empty;
    if (text.StartsWith("//", StringComparison.Ordinal)) return string.Empty;
    if (text.StartsWith("/\\", StringComparison.Ordinal)) return string.Empty;
    if (text.StartsWith("/", StringComparison.Ordinal)) return text;
    if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
    {
        var host = http.Host.HasValue ? http.Host.Value : string.Empty;
        if (!string.IsNullOrWhiteSpace(host) && absolute.Host.Equals(http.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            var portMatches = !http.Host.Port.HasValue || absolute.Port == http.Host.Port.Value;
            if (portMatches)
            {
                var path = string.IsNullOrWhiteSpace(absolute.PathAndQuery) ? "/" : absolute.PathAndQuery;
                return path + absolute.Fragment;
            }
        }
    }
    return string.Empty;
}

static bool IsTrayPluginMenuSource(string? source)
    => string.Equals((source ?? string.Empty).Trim(), "tray", StringComparison.OrdinalIgnoreCase)
       || string.Equals((source ?? string.Empty).Trim(), "tasktray", StringComparison.OrdinalIgnoreCase)
       || string.Equals((source ?? string.Empty).Trim(), "taskbar", StringComparison.OrdinalIgnoreCase);

static IResult DispatchPluginDefaultMenuAction(string routeSegment, string source, HttpRequest http, PluginRegistry registry, PluginDefaultMenuActionService menuActions, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, PluginBoundaryGate boundaryGate, LogRepository log)
{
    var isTraySource = IsTrayPluginMenuSource(source);
    var actionInfo = menuActions.ResolveActionByRoute(routeSegment);
    if (actionInfo is null)
    {
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", "DENY", $"result=NOT_FOUND route={SafePluginActionValue(routeSegment)} source={SafePluginActionValue(source)} rule=release_contract");
        return PluginHtmlMessage("プラグイン操作", "指定されたプラグイン既定アクションが見つかりません。", StatusCodes.Status404NotFound, "/", "番組表へ戻る");
    }

    var route = actionInfo.RouteSegment;
    var plugin = FindPluginForDefaultMenuAction(registry, actionInfo.PluginId, route);
    if (plugin is null)
    {
        var missingKind = (actionInfo.Kind ?? string.Empty).Trim();
        if (missingKind.Equals(PluginMenuActionKinds.StatusDialog, StringComparison.OrdinalIgnoreCase)
            && string.Equals((actionInfo.Source ?? string.Empty).Trim(), "capability.statusDialog", StringComparison.OrdinalIgnoreCase))
        {
            var returnUrl = ResolvePluginMenuReturnUrl(http);
            log.Add("PLUGIN_MENU_ACTION_DISPATCH", actionInfo.Name, $"result=STATUS_INFO kind={SafePluginActionValue(missingKind)} source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} sourceKind={SafePluginActionValue(actionInfo.Source)} browserNavigation={(isTraySource ? "none" : "status_dialog_fallback")} mainBrowserOpened=False programGuideOpened=False redirectBack={!isTraySource} returnUrl={(isTraySource ? "-" : SafePluginActionValue(returnUrl))} rule=release_contract");
            return isTraySource ? Results.NoContent() : RenderPluginVersionInfoPage(actionInfo, returnUrl);
        }

        log.Add("PLUGIN_MENU_ACTION_DISPATCH", actionInfo.Name, $"result=PLUGIN_NOT_FOUND kind={SafePluginActionValue(actionInfo.Kind)} route={SafePluginActionValue(route)} source={SafePluginActionValue(source)} rule=release_contract");
        return PluginHtmlMessage("プラグイン操作", "対象プラグインが読み込まれていません。", StatusCodes.Status404NotFound, "/", "番組表へ戻る");
    }

    if (actionInfo.Kind.Equals(PluginMenuActionKinds.ToolWindow, StringComparison.OrdinalIgnoreCase))
    {
        var menuWindowBoundary = boundaryGate.CheckWindow(ResolveRuntimeBoundaryPlugin(registry, plugin), "openWindow", http.Path.Value ?? string.Empty);
        if (!menuWindowBoundary.Allowed)
        {
            log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=DENIED kind=toolWindow reason={SafePluginActionValue(menuWindowBoundary.Reason)} route={SafePluginActionValue(route)} source={SafePluginActionValue(source)} rule=plugin_boundary_gate");
            return PluginHtmlMessage("プラグイン操作", "このプラグインの画面を操作できませんでした。", StatusCodes.Status403Forbidden, "/", "番組表へ戻る");
        }

        var runtimeMenuPlugin = ResolveRuntimeBoundaryPlugin(registry, plugin);
        if (runtimeMenuPlugin is not ITvAirRuntimeUiPlugin)
        {
            log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=DENIED kind=toolWindow reason=runtime_ui_not_implemented route={SafePluginActionValue(route)} source={SafePluginActionValue(source)} rule=runtime_ui_contract");
            return PluginHtmlMessage("プラグイン操作", "このプラグインには開ける画面がありません。", StatusCodes.Status400BadRequest, "/", "番組表へ戻る");
        }

        var pluginActionId = NormalizePluginActionId(runtimeMenuPlugin.Descriptor.PluginId);
        var request = new PluginWindowRequest
        {
            Action = "openWindow",
            PluginId = pluginActionId,
            RouteSegment = route,
            Title = string.IsNullOrWhiteSpace(actionInfo.Label) ? plugin.Descriptor.DisplayName : actionInfo.Label,
            Width = 0,
            Height = 0,
            MinWidth = 0,
            MinHeight = 0,
            Resizable = true,
            Movable = true,
            ContentRoute = $"/plugin/{Uri.EscapeDataString(route)}",
            ReuseExisting = true,
            ActivateExisting = true,
            ResponseMode = "hostHandled",
            Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = source,
                ["defaultMenuAction"] = "true",
                ["showInTaskbar"] = actionInfo.ShowInTaskbar ? "true" : "false",
                ["runtimeActionId"] = actionInfo.ActionId,
                ["windowDefinitionId"] = actionInfo.WindowDefinitionId,
                ["surfaceDefinitionId"] = actionInfo.SurfaceDefinitionId
            }
        };

        var unifiedOpen = OpenOrActivatePluginToolWindowUnified(runtimeMenuPlugin, pluginActionId, route, request, source, "default_menu_toolwindow", http, windows, toolWindows, log);
        var session = unifiedOpen.Session;
        var reusedWindowSession = unifiedOpen.ReusedSession;
        var hostResult = unifiedOpen.HostResult;

        var returnUrl = ResolvePluginMenuReturnUrl(http);
        var browserNavigation = isTraySource ? "none" : "redirectBack";
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=OK kind=toolWindow source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} windowId={SafePluginActionValue(session.WindowId)} reusedSession={reusedWindowSession} hostResult={SafePluginActionValue(hostResult.Result)} hostReused={hostResult.Reused} activated={hostResult.Activated} size={session.Width}x{session.Height} minSize={session.MinWidth}x{session.MinHeight} showInTaskbar={actionInfo.ShowInTaskbar} browserNavigation={browserNavigation} mainBrowserOpened=False programGuideOpened=False redirectBack={!isTraySource} returnUrl={(isTraySource ? "-" : SafePluginActionValue(returnUrl))} rule=release_contract");
        log.Add("PLUGIN_TOOL_WINDOW_ACTIVATE", plugin.Descriptor.DisplayName, $"result={SafePluginActionValue(hostResult.Result)} source={SafePluginActionValue(source)} windowId={SafePluginActionValue(session.WindowId)} reused={hostResult.Reused} activated={hostResult.Activated} showInTaskbar={actionInfo.ShowInTaskbar} reason=default_menu_action browserNavigationSuppressed=True rule=release_contract");
        return isTraySource ? Results.NoContent() : PluginSeeOther(returnUrl);
    }

    var kind = (actionInfo.Kind ?? string.Empty).Trim();
    var returnUrlForLog = ResolvePluginMenuReturnUrl(http);

    if (kind.Equals(PluginMenuActionKinds.Page, StringComparison.OrdinalIgnoreCase))
    {
        var menuPageBoundary = boundaryGate.CheckRender(ResolveRuntimeBoundaryPlugin(registry, plugin), hostManagedToolWindowContent: false, http.Path.Value ?? string.Empty);
        if (!menuPageBoundary.Allowed)
        {
            log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=DENIED kind=page reason={SafePluginActionValue(menuPageBoundary.Reason)} route={SafePluginActionValue(route)} source={SafePluginActionValue(source)} rule=plugin_boundary_gate");
            return PluginHtmlMessage("プラグイン操作", "このプラグインの画面を開けませんでした。", StatusCodes.Status403Forbidden, "/", "番組表へ戻る");
        }
        var target = $"/plugin/{Uri.EscapeDataString(route)}";
        // Page actions are browser navigation actions regardless of the menu entry point.
        // Tray already opens this dispatch URL in the user's default browser; returning 204 for
        // source=tray would strand that browser request on the dispatcher instead of the plugin page.
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=REDIRECT kind=page source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} target={SafePluginActionValue(target)} browserNavigation=plugin_page mainBrowserOpened=True programGuideOpened=False redirectBack=False returnUrl={SafePluginActionValue(returnUrlForLog)} rule=release_contract");
        return PluginSeeOther(target);
    }

    if (kind.Equals(PluginMenuActionKinds.Settings, StringComparison.OrdinalIgnoreCase))
    {
        var menuSettingsBoundary = boundaryGate.CheckRender(ResolveRuntimeBoundaryPlugin(registry, plugin), hostManagedToolWindowContent: false, http.Path.Value ?? string.Empty);
        if (!menuSettingsBoundary.Allowed)
        {
            log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=DENIED kind=settings reason={SafePluginActionValue(menuSettingsBoundary.Reason)} route={SafePluginActionValue(route)} source={SafePluginActionValue(source)} rule=plugin_boundary_gate");
            return PluginHtmlMessage("プラグイン操作", "このプラグインの設定画面を開けませんでした。", StatusCodes.Status403Forbidden, "/", "番組表へ戻る");
        }
        var target = $"/plugin/{Uri.EscapeDataString(route)}?mode=settings";
        // Settings actions are browser navigation actions for the same reason as page actions.
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=REDIRECT kind=settings source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} target={SafePluginActionValue(target)} browserNavigation=plugin_settings mainBrowserOpened=True programGuideOpened=False redirectBack=False returnUrl={SafePluginActionValue(returnUrlForLog)} rule=release_contract");
        return PluginSeeOther(target);
    }

    if (kind.Equals(PluginMenuActionKinds.VersionDialog, StringComparison.OrdinalIgnoreCase) || kind.Equals(PluginMenuActionKinds.StatusDialog, StringComparison.OrdinalIgnoreCase))
    {
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=VERSION_INFO kind=info source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} version={SafePluginActionValue(actionInfo.Version)} browserNavigation={(isTraySource ? "none" : "version_dialog_fallback")} mainBrowserOpened=False programGuideOpened=False redirectBack={!isTraySource} returnUrl={(isTraySource ? "-" : SafePluginActionValue(returnUrlForLog))} rule=release_contract");
        return isTraySource ? Results.NoContent() : RenderPluginVersionInfoPage(actionInfo, returnUrlForLog);
    }

    log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Descriptor.DisplayName, $"result=DENIED kind={SafePluginActionValue(kind)} reason=unsupported_action_kind source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} rule=release_contract");
    return PluginHtmlMessage("プラグイン操作", "このプラグインの既定アクション種別はTvAIr本体で実行できません。", StatusCodes.Status400BadRequest, ResolvePluginMenuReturnUrl(http), "戻る");
}

static ITvAirRuntimeCapabilityPlugin? FindPluginForDefaultMenuAction(PluginRegistry registry, string pluginId, string route)
    => FindPluginByActionIdentity(registry, pluginId, route);

static string ResolvePluginMenuReturnUrl(HttpRequest http)
{
    var explicitReturn = ReadPluginReturnQuery(http, "returnUrl", "ReturnUrl");
    var normalizedExplicit = NormalizeLocalPluginReturnUrl(http, explicitReturn);
    if (IsAllowedPluginMenuReturnUrl(normalizedExplicit)) return normalizedExplicit;

    var referrer = NormalizeLocalPluginReturnUrl(http, http.Headers.Referer.ToString());
    if (IsAllowedPluginMenuReturnUrl(referrer)) return referrer;
    return "/";
}

static string ReadPluginReturnQuery(HttpRequest http, params string[] keys)
{
    foreach (var key in keys)
    {
        if (http.Query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value.ToString())) return value.ToString();
    }
    return string.Empty;
}

static bool IsAllowedPluginMenuReturnUrl(string? value)
{
    var url = (value ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(url)) return false;
    if (!url.StartsWith("/", StringComparison.Ordinal) || url.StartsWith("//", StringComparison.Ordinal)) return false;
    if (url.StartsWith("/plugin-menu/", StringComparison.OrdinalIgnoreCase)) return false;
    if (url.StartsWith("/plugin-menu-info/", StringComparison.OrdinalIgnoreCase)) return false;
    if (url.StartsWith("/plugin-window/", StringComparison.OrdinalIgnoreCase)) return false;
    if (url.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return false;
    return true;
}


static IResult RenderPluginVersionInfoPage(PluginDefaultMenuActionInfo actionInfo, string? returnUrl)
{
    var safeTitle = HtmlEncoder.Default.Encode(actionInfo.Name);
    var statusText = string.IsNullOrWhiteSpace(actionInfo.Version) ? actionInfo.Description : $"バージョン: {actionInfo.Version}";
    if (string.IsNullOrWhiteSpace(statusText)) statusText = "バージョン: 不明";
    var safeVersion = HtmlEncoder.Default.Encode(statusText);
    var safeReturn = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    var html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{safeTitle}}</title>
<link rel="stylesheet" href="/tvair-notification.css?v=1.2.0-owner203">
</head>
<body>
<script src="/tvair-notification.js?v=1.2.0"></script>
<script>
document.addEventListener('DOMContentLoaded',function(){
  if(window.TvAIrNotify){ TvAIrNotify({ title:'{{safeTitle}}', message:'{{safeVersion}}', onOk:function(){ location.replace('{{safeReturn}}'); } }); }
  else { alert('{{safeTitle}}\n{{safeVersion}}'); location.replace('{{safeReturn}}'); }
});
</script>
</body>
</html>
""";
    return Results.Content(html, "text/html; charset=utf-8");
}

static IResult RenderPluginDefaultMenuInfoByRoute(string routeSegment, PluginRegistry registry, PluginDefaultMenuActionService menuActions, LogRepository log)
{
    var actionInfo = menuActions.ResolveActionByRoute(routeSegment);
    if (actionInfo is null)
    {
        log.Add("PLUGIN_MENU_INFO_RENDER", "DENY", $"result=NOT_FOUND route={SafePluginActionValue(routeSegment)} rule=release_contract");
        return PluginHtmlMessage("プラグイン情報", "指定されたプラグイン情報が見つかりません。", StatusCodes.Status404NotFound, "/", "番組表へ戻る");
    }

    var plugin = FindPluginForDefaultMenuAction(registry, actionInfo.PluginId, actionInfo.RouteSegment);
    if (plugin is null)
    {
        if (actionInfo.Kind.Equals(PluginMenuActionKinds.StatusDialog, StringComparison.OrdinalIgnoreCase)
            && string.Equals((actionInfo.Source ?? string.Empty).Trim(), "capability.statusDialog", StringComparison.OrdinalIgnoreCase))
        {
            log.Add("PLUGIN_MENU_INFO_RENDER", actionInfo.Name, $"result=OK_STATUS_ONLY route={SafePluginActionValue(actionInfo.RouteSegment)} version={SafePluginActionValue(actionInfo.Version)} source={SafePluginActionValue(actionInfo.Source)} rule=release_contract");
            return RenderPluginVersionInfoPage(actionInfo, "/");
        }

        log.Add("PLUGIN_MENU_INFO_RENDER", actionInfo.Name, $"result=PLUGIN_NOT_FOUND route={SafePluginActionValue(actionInfo.RouteSegment)} rule=release_contract");
        return PluginHtmlMessage("プラグイン情報", "対象プラグインが読み込まれていません。", StatusCodes.Status404NotFound, "/", "番組表へ戻る");
    }

    log.Add("PLUGIN_MENU_INFO_RENDER", actionInfo.Name, $"result=OK route={SafePluginActionValue(actionInfo.RouteSegment)} version={SafePluginActionValue(actionInfo.Version)} rule=release_contract");
    return RenderPluginDefaultMenuInfo(actionInfo, plugin);
}

static IResult RenderPluginDefaultMenuInfo(PluginDefaultMenuActionInfo actionInfo, ITvAirRuntimeCapabilityPlugin plugin)
{
    var safeName = HtmlEncoder.Default.Encode(actionInfo.Name);
    var safeVersion = HtmlEncoder.Default.Encode(actionInfo.Version);
    var safeRoute = HtmlEncoder.Default.Encode(actionInfo.RouteSegment);
    var safeKind = HtmlEncoder.Default.Encode(actionInfo.Kind);
    var safeDescription = HtmlEncoder.Default.Encode(actionInfo.Description ?? string.Empty);
    var html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{safeName}} 情報</title>
<link rel="stylesheet" href="/tvair-generated-surfaces.css?v=1.2.0-css-final199">
</head>
<body class="tvair-plugin-info-page"><div class="card"><h1>{{safeName}} 情報</h1><div class="row"><div class="k">Version</div><div class="v">{{safeVersion}}</div></div><div class="row"><div class="k">Route</div><div class="v">{{safeRoute}}</div></div><div class="row"><div class="k">Action</div><div class="v">{{safeKind}}</div></div><p>{{safeDescription}}</p><a class="button" href="/">番組表へ戻る</a></div></body>
</html>
""";
    return Results.Content(html, "text/html; charset=utf-8");
}

static IResult PluginHtmlMessage(string title, string message, int statusCode, string? href = null, string? linkText = null)
{
    var safeTitle = HtmlEncoder.Default.Encode(title);
    var safeMessage = HtmlEncoder.Default.Encode(message);
    var safeHref = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(href) ? "/" : href);
    var safeLinkText = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(linkText) ? "戻る" : linkText);
    var html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{safeTitle}}</title>
<link rel="stylesheet" href="/tvair-generated-surfaces.css?v=1.2.0-css-final199">
</head>
<body class="tvair-message-page"><div class="card"><h1>{{safeTitle}}</h1><p>{{safeMessage}}</p><a class="button" href="{{safeHref}}">{{safeLinkText}}</a></div></body>
</html>
""";
    return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
}

static IResult BuildPluginWindowDispatchResponse(PluginWindowResult result, string responseMode, string redirectUrl)
{
    if (responseMode.Equals("redirect", StringComparison.OrdinalIgnoreCase)) return PluginSeeOther(redirectUrl);
    if (responseMode.Equals("redirectBack", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase)) return Results.NoContent();
    if (responseMode.Equals("html", StringComparison.OrdinalIgnoreCase)) return PluginHtmlMessage("プラグイン画面", result.Message, StatusCodes.Status200OK, result.WindowUrl, "プラグイン画面を開く");
    if (responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase)) return Results.NoContent();
    return Results.Ok(result);
}

static IResult BuildPluginWindowDispatchError(PluginWindowResult result, string responseMode, int statusCode)
{
    if (responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("redirectBack", StringComparison.OrdinalIgnoreCase))
        return Results.NoContent();
    if (!responseMode.Equals("json", StringComparison.OrdinalIgnoreCase))
        return PluginHtmlMessage("プラグイン画面エラー", "プラグイン画面を開けませんでした。", statusCode, "/", "番組表へ戻る");
    return Results.Json(result, statusCode: statusCode);
}

static IReadOnlyList<RuntimeUiPatch> NormalizeRuntimeUiPatches(IReadOnlyList<RuntimeUiPatch>? patches)
    => RuntimeUiPatchContract.Normalize(patches);

static string BuildPluginFloatingButtonsHtml(RuntimeUiRenderContext context, string pluginName, LogRepository log)
{
    if (context.FloatingButtons.Count == 0) return string.Empty;
    static string Enc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    static string NormalizeId(string? value)
    {
        var id = (value ?? string.Empty).Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9_\\-:.]{0,127}$") ? id : string.Empty;
    }

    var normalized = context.FloatingButtons
        .Where(x => x is not null && x.ActionAvailable)
        .Select(x => new
        {
            Item = x,
            Id = NormalizeId(x.Id),
            Label = (x.Label ?? string.Empty).Trim(),
            Tooltip = (x.Tooltip ?? string.Empty).Trim(),
            Icon = (x.Icon ?? string.Empty).Trim()
        })
        .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Label))
        .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .OrderBy(x => x.Item.Position)
        .ThenBy(x => x.Item.Priority)
        .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .Take(16)
        .ToArray();
    if (normalized.Length == 0) return string.Empty;

    var sb = new System.Text.StringBuilder();
    sb.Append("<link rel=\"stylesheet\" href=\"/tvair-generated-surfaces.css?v=1.2.0-css-final199\">");

    foreach (var group in normalized.GroupBy(x => x.Item.Position))
    {
        var cls = group.Key switch
        {
            PluginFloatingButtonPosition.BottomLeft => "bl",
            PluginFloatingButtonPosition.TopRight => "tr",
            PluginFloatingButtonPosition.TopLeft => "tl",
            _ => "br"
        };
        sb.Append("<div class=\"tvair-floating-buttons ").Append(cls).Append("\" data-tvair-floating-position=\"").Append(cls).Append("\">");
        foreach (var x in group)
        {
            var feedback = x.Item.Feedback;
            var attrs = feedback is null
                ? context.BuildPluginActionAttributes(x.Item.Payload, responseMode: x.Item.ResponseMode)
                : context.BuildPluginActionAttributes(x.Item.Payload, feedback, responseMode: x.Item.ResponseMode);
            var threshold = Math.Clamp(x.Item.ScrollThresholdPixels, 0, 10000);
            var tooltip = string.IsNullOrWhiteSpace(x.Tooltip) ? x.Label : x.Tooltip;
            sb.Append("<button type=\"button\" class=\"tvair-floating-button\" id=\"").Append(Enc(x.Id)).Append("\" title=\"").Append(Enc(tooltip)).Append("\" aria-label=\"").Append(Enc(tooltip)).Append("\" ")
              .Append(attrs)
              .Append(" data-tvair-floating-visibility=\"").Append(x.Item.Visibility.ToString()).Append("\" data-tvair-floating-scroll-threshold=\"").Append(threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\" data-tvair-floating-hide-running=\"").Append(x.Item.HideWhileRunning ? "true" : "false").Append("\"");
            if (x.Item.Visibility == PluginFloatingButtonVisibility.ActionAvailable && !x.Item.ActionAvailable) sb.Append(" hidden");
            sb.Append(">");
            if (!string.IsNullOrWhiteSpace(x.Icon)) sb.Append("<span aria-hidden=\"true\">").Append(Enc(x.Icon.Length > 8 ? x.Icon[..8] : x.Icon)).Append("</span> ");
            sb.Append("<span class=\"tvair-floating-label-text\">").Append(Enc(x.Label.Length > 80 ? x.Label[..80] : x.Label)).Append("</span></button>");
        }
        sb.Append("</div>");
    }
    sb.Append("<script>(function(){function u(){var a=document.querySelectorAll?document.querySelectorAll('[data-tvair-floating-visibility=AfterScroll]'):[];var y=window.pageYOffset||document.documentElement.scrollTop||document.body.scrollTop||0;for(var i=0;i<a.length;i++){var t=parseInt(a[i].getAttribute('data-tvair-floating-scroll-threshold')||'240',10);a[i].hidden=y<t;}}if(window.addEventListener){window.addEventListener('scroll',u,false);window.addEventListener('resize',u,false);}else if(window.attachEvent){window.attachEvent('onscroll',u);window.attachEvent('onresize',u);}u();})();</script>");
    log.Add("PLUGIN_FLOATING_BUTTON_CONTRACT", pluginName, $"result=ISSUED declared={context.FloatingButtons.Count} rendered={normalized.Length} positions={SafePluginActionValue(string.Join(",", normalized.Select(x => x.Item.Position).Distinct()))} route=pluginOwnedAction safeEvent=True rule=floating_button_contract");
    return sb.ToString();
}

static string NormalizePluginUserFeedbackMessage(string? message, bool success)
{
    var value = (message ?? string.Empty).Trim();
    if (success) return value;
    if (string.IsNullOrWhiteSpace(value)) return "処理できませんでした";

    // 利用者向け面へSQLパラメータ名・例外型・スタック/パスなどの内部診断を露出させない。
    // 詳細はサーバーログに保持し、Plugin Action Feedbackは安全な短文へ正規化する。
    var looksInternal = value.Contains("parameter", StringComparison.OrdinalIgnoreCase)
        || value.Contains("exception", StringComparison.OrdinalIgnoreCase)
        || value.Contains("stack", StringComparison.OrdinalIgnoreCase)
        || value.Contains(" at ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("$", StringComparison.Ordinal)
        || value.Contains("\\", StringComparison.Ordinal)
        || value.Contains("/", StringComparison.Ordinal);
    return looksInternal ? "処理できませんでした" : value;
}

static PluginFloatingLabel? NormalizePluginFloatingLabel(PluginFloatingLabel? label, PluginActionFeedback? feedback, string correlationId)
{
    if (label is null && (feedback is null || !feedback.ShowFloatingLabel || string.IsNullOrWhiteSpace(feedback.Message))) return null;
    label ??= new PluginFloatingLabel
    {
        CorrelationId = feedback?.CorrelationId ?? correlationId,
        Message = feedback?.Message ?? string.Empty,
        Kind = feedback?.Kind ?? PluginActionFeedbackKind.Information,
        Position = PluginFloatingLabelPosition.ContentCenter
    };
    var message = (label.Message ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(message)) return null;
    var duration = label.DurationMilliseconds;
    if (duration <= 0)
    {
        duration = label.Kind switch
        {
            PluginActionFeedbackKind.Warning => 2500,
            PluginActionFeedbackKind.Error => 3000,
            _ => 1800
        };
    }
    duration = Math.Clamp(duration, 1000, 5000);
    return new PluginFloatingLabel
    {
        CorrelationId = string.IsNullOrWhiteSpace(label.CorrelationId) ? correlationId : label.CorrelationId.Trim(),
        Message = message.Length > 240 ? message[..240] : message,
        Kind = label.Kind,
        Position = label.Position,
        DurationMilliseconds = duration
    };
}

static IResult BuildPluginActionDispatchError(RuntimeUiActionResult result, string responseMode, string windowId, int statusCode, bool feedbackRequested = false, string correlationId = "")
{
    if (feedbackRequested)
    {
        result.Feedback ??= new PluginActionFeedback
        {
            CorrelationId = correlationId,
            Phase = PluginActionFeedbackPhase.Failed,
            Kind = PluginActionFeedbackKind.Error,
            Message = NormalizePluginUserFeedbackMessage(result.Message, success: false)
        };
        result.Feedback.CorrelationId = correlationId;
        result.FloatingLabel = NormalizePluginFloatingLabel(result.FloatingLabel, result.Feedback, correlationId);
        return Results.Json(result, statusCode: statusCode);
    }
    // plugin_action_error_status_contract: hostHandled/noContent/toolWindow are success response modes,
    // not permission to erase a Host-side DENY/ERROR into HTTP 204. The safe-event client uses
    // the HTTP status as its authoritative success bit, so preserve the real failure status.
    if (responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase))
        return Results.Json(result, statusCode: statusCode);

    if (!responseMode.Equals("json", StringComparison.OrdinalIgnoreCase)
        && !responseMode.Equals("patchWindow", StringComparison.OrdinalIgnoreCase))
    {
        var href = string.IsNullOrWhiteSpace(windowId) ? "/" : $"/plugin-window/{Uri.EscapeDataString(windowId)}";
        return PluginHtmlMessage("プラグイン操作エラー", "プラグインの操作を完了できませんでした。", statusCode, href, "戻る");
    }
    return Results.Json(result, statusCode: statusCode);
}

static string BuildRuntimePageRefreshNavigationTarget(HttpRequest http, string requestedContentRoute)
{
    var referer = http.Headers["Referer"].FirstOrDefault();
    var targetPath = string.Empty;
    var targetQuery = string.Empty;
    if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri)
        && string.Equals(refererUri.Authority, http.Host.Value, StringComparison.OrdinalIgnoreCase)
        && refererUri.AbsolutePath.StartsWith("/plugin/", StringComparison.OrdinalIgnoreCase))
    {
        targetPath = refererUri.AbsolutePath;
        targetQuery = refererUri.Query;
    }
    else if (!string.IsNullOrWhiteSpace(requestedContentRoute)
        && Uri.TryCreate(requestedContentRoute.Trim(), UriKind.Relative, out _)
        && requestedContentRoute.Trim().StartsWith("/plugin/", StringComparison.OrdinalIgnoreCase))
    {
        var raw = requestedContentRoute.Trim();
        var separator = raw.IndexOf('?');
        targetPath = separator >= 0 ? raw[..separator] : raw;
        targetQuery = separator >= 0 ? raw[separator..] : string.Empty;
    }
    if (string.IsNullOrWhiteSpace(targetPath))
        return "/";

    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(targetQuery);
    var kept = query
        .Where(pair => !string.Equals(pair.Key, "_tvairPageRefresh", StringComparison.OrdinalIgnoreCase))
        .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))
        .ToList();
    kept.Add(new KeyValuePair<string, string?>("_tvairPageRefresh", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
    return targetPath + (Microsoft.AspNetCore.Http.QueryString.Create(kept).Value ?? string.Empty);
}

static IResult BuildPluginOwnedActionResponse(RuntimeUiActionResult result, string responseMode, string windowId, string refreshTarget, bool preserveScroll, PluginWindowSessionStore windows, string pluginId, LogRepository log, string pluginName, string action, TvAIrPlugin.Runtime.RuntimeUiKind? runtimeUiKind, bool refreshAfter = false, PluginToolWindowHostService? toolWindows = null, HttpRequest? http = null, string requestedContentRoute = "", bool feedbackRequested = false, string correlationId = "")
{
    if (feedbackRequested)
    {
        result.Feedback ??= new PluginActionFeedback
        {
            Phase = result.Succeeded ? PluginActionFeedbackPhase.Succeeded : PluginActionFeedbackPhase.Failed,
            Kind = result.Succeeded ? PluginActionFeedbackKind.Success : PluginActionFeedbackKind.Error,
            Message = NormalizePluginUserFeedbackMessage(result.Message, result.Succeeded)
        };
        result.Feedback.CorrelationId = correlationId;
        result.FloatingLabel = NormalizePluginFloatingLabel(result.FloatingLabel, result.Feedback, correlationId);
    }
    else if (result.FloatingLabel is not null)
    {
        result.FloatingLabel = NormalizePluginFloatingLabel(result.FloatingLabel, null, correlationId);
    }
    if (responseMode.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase))
    {
        if (runtimeUiKind == TvAIrPlugin.Runtime.RuntimeUiKind.Page)
        {
            var pageRoute = http?.Headers["Referer"].FirstOrDefault();
            var redirectTarget = "/";
            if (http is not null && Uri.TryCreate(pageRoute, UriKind.Absolute, out var refererUri)
                && string.Equals(refererUri.Authority, http.Host.Value, StringComparison.OrdinalIgnoreCase)
                && refererUri.AbsolutePath.StartsWith("/plugin/", StringComparison.OrdinalIgnoreCase))
            {
                redirectTarget = refererUri.PathAndQuery;
            }
            log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} responseMode=refreshWindow result=REDIRECT surface=page windowId=- target=current_page preserveScroll={preserveScroll} rule=runtime_ui_action_result_refresh_contract");
            return PluginSeeOther(redirectTarget);
        }

        var resolvedWindowId = NormalizePluginWindowId(windowId);
        var currentSession = windows.Get(resolvedWindowId);
        var declaredRefreshMode = currentSession?.RefreshMode ?? TvAIrPlugin.Windows.PluginWindowRefreshMode.Navigate;
        if (declaredRefreshMode == TvAIrPlugin.Windows.PluginWindowRefreshMode.None)
        {
            log.Add("PLUGIN_ACTION_RESPONSE_CONTRACT", pluginName, $"action={SafePluginActionValue(action)} result=NO_REFRESH declaredRefreshMode=None windowId={SafePluginActionValue(resolvedWindowId)} rule=runtime_descriptor_refresh_mode_contract");
            return Results.Json(result);
        }
        if (declaredRefreshMode == TvAIrPlugin.Windows.PluginWindowRefreshMode.StatePatch)
        {
            var normalizedPatches = NormalizeRuntimeUiPatches(result.UiPatches);
            result.UiPatches = normalizedPatches;
            log.Add("PLUGIN_ACTION_RESPONSE_CONTRACT", pluginName, $"action={SafePluginActionValue(action)} result=STATE_PATCH declaredRefreshMode=StatePatch windowId={SafePluginActionValue(resolvedWindowId)} patches={normalizedPatches.Count} navigation=False rule=runtime_descriptor_refresh_mode_contract");
            return Results.Json(result);
        }
        var refreshRequest = new PluginWindowRequest
        {
            WindowId = resolvedWindowId,
            RefreshTarget = NormalizePluginWindowRefreshTarget(refreshTarget),
            PreserveScroll = preserveScroll,
            ForceReload = true,
            ContentRoute = string.IsNullOrWhiteSpace(requestedContentRoute) ? string.Empty : requestedContentRoute.Trim()
        };
        var session = windows.Refresh(resolvedWindowId, pluginId, refreshRequest);
        if (session is null)
        {
            log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} responseMode=refreshWindow result=SKIPPED reason=window_already_closed windowId={SafePluginActionValue(resolvedWindowId)} rule=runtime_ui_action_result_refresh_contract");
            return Results.Json(result);
        }

        var contentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} responseMode=refreshWindow result=REDIRECT windowId={SafePluginActionValue(session.WindowId)} target=content preserveScroll={preserveScroll} revision={session.Revision} contentRoute={SafePluginActionValue(contentRoute)} rule=runtime_ui_action_result_refresh_contract");
        return PluginSeeOther(contentRoute);
    }

    if (responseMode.Equals("patchWindow", StringComparison.OrdinalIgnoreCase))
    {
        var normalizedPatches = NormalizeRuntimeUiPatches(result.UiPatches);
        result.UiPatches = normalizedPatches;
        log.Add("PLUGIN_ACTION_RESPONSE_CONTRACT", pluginName, $"action={SafePluginActionValue(action)} result=PATCH_WINDOW responseMode=patchWindow windowId={SafePluginActionValue(windowId)} requestedPatches={result.UiPatches?.Count ?? 0} appliedContractPatches={normalizedPatches.Count} contract=plugin_action_declarative_window_patch rule=release_contract");
        return Results.Json(result);
    }

    if (responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase))
    {
        var refreshIssued = false;
        var hostRefresh = "-";
        var resolvedWindowId = NormalizePluginWindowId(windowId);
        var normalizedTarget = NormalizePluginWindowRefreshTarget(refreshTarget);
        var pageRefreshIssued = false;
        if (refreshAfter && runtimeUiKind == TvAIrPlugin.Runtime.RuntimeUiKind.Page && http is not null)
        {
            var pageNavigationTarget = BuildRuntimePageRefreshNavigationTarget(http, requestedContentRoute);
            http.HttpContext.Response.Headers["X-TvAIr-Refresh-Surface"] = "page";
            http.HttpContext.Response.Headers["X-TvAIr-Preserve-Scroll"] = preserveScroll ? "true" : "false";
            http.HttpContext.Response.Headers["X-TvAIr-Refresh-Location"] = pageNavigationTarget;
            pageRefreshIssued = true;
            refreshIssued = true;
            hostRefresh = "PAGE_NAVIGATE";
            log.Add("PLUGIN_PAGE_REFRESH_NAVIGATION", pluginName, $"action={SafePluginActionValue(action)} result=ISSUED target={SafePluginActionValue(pageNavigationTarget)} preserveScroll={preserveScroll} source=host_owned_same_page_navigation rule=runtime_page_refresh_navigation_contract");
        }
        else if (refreshAfter && !string.IsNullOrWhiteSpace(resolvedWindowId))
        {
            var refreshRequest = new PluginWindowRequest
            {
                WindowId = resolvedWindowId,
                RefreshTarget = normalizedTarget,
                PreserveScroll = preserveScroll,
                ForceReload = true
            };
            var session = windows.Refresh(resolvedWindowId, pluginId, refreshRequest);
            if (session is not null && toolWindows is not null && http is not null)
            {
                var contentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
                var hostCaps = toolWindows.GetCapabilities();
                var navigationUrl = BuildToolWindowNavigationUrl($"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", contentRoute, hostCaps);
                var absoluteNavigationUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
                var hostResult = toolWindows.RefreshExisting(session, absoluteNavigationUrl);
                hostRefresh = hostResult.Result;
                refreshIssued = true;
            }
            else
            {
                hostRefresh = session is null ? "REFRESH_NOT_FOUND" : "HOST_UNAVAILABLE";
            }
        }
        if (feedbackRequested)
        {
            log.Add("PLUGIN_ACTION_FEEDBACK", pluginName, $"action={SafePluginActionValue(action)} correlationId={SafePluginActionValue(correlationId)} phase={SafePluginActionValue(result.Feedback?.Phase.ToString())} success={result.Succeeded} floatingLabel={(result.FloatingLabel is null ? "none" : "issued")} refreshIssued={refreshIssued} contract=plugin_action_feedback_lifecycle rule=release_contract");
            return Results.Json(result);
        }
        log.Add("PLUGIN_ACTION_RESPONSE_CONTRACT", pluginName, $"action={SafePluginActionValue(action)} result=NO_CONTENT responseMode={SafePluginActionValue(responseMode)} surface={SafePluginActionValue(runtimeUiKind?.ToString())} windowId={SafePluginActionValue(windowId)} refreshAfter={refreshAfter} refreshTarget={SafePluginActionValue(normalizedTarget)} refreshIssued={refreshIssued} pageRefreshIssued={pageRefreshIssued} hostRefresh={SafePluginActionValue(hostRefresh)} jsonSuppressed=True contract=plugin_action_hosthandled_refresh_after_content rule=release_contract");
        return Results.NoContent();
    }
    if (responseMode.Equals("html", StringComparison.OrdinalIgnoreCase)) return PluginHtmlMessage("プラグイン操作", result.Message, StatusCodes.Status200OK, string.IsNullOrWhiteSpace(windowId) ? "/" : $"/plugin-window/{Uri.EscapeDataString(windowId)}", "戻る");
    return Results.Ok(result);
}

static async Task<IResult> HandlePluginActionDispatchAsync(
    HttpRequest http,
    PluginRegistry registry,
    PluginActionTokenStore actionTokens,
    PluginWindowSessionStore windows,
    PluginToolWindowHostService toolWindows,
    PluginBoundaryGate boundaryGate,
    LogRepository log)
{
    var request = await ReadRuntimeUiActionHttpRequestAsync(http);
    var action = SplitPluginWindowRawValues(request.Action).FirstOrDefault() ?? string.Empty;
    var payload = request.Payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    request.Payload = payload;
    var pluginId = NormalizePluginActionId(!string.IsNullOrWhiteSpace(request.PluginId)
        ? request.PluginId
        : ReadPayload(payload, "PluginId", "pluginId"));
    var route = !string.IsNullOrWhiteSpace(request.RouteSegment)
        ? request.RouteSegment.Trim()
        : ReadPayload(payload, "RouteSegment", "routeSegment");

    var responseMode = NormalizePluginFormResponseMode(!string.IsNullOrWhiteSpace(request.ResponseMode) ? request.ResponseMode : ReadPayload(payload, "responseMode", "ResponseMode"));
    var requestedWindowId = NormalizePluginWindowId(!string.IsNullOrWhiteSpace(request.WindowId) ? request.WindowId : ReadPayload(payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId"));
    var requestedRefreshTarget = NormalizePluginWindowRefreshTarget(!string.IsNullOrWhiteSpace(request.RefreshTarget) ? request.RefreshTarget : ReadPayload(payload, "refreshTarget", "RefreshTarget"));
    var safeEvent = ReadPayload(payload, "safeEvent", "SafeEvent");
    var safeEventInteractionId = ReadPayload(payload, "safeEventInteractionId", "SafeEventInteractionId", "interactionId", "InteractionId");
    var safeEventWindowId = NormalizePluginWindowId(ReadPayload(payload, "safeEventWindowId", "SafeEventWindowId"));
    var feedbackRequested = TryReadBoolPayload(payload, out var parsedFeedbackRequested, "feedbackRequested", "FeedbackRequested") && parsedFeedbackRequested;
    var feedbackCorrelationId = string.IsNullOrWhiteSpace(safeEventInteractionId) ? Guid.NewGuid().ToString("N") : safeEventInteractionId.Trim();
    var recoveredIdentity = RecoverPluginActionIdentity(registry, windows, pluginId, route, action, requestedWindowId, safeEventWindowId, payload);
    if (!string.Equals(pluginId, recoveredIdentity.PluginId, StringComparison.Ordinal)
        || !string.Equals(route, recoveredIdentity.RouteSegment, StringComparison.Ordinal))
    {
        pluginId = recoveredIdentity.PluginId;
        route = recoveredIdentity.RouteSegment;
        request.PluginId = pluginId;
        request.RouteSegment = route;
        if (!string.IsNullOrWhiteSpace(pluginId)) payload["PluginId"] = pluginId;
        if (!string.IsNullOrWhiteSpace(route)) payload["RouteSegment"] = route;
        log.Add("PLUGIN_ACTION_IDENTITY_RECOVER", string.IsNullOrWhiteSpace(pluginId) ? "-" : pluginId,
            $"result=APPLIED action={SafePluginActionValue(action)} reason={SafePluginActionValue(recoveredIdentity.Reason)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(route)} windowId={SafePluginActionValue(requestedWindowId)} safeEventWindowId={SafePluginActionValue(safeEventWindowId)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_action_identity_recovery");
    }
    if (!string.IsNullOrWhiteSpace(route)) payload["RouteSegment"] = route;

    var requestedPreserveScroll = true;
    if (TryReadBoolPayload(payload, out var parsedPreserveScroll, "preserveScroll", "PreserveScroll"))
        requestedPreserveScroll = parsedPreserveScroll;
    var requestedRefreshAfter = TryReadBoolPayload(payload, out var parsedRefreshAfter, "refreshAfter", "RefreshAfter") && parsedRefreshAfter;
    var requestedContentRoute = ReadPayload(payload, "contentRoute", "ContentRoute");
    if (string.IsNullOrWhiteSpace(requestedContentRoute))
    {
        var requestedRefreshQuery = ReadPayload(payload, "refreshQuery", "RefreshQuery");
        if (!string.IsNullOrWhiteSpace(requestedRefreshQuery))
            requestedContentRoute = $"/plugin/{Uri.EscapeDataString(route)}?{requestedRefreshQuery.TrimStart('?')}";
    }

    var plugin = FindPluginByActionIdentity(registry, pluginId, route);
    var pluginName = plugin?.Descriptor.DisplayName ?? pluginId;
    if (plugin is null)
    {
        log.Add("PLUGIN_ACTION", "DENY", $"plugin={SafePluginActionValue(pluginId)} action={SafePluginActionValue(action)} result=DENIED reason=plugin_not_found endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_action_dispatch");
        return BuildPluginActionDispatchError(new RuntimeUiActionResult { Succeeded = false, Message = "Plugin not found.", Diagnostics = "plugin_not_found" }, responseMode, requestedWindowId, StatusCodes.Status404NotFound);
    }

    registry.FindRuntimeUiPluginNative(route, out var actionUiDefinition);
    var actionUiKind = actionUiDefinition?.Kind;

    var token = request.ActionToken;
    var pluginActionIdForToken = GetPluginActionIdentity(plugin);
    if (!ValidatePluginActionTokenOrRecoverHostWindow(actionTokens, windows, token, pluginActionIdForToken, route, pluginName, action, requestedWindowId, safeEventWindowId, http.Path.Value ?? string.Empty, "action_dispatch", log, out var tokenReason))
    {
        if (!string.IsNullOrWhiteSpace(safeEvent))
            log.Add("PLUGIN_SAFE_EVENT", pluginName, $"event={SafePluginActionValue(safeEvent)} action={SafePluginActionValue(action)} result=DENIED reason={tokenReason} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(route)} windowId={SafePluginActionValue(requestedWindowId)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_action_dispatch");
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason={tokenReason} windowId={SafePluginActionValue(requestedWindowId)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_action_dispatch");
        return BuildPluginActionDispatchError(new RuntimeUiActionResult { Succeeded = false, Message = "Invalid plugin action token.", Diagnostics = tokenReason }, responseMode, requestedWindowId, StatusCodes.Status400BadRequest);
    }

    var actionBoundary = boundaryGate.CheckAction(ResolveRuntimeBoundaryPlugin(registry, plugin), pluginId, action, http.Path.Value ?? string.Empty);
    if (!actionBoundary.Allowed)
    {
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason={SafePluginActionValue(actionBoundary.Reason)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_boundary_gate");
        var status = actionBoundary.FailureKind == PluginBoundaryFailureKind.PermissionDenied ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
        return BuildPluginActionDispatchError(new RuntimeUiActionResult { Succeeded = false, Message = "Plugin action was denied by TvAIr host boundary.", Diagnostics = actionBoundary.Reason }, responseMode, requestedWindowId, status);
    }

    var nativeActionHandler = registry.FindRuntimeUiPluginNative(route, out var nativeActionDefinition);
    if (nativeActionHandler is null || nativeActionDefinition is null)
    {
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason=plugin_action_handler_not_implemented route={SafePluginActionValue(route)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_owned_action_dispatch");
        return BuildPluginActionDispatchError(new RuntimeUiActionResult { Succeeded = false, Message = "Plugin action handler is not implemented.", Diagnostics = "plugin_action_handler_not_implemented" }, responseMode, requestedWindowId, StatusCodes.Status400BadRequest);
    }

    try
    {
        request.PluginId = pluginId;
        request.RouteSegment = route;
        request.Action = action;
        request.Payload = payload;
        request.WindowId = requestedWindowId;
        request.RefreshTarget = requestedRefreshTarget;
        request.PreserveScroll = requestedPreserveScroll;
        var enabledPayload = ReadPayload(request.Payload, "enabled", "Enabled", "isEnabled", "checked");
        log.Add("PLUGIN_ACTION_PAYLOAD_CONTRACT", pluginName,
            $"action={SafePluginActionValue(action)} interactionId={SafePluginActionValue(safeEventInteractionId)} result=RECEIVED route={SafePluginActionValue(route)} windowId={SafePluginActionValue(requestedWindowId)} responseMode={SafePluginActionValue(responseMode)} payloadKeys={SafePluginActionValue(FormatPluginPayloadKeys(request.Payload))} enabled={SafePluginActionValue(string.IsNullOrWhiteSpace(enabledPayload) ? "-" : enabledPayload)} rule=plugin_owned_action_dispatch");
        var pluginResult = await nativeActionHandler.HandleActionAsync(
            new RuntimeUiActionContext
            {
                PluginId = pluginId,
                UiDefinitionId = nativeActionDefinition.UiDefinitionId,
                Route = route,
                ActionName = action,
                CurrentWindowId = requestedWindowId,
                Payload = request.Payload,
                CorrelationId = feedbackCorrelationId,
                RequestedAt = DateTime.Now
            },
            http.HttpContext.RequestAborted);
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} interactionId={SafePluginActionValue(safeEventInteractionId)} result={(pluginResult.Succeeded ? "OK" : "PLUGIN_DECLINED")} route={SafePluginActionValue(route)} responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_owned_action_dispatch");
        var effectiveRefreshTarget = pluginResult.RefreshRequested
            ? NormalizePluginWindowRefreshTarget(pluginResult.RefreshTarget)
            : requestedRefreshTarget;
        var effectivePreserveScroll = pluginResult.RefreshRequested
            ? pluginResult.PreserveScroll
            : requestedPreserveScroll;
        var effectiveContentRoute = pluginResult.RefreshRequested && !string.IsNullOrWhiteSpace(pluginResult.ContentRoute)
            ? pluginResult.ContentRoute.Trim()
            : requestedContentRoute;
        var effectiveResponseMode = pluginResult.RefreshRequested
            ? (actionUiKind == TvAIrPlugin.Runtime.RuntimeUiKind.Page ? responseMode : "refreshWindow")
            : responseMode;
        var effectiveRefreshAfter = pluginResult.RefreshRequested || requestedRefreshAfter;
        log.Add("PLUGIN_RUNTIME_UI_ACTION_REFRESH_CONTRACT", pluginName,
            $"action={SafePluginActionValue(action)} result={(pluginResult.RefreshRequested ? "PLUGIN_REQUESTED" : "REQUEST_FALLBACK")} surface={SafePluginActionValue(actionUiKind?.ToString())} responseMode={SafePluginActionValue(effectiveResponseMode)} windowId={SafePluginActionValue(requestedWindowId)} refreshTarget={SafePluginActionValue(effectiveRefreshTarget)} preserveScroll={effectivePreserveScroll} contentRoute={SafePluginActionValue(effectiveContentRoute)} rule=runtime_ui_action_result_refresh_contract");
        return pluginResult.Succeeded
            ? BuildPluginOwnedActionResponse(pluginResult, effectiveResponseMode, requestedWindowId, effectiveRefreshTarget, effectivePreserveScroll, windows, GetPluginActionIdentity(plugin), log, pluginName, action, actionUiKind, effectiveRefreshAfter, toolWindows, http, effectiveContentRoute, feedbackRequested, feedbackCorrelationId)
            : BuildPluginActionDispatchError(pluginResult, effectiveResponseMode, requestedWindowId, StatusCodes.Status400BadRequest, feedbackRequested, feedbackCorrelationId);
    }
    catch (Exception ex)
    {
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} result=ERROR type={SafePluginActionValue(ex.GetType().Name)} message={SafePluginActionValue(ex.Message)} endpoint={SafePluginActionValue(http.Path.Value)} rule=plugin_owned_action_dispatch");
        return BuildPluginActionDispatchError(new RuntimeUiActionResult { Succeeded = false, Message = "Plugin action failed.", Diagnostics = ex.GetType().Name }, responseMode, requestedWindowId, StatusCodes.Status500InternalServerError, feedbackRequested, feedbackCorrelationId);
    }
}

static async Task<PluginWindowRequest> ReadPluginWindowRequestAsync(HttpRequest http)
{
    var result = new PluginWindowRequest();
    try
    {
        if (string.Equals(http.Method, "GET", StringComparison.OrdinalIgnoreCase) && http.Query.Count > 0)
        {
            foreach (var kv in http.Query)
            {
                AssignPluginWindowField(result, kv.Key ?? string.Empty, kv.Value.ToString());
            }
            return result;
        }

        if (http.HasFormContentType)
        {
            var form = await http.ReadFormAsync();
            foreach (var kv in form)
            {
                AssignPluginWindowField(result, kv.Key ?? string.Empty, kv.Value.ToString());
            }
            return result;
        }

        using var reader = new StreamReader(http.Body, Encoding.UTF8, leaveOpen: false);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body)) return result;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Name.Equals("payload", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in prop.Value.EnumerateObject())
                {
                    result.Payload[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.ToString();
                }
                continue;
            }
            AssignPluginWindowField(result, prop.Name, prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : prop.Value.ToString());
        }
    }
    catch
    {
        // 呼び出し元で不足項目として拒否する。
    }
    return result;
}

static void AssignPluginWindowField(PluginWindowRequest result, string key, string value)
{
    if (string.IsNullOrWhiteSpace(key)) return;
    switch (key.Trim())
    {
        case "pluginId": case "PluginId": result.PluginId = value; break;
        case "routeSegment": case "RouteSegment": result.RouteSegment = value; break;
        case "action": case "Action": result.Action = value; break;
        case "windowId": case "WindowId": result.WindowId = value; break;
        case "title": case "Title": result.Title = value; result.Payload[key] = value; break;
        case "width": case "Width": if (int.TryParse(value, out var w)) { result.Width = NormalizePluginWindowDimension(w); result.Payload[key] = value; } break;
        case "height": case "Height": if (int.TryParse(value, out var h)) { result.Height = NormalizePluginWindowDimension(h); result.Payload[key] = value; } break;
        case "minWidth": case "MinWidth": if (int.TryParse(value, out var mw)) { result.MinWidth = NormalizePluginWindowDimension(mw); result.Payload[key] = value; } break;
        case "minHeight": case "MinHeight": if (int.TryParse(value, out var mh)) { result.MinHeight = NormalizePluginWindowDimension(mh); result.Payload[key] = value; } break;
        case "resizable": case "Resizable": if (bool.TryParse(value, out var rz)) { result.Resizable = rz; result.Payload[key] = value; } break;
        case "movable": case "Movable": if (bool.TryParse(value, out var mv)) { result.Movable = mv; result.Payload[key] = value; } break;
        case "alwaysOnTop": case "AlwaysOnTop": if (bool.TryParse(value, out var aot)) { result.AlwaysOnTop = aot; result.Payload[key] = value; } break;
        case "contentRoute": case "ContentRoute": result.ContentRoute = value; result.Payload[key] = value; break;
        case "refreshTarget": case "RefreshTarget": result.RefreshTarget = value; break;
        case "preserveScroll": case "PreserveScroll": if (bool.TryParse(value, out var ps)) { result.PreserveScroll = ps; result.Payload[key] = value; } break;
        case "forceReload": case "ForceReload": if (bool.TryParse(value, out var fr)) { result.ForceReload = fr; result.Payload[key] = value; } break;
        case "refreshAfter": case "RefreshAfter": if (bool.TryParse(value, out var ra)) { result.RefreshAfter = ra; result.Payload[key] = value; } break;
        case "reuseExisting": case "ReuseExisting": if (bool.TryParse(value, out var re)) { result.ReuseExisting = re; result.Payload[key] = value; } break;
        case "activateExisting": case "ActivateExisting": if (bool.TryParse(value, out var ae)) { result.ActivateExisting = ae; result.Payload[key] = value; } break;
        case "windowToken": case "WindowToken": result.WindowToken = value; break;
        case "responseMode": case "ResponseMode": result.ResponseMode = value; result.Payload[key] = value; break;
        case "returnUrl": case "ReturnUrl": result.ReturnUrl = value; result.Payload[key] = value; break;
        default:
            result.Payload[key] = value;
            break;
    }
}


static int NormalizePluginWindowDimension(int value) => value > 0 ? value : 0;

static void NormalizePluginWindowRequestFromPayload(PluginWindowRequest request)
{
    if (string.IsNullOrWhiteSpace(request.PluginId)) request.PluginId = ReadPayload(request.Payload, "pluginId", "PluginId");
    if (string.IsNullOrWhiteSpace(request.RouteSegment)) request.RouteSegment = ReadPayload(request.Payload, "routeSegment", "RouteSegment");
    if (string.IsNullOrWhiteSpace(request.Action)) request.Action = ReadPayload(request.Payload, "action", "Action");
    request.Action = NormalizePluginWindowAction(request.Action);
    request.Payload["action"] = request.Action;
    if (string.IsNullOrWhiteSpace(request.WindowId)) request.WindowId = ReadPayload(request.Payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId", "safeEventWindowId", "SafeEventWindowId");
    request.WindowId = NormalizePluginWindowId(request.WindowId);
    if (!string.IsNullOrWhiteSpace(request.WindowId)) request.Payload["windowId"] = request.WindowId;
    if (string.IsNullOrWhiteSpace(request.Title)) request.Title = ReadPayload(request.Payload, "title", "Title");
    if (TryReadIntPayload(request.Payload, out var width, "width", "Width")) request.Width = NormalizePluginWindowDimension(width);
    if (TryReadIntPayload(request.Payload, out var height, "height", "Height")) request.Height = NormalizePluginWindowDimension(height);
    if (TryReadIntPayload(request.Payload, out var minWidth, "minWidth", "MinWidth")) request.MinWidth = NormalizePluginWindowDimension(minWidth);
    if (TryReadIntPayload(request.Payload, out var minHeight, "minHeight", "MinHeight")) request.MinHeight = NormalizePluginWindowDimension(minHeight);
    if (TryReadBoolPayload(request.Payload, out var resizable, "resizable", "Resizable")) request.Resizable = resizable;
    if (TryReadBoolPayload(request.Payload, out var movable, "movable", "Movable")) request.Movable = movable;
    if (TryReadBoolPayload(request.Payload, out var alwaysOnTop, "alwaysOnTop", "AlwaysOnTop")) request.AlwaysOnTop = alwaysOnTop;
    if (string.IsNullOrWhiteSpace(request.ContentRoute)) request.ContentRoute = ReadPayload(request.Payload, "contentRoute", "ContentRoute");
    var refreshTarget = ReadPayload(request.Payload, "refreshTarget", "RefreshTarget");
    if (!string.IsNullOrWhiteSpace(refreshTarget)) request.RefreshTarget = refreshTarget;
    if (TryReadBoolPayload(request.Payload, out var preserveScroll, "preserveScroll", "PreserveScroll")) request.PreserveScroll = preserveScroll;
    if (TryReadBoolPayload(request.Payload, out var forceReload, "forceReload", "ForceReload")) request.ForceReload = forceReload;
    if (TryReadBoolPayload(request.Payload, out var refreshAfter, "refreshAfter", "RefreshAfter")) request.RefreshAfter = refreshAfter;
    if (TryReadBoolPayload(request.Payload, out var reuseExisting, "reuseExisting", "ReuseExisting")) request.ReuseExisting = reuseExisting;
    if (TryReadBoolPayload(request.Payload, out var activateExisting, "activateExisting", "ActivateExisting")) request.ActivateExisting = activateExisting;
    if (string.IsNullOrWhiteSpace(request.WindowToken)) request.WindowToken = ReadPayload(request.Payload, "windowToken", "WindowToken");
    if (string.IsNullOrWhiteSpace(request.ReturnUrl)) request.ReturnUrl = ReadPayload(request.Payload, "returnUrl", "ReturnUrl");
    if (string.IsNullOrWhiteSpace(request.ResponseMode) || request.ResponseMode.Equals("json", StringComparison.OrdinalIgnoreCase))
    {
        var responseMode = ReadPayload(request.Payload, "responseMode", "ResponseMode");
        if (!string.IsNullOrWhiteSpace(responseMode)) request.ResponseMode = responseMode;
    }
}

static string ResolvePluginWindowId(PluginWindowRequest request)
{
    var windowId = ReadPayload(request.Payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId");
    if (string.IsNullOrWhiteSpace(windowId)) windowId = request.WindowId;
    return NormalizePluginWindowId(windowId);
}


static string NormalizePluginWindowRefreshTarget(string? value)
{
    var target = (value ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(target)) return "content";
    if (target.Equals("self", StringComparison.OrdinalIgnoreCase) || target.Equals("current", StringComparison.OrdinalIgnoreCase)) return "content";
    if (target.Equals("iframe", StringComparison.OrdinalIgnoreCase) || target.Equals("frame", StringComparison.OrdinalIgnoreCase)) return "content";
    return target.Equals("content", StringComparison.OrdinalIgnoreCase) ? "content" : "content";
}

static bool HasPluginWindowPayload(PluginWindowRequest request, params string[] keys)
{
    foreach (var key in keys)
    {
        if (request.Payload.ContainsKey(key)) return true;
    }
    return false;
}

static string FormatPluginHostSize(int? width, int? height)
    => width.HasValue && height.HasValue ? $"{width.Value}x{height.Value}" : "-";

static bool TryReadIntPayload(Dictionary<string, string> payload, out int value, params string[] keys)
{
    value = 0;
    var raw = ReadPayload(payload, keys);
    return int.TryParse(raw, out value);
}

static bool TryReadBoolPayload(Dictionary<string, string> payload, out bool value, params string[] keys)
{
    value = false;
    var raw = ReadPayload(payload, keys);
    if (string.IsNullOrWhiteSpace(raw)) return false;
    if (bool.TryParse(raw, out value)) return true;
    if (raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw.Equals("on", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
    if (raw == "0" || raw.Equals("no", StringComparison.OrdinalIgnoreCase) || raw.Equals("off", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
    return false;
}

static async Task<RuntimeUiActionHttpRequest> ReadRuntimeUiActionHttpRequestAsync(HttpRequest http)
{
    var result = new RuntimeUiActionHttpRequest();
    try
    {
        foreach (var kv in http.Query)
        {
            var key = kv.Key ?? string.Empty;
            var value = kv.Value.ToString();
            AssignRuntimeUiActionField(result, key, value);
        }

        if (http.HasFormContentType)
        {
            var form = await http.ReadFormAsync();
            foreach (var kv in form)
            {
                var key = kv.Key ?? string.Empty;
                var value = kv.Value.ToString();
                AssignRuntimeUiActionField(result, key, value);
            }
            NormalizePluginActionPayloadAliases(result.Payload);
            return result;
        }

        if (http.ContentLength is null or 0)
        {
            NormalizePluginActionPayloadAliases(result.Payload);
            return result;
        }

        using var doc = await JsonDocument.ParseAsync(http.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            NormalizePluginActionPayloadAliases(result.Payload);
            return result;
        }
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("payload") || prop.NameEquals("Payload"))
            {
                MergePluginActionJsonPayload(result.Payload, prop.Value);
                continue;
            }
            AssignRuntimeUiActionField(result, prop.Name, JsonElementToPluginActionString(prop.Value));
        }
        NormalizePluginActionPayloadAliases(result.Payload);
    }
    catch
    {
        // 不正なAction要求は後段の必須項目/トークン検証で拒否する。
    }
    return result;
}

static void AssignRuntimeUiActionField(RuntimeUiActionHttpRequest target, string key, string value)
{
    key = (key ?? string.Empty).Trim();
    value = (value ?? string.Empty).Trim();
    if (key.Equals("pluginId", StringComparison.OrdinalIgnoreCase))
        target.PluginId = value;
    else if (key.Equals("routeSegment", StringComparison.OrdinalIgnoreCase))
        target.RouteSegment = value;
    else if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
        target.Action = value;
    else if (key.Equals("actionToken", StringComparison.OrdinalIgnoreCase))
        target.ActionToken = value;
    else if (key.Equals("responseMode", StringComparison.OrdinalIgnoreCase))
    {
        target.ResponseMode = value;
        target.Payload[key] = value;
    }
    else if (key.Equals("windowId", StringComparison.OrdinalIgnoreCase) || key.Equals("currentWindowId", StringComparison.OrdinalIgnoreCase))
    {
        target.WindowId = value;
        target.Payload[key] = value;
    }
    else if (key.Equals("refreshTarget", StringComparison.OrdinalIgnoreCase))
    {
        target.RefreshTarget = value;
        target.Payload[key] = value;
    }
    else if (key.Equals("preserveScroll", StringComparison.OrdinalIgnoreCase))
    {
        if (bool.TryParse(value, out var preserveScroll)) target.PreserveScroll = preserveScroll;
        target.Payload[key] = value;
    }
    else if (!string.IsNullOrWhiteSpace(key))
        target.Payload[key] = value;
}

static void MergePluginActionJsonPayload(Dictionary<string, string> payload, JsonElement value)
{
    if (value.ValueKind == JsonValueKind.String)
    {
        MergePluginActionPayloadJson(payload, value.GetString());
        return;
    }

    if (value.ValueKind != JsonValueKind.Object) return;
    foreach (var p in value.EnumerateObject())
        payload[p.Name] = JsonElementToPluginActionString(p.Value);
}

static void MergePluginActionPayloadJson(Dictionary<string, string> payload, string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return;
    try
    {
        using var doc = JsonDocument.Parse(json);
        MergePluginActionJsonPayload(payload, doc.RootElement);
    }
    catch
    {
        // payloadJson系の不正JSONは、後段の必須項目検証で拒否する。
    }
}

static void NormalizePluginActionPayloadAliases(Dictionary<string, string> payload)
{
    MergePluginActionPayloadJson(payload, ReadPayload(payload, "payloadJson", "PayloadJson", "actionPayloadJson", "ActionPayloadJson"));
    CopyPluginActionPayloadAlias(payload, "networkId", "NetworkId", "network_id", "network-id", "nid", "Nid", "NID");
    CopyPluginActionPayloadAlias(payload, "transportStreamId", "TransportStreamId", "transport_stream_id", "transport-stream-id", "tsid", "Tsid", "TSID");
    CopyPluginActionPayloadAlias(payload, "serviceId", "ServiceId", "service_id", "service-id", "sid", "Sid", "SID");
    CopyPluginActionPayloadAlias(payload, "serviceName", "ServiceName", "service", "name", "channelName");
    CopyPluginActionPayloadAlias(payload, "refreshTarget", "RefreshTarget", "refreshtarget", "refresh-target");
    CopyPluginActionPayloadAlias(payload, "preserveScroll", "PreserveScroll", "preservescroll", "preserve-scroll");
    NormalizeViewerProfilePayloadAlias(payload);
}

static void NormalizeViewerProfilePayloadAlias(Dictionary<string, string> payload)
{
    // release_contract: vtuner is accepted only as a backward-compatible input alias.
    // The host canonicalizes viewer profile to viewerProfile/viewerProfileId aliases and no longer
    // re-emits vtuner, so plugin UI can retire the legacy name without breaking older payloads.
    var candidates = new[]
    {
        "viewerProfile", "ViewerProfile", "viewer_profile", "viewer-profile",
        "viewerProfileId", "ViewerProfileId", "vtuner", "vTuner", "viewer", "profile"
    };

    var explicitFrame = candidates
        .Select(k => payload.TryGetValue(k, out var v) ? v : string.Empty)
        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && IsExplicitTvTestProfileValue(v));

    var selected = !string.IsNullOrWhiteSpace(explicitFrame)
        ? explicitFrame
        : ReadPayload(payload, candidates);

    if (string.IsNullOrWhiteSpace(selected)) return;

    payload["viewerProfile"] = selected;
}

static bool IsExplicitTvTestProfileValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    var v = value.Trim();
    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0) return true;
    return v.StartsWith("tvtest", StringComparison.OrdinalIgnoreCase)
        && v.Length > "tvtest".Length
        && int.TryParse(v["tvtest".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
        && ordinal > 0;
}

static void CopyPluginActionPayloadAlias(Dictionary<string, string> payload, string canonical, params string[] aliases)
{
    var value = ReadPayload(payload, new[] { canonical }.Concat(aliases).ToArray());
    if (string.IsNullOrWhiteSpace(value)) return;
    payload[canonical] = value;
    foreach (var alias in aliases)
        payload[alias] = value;
}

static string FormatPluginPayloadKeys(Dictionary<string, string> payload)
{
    if (payload.Count == 0) return "-";
    return string.Join("|", payload.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Take(80));
}

static string JsonElementToPluginActionString(JsonElement value)
{
    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };
}

static string NormalizePluginActionId(string? value)
    => string.IsNullOrWhiteSpace(value) ? string.Empty : PluginIdentity.Normalize(value);

static ITvAirRuntimeCapabilityPlugin ResolveRuntimeBoundaryPlugin(PluginRegistry registry, ITvAirRuntimeCapabilityPlugin plugin)
    => plugin;

static string GetPluginActionIdentity(ITvAirRuntimeCapabilityPlugin plugin)
    => NormalizePluginActionId(plugin.Descriptor.PluginId);

static ITvAirRuntimeCapabilityPlugin? FindPluginByActionIdentity(PluginRegistry registry, string? pluginId, string? routeSegment)
{
    var id = NormalizePluginActionId(pluginId);
    var route = NormalizePluginRouteSegment(routeSegment);
    foreach (var plugin in registry.GetRuntimePlugins())
    {
        var descriptor = plugin.Descriptor;
        var canonicalPluginId = NormalizePluginActionId(descriptor.PluginId);
        var routeMatched = descriptor.UiDefinitions.Any(definition =>
                string.Equals(NormalizePluginRouteSegment(definition.Route), route, StringComparison.OrdinalIgnoreCase))
            || descriptor.MenuActions.Any(action =>
                string.Equals(NormalizePluginRouteSegment(action.Route), route, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(id)
            && string.Equals(canonicalPluginId, id, StringComparison.OrdinalIgnoreCase))
            return plugin;
        if (!string.IsNullOrWhiteSpace(route) && routeMatched)
            return plugin;
    }
    return null;
}


static (string PluginId, string RouteSegment, string Reason) RecoverPluginActionIdentity(
    PluginRegistry registry,
    PluginWindowSessionStore windows,
    string? pluginId,
    string? routeSegment,
    string? action,
    string? windowId,
    string? safeEventWindowId,
    Dictionary<string, string>? payload)
{
    var id = NormalizePluginActionId(pluginId);
    var route = NormalizePluginRouteSegment(routeSegment);
    if (FindPluginByActionIdentity(registry, id, route) is not null)
        return (id, route, "already_resolved");

    static string CleanRoute(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var raw = value.Trim().Trim('/');
        if (raw.StartsWith("plugin/", StringComparison.OrdinalIgnoreCase)) raw = raw[7..];
        if (raw.StartsWith("plugin-ui/", StringComparison.OrdinalIgnoreCase)) raw = raw[10..];
        return new string(raw.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
    }

    var candidateWindowIds = new[] { windowId, safeEventWindowId, ReadPayload(payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "windowId", "WindowId", "currentWindowId", "CurrentWindowId", "safeEventWindowId", "SafeEventWindowId") }
        .Select(NormalizePluginWindowId)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    foreach (var candidateWindowId in candidateWindowIds)
    {
        var session = windows.Get(candidateWindowId);
        if (session is null || session.IsClosed) continue;
        var sessionPluginId = NormalizePluginActionId(session.PluginId);
        var sessionRoute = NormalizePluginRouteSegment(session.RouteSegment);
        if (FindPluginByActionIdentity(registry, sessionPluginId, sessionRoute) is not null)
            return (sessionPluginId, sessionRoute, "host_window_session");
    }

    var actionText = (action ?? string.Empty).Trim();
    var dot = actionText.IndexOf('.');
    if (dot > 0)
    {
        var prefix = CleanRoute(actionText[..dot]);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var prefixedPlugin = FindPluginByActionIdentity(registry, prefix, prefix);
            if (prefixedPlugin is not null)
                return (GetPluginActionIdentity(prefixedPlugin), prefix, "action_prefix");
        }
    }

    var payloadId = ReadPayload(payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "PluginId", "pluginId");
    var payloadRoute = ReadPayload(payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "RouteSegment", "routeSegment");
    if (!string.IsNullOrWhiteSpace(payloadId) || !string.IsNullOrWhiteSpace(payloadRoute))
    {
        var payloadPlugin = FindPluginByActionIdentity(registry, payloadId, payloadRoute);
        if (payloadPlugin is not null)
            return (GetPluginActionIdentity(payloadPlugin), CleanRoute(payloadRoute), "payload_identity");
    }

    return (id, route, "unresolved");
}

static string ReadPayload(Dictionary<string, string> payload, params string[] keys)
{
    foreach (var key in keys)
    {
        if (payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.Trim();
    }
    return string.Empty;
}

static string SafePluginActionValue(string? value)
{
    var v = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
    return string.IsNullOrWhiteSpace(v) ? "-" : (v.Length > 180 ? v[..180] + "…" : v);
}

static string ExtractPluginBodyFragment(string html)
{
    if (string.IsNullOrWhiteSpace(html)) return string.Empty;

    var lower = html.ToLowerInvariant();
    var bodyStart = lower.IndexOf("<body", StringComparison.Ordinal);
    if (bodyStart < 0) return html;

    var bodyOpenEnd = lower.IndexOf('>', bodyStart);
    if (bodyOpenEnd < 0) return html;

    var bodyEnd = lower.LastIndexOf("</body>", StringComparison.Ordinal);
    if (bodyEnd < 0 || bodyEnd <= bodyOpenEnd) return html[(bodyOpenEnd + 1)..];

    return html[(bodyOpenEnd + 1)..bodyEnd];
}

static string ResolveEffectiveHostTheme(string? selectedTheme)
{
    var selected = IniSettingsService.NormalizeSystemTheme(selectedTheme);
    if (string.Equals(selected, "dark", StringComparison.OrdinalIgnoreCase)) return "dark";
    if (string.Equals(selected, "light", StringComparison.OrdinalIgnoreCase)) return "light";
    try
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        var light = value is int i ? i != 0 : value?.ToString() != "0";
        return light ? "light" : "dark";
    }
    catch
    {
        return "light";
    }
}

static IReadOnlyDictionary<string, string> BuildPluginThemeContract(string selectedTheme, string effectiveTheme)
{
    var dark = string.Equals(effectiveTheme, "dark", StringComparison.OrdinalIgnoreCase);
    var theme = dark ? "dark" : "light";
    string Role(string token) => UiThemeRoleContract.Get(theme, token);

    // Runtime UI receives a projection of the Host semantic role contract.
    // Color values are never re-declared here; tvair-theme-contract.css is the source of truth.
    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["contractVersion"] = "2",
        ["statePrecedence"] = "semantic_state_over_generic_hover",
        ["selectedTheme"] = selectedTheme,
        ["effectiveTheme"] = theme,
        ["htmlAttribute"] = "data-tvair-effective-theme",
        ["runtimeEvent"] = "tvair-theme-runtime-synced",

        ["pageBackground"] = Role("--tvair-role-page-bg"),
        ["surfaceBackground"] = Role("--tvair-role-panel-bg"),
        ["subtleBackground"] = Role("--tvair-role-subpanel-bg"),
        ["inputBackground"] = Role("--tvair-role-control-bg"),
        ["text"] = Role("--tvair-role-page-fg"),
        ["mutedText"] = Role("--tvair-role-text-muted-fg"),
        ["border"] = Role("--tvair-role-border-soft"),
        ["accent"] = Role("--tvair-role-action-primary-bg"),
        ["accentText"] = Role("--tvair-role-action-primary-fg"),
        ["focus"] = Role("--tvair-role-focus"),

        ["controlBackground"] = Role("--tvair-role-control-bg"),
        ["controlText"] = Role("--tvair-role-control-fg"),
        ["controlBorder"] = Role("--tvair-role-control-border"),
        ["controlHoverBackground"] = Role("--tvair-role-control-hover-bg"),
        ["controlHoverText"] = Role("--tvair-role-control-hover-fg"),
        ["controlHoverBorder"] = Role("--tvair-role-control-hover-border"),

        ["selectedBackground"] = Role("--tvair-role-focus"),
        ["selectedText"] = Role("--tvair-role-control-active-fg"),
        ["selectedBorder"] = Role("--tvair-role-focus"),
        ["selectedHoverBackground"] = Role("--tvair-role-focus"),
        ["selectedHoverText"] = Role("--tvair-role-control-active-fg"),
        ["selectedHoverBorder"] = Role("--tvair-role-focus"),

        ["disabledBackground"] = Role("--tvair-role-control-disabled-bg"),
        ["disabledText"] = Role("--tvair-role-control-disabled-fg"),
        ["disabledBorder"] = Role("--tvair-role-action-disabled-border"),

        ["primaryActionBackground"] = Role("--tvair-role-action-primary-bg"),
        ["primaryActionText"] = Role("--tvair-role-action-primary-fg"),
        ["primaryActionBorder"] = Role("--tvair-role-action-primary-border"),
        ["primaryActionHoverBackground"] = Role("--tvair-role-action-primary-hover-bg"),
        ["primaryActionHoverText"] = Role("--tvair-role-action-primary-fg"),
        ["primaryActionHoverBorder"] = Role("--tvair-role-action-primary-border"),

        ["secondaryActionBackground"] = Role("--tvair-role-action-secondary-bg"),
        ["secondaryActionText"] = Role("--tvair-role-action-secondary-fg"),
        ["secondaryActionBorder"] = Role("--tvair-role-action-secondary-border"),
        ["secondaryActionHoverBackground"] = Role("--tvair-role-action-secondary-hover-bg"),
        ["secondaryActionHoverText"] = Role("--tvair-role-action-secondary-fg"),
        ["secondaryActionHoverBorder"] = Role("--tvair-role-action-secondary-border"),

        ["dangerActionBackground"] = Role("--tvair-role-action-danger-bg"),
        ["dangerActionText"] = Role("--tvair-role-action-danger-fg"),
        ["dangerActionBorder"] = Role("--tvair-role-action-danger-border"),
        ["dangerActionHoverBackground"] = Role("--tvair-role-action-danger-hover-bg"),
        ["dangerActionHoverText"] = Role("--tvair-role-action-danger-fg"),
        ["dangerActionHoverBorder"] = Role("--tvair-role-action-danger-border")
    };
}


static string BuildPluginShellHtml(string title, string route, string pluginBody, bool toolWindowContentOnly = false, string selectedTheme = "current", string effectiveTheme = "light")
{
    if (toolWindowContentOnly)
    {
        return BuildPluginToolWindowContentHtml(title, route, pluginBody, selectedTheme, effectiveTheme);
    }

    var safeTitle = System.Net.WebUtility.HtmlEncode(title);
    var safeRoute = System.Net.WebUtility.HtmlEncode(route);
    var safeSelectedTheme = System.Net.WebUtility.HtmlEncode(IniSettingsService.NormalizeSystemTheme(selectedTheme));
    var safeEffectiveTheme = System.Net.WebUtility.HtmlEncode(string.Equals(effectiveTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light");
    var themeClass = string.Equals(safeEffectiveTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "tvair-theme-dark theme-dark" : "tvair-theme-light theme-light";
    var contentOnlyClass = string.Empty;
#if TVAIR_DEVELOPER_DIAGNOSTICS
    var developerBeaconBody = """try{var img=new Image();var url='/api/plugins/safe-event/client-log?phase='+encodeURIComponent(phase||'')+'&event='+encodeURIComponent(eventName||'')+'&action='+encodeURIComponent(action||'')+'&interactionId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-debug-interaction-id')||'')+'&pluginId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-plugin-id')||'')+'&routeSegment='+encodeURIComponent(tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'')+'&windowId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId()||'')+'&hostKind='+encodeURIComponent('winforms_webbrowser_fallback_direct_content')+'&tag='+encodeURIComponent(tvairTagName(el))+'&type='+encodeURIComponent(tvairGetAttr(el,'type')||'')+'&hasToken='+encodeURIComponent((tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'))?'true':'false')+'&payloadCount='+encodeURIComponent(tvairGetAttr(el,'data-tvair-debug-payload-count')||'')+'&payloadKeys='+encodeURIComponent(tvairGetAttr(el,'data-tvair-debug-payload-keys')||'')+'&endpoint='+encodeURIComponent(tvairGetAttr(el,'data-tvair-debug-endpoint')||tvairGetAttr(el,'data-tvair-endpoint')||'')+'&status='+encodeURIComponent(tvairGetAttr(el,'data-tvair-debug-status')||'')+'&reason='+encodeURIComponent(tvairGetAttr(el,'data-tvair-debug-reason')||'')+'&readyState='+encodeURIComponent(document.readyState||'')+'&candidates='+encodeURIComponent(tvairCountSafeEventCandidates())+'&_='+String(new Date().getTime());img.src=url;}catch(_){}""";
#else
    var developerBeaconBody = "return;";
#endif
    return $$$$"""
<!doctype html>
<html lang="ja" class="{{{{themeClass}}}}" data-theme="{{{{safeEffectiveTheme}}}}" data-tvair-theme="{{{{safeSelectedTheme}}}}" data-tvair-selected-theme="{{{{safeSelectedTheme}}}}" data-tvair-effective-theme="{{{{safeEffectiveTheme}}}}" data-tvair-theme-scope="all">
<head>
<meta charset="utf-8">
<meta http-equiv="X-UA-Compatible" content="IE=edge">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; connect-src 'self'; media-src 'self' data:">
<title>{{{{safeTitle}}}} - TvAIr</title>
<link rel="icon" type="image/x-icon" href="/favicon.ico?v=1.2.0">
<link rel="shortcut icon" type="image/x-icon" href="/favicon.ico?v=1.2.0">
<link rel="stylesheet" href="/tvair-ui-foundation.css?v=1.2.0">
<link rel="stylesheet" href="/tvair-theme-contract.css?v=1.2.0-theme204">
<link rel="stylesheet" href="/tvair-ui-modules.css?v=1.2.0-modules205">
<link rel="stylesheet" href="/tvair-notification.css?v=1.2.0-owner203">
<link rel="stylesheet" href="/tvair-generated-surfaces.css?v=1.2.0-css-final199">
<script src="/tvair-theme.js?v=1.2.0"></script>
</head>
<body class="tvair-non-program-page tvair-plugin-shell-page {{{{themeClass}}}}{{{{contentOnlyClass}}}}" data-plugin-route="{{{{safeRoute}}}}" data-theme="{{{{safeEffectiveTheme}}}}" data-tvair-theme="{{{{safeSelectedTheme}}}}" data-tvair-selected-theme="{{{{safeSelectedTheme}}}}" data-tvair-effective-theme="{{{{safeEffectiveTheme}}}}" data-tvair-theme-scope="all">
<div id="nav">
  <a class="nav-btn" href="/" title="番組表">番組表</a>
  <a class="nav-btn" href="/reservations.html" title="予約リスト">予約リスト</a>
  <a class="nav-btn" href="/keyword.html" title="キーワード検索">キーワード検索</a>
  <a class="nav-btn" href="/program-rules.html" title="自動検索予約">自動検索予約</a>
  <a class="nav-btn" href="/new-reservation.html" title="プログラム予約">プログラム予約</a>
  <div class="spacer"></div>
  <span class="nav-brand-text">TvAIr</span>
  <div id="menu-wrap">
    <button id="menu-btn" type="button" data-tvair-menu-entry="hamburger" aria-controls="menu-dropdown">&#9776;</button>
    <div id="menu-dropdown" data-tvair-menu-host="1"></div>
  </div>
</div>
<div class="plugin-shell-content">
  <main class="plugin-shell-inner">
{{{{pluginBody}}}}
  </main>
</div>
<script src="/tvair-notification.js?v=1.2.0"></script>
<script src="/tvair-epg-run-contract.js?v=1.2.0"></script>
<script src="/tvair-safe-event-host.js?v=1.2.0"></script>
<script src="/tvair-menu-spine.js?v=1.2.0"></script>
<script>
function tvairAppendHidden(form,name,value){if(!name||value==null||value==='')return;var i=document.createElement('input');i.type='hidden';i.name=name;i.value=String(value);form.appendChild(i);}
function tvairGetAttr(el,name){try{return el&&el.getAttribute?el.getAttribute(name)||'':'';}catch(_){return '';} }
function tvairHasToken(value, token){return ((' '+(value||'')+' ').indexOf(' '+token+' '))>=0;}
function tvairTagName(el){try{return el&&el.tagName?String(el.tagName).toLowerCase():'';}catch(_){return '';} }
function tvairCurrentWindowId(){
  var q=location.search||'';var m=q.match(/[?&]__tvairWindowId=([^&]+)/)||q.match(/[?&]windowId=([^&]+)/);
  return m?decodeURIComponent(m[1].replace(/\+/g,' ')):'';
}
function tvairCurrentRevision(){var q=location.search||'';var m=q.match(/[?&]_tvairWindowRevision=([^&]+)/);return m?decodeURIComponent(m[1].replace(/\+/g,' ')):'';}
function tvairCountSafeEventCandidates(){try{var all=document.getElementsByTagName('*');var count=0;for(var i=0;i<all.length;i++){if(tvairGetAttr(all[i],'data-tvair-action'))count++;}return count;}catch(_){return -1;}}
function tvairIsReservedPayloadKey(name){var n=String(name||'').toLowerCase();return n==='action'||n==='pluginid'||n==='routesegment'||n==='route'||n==='token'||n==='actiontoken'||n==='responsemode'||n==='windowid';}
function tvairCollectFormValues(el,form){var mode=tvairGetAttr(el,'data-tvair-form-capture');if(!mode)return;var scope=null;if(mode==='closestForm'){var n=el;while(n&&n!==document){if(tvairTagName(n)==='form'){scope=n;break;}n=n.parentNode;}}else if(mode.charAt(0)==='#'){scope=document.getElementById(mode.substring(1));}if(!scope||!scope.elements)return;for(var i=0;i<scope.elements.length;i++){var item=scope.elements[i];if(!item||!item.name||item.disabled||tvairIsReservedPayloadKey(item.name))continue;var type=String(item.type||'').toLowerCase();if((type==='checkbox'||type==='radio')&&!item.checked)continue;tvairAppendHidden(form,item.name,item.value);}}
function tvairCountSafeEventCandidates(){try{var all=document.getElementsByTagName('*');var count=0;for(var i=0;i<all.length;i++){if(tvairGetAttr(all[i],'data-tvair-action'))count++;}return count;}catch(_){return -1;}}
function tvairNewInteractionId(){return 'ix-'+String((new Date()).getTime())+'-'+String(Math.floor(Math.random()*1000000000));}
function tvairClientBeacon(phase,eventName,action,el){ {{{{developerBeaconBody}}}} }
function tvairFindSafeEventTarget(start,eventName){
  var n=start;
  while(n&&n!==document){
    if(n.getAttribute&&tvairGetAttr(n,'data-tvair-action')){
      var events=tvairGetAttr(n,'data-tvair-event');
      if(tvairHasToken(events,eventName))return n;
      if(eventName==='click'&&tvairHasToken(events,'dblclick')&&tvairGetAttr(n,'data-tvair-click-fallback')==='true')return n;
    }
    n=n.parentNode;
  }
  return null;
}
function tvairFindHoverTarget(start){
  var n=start;
  while(n&&n!==document){
    if(n.getAttribute&&tvairGetAttr(n,'data-tvair-hover-key'))return n;
    n=n.parentNode;
  }
  return null;
}
function tvairNodeInside(root,node){try{while(node&&node!==document){if(node===root)return true;node=node.parentNode;}}catch(_){}return false;}
function tvairDispatchRuntimeHover(el,state){
  if(!el)return;
  var detail={state:state,hoverKey:tvairGetAttr(el,'data-tvair-hover-key')||'',pluginId:tvairGetAttr(el,'data-tvair-plugin-id')||'',routeSegment:tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||''};
  try{
    var ev=null;
    try{
      ev=document.createEvent('CustomEvent');
      if(ev.initCustomEvent)ev.initCustomEvent('tvair-runtime-hover',true,false,detail);
    }catch(_){}
    if(!ev||!ev.initCustomEvent){
      ev=document.createEvent('Event');
      ev.initEvent('tvair-runtime-hover',true,false);
      ev.detail=detail;
    }
    if(el.dispatchEvent)el.dispatchEvent(ev);
  }catch(_){}
}
function tvairHandleRuntimeHover(e,state){
  e=e||window.event;
  var target=e.target||e.srcElement;
  var el=tvairFindHoverTarget(target);
  if(!el)return;
  var related=state==='enter'?(e.relatedTarget||e.fromElement):(e.relatedTarget||e.toElement);
  if(related&&tvairNodeInside(el,related))return;
  tvairDispatchRuntimeHover(el,state);
}
function tvairPreserveToolWindowLink(href){
  if(!href)return href;
  var route=document.body.getAttribute('data-plugin-route')||'';
  var currentWindow=tvairCurrentWindowId();
  if(!currentWindow||!route)return href;
  var a=document.createElement('a');a.href=href;
  if(a.pathname!=='/plugin/'+route)return href;
  if(a.search.indexOf('__tvairWindowId=')<0)a.search+=(a.search?'&':'?')+'__tvairWindowId='+encodeURIComponent(currentWindow);
  if(a.search.indexOf('__tvairHostWindow=')<0)a.search+='&__tvairHostWindow=1';
  var rev=tvairCurrentRevision(); if(rev&&a.search.indexOf('_tvairWindowRevision=')<0)a.search+='&_tvairWindowRevision='+encodeURIComponent(rev);
  return a.pathname+a.search+a.hash;
}
function tvairTryBeginRepeatPolicy(el,eventName,action){
  var policy=String(tvairGetAttr(el,'data-tvair-repeat-policy')||'').toLowerCase();
  if(policy!=='suppressburst')return true;
  var windowMs=parseInt(tvairGetAttr(el,'data-tvair-burst-window-ms')||'0',10);
  if(!isFinite(windowMs)||windowMs<=0)return true;
  windowMs=Math.max(50,Math.min(1000,windowMs));
  var now=(new Date()).getTime();
  var key=String(eventName||'')+'|'+String(action||'');
  var guard=el.__tvairBurstGuard;
  if(guard&&guard.key===key&&now<guard.until){
    var suppressedToken=String(now)+'|'+String(Math.random());
    guard.until=now+windowMs;
    guard.token=suppressedToken;
    window.setTimeout(function(){try{var current=el.__tvairBurstGuard;if(current&&current.token===suppressedToken)delete el.__tvairBurstGuard;}catch(_){}},windowMs+1);
    tvairClientBeacon('burst_suppressed',eventName,action,el);
    return false;
  }
  var token=String(now)+'|'+String(Math.random());
  el.__tvairBurstGuard={key:key,until:now+windowMs,token:token};
  window.setTimeout(function(){try{var current=el.__tvairBurstGuard;if(current&&current.token===token)delete el.__tvairBurstGuard;}catch(_){}},windowMs+1);
  return true;
}
function tvairApplyAcceptedButtonState(el,action,eventName){
  if(!el||tvairTagName(el)!=='button')return;
  var acceptedLabel=tvairGetAttr(el,'data-tvair-accepted-label');
  if(!acceptedLabel)return;
  el.setAttribute('aria-label',acceptedLabel);
  try{el.innerText=acceptedLabel;}catch(_){try{el.textContent=acceptedLabel;}catch(__){}}
  tvairClientBeacon('accepted_button_state_applied',eventName,action,el);
}
function tvairFeedbackEnabled(el){return String(tvairGetAttr(el,'data-tvair-feedback')||'').toLowerCase()==='true';}
function tvairFeedbackSetLabel(el,label){if(!el||!label)return;el.setAttribute('aria-label',label);try{el.innerText=label;}catch(_){try{el.textContent=label;}catch(__){} } }
function tvairBeginActionFeedback(el,eventName,action,correlationId){
  if(!tvairFeedbackEnabled(el))return true;
  if(el.__tvairFeedbackInFlight){tvairClientBeacon('feedback_duplicate_suppressed',eventName,action,el);return false;}
  el.__tvairFeedbackInFlight=correlationId||'pending';
  if(!el.__tvairFeedbackOriginal){el.__tvairFeedbackOriginal={label:(el.innerText||el.textContent||''),aria:tvairGetAttr(el,'aria-label'),disabled:!!el.disabled};}
  if(String(tvairGetAttr(el,'data-tvair-feedback-disable-running')||'').toLowerCase()==='true'){el.disabled=true;el.setAttribute('disabled','disabled');}
  tvairFeedbackSetLabel(el,tvairGetAttr(el,'data-tvair-feedback-pending-label'));
  el.setAttribute('aria-busy','true');
  tvairClientBeacon('feedback_running',eventName,action,el);
  return true;
}
function tvairFloatingLabelKind(value){var kind=String(value||'Information').toLowerCase();return kind==='success'||kind==='warning'||kind==='error'?kind:'information';}
function tvairShowFloatingLabel(body,el,eventName,action){
  var feedback=body&&(body.feedback||body.Feedback)||{};
  var label=body&&(body.floatingLabel||body.FloatingLabel)||null;
  if(!label&&feedback&&(feedback.showFloatingLabel===false||feedback.ShowFloatingLabel===false))return;
  var message=label?(label.message||label.Message||''):(feedback.message||feedback.Message||'');
  if(!message)return;
  var correlation=String((label&&(label.correlationId||label.CorrelationId))||(feedback.correlationId||feedback.CorrelationId)||'');
  var kind=tvairFloatingLabelKind((label&&(label.kind||label.Kind))||(feedback.kind||feedback.Kind));
  var duration=parseInt((label&&(label.durationMilliseconds||label.DurationMilliseconds))||0,10)||0;
  if(duration<=0)duration=kind==='error'?3000:(kind==='warning'?2500:1800);
  if(duration<1000)duration=1000;if(duration>5000)duration=5000;
  var node=document.getElementById('tvair-host-floating-label');
  if(!node){
    node=document.createElement('div');node.id='tvair-host-floating-label';node.setAttribute('role',kind==='error'?'alert':'status');node.setAttribute('aria-live',kind==='error'?'assertive':'polite');node.setAttribute('aria-atomic','true');node.className='tvair-host-floating-label';document.body.appendChild(node);
  }node.setAttribute('data-tvair-feedback-kind',kind);node.setAttribute('data-tvair-correlation-id',correlation);node.textContent=String(message);node.classList.add('is-visible');
  if(node.__tvairHideTimer)clearTimeout(node.__tvairHideTimer);node.__tvairHideTimer=setTimeout(function(){node.classList.remove('is-visible');node.__tvairHideTimer=setTimeout(function(){if(node&&node.parentNode)node.parentNode.removeChild(node);},160);},duration);
  tvairClientBeacon('floating_label_shown',eventName,action,el);
}
function tvairCompleteActionFeedback(el,eventName,action,body,httpOk){
  if(!tvairFeedbackEnabled(el))return;
  var feedback=body&&(body.feedback||body.Feedback)||{};
  var phase=String(feedback.phase||feedback.Phase||(httpOk?'Succeeded':'Failed')).toLowerCase();
  var success=(phase==='succeeded'||phase==='success');var nochange=(phase==='nochange'||phase==='no_change');var cancelled=(phase==='cancelled'||phase==='canceled');
  var label=feedback.buttonLabel||feedback.ButtonLabel||'';
  if(!label){if(success)label=tvairGetAttr(el,'data-tvair-feedback-success-label');else if(nochange)label=tvairGetAttr(el,'data-tvair-feedback-nochange-label');else if(!cancelled)label=tvairGetAttr(el,'data-tvair-feedback-failure-label');}
  var original=el.__tvairFeedbackOriginal||{};
  if(label)tvairFeedbackSetLabel(el,label);
  var keepDisabled=(typeof feedback.keepDisabled!=='undefined')?!!feedback.keepDisabled:((typeof feedback.KeepDisabled!=='undefined')?!!feedback.KeepDisabled:false);
  if(success&&String(tvairGetAttr(el,'data-tvair-feedback-keep-disabled-success')||'').toLowerCase()==='true')keepDisabled=true;
  if(!success&&!nochange&&String(tvairGetAttr(el,'data-tvair-feedback-restore-failure')||'').toLowerCase()==='true'){tvairFeedbackSetLabel(el,original.label||'');if(original.aria)el.setAttribute('aria-label',original.aria);else el.removeAttribute('aria-label');}
  if(keepDisabled){el.disabled=true;el.setAttribute('disabled','disabled');}else{el.disabled=!!original.disabled;if(original.disabled)el.setAttribute('disabled','disabled');else el.removeAttribute('disabled');}
  el.removeAttribute('aria-busy');delete el.__tvairFeedbackInFlight;
  tvairShowFloatingLabel(body,el,eventName,action);
  tvairClientBeacon(success?'feedback_succeeded':(nochange?'feedback_nochange':(cancelled?'feedback_cancelled':'feedback_failed')),eventName,action,el);
}
function tvairApplyUiPatches(patches){if(!patches||typeof patches.length==='undefined')return;for(var i=0;i<patches.length&&i<64;i++){var p=patches[i]||{};var id=p.elementId||p.ElementId||'';if(!id)continue;var target=document.getElementById(id);if(!target)continue;var text=(typeof p.textContent!=='undefined')?p.textContent:p.TextContent;if(text!==null&&typeof text!=='undefined'){try{target.textContent=String(text);}catch(_){target.innerText=String(text);}}var cls=(typeof p.className!=='undefined')?p.className:p.ClassName;if(cls!==null&&typeof cls!=='undefined')target.className=String(cls);var remove=p.removeClasses||p.RemoveClasses||[];for(var r=0;r<remove.length;r++){var rc=String(remove[r]||'');if(!rc)continue;if(target.classList)target.classList.remove(rc);else target.className=(' '+target.className+' ').replace(' '+rc+' ',' ').replace(/^\s+|\s+$/g,'');}var add=p.addClasses||p.AddClasses||[];for(var a=0;a<add.length;a++){var ac=String(add[a]||'');if(!ac)continue;if(target.classList)target.classList.add(ac);else if((' '+target.className+' ').indexOf(' '+ac+' ')<0)target.className=(target.className?target.className+' ':'')+ac;}var disabled=(typeof p.disabled!=='undefined')?p.disabled:p.Disabled;if(disabled!==null&&typeof disabled!=='undefined'){target.disabled=!!disabled;if(disabled)target.setAttribute('disabled','disabled');else target.removeAttribute('disabled');}var hidden=(typeof p.hidden!=='undefined')?p.hidden:p.Hidden;if(hidden!==null&&typeof hidden!=='undefined'){target.hidden=!!hidden;if(hidden)target.setAttribute('hidden','hidden');else target.removeAttribute('hidden');}var checked=(typeof p.checked!=='undefined')?p.checked:p.Checked;if(checked!==null&&typeof checked!=='undefined'){target.checked=!!checked;if(checked)target.setAttribute('checked','checked');else target.removeAttribute('checked');}var value=(typeof p.value!=='undefined')?p.value:p.Value;if(value!==null&&typeof value!=='undefined')target.value=String(value);var attrs=p.attributes||p.Attributes||{};for(var name in attrs){if(!Object.prototype.hasOwnProperty.call(attrs,name))continue;var lower=String(name).toLowerCase();if(!(lower==='title'||lower.indexOf('aria-')===0||lower.indexOf('data-')===0))continue;var attrValue=attrs[name];if(attrValue===null||typeof attrValue==='undefined')target.removeAttribute(name);else target.setAttribute(name,String(attrValue));} } }
function tvairApplyUiPatchesJson(json){try{var patches=JSON.parse(String(json||'[]'));tvairApplyUiPatches(patches);var applied=0;if(patches&&typeof patches.length!=='undefined'){for(var i=0;i<patches.length&&i<64;i++){var p=patches[i]||{};var id=p.elementId||p.ElementId||'';if(id&&document.getElementById(id))applied++;}}return applied;}catch(_){return -1;}}
function tvairPageRefreshStateKey(){try{var search=String(location.search||'');search=search.replace(/([?&])_tvairPageRefresh=[^&]*&?/ig,function(_,sep){return sep==='?'?'?':'';});search=search.replace(/\?&/g,'?').replace(/[?&]$/,'');return 'tvair-page-refresh:'+location.pathname+search;}catch(_){return '';}}function tvairCapturePageRefreshState(){try{var key=tvairPageRefreshStateKey();if(!key)return;var state={x:window.pageXOffset||document.documentElement.scrollLeft||0,y:window.pageYOffset||document.documentElement.scrollTop||0};sessionStorage.setItem(key,JSON.stringify(state));}catch(_){}}function tvairRestorePageRefreshState(){try{var key=tvairPageRefreshStateKey();if(!key)return;var raw=sessionStorage.getItem(key);if(!raw)return;sessionStorage.removeItem(key);var state=JSON.parse(raw);window.scrollTo(Number(state.x)||0,Number(state.y)||0);}catch(_){}}function tvairInteractionStateKey(){var id=tvairCurrentWindowId();return id?'tvair-interaction-state:'+id:'';}function tvairCaptureInteractionState(){try{var key=tvairInteractionStateKey();if(!key)return;var a=document.activeElement;var state={x:window.pageXOffset||document.documentElement.scrollLeft||0,y:window.pageYOffset||document.documentElement.scrollTop||0,id:a&&a.id?a.id:'',start:(a&&typeof a.selectionStart==='number')?a.selectionStart:null,end:(a&&typeof a.selectionEnd==='number')?a.selectionEnd:null};sessionStorage.setItem(key,JSON.stringify(state));}catch(_){}}function tvairRestoreInteractionState(){try{var key=tvairInteractionStateKey();if(!key)return;var raw=sessionStorage.getItem(key);if(!raw)return;sessionStorage.removeItem(key);var state=JSON.parse(raw);window.scrollTo(Number(state.x)||0,Number(state.y)||0);if(state.id){var a=document.getElementById(state.id);if(a&&a.focus){a.focus();if(typeof a.setSelectionRange==='function'&&state.start!==null)a.setSelectionRange(state.start,state.end===null?state.start:state.end);}}}catch(_){}}function tvairRestoreRefreshState(){tvairRestoreInteractionState();tvairRestorePageRefreshState();}if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',tvairRestoreRefreshState);else setTimeout(tvairRestoreRefreshState,0);function tvairSubmitSafeAction(el,eventName){
  var action=tvairGetAttr(el,'data-tvair-action');
  var interactionId=tvairNewInteractionId();el.setAttribute('data-tvair-debug-interaction-id',interactionId);
  tvairClientBeacon('click_captured',eventName,action,el);tvairClientBeacon('received_before_validate',eventName,action,el);
  if(!action){tvairClientBeacon('denied_missing_action',eventName,action,el);return;}
  var confirmMessage=tvairGetAttr(el,'data-tvair-confirm-message');
  if(confirmMessage){var confirmed=false;try{confirmed=window.confirm(String(confirmMessage));}catch(_){confirmed=false;}if(!confirmed){tvairClientBeacon('action_cancelled_by_user',eventName,action,el);return;}}
  if(!tvairTryBeginRepeatPolicy(el,eventName,action))return;
  if(!tvairBeginActionFeedback(el,eventName,action,interactionId))return;
  if(!tvairFeedbackEnabled(el))tvairApplyAcceptedButtonState(el,action,eventName);
  var isWindowAction=(action==='refreshWindow'||action==='updateWindow'||action==='closeWindow'||action==='rerenderWindow'||action==='openWindow');
  var form=document.createElement('form');form.method='post';form.action=isWindowAction?'/api/plugins/window':'/api/plugins/action';
  tvairAppendHidden(form,'action',action);
  tvairAppendHidden(form,'pluginId',tvairGetAttr(el,'data-tvair-plugin-id'));
  tvairAppendHidden(form,'routeSegment',tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'');
  tvairAppendHidden(form,'token',tvairGetAttr(el,'data-tvair-token')||tvairGetAttr(el,'data-tvair-action-token'));
  tvairAppendHidden(form,'actionToken',tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'));
  tvairAppendHidden(form,'responseMode',tvairGetAttr(el,'data-tvair-response-mode')||'hostHandled');
  tvairAppendHidden(form,'windowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());
  tvairAppendHidden(form,'target',tvairGetAttr(el,'data-tvair-target')||'content');
  tvairAppendHidden(form,'refreshTarget',tvairGetAttr(el,'data-tvair-refresh-target')||'content');
  tvairAppendHidden(form,'preserveScroll',tvairGetAttr(el,'data-tvair-preserve-scroll')||'true');
  tvairAppendHidden(form,'safeEvent',eventName||'unknown');
  tvairAppendHidden(form,'safeEventAction',action);
  tvairAppendHidden(form,'safeEventSource','host-script-no-plugin-js');
  tvairAppendHidden(form,'safeEventInteractionId',interactionId);
  tvairAppendHidden(form,'safeEventWindowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());
  tvairAppendHidden(form,'feedbackRequested',tvairFeedbackEnabled(el)?'true':'false');
  tvairCollectFormValues(el,form);
  if(el.attributes){
    for(var i=0;i<el.attributes.length;i++){
      var a=el.attributes[i];
      if(a&&a.name&&a.name.indexOf('data-tvair-payload-')===0){var pk=a.name.substring('data-tvair-payload-'.length);if(!tvairIsReservedPayloadKey(pk))tvairAppendHidden(form,pk,a.value);}
    }
  }
  el.setAttribute('data-tvair-debug-payload-count',String(form.elements.length));tvairClientBeacon('payload_built',eventName,action,el);document.body.appendChild(form);
  tvairClientBeacon('post_started',eventName,action,el);tvairClientBeacon('posting_form',eventName,action,el);
  var submitResponseMode=tvairGetAttr(el,'data-tvair-response-mode')||'hostHandled';if(submitResponseMode==='hostHandled'||submitResponseMode==='noContent'||submitResponseMode==='patchWindow'){
    try{
      var pairs=[];
      for(var j=0;j<form.elements.length;j++){var it=form.elements[j];if(it&&it.name)pairs.push(encodeURIComponent(it.name)+'='+encodeURIComponent(it.value||''));}
      var endpoint=isWindowAction?'/api/plugins/window':'/api/plugins/action';var resolvedEndpoint=location.protocol+'//'+location.host+endpoint;el.setAttribute('data-tvair-debug-endpoint',resolvedEndpoint);var xhr=new XMLHttpRequest();
      xhr.open('POST',resolvedEndpoint,true);
      xhr.setRequestHeader('Content-Type','application/x-www-form-urlencoded; charset=UTF-8');var requestInteractionId=interactionId;xhr.onreadystatechange=function(){if(xhr.readyState===4){var previousInteractionId=tvairGetAttr(el,'data-tvair-debug-interaction-id');el.setAttribute('data-tvair-debug-interaction-id',requestInteractionId);el.setAttribute('data-tvair-debug-status',String(xhr.status||0));var body={};try{body=JSON.parse(xhr.responseText||'{}');}catch(_){if(submitResponseMode==='patchWindow'||tvairFeedbackEnabled(el))el.setAttribute('data-tvair-debug-reason','response_parse_failed');}if(xhr.status>=200&&xhr.status<400&&submitResponseMode==='patchWindow')tvairApplyUiPatches(body&&body.uiPatches?body.uiPatches:[]);var requestSucceeded=xhr.status>=200&&xhr.status<400;tvairCompleteActionFeedback(el,eventName,action,body,requestSucceeded);tvairClientBeacon(requestSucceeded?'post_completed':'post_failed',eventName,action,el);el.setAttribute('data-tvair-debug-interaction-id',previousInteractionId);var refreshSurface='';var preservePageScroll=false;var pageRefreshLocation='';try{refreshSurface=String(xhr.getResponseHeader('X-TvAIr-Refresh-Surface')||'').toLowerCase();preservePageScroll=String(xhr.getResponseHeader('X-TvAIr-Preserve-Scroll')||'').toLowerCase()==='true';pageRefreshLocation=String(xhr.getResponseHeader('X-TvAIr-Refresh-Location')||'');}catch(_){}if(requestSucceeded&&refreshSurface==='page'){if(preservePageScroll)tvairCapturePageRefreshState();tvairClientBeacon('page_refresh_issued',eventName,action,el);if(pageRefreshLocation){location.href=pageRefreshLocation;}else{location.href=location.pathname+location.search;}return;}}};xhr.onerror=function(){var previousInteractionId=tvairGetAttr(el,'data-tvair-debug-interaction-id');el.setAttribute('data-tvair-debug-interaction-id',requestInteractionId);el.setAttribute('data-tvair-debug-reason','xhr_error');tvairCompleteActionFeedback(el,eventName,action,{},false);tvairClientBeacon('post_failed',eventName,action,el);el.setAttribute('data-tvair-debug-interaction-id',previousInteractionId);};
      xhr.send(pairs.join('&'));
      return;
    }catch(_){ }
  }
  form.submit();
}
function tvairHandleSafeEvent(e,eventName){
  e=e||window.event;
  var target=e.target||e.srcElement;
  var el=tvairFindSafeEventTarget(target,eventName);
  if(!el)return true;
  try{if(e.preventDefault)e.preventDefault();e.returnValue=false;}catch(_){ }
  tvairSubmitSafeAction(el,eventName);
  return false;
}
function tvairBindSafeEvents(){
  if(window.__tvairSafeEventBound){tvairClientBeacon('bind_skip_already_bound','','',document.body);return;}
  window.__tvairSafeEventBound=true;
  tvairClientBeacon('bind_start','','',document.body);
  if(document.addEventListener){
    document.addEventListener('dblclick',function(e){return tvairHandleSafeEvent(e,'dblclick');},false);
    document.addEventListener('mouseover',function(e){tvairHandleRuntimeHover(e,'enter');},false);
    document.addEventListener('mouseout',function(e){tvairHandleRuntimeHover(e,'leave');},false);
    document.addEventListener('click',function(e){
      if(tvairHandleSafeEvent(e,'click')===false)return false;
      var a=e.target;while(a&&a!==document&&!(a.tagName&&String(a.tagName).toLowerCase()==='a'))a=a.parentNode;
      if(a&&a.href&&document.body.className.indexOf('tvair-plugin-toolwindow-content-only')>=0){
        var next=tvairPreserveToolWindowLink(a.getAttribute('href')||'');
        if(next&&(next!==a.getAttribute('href'))){if(e.preventDefault)e.preventDefault();location.href=next;}
      }
    },false);
  }else if(document.attachEvent){
    document.attachEvent('ondblclick',function(){return tvairHandleSafeEvent(window.event,'dblclick');});
    document.attachEvent('onmouseover',function(){tvairHandleRuntimeHover(window.event,'enter');});
    document.attachEvent('onmouseout',function(){tvairHandleRuntimeHover(window.event,'leave');});
    document.attachEvent('onclick',function(){return tvairHandleSafeEvent(window.event,'click');});
  }else{
    var oldDbl=document.ondblclick;document.ondblclick=function(e){if(tvairHandleSafeEvent(e||window.event,'dblclick')===false)return false;return oldDbl?oldDbl(e):true;};
    var oldOver=document.onmouseover;document.onmouseover=function(e){tvairHandleRuntimeHover(e||window.event,'enter');return oldOver?oldOver(e):true;};
    var oldOut=document.onmouseout;document.onmouseout=function(e){tvairHandleRuntimeHover(e||window.event,'leave');return oldOut?oldOut(e):true;};
    var oldClick=document.onclick;document.onclick=function(e){if(tvairHandleSafeEvent(e||window.event,'click')===false)return false;return oldClick?oldClick(e):true;};
  }
  tvairClientBeacon('bind_complete','','',document.body);
}
function tvairFindActionTokenElement(){try{return document.querySelector?document.querySelector('[data-tvair-action-token],[data-tvair-token]'):null;}catch(_){return null;}}
function tvairActionTokenIdentity(){try{var el=tvairFindActionTokenElement();if(!el)return null;var token=tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token');var pluginId=tvairGetAttr(el,'data-tvair-plugin-id');var route=tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'';if(!token||!pluginId||!route)return null;return{token:token,pluginId:pluginId,route:route};}catch(_){return null;}}
function tvairRenewActionToken(){try{var id=tvairActionTokenIdentity();if(!id)return;var xhr=new XMLHttpRequest();xhr.open('POST','/api/plugins/action-token/renew',true);xhr.setRequestHeader('Content-Type','application/x-www-form-urlencoded; charset=UTF-8');xhr.send('actionToken='+encodeURIComponent(id.token)+'&pluginId='+encodeURIComponent(id.pluginId)+'&routeSegment='+encodeURIComponent(id.route));}catch(_){}}
function tvairRecoverExpiredPageToken(){try{if(window.__tvairPageTokenRecoveryInFlight)return;var id=tvairActionTokenIdentity();if(!id)return;window.__tvairPageTokenRecoveryInFlight=true;var xhr=new XMLHttpRequest();xhr.open('POST','/api/plugins/action-token/validate',true);xhr.setRequestHeader('Content-Type','application/x-www-form-urlencoded; charset=UTF-8');xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;window.__tvairPageTokenRecoveryInFlight=false;if(xhr.status>=200&&xhr.status<300)return;var path='/plugin/'+encodeURIComponent(id.route);var query=window.location&&window.location.search?window.location.search:'';window.location.replace(path+query);};xhr.onerror=function(){window.__tvairPageTokenRecoveryInFlight=false;};xhr.send('actionToken='+encodeURIComponent(id.token)+'&pluginId='+encodeURIComponent(id.pluginId)+'&routeSegment='+encodeURIComponent(id.route));}catch(_){window.__tvairPageTokenRecoveryInFlight=false;}}
function tvairStartActionTokenKeepalive(){try{if(window.__tvairActionTokenKeepaliveStarted)return;window.__tvairActionTokenKeepaliveStarted=true;tvairRenewActionToken();window.setInterval(tvairRenewActionToken,300000);if(document.addEventListener)document.addEventListener('visibilitychange',function(){if(!document.hidden)tvairRecoverExpiredPageToken();},false);if(window.addEventListener){window.addEventListener('focus',tvairRecoverExpiredPageToken,false);window.addEventListener('pageshow',tvairRecoverExpiredPageToken,false);}else if(window.attachEvent)window.attachEvent('onfocus',tvairRecoverExpiredPageToken);}catch(_){}}
function tvairBootSafeEvents(){try{tvairBindSafeEvents();tvairStartActionTokenKeepalive();}catch(_){tvairClientBeacon('bind_failed','','',document.body);}}if(document.readyState==='complete'||document.readyState==='interactive'){tvairBootSafeEvents();}else if(window.attachEvent){window.attachEvent('onload',tvairBootSafeEvents);}else if(window.addEventListener){window.addEventListener('load',tvairBootSafeEvents,false);}else{window.onload=tvairBootSafeEvents;}</script>

</body>
</html>
""";
}

static string BuildPluginToolWindowContentHtml(string title, string route, string pluginBody, string selectedTheme, string effectiveTheme)
{
    var safeTitle = System.Net.WebUtility.HtmlEncode(title);
    var safeRoute = System.Net.WebUtility.HtmlEncode(route);
    var safeSelectedTheme = System.Net.WebUtility.HtmlEncode(IniSettingsService.NormalizeSystemTheme(selectedTheme));
    var safeEffectiveTheme = System.Net.WebUtility.HtmlEncode(string.Equals(effectiveTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light");
    var themeClass = string.Equals(safeEffectiveTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "tvair-theme-dark theme-dark" : "tvair-theme-light theme-light";
    var normalized = NormalizePluginToolWindowContent(pluginBody, route);
    var pluginHead = normalized.Head;
    var pluginContent = normalized.Body;
#if TVAIR_DEVELOPER_DIAGNOSTICS
    var developerBeaconBody = """try{var img=new Image();var networkId=tvairGetAttr(el,'data-tvair-payload-networkId')||tvairGetAttr(el,'data-tvair-payload-nid');var tsid=tvairGetAttr(el,'data-tvair-payload-transportStreamId')||tvairGetAttr(el,'data-tvair-payload-tsid');var sid=tvairGetAttr(el,'data-tvair-payload-serviceId')||tvairGetAttr(el,'data-tvair-payload-sid');var url='/api/plugins/safe-event/client-log?phase='+encodeURIComponent(phase||'')+'&event='+encodeURIComponent(eventName||'')+'&action='+encodeURIComponent(action||'')+'&interactionId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-debug-interaction-id')||'')+'&pluginId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-plugin-id')||'')+'&routeSegment='+encodeURIComponent(tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'')+'&windowId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId()||'')+'&mode='+encodeURIComponent('directContent')+'&hostKind='+encodeURIComponent('winforms_webbrowser_fallback_direct_content')+'&tag='+encodeURIComponent(tvairTagName(el))+'&hasAction='+encodeURIComponent(action?'true':'false')+'&hasToken='+encodeURIComponent((tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'))?'true':'false')+'&hasTriplet='+encodeURIComponent((networkId&&tsid&&sid)?'true':'false')+'&_='+String(new Date().getTime());img.src=url;}catch(_){ }""";
#else
    var developerBeaconBody = "return;";
#endif
    var template = """
<!doctype html>
<html lang="ja" class="{{themeClass}}" data-theme="{{safeEffectiveTheme}}" data-tvair-theme="{{safeSelectedTheme}}" data-tvair-selected-theme="{{safeSelectedTheme}}" data-tvair-effective-theme="{{safeEffectiveTheme}}" data-tvair-theme-scope="all">
<head>
<meta charset="utf-8">
<meta http-equiv="X-UA-Compatible" content="IE=edge">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; connect-src 'self'; media-src 'self' data:">
<title>{{safeTitle}} - TvAIr Tool Window</title>
<link rel="stylesheet" href="/tvair-theme-contract.css?v=1.2.0-theme204">
<script src="/tvair-theme.js?v=1.2.0"></script>
<link rel="stylesheet" href="/tvair-generated-surfaces.css?v=1.2.0-css-final199">
{{pluginHead}}
</head>
<body class="tvair-plugin-toolwindow-content-only {{themeClass}}" data-plugin-route="{{safeRoute}}" data-theme="{{safeEffectiveTheme}}" data-tvair-theme="{{safeSelectedTheme}}" data-tvair-selected-theme="{{safeSelectedTheme}}" data-tvair-effective-theme="{{safeEffectiveTheme}}" data-tvair-theme-scope="all" data-tvair-host-kind="winforms_webbrowser_fallback_direct_content" data-tvair-toolwindow-contract="release_contract">
<div class="tvair-toolwindow-content-root">
{{pluginContent}}
</div>
<script src="/tvair-safe-event-host.js?v=1.2.0"></script>
<script>
function tvairAppendHidden(form,name,value){if(!name||value==null||value==='')return;var i=document.createElement('input');i.type='hidden';i.name=name;i.value=String(value);form.appendChild(i);}
function tvairGetAttr(el,name){try{return el&&el.getAttribute?el.getAttribute(name)||'':'';}catch(_){return '';} }
function tvairHasToken(value, token){return ((' '+(value||'')+' ').indexOf(' '+token+' '))>=0;}
function tvairTagName(el){try{return el&&el.tagName?String(el.tagName).toLowerCase():'';}catch(_){return '';} }
function tvairCurrentWindowId(){var q=location.search||'';var m=q.match(/[?&]__tvairWindowId=([^&]+)/)||q.match(/[?&]windowId=([^&]+)/);return m?decodeURIComponent(m[1].replace(/\+/g,' ')):'';}
function tvairCurrentRevision(){var q=location.search||'';var m=q.match(/[?&]_tvairWindowRevision=([^&]+)/);return m?decodeURIComponent(m[1].replace(/\+/g,' ')):'';}
function tvairIsReservedPayloadKey(name){var n=String(name||'').toLowerCase();return n==='action'||n==='pluginid'||n==='routesegment'||n==='route'||n==='token'||n==='actiontoken'||n==='responsemode'||n==='windowid';}
function tvairCollectFormValues(el,form){var mode=tvairGetAttr(el,'data-tvair-form-capture');if(!mode)return;var scope=null;if(mode==='closestForm'){var n=el;while(n&&n!==document){if(tvairTagName(n)==='form'){scope=n;break;}n=n.parentNode;}}else if(mode.charAt(0)==='#'){scope=document.getElementById(mode.substring(1));}if(!scope||!scope.elements)return;for(var i=0;i<scope.elements.length;i++){var item=scope.elements[i];if(!item||!item.name||item.disabled||tvairIsReservedPayloadKey(item.name))continue;var type=String(item.type||'').toLowerCase();if((type==='checkbox'||type==='radio')&&!item.checked)continue;tvairAppendHidden(form,item.name,item.value);}}
function tvairNewInteractionId(){return 'ix-'+String((new Date()).getTime())+'-'+String(Math.floor(Math.random()*1000000000));}function tvairClientBeacon(phase,eventName,action,el){ {{developerBeaconBody}} }
function tvairFindSafeEventTarget(start,eventName){var n=start;while(n&&n!==document){if(n.getAttribute&&tvairGetAttr(n,'data-tvair-action')){var events=tvairGetAttr(n,'data-tvair-event');if(tvairHasToken(events,eventName))return n;if(eventName==='click'&&tvairHasToken(events,'dblclick')&&tvairGetAttr(n,'data-tvair-click-fallback')==='true')return n;}n=n.parentNode;}return null;}
function tvairFindHoverTarget(start){
  var n=start;
  while(n&&n!==document){
    if(n.getAttribute&&tvairGetAttr(n,'data-tvair-hover-key'))return n;
    n=n.parentNode;
  }
  return null;
}
function tvairNodeInside(root,node){try{while(node&&node!==document){if(node===root)return true;node=node.parentNode;}}catch(_){}return false;}
function tvairDispatchRuntimeHover(el,state){
  if(!el)return;
  var detail={state:state,hoverKey:tvairGetAttr(el,'data-tvair-hover-key')||'',pluginId:tvairGetAttr(el,'data-tvair-plugin-id')||'',routeSegment:tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||''};
  try{
    var ev=null;
    try{
      ev=document.createEvent('CustomEvent');
      if(ev.initCustomEvent)ev.initCustomEvent('tvair-runtime-hover',true,false,detail);
    }catch(_){}
    if(!ev||!ev.initCustomEvent){
      ev=document.createEvent('Event');
      ev.initEvent('tvair-runtime-hover',true,false);
      ev.detail=detail;
    }
    if(el.dispatchEvent)el.dispatchEvent(ev);
  }catch(_){}
}
function tvairHandleRuntimeHover(e,state){
  e=e||window.event;
  var target=e.target||e.srcElement;
  var el=tvairFindHoverTarget(target);
  if(!el)return;
  var related=state==='enter'?(e.relatedTarget||e.fromElement):(e.relatedTarget||e.toElement);
  if(related&&tvairNodeInside(el,related))return;
  tvairDispatchRuntimeHover(el,state);
}
function tvairPreserveToolWindowLink(href){if(!href)return href;var route=document.body.getAttribute('data-plugin-route')||'';var currentWindow=tvairCurrentWindowId();if(!currentWindow||!route)return href;var a=document.createElement('a');a.href=href;if(a.pathname!=='/plugin/'+route)return href;if(a.search.indexOf('__tvairWindowId=')<0)a.search+=(a.search?'&':'?')+'__tvairWindowId='+encodeURIComponent(currentWindow);if(a.search.indexOf('__tvairHostWindow=')<0)a.search+='&__tvairHostWindow=1';var rev=tvairCurrentRevision(); if(rev&&a.search.indexOf('_tvairWindowRevision=')<0)a.search+='&_tvairWindowRevision='+encodeURIComponent(rev);return a.pathname+a.search+a.hash;}
function tvairTryBeginRepeatPolicy(el,eventName,action){var policy=String(tvairGetAttr(el,'data-tvair-repeat-policy')||'').toLowerCase();if(policy!=='suppressburst')return true;var windowMs=parseInt(tvairGetAttr(el,'data-tvair-burst-window-ms')||'0',10);if(!isFinite(windowMs)||windowMs<=0)return true;windowMs=Math.max(50,Math.min(1000,windowMs));var now=(new Date()).getTime();var key=String(eventName||'')+'|'+String(action||'');var guard=el.__tvairBurstGuard;if(guard&&guard.key===key&&now<guard.until){var suppressedToken=String(now)+'|'+String(Math.random());guard.until=now+windowMs;guard.token=suppressedToken;window.setTimeout(function(){try{var current=el.__tvairBurstGuard;if(current&&current.token===suppressedToken)delete el.__tvairBurstGuard;}catch(_){}},windowMs+1);tvairClientBeacon('burst_suppressed',eventName,action,el);return false;}var token=String(now)+'|'+String(Math.random());el.__tvairBurstGuard={key:key,until:now+windowMs,token:token};window.setTimeout(function(){try{var current=el.__tvairBurstGuard;if(current&&current.token===token)delete el.__tvairBurstGuard;}catch(_){}},windowMs+1);return true;}
function tvairApplyAcceptedButtonState(el,action,eventName){if(!el||tvairTagName(el)!=='button')return;var acceptedLabel=tvairGetAttr(el,'data-tvair-accepted-label');if(!acceptedLabel)return;el.setAttribute('aria-label',acceptedLabel);try{el.innerText=acceptedLabel;}catch(_){try{el.textContent=acceptedLabel;}catch(__){}}tvairClientBeacon('accepted_button_state_applied',eventName,action,el);}
function tvairFeedbackEnabled(el){return String(tvairGetAttr(el,'data-tvair-feedback')||'').toLowerCase()==='true';}function tvairFeedbackSetLabel(el,label){if(!el||!label)return;el.setAttribute('aria-label',label);try{el.innerText=label;}catch(_){try{el.textContent=label;}catch(__){} } }function tvairBeginActionFeedback(el,eventName,action,correlationId){if(!tvairFeedbackEnabled(el))return true;if(el.__tvairFeedbackInFlight){tvairClientBeacon('feedback_duplicate_suppressed',eventName,action,el);return false;}el.__tvairFeedbackInFlight=correlationId||'pending';if(!el.__tvairFeedbackOriginal)el.__tvairFeedbackOriginal={label:(el.innerText||el.textContent||''),aria:tvairGetAttr(el,'aria-label'),disabled:!!el.disabled};if(String(tvairGetAttr(el,'data-tvair-feedback-disable-running')||'').toLowerCase()==='true'){el.disabled=true;el.setAttribute('disabled','disabled');}tvairFeedbackSetLabel(el,tvairGetAttr(el,'data-tvair-feedback-pending-label'));el.setAttribute('aria-busy','true');tvairClientBeacon('feedback_running',eventName,action,el);return true;}function tvairFloatingLabelKind(value){var kind=String(value||'Information').toLowerCase();return kind==='success'||kind==='warning'||kind==='error'?kind:'information';}function tvairShowFloatingLabel(body,el,eventName,action){var feedback=body&&(body.feedback||body.Feedback)||{};var label=body&&(body.floatingLabel||body.FloatingLabel)||null;if(!label&&feedback&&(feedback.showFloatingLabel===false||feedback.ShowFloatingLabel===false))return;var message=label?(label.message||label.Message||''):(feedback.message||feedback.Message||'');if(!message)return;var correlation=String((label&&(label.correlationId||label.CorrelationId))||(feedback.correlationId||feedback.CorrelationId)||'');var kind=tvairFloatingLabelKind((label&&(label.kind||label.Kind))||(feedback.kind||feedback.Kind));var duration=parseInt((label&&(label.durationMilliseconds||label.DurationMilliseconds))||0,10)||0;if(duration<=0)duration=kind==='error'?3000:(kind==='warning'?2500:1800);if(duration<1000)duration=1000;if(duration>5000)duration=5000;var node=document.getElementById('tvair-host-floating-label');if(!node){node=document.createElement('div');node.id='tvair-host-floating-label';node.setAttribute('role',kind==='error'?'alert':'status');node.setAttribute('aria-live',kind==='error'?'assertive':'polite');node.setAttribute('aria-atomic','true');node.className='tvair-host-floating-label';document.body.appendChild(node);}node.setAttribute('data-tvair-feedback-kind',kind);node.setAttribute('data-tvair-correlation-id',correlation);node.innerText=String(message);node.classList.add('is-visible');if(node.__tvairHideTimer)clearTimeout(node.__tvairHideTimer);node.__tvairHideTimer=setTimeout(function(){if(node&&node.parentNode)node.parentNode.removeChild(node);},duration);tvairClientBeacon('floating_label_shown',eventName,action,el);} function tvairCompleteActionFeedback(el,eventName,action,body,httpOk){if(!tvairFeedbackEnabled(el))return;var feedback=body&&(body.feedback||body.Feedback)||{};var phase=String(feedback.phase||feedback.Phase||(httpOk?'Succeeded':'Failed')).toLowerCase();var success=phase==='succeeded'||phase==='success';var nochange=phase==='nochange'||phase==='no_change';var cancelled=phase==='cancelled'||phase==='canceled';var label=feedback.buttonLabel||feedback.ButtonLabel||'';if(!label){if(success)label=tvairGetAttr(el,'data-tvair-feedback-success-label');else if(nochange)label=tvairGetAttr(el,'data-tvair-feedback-nochange-label');else if(!cancelled)label=tvairGetAttr(el,'data-tvair-feedback-failure-label');}var original=el.__tvairFeedbackOriginal||{};if(label)tvairFeedbackSetLabel(el,label);var keepDisabled=(typeof feedback.keepDisabled!=='undefined')?!!feedback.keepDisabled:((typeof feedback.KeepDisabled!=='undefined')?!!feedback.KeepDisabled:false);if(success&&String(tvairGetAttr(el,'data-tvair-feedback-keep-disabled-success')||'').toLowerCase()==='true')keepDisabled=true;if(!success&&!nochange&&String(tvairGetAttr(el,'data-tvair-feedback-restore-failure')||'').toLowerCase()==='true'){tvairFeedbackSetLabel(el,original.label||'');if(original.aria)el.setAttribute('aria-label',original.aria);else el.removeAttribute('aria-label');}if(keepDisabled){el.disabled=true;el.setAttribute('disabled','disabled');}else{el.disabled=!!original.disabled;if(original.disabled)el.setAttribute('disabled','disabled');else el.removeAttribute('disabled');}el.removeAttribute('aria-busy');delete el.__tvairFeedbackInFlight;tvairShowFloatingLabel(body,el,eventName,action);tvairClientBeacon(success?'feedback_succeeded':(nochange?'feedback_nochange':(cancelled?'feedback_cancelled':'feedback_failed')),eventName,action,el);}
function tvairApplyUiPatches(patches){if(!patches||typeof patches.length==='undefined')return;for(var i=0;i<patches.length&&i<64;i++){var p=patches[i]||{};var id=p.elementId||p.ElementId||'';if(!id)continue;var target=document.getElementById(id);if(!target)continue;var text=(typeof p.textContent!=='undefined')?p.textContent:p.TextContent;if(text!==null&&typeof text!=='undefined'){try{target.textContent=String(text);}catch(_){target.innerText=String(text);}}var cls=(typeof p.className!=='undefined')?p.className:p.ClassName;if(cls!==null&&typeof cls!=='undefined')target.className=String(cls);var remove=p.removeClasses||p.RemoveClasses||[];for(var r=0;r<remove.length;r++){var rc=String(remove[r]||'');if(!rc)continue;if(target.classList)target.classList.remove(rc);else target.className=(' '+target.className+' ').replace(' '+rc+' ',' ').replace(/^\s+|\s+$/g,'');}var add=p.addClasses||p.AddClasses||[];for(var a=0;a<add.length;a++){var ac=String(add[a]||'');if(!ac)continue;if(target.classList)target.classList.add(ac);else if((' '+target.className+' ').indexOf(' '+ac+' ')<0)target.className=(target.className?target.className+' ':'')+ac;}var disabled=(typeof p.disabled!=='undefined')?p.disabled:p.Disabled;if(disabled!==null&&typeof disabled!=='undefined'){target.disabled=!!disabled;if(disabled)target.setAttribute('disabled','disabled');else target.removeAttribute('disabled');}var hidden=(typeof p.hidden!=='undefined')?p.hidden:p.Hidden;if(hidden!==null&&typeof hidden!=='undefined'){target.hidden=!!hidden;if(hidden)target.setAttribute('hidden','hidden');else target.removeAttribute('hidden');}var checked=(typeof p.checked!=='undefined')?p.checked:p.Checked;if(checked!==null&&typeof checked!=='undefined'){target.checked=!!checked;if(checked)target.setAttribute('checked','checked');else target.removeAttribute('checked');}var value=(typeof p.value!=='undefined')?p.value:p.Value;if(value!==null&&typeof value!=='undefined')target.value=String(value);var attrs=p.attributes||p.Attributes||{};for(var name in attrs){if(!Object.prototype.hasOwnProperty.call(attrs,name))continue;var lower=String(name).toLowerCase();if(!(lower==='title'||lower.indexOf('aria-')===0||lower.indexOf('data-')===0))continue;var attrValue=attrs[name];if(attrValue===null||typeof attrValue==='undefined')target.removeAttribute(name);else target.setAttribute(name,String(attrValue));} } }
function tvairApplyUiPatchesJson(json){try{var patches=JSON.parse(String(json||'[]'));tvairApplyUiPatches(patches);var applied=0;if(patches&&typeof patches.length!=='undefined'){for(var i=0;i<patches.length&&i<64;i++){var p=patches[i]||{};var id=p.elementId||p.ElementId||'';if(id&&document.getElementById(id))applied++;}}return applied;}catch(_){return -1;}}
function tvairPageRefreshStateKey(){try{return 'tvair-page-refresh:'+location.pathname+location.search;}catch(_){return '';}}function tvairCapturePageRefreshState(){try{var key=tvairPageRefreshStateKey();if(!key)return;var state={x:window.pageXOffset||document.documentElement.scrollLeft||0,y:window.pageYOffset||document.documentElement.scrollTop||0};sessionStorage.setItem(key,JSON.stringify(state));}catch(_){}}function tvairRestorePageRefreshState(){try{var key=tvairPageRefreshStateKey();if(!key)return;var raw=sessionStorage.getItem(key);if(!raw)return;sessionStorage.removeItem(key);var state=JSON.parse(raw);window.scrollTo(Number(state.x)||0,Number(state.y)||0);}catch(_){}}function tvairInteractionStateKey(){var id=tvairCurrentWindowId();return id?'tvair-interaction-state:'+id:'';}function tvairCaptureInteractionState(){try{var key=tvairInteractionStateKey();if(!key)return;var a=document.activeElement;var state={x:window.pageXOffset||document.documentElement.scrollLeft||0,y:window.pageYOffset||document.documentElement.scrollTop||0,id:a&&a.id?a.id:'',start:(a&&typeof a.selectionStart==='number')?a.selectionStart:null,end:(a&&typeof a.selectionEnd==='number')?a.selectionEnd:null};sessionStorage.setItem(key,JSON.stringify(state));}catch(_){}}function tvairRestoreInteractionState(){try{var key=tvairInteractionStateKey();if(!key)return;var raw=sessionStorage.getItem(key);if(!raw)return;sessionStorage.removeItem(key);var state=JSON.parse(raw);window.scrollTo(Number(state.x)||0,Number(state.y)||0);if(state.id){var a=document.getElementById(state.id);if(a&&a.focus){a.focus();if(typeof a.setSelectionRange==='function'&&state.start!==null)a.setSelectionRange(state.start,state.end===null?state.start:state.end);}}}catch(_){}}function tvairRestoreRefreshState(){tvairRestoreInteractionState();tvairRestorePageRefreshState();}if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',tvairRestoreRefreshState);else setTimeout(tvairRestoreRefreshState,0);function tvairSubmitSafeAction(el,eventName){var action=tvairGetAttr(el,'data-tvair-action');var interactionId=tvairNewInteractionId();el.setAttribute('data-tvair-debug-interaction-id',interactionId);tvairClientBeacon('click_captured',eventName,action,el);tvairClientBeacon('received_before_validate',eventName,action,el);if(!action){tvairClientBeacon('denied_missing_action',eventName,action,el);return;}var confirmMessage=tvairGetAttr(el,'data-tvair-confirm-message');if(confirmMessage){var confirmed=false;try{confirmed=window.confirm(String(confirmMessage));}catch(_){confirmed=false;}if(!confirmed){tvairClientBeacon('action_cancelled_by_user',eventName,action,el);return;}}if(!tvairTryBeginRepeatPolicy(el,eventName,action))return;if(!tvairBeginActionFeedback(el,eventName,action,interactionId))return;if(!tvairFeedbackEnabled(el))tvairApplyAcceptedButtonState(el,action,eventName);var isWindowAction=(action==='refreshWindow'||action==='updateWindow'||action==='closeWindow'||action==='rerenderWindow'||action==='openWindow');var form=document.createElement('form');form.method='post';form.action=isWindowAction?'/api/plugins/window':'/api/plugins/action';tvairAppendHidden(form,'action',action);tvairAppendHidden(form,'pluginId',tvairGetAttr(el,'data-tvair-plugin-id'));tvairAppendHidden(form,'routeSegment',tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'');tvairAppendHidden(form,'token',tvairGetAttr(el,'data-tvair-token')||tvairGetAttr(el,'data-tvair-action-token'));tvairAppendHidden(form,'actionToken',tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'));tvairAppendHidden(form,'responseMode',tvairGetAttr(el,'data-tvair-response-mode')||'hostHandled');tvairAppendHidden(form,'windowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());tvairAppendHidden(form,'target',tvairGetAttr(el,'data-tvair-target')||'content');tvairAppendHidden(form,'refreshTarget',tvairGetAttr(el,'data-tvair-refresh-target')||'content');tvairAppendHidden(form,'preserveScroll',tvairGetAttr(el,'data-tvair-preserve-scroll')||'true');tvairAppendHidden(form,'safeEvent',eventName||'unknown');tvairAppendHidden(form,'safeEventAction',action);tvairAppendHidden(form,'safeEventSource','host-script-no-plugin-js');tvairAppendHidden(form,'safeEventInteractionId',interactionId);tvairAppendHidden(form,'safeEventWindowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());tvairAppendHidden(form,'feedbackRequested',tvairFeedbackEnabled(el)?'true':'false');tvairCollectFormValues(el,form);if(el.attributes){for(var i=0;i<el.attributes.length;i++){var a=el.attributes[i];if(a&&a.name&&a.name.indexOf('data-tvair-payload-')===0){var pk=a.name.substring('data-tvair-payload-'.length);if(!tvairIsReservedPayloadKey(pk))tvairAppendHidden(form,pk,a.value);} } }el.setAttribute('data-tvair-debug-payload-count',String(form.elements.length));tvairClientBeacon('payload_built',eventName,action,el);document.body.appendChild(form);tvairClientBeacon('post_started',eventName,action,el);tvairClientBeacon('posting_form',eventName,action,el);var submitResponseMode=tvairGetAttr(el,'data-tvair-response-mode')||'hostHandled';if(submitResponseMode==='hostHandled'||submitResponseMode==='noContent'||submitResponseMode==='patchWindow'){try{var pairs=[];for(var j=0;j<form.elements.length;j++){var it=form.elements[j];if(it&&it.name)pairs.push(encodeURIComponent(it.name)+'='+encodeURIComponent(it.value||''));}var endpoint=isWindowAction?'/api/plugins/window':'/api/plugins/action';var resolvedEndpoint=location.protocol+'//'+location.host+endpoint;el.setAttribute('data-tvair-debug-endpoint',resolvedEndpoint);var xhr=new XMLHttpRequest();xhr.open('POST',resolvedEndpoint,true);xhr.setRequestHeader('Content-Type','application/x-www-form-urlencoded; charset=UTF-8');xhr.setRequestHeader('Accept','application/json, text/plain, */*');var requestInteractionId=interactionId;xhr.onreadystatechange=function(){if(xhr.readyState===4){var previousInteractionId=tvairGetAttr(el,'data-tvair-debug-interaction-id');el.setAttribute('data-tvair-debug-interaction-id',requestInteractionId);el.setAttribute('data-tvair-debug-status',String(xhr.status||0));var body={};try{body=JSON.parse(xhr.responseText||'{}');}catch(_){if(submitResponseMode==='patchWindow'||tvairFeedbackEnabled(el))el.setAttribute('data-tvair-debug-reason','response_parse_failed');}if(xhr.status>=200&&xhr.status<400&&submitResponseMode==='patchWindow')tvairApplyUiPatches(body&&body.uiPatches?body.uiPatches:[]);var requestSucceeded=xhr.status>=200&&xhr.status<400;tvairCompleteActionFeedback(el,eventName,action,body,requestSucceeded);tvairClientBeacon(requestSucceeded?'post_completed':'post_failed',eventName,action,el);el.setAttribute('data-tvair-debug-interaction-id',previousInteractionId);var refreshSurface='';var preservePageScroll=false;var pageRefreshLocation='';try{refreshSurface=String(xhr.getResponseHeader('X-TvAIr-Refresh-Surface')||'').toLowerCase();preservePageScroll=String(xhr.getResponseHeader('X-TvAIr-Preserve-Scroll')||'').toLowerCase()==='true';pageRefreshLocation=String(xhr.getResponseHeader('X-TvAIr-Refresh-Location')||'');}catch(_){}if(requestSucceeded&&refreshSurface==='page'){if(preservePageScroll)tvairCapturePageRefreshState();tvairClientBeacon('page_refresh_issued',eventName,action,el);if(pageRefreshLocation){location.href=pageRefreshLocation;}else{location.href=location.pathname+location.search;}return;}}};xhr.onerror=function(){var previousInteractionId=tvairGetAttr(el,'data-tvair-debug-interaction-id');el.setAttribute('data-tvair-debug-interaction-id',requestInteractionId);el.setAttribute('data-tvair-debug-reason','xhr_error');tvairCompleteActionFeedback(el,eventName,action,{},false);tvairClientBeacon('post_failed',eventName,action,el);el.setAttribute('data-tvair-debug-interaction-id',previousInteractionId);};xhr.send(pairs.join('&'));return;}catch(_){}}tvairCaptureInteractionState();form.submit();}
function tvairHandleSafeEvent(e,eventName){e=e||window.event;var target=e.target||e.srcElement;var el=tvairFindSafeEventTarget(target,eventName);if(!el)return true;try{if(e.preventDefault)e.preventDefault();e.returnValue=false;}catch(_){ }tvairSubmitSafeAction(el,eventName);return false;}
function tvairBindSafeEvents(){if(window.__tvairSafeEventBound){tvairClientBeacon('bind_skip_already_bound','','',document.body);return;}window.__tvairSafeEventBound=true;tvairClientBeacon('bind_start','','',document.body);if(document.addEventListener){document.addEventListener('dblclick',function(e){return tvairHandleSafeEvent(e,'dblclick');},false);document.addEventListener('mouseover',function(e){tvairHandleRuntimeHover(e,'enter');},false);document.addEventListener('mouseout',function(e){tvairHandleRuntimeHover(e,'leave');},false);document.addEventListener('click',function(e){if(tvairHandleSafeEvent(e,'click')===false)return false;var a=e.target;while(a&&a!==document&&!(a.tagName&&String(a.tagName).toLowerCase()==='a'))a=a.parentNode;if(a&&a.href){var next=tvairPreserveToolWindowLink(a.getAttribute('href')||'');if(next&&(next!==a.getAttribute('href'))){if(e.preventDefault)e.preventDefault();location.href=next;} } },false);}else if(document.attachEvent){document.attachEvent('ondblclick',function(){return tvairHandleSafeEvent(window.event,'dblclick');});document.attachEvent('onmouseover',function(){tvairHandleRuntimeHover(window.event,'enter');});document.attachEvent('onmouseout',function(){tvairHandleRuntimeHover(window.event,'leave');});document.attachEvent('onclick',function(){return tvairHandleSafeEvent(window.event,'click');});}else{var oldDbl=document.ondblclick;document.ondblclick=function(e){if(tvairHandleSafeEvent(e||window.event,'dblclick')===false)return false;return oldDbl?oldDbl(e):true;};var oldOver=document.onmouseover;document.onmouseover=function(e){tvairHandleRuntimeHover(e||window.event,'enter');return oldOver?oldOver(e):true;};var oldOut=document.onmouseout;document.onmouseout=function(e){tvairHandleRuntimeHover(e||window.event,'leave');return oldOut?oldOut(e):true;};var oldClick=document.onclick;document.onclick=function(e){if(tvairHandleSafeEvent(e||window.event,'click')===false)return false;return oldClick?oldClick(e):true;};}tvairClientBeacon('bind_complete','','',document.body);}
function tvairFindActionTokenElement(){try{return document.querySelector?document.querySelector('[data-tvair-action-token],[data-tvair-token]'):null;}catch(_){return null;}}
function tvairRenewActionToken(){try{var el=tvairFindActionTokenElement();if(!el)return;var token=tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token');var pluginId=tvairGetAttr(el,'data-tvair-plugin-id');var route=tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'';if(!token||!pluginId||!route)return;var xhr=new XMLHttpRequest();xhr.open('POST','/api/plugins/action-token/renew',true);xhr.setRequestHeader('Content-Type','application/x-www-form-urlencoded; charset=UTF-8');xhr.send('actionToken='+encodeURIComponent(token)+'&pluginId='+encodeURIComponent(pluginId)+'&routeSegment='+encodeURIComponent(route));}catch(_){}}
function tvairStartActionTokenKeepalive(){try{if(window.__tvairActionTokenKeepaliveStarted)return;window.__tvairActionTokenKeepaliveStarted=true;tvairRenewActionToken();window.setInterval(tvairRenewActionToken,300000);if(document.addEventListener)document.addEventListener('visibilitychange',function(){if(!document.hidden)tvairRenewActionToken();},false);if(window.addEventListener)window.addEventListener('focus',tvairRenewActionToken,false);else if(window.attachEvent)window.attachEvent('onfocus',tvairRenewActionToken);}catch(_){}}
function tvairBootSafeEvents(){try{tvairBindSafeEvents();tvairStartActionTokenKeepalive();}catch(_){tvairClientBeacon('bind_failed','','',document.body);}}if(document.readyState==='complete'||document.readyState==='interactive'){tvairBootSafeEvents();}else if(window.attachEvent){window.attachEvent('onload',tvairBootSafeEvents);}else if(window.addEventListener){window.addEventListener('load',tvairBootSafeEvents,false);}else{window.onload=tvairBootSafeEvents;}
</script>
</body>
</html>
""";
    return template
        .Replace("{{safeTitle}}", safeTitle, StringComparison.Ordinal)
        .Replace("{{safeRoute}}", safeRoute, StringComparison.Ordinal)
        .Replace("{{safeSelectedTheme}}", safeSelectedTheme, StringComparison.Ordinal)
        .Replace("{{safeEffectiveTheme}}", safeEffectiveTheme, StringComparison.Ordinal)
        .Replace("{{themeClass}}", themeClass, StringComparison.Ordinal)
        .Replace("{{pluginHead}}", pluginHead, StringComparison.Ordinal)
        .Replace("{{pluginContent}}", pluginContent, StringComparison.Ordinal)
        .Replace("{{developerBeaconBody}}", developerBeaconBody, StringComparison.Ordinal);
}

static (string Head, string Body) NormalizePluginToolWindowContent(string? html, string? routeSegment)
{
    var source = html ?? string.Empty;
    var headSource = ExtractPluginHeadFragment(source);
    var body = source;
    if (LooksLikeFullHtmlDocument(source)) body = ExtractPluginBodyFragment(source);

    var headParts = new List<string>();
    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(headSource, "<\\s*style\\b[^>]*>.*?<\\s*/\\s*style\\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
        headParts.Add(match.Value);
    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(headSource, "<\\s*link\\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
    {
        if (IsPluginHeadResourceAllowed(match.Value, routeSegment)) headParts.Add(match.Value);
    }
    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(headSource, "<\\s*script\\b[^>]*src\\s*=\\s*(['\"]?)([^'\" >]+)\\1[^>]*>\\s*<\\s*/\\s*script\\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
    {
        if (IsPluginHeadResourceAllowed(match.Value, routeSegment)) headParts.Add(match.Value);
    }

    var bodyInlineStyles = string.Join("\n", System.Text.RegularExpressions.Regex.Matches(body, "<\\s*style\\b[^>]*>.*?<\\s*/\\s*style\\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value));
    if (!string.IsNullOrWhiteSpace(bodyInlineStyles)) headParts.Add(bodyInlineStyles);

    body = System.Text.RegularExpressions.Regex.Replace(body, "<\\s*style\\b[^>]*>.*?<\\s*/\\s*style\\s*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    body = System.Text.RegularExpressions.Regex.Replace(body, "<\\s*/?\\s*(html|head|body)\\b[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    return (string.Join("\n", headParts), body);
}

static string NormalizePluginPageContent(string? html)
{
    var source = html ?? string.Empty;
    if (string.IsNullOrWhiteSpace(source)) return string.Empty;

    var body = LooksLikeFullHtmlDocument(source) ? ExtractPluginBodyFragment(source) : source;
    var head = ExtractPluginHeadFragment(source);
    var styles = new List<string>();
    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(head, @"<\s*style\b[^>]*>.*?<\s*/\s*style\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
        styles.Add(match.Value);
    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(body, @"<\s*style\b[^>]*>.*?<\s*/\s*style\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline))
        styles.Add(match.Value);

    body = System.Text.RegularExpressions.Regex.Replace(body, @"<\s*style\b[^>]*>.*?<\s*/\s*style\s*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    body = System.Text.RegularExpressions.Regex.Replace(body, @"<\s*/?\s*(html|head|body)\b[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    return PluginHtmlSanitizer.Sanitize(string.Join("\n", styles) + "\n" + body);
}

static string BuildPluginPageActionAudit(string? html)
{
    var source = html ?? string.Empty;
    var matches = System.Text.RegularExpressions.Regex.Matches(source, @"<(?<tag>[a-zA-Z][a-zA-Z0-9:-]*)\b(?<attrs>[^>]*\bdata-tvair-action\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)[^>]*)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    if (matches.Count == 0) return "candidates=0";

    var samples = new List<string>();
    foreach (System.Text.RegularExpressions.Match match in matches.Cast<System.Text.RegularExpressions.Match>().Take(8))
    {
        var attrs = match.Groups["attrs"].Value;
        string Attr(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(attrs, @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["v"].Value : string.Empty;
        }
        var payloadCount = System.Text.RegularExpressions.Regex.Matches(attrs, @"\bdata-tvair-payload-[a-zA-Z0-9_-]+\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        samples.Add($"tag={SafePluginActionValue(match.Groups["tag"].Value)} type={SafePluginActionValue(Attr("type"))} event={SafePluginActionValue(Attr("data-tvair-event"))} action={SafePluginActionValue(Attr("data-tvair-action"))} endpoint={SafePluginActionValue(Attr("data-tvair-endpoint"))} pluginId={SafePluginActionValue(Attr("data-tvair-plugin-id"))} route={SafePluginActionValue(Attr("data-tvair-route-segment"))} tokenPresent={!string.IsNullOrWhiteSpace(Attr("data-tvair-action-token"))} responseMode={SafePluginActionValue(Attr("data-tvair-response-mode"))} payloadCount={payloadCount}");
    }
    return $"candidates={matches.Count} sample=[{string.Join(" | ", samples)}]";
}

static string ExtractPluginHeadFragment(string html)
{
    if (string.IsNullOrWhiteSpace(html)) return string.Empty;
    var match = System.Text.RegularExpressions.Regex.Match(html, "<\\s*head\\b[^>]*>(.*?)<\\s*/\\s*head\\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    return match.Success ? match.Groups[1].Value : string.Empty;
}

static bool IsPluginHeadResourceAllowed(string tag, string? routeSegment)
{
    if (string.IsNullOrWhiteSpace(tag)) return false;
    var src = System.Text.RegularExpressions.Regex.Match(tag, "\\b(?:href|src)\\s*=\\s*(['\"]?)([^'\" >]+)\\1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!src.Success) return true;
    var url = src.Groups[2].Value.Trim();
    if (string.IsNullOrWhiteSpace(url)) return false;
    if (url.StartsWith("/", StringComparison.Ordinal) && !url.StartsWith("//", StringComparison.Ordinal)) return true;
    if (url.StartsWith("./", StringComparison.Ordinal) || url.StartsWith("../", StringComparison.Ordinal)) return true;
    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

static bool LooksLikeFullHtmlDocument(string? html)
{
    if (string.IsNullOrWhiteSpace(html)) return false;
    return html.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
        || html.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0
        || html.IndexOf("<head", StringComparison.OrdinalIgnoreCase) >= 0;
}

static IResult RenderPluginHtml(string route, HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, ExternalTunerLeaseService externalTuners, ViewerSessionRegistry viewerSessions, IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, PluginBoundaryGate boundaryGate, LogPresentationStore logPresentationStore, LogRepository log)
{
    var requestedRoute = string.Join("", route.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    if (string.IsNullOrWhiteSpace(requestedRoute))
        return Results.NotFound("Plugin UI not found.");

    var publicRoute = NormalizePluginRouteSegment(requestedRoute);
    var nativeUi = registry.FindRuntimeUiPluginNative(requestedRoute, out var nativeUiDefinition);
    var plugin = registry.FindRuntimePlugin(requestedRoute);
    var title = plugin?.Descriptor.DisplayName ?? publicRoute;

    var currentWindowId = NormalizePluginWindowId(http.Query["__tvairWindowId"].FirstOrDefault()
        ?? http.Query["_tvairWindowId"].FirstOrDefault()
        ?? http.Query["windowId"].FirstOrDefault());
    var hostManagedWindow = !string.IsNullOrWhiteSpace(currentWindowId) ? windows.Get(currentWindowId) : null;
    var expectedWindowPluginId = plugin is not null
        ? GetPluginActionIdentity(plugin)
        : string.Empty;
    var isHostManagedWindowContent = hostManagedWindow is not null
        && plugin is not null
        && string.Equals(hostManagedWindow.PluginId, expectedWindowPluginId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizePluginRouteSegment(hostManagedWindow.RouteSegment), NormalizePluginRouteSegment(publicRoute), StringComparison.OrdinalIgnoreCase);
    if (!isHostManagedWindowContent) currentWindowId = string.Empty;

    var toolWindowContentOnly = isHostManagedWindowContent
        || IsTruthy(http.Query["__tvairToolHostContent"].FirstOrDefault())
        || IsTruthy(http.Query["__tvairToolHost"].FirstOrDefault())
        || (IsTruthy(http.Query["__tvairHostWindow"].FirstOrDefault()) && !string.IsNullOrWhiteSpace(currentWindowId));
    var currentRequestPath = http.Path.Value ?? string.Empty;
    var currentRequestQueryString = http.QueryString.HasValue ? http.QueryString.Value ?? string.Empty : string.Empty;
    var currentRequestPathAndQuery = string.Concat(currentRequestPath, currentRequestQueryString);
    var currentRequestQuery = http.Query
        .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
        .ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.FirstOrDefault() ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    var currentRequestWave = currentRequestQuery.TryGetValue("wave", out var requestWave) ? requestWave : string.Empty;
    var currentRequestQueryKeys = string.Join(",", currentRequestQuery.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

    // release_contract: Window state absolute URL contract must be derived before RuntimeUiRenderContext construction.
    // Keep this outside the plugin-specific block so WindowContract and diagnostics can share one authoritative value.
    var currentWindowStateEndpoint = isHostManagedWindowContent && !string.IsNullOrWhiteSpace(currentWindowId)
        ? $"/plugin-window/{Uri.EscapeDataString(currentWindowId)}/state"
        : string.Empty;
    var currentWindowUrl = isHostManagedWindowContent && !string.IsNullOrWhiteSpace(currentWindowId)
        ? $"/plugin-window/{Uri.EscapeDataString(currentWindowId)}"
        : string.Empty;
    var currentWindowStateUrl = string.IsNullOrWhiteSpace(currentWindowStateEndpoint)
        ? string.Empty
        : BuildAbsoluteLocalUrl(http, currentWindowStateEndpoint);
    var currentWindowAbsoluteUrl = string.IsNullOrWhiteSpace(currentWindowUrl)
        ? string.Empty
        : BuildAbsoluteLocalUrl(http, currentWindowUrl);
    var currentWindowHostState = isHostManagedWindowContent && !string.IsNullOrWhiteSpace(currentWindowId)
        ? toolWindows.GetHostState(currentWindowId)
        : null;
    var currentWindowAlwaysOnTop = isHostManagedWindowContent
        ? (currentWindowHostState?.AlwaysOnTop ?? hostManagedWindow?.AlwaysOnTop ?? false)
        : false;
    var currentWindowRevision = isHostManagedWindowContent ? (hostManagedWindow?.Revision ?? 0) : 0;
    var currentWindowHostAlive = isHostManagedWindowContent
        && (currentWindowHostState?.HostAlive ?? toolWindows.IsHostAlive(currentWindowId));

    if (isHostManagedWindowContent && !string.IsNullOrWhiteSpace(currentWindowId) && !string.IsNullOrWhiteSpace(expectedWindowPluginId))
    {
        windows.UpdateContentRouteFromRender(currentWindowId, expectedWindowPluginId, currentRequestPathAndQuery);
    }
    var hostSelectedTheme = IniSettingsService.NormalizeSystemTheme(ini.SystemTheme);
    var hostEffectiveTheme = ResolveEffectiveHostTheme(hostSelectedTheme);
    log.Add("PLUGIN_RENDER_ENTER", plugin?.Descriptor.DisplayName ?? publicRoute, $"routeSegment={SafePluginActionValue(publicRoute)} requestedRoute={SafePluginActionValue(requestedRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} requestPath={SafePluginActionValue(currentRequestPath)} query={SafePluginActionValue(currentRequestQueryString)} hostSelectedTheme={SafePluginActionValue(hostSelectedTheme)} hostEffectiveTheme={SafePluginActionValue(hostEffectiveTheme)} rule=release_contract");

    if (plugin is not null)
    {
        var renderBoundary = boundaryGate.CheckRender(ResolveRuntimeBoundaryPlugin(registry, plugin), isHostManagedWindowContent, currentRequestPath);
        if (!renderBoundary.Allowed)
        {
            log.Add("PLUGIN_RENDER", plugin.Descriptor.DisplayName, $"result=DENIED reason={SafePluginActionValue(renderBoundary.Reason)} routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} endpoint={SafePluginActionValue(currentRequestPath)} rule=plugin_boundary_gate");
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    try
    {
        string body;

        // TvAIr 1.0.0: プラグインページはTvAIr共通ヘッダー付きの拡張画面として表示する。
        var physicalRoute = NormalizePluginRouteSegment(nativeUiDefinition?.Route ?? publicRoute);
        if (string.IsNullOrWhiteSpace(physicalRoute)) physicalRoute = publicRoute;
        var pluginPageRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Plugins", physicalRoute));
        var pluginsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Plugins")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var indexPath = Path.Combine(pluginPageRoot, "wwwroot", "index.html");
        if (!Path.GetFullPath(indexPath).StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase)) indexPath = string.Empty;
        if (File.Exists(indexPath))
        {
            var staticHtml = File.ReadAllText(indexPath);
            body = toolWindowContentOnly ? staticHtml : ExtractPluginBodyFragment(staticHtml);
            log.Add("PLUGIN_RENDER_RESULT", plugin?.Descriptor.DisplayName ?? publicRoute, $"source=static_index routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} preserveFullHtml={toolWindowContentOnly} {BuildPluginRenderHtmlAudit(body)} rule=plugin_toolwindow_full_html_compat");
        }
        else if (plugin is not null)
        {
            var pluginId = GetPluginActionIdentity(plugin);
            var token = actionTokens.Issue(pluginId, publicRoute);
            var supportedActions = new[] { "pluginOwnedAction" };
            var pluginAssetBaseUrl = $"/plugin-assets/{Uri.EscapeDataString(publicRoute)}";
            var pluginAssetApiBaseUrl = $"/api/plugins/{Uri.EscapeDataString(pluginId)}/assets";
            var toolWindowCaps = toolWindows.GetCapabilities();
            var runtimeUiContext = new RuntimeUiRenderContext
            {
                PluginId = pluginId,
                UiDefinitionId = nativeUiDefinition?.UiDefinitionId ?? string.Empty,
                Route = publicRoute,
                RequestedAt = DateTime.Now,
                IsClosedNetwork = true,
                HostSelectedTheme = hostSelectedTheme,
                HostEffectiveTheme = hostEffectiveTheme,
                ThemeContract = BuildPluginThemeContract(hostSelectedTheme, hostEffectiveTheme),
                ActionEndpoint = "/api/plugins/action",
                ActionMethod = "POST",
                SupportedActions = supportedActions,
                ActionToken = token.Token,
                ActionContract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["route"] = "/plugin-action",
                    ["endpoint"] = "/api/plugins/action",
                    ["method"] = "POST",
                    ["actions"] = string.Join(",", supportedActions),
                    ["token"] = token.Token,
                    ["pluginId"] = pluginId,
                    ["routeSegment"] = publicRoute,
                    ["hostSelectedTheme"] = hostSelectedTheme,
                    ["hostEffectiveTheme"] = hostEffectiveTheme,
                    ["themeContract"] = "RuntimeUiRenderContext.ThemeContract",
                    ["responseMode"] = "json|refreshWindow|patchWindow|hostHandled|noContent",
                    ["pageResponseMode"] = "hostHandled",
                    ["browserTransport"] = "application/x-www-form-urlencoded",
                    ["browserTransportMethod"] = "host_managed_declarative_safe_event",
                    ["scriptExecutionAllowed"] = "false",
                    ["endpointUsage"] = "POST ActionContract.endpoint (/api/plugins/action); ActionContract.route (/plugin-action) is a logical contract identifier, not the HTTP POST target",
                    ["tokenField"] = "actionToken (token alias accepted)",
                    ["identityFields"] = "pluginId,routeSegment",
                    ["actionField"] = "action=pluginOwnedAction",
                    ["payloadFields"] = "data-tvair-payload-{name} -> form field {name} -> RuntimeUiActionHttpRequest.Payload",
                    ["pageButtonContract"] = "data-tvair-event=click;data-tvair-action=pluginOwnedAction;data-tvair-endpoint=/api/plugins/action;data-tvair-plugin-id;data-tvair-route-segment;data-tvair-action-token;data-tvair-response-mode=hostHandled;data-tvair-repeat-policy;data-tvair-burst-window-ms;data-tvair-accepted-label;data-tvair-payload-*",
                    ["pageAndToolWindowTransportSame"] = "true",
                    ["refreshRequestedSurfaceOwner"] = "RuntimeUiDefinition.Kind",
                    ["pageRefreshContract"] = "RefreshRequested -> Host-issued same ApplicationPage navigation; WindowInstanceId not required",
                    ["toolWindowRefreshContract"] = "RefreshRequested -> current WindowContent refresh; WindowInstanceId required",
                },
                HoverContract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["contract"] = "RuntimeHover",
                    ["optInAttribute"] = RuntimeUiRenderContext.RuntimeHoverKeyAttribute,
                    ["eventName"] = RuntimeUiRenderContext.RuntimeHoverEventName,
                    ["states"] = "enter,leave",
                    ["delivery"] = "bubbling_dom_custom_event",
                    ["hostOwns"] = "hover_state_normalization_only",
                    ["pluginOwns"] = "reaction,popup,marquee,overflow_detection,expansion,highlight",
                    ["network"] = "none",
                    ["tokenRequired"] = "false"
                },
                WindowRoute = "/plugin-window",
                WindowEndpoint = "/api/plugins/window",
                CurrentWindowId = currentWindowId,
                IsHostManagedWindowContent = isHostManagedWindowContent,
                CurrentWindowStateEndpoint = currentWindowStateEndpoint,
                CurrentWindowStateUrl = currentWindowStateUrl,
                CurrentWindowUrl = currentWindowUrl,
                CurrentWindowAbsoluteUrl = currentWindowAbsoluteUrl,
                CurrentWindowAlwaysOnTop = currentWindowAlwaysOnTop,
                CurrentWindowRevision = currentWindowRevision,
                CurrentWindowHostAlive = currentWindowHostAlive,
                WindowMethod = "POST",
                WindowToken = token.Token,
                SupportedWindowActions = new[] { "openWindow", "closeWindow", "updateWindow", "refreshWindow", "rerenderWindow" },
                ToolWindowCapabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["contractVersion"] = toolWindowCaps.ContractVersion,
                    ["toolWindowSupported"] = toolWindowCaps.ToolWindowSupported ? "true" : "false",
                    ["hostWindowSupported"] = toolWindowCaps.HostWindowSupported ? "true" : "false",
                    ["webView2RuntimeAvailable"] = toolWindowCaps.WebView2RuntimeAvailable ? "true" : "false",
                    ["hostKind"] = toolWindowCaps.HostKind,
                    ["fallbackHostKind"] = toolWindowCaps.FallbackHostKind,
                    ["fallbackToBrowserRedirectSupported"] = toolWindowCaps.FallbackToBrowserRedirectSupported ? "true" : "false",
                    ["jsonScreenSuppressed"] = toolWindowCaps.JsonScreenSuppressed ? "true" : "false",
                    ["supportsAlwaysOnTop"] = toolWindowCaps.SupportsAlwaysOnTop ? "true" : "false",
                    ["supportsSize"] = toolWindowCaps.SupportsSize ? "true" : "false",
                    ["supportsMinSize"] = toolWindowCaps.SupportsMinSize ? "true" : "false",
                    ["supportsPositionPersistence"] = toolWindowCaps.SupportsPositionPersistence ? "true" : "false",
                    ["supportsStatePersistence"] = toolWindowCaps.SupportsStatePersistence ? "true" : "false",
                    ["supportsReuseExisting"] = toolWindowCaps.SupportsReuseExisting ? "true" : "false",
                    ["supportsActivateExisting"] = toolWindowCaps.SupportsActivateExisting ? "true" : "false",
                    ["reuseKey"] = toolWindowCaps.ReuseKey,
                    ["refreshTarget"] = toolWindowCaps.RefreshTarget,
                    ["refreshReloadScope"] = isHostManagedWindowContent ? "toolwindow-content-document" : toolWindowCaps.RefreshReloadScope,
                    ["scriptExecutionAllowed"] = toolWindowCaps.ScriptExecutionAllowed ? "true" : "false",
                    ["supportsManifestFormIcon"] = toolWindowCaps.SupportsManifestFormIcon ? "true" : "false",
                    ["formIconSourcePriority"] = toolWindowCaps.FormIconSourcePriority
                },
                RequestPathAndQuery = currentRequestPathAndQuery,
                RequestQuery = currentRequestQuery,
                AssetBaseUrl = pluginAssetBaseUrl,
                AssetContract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["contractVersion"] = "1.0.0",
                    ["preferredUrlPattern"] = "/plugin-assets/{routeSegment}/{assetName}",
                    ["apiUrlPattern"] = "/api/plugins/{pluginId}/assets/{assetName}",
                    ["assetBaseUrl"] = pluginAssetBaseUrl,
                    ["assetApiBaseUrl"] = pluginAssetApiBaseUrl,
                    ["allowedExtensions"] = "png",
                    ["allowedMimeTypes"] = "image/png",
                    ["allowedLocations"] = "Plugins/{route}/Assets;Plugins/{route}/assets;Plugins/{route}/wwwroot/assets;Plugins/{route}/wwwroot",
                    ["htmlTag"] = "img",
                    ["htmlAttributes"] = "src,alt,width,height,class,title",
                    ["sanitizerSrcPolicy"] = "self/plugin-assets only; external http(s) blocked; javascript blocked",
                    ["dataUriPolicy"] = "not_recommended_do_not_use_for_toolbar_icons",
                    ["routeSegment"] = publicRoute,
                    ["pluginId"] = pluginId
                },
                WindowContract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["route"] = "/plugin-window",
                    ["endpoint"] = "/api/plugins/window",
                    ["method"] = "POST",
                    ["actions"] = "openWindow,closeWindow,updateWindow,refreshWindow,rerenderWindow",
                    ["token"] = token.Token,
                    ["pluginId"] = pluginId,
                    ["routeSegment"] = publicRoute,
                    ["hostManaged"] = "true",
                    ["stateEndpointTemplate"] = "/plugin-window/{windowId}/state",
                    ["currentWindowId"] = currentWindowId,
                    ["windowId"] = currentWindowId,
                    ["currentWindowStateEndpoint"] = currentWindowStateEndpoint,
                    ["currentWindowStateUrl"] = currentWindowStateUrl,
                    ["currentWindowUrl"] = currentWindowUrl,
                    ["currentWindowAbsoluteUrl"] = currentWindowAbsoluteUrl,
                    ["currentWindowAlwaysOnTop"] = currentWindowAlwaysOnTop ? "true" : "false",
                    ["currentWindowRevision"] = currentWindowRevision.ToString(CultureInfo.InvariantCulture),
                    ["currentWindowHostAlive"] = currentWindowHostAlive ? "true" : "false",
                    ["currentWindowStateDirectValues"] = "RuntimeUiRenderContext.CurrentWindowAlwaysOnTop|CurrentWindowRevision|CurrentWindowHostAlive; no RenderHtml self HTTP call required",
                    ["isHostManagedWindowContent"] = isHostManagedWindowContent ? "true" : "false",
                    ["refreshContract"] = "host_state_revision",
                    ["refreshTarget"] = "content",
                    ["refreshReloadScope"] = isHostManagedWindowContent ? "toolwindow-content-document" : "iframe-content-only",
                    ["refreshPayload"] = "action=refreshWindow;payload.windowId=currentWindowId;payload.target=content;payload.preserveScroll=true",
                    ["openWindowForm"] = "form POST /api/plugins/window responseMode=redirectBack or hostHandled; current page stays on plugin route",
                    ["toolWindowForm"] = "form POST /api/plugins/window responseMode=redirectBack|hostHandled reuseExisting=true activateExisting=true returnUrl=current plugin route; host opens separate tool window and responds 303 redirectBack",
                    ["toolWindowStateSource"] = "/plugin-window/{windowId}/state is authoritative for alwaysOnTop/revision/hostAlive",
                    ["updateWindowPreferredResponseMode"] = "hostHandled",
                    ["updateWindowAutoRender"] = "not_guaranteed; state/form is applied only; use refreshWindow hostHandled for rerender",
                    ["openWindowModes"] = "json|redirect|redirectBack|hostHandled|toolWindow|toolWindowRedirectBack|hostWindow|auto|html|noContent",
                    ["toolWindowSupported"] = toolWindowCaps.ToolWindowSupported ? "true" : "false",
                    ["toolWindowCapabilitiesEndpoint"] = "/api/plugins/window/capabilities",
                    ["toolWindowHostKind"] = toolWindowCaps.HostKind,
                    ["toolWindowFallbackHostKind"] = toolWindowCaps.FallbackHostKind,
                    ["toolWindowWebView2RuntimeAvailable"] = toolWindowCaps.WebView2RuntimeAvailable ? "true" : "false",
                    ["toolWindowJsonScreenSuppressed"] = toolWindowCaps.JsonScreenSuppressed ? "true" : "false",
                    ["toolWindowReuseKey"] = toolWindowCaps.ReuseKey,
                    ["toolWindowSupportsAlwaysOnTop"] = toolWindowCaps.SupportsAlwaysOnTop ? "true" : "false",
                    ["toolWindowSupportsPositionPersistence"] = toolWindowCaps.SupportsPositionPersistence ? "true" : "false",
                    ["toolWindowSupportsStatePersistence"] = toolWindowCaps.SupportsStatePersistence ? "true" : "false",
                    ["toolWindowStateFields"] = "windowId|pluginId|routeSegment|title|width|height|minWidth|minHeight|left|top|alwaysOnTop|revision|isClosed|hostAlive|hostKind|webView2RuntimeAvailable|reuseKey|jsonScreenSuppressed|closeSync",
                    ["toolWindowFormIconContract"] = "Runtime descriptor Assets .ico -> host-managed Form.Icon",
                    ["toolWindowFormIconAllowedExtension"] = "ico",
                    ["toolWindowFormIconSourcePriority"] = "EmbeddedResource>plugin_file>default_TvAIr_icon",
                    ["toolWindowFormResponse"] = "303_redirect_back_to_returnUrl_or_referrer_no_json_no_blank",
                    ["programGuideWaveFiltersEndpoint"] = "/api/plugins/program-guide/wave-filters",
                    ["preferredOpenMode"] = "toolWindow",
                    ["toolWindowContentOnly"] = "true",
                    ["actionForm"] = "form POST /api/plugins/action responseMode=hostHandled windowId=currentWindowId; use refreshWindow only when immediate rerender is required",
                    ["currentRequestPath"] = currentRequestPath,
                    ["currentRequestQueryString"] = currentRequestQueryString,
                    ["currentRequestPathAndQuery"] = currentRequestPathAndQuery,
                    ["currentRequestQueryKeys"] = currentRequestQueryKeys,
                    ["currentRequestWave"] = currentRequestWave,
                    ["pluginAssetBaseUrl"] = pluginAssetBaseUrl,
                    ["pluginAssetAllowedExtensions"] = "png",
                    ["pluginAssetResolveMethod"] = "RuntimeUiRenderContext.ResolveAssetUrl(assetName)"
                }
            };
            log.Add("PLUGIN_RUNTIME_UI_CONTEXT_ACTION_CONTRACT", plugin.Descriptor.DisplayName, $"result=ISSUED route=/plugin-action endpoint={SafePluginActionValue(runtimeUiContext.ActionEndpoint)} method={SafePluginActionValue(runtimeUiContext.ActionMethod)} actions={SafePluginActionValue(string.Join(",", supportedActions))} tokenPresent={!string.IsNullOrWhiteSpace(runtimeUiContext.ActionToken)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(publicRoute)} rule=release_contract");
            log.Add("PLUGIN_RUNTIME_UI_CONTEXT_HOVER_CONTRACT", plugin.Descriptor.DisplayName, $"result=ISSUED contract=RuntimeHover optIn=data-tvair-hover-key event=tvair-runtime-hover states=enter,leave delivery=bubbling_dom_custom_event network=none pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(publicRoute)} rule=runtime_hover_contract");
            log.Add("PLUGIN_RUNTIME_UI_CONTEXT_WINDOW_CONTRACT", plugin.Descriptor.DisplayName, $"result=ISSUED route={SafePluginActionValue(runtimeUiContext.WindowRoute)} endpoint={SafePluginActionValue(runtimeUiContext.WindowEndpoint)} method={SafePluginActionValue(runtimeUiContext.WindowMethod)} actions={SafePluginActionValue(string.Join(",", runtimeUiContext.SupportedWindowActions))} tokenPresent={!string.IsNullOrWhiteSpace(runtimeUiContext.WindowToken)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(publicRoute)} hostManaged=True currentWindowId={SafePluginActionValue(currentWindowId)} isHostManagedWindowContent={isHostManagedWindowContent} refreshTarget=content toolWindowSupported={toolWindowCaps.ToolWindowSupported} webView2Runtime={toolWindowCaps.WebView2RuntimeAvailable} hostKind={SafePluginActionValue(toolWindowCaps.HostKind)} reuseKey={SafePluginActionValue(toolWindowCaps.ReuseKey)} positionPersistence={toolWindowCaps.SupportsPositionPersistence} statePersistence={toolWindowCaps.SupportsStatePersistence} closeSync=closeWindow_and_host_x_button rule=release_contract");
            log.Add("WINDOW_STATE_ENDPOINT_CONTRACT", plugin.Descriptor.DisplayName, $"result=ISSUED currentWindowId={SafePluginActionValue(currentWindowId)} endpoint={SafePluginActionValue(currentWindowStateEndpoint)} absoluteUrl={SafePluginActionValue(currentWindowStateUrl)} currentWindowAlwaysOnTop={currentWindowAlwaysOnTop} currentWindowRevision={currentWindowRevision} currentWindowHostAlive={currentWindowHostAlive} csharpReadable={(!string.IsNullOrWhiteSpace(currentWindowStateUrl)).ToString()} stateDirectValues=RuntimeUiRenderContext source=RuntimeUiRenderContext.WindowContract rule=release_contract");
            log.Add("PLUGIN_RUNTIME_UI_CONTEXT_REQUEST_CONTRACT", plugin.Descriptor.DisplayName, $"result=ISSUED routeSegment={SafePluginActionValue(publicRoute)} requestPath={SafePluginActionValue(currentRequestPath)} requestQuery={SafePluginActionValue(currentRequestQueryString)} pathAndQuery={SafePluginActionValue(runtimeUiContext.RequestPathAndQuery)} queryKeys={SafePluginActionValue(currentRequestQueryKeys)} wave={SafePluginActionValue(currentRequestWave)} toolWindow={isHostManagedWindowContent} directContent={toolWindowContentOnly} currentWindowId={SafePluginActionValue(currentWindowId)} rule=release_contract");
            log.Add("PLUGIN_RUNTIME_UI_CONTEXT_ASSET_CONTRACT", plugin.Descriptor.DisplayName, $"result=ISSUED routeSegment={SafePluginActionValue(publicRoute)} pluginId={SafePluginActionValue(pluginId)} assetBaseUrl={SafePluginActionValue(pluginAssetBaseUrl)} apiBaseUrl={SafePluginActionValue(pluginAssetApiBaseUrl)} allowedExtensions=png imgTagAllowed=True externalUrlAllowed=False dataUriRecommended=False formIconAllowedExtensions=ico formIconSourcePriority=EmbeddedResource>plugin_file>default_TvAIr_icon rule=release_contract");
            if (nativeUi is null || nativeUiDefinition is null)
                return Results.NotFound("Runtime Plugin UI not found.");
            var renderHtml = nativeUi.RenderHtml(runtimeUiContext);
            var floatingButtonsHtml = BuildPluginFloatingButtonsHtml(runtimeUiContext, plugin.Descriptor.DisplayName, log);
            if (!string.IsNullOrWhiteSpace(floatingButtonsHtml))
                renderHtml = (renderHtml ?? string.Empty) + floatingButtonsHtml;
            log.Add("PLUGIN_RENDER_RESULT", plugin.Descriptor.DisplayName, $"source=plugin_render_raw routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} {BuildPluginRenderHtmlAudit(renderHtml)} rule=release_contract");
            ApplyPluginPresentationLifecycleHint(pluginId, plugin.Descriptor.DisplayName, currentRequestQuery, logPresentationStore, log);
            body = toolWindowContentOnly ? (renderHtml ?? string.Empty) : NormalizePluginPageContent(renderHtml);
            log.Add("PLUGIN_RENDER_RESULT", plugin.Descriptor.DisplayName, $"source={(toolWindowContentOnly ? "plugin_render_full_html_compat" : "plugin_render_page_fragment_normalized")} routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} preserveFullHtml={toolWindowContentOnly} {BuildPluginRenderHtmlAudit(body)} rule=plugin_page_fragment_normalization_contract");
            var pageActionAudit = BuildPluginPageActionAudit(body);
            log.Add("PLUGIN_PAGE_ACTION_CANDIDATE", plugin.Descriptor.DisplayName, $"result={(pageActionAudit.StartsWith("candidates=0", StringComparison.Ordinal) ? "NONE" : "DETECTED")} routeSegment={SafePluginActionValue(publicRoute)} toolWindowContentOnly={toolWindowContentOnly} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} {pageActionAudit} rule=plugin_page_action_contract");
        }
        else
        {
            return Results.NotFound("Plugin UI not found.");
        }

        log.Add("PLUGIN_SAFE_EVENT_INJECT", plugin?.Descriptor.DisplayName ?? publicRoute, $"action=render result=INJECTED routeSegment={SafePluginActionValue(publicRoute)} toolWindowContentOnly={toolWindowContentOnly} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} script=external_and_inline_guarded hostKind={SafePluginActionValue(toolWindows.GetCapabilities().HostKind)} rule=release_contract");
        return Results.Content(BuildPluginShellHtml(title, publicRoute, body, toolWindowContentOnly, hostSelectedTheme, hostEffectiveTheme), "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        var exMessage = $"{ex.GetType().Name}: {ex.Message}";
        var stackSummary = (ex.StackTrace ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        if (stackSummary.Length > 500) stackSummary = stackSummary[..500];
        log.Add("PLUGIN_RENDER_EXCEPTION", plugin?.Descriptor.DisplayName ?? publicRoute, $"routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} exceptionType={SafePluginActionValue(ex.GetType().Name)} message={SafePluginActionValue(ex.Message)} stack={SafePluginActionValue(stackSummary)} rule=release_contract");
        var errorBody = BuildPluginRenderErrorBody(plugin?.Descriptor.DisplayName ?? publicRoute, publicRoute, exMessage);
        return Results.Content(BuildPluginShellHtml(title, publicRoute, errorBody, toolWindowContentOnly, hostSelectedTheme, hostEffectiveTheme), "text/html; charset=utf-8");
    }
}


static void ApplyPluginPresentationLifecycleHint(string pluginId, string pluginName, IReadOnlyDictionary<string, string> requestQuery, LogPresentationStore logPresentationStore, LogRepository log)
{
    if (string.IsNullOrWhiteSpace(pluginId) || requestQuery.Count == 0)
        return;

    var lifecycle = ReadPluginPresentationLifecycleHint(requestQuery);
    if (lifecycle == PluginPresentationLifecycleHint.None)
        return;

    var activeBefore = logPresentationStore.ListLogSnapshots()
        .Where(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var activePolicyBefore = logPresentationStore.ListLogPolicies()
        .Where(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var inactiveBefore = logPresentationStore.ListInactiveLogSnapshots()
        .Where(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var actionName = lifecycle.ToString().ToLowerInvariant();

    if (lifecycle == PluginPresentationLifecycleHint.Disable)
    {
        logPresentationStore.ClearLogSnapshot(pluginId);
        logPresentationStore.ClearLogPolicy(pluginId);
        var activeAfterDisable = logPresentationStore.ListLogSnapshots().Count(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        var activePolicyAfterDisable = logPresentationStore.ListLogPolicies().Count(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        var inactiveAfterDisable = logPresentationStore.ListInactiveLogSnapshots().Count(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        log.Add("PLUGIN_PRESENTATION_LIFECYCLE", pluginName,
            $"result=SUSPENDED action=disable pluginId={SafePluginActionValue(pluginId)} activeBefore={activeBefore.Length} activePolicyBefore={activePolicyBefore.Length} inactiveBefore={inactiveBefore.Length} activeAfter={activeAfterDisable} activePolicyAfter={activePolicyAfterDisable} inactiveAfter={inactiveAfterDisable} reason=render_lifecycle_hint rule=plugin_presentation_lifecycle_contract");
        return;
    }

    if ((activeBefore.Length > 0 || activePolicyBefore.Length > 0) && inactiveBefore.Length == 0)
    {
        var activeSample = activeBefore.FirstOrDefault();
        log.Add("PLUGIN_PRESENTATION_LIFECYCLE", pluginName,
            $"result=ACTIVE_PRESENT action={actionName} pluginId={SafePluginActionValue(pluginId)} restored=0 activeBefore={activeBefore.Length} activePolicyBefore={activePolicyBefore.Length} inactiveBefore=0 activeViewKeys={SafePluginActionValue(PluginPresentationViewKeys(activeBefore))} inactiveViewKeys=- entries={(activeSample?.Snapshot.Entries.Count ?? 0)} source=already_active_before_lifecycle_hint reason=render_lifecycle_hint rule=plugin_presentation_lifecycle_contract");
        return;
    }

    var restored = logPresentationStore.ReactivateInactiveLogSnapshots(pluginId);
    var activeAfter = logPresentationStore.ListLogSnapshots()
        .Where(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var inactiveAfter = logPresentationStore.ListInactiveLogSnapshots()
        .Where(x => string.Equals(x.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var sample = restored.FirstOrDefault() ?? activeAfter.FirstOrDefault();
    var result = restored.Count > 0
        ? "REACTIVATED"
        : (activeAfter.Length > 0 ? "ACTIVE_PRESENT_AFTER_RENDER" : "NO_PRESENTATION_SNAPSHOT");
    var source = restored.Count > 0
        ? "inactive_snapshot_reactivated"
        : (activeAfter.Length > 0 ? "plugin_render_or_existing_active_snapshot" : "no_active_or_inactive_snapshot");

    log.Add("PLUGIN_PRESENTATION_LIFECYCLE", pluginName,
        $"result={result} action={actionName} pluginId={SafePluginActionValue(pluginId)} restored={restored.Count} activeBefore={activeBefore.Length} inactiveBefore={inactiveBefore.Length} activeAfter={activeAfter.Length} inactiveAfter={inactiveAfter.Length} activeViewKeys={SafePluginActionValue(PluginPresentationViewKeys(activeAfter.Length > 0 ? activeAfter : activeBefore))} inactiveViewKeys={SafePluginActionValue(PluginPresentationViewKeys(inactiveAfter.Length > 0 ? inactiveAfter : inactiveBefore))} entries={(sample?.Snapshot.Entries.Count ?? 0)} source={source} reason=render_lifecycle_hint rule=plugin_presentation_lifecycle_contract");
}

static string PluginPresentationViewKeys(IEnumerable<PluginLogPresentationSnapshot> snapshots)
{
    var value = string.Join(",", snapshots
        .Select(x => x.Snapshot.ViewKey)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    return string.IsNullOrWhiteSpace(value) ? "-" : value;
}

static PluginPresentationLifecycleHint ReadPluginPresentationLifecycleHint(IReadOnlyDictionary<string, string> query)
{
    static bool Truthy(string? value) => IsTruthy(value);
    static string V(IReadOnlyDictionary<string, string> q, string key)
        => q.TryGetValue(key, out var value) ? (value ?? string.Empty).Trim() : string.Empty;
    static bool IsEnableValue(string value)
        => value.Equals("enable", StringComparison.OrdinalIgnoreCase)
           || value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("activate", StringComparison.OrdinalIgnoreCase)
           || value.Equals("active", StringComparison.OrdinalIgnoreCase)
           || value.Equals("refresh", StringComparison.OrdinalIgnoreCase)
           || value.Equals("reactivate", StringComparison.OrdinalIgnoreCase);
    static bool IsDisableValue(string value)
        => value.Equals("disable", StringComparison.OrdinalIgnoreCase)
           || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
           || value.Equals("clear", StringComparison.OrdinalIgnoreCase)
           || value.Equals("deactivate", StringComparison.OrdinalIgnoreCase)
           || value.Equals("inactive", StringComparison.OrdinalIgnoreCase);
    static bool IsSaveCloseValue(string value)
        => value.Equals("saveclose", StringComparison.OrdinalIgnoreCase)
           || value.Equals("save-close", StringComparison.OrdinalIgnoreCase)
           || value.Equals("save_close", StringComparison.OrdinalIgnoreCase)
           || value.Equals("save", StringComparison.OrdinalIgnoreCase);
    static bool IsLifecycleCommandKey(string key)
        => key.Equals("presentationLifecycle", StringComparison.OrdinalIgnoreCase)
           || key.Equals("presentationCommand", StringComparison.OrdinalIgnoreCase)
           || key.Equals("lifecycle", StringComparison.OrdinalIgnoreCase)
           || key.Equals("lifecycleCommand", StringComparison.OrdinalIgnoreCase);

    // The saved post-action enabled value is the authoritative presentation state.
    if (query.ContainsKey("enabled"))
        return Truthy(V(query, "enabled")) ? PluginPresentationLifecycleHint.Enable : PluginPresentationLifecycleHint.Disable;

    // Explicit lifecycle/command values have priority over compatibility booleans.
    // This prevents a form that carries both "command=disable" and an unrelated/stale
    // "enabled=true" field from reactivating a snapshot during the same request.
    foreach (var pair in query)
    {
        if (!IsLifecycleCommandKey(pair.Key))
            continue;
        var value = (pair.Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            continue;
        if (IsDisableValue(value))
            return PluginPresentationLifecycleHint.Disable;
        if (IsEnableValue(value))
            return PluginPresentationLifecycleHint.Enable;
        if (IsSaveCloseValue(value))
            break;
    }

    return PluginPresentationLifecycleHint.None;
}

// release_contract: 既存プラグインToolWindow/Actionのホスト管理入口。
// Capability API整理後も、既存ToolWindow経路は本体標準ルートとして維持する。
#if TVAIR_DEVELOPER_DIAGNOSTICS
app.MapGet("/api/plugins/safe-event/client-log", (HttpRequest http, LogRepository log) =>
{
    var q = http.Query;
    static string Q(IQueryCollection values, string key) => values.TryGetValue(key, out var value) ? value.ToString() : string.Empty;
    var phase = Q(q, "phase");
    var pluginId = Q(q, "pluginId");
    var route = Q(q, "routeSegment");
    var eventType = phase.StartsWith("bind_", StringComparison.OrdinalIgnoreCase) ? "PLUGIN_SAFE_EVENT_CLIENT_INIT"
        : phase.StartsWith("payload_", StringComparison.OrdinalIgnoreCase) ? "PLUGIN_SAFE_EVENT_PAYLOAD"
        : phase.StartsWith("post_", StringComparison.OrdinalIgnoreCase) ? "PLUGIN_SAFE_EVENT_POST"
        : "PLUGIN_SAFE_EVENT_CLIENT";
    log.Add(eventType, string.IsNullOrWhiteSpace(pluginId) ? route : pluginId,
        $"phase={SafePluginActionValue(phase)} interactionId={SafePluginActionValue(Q(q, "interactionId"))} event={SafePluginActionValue(Q(q, "event"))} action={SafePluginActionValue(Q(q, "action"))} pluginId={SafePluginActionValue(pluginId)} route={SafePluginActionValue(route)} tag={SafePluginActionValue(Q(q, "tag"))} type={SafePluginActionValue(Q(q, "type"))} tokenPresent={SafePluginActionValue(Q(q, "hasToken"))} payloadCount={SafePluginActionValue(Q(q, "payloadCount"))} payloadKeys={SafePluginActionValue(Q(q, "payloadKeys"))} endpoint={SafePluginActionValue(Q(q, "endpoint"))} status={SafePluginActionValue(Q(q, "status"))} reason={SafePluginActionValue(Q(q, "reason"))} readyState={SafePluginActionValue(Q(q, "readyState"))} candidates={SafePluginActionValue(Q(q, "candidates"))} hostKind={SafePluginActionValue(Q(q, "hostKind"))} rule=safe_event_host_capture_contract");
    return Results.NoContent();
});
#endif

static async Task<IResult> RenewPluginActionTokenEndpoint(
    HttpRequest http,
    PluginActionTokenStore actionTokens,
    LogRepository log)
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "form_content_required" });

    var form = await http.ReadFormAsync();
    var token = form["actionToken"].ToString();
    if (string.IsNullOrWhiteSpace(token)) token = form["token"].ToString();
    var pluginId = form["pluginId"].ToString();
    var routeSegment = form["routeSegment"].ToString().Trim().Trim('/');

    // plugin_action_token_keepalive_contract: renew only an already-issued token whose plugin/route
    // identity still matches. No windowId is required, so long-lived Page surfaces and ToolWindows
    // share the same Host-owned lifetime contract without weakening token identity validation.
    if (!actionTokens.Renew(token, pluginId, routeSegment, out var reason, out _))
    {
        log.Add("PLUGIN_ACTION_TOKEN_KEEPALIVE", string.IsNullOrWhiteSpace(pluginId) ? routeSegment : pluginId,
            $"result=DENIED reason={SafePluginActionValue(reason)} routeSegment={SafePluginActionValue(routeSegment)} rule=plugin_action_token_keepalive_contract");
        return Results.BadRequest(new { error = reason });
    }

    return Results.NoContent();
}

static async Task<IResult> ValidatePluginPageActionTokenEndpoint(
    HttpRequest http,
    PluginActionTokenStore actionTokens,
    LogRepository log)
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "form_content_required" });

    var form = await http.ReadFormAsync();
    var token = form["actionToken"].ToString();
    if (string.IsNullOrWhiteSpace(token)) token = form["token"].ToString();
    var pluginId = form["pluginId"].ToString();
    var routeSegment = form["routeSegment"].ToString().Trim().Trim('/');

    // plugin_page_action_token_recovery_contract: Page history/BFCache restoration may revive an old
    // document after its token expired while that Page was not active. Validate possession only;
    // never issue or resurrect a token here. A stale Page must navigate through /plugin/{route}
    // so RenderPluginHtml remains the single source of new action tokens.
    if (!actionTokens.Validate(token, pluginId, routeSegment, out var reason))
    {
        log.Add("PLUGIN_ACTION_TOKEN_PAGE_VALIDATE", string.IsNullOrWhiteSpace(pluginId) ? routeSegment : pluginId,
            $"result=STALE reason={SafePluginActionValue(reason)} routeSegment={SafePluginActionValue(routeSegment)} recovery=host_plugin_render rule=plugin_page_action_token_recovery_contract");
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    return Results.NoContent();
}

static Task<IResult> DispatchPluginOwnedActionEndpoint(
    HttpRequest http,
    PluginRegistry registry,
    PluginActionTokenStore actionTokens,
    PluginWindowSessionStore windows,
    PluginToolWindowHostService toolWindows,
    PluginBoundaryGate boundaryGate,
    LogRepository log)
{
    log.Add("PLUGIN_ACTION_HTTP_ROUTE", "POST",
        $"result=ACCEPTED path={SafePluginActionValue(http.Path.Value)} contentType={SafePluginActionValue(http.ContentType)} hasFormContentType={http.HasFormContentType} logicalRoute=/plugin-action physicalEndpoint=/api/plugins/action rule=plugin_action_http_route_contract");
    return HandlePluginActionDispatchAsync(http, registry, actionTokens, windows, toolWindows, boundaryGate, log);
}

// plugin_action_http_route_contract: /api/plugins/action is the only physical mutation endpoint.
// /plugin-action is a logical ActionContract route identifier and must never be used as a POST target.
// Token keepalive and Page restoration validation are Host-owned lifetime maintenance; neither dispatches a plugin mutation.
app.MapPost("/api/plugins/action-token/renew", RenewPluginActionTokenEndpoint);
app.MapPost("/api/plugins/action-token/validate", ValidatePluginPageActionTokenEndpoint);
app.MapPost("/api/plugins/action", DispatchPluginOwnedActionEndpoint);
app.MapMethods("/api/plugins/action", new[] { "OPTIONS" }, () => Results.NoContent());
app.MapPost("/api/plugins/window", (HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, PluginBoundaryGate boundaryGate, LogRepository log) =>
    HandlePluginWindowDispatchAsync(http, registry, actionTokens, windows, toolWindows, boundaryGate, log));

// plugin_window_close_contract: old WebBrowser/tool-window content can accidentally navigate
// close buttons as a normal GET. Treat only closeWindow/close with a valid window token/context
// as a host-handled close request; this is a compatibility shim, not a generic GET mutation API.
app.MapGet("/api/plugins/window", (HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, PluginBoundaryGate boundaryGate, LogRepository log) =>
    HandlePluginWindowDispatchAsync(http, registry, actionTokens, windows, toolWindows, boundaryGate, log));
app.MapGet("/plugin-window/{windowId}", (string windowId, HttpRequest http, PluginWindowSessionStore windows, LogRepository log) =>
    RenderPluginWindowHost(windowId, http, windows, log));
app.MapGet("/plugin-window/{windowId}/state", (string windowId, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, LogRepository log) =>
    RenderPluginWindowState(windowId, windows, toolWindows, log));
app.MapGet("/api/plugins/window/capabilities", (PluginToolWindowHostService toolWindows, LogRepository log) =>
    RenderPluginWindowHostCapabilities(toolWindows, log));

app.MapGet("/plugin/{route}", (string route, HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, ExternalTunerLeaseService externalTuners, ViewerSessionRegistry viewerSessions, IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, PluginBoundaryGate boundaryGate, LogPresentationStore logPresentationStore, LogRepository log) => RenderPluginHtml(route, http, registry, actionTokens, windows, toolWindows, externalTuners, viewerSessions, tvTestOptions, ini, tunerProfiles, boundaryGate, logPresentationStore, log));

// 1.0.0互換URL。今後は /plugin/{route} を正式入口とする。


// ─── バージョン情報 ───────────────────────────────────────────────

// 復号実行・監視はTvAIrEpgRecへ集約し、Host側に別の実行APIを持たない。
// 番組表のチェーンボタン候補はHost側の共通成立条件で一括評価する。
// UIは候補IDを表示へ投影するだけで、同一SID・隣接・親状態を再判定しない。
app.MapPost("/api/chain-reservation-candidates", (ChainCandidatePreviewRequest request, ReservationStore store) =>
{
    var frames = request.Events ?? Array.Empty<ChainCandidateEventFrame>();
    if (frames.Count == 0)
        return Results.Ok(new { candidates = Array.Empty<object>(), checkedCount = 0, eligibleCount = 0 });

    var reservations = store.GetAll()
        .Where(r => r.Source != ReservationSource.Epg)
        .ToList();
    var featureEnabled = request.LaterProgramPriorityEnabled && request.PseudoContinuousRecordingEnabled;
    var now = DateTime.Now;
    var candidates = new List<object>();
    var checkedCount = 0;

    Reservation? FindPredecessor(ChainCandidateEventFrame frame)
    {
        if (frame.EventId == 0) return null;
        return reservations
            .Where(r => ChainReservationEligibilityContract.IsActivePredecessorStatus(r.Status))
            .Where(r => r.NetworkId == frame.NetworkId
                && r.TransportStreamId == frame.TransportStreamId
                && r.ServiceId == frame.ServiceId
                && r.EventId == frame.EventId)
            .OrderByDescending(r => r.Status == ReservationStatus.Recording)
            .ThenByDescending(r => r.Status == ReservationStatus.Starting)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault();
    }

    bool TargetAlreadyReserved(ChainCandidateEventFrame frame)
    {
        if (frame.EventId == 0) return false;
        return reservations.Any(r =>
            r.NetworkId == frame.NetworkId
            && r.TransportStreamId == frame.TransportStreamId
            && r.ServiceId == frame.ServiceId
            && r.EventId == frame.EventId
            && r.Status is ReservationStatus.Scheduled or ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping);
    }

    foreach (var serviceFrames in frames.GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId)))
    {
        var ordered = serviceFrames.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EventId).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            checkedCount++;
            var previousFrame = ordered[i - 1];
            var target = ordered[i];
            var predecessor = FindPredecessor(previousFrame);
            var eligibility = ChainReservationEligibilityContract.EvaluateOffer(
                predecessor,
                target.NetworkId,
                target.TransportStreamId,
                target.ServiceId,
                target.Start,
                target.End,
                !string.IsNullOrWhiteSpace(target.Title),
                TargetAlreadyReserved(target),
                now,
                featureEnabled);
            if (!eligibility.IsEligible || predecessor is null)
                continue;

            candidates.Add(new
            {
                targetKey = target.Key,
                predecessorReservationId = predecessor.Id,
                reason = eligibility.ReasonToken
            });
        }
    }

    return Results.Ok(new
    {
        candidates,
        checkedCount,
        eligibleCount = candidates.Count,
        featureEnabled,
        rule = "chain_reservation_eligibility_contract"
    });
});

app.MapGet("/api/version", () => Results.Ok(new
{
    product = "TvAIr",
    version = GetTvAIrAppVersion()
}));

static string GetTvAIrAppVersion()
{
    var ver = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion
        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
    var plus = ver.IndexOf('+');
    if (plus >= 0) ver = ver[..plus];
    return ver;
}


static void RunTvAIrEpgRecStartupOrphanSafety(LogRepository log)
{
    const string Rule = "epg_startup_orphan_safety_contract";
    try
    {
        var workerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TvAIrEpgRec.exe"));
        var currentStartedAt = Process.GetCurrentProcess().StartTime;
        var candidates = Process.GetProcessesByName("TvAIrEpgRec");
        var scanned = 0;
        var sameBase = 0;
        var terminated = 0;
        var auditOnly = 0;
        foreach (var proc in candidates)
        {
            scanned++;
            try
            {
                var path = string.Empty;
                try { path = proc.MainModule?.FileName ?? string.Empty; } catch { }
                var pathMatches = !string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFullPath(path), workerPath, StringComparison.OrdinalIgnoreCase);
                if (!pathMatches) continue;
                sameBase++;

                DateTime startedAt;
                try { startedAt = proc.StartTime; } catch { startedAt = DateTime.MinValue; }
                var ageSec = startedAt == DateTime.MinValue ? -1 : (int)Math.Max(0, (DateTime.Now - startedAt).TotalSeconds);
                var predatesThisTvair = startedAt != DateTime.MinValue && startedAt < currentStartedAt.AddSeconds(-5);
                var commandLine = TryGetProcessCommandLine(proc.Id);
                var isEpgWorker = ContainsAny(commandLine, "--mode epg", "mode epg", "production-epg", "epg_job_", "epg_result_");
                var isRecordWorker = ContainsAny(commandLine, "--mode record", "mode record", "record_job_", "record_result_");
                var canTerminate = predatesThisTvair && isEpgWorker && !isRecordWorker;

                if (canTerminate)
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        terminated++;
                        log.Add("EPG_ORPHAN_SAFETY", "STARTUP",
                            $"result=TERMINATED pid={proc.Id} ageSec={ageSec} reason=stale_same_base_epg_worker_predates_current_tvair path={SafePathForLog(path)} commandLine={SafePathForLog(commandLine)} rule={Rule}");
                    }
                    catch (Exception ex)
                    {
                        auditOnly++;
                        log.Add("EPG_ORPHAN_SAFETY", "STARTUP",
                            $"result=TERMINATE_FAILED pid={proc.Id} ageSec={ageSec} error={ex.GetType().Name}:{SafePathForLog(ex.Message)} action=audit_only path={SafePathForLog(path)} commandLine={SafePathForLog(commandLine)} rule={Rule}");
                    }
                }
                else
                {
                    auditOnly++;
                    log.Add("EPG_ORPHAN_SAFETY", "STARTUP",
                        $"result=AUDIT_ONLY pid={proc.Id} ageSec={ageSec} predatesCurrentTvair={predatesThisTvair} isEpgWorker={isEpgWorker} isRecordWorker={isRecordWorker} reason=safety_gate_not_met path={SafePathForLog(path)} commandLine={SafePathForLog(commandLine)} rule={Rule}");
                }
            }
            catch (Exception ex)
            {
                auditOnly++;
                log.Add("EPG_ORPHAN_SAFETY", "STARTUP",
                    $"result=WARN pid={proc.Id} error={ex.GetType().Name}:{SafePathForLog(ex.Message)} action=audit_only rule={Rule}");
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        log.Add("EPG_ORPHAN_SAFETY", "STARTUP_SUMMARY",
            $"result=OK scanned={scanned} sameBase={sameBase} terminated={terminated} auditOnly={auditOnly} policy=terminate_stale_same_base_epg_only_no_record_worker rule={Rule}");
    }
    catch (Exception ex)
    {
        log.Add("EPG_ORPHAN_SAFETY", "STARTUP_SUMMARY",
            $"result=WARN error={ex.GetType().Name}:{SafePathForLog(ex.Message)} action=continue_startup rule={Rule}");
    }
}

static bool ContainsAny(string? value, params string[] needles)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    foreach (var n in needles)
    {
        if (value.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
    }
    return false;
}

static string TryGetProcessCommandLine(int pid)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"$p=Get-CimInstance Win32_Process -Filter 'ProcessId={pid}' -ErrorAction SilentlyContinue; if ($p) {{ $p.CommandLine }}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi);
        if (p is null) return string.Empty;
        if (!p.WaitForExit(2000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return string.Empty;
        }
        var output = p.StandardOutput.ReadToEnd().Trim();
        if (output.Length > 2000) output = output[..2000];
        return output;
    }
    catch
    {
        return string.Empty;
    }
}

static string SafePathForLog(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    return value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
}

static IEnumerable<string> EnumerateRuntimeReleaseMarkerFiles(string baseDir)
{
    try
    {
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(baseDir, "release_*.txt", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return !string.IsNullOrWhiteSpace(name)
                    && System.Text.RegularExpressions.Regex.IsMatch(name, @"^release_\d+_\d+_\d+\.txt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            })
            .ToArray();
    }
    catch
    {
        return Array.Empty<string>();
    }
}

static string CleanupRuntimeReleaseMarkerFiles(string baseDir)
{
    try
    {
        var markers = EnumerateRuntimeReleaseMarkerFiles(baseDir).ToList();
        var deleted = 0;
        var failed = 0;
        var failedNames = new List<string>();

        foreach (var path in markers)
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch
            {
                failed++;
                if (failedNames.Count < 3) failedNames.Add(Path.GetFileName(path) ?? "-");
            }
        }

        var failedSample = failedNames.Count == 0 ? "-" : string.Join(",", failedNames);
        return $"deleted={deleted} failed={failed} failedSample={failedSample}";
    }
    catch (Exception ex)
    {
        return "failed_" + ex.GetType().Name;
    }
}

#if TVAIR_DEVELOPER_DIAGNOSTICS
static string BuildReleaseNotesAudit(string baseDir)
{
    try
    {
        var notesPath = Path.Combine(baseDir, "RELEASE_NOTES.txt");
        var markerResidueCleanup = CleanupRuntimeReleaseMarkerFiles(baseDir);
        var remainingMarkers = EnumerateRuntimeReleaseMarkerFiles(baseDir).Count();
        return $"releaseNotes=RELEASE_NOTES.txt releaseNotesExists={File.Exists(notesPath)} releaseMarkerFiles=disabled markerResidueCleanup={markerResidueCleanup} remainingReleaseMarkers={remainingMarkers} releaseHistory=single_file";
    }
    catch (Exception ex)
    {
        return $"releaseNotes=RELEASE_NOTES.txt releaseNotesAuditError={ex.GetType().Name}:{SafePathForLog(ex.Message)} releaseMarkerFiles=disabled releaseHistory=single_file";
    }
}
#endif

static void EmitTvAIrRuntimeIdentityAudit(LogRepository log)
{
#if !TVAIR_DEVELOPER_DIAGNOSTICS
    // 公開版でも旧開発成果物から上書き更新された場合のrelease marker残留だけは掃除する。
    // 開発ログそのものは生成しない。
    try { _ = CleanupRuntimeReleaseMarkerFiles(AppContext.BaseDirectory); } catch { }
    return;
#else
    static string Stamp(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "missing";
            var fi = new FileInfo(path);
            return $"exists=True size={fi.Length} writeLocal={fi.LastWriteTime:yyyy-MM-dd HH:mm:ss} writeUtc={fi.LastWriteTimeUtc:O}";
        }
        catch (Exception ex)
        {
            return $"error={ex.GetType().Name}:{ex.Message}";
        }
    }

    try
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var tvairExe = Environment.ProcessPath ?? asm.Location;
        var workerPath = Path.Combine(AppContext.BaseDirectory, "TvAIrEpgRec.exe");
        var releaseNotesAudit = BuildReleaseNotesAudit(AppContext.BaseDirectory);
        var buildConfiguration = asm
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>()
            .FirstOrDefault()?.Configuration ?? "unknown";
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Replace(' ', '_');
        var processArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var osArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;

        // developer_log_header_contract:
        // APP_BINARY_IDENTITY は通常の循環ログ本体ではなく固定ヘッダとして保持する。
        // これにより長期連続稼働で10,000件を超えても、また開発者ログを途中クリアしても、
        // 解析に必要なビルド/契約/実行環境の正本を必ず先頭に残す。
        log.SetPinnedHeader(new LogEntry
        {
            Event = "APP_BINARY_IDENTITY",
            Title = "BUILD_ENVIRONMENT",
            Message =
                $"tvairVersion={GetTvAIrAppVersion()} buildConfiguration={buildConfiguration} targetFramework=net8.0-windows " +
                $"runtime={framework} processArch={processArch} osArch={osArch} pluginSdk={TvAIrVersionContract.PluginSdkVersion} hostContract={TvAIrVersionContract.PluginHostContractVersion} " +
                $"tvairExe={Path.GetFileName(tvairExe)} tvairFile={Stamp(tvairExe)} " +
                $"workerFileName={Path.GetFileName(workerPath)} workerFile={Stamp(workerPath)} {releaseNotesAudit} " +
                $"baseDir=app_base rule=developer_log_header_contract rollbackPoint=True rollbackBase=release_contract ntp=removed recordFileName=tvtest_ini_template pluginUiAction=host_action_dispatch_value_contract logPolicy=release_noise_reduce",
            CreatedAt = DateTime.Now
        });
    }
    catch (Exception ex)
    {
        // ヘッダ構築そのものに失敗した場合だけ通常ログへ残す。
        log.Add("APP_BINARY_IDENTITY", "ERROR", $"error={ex.GetType().Name}:{ex.Message} rule=developer_log_header_contract");
    }
#endif
}


// ─── EPG API ─────────────────────────────────────────────────────

// EPG取得ステータス
app.MapGet("/api/epg/status", (EpgCapture capture, EpgScheduler scheduler) =>
{
    var status = capture.GetStatus();
    var run = scheduler.GetRunState();
    return Results.Ok(status with
    {
        RunSource = string.IsNullOrWhiteSpace(status.RunSource) ? run.Source : status.RunSource,
        UiMode = run.IsRunning ? run.UiMode : status.UiMode,
        CancelRoute = run.IsRunning ? run.CancelRoute : status.CancelRoute
    });
});

// ProgramGuide scheduled-Daily time-range projection.
// Read-only: consume only EpgScheduler's persisted SystemDailyEpg reservation rows;
// do not reconstruct planned time from settings and do not alter System EPG responsibility.
app.MapGet("/api/epg/scheduled-daily-windows", (ReservationStore store) =>
{
    var now = DateTime.Now;
    var rows = store.GetAll()
        .Where(ReservationIntentContract.IsDailyEpg)
        .Where(r => r.Status is ReservationStatus.Scheduled or ReservationStatus.Recording)
        .Where(r => r.EndTime > now)
        .GroupBy(r => new { r.StartTime, r.EndTime })
        .Select(g => new { startTime = g.Key.StartTime, endTime = g.Key.EndTime })
        .OrderBy(x => x.startTime)
        .ToArray();
    return Results.Ok(rows);
});

// EPG取得ジョブ実行契約状態（メニューガード用）
app.MapGet("/api/epg/run-state", (EpgScheduler scheduler) =>
    Results.Ok(scheduler.GetRunState()));

// TvAIr終了（メニュー操作用）
app.MapPost("/api/app/exit", (string? source, LogRepository log, IHostApplicationLifetime lifetime) =>
{
    var safeSource = string.IsNullOrWhiteSpace(source) ? "WebMenu" : source.Trim().Replace("\r", " ").Replace("\n", " ");
    try { log.Add("APP_EXIT_REQUEST", "Menu", $"source={safeSource} action=StopApplication commonRoute=/api/app/exit rule=release_contract"); } catch { }
    _ = Task.Run(async () =>
    {
        await Task.Delay(150).ConfigureAwait(false);
        lifetime.StopApplication();
    });
    return Results.Ok(new { accepted = true });
});

// EPG取得を今すぐ実行
app.MapPost("/api/epg/run", (HttpRequest request, EpgScheduler scheduler) =>
{
    try
    {
        if (!request.Query.TryGetValue("scope", out var q) || string.IsNullOrWhiteSpace(q.ToString()))
        {
            var state = scheduler.GetRunState();
            return Results.Ok(new { started = false, scope = "-", runState = state, message = "EPG取得範囲が指定されていません。" });
        }

        var scope = q.ToString().Trim();
        var normalizedScope = scope.Equals("GR", StringComparison.OrdinalIgnoreCase)
            ? "GR"
            : scope.Equals("BS", StringComparison.OrdinalIgnoreCase)
                ? "BS"
                : scope.Equals("CS", StringComparison.OrdinalIgnoreCase)
                    ? "CS"
                    : scope.Equals("BSCS", StringComparison.OrdinalIgnoreCase) || scope.Equals("BS/CS", StringComparison.OrdinalIgnoreCase)
                        ? "BSCS"
                        : scope.Equals("All", StringComparison.OrdinalIgnoreCase)
                            ? "All"
                            : string.Empty;
        var silent = request.Query.TryGetValue("silent", out var silentQuery)
            && (string.Equals(silentQuery.ToString(), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(silentQuery.ToString(), "1", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(normalizedScope))
        {
            var state = scheduler.GetRunState();
            return Results.Ok(new { started = false, scope, runState = state, message = "EPG取得範囲が不正です。" });
        }
        var sourceBase = request.Query.TryGetValue("source", out var sourceQuery)
            ? NormalizeManualEpgRunSource(sourceQuery.ToString(), silent)
            : (silent ? "WebApi.SilentEpg" : "WebApi.EpgRun");
        var source = $"{sourceBase}.{normalizedScope}";
        var started = scheduler.TriggerNow(source, silent: silent, targetScope: normalizedScope);
        var runState = scheduler.GetRunState();
        var block = started ? null : scheduler.GetLastStartBlockInfo(normalizedScope);
        var displayScope = normalizedScope.Equals("GR", StringComparison.OrdinalIgnoreCase)
            ? "地上波"
            : normalizedScope.Equals("BS", StringComparison.OrdinalIgnoreCase)
                ? "BS"
                : normalizedScope.Equals("CS", StringComparison.OrdinalIgnoreCase)
                    ? "CS"
                    : normalizedScope.Equals("BSCS", StringComparison.OrdinalIgnoreCase)
                        ? "BS/CS"
                        : "全体";
        var displayBlockedGroup = string.IsNullOrWhiteSpace(block?.BlockedGroup)
            ? string.Empty
            : block!.BlockedGroup.Equals("GR", StringComparison.OrdinalIgnoreCase)
                ? "地上波"
                : block.BlockedGroup.Equals("BSCS", StringComparison.OrdinalIgnoreCase)
                    ? "BS/CS"
                    : block.BlockedGroup;
        return Results.Ok(new
        {
            started,
            scope = normalizedScope,
            displayScope,
            runState,
            blocked = block is not null,
            blockedReason = block?.Reason ?? string.Empty,
            blockedGroup = block?.BlockedGroup ?? string.Empty,
            displayBlockedGroup,
            message = started ? "EPG取得を開始しました" : block?.DisplayMessage ?? "EPG取得を開始できません",
            silent,
            guidance = string.Empty
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { started = false, message = $"エラー: {ex.Message}" });
    }
});

// EPG取得をキャンセル
app.MapPost("/api/epg/cancel", (HttpRequest request, EpgScheduler scheduler) =>
{
    var source = request.Query.TryGetValue("source", out var q) ? q.ToString() : "WebApi.EpgCancel";
    var accepted = scheduler.CancelVisible(source);
    var runState = scheduler.GetRunState();
    return Results.Ok(new { accepted, runState, message = accepted ? "Visible EPG取得のキャンセル要求を送信しました。" : "キャンセル対象のVisible EPG取得は実行中ではありません。" });
});

// 番組表データ取得（日付指定）
app.MapGet("/api/epg/events", (string? date, EpgStore store, ReservationStore reservations, ChannelFileLoader channelLoader, LogRepository log, IProgramEventSource programEvents) =>
{
#if TVAIR_DEVELOPER_DIAGNOSTICS
    var requestStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
    var baseDate = DateOnly.TryParse(date, out var parsed)
        ? parsed
        : DateOnly.FromDateTime(DateTime.Now);

    var dayStart = baseDate.ToDateTime(new TimeOnly(4, 0));  // 4:00 開始（TvRock準拠）
    var dayEnd   = dayStart.AddDays(1);

    var displayDate = dayStart.ToString("M月d日・dddd",
        System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));

    var channels = BuildCurrentProgramGuideChannels(channelLoader);
    var events = programEvents.GetByRange(dayStart, dayEnd)
        .ToList();

    var chOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var ci = 0; ci < channels.Count; ci++)
    {
        var ch = channels[ci];
        var key = ProgramGuideChannelServiceKey(ch);
        chOrder.TryAdd(key, ci);
    }
    var serviceDisplayNameByKey = channels
        .GroupBy(ProgramGuideChannelServiceKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => g.Key,
            g => string.IsNullOrWhiteSpace(g.First().Name) ? $"SID {g.First().ServiceId}" : g.First().Name,
            StringComparer.OrdinalIgnoreCase);

    var sortedEvents = events
        .OrderBy(e =>
        {
            var key = ProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId);
            return chOrder.TryGetValue(key, out var idx) ? idx : int.MaxValue;
        })
        .ThenBy(e => e.Start)
        .ThenBy(e => e.End)
        .ThenBy(e => e.EventId)
        .ThenBy(e => e.SourceKind, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var timelineEvents = BuildProjectedProgramGuideTimelineEvents(
        sortedEvents,
        channels,
        dayStart,
        dayEnd,
        log,
        baseDate);

    var displayEvents = timelineEvents
        .Select(e => NormalizeProjectedProgramGuideEventForDisplay(e, serviceDisplayNameByKey))
        .ToList();

#if TVAIR_DEVELOPER_DIAGNOSTICS
    var projectedOnlyCount = timelineEvents.Count(e => !e.DbEventExists);
    var dbWithOverlayCount = timelineEvents.Count(e => string.Equals(e.ProjectionState, ProjectedEventStates.DbWithOverlay, StringComparison.OrdinalIgnoreCase));
    log.Add("PROGRAM_GUIDE_PROJECTED_DISPLAY_HANDOFF", "API",
        $"result=OK date={baseDate:yyyy-MM-dd} projectedOnly={projectedOnlyCount} dbWithOverlay={dbWithOverlayCount} displayEvents={displayEvents.Count} elapsedMs={requestStopwatch.ElapsedMilliseconds} diagnostics=release_compact source=IProgramEventSource target=programguide_display dbWrite=none rule=program_guide_projection_contract");

    var dbWithOverlayEvents = timelineEvents
        .Where(e => string.Equals(e.ProjectionState, ProjectedEventStates.DbWithOverlay, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (dbWithOverlayEvents.Count > 0)
    {
        var titleDbUsed = dbWithOverlayEvents.Count(e => string.Equals(e.ProjectionTitleSource, "db", StringComparison.OrdinalIgnoreCase));
        var titleOverlayUsed = dbWithOverlayEvents.Count(e => string.Equals(e.ProjectionTitleSource, "overlay", StringComparison.OrdinalIgnoreCase));
        var titleOverlayCandidateIgnored = dbWithOverlayEvents.Count(e => e.ProjectionTitleDbPresent && e.ProjectionTitleOverlayCandidatePresent);
        var titleBothMissing = dbWithOverlayEvents.Count(e => !e.ProjectionTitleDbPresent && !e.ProjectionTitleOverlayCandidatePresent);

        var outlineDbPresent = dbWithOverlayEvents.Count(e => e.ProjectionOutlineDbPresent);
        var outlineDbMissingOverlayPresent = dbWithOverlayEvents.Count(e => !e.ProjectionOutlineDbPresent && e.ProjectionOutlineOverlayCandidatePresent);
        var outlineOverlayUsed = dbWithOverlayEvents.Count(e => string.Equals(e.ProjectionOutlineSource, "overlay", StringComparison.OrdinalIgnoreCase));
        var outlineDbPresentOverlayIgnored = dbWithOverlayEvents.Count(e => e.ProjectionOutlineDbPresent && e.ProjectionOutlineOverlayCandidatePresent);
        var outlineBothMissing = dbWithOverlayEvents.Count(e => !e.ProjectionOutlineDbPresent && !e.ProjectionOutlineOverlayCandidatePresent);

        var detailDbPresent = dbWithOverlayEvents.Count(e => e.ProjectionDetailDbPresent);
        var detailDbMissingOverlayPresent = dbWithOverlayEvents.Count(e => !e.ProjectionDetailDbPresent && e.ProjectionDetailOverlayCandidatePresent);
        var detailOverlayUsed = dbWithOverlayEvents.Count(e => string.Equals(e.ProjectionDetailSource, "overlay", StringComparison.OrdinalIgnoreCase));
        var detailDbPresentOverlayIgnored = dbWithOverlayEvents.Count(e => e.ProjectionDetailDbPresent && e.ProjectionDetailOverlayCandidatePresent);
        var detailBothMissing = dbWithOverlayEvents.Count(e => !e.ProjectionDetailDbPresent && !e.ProjectionDetailOverlayCandidatePresent);

        log.Add("DB_WITH_OVERLAY_FIELD_MERGE_SUMMARY", "API",
            $"result=OK date={baseDate:yyyy-MM-dd} dbWithOverlay={dbWithOverlayEvents.Count} " +
            $"titleDbUsed={titleDbUsed} titleOverlayUsed={titleOverlayUsed} titleOverlayCandidateIgnored={titleOverlayCandidateIgnored} titleBothMissing={titleBothMissing} " +
            $"outlineDbPresent={outlineDbPresent} outlineDbMissingOverlayPresent={outlineDbMissingOverlayPresent} outlineOverlayUsed={outlineOverlayUsed} outlineDbPresentOverlayIgnored={outlineDbPresentOverlayIgnored} outlineBothMissing={outlineBothMissing} " +
            $"detailDbPresent={detailDbPresent} detailDbMissingOverlayPresent={detailDbMissingOverlayPresent} detailOverlayUsed={detailOverlayUsed} detailDbPresentOverlayIgnored={detailDbPresentOverlayIgnored} detailBothMissing={detailBothMissing} " +
            $"titlePolicy=db_first overlayTitleUse=only_when_db_missing outlinePolicy=db_first_fill_missing detailPolicy=db_first_fill_missing dbWrite=none rule=db_with_overlay_field_merge_contract");
    }

    var rawBlankTitleCount = displayEvents.Count(e => string.IsNullOrEmpty(e.CellText.Title));
    var titleLessDescriptorCount = displayEvents.Count(e => string.IsNullOrEmpty(e.CellText.Title) && string.IsNullOrEmpty(e.RawShortEventDescriptorHex));
    var rawExtendedDescriptorHexCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.RawExtendedEventDescriptorHex));
    var cellTitleCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Title));
    var cellOutlineCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Outline));
    var cellDetailCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Detail));
    var cellItemsCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Items));

    log.Add("PROGRAMGUIDE_CELL_TEXT_DIRECT_HANDOFF", "API",
        $"result=OK date={baseDate:yyyy-MM-dd} events={displayEvents.Count} blankTitle={rawBlankTitleCount} titleLessDescriptor={titleLessDescriptorCount} rawExtendedDescriptorHex={rawExtendedDescriptorHexCount} cellTitle={cellTitleCount} cellOutline={cellOutlineCount} cellDetail={cellDetailCount} cellItems={cellItemsCount} cellTextSource=db_raw_descriptor_common_decoder_or_projected_event boundary=outline_detail_separator_kept dbReadFilters=none dbWrite=none ch2Filter=removed reservationTitleBorrow=removed dtoDirectField=cellText legacyBodyFields=deleted rule=release_contract");

    if (rawBlankTitleCount > 0)
    {
        const int blankTitleDiagnosticLimit = 24;
        var blankTitleDiagnostics = timelineEvents
            .Select((projected, index) => new
            {
                Projected = projected,
                Display = index < displayEvents.Count ? displayEvents[index] : null
            })
            .Where(x => x.Display is not null && string.IsNullOrEmpty(x.Display.CellText.Title))
            .Take(blankTitleDiagnosticLimit)
            .ToList();

        foreach (var item in blankTitleDiagnostics)
        {
            var projected = item.Projected;
            var display = item.Display!;
            var dbTitle = projected.DbEvent is null ? string.Empty : EpgProjection.Title(projected.DbEvent);
            var overlayTitle = projected.Title ?? string.Empty;
            var rawShortHex = projected.DbEvent?.RawShortEventDescriptorHex ?? string.Empty;
            var rawExtendedHex = projected.DbEvent?.RawExtendedEventDescriptorHex ?? string.Empty;
            var rawLoopHex = projected.DbEvent?.RawDescriptorLoopHex ?? string.Empty;

            log.Add("PROGRAMGUIDE_BLANK_TITLE_DIAGNOSTIC", "API",
                $"result=OBSERVED date={baseDate:yyyy-MM-dd} " +
                $"nid={projected.NetworkId} tsid={projected.TransportStreamId} sid={projected.ServiceId} eventId={projected.EventId} " +
                $"start={projected.Start:yyyy-MM-ddTHH:mm:ss} end={projected.End:yyyy-MM-ddTHH:mm:ss} service={SafeProgramGuideDiagnosticValue(display.ServiceName)} " +
                $"projectionState={SafeProgramGuideDiagnosticValue(projected.ProjectionState)} sourceKind={SafeProgramGuideDiagnosticValue(projected.SourceKind)} " +
                $"sourcePluginId={SafeProgramGuideDiagnosticValue(projected.SourcePluginId)} sourceEventKey={SafeProgramGuideDiagnosticValue(projected.SourceEventKey)} " +
                $"dbEventExists={projected.DbEventExists} dbTitlePresent={!string.IsNullOrWhiteSpace(dbTitle)} overlayTitlePresent={!string.IsNullOrWhiteSpace(overlayTitle)} " +
                $"dbTitle={SafeProgramGuideDiagnosticValue(dbTitle)} overlayTitle={SafeProgramGuideDiagnosticValue(overlayTitle)} finalCellTitle={SafeProgramGuideDiagnosticValue(display.CellText.Title)} " +
                $"rawShortPresent={!string.IsNullOrWhiteSpace(rawShortHex)} rawShortBytes={ProgramGuideHexByteLength(rawShortHex)} " +
                $"rawExtendedPresent={!string.IsNullOrWhiteSpace(rawExtendedHex)} rawExtendedBytes={ProgramGuideHexByteLength(rawExtendedHex)} " +
                $"descriptorLoopPresent={!string.IsNullOrWhiteSpace(rawLoopHex)} descriptorLoopBytes={ProgramGuideHexByteLength(rawLoopHex)} " +
                $"projectionTitleSource={SafeProgramGuideDiagnosticValue(projected.ProjectionTitleSource)} " +
                $"rule=programguide_blank_title_source_trace");
        }

        log.Add("PROGRAMGUIDE_BLANK_TITLE_DIAGNOSTIC_SUMMARY", "API",
            $"result=OBSERVED date={baseDate:yyyy-MM-dd} blankTitle={rawBlankTitleCount} emitted={blankTitleDiagnostics.Count} truncated={rawBlankTitleCount > blankTitleDiagnosticLimit} " +
            $"limit={blankTitleDiagnosticLimit} purpose=source_boundary_trace dbWrite=none rule=programguide_blank_title_source_trace");
    }

#endif

    return Results.Ok(new
    {
        date        = baseDate.ToString("yyyy-MM-dd"),
        displayDate,
        dayStart,
        dayEnd,
        count     = displayEvents.Count,
        events    = displayEvents
    });
});

// 番組詳細
app.MapGet("/api/epg/event", (
    ushort networkId, ushort tsId, ushort serviceId, ushort eventId,
    IProgramEventSource programEvents,
    ChannelFileLoader channelLoader) =>
{
    if (!BuildProgramGuideChannelServiceKeySet(BuildCurrentProgramGuideChannels(channelLoader))
        .Contains(ProgramGuideServiceKey3(networkId, tsId, serviceId)))
        return Results.NotFound();
    var ev = programEvents.GetByEventKey(networkId, tsId, serviceId, eventId);
    return ev is null ? Results.NotFound() : Results.Ok(ev.ToEpgEvent());
});

// 番組検索
app.MapGet("/api/epg/search", (
    string?  q,            // キーワード
    bool?    desc,         // 説明文も検索
    string?  services,     // 局identity NID:TSID:SID カンマ区切り
    string?  sids,         // 旧互換: SID カンマ区切り。一意に現在局へ解決できる場合のみ使用
    string?  dow,          // 曜日 カンマ区切り (0=日〜6=土)
    int?     timeFrom,     // 開始時間 (0〜23)
    int?     timeTo,       // 終了時間 (1〜24)
    string?  dateFrom,     // 期間開始 yyyy-MM-dd
    string?  dateTo,       // 期間終了 yyyy-MM-dd
    IProgramEventSource programEvents,
    ChannelFileLoader channelLoader) =>
{
    var serviceKeys = new HashSet<ServiceIdentityContract.Key>();
    foreach (var token in (services ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!ServiceIdentityContract.TryParseKey(token, out var key))
            return Results.BadRequest(new { message = $"対象局identityが不正です: {token}" });
        serviceKeys.Add(key);
    }

    var days = dow?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(d => int.TryParse(d.Trim(), out var v) ? (int?)v : null)
        .Where(v => v.HasValue).Select(v => v!.Value);
    var from = DateOnly.TryParse(dateFrom, out var df)
        ? df.ToDateTime(TimeOnly.MinValue) : (DateTime?)null;
    var to   = DateOnly.TryParse(dateTo,   out var dt)
        ? dt.ToDateTime(TimeOnly.MaxValue) : (DateTime?)null;

    var channels = BuildCurrentProgramGuideChannels(channelLoader);
    foreach (var token in (sids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!ushort.TryParse(token, out var legacySid))
            return Results.BadRequest(new { message = $"旧形式の対象局SIDが不正です: {token}" });

        var matches = channels
            .Where(ch => ch.ServiceId == legacySid)
            .Select(ServiceIdentityContract.From)
            .Distinct()
            .Take(2)
            .ToList();
        if (matches.Count != 1)
            return Results.BadRequest(new
            {
                message = matches.Count == 0
                    ? $"旧形式の対象局 SID={legacySid} を現在の局情報から一意に解決できません。"
                    : $"旧形式の対象局 SID={legacySid} は複数局に一致します。NID:TSID:SIDで指定してください。"
            });
        serviceKeys.Add(matches[0]);
    }

    var daySet = days?.ToHashSet();
    var keyword = (q ?? string.Empty).Trim();
    var searchFrom = from ?? DateTime.Now;
    var searchTo = to ?? searchFrom.AddDays(14);

    IEnumerable<ProjectedProgramEvent> query = programEvents.GetByRange(searchFrom, searchTo)
        .Where(e => e.End >= DateTime.Now);

    if (serviceKeys.Count > 0)
        query = query.Where(e => serviceKeys.Contains(new ServiceIdentityContract.Key(e.NetworkId, e.TransportStreamId, e.ServiceId)));
    if (daySet is { Count: > 0 })
        query = query.Where(e => daySet.Contains((int)e.Start.DayOfWeek));
    if (timeFrom.HasValue)
        query = query.Where(e => e.Start.Hour >= timeFrom.Value);
    if (timeTo.HasValue && timeTo.Value < 24)
        query = query.Where(e => e.Start.Hour < timeTo.Value);
    if (keyword.Length > 0)
    {
        query = desc == true
            ? query.Where(e => ProgramGuideProjectedContains(e.Title, keyword) || ProgramGuideProjectedContains(e.ShortText, keyword) || ProgramGuideProjectedContains(e.ExtendedText, keyword) || ProgramGuideProjectedContains(e.CellText, keyword))
            : query.Where(e => ProgramGuideProjectedContains(e.Title, keyword));
    }

    var events = query.ToList();

    var chOrder  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < channels.Count; i++)
    {
        var ch  = channels[i];
        var key = ProgramGuideChannelServiceKey(ch);
        chOrder.TryAdd(key, i);
    }

    var sorted = events
        .OrderBy(e => e.Start)
        .ThenBy(e =>
        {
            var key = $"{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}";
            return chOrder.TryGetValue(key, out var idx) ? idx : int.MaxValue;
        })
        .Select(e => new
        {
            e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId,
            ServiceName = ServiceIdentityContract.ResolveCurrentServiceName(channels, e.NetworkId, e.TransportStreamId, e.ServiceId, e.ServiceName),
            Title = e.Title,
            Description = e.ShortText,
            ExtendedText = e.ExtendedText,
            e.Genre,
            GenreCodes = e.GenreCodes,
            e.DurationSeconds, e.Start, e.End,
            projectedEventId = e.Key.Value,
            projectionState = e.ProjectionState,
            sourceKind = e.SourceKind,
            sourcePluginId = e.SourcePluginId,
            sourceEventKey = e.SourceEventKey,
            dbEventExists = e.DbEventExists
        });

    return Results.Ok(new { count = events.Count, events = sorted });
});

app.MapGet("/api/epg/tagged", (
    string kind,
    IProgramEventSource programEvents,
    ChannelFileLoader channelLoader) =>
{
    var now = DateTime.Now;
    var to = now.AddDays(7);
    // The tagged candidate lists use the same seven-day window over the canonical
    // projected programme set. Reuse ProgramGuideProjectionService's immutable
    // revision-bound full snapshot and apply the existing range predicate locally
    // instead of rebuilding the DB + External EPG merge for each tagged tab read.
    var events = programEvents.GetAll()
        .Where(e => e.End > now && e.Start < to)
        .ToList();

    Func<ProjectedProgramEvent, bool> match = kind?.ToLowerInvariant() switch
    {
        "newprogram" => e =>
        {
            var title = e.Title ?? string.Empty;
            return title.Contains("[新]") || title.Contains("［新］") || title.Contains("【新】") || title.Contains("新番組");
        },
        "finalepisode" => e =>
        {
            var title = e.Title ?? string.Empty;
            return title.Contains("[終]") || title.Contains("［終］") || title.Contains("【終】") || title.Contains("最終回");
        },
        _ => _ => false
    };

    var channels = BuildCurrentProgramGuideChannels(channelLoader);
    var chOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < channels.Count; i++)
    {
        var ch = channels[i];
        var key = ProgramGuideChannelServiceKey(ch);
        chOrder.TryAdd(key, i);
    }

    var filtered = events
        .Where(match)
        .GroupBy(e => new { e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.SourceKind, e.SourcePluginId, e.SourceEventKey })
        .Select(g => g.OrderBy(x => x.Start).First())
        .OrderBy(e => e.Start)
        .ThenBy(e =>
        {
            var key = $"{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}";
            return chOrder.TryGetValue(key, out var idx) ? idx : int.MaxValue;
        })
        .Select(e => new
        {
            e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId,
            e.ServiceName,
            Title = e.Title,
            Description = e.ShortText,
            e.Genre,
            GenreCodes = e.GenreCodes,
            e.DurationSeconds, e.Start, e.End,
            projectedEventId = e.Key.Value,
            projectionState = e.ProjectionState,
            sourceKind = e.SourceKind,
            sourcePluginId = e.SourcePluginId,
            sourceEventKey = e.SourceEventKey,
            dbEventExists = e.DbEventExists,
            titleProjectionSafe = false, titleProjectionReason = "raw_unsealed_or_projected"
        })
        .ToList();

    return Results.Ok(new { count = filtered.Count, events = filtered, rule = "release_contract" });
});

// Developer Diagnostics public-release boundary:
// 一般公開版では診断APIをルーティングせず、開発者ログ/診断スナップショットを外部公開しない。
#if TVAIR_DEVELOPER_DIAGNOSTICS
// ログ
app.MapGet("/api/debug/tuner-allocation", (ReservationStore store) =>
{
    var json = store.ReadTunerAllocationDebugJson();
    return json is null
        ? Results.NotFound(new { message = "tuner_allocation_debug.json がまだ作成されていません。" })
        : Results.Text(json, "application/json; charset=utf-8");
});

app.MapPost("/api/debug/tuner-allocation/rebuild",
    (ReservationStore store, ReservationAllocationRouteService allocationRoute) =>
{
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "Debug",
        Action: "TunerAllocationRebuild",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: false,
        RefreshWakeTask: false));

    var json = store.ReadTunerAllocationDebugJson();
    return json is null
        ? Results.NotFound(new { message = "tuner_allocation_debug.json の生成に失敗しました。" })
        : Results.Text(json, "application/json; charset=utf-8");
});


if (enableRouteReplayDebugApi)
{
    app.MapPost("/api/debug/tuner-allocation/rebuild-manual-route",
        (ReservationStore store, ReservationAllocationRouteService allocationRoute) =>
    {
        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "ManualReservation",
            Action: "DebugReplay:ManualRoute",
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: true,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: false,
            RefreshWakeTask: false));

        var json = store.ReadTunerAllocationDebugJson();
        return json is null
            ? Results.NotFound(new { message = "manual route replay failed." })
            : Results.Text(json, "application/json; charset=utf-8");
    });

    app.MapPost("/api/debug/tuner-allocation/rebuild-keyword-route",
        (ReservationStore store, ReservationAllocationRouteService allocationRoute) =>
    {
        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "KeywordMatcher",
            Action: "DebugReplay:KeywordRoute",
            RunKeywordMatcher: true,
            SyncProgramRuleReservations: true,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: false,
            RefreshWakeTask: false,
            WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));

        var json = store.ReadTunerAllocationDebugJson();
        return json is null
            ? Results.NotFound(new { message = "keyword route replay failed." })
            : Results.Text(json, "application/json; charset=utf-8");
    });
}



// developer_log_delta_read_contract:
// /api/log は開発者専用の差分取得口。通常アクセスでは、その時点までのログ本体を
// 同一lock内でスナップショット化して消費し、固定ビルドヘッダだけは毎回先頭に付与する。
// ?count= 指定時だけ既存の非破壊Recent取得を残し、診断補助用途との互換性を維持する。
app.MapGet("/api/log", (int? count, LogRepository log) =>
{
    var entries = count.HasValue
        ? log.GetRecent(count.Value)
        : log.ConsumeAll();
    return Results.Ok(entries);
});
#endif

app.MapGet("/api/user-events", (int? count, string? severity, string? category, UserEventLogService userEvents) =>
{
    var max = Math.Clamp(count ?? 100, 1, 1000);
    var rows = userEvents.GetRecent(max, severity, category)
        .Select(e => new UserEventLogDisplayEntry
        {
            Id = e.Id,
            Severity = e.Severity,
            Category = e.Category,
            Result = e.Result,
            Target = e.Target,
            Message = UserLogProjectionService.BuildStandardMessage(e),
            CreatedAt = e.CreatedAt
        })
        .ToArray();
    return Results.Ok(rows);
});

app.MapGet("/api/user-events/report", (int? hours, int? count, UserEventLogService userEvents) =>
{
    var h = Math.Clamp(hours ?? 24, 1, 24 * 7);
    var max = Math.Clamp(count ?? 200, 1, 500);
    var text = userEvents.BuildReportText(TimeSpan.FromHours(h), max, GetTvAIrAppVersion());
    return Results.Text(text, "text/plain; charset=utf-8");
});

app.MapDelete("/api/user-events", (UserEventLogService userEvents) =>
{
    var removed = userEvents.Clear();
    return Results.Ok(new { message = $"ユーザー向けログを{removed}件削除しました。", removed });
});

// release_contract: DROP品質調査再開用の読み取り専用ログ窓口。
// 既存ログを絞り込むだけで、録画・EPG・割当・停止処理には介入しない。


static bool IsPluginPresentationTextMode(string? value, string expected)
    => string.Equals((value ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase);

static bool IsPluginPresentationMultilineText(string? value)
    => (value ?? string.Empty).Contains('\n');

static bool IsPluginPresentationTargetMultiline(TvAirLogPresentationEntryDto entry)
{
    if (IsPluginPresentationTextMode(entry.TargetTextMode, "multiline"))
        return true;
    if (IsPluginPresentationTextMode(entry.TargetTextMode, "singleline"))
        return false;

    var parts = 0;
    if (!string.IsNullOrWhiteSpace(entry.ProgramTitle)) parts++;
    if (!string.IsNullOrWhiteSpace(entry.ServiceName)) parts++;
    if (!string.IsNullOrWhiteSpace(entry.ReservationId)) parts++;
    return parts > 1;
}

// release_contract: generic log presentation read surface.
// Host settings and plugin policies share the same projection contract; highest priority wins deterministically.
app.MapGet("/api/log-presentation/{viewKey}", (string viewKey, LogPresentationStore store, UserEventLogService userEvents, IniSettingsService ini, LogRepository log) =>
{
    var activePolicy = store.GetActiveLogPolicy(viewKey);
    var activeSnapshot = store.GetActiveLogSnapshot(viewKey);
    var hostPolicy = UserLogProjectionService.CreateHostPolicy(viewKey, ini);

    var policyPriority = activePolicy?.Policy.Priority ?? int.MinValue;
    var snapshotPriority = activeSnapshot?.Snapshot.Priority ?? int.MinValue;
    var hostPriority = hostPolicy?.Priority ?? int.MinValue;

    TvAirLogPresentationSnapshotDto? snapshot;
    string sourcePluginId;
    if (activePolicy is not null && policyPriority >= snapshotPriority && policyPriority >= hostPriority)
    {
        snapshot = UserLogProjectionService.BuildSnapshot(activePolicy.Policy, userEvents.GetRecent(100));
        sourcePluginId = activePolicy.SourcePluginId;
    }
    else if (activeSnapshot is not null && snapshotPriority >= hostPriority)
    {
        snapshot = activeSnapshot.Snapshot;
        sourcePluginId = activeSnapshot.SourcePluginId;
    }
    else if (hostPolicy is not null)
    {
        snapshot = UserLogProjectionService.BuildSnapshot(hostPolicy, userEvents.GetRecent(100));
        sourcePluginId = "tvair.settings";
    }
    else
    {
        snapshot = null;
        sourcePluginId = string.Empty;
    }
    var entries = snapshot?.Entries ?? Array.Empty<TvAirLogPresentationEntryDto>();
    var entryCount = entries.Count;
    var multilineMessageCount = entries.Count(e => IsPluginPresentationMultilineText(e.Message));
    var multilineTargetCount = entries.Count(e => IsPluginPresentationTargetMultiline(e));
    var projectedDetailRowCount = entries.Count(e => (e.Message ?? string.Empty).Contains('\n'));
    var projectedDetailItemCount = entries.Sum(e => Math.Max(0, (e.Message ?? string.Empty).Split('\n').Length - 1));
    var firstMessageHasNewline = entries.FirstOrDefault()?.Message?.Contains('\n') == true;
    var firstTargetIsMultiline = entries.FirstOrDefault() is { } firstEntry && IsPluginPresentationTargetMultiline(firstEntry);
    log.Add("LOG_PRESENTATION_READ", viewKey,
        $"active={snapshot is not null} sourcePluginId={SafePluginActionValue(sourcePluginId)} entries={entryCount} projectedDetailRows={projectedDetailRowCount} projectedDetailItems={projectedDetailItemCount} multilineMessages={multilineMessageCount} multilineTargets={multilineTargetCount} firstMessageHasNewline={firstMessageHasNewline} firstTargetIsMultiline={firstTargetIsMultiline} replaceHostDefault={snapshot?.ReplaceHostDefault.ToString() ?? "-"} rule=log_presentation_readback_contract");
    return Results.Ok(new
    {
        active = snapshot is not null,
        viewKey,
        sourcePluginId,
        entryCount,
        multilineMessageCount,
        multilineTargetCount,
        projectedDetailRowCount,
        projectedDetailItemCount,
        firstMessageHasNewline,
        firstTargetIsMultiline,
        snapshot,
        rule = "log_presentation_capability"
    });
});

app.MapGet("/api/log-presentation", (LogPresentationStore store) =>
{
    var snapshots = store.ListLogSnapshots();
    var policies = store.ListLogPolicies();
    return Results.Ok(new
    {
        count = snapshots.Count + policies.Count,
        snapshots = snapshots.Select(x => new { x.SourcePluginId, x.Snapshot.ViewKey, x.Snapshot.Title, x.Snapshot.Summary, x.Snapshot.Priority, x.Snapshot.UpdatedAt, entryCount = x.Snapshot.Entries.Count }),
        policies = policies.Select(x => new { x.SourcePluginId, x.Policy.ViewKey, x.Policy.Title, x.Policy.Enabled, x.Policy.DetailKeys, x.Policy.Layout, x.Policy.HideEmptyDetails, x.Policy.Priority, x.Policy.UpdatedAt }),
        rule = "user_operation_log_projection_policy"
    });
});

app.MapGet("/api/recording-quality/presentation", (LogPresentationStore store) =>
{
    var active = store.GetActiveRecordingQualitySnapshot();
    return Results.Ok(new
    {
        active = active is not null,
        sourcePluginId = active?.SourcePluginId ?? string.Empty,
        snapshot = active?.Snapshot,
        rule = "recording_quality_presentation_capability"
    });
});

#if TVAIR_DEVELOPER_DIAGNOSTICS
app.MapGet("/api/recording-quality/logs", (int? count, LogRepository log) =>
{
    var max = Math.Clamp(count ?? 200, 1, 1000);
    var targetEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "REC_QUALITY_RESULT",
        "REC_QUALITY_CORRELATION",
        "REC_DROP_TIMELINE_SUMMARY",
        "DIRECT_RECORDER_RUNTIME_STATS",
        "DIRECT_RECORDER_FINAL_STATUS",
        "REC_TS_VERIFY"
    };

    var entries = log.GetAll()
        .Where(e => targetEvents.Contains(e.Event))
        .TakeLast(max)
        .ToArray();

    return Results.Ok(new
    {
        count = entries.Length,
        limit = max,
        purpose = "drop_quality_audit_readonly",
        rule = "release_contract",
        events = targetEvents.OrderBy(x => x).ToArray(),
        entries
    });
});
#endif



// チャンネル一覧
app.MapGet("/api/channels", (ChannelFileLoader loader) =>
{
    var result = loader.Load();
    return Results.Ok(new
    {
        result.Message,
        result.Files,
        result.Warnings,
        count    = result.Targets.Count,
        channels = result.Targets.Select(t => new
        {
            t.Group,
            t.ServiceId,
            t.OriginalNetworkId,
            t.TransportStreamId,
            t.Name,
            t.ChannelArgument
        })
    });
});



static string NormalizeManualEpgRunSource(string? value, bool silent)
{
    var fallback = silent ? "WebApi.SilentEpg" : "WebApi.EpgRun";
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    var raw = value.Trim();
    if (raw.Length > 96) raw = raw[..96];
    var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_').ToArray();
    var safe = new string(chars).Trim('.', '_', '-');
    return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
}

static bool ApplyReservationTitleQualityGuard(Reservation r, EpgEvent? requestEvent, string source, LogRepository log, out string errorMessage)
{
    errorMessage = string.Empty;
    var beforeTitle = r.Title;
    if (string.IsNullOrWhiteSpace(r.Title) && requestEvent is not null)
    {
        var projectedTitle = EpgProjection.Title(requestEvent);
        if (!string.IsNullOrWhiteSpace(projectedTitle))
            r.Title = projectedTitle.Trim();
    }
    log.Add("RESERVATION_TITLE_SOURCE", source,
        $"result=PASSTHROUGH service=[{TitleGuardLogValue(r.ServiceName)}] title=[{TitleGuardLogValue(r.Title)}] beforeTitle=[{TitleGuardLogValue(beforeTitle)}] dbTitle=[{TitleGuardLogValue(requestEvent?.Title)}] projectedTitle=[{TitleGuardLogValue(requestEvent is null ? null : EpgProjection.Title(requestEvent))}] displayTitle=[{ReservationUserTitleLogValue(r.Title)}] displaySource=reservation_display_metadata_contract nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} rule=release_contract");
    return true;
}

static string TitleGuardLogValue(string? value)
{
    var v = (value ?? string.Empty).Replace("\r", " " ).Replace("\n", " " ).Trim();
    if (v.Length <= 120) return v;
    return v[..120] + "…";
}

static string ReservationUserTitleLogValue(string? rawTitle)
    => ReservationTitleDisplayContract.ForLog(rawTitle);

static string ReadProjectedEventIdFromRequest(HttpRequest request)
{
    if (request.Query.TryGetValue("projectedEventId", out var queryValue) && !string.IsNullOrWhiteSpace(queryValue.ToString()))
        return queryValue.ToString().Trim();
    if (request.Query.TryGetValue("projectedId", out var shortQueryValue) && !string.IsNullOrWhiteSpace(shortQueryValue.ToString()))
        return shortQueryValue.ToString().Trim();
    if (request.Headers.TryGetValue("X-TvAIr-Projected-Event-Id", out var headerValue) && !string.IsNullOrWhiteSpace(headerValue.ToString()))
        return headerValue.ToString().Trim();

    if (request.HasFormContentType)
    {
        var form = request.Form;
        if (form.TryGetValue("projectedEventId", out var formValue) && !string.IsNullOrWhiteSpace(formValue.ToString()))
            return formValue.ToString().Trim();
        if (form.TryGetValue("projectedId", out var shortFormValue) && !string.IsNullOrWhiteSpace(shortFormValue.ToString()))
            return shortFormValue.ToString().Trim();
    }

    return string.Empty;
}

static bool ReadForceProjectedFallbackFromRequest(HttpRequest request)
{
    static bool IsEnabledValue(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }

    if (request.Query.TryGetValue("forceProjectedFallback", out var queryValue) && IsEnabledValue(queryValue.ToString()))
        return true;
    if (request.Query.TryGetValue("forceProjectedReservationFallback", out var longQueryValue) && IsEnabledValue(longQueryValue.ToString()))
        return true;
    if (request.Headers.TryGetValue("X-TvAIr-Force-Projected-Fallback", out var headerValue) && IsEnabledValue(headerValue.ToString()))
        return true;

    if (request.HasFormContentType)
    {
        var form = request.Form;
        if (form.TryGetValue("forceProjectedFallback", out var formValue) && IsEnabledValue(formValue.ToString()))
            return true;
        if (form.TryGetValue("forceProjectedReservationFallback", out var longFormValue) && IsEnabledValue(longFormValue.ToString()))
            return true;
    }

    return false;
}

static ProjectedProgramEvent? ResolveProjectedReservationEvent(string projectedEventId, Reservation reservation, IProgramEventSource programEvents, LogRepository log, string sourceText, bool forceProjectedFallback)
{
    var trimmed = (projectedEventId ?? string.Empty).Trim();
    if (forceProjectedFallback)
    {
        log.Add("PROJECTED_RESERVATION_REQUEST", sourceText,
            $"result=DIAGNOSTIC mode=force_projected_fallback projectedEventId={SafeProjectedEventLogValue(trimmed)} nid={reservation.NetworkId} tsid={reservation.TransportStreamId} sid={reservation.ServiceId} eid={reservation.EventId} rule=projected_reservation_contract");
        return ResolveProjectedReservationEventByRequestIdentity(reservation, programEvents, log, sourceText, "forced_fallback_diagnostic", trimmed);
    }

    if (!string.IsNullOrWhiteSpace(trimmed))
    {
        var byProjectedId = programEvents.GetAll()
            .FirstOrDefault(e => string.Equals(e.Key.Value, trimmed, StringComparison.Ordinal));
        if (byProjectedId is null
            && ProjectedEventKey.TryParse(trimmed, out var parsedProjectedKey))
        {
            byProjectedId = programEvents.GetByProjectedKey(parsedProjectedKey);
        }
        if (byProjectedId is not null)
        {
            log.Add("PROJECTED_RESERVATION_REQUEST", sourceText,
                $"result=RESOLVED mode=projectedEventId projectionState={byProjectedId.ProjectionState} sourceKind={byProjectedId.SourceKind} sourcePluginId={SafeProjectedEventLogValue(byProjectedId.SourcePluginId)} sourceEventKey={SafeProjectedEventLogValue(byProjectedId.SourceEventKey)} projectedEventId={SafeProjectedEventLogValue(trimmed)} dbEventExists={byProjectedId.DbEventExists} rule=projected_reservation_contract");
            return byProjectedId;
        }

        var fallback = ResolveProjectedReservationEventByRequestIdentity(reservation, programEvents, log, sourceText, "stale_projected_event_id", trimmed);
        if (fallback is not null) return fallback;
        return null;
    }

    return ResolveProjectedReservationEventByRequestIdentity(reservation, programEvents, log, sourceText, "request_identity", string.Empty);
}

static ProjectedProgramEvent? ResolveProjectedReservationEventByRequestIdentity(Reservation reservation, IProgramEventSource programEvents, LogRepository log, string sourceText, string reason, string projectedEventId)
{
    if (reservation.NetworkId == 0 || reservation.TransportStreamId == 0 || reservation.ServiceId == 0)
        return null;

    if (reservation.EventId != 0)
    {
        var byEventKey = programEvents.GetByEventKey(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventId);
        if (byEventKey is not null)
        {
            log.Add("PROJECTED_RESERVATION_REQUEST", sourceText,
                $"result=RESOLVED mode=fallback_event_key reason={reason} projectionState={byEventKey.ProjectionState} sourceKind={byEventKey.SourceKind} sourcePluginId={SafeProjectedEventLogValue(byEventKey.SourcePluginId)} sourceEventKey={SafeProjectedEventLogValue(byEventKey.SourceEventKey)} projectedEventId={SafeProjectedEventLogValue(projectedEventId)} resolvedProjectedEventId={SafeProjectedEventLogValue(byEventKey.Key.Value)} dbEventExists={byEventKey.DbEventExists} nid={reservation.NetworkId} tsid={reservation.TransportStreamId} sid={reservation.ServiceId} eid={reservation.EventId} rule=projected_reservation_contract");
            return byEventKey;
        }
    }

    var byTime = ResolveProjectedReservationEventByTimeIdentity(reservation, programEvents, log, sourceText, reason, projectedEventId);
    return byTime;
}

static ProjectedProgramEvent? ResolveProjectedReservationEventByTimeIdentity(Reservation reservation, IProgramEventSource programEvents, LogRepository log, string sourceText, string reason, string projectedEventId)
{
    if (reservation.StartTime == default || reservation.EndTime == default)
        return null;

    var requestStart = reservation.StartTime;
    var requestEnd = reservation.EndTime;
    var localStart = requestStart.ToLocalTime();
    var localEnd = requestEnd.ToLocalTime();
    var from = MinDateTime(requestStart, localStart).AddMinutes(-3);
    var to = MaxDateTime(requestEnd, localEnd).AddMinutes(3);

    if (reservation.EndTime <= reservation.StartTime || to <= from)
        return null;

    var sameService = programEvents.GetByRange(from, to)
        .Where(e => e.NetworkId == reservation.NetworkId
                 && e.TransportStreamId == reservation.TransportStreamId
                 && e.ServiceId == reservation.ServiceId)
        .ToList();

    if (sameService.Count == 0)
        return null;

    var eventIdCandidates = reservation.EventId == 0
        ? new List<ProjectedProgramEvent>()
        : sameService.Where(e => e.EventId == reservation.EventId).ToList();
    var selected = SelectSingleProjectedCandidate(eventIdCandidates, out var eventIdAmbiguous);
    if (selected is not null)
    {
        LogProjectedReservationFallbackResolved(log, sourceText, "fallback_range_event_id", reason, projectedEventId, selected, reservation, eventIdCandidates.Count);
        return selected;
    }
    if (eventIdAmbiguous)
    {
        LogProjectedReservationFallbackRejected(log, sourceText, "ambiguous_fallback_event_id_candidates", reason, projectedEventId, reservation, eventIdCandidates.Count);
        return null;
    }

    var exactTimeCandidates = sameService
        .Where(e => IsSameDateTime(e.Start, requestStart) && IsSameDateTime(e.End, requestEnd)
                 || IsSameDateTime(e.Start, localStart) && IsSameDateTime(e.End, localEnd))
        .ToList();
    selected = SelectSingleProjectedCandidate(exactTimeCandidates, out var exactTimeAmbiguous);
    if (selected is not null)
    {
        LogProjectedReservationFallbackResolved(log, sourceText, "fallback_exact_time", reason, projectedEventId, selected, reservation, exactTimeCandidates.Count);
        return selected;
    }
    if (exactTimeAmbiguous)
    {
        LogProjectedReservationFallbackRejected(log, sourceText, "ambiguous_fallback_exact_time_candidates", reason, projectedEventId, reservation, exactTimeCandidates.Count);
        return null;
    }

    var normalizedTitle = NormalizeProjectedReservationCandidateTitle(reservation.Title);
    if (string.IsNullOrWhiteSpace(normalizedTitle))
        return null;

    var closeTitleCandidates = sameService
        .Where(e => IsCloseDateTime(e.Start, requestStart, 60) && IsCloseDateTime(e.End, requestEnd, 60)
                 || IsCloseDateTime(e.Start, localStart, 60) && IsCloseDateTime(e.End, localEnd, 60))
        .Where(e => string.Equals(NormalizeProjectedReservationCandidateTitle(e.Title), normalizedTitle, StringComparison.Ordinal))
        .ToList();
    selected = SelectSingleProjectedCandidate(closeTitleCandidates, out var closeTitleAmbiguous);
    if (selected is not null)
    {
        LogProjectedReservationFallbackResolved(log, sourceText, "fallback_close_time_title", reason, projectedEventId, selected, reservation, closeTitleCandidates.Count);
        return selected;
    }
    if (closeTitleAmbiguous)
    {
        LogProjectedReservationFallbackRejected(log, sourceText, "ambiguous_fallback_close_time_title_candidates", reason, projectedEventId, reservation, closeTitleCandidates.Count);
        return null;
    }

    return null;
}

static ProjectedProgramEvent? SelectSingleProjectedCandidate(IReadOnlyList<ProjectedProgramEvent> candidates, out bool ambiguous)
{
    ambiguous = false;
    if (candidates.Count == 0) return null;

    var dbCandidates = candidates.Where(e => e.DbEventExists || e.DbEvent is not null).ToList();
    if (dbCandidates.Count == 1) return dbCandidates[0];
    if (dbCandidates.Count > 1)
    {
        ambiguous = true;
        return null;
    }

    if (candidates.Count == 1) return candidates[0];
    ambiguous = true;
    return null;
}

static void LogProjectedReservationFallbackResolved(LogRepository log, string sourceText, string mode, string reason, string projectedEventId, ProjectedProgramEvent resolved, Reservation reservation, int candidateCount)
{
    log.Add("PROJECTED_RESERVATION_REQUEST", sourceText,
        $"result=RESOLVED mode={mode} reason={reason} candidateCount={candidateCount} projectionState={resolved.ProjectionState} sourceKind={resolved.SourceKind} sourcePluginId={SafeProjectedEventLogValue(resolved.SourcePluginId)} sourceEventKey={SafeProjectedEventLogValue(resolved.SourceEventKey)} projectedEventId={SafeProjectedEventLogValue(projectedEventId)} resolvedProjectedEventId={SafeProjectedEventLogValue(resolved.Key.Value)} dbEventExists={resolved.DbEventExists} nid={reservation.NetworkId} tsid={reservation.TransportStreamId} sid={reservation.ServiceId} eid={reservation.EventId} resolvedEid={resolved.EventId} start={reservation.StartTime:MM/dd HH:mm:ss} end={reservation.EndTime:MM/dd HH:mm:ss} resolvedStart={resolved.Start:MM/dd HH:mm:ss} resolvedEnd={resolved.End:MM/dd HH:mm:ss} rule=projected_reservation_contract");
}

static void LogProjectedReservationFallbackRejected(LogRepository log, string sourceText, string rejectReason, string reason, string projectedEventId, Reservation reservation, int candidateCount)
{
    log.Add("PROJECTED_RESERVATION_REQUEST", sourceText,
        $"result=REJECTED reason={rejectReason} fallbackReason={reason} candidateCount={candidateCount} projectedEventId={SafeProjectedEventLogValue(projectedEventId)} nid={reservation.NetworkId} tsid={reservation.TransportStreamId} sid={reservation.ServiceId} eid={reservation.EventId} start={reservation.StartTime:MM/dd HH:mm:ss} end={reservation.EndTime:MM/dd HH:mm:ss} rule=projected_reservation_contract");
}

static string NormalizeProjectedReservationCandidateTitle(string? title)
{
    var value = (title ?? string.Empty).Trim();
    if (value.Length == 0) return string.Empty;
    return string.Join(" ", value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
}

static bool IsSameDateTime(DateTime a, DateTime b)
    => Math.Abs((a - b).TotalSeconds) < 1;

static bool IsCloseDateTime(DateTime a, DateTime b, int toleranceSeconds)
    => Math.Abs((a - b).TotalSeconds) <= toleranceSeconds;

static DateTime MinDateTime(DateTime a, DateTime b) => a <= b ? a : b;
static DateTime MaxDateTime(DateTime a, DateTime b) => a >= b ? a : b;

static void ApplyProjectedEventToReservation(Reservation reservation, ProjectedProgramEvent projectedEvent)
{
    reservation.NetworkId = projectedEvent.NetworkId;
    reservation.TransportStreamId = projectedEvent.TransportStreamId;
    reservation.ServiceId = projectedEvent.ServiceId;
    reservation.EventId = projectedEvent.EventId;
    reservation.StartTime = projectedEvent.Start;
    reservation.EndTime = projectedEvent.End;
    if (!string.IsNullOrWhiteSpace(projectedEvent.Title))
        reservation.Title = projectedEvent.Title.Trim();
    if (string.IsNullOrWhiteSpace(reservation.ServiceName) && !string.IsNullOrWhiteSpace(projectedEvent.ServiceName))
        reservation.ServiceName = projectedEvent.ServiceName.Trim();
}

static string SafeProjectedEventLogValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    var v = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Replace("|", "/").Replace("\"", "'").Trim();
    return v.Length <= 180 ? v : v[..180] + "…";
}

// ─── 予約 API ───────────────────────────────────────────────────

// 予約一覧
app.MapGet("/api/reservations", (ReservationPresentationService presenter, LogRepository log) =>
{
    try
    {
        return Results.Ok(presenter.GetReservations());
    }
    catch (Exception ex)
    {
        log.Add("ReservationAPI", "Get", $"/api/reservations 失敗: {ex}");
        return Results.Json(Array.Empty<ReservationPresentationItem>());
    }
});

// release_contract ReservationListManualRefreshContract:
// Manual refresh reads GET /api/reservations only.  The former POST refresh route
// re-ran ProgramRule sync, allocation, PreRecEpg, and Wake reconstruction and was
// therefore an invalid recovery path for stale UI state.

// 予約追加（番組表からの直接予約）
app.MapPost("/api/reservations", (HttpRequest request, Reservation r, ReservationStore store, ReservationPresentationService presentation, IProgramEventSource programEvents, ReservationProjectionMetadataStore projectionMetadataStore, ChannelFileLoader channelLoader, ReservationAllocationRouteService allocationRoute, PluginTypedEventHub typedEvents, LogRepository log) =>
{
    try
    {
        // release_contract: 番組表の「予約」と「今すぐ録画」は同じAPI入口を通るが、
        // ファイル名タイムスタンプ基準が異なるため source をここで確定する。
        // フロントから Immediate が明示された場合だけ今すぐ録画、それ以外は番組表予約。
        r.Source = r.Source == ReservationSource.Immediate
            ? ReservationSource.Immediate
            : ReservationSource.Manual;

        var sourceText = r.Source == ReservationSource.Immediate ? "Immediate" : "Manual";
        var projectedEventId = ReadProjectedEventIdFromRequest(request);
        var forceProjectedFallback = ReadForceProjectedFallbackFromRequest(request);
        var projectedEvent = ResolveProjectedReservationEvent(projectedEventId, r, programEvents, log, sourceText, forceProjectedFallback);
        if ((forceProjectedFallback || !string.IsNullOrWhiteSpace(projectedEventId)) && projectedEvent is null)
        {
            log.Add("PROJECTED_RESERVATION_REQUEST", "Manual",
                $"result=REJECTED reason=projected_event_not_found fallbackForced={forceProjectedFallback} projectedEventId={SafeProjectedEventLogValue(projectedEventId)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} rule=projected_reservation_contract");
            return Results.NotFound(new { message = "投影番組が見つかりません。" });
        }

        if (projectedEvent is not null)
        {
            ApplyProjectedEventToReservation(r, projectedEvent);
        }

        var requestEvent = projectedEvent?.ToEpgEvent() ?? (r.EventId == 0 ? null : programEvents.GetByEventKey(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId)?.ToEpgEvent());
        var routeSource = r.Source == ReservationSource.Immediate ? "ImmediateReservation" : "ManualReservation";

        // ChannelArgument はフロントや既存DBの値を信用せず、現在の .ch2 から常に再解決する。
        // BS/CS の /chspace /ch は TVTest の現在チャンネル一覧と一致している必要がある。
        var ch = channelLoader.Load().Targets
            .FirstOrDefault(t =>
                t.OriginalNetworkId   == r.NetworkId &&
                t.TransportStreamId   == r.TransportStreamId &&
                t.ServiceId           == r.ServiceId);
        if (ch is not null)
        {
            r.ChannelArgument = ch.ChannelArgument;
            if (string.IsNullOrWhiteSpace(r.ServiceName))
                r.ServiceName = ch.Name;
        }

        // フロントからUTC('Z'サフィックス)で来るstartTime/endTimeをローカル時刻に統一
        r.StartTime = r.StartTime.ToLocalTime();
        r.EndTime   = r.EndTime.ToLocalTime();

        // release_contract:
        // Immediate は「番組表の当該イベントを今から録る」操作であり、予約本体の時間軸は実開始側を正本にする。
        // EPG 番組開始時刻は requestEvent/EPG DB の event metadata として残し、StartTime / occupancy / segmentStart へは流し込まない。
        // 初期予約作成時点で現在時刻より前の開始時刻を生成しない。
        if (r.Source == ReservationSource.Immediate)
        {
            var immediateNow = DateTime.Now;
            var requestedStart = r.StartTime;
            var eventStart = requestEvent?.Start;
            if (r.StartTime < immediateNow && r.EndTime > immediateNow)
            {
                r.StartTime = immediateNow;
                log.Add("IMMEDIATE_START_TIME_GUARD", sourceText,
                    $"result=APPLIED source=Immediate oldStart={requestedStart:MM/dd HH:mm:ss} effectiveStart={r.StartTime:MM/dd HH:mm:ss} eventStart={(eventStart.HasValue ? eventStart.Value.ToString("MM/dd HH:mm:ss") : "-")} end={r.EndTime:MM/dd HH:mm:ss} policy=reservation_start_uses_runtime_actual_start eventMetadataPreserved=True occupancyRewindPrevented=True segmentStartRewindPrevented=True rule=release_contract");
            }
            else
            {
                log.Add("IMMEDIATE_START_TIME_GUARD", sourceText,
                    $"result=NOT_APPLIED source=Immediate reason=no_past_start_to_guard oldStart={requestedStart:MM/dd HH:mm:ss} effectiveStart={r.StartTime:MM/dd HH:mm:ss} eventStart={(eventStart.HasValue ? eventStart.Value.ToString("MM/dd HH:mm:ss") : "-")} end={r.EndTime:MM/dd HH:mm:ss} rule=release_contract");
            }
        }

        if (!ApplyReservationTitleQualityGuard(r, requestEvent, sourceText, log, out var rawTitleError))
            return Results.BadRequest(new { message = rawTitleError });

        // Fast no-side-effect reuse check. The authoritative duplicate check is repeated atomically
        // with INSERT below, so this does not become a check-then-insert race.
        var existingBeforeSideEffects = store.FindActiveParentDuplicate(r);
        if (existingBeforeSideEffects is not null)
        {
            log.Add("RESERVATION_DEDUPE", sourceText,
                $"result=REUSE_EXISTING existing=R{existingBeforeSideEffects.Id} requestedSource={sourceText} existingStatus={existingBeforeSideEffects.Status} existingDataVersion={existingBeforeSideEffects.DataVersion} stage=before_epg_side_effects rule=release_contract");
            return Results.Ok(new { id = existingBeforeSideEffects.Id, message = "既に予約済みです。", isConflicted = existingBeforeSideEffects.IsConflicted, reused = true, reservation = presentation.GetReservation(existingBeforeSideEffects.Id) });
        }

        // Duplicate detection and INSERT are committed atomically below.
        // This includes Immediate requests so concurrent clicks/retries converge on one reservation id.
        log.Add("RESERVE_ENTRY", sourceText, $"共通入口要求 source={sourceText} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(r.Title)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm} end={r.EndTime:MM/dd HH:mm} rule=release_contract");
        using var eventScope = typedEvents.BeginOutboxScope(out var commitEvents);
        var addResult = store.AddOrGetActiveParent(r);
        var id = addResult.ReservationId;
        if (!addResult.Added && !addResult.Reactivated)
        {
            log.Add("RESERVATION_DEDUPE", sourceText,
                $"result=REUSE_EXISTING existing=R{id} requestedSource={sourceText} existingStatus={addResult.Reservation.Status} existingDataVersion={addResult.Reservation.DataVersion} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(r.Title)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} commonRoute=ATOMIC_ADD rule=release_contract");
            return Results.Ok(new { id, message = "既に予約済みです。", isConflicted = addResult.Reservation.IsConflicted, reused = true, reservation = presentation.GetReservation(id) });
        }

        if (addResult.Reactivated)
        {
            log.Add("RESERVATION_DEDUPE", sourceText,
                $"result=RETRY_EXISTING existing=R{id} requestedSource={sourceText} status={addResult.Reservation.Status} dataVersion={addResult.Reservation.DataVersion} action=continue_common_allocation_route newReservationId=False rule=release_contract");
        }

        if (projectedEvent is not null)
        {
            projectionMetadataStore.UpsertFromProjectedEvent(id, projectedEvent);
            log.Add("PROJECTED_RESERVATION_METADATA", sourceText,
                $"result=SAVED reservation=R{id} projectionState={projectedEvent.ProjectionState} sourceKind={projectedEvent.SourceKind} sourcePluginId={SafeProjectedEventLogValue(projectedEvent.SourcePluginId)} sourceEventKey={SafeProjectedEventLogValue(projectedEvent.SourceEventKey)} projectedEventId={SafeProjectedEventLogValue(projectedEvent.Key.Value)} dbEventExists={projectedEvent.DbEventExists} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} rule=projected_reservation_contract");
        }
        log.Add("Reservation", addResult.Reactivated ? "Retry" : "Add",
            $"{(addResult.Reactivated ? "予約再試行" : "予約追加")}: service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] id=R{id} source={sourceText} {r.StartTime:HH:mm}〜{r.EndTime:HH:mm} newReservationId={!addResult.Reactivated} rule=release_contract");

        var isImmediate = r.Source == ReservationSource.Immediate;

        // INTERACTIVE_RESERVATION_ALLOCATION_ORDER_INVARIANT — 変更禁止:
        // 番組表の通常予約/今すぐ録画は、予約追加後に共通割当single-flightの確定結果を待ってからAPI応答する。
        // pending batchへの合流だけで応答すると、割当未確定予約がUI/Due監視へ露出し、
        // 物理Tuner、イベント単位優先順位、競合結果、チェーン固定Tunerが未確定のまま次状態へ進み得るため禁止する。
        // 一方、当該ユーザーMutationと無関係なKeywordMatcher全件照合とProgramRule再生成は同期クリティカルパスへ混載しない。
        // Tuner確定、競合判定、PreRecEpg/Wake更新、Starting CAS、worker開始の正規順序は共通割当ルートで維持する。
        var allocationResult = allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: routeSource,
            Action: "Add",
            // UI直結の手動予約/今すぐ録画では、予約Mutationと無関係な全件Keyword照合・ProgramRule再生成を
            // 同期クリティカルパスへ混載しない。Tuner再評価/競合/PreRec/Wakeは共通割当ルートで必ず維持する。
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: false,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: true,
            RefreshWakeTask: true,
            EmitConflictLogs: true,
            ConflictLogCategory: "Reservation",
            ConflictLogTitle: "Conflict",
            WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce),
            waitForActiveSingleFlight: true);

        var added = store.GetById(id);
        commitEvents();
        log.Add("PLUGIN_TYPED_EVENT_OUTBOX", sourceText, $"result=COMMITTED operation=ReservationAdd reservation=R{id} rule=typed_event_outbox");

        // EPG実行中はそのwave占有を固定し、後着の予約／今すぐ録画は共通競合判定へ委ねる。
        // 録画要求からEPGを自動停止・縮小・再配置しない。EPG停止はVisible/Silent各UIの明示キャンセルだけが所有する。
        return Results.Ok(new
        {
            id,
            message = isImmediate
                ? (allocationResult.Deferred ? "録画準備を受け付けました。" : "録画準備を開始しました。")
                : (addResult.Reactivated ? "録画を再試行しました。" : "予約しました。"),
            isConflicted = added?.IsConflicted ?? false,
            reused = addResult.Reactivated,
            reactivated = addResult.Reactivated,
            preparing = isImmediate && (allocationResult.Deferred || added?.Status == ReservationStatus.Scheduled),
            allocationDeferred = allocationResult.Deferred,
            allocationReason = allocationResult.Reason,
            reservation = presentation.GetReservation(id)
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});


// ユーザー明示チェーン予約（番組表の緑「チェーン」ボタンからのみ使用）
app.MapPost("/api/reservations/chain", (HttpRequest request, Reservation r, ReservationStore store, IProgramEventSource programEvents, ReservationProjectionMetadataStore projectionMetadataStore, ChannelFileLoader channelLoader, ReservationAllocationRouteService allocationRoute, IniSettingsService ini, PluginTypedEventHub typedEvents, LogRepository log, UserEventLogService userEvents) =>
{
    using var eventScope = typedEvents.BeginOutboxScope(out var commitEvents);
    try
    {
        // release_contract: チェーン予約は「後番組優先ON＋チェーン録画ON」を利用条件にしたうえで、
        // ユーザーが番組表のチェーンボタンで明示指定した場合だけ成立する予約契約。
        // 自動救済ではなく、共通ChainReservationEligibilityContractをAPI入口とStore Transactionで再検証する。
        log.Add("RESERVE_ENTRY", "UserChainPolicy",
            $"later={ini.LaterProgramPriority} chain={ini.PseudoContinuousRecording} explicitButton=True title=[{ReservationUserTitleLogValue(r.Title)}] service=[{r.ServiceName}] rule=release_contract");

        var projectedEventId = ReadProjectedEventIdFromRequest(request);
        var forceProjectedFallback = ReadForceProjectedFallbackFromRequest(request);
        var projectedEvent = ResolveProjectedReservationEvent(projectedEventId, r, programEvents, log, "UserChain", forceProjectedFallback);
        if ((forceProjectedFallback || !string.IsNullOrWhiteSpace(projectedEventId)) && projectedEvent is null)
        {
            log.Add("PROJECTED_RESERVATION_REQUEST", "UserChain",
                $"result=REJECTED reason=projected_event_not_found fallbackForced={forceProjectedFallback} projectedEventId={SafeProjectedEventLogValue(projectedEventId)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} rule=projected_reservation_contract");
            return Results.NotFound(new { message = "投影番組が見つかりません。" });
        }
        if (projectedEvent is not null)
        {
            ApplyProjectedEventToReservation(r, projectedEvent);
        }

        var requestEvent = projectedEvent?.ToEpgEvent() ?? (r.EventId == 0 ? null : programEvents.GetByEventKey(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId)?.ToEpgEvent());

        if (!r.UserChainPreviousId.HasValue)
            return Results.BadRequest(new { message = "チェーン元予約が指定されていません。" });

        var predecessor = store.GetById(r.UserChainPreviousId.Value);
        if (predecessor is null)
            return Results.BadRequest(new { message = "チェーン元予約が見つかりません。" });
        var predecessorTuner = !string.IsNullOrWhiteSpace(predecessor.ActualTunerName)
            ? predecessor.ActualTunerName
            : predecessor.TunerName;

        // CHAIN_CREATION_ALLOCATION_INVARIANT:
        // チェーンボタンは親子トポロジーを原子的に確定し、その直後に共通割当ルートへ渡す。
        // 初の子だけ、親予約のINSERT後から共通割当完了までTunerNameが一時的に空になり得る。
        // ここで未確定Tunerを拒否すると、同じ操作を二度押しした時だけ成功する競合窓になる。
        // 作成時の物理Tuner正本はFinalConflictPlanであり、API入口で空きTunerを推測・待機・再試行しない。

        r.StartTime = r.StartTime.ToLocalTime();
        r.EndTime   = r.EndTime.ToLocalTime();

        if (!ApplyReservationTitleQualityGuard(r, requestEvent, "UserChain", log, out var rawTitleError))
            return Results.BadRequest(new { message = rawTitleError });

        var chainGapSeconds = (int)Math.Round((r.StartTime - predecessor.EndTime).TotalSeconds);
        var chainEligibility = ChainReservationEligibilityContract.EvaluatePair(
            predecessor,
            r.NetworkId,
            r.TransportStreamId,
            r.ServiceId,
            r.StartTime,
            ini.LaterProgramPriority && ini.PseudoContinuousRecording);
        if (!chainEligibility.IsEligible)
        {
            log.Add("RESERVE_ENTRY", "UserChainRejected",
                $"reason={chainEligibility.ReasonToken} predecessor=R{predecessor.Id} prevNid={predecessor.NetworkId} prevTsid={predecessor.TransportStreamId} prevSid={predecessor.ServiceId} nextNid={r.NetworkId} nextTsid={r.TransportStreamId} nextSid={r.ServiceId} gapSec={chainGapSeconds} prevEnd={predecessor.EndTime:MM/dd HH:mm:ss} nextStart={r.StartTime:MM/dd HH:mm:ss} rule=release_contract");
            return Results.Conflict(new
            {
                message = chainEligibility.Reason switch
                {
                    ChainReservationEligibilityContract.FailureReason.FeatureDisabled => "チェーン予約は後番組優先＋チェーン予約オプションが有効な場合のみ使用できます。",
                    ChainReservationEligibilityContract.FailureReason.PredecessorNotActive => "チェーン元予約が有効な予約状態ではありません。",
                    ChainReservationEligibilityContract.FailureReason.PredecessorDisabled => "チェーン元予約が無効化されています。",
                    ChainReservationEligibilityContract.FailureReason.PredecessorConflicted => "チェーン元予約が競合しています。",
                    ChainReservationEligibilityContract.FailureReason.NotSameService => "チェーン予約は同一局（同一NID/TSID/SID）の連続番組のみ使用できます。",
                    ChainReservationEligibilityContract.FailureReason.NotAdjacent => "チェーン予約は同一時刻を跨ぐ連続番組のみ使用できます。",
                    _ => "チェーン予約を作成できません。"
                }
            });
        }

        var chainSameNetwork = predecessor.NetworkId == r.NetworkId;
        var chainSameTransport = predecessor.TransportStreamId == r.TransportStreamId;
        var chainSameService = predecessor.ServiceId == r.ServiceId;
        var chainSameChannel = ChainReservationEligibilityContract.IsSameService(predecessor, r.NetworkId, r.TransportStreamId, r.ServiceId);
        var chainAdjacent = ChainReservationContract.IsAdjacent(predecessor.EndTime, r.StartTime);

        var existingReservation = store.GetActiveByEvent(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);

        r.Source = ReservationSource.Manual;
        r.IsUserChain = true;
        // 親に確定済みTunerがあれば初期投影として引き継ぐが、空でもチェーン作成を許可する。
        // 最終的な親子共通Tunerは直後の共通割当ルートだけが確定する。
        r.TunerName = predecessorTuner;
        r.UserChainRootId = predecessor.UserChainRootId ?? predecessor.Id;

        var ch = channelLoader.Load().Targets
            .FirstOrDefault(t =>
                t.OriginalNetworkId   == r.NetworkId &&
                t.TransportStreamId   == r.TransportStreamId &&
                t.ServiceId           == r.ServiceId);
        if (ch is not null)
        {
            r.ChannelArgument = ch.ChannelArgument;
            if (string.IsNullOrWhiteSpace(r.ServiceName))
                r.ServiceName = ch.Name;
        }

        var chainExecutionMode = "ChainDirectRecorder";

        log.Add("RESERVE_ENTRY", "UserChain", $"共通入口要求 source=UserChain service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] predecessor=R{predecessor.Id} root=R{r.UserChainRootId} inheritTuner=[{(string.IsNullOrWhiteSpace(predecessorTuner) ? "pending-final-plan" : predecessorTuner)}] nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm} end={r.EndTime:MM/dd HH:mm} executionMode={chainExecutionMode} commonRoute=ALLOC_ROUTE rule=release_contract");
        log.Add("CHAIN_COMMON_ENTRY", $"R{predecessor.Id}->pending",
            $"button=Chain route=ALLOC_ROUTE executionMode={chainExecutionMode} normalRecordingRouteTouched=False prevService=[{predecessor.ServiceName}] prevTitle=[{ReservationUserTitleLogValue(predecessor.Title)}] nextService=[{r.ServiceName}] nextTitle=[{ReservationUserTitleLogValue(r.Title)}] prevTuner={(string.IsNullOrWhiteSpace(predecessorTuner) ? "pending-final-plan" : predecessorTuner)} prevActualTuner={(string.IsNullOrWhiteSpace(predecessor.ActualTunerName) ? "-" : predecessor.ActualTunerName)} root=R{r.UserChainRootId} rule=release_contract");
        log.Add("CHAIN_PAIR_EVAL", $"R{predecessor.Id}->pending",
            $"result=READY_FOR_COMMON_ALLOC_ROUTE sameNetwork={chainSameNetwork} sameTransport={chainSameTransport} sameService={chainSameService} sameChannel={chainSameChannel} adjacent={chainAdjacent} gapSec={chainGapSeconds} userChain=True executionMode={chainExecutionMode} contract=same_sid_adjacent_explicit_button executionOwner=ReservationScheduler.StopRestartHandoff prevStart={predecessor.StartTime:MM/dd HH:mm:ss} prevEnd={predecessor.EndTime:MM/dd HH:mm:ss} nextStart={r.StartTime:MM/dd HH:mm:ss} nextEnd={r.EndTime:MM/dd HH:mm:ss} rule=release_contract");
        log.Add("CHAIN_CONTRACT_WARNING", $"R{predecessor.Id}->pending",
            $"accepted=True frontSegmentMayBeCut=True successorCompletenessPriority=True sameSidOnly=True userExplicitButton=True message=チェーン予約では前番組の後半がカットされる可能性があります rule=release_contract");

        var chainMutation = store.AddOrPromoteUserChain(r, predecessor.Id, r.UserChainRootId.Value, ini.LaterProgramPriority && ini.PseudoContinuousRecording);
        if (!chainMutation.Applied)
        {
            log.Add("CHAIN_MUTATION", $"R{predecessor.Id}->pending",
                $"result=REJECTED reason={chainMutation.Reason} existing=R{(chainMutation.ReservationId == 0 ? "-" : chainMutation.ReservationId.ToString())} rule=release_contract");
            return Results.Conflict(new
            {
                message = chainMutation.Reason switch
                {
                    "predecessor_already_has_successor" => "チェーン元予約には既に別の後続予約があります。",
                    "successor_already_has_predecessor" => "対象予約は既に別のチェーンに属しています。",
                    "chain_cycle_detected" => "循環するチェーン予約は作成できません。",
                    "chain_depth_exceeded" => "チェーン予約の長さが上限を超えています。",
                    "chain_broken_predecessor" => "既存チェーンの参照が壊れているため追加できません。",
                    "chain_root_mismatch" => "既存チェーンのルートが一致しません。",
                    "compare_and_set_failed" => "予約が同時に更新されたため、再読み込みしてください。",
                    _ => "チェーン予約の構造検証に失敗しました。"
                },
                reason = chainMutation.Reason,
                reservationId = chainMutation.ReservationId
            });
        }

        var id = chainMutation.ReservationId;
        existingReservation = chainMutation.Added ? null : existingReservation ?? chainMutation.Reservation;
        if (projectedEvent is not null)
        {
            projectionMetadataStore.UpsertFromProjectedEvent(id, projectedEvent);
            log.Add("PROJECTED_RESERVATION_METADATA", "UserChain",
                $"result=SAVED reservation=R{id} projectionState={projectedEvent.ProjectionState} sourceKind={projectedEvent.SourceKind} sourcePluginId={SafeProjectedEventLogValue(projectedEvent.SourcePluginId)} sourceEventKey={SafeProjectedEventLogValue(projectedEvent.SourceEventKey)} projectedEventId={SafeProjectedEventLogValue(projectedEvent.Key.Value)} dbEventExists={projectedEvent.DbEventExists} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} rule=projected_reservation_contract");
        }
        if (chainMutation.Added)
        {
            log.Add("Reservation", "ChainAdd", $"チェーン予約追加: service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] id=R{id} predecessor=R{predecessor.Id} tuner=[{(string.IsNullOrWhiteSpace(predecessorTuner) ? "pending-final-plan" : predecessorTuner)}] executionMode={chainExecutionMode} {r.StartTime:HH:mm}〜{r.EndTime:HH:mm} rule=release_contract");
            log.Add("CHAIN_EXECUTION_MODE", $"R{id}", $"mode={chainExecutionMode} stage=new_chain_reservation_added commonRoute=ALLOC_ROUTE normalExecutorFrozen=True handoffMode=stop_restart handoffImplemented=True predecessor=R{predecessor.Id} rule=release_contract");
        }
        else
        {
            var promoted = chainMutation.Reservation ?? existingReservation;
            log.Add("Reservation", "ChainConvert", $"既存予約をチェーン予約へ昇格: service=[{promoted?.ServiceName}] title=[{ReservationUserTitleLogValue(promoted?.Title ?? string.Empty)}] id=R{id} predecessor=R{predecessor.Id} tuner=[{(string.IsNullOrWhiteSpace(predecessorTuner) ? "pending-final-plan" : predecessorTuner)}] executionMode={chainExecutionMode} wasConflicted={promoted?.IsConflicted} {(promoted?.StartTime.ToString("HH:mm") ?? "-")}〜{(promoted?.EndTime.ToString("HH:mm") ?? "-")} rule=release_contract");
            log.Add("CHAIN_EXECUTION_MODE", $"R{id}", $"mode={chainExecutionMode} stage=existing_reservation_converted commonRoute=ALLOC_ROUTE normalExecutorFrozen=True handoffMode=stop_restart handoffImplemented=True predecessor=R{predecessor.Id} rule=release_contract");
        }

        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "UserChainReservation",
            Action: "Add",
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: false,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: true,
            RefreshWakeTask: true,
            EmitConflictLogs: true,
            ConflictLogCategory: "Reservation",
            ConflictLogTitle: "Conflict",
            ExecutionMode: chainExecutionMode,
            WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));

        var added = store.GetById(id);
        log.Add("CHAIN_COMMON_ENTRY", $"R{id}", $"result=ROUTED_TO_ALLOC_ROUTE executionMode={chainExecutionMode} isConflicted={(added?.IsConflicted.ToString() ?? "-")} assignedTuner={(string.IsNullOrWhiteSpace(added?.TunerName) ? "-" : added!.TunerName)} actualTuner={(string.IsNullOrWhiteSpace(added?.ActualTunerName) ? "-" : added!.ActualTunerName)} predecessor=R{predecessor.Id} normalRecordingRouteTouched=False rule=release_contract");
        if (added is not null)
            userEvents.AddChainReservationAdded(predecessor, added, id, !chainMutation.Added);
        commitEvents();
        log.Add("PLUGIN_TYPED_EVENT_OUTBOX", "UserChain", $"result=COMMITTED operation=ChainReservation reservation=R{id} rule=typed_event_outbox");
        return Results.Ok(new { id, message = chainMutation.Added ? "チェーン予約しました。" : "既存予約をチェーン予約に変更しました。", isConflicted = added?.IsConflicted ?? false, tunerName = added?.TunerName ?? "" });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});
// 予約キャンセル/物理削除
// scheduled はキャンセル状態へ移行し、completed/failed/cancelled は物理削除する。
// ユーザー明示チェーンは GetUserChainCancelTargets で対象範囲を決定し、
// 単独／チェーン範囲とも同じ原子的取消・確定ID応答・共通割り当て出口へ通す。
// 番組表／予約一覧など呼出画面を処理所有者にせず、解除後は必ず共通割り当てルートで再評価する。
app.MapDelete("/api/reservations/{id}", (int id, ReservationStore store, ReservationProjectionMetadataStore projectionMetadataStore, ReservationAllocationRouteService allocationRoute, PluginTypedEventHub typedEvents, LogRepository log, UserEventLogService userEvents) =>
{
    using var eventScope = typedEvents.BeginOutboxScope(out var commitEvents);
    var r = store.GetById(id);
    if (r is null) return Results.NotFound();

    // release_contract: 番組表セルが内部用の録画前EPG確認(SystemEpg)を拾ってしまっても、
    // 取消対象は親の実録画予約へ向ける。SystemEpgは番組表上の通常予約として扱わない。
    if (r.SourceRuleId.HasValue && ReservationIntentContract.IsPreRecordEpg(r))
    {
        var parent = store.GetById(r.SourceRuleId.Value);
        if (parent is not null && parent.Status != ReservationStatus.Completed && parent.Status != ReservationStatus.Failed && parent.Status != ReservationStatus.Cancelled)
        {
            log.Add("PROGRAM_GRID_CANCEL_TARGET", $"R{id}",
                $"result=REDIRECT_CHILD_TO_PARENT child=R{id} parent=R{parent.Id} childTitle={ReservationUserTitleLogValue(r.Title)} parentService={parent.ServiceName} parentTitle={ReservationUserTitleLogValue(parent.Title)} rule=release_contract");
            id = parent.Id;
            r = parent;
        }
    }

    if (r.Status == ReservationStatus.Recording)
        return Results.BadRequest(new { message = "録画中の予約はキャンセルできません。" });

    // 完了・失敗・キャンセル済みはDBから物理削除（ログタブからの削除）
    if (r.Status == ReservationStatus.Completed ||
        r.Status == ReservationStatus.Failed ||
        r.Status == ReservationStatus.Cancelled)
    {
        // PHYSICAL_DELETE_ORDER_INVARIANT:
        // 予約本体・チェーントポロジーの原子的CAS削除を先にcommitし、成功後だけ投影メタデータを削除する。
        // CAS拒否時も予約本体と投影メタデータの整合を同じ順序で維持する。
        if (!store.TryDeleteTerminalReservationAtomicCas(r, out _))
        {
            log.Add("RESERVATION_PHYSICAL_DELETE_CAS", $"R{id}",
                $"result=REJECTED status={r.Status} dataVersion={r.DataVersion} action=reload_and_retry_manually rule=release_contract");
            return Results.Conflict(new { message = "予約状態またはチェーン構造が更新されたため、最新状態を確認してからもう一度削除してください。" });
        }

        projectionMetadataStore.Delete(id);
        commitEvents();
        log.Add("PLUGIN_TYPED_EVENT_OUTBOX", "ReservationDelete", $"result=COMMITTED operation=PhysicalDelete reservation=R{id} rule=typed_event_outbox");
        return Results.Ok(new { message = "削除しました。" });
    }

    // scheduled → cancelled に変更（予約解除）
    // ユーザー明示チェーンは「末尾=単体 / 途中=そこ以降 / 先頭=全体」をまとめて取り消す。
    var chainTargets = store.GetUserChainCancelTargets(id);
    if (chainTargets.Count == 0)
        chainTargets = new[] { r };

    var recordingTarget = chainTargets.FirstOrDefault(x => x.Status == ReservationStatus.Recording);
    if (recordingTarget is not null)
        return Results.BadRequest(new { message = $"チェーン内に録画中の予約があります。R{recordingTarget.Id} はキャンセルできません。" });

    var targetIds = chainTargets.Select(x => x.Id).ToList();
    var policyTargetText = string.Join(",", chainTargets.Select(x => $"R{x.Id}:{x.ServiceName}:{ReservationUserTitleLogValue(x.Title)}:rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(x.Title)}"));
    var policyHead = chainTargets.FirstOrDefault(x => x.Id == id) ?? chainTargets.FirstOrDefault();
    log.Add("CHAIN_CANCEL_POLICY", $"R{id}",
        $"operation=ReservationCancel service={TitleGuardLogValue(policyHead?.ServiceName)} title={ReservationUserTitleLogValue(policyHead?.Title)} rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(policyHead?.Title)} result={(targetIds.Count > 1 ? "CANCEL_CHAIN_RANGE" : "CANCEL_SINGLE")} reason=explicit_reservation_cancel cancelSuccessors={(targetIds.Count > 1)} stopOperation=False targetCount={targetIds.Count} targets=[{policyTargetText}] rule=release_contract");
    foreach (var target in chainTargets.Where(x => x.Source == ReservationSource.Keyword && x.Status == ReservationStatus.Scheduled))
        store.AddKeywordCancelOnce(target);
    var isChainCancel = targetIds.Count > 1 || chainTargets.Any(x => x.IsUserChain || x.UserChainPreviousId.HasValue || x.UserChainRootId.HasValue);
    if (!store.TryCancelReservationsAtomicCas(
            chainTargets.ToList(),
            new Dictionary<string, string?>
            {
                ["suppressUserEvent"] = isChainCancel ? "chain_cancel_specialized" : "reservation_cancel_specialized"
            },
            out var cancelledTargets))
    {
        log.Add("RESERVATION_CANCEL_BATCH_CAS", $"R{id}",
            $"result=REJECTED action=reload_and_retry_manually targets=[{string.Join(",", chainTargets.Select(x => $"R{x.Id}:v{x.DataVersion}"))}] rule=release_contract");
        return Results.Conflict(new { message = "予約状態が更新されたため、最新状態を確認してからもう一度キャンセルしてください。" });
    }
    chainTargets = cancelledTargets;
    targetIds = chainTargets.Select(x => x.Id).ToList();
    if (isChainCancel)
    {
        userEvents.AddChainReservationCancelled(chainTargets.ToList(), id);
    }
    else
    {
        userEvents.AddReservationDeleted(r, id);
    }

    var preRecChildCancelled = store.CancelScheduledPreRecordEpgChildrenForParents(targetIds);
    if (preRecChildCancelled > 0)
    {
        log.Add("PRE_REC_EPG_CHILD_CANCEL", $"R{id}",
            $"result=CANCELLED children={preRecChildCancelled} parents=[{string.Join(",", targetIds.Select(x => $"R{x}"))}] reason=parent_reservation_cancelled rule=release_contract");
    }

    if (targetIds.Count > 1)
    {
        var rangeText = string.Join(",", targetIds.Select(x => $"R{x}"));
        log.Add("Reservation", "ChainCancel", $"チェーン予約キャンセル: start=R{id} count={targetIds.Count} targets=[{rangeText}] policy=reservation_cancel_cascades_chain rule=release_contract");
    }
    else
    {
        log.Add("Reservation", "Cancel", $"予約キャンセル: [{ReservationUserTitleLogValue(r.Title)}] {r.StartTime:HH:mm}〜{r.EndTime:HH:mm} policy=single_reservation_cancel rule=release_contract");
    }

    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "ReservationMutation",
        Action: targetIds.Count > 1 ? "ChainCancel" : "Cancel",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: false,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "Reservation",
        ConflictLogTitle: "Conflict"));

    commitEvents();
    log.Add("PLUGIN_TYPED_EVENT_OUTBOX", "ReservationCancel", $"result=COMMITTED operation={(targetIds.Count > 1 ? "ChainCancel" : "Cancel")} reservation=R{id} targetCount={targetIds.Count} rule=typed_event_outbox");
    return Results.Ok(new
    {
        message = targetIds.Count > 1 ? $"チェーン予約を{targetIds.Count}件キャンセルしました。" : "キャンセルしました。",
        changed = true,
        mutation = "cancel",
        affectedReservationIds = targetIds,
        cancelledIds = targetIds
    });
});


app.MapDelete("/api/reservation-logs", (ReservationStore store) =>
{
    var removed = store.DeleteLogEntries();
    return Results.Ok(new { message = $"ログを{removed}件削除しました。", removed });
});


// 次回Wakeタスク情報取得
app.MapGet("/api/wake-status", (TaskSchedulerService taskSvc) =>
{
    var info = taskSvc.GetNextWakeInfo();
    if (info is null)
        return Results.Ok(new { scheduled = false, wakeAt = (string?)null, title = (string?)null, startTime = (string?)null });
    return Results.Ok(new
    {
        scheduled  = true,
        wakeAt     = info.WakeAt.ToString("O"),
        title      = info.Title,
        startTime  = info.StartTime.ToString("O"),
        wakeMinutesBefore = info.WakeMinutesBefore
    });
});

// 録画停止（録画中の予約を停止し、録画ライフサイクル側で状態確定する）
// release_contract: 番組表/自動検索/プログラム/手動を同じ共通停止入口に通す。
app.MapPost("/api/reservations/{id}/stop", (int id, ReservationStore store, ReservationScheduler scheduler, LogRepository log) =>
{
    var r = store.GetById(id);
    if (r is null) return Results.NotFound(new { message = "予約が見つかりません。" });

    if (r.SourceRuleId.HasValue && ReservationIntentContract.IsPreRecordEpg(r))
    {
        var parent = store.GetById(r.SourceRuleId.Value);
        if (parent is not null && parent.Status == ReservationStatus.Recording)
        {
            log.Add("REC_STOP_ROUTE", $"R{id}",
                $"result=REDIRECT_CHILD_TO_PARENT child=R{id} parent=R{parent.Id} childTitle={ReservationUserTitleLogValue(r.Title)} parentService={parent.ServiceName} parentTitle={ReservationUserTitleLogValue(parent.Title)} rule=release_contract");
            id = parent.Id;
            r = parent;
        }
    }

    if (r.Status != ReservationStatus.Recording)
        return Results.BadRequest(new { message = "録画中ではありません。" });

    try
    {
        log.Add("REC_STOP_ROUTE", $"R{id}",
            $"result=REQUESTED source={r.Source} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] status={r.Status} tuner=[{r.TunerName}] actualTuner=[{r.ActualTunerName}] route=ReservationApiStop->ReservationScheduler.StopRecording commonRoute=recording_stop_all_sources rule=release_contract");
    }
    catch
    {
        // 受付前診断ログの失敗で停止要求を拒否しない。
    }

    var stopResult = scheduler.StopRecording(id);

    // StopSessionAsync側が停止完了後の再評価/Wake再構築を正本として実行する。
    // API入口では停止要求を即時受理し、二重の同期再評価でUI応答を待たせない。
    try
    {
        log.Add("REC_STOP_ROUTE", $"R{id}",
            $"result={(stopResult.Accepted ? "ACCEPTED_DEFER_REALLOCATION" : stopResult.Pending ? "ALREADY_PENDING" : "REJECTED")} source={r.Source} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] route=ReservationApiStop->StopSessionAsyncReevaluation apiSynchronousAllocation=False rule=release_contract");
    }
    catch
    {
        // 停止要求の確定結果をログ基盤障害で変更しない。
    }

    if (stopResult.Pending)
        return Results.Ok(new { message = stopResult.Message, stoppedId = id, source = r.Source.ToString(), accepted = false, pending = true });
    if (!stopResult.Accepted)
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: stopResult.Message);

    return Results.Ok(new { message = stopResult.Message, stoppedId = id, source = r.Source.ToString(), accepted = true, pending = false });
});

// 録画ON/OFF切り替え（ユーザーによる能動的な有効/無効化）
app.MapPatch("/api/reservations/{id}/enabled", (int id, EnabledRequest req, ReservationStore store, ReservationPresentationService presenter, ReservationAllocationRouteService allocationRoute, PluginTypedEventHub typedEvents, LogRepository log) =>
{
    using var eventScope = typedEvents.BeginOutboxScope(out var commitEvents);
    var r = store.GetById(id);
    if (r is null) return Results.NotFound(new { message = "予約が見つかりません。" });
    log.Add("RESERVE_ENTRY", "EnabledToggle", $"共通入口要求 action=EnabledToggle service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] id=R{id} source={r.Source} enabled={r.IsEnabled}->{req.IsEnabled} start={r.StartTime:MM/dd HH:mm}");
    if (r.IsEnabled != req.IsEnabled && !ReservationOperationPolicy.CanToggleEnabled(r))
    {
        log.Add("RESERVE_ENTRY", "EnabledToggle",
            $"共通入口結果 action=EnabledToggle id=R{id} result=REJECTED reason=status_not_toggleable status={r.Status} enabled={r.IsEnabled} requested={req.IsEnabled} rule=reservation_operation_policy_contract");
        return Results.Conflict(new
        {
            message = "この予約状態では録画ON/OFFを変更できません。最新状態を再取得してください。",
            changed = false,
            reason = "status_not_toggleable",
            enabled = r.IsEnabled,
            dataVersion = r.DataVersion,
            reservation = presenter.GetReservation(id)
        });
    }
    var enabledUpdate = store.UpdateEnabledIfChanged(id, req.IsEnabled);
    if (!enabledUpdate.Found)
        return Results.NotFound(new { message = "予約が見つかりません。" });
    if (!enabledUpdate.Changed)
    {
        if (enabledUpdate.Reason == "compare_and_set_failed")
            return Results.Conflict(new
            {
                message = "予約状態が同時に変更されました。最新状態を再取得してください。",
                changed = false,
                reason = enabledUpdate.Reason,
                enabled = enabledUpdate.CurrentEnabled,
                dataVersion = enabledUpdate.CurrentDataVersion,
                reservation = presenter.GetReservation(id)
            });

        log.Add("RESERVE_ENTRY", "EnabledToggle", $"共通入口結果 action=EnabledToggle id=R{id} result=NO_CHANGE enabled={enabledUpdate.CurrentEnabled} dataVersion={enabledUpdate.CurrentDataVersion} reason={enabledUpdate.Reason} allocationSkipped=True wakeSkipped=True eventsSkipped=True rule=release_contract");
        return Results.Ok(new
        {
            message = req.IsEnabled ? "すでに録画ONです。" : "すでに録画OFFです。",
            changed = false,
            reason = enabledUpdate.Reason,
            enabled = enabledUpdate.CurrentEnabled,
            dataVersion = enabledUpdate.CurrentDataVersion,
            reservation = presenter.GetReservation(id)
        });
    }

    if (!req.IsEnabled)
    {
        var deletedPreRec = store.DeleteScheduledPreRecordEpgEntriesForParent(id);
        log.Add("PRE_REC_EPG_PARENT_CLEANUP", $"R{id}",
            $"result={(deletedPreRec > 0 ? "DELETED" : "NONE")} parent=R{id} reason=parent_disabled deleted={deletedPreRec} trigger=enabled_toggle action=release_prerec_epg_before_reallocation rule=release_contract");
    }
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "ReservationList",
        Action: "EnabledToggle",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: false,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "ReservationAPI",
        ConflictLogTitle: "EnabledToggleConflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    var updated = store.GetById(id);
    commitEvents();
    log.Add("PLUGIN_TYPED_EVENT_OUTBOX", "EnabledToggle", $"result=COMMITTED operation=EnabledToggle reservation=R{id} rule=typed_event_outbox");
    if (updated is not null)
        log.Add("RESERVE_ENTRY", "EnabledToggle", $"共通入口結果 action=EnabledToggle service=[{updated.ServiceName}] title=[{ReservationUserTitleLogValue(updated.Title)}] id=R{id} source={updated.Source} enabled={updated.IsEnabled} tuner=[{updated.TunerName}] conflicted={updated.IsConflicted} changed=True dataVersion={updated.DataVersion}");
    return Results.Ok(new
    {
        message = req.IsEnabled ? "録画ONにしました。" : "録画OFFにしました。",
        changed = true,
        reason = enabledUpdate.Reason,
        enabled = req.IsEnabled,
        dataVersion = updated?.DataVersion ?? enabledUpdate.CurrentDataVersion,
        reservation = presenter.GetReservation(id)
    });
});

// ─── キーワードルール API ─────────────────────────────────────────

app.MapGet("/api/keyword-rules", (ReservationStore store, KeywordMatcher matcher) =>
{
    var rules = store.GetKeywordRules();
    var hitCounts = matcher.GetRuleHitCounts(rules.Where(r => r.Enabled));
    return Results.Ok(rules.Select(r => new
    {
        r.Id,
        r.Name,
        r.Pattern,
        r.ExcludePattern,
        r.UseRegex,
        r.SearchFields,
        r.SearchTitle,
        r.SearchOutline,
        r.SearchDetail,
        r.SearchCast,
        r.TargetGenres,
        r.TargetServices,
        r.TargetDays,
        r.UseAllChannels,
        r.UseTimeRange,
        r.StartTime,
        r.EndTime,
        r.Enabled,
        r.SortOrder,
        r.ExpiresOn,
        r.CreatedAt,
        r.UpdatedAt,
        hitCount = r.Enabled && hitCounts.TryGetValue(r.Id, out var count) ? count : 0
    }));
});

app.MapGet("/api/keyword-rules/export", (ReservationStore store) =>
{
    var payload = new KeywordRuleImportPayload
    {
        ExportedAt = DateTime.Now,
        Rules = store.GetKeywordRules().Select(r => new KeywordRule
        {
            Id = r.Id,
            Name = r.Name,
            Pattern = r.Pattern,
            ExcludePattern = r.ExcludePattern,
            UseRegex = r.UseRegex,
            SearchFields = r.SearchFields,
            SearchTitle = r.SearchTitle,
            SearchOutline = r.SearchOutline,
            SearchDetail = r.SearchDetail,
            SearchCast = r.SearchCast,
            UseAllChannels = r.UseAllChannels,
            TargetServices = r.TargetServices,
            TargetGenres = r.TargetGenres,
            TargetDays = r.TargetDays,
            UseTimeRange = r.UseTimeRange,
            StartTime = r.StartTime,
            EndTime = r.EndTime,
            ExpiresOn = r.ExpiresOn,
            SortOrder = r.SortOrder,
            Enabled = r.Enabled,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        }).ToList()
    };

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(json)).ToArray();
    var fileName = $"tvair-keyword-rules-{DateTime.Now:yyyyMMdd-HHmmss}.json";
    return Results.File(bytes, "application/json; charset=utf-8", fileName);
});

app.MapPost("/api/keyword-rules/import", async (HttpRequest request, ReservationStore store, KeywordMatcher matcher, ReservationAllocationRouteService allocationRoute, LogRepository log, ChannelFileLoader channelLoader) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "インポートファイルを指定してください。" });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "インポートファイルが空です。" });

    KeywordRuleImportPayload? payload;
    try
    {
        using var stream = file.OpenReadStream();
        payload = await JsonSerializer.DeserializeAsync<KeywordRuleImportPayload>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"JSONの読み込みに失敗しました。 {ex.Message}" });
    }

    if (payload is null)
        return Results.BadRequest(new { message = "インポートデータを読み取れませんでした。" });
    if (!string.Equals(payload.Format, "TvAIr.KeywordRules", StringComparison.Ordinal))
        return Results.BadRequest(new { message = "TvAIrの自動検索ルール形式ではありません。" });
    if (payload.Version != 1)
        return Results.BadRequest(new { message = $"未対応のバージョンです。 version={payload.Version}" });

    var ordered = (payload.Rules ?? new List<KeywordRule>())
        .OrderBy(r => r.SortOrder <= 0 ? int.MaxValue : r.SortOrder)
        .ThenBy(r => r.Id)
        .ToList();

    var usedIds = new HashSet<int>();
    var nextId = Math.Max(ordered.Count, ordered.Where(r => r.Id > 0).DefaultIfEmpty(new KeywordRule { Id = 0 }).Max(r => r.Id)) + 1;
    for (var i = 0; i < ordered.Count; i++)
    {
        var serviceIdentityError = NormalizeKeywordRule(ordered[i], channelLoader);
        if (serviceIdentityError is not null)
            return Results.BadRequest(new { message = $"{i + 1}件目: {serviceIdentityError}" });
        ordered[i].SortOrder = i + 1;
        if (ordered[i].Id <= 0) ordered[i].Id = nextId++;
        while (!usedIds.Add(ordered[i].Id)) ordered[i].Id = nextId++;
        var err = ValidateKeywordRule(ordered[i]);
        if (err is not null)
            return Results.BadRequest(new { message = $"{i + 1}件目: {err}" });
    }

    var previousRuleIds = store.GetKeywordRules().Select(x => x.Id).ToHashSet();
    var importedRuleIds = ordered.Select(x => x.Id).ToHashSet();

    store.ReplaceKeywordRules(ordered);

    var preserved = 0;
    var removed = 0;
    foreach (var rule in ordered)
    {
        var reconcile = matcher.ReconcileScheduledReservationsForRule(rule);
        preserved += reconcile.Preserved;
        removed += reconcile.Removed;
    }

    foreach (var removedRuleId in previousRuleIds.Except(importedRuleIds))
        removed += store.DeleteScheduledByRuleId(removedRuleId);

    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "KeywordRule",
        Action: "Import",
        RunKeywordMatcher: true,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "KeywordRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    log.Add("KEYWORD_RULE", "Import",
        $"自動検索予約ルールをインポート: {ordered.Count}件 / preservedScheduled={preserved} removedScheduled={removed} / file={file.FileName}");

    return Results.Ok(new
    {
        importedCount = ordered.Count,
        preservedReservations = preserved,
        removedReservations = removed,
        message = $"{ordered.Count}件のルールをインポートしました。"
    });
});

app.MapGet("/api/keyword-rule-reservations", (ReservationPresentationService presenter, LogRepository log) =>
{
    try
    {
        return Results.Ok(presenter.GetKeywordRuleReservations());
    }
    catch (Exception ex)
    {
        log.Add("ReservationAPI", "GetKeyword", $"/api/keyword-rule-reservations 失敗: {ex}");
        return Results.Json(Array.Empty<KeywordRuleReservationPresentationGroup>());
    }
});

app.MapPost("/api/keyword-rules", (KeywordRule r, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log, ChannelFileLoader channelLoader) =>
{
    var serviceIdentityError = NormalizeKeywordRule(r, channelLoader);
    if (serviceIdentityError is not null) return Results.BadRequest(new { message = serviceIdentityError });
    var err = ValidateKeywordRule(r);
    if (err is not null) return Results.BadRequest(new { message = err });
    r.CreatedAt = r.UpdatedAt = DateTime.Now;
    var id = store.AddKeywordRule(r);
    log.Add("KEYWORD_RULE", $"Rule{id}", $"ルール作成: enabled={r.Enabled} name=[{r.Name}] pattern=[{r.Pattern}] exclude=[{r.ExcludePattern}] allChannels={r.UseAllChannels}");
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "KeywordRule",
        Action: "Create",
        RunKeywordMatcher: true,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "KeywordRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    return Results.Ok(new { id, message = "自動検索予約ルールを登録しました。" });
});

app.MapPost("/api/keyword-rules/preview", (KeywordRule r, KeywordMatcher matcher, ChannelFileLoader channelLoader) =>
{
    var serviceIdentityError = NormalizeKeywordRule(r, channelLoader);
    if (serviceIdentityError is not null) return Results.BadRequest(new { message = serviceIdentityError });
    var err = ValidateKeywordRule(r);
    if (err is not null) return Results.BadRequest(new { message = err });
    var preview = matcher.PreviewRule(r);
    return Results.Ok(new
    {
        candidateCount = preview.CandidateCount,
        alreadyReservedCount = preview.AlreadyReservedCount,
        previewCount = preview.Items.Count,
        items = preview.Items.Select(x => new
        {
            x.NetworkId,
            x.TransportStreamId,
            x.ServiceId,
            x.EventId,
            x.ServiceName,
            Title = x.Title,
            Description = x.Description,
            x.Genre,
            x.GenreCodes,
            x.Start,
            x.End,
            x.AlreadyReserved
        })
    });
});

app.MapPut("/api/keyword-rules/{id}", (int id, KeywordRule r, ReservationStore store, KeywordMatcher matcher, ReservationAllocationRouteService allocationRoute, LogRepository log, ChannelFileLoader channelLoader) =>
{
    var serviceIdentityError = NormalizeKeywordRule(r, channelLoader);
    if (serviceIdentityError is not null) return Results.BadRequest(new { message = serviceIdentityError });
    var err = ValidateKeywordRule(r);
    if (err is not null) return Results.BadRequest(new { message = err });
    r.Id = id;

    store.UpdateKeywordRule(r);
    var reconcile = matcher.ReconcileScheduledReservationsForRule(r);
    log.Add("KEYWORD_RULE", $"Rule{id}",
        $"ルール更新: enabled={r.Enabled} name=[{r.Name}] pattern=[{r.Pattern}] preservedScheduled={reconcile.Preserved} preservedSourceMissing={reconcile.PreservedSourceMissing} removedScheduled={reconcile.Removed}");

    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "KeywordRule",
        Action: "Update",
        RunKeywordMatcher: r.Enabled,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "KeywordRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));

    var updatedRule = store.GetKeywordRules().FirstOrDefault(x => x.Id == id);
    var hitCount = updatedRule is not null && updatedRule.Enabled
        ? matcher.GetRuleHitCounts(new[] { updatedRule }).GetValueOrDefault(id)
        : 0;
    return Results.Ok(new
    {
        message = "更新しました。",
        preservedReservations = reconcile.Preserved,
        removedReservations = reconcile.Removed,
        rule = updatedRule is null ? null : new
        {
            updatedRule.Id, updatedRule.Name, updatedRule.Pattern, updatedRule.ExcludePattern,
            updatedRule.UseRegex, updatedRule.SearchFields, updatedRule.SearchTitle,
            updatedRule.SearchOutline, updatedRule.SearchDetail, updatedRule.SearchCast,
            updatedRule.TargetGenres, updatedRule.TargetServices, updatedRule.TargetDays,
            updatedRule.UseTimeRange, updatedRule.StartTime, updatedRule.EndTime,
            updatedRule.Enabled, updatedRule.UseAllChannels, updatedRule.ExpiresOn,
            updatedRule.SortOrder, HitCount = hitCount
        }
    });
});

app.MapPost("/api/keyword-rules/reorder", (KeywordRuleOrderRequest req, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    var current = store.GetKeywordRules().Select(x => x.Id).OrderBy(x => x).ToArray();
    var ordered = (req.OrderedIds ?? Array.Empty<int>()).Distinct().ToArray();
    if (ordered.Length != current.Length || !ordered.OrderBy(x => x).SequenceEqual(current))
        return Results.BadRequest(new { message = "並び順データが不正です。" });
    store.ReorderKeywordRules(ordered);
    log.Add("KEYWORD_RULE", "Reorder", $"並び順更新: [{string.Join(",", ordered)}]");
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "KeywordRule",
        Action: "Reorder",
        RunKeywordMatcher: true,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "KeywordRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    return Results.Ok(new { message = "並び順を更新しました。" });
});

app.MapDelete("/api/keyword-rules/{id}", (int id, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    // ルール削除時は、ルール本体削除だけで終わらせず、
    // 旧ルール由来予約の解放→再マッチング→共通割当再評価まで必ず通す。
    // さらに前後の件数をログに残し、画面側の誤認と切り分けやすくする。
    var beforeCount = store.GetKeywordRules().Count;
    var removed = store.DeleteScheduledByRuleId(id);
    store.DeleteKeywordRule(id);
    var afterDeleteCount = store.GetKeywordRules().Count;
    log.Add("KEYWORD_RULE", $"Rule{id}", $"ルール削除: before={beforeCount} after={afterDeleteCount} removedScheduled={removed}");

    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "KeywordRule",
        Action: "Delete",
        RunKeywordMatcher: true,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "KeywordRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));

    if (removed > 0)
        log.Add("KEYWORD_RULE", $"Rule{id}",
            $"ルール削除に伴い関連予約を解放: {removed}件");

    return Results.Ok(new { message = "削除しました。", beforeCount, afterDeleteCount, removedScheduled = removed });
});

string? ValidateKeywordRule(KeywordRule r)
{
    if (string.IsNullOrWhiteSpace(r.Pattern)) return "キーワードは必須です。";
    if (!r.SearchTitle && !r.SearchOutline && !r.SearchDetail && !r.SearchCast)
        return "検索対象フィールドを1つ以上選択してください。";
    if (r.UseTimeRange)
    {
        if (!TimeOnly.TryParse(r.StartTime, out _)) return "開始時間の形式が不正です。";
        if (!TimeOnly.TryParse(r.EndTime, out _)) return "終了時間の形式が不正です。";
    }
    var exprError = KeywordMatcher.ValidateExpression(r.Pattern, r.UseRegex);
    if (exprError is not null) return r.UseRegex ? $"キーワードの正規表現が不正です: {exprError}" : $"キーワード条件の書式が不正です: {exprError}";
    exprError = KeywordMatcher.ValidateExpression(r.ExcludePattern, r.UseRegex);
    if (exprError is not null) return r.UseRegex ? $"除外キーワードの正規表現が不正です: {exprError}" : $"除外キーワード条件の書式が不正です: {exprError}";
    return null;
}

string? NormalizeKeywordRule(KeywordRule r, ChannelFileLoader channelLoader)
{
    r.SearchFields = "title";
    r.Pattern = r.Pattern?.Trim() ?? "";
    r.ExcludePattern = r.ExcludePattern?.Trim() ?? "";

    if (r.UseAllChannels)
    {
        r.TargetServices = "";
    }
    else
    {
        IReadOnlyList<ChannelTarget> targets;
        try
        {
            targets = channelLoader.Load().Targets.ToList();
        }
        catch (Exception ex)
        {
            return $"対象局の現在情報を読み取れません: {ex.GetType().Name}";
        }

        var normalized = new List<string>();
        foreach (var token in (r.TargetServices ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ServiceIdentityContract.TryParseKey(token, out var exact))
            {
                normalized.Add(exact.ToString());
                continue;
            }

            // Legacy keyword-rule compatibility: SID-only rows may be migrated only when the
            // current channel metadata resolves that SID to exactly one service identity.
            // Ambiguous or malformed values must not be newly persisted as service identity.
            if (!ushort.TryParse(token, out var legacySid))
                return $"対象局の識別子が不正です: {token}";

            var matches = targets
                .Where(t => t.ServiceId == legacySid)
                .Select(ServiceIdentityContract.From)
                .Distinct()
                .Take(2)
                .ToList();
            if (matches.Count != 1)
                return matches.Count == 0
                    ? $"旧形式の対象局 SID={legacySid} を現在の局情報から一意に解決できません。対象局を選び直してください。"
                    : $"旧形式の対象局 SID={legacySid} は複数局に一致します。対象局を選び直してください。";

            normalized.Add(matches[0].ToString());
        }

        r.TargetServices = string.Join(",", normalized.Distinct(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(r.TargetServices))
            return "対象局を1局以上選択してください。";
    }

    r.TargetGenres = string.Join(",", (r.TargetGenres ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()));
    r.TargetDays = string.Join(",", (r.TargetDays ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    r.StartTime = string.IsNullOrWhiteSpace(r.StartTime) ? "00:00" : r.StartTime;
    r.EndTime = string.IsNullOrWhiteSpace(r.EndTime) ? "23:59" : r.EndTime;
    return null;
}

// ─── プログラム予約ルール API ─────────────────────────────────────

app.MapGet("/api/program-rules", (ReservationStore store) =>
{
    store.PurgeExpiredProgramGuideMissingProgramRules();
    return Results.Ok(store.GetProgramRules());
});

app.MapPost("/api/program-rules", (ProgramRule r, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log, UserEventLogService userEvents) =>
{
    r.CreatedAt = r.UpdatedAt = DateTime.Now;
    var id = store.AddProgramRule(r);
    log.Add("PROGRAM_RULE", $"Rule{id}", $"ルール作成: enabled={r.Enabled} name=[{r.Name}] dayOfWeek={r.DayOfWeek} start={r.StartTime} end={r.EndTime} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId}");
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "ProgramRule",
        Action: "Create",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "ProgramRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    return Results.Ok(new { id, message = "プログラム予約を登録しました。" });
});

app.MapPut("/api/program-rules/{id}", (int id, ProgramRule r, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log, UserEventLogService userEvents) =>
{
    var before = store.GetProgramRules().FirstOrDefault(x => x.Id == id);
    r.Id = id;
    store.UpdateProgramRule(r);
    log.Add("PROGRAM_RULE", $"Rule{id}", $"ルール更新: enabled={r.Enabled} name=[{r.Name}] dayOfWeek={r.DayOfWeek} start={r.StartTime} end={r.EndTime} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId}");
    if (before is not null && before.Enabled != r.Enabled)
        userEvents.AddProgramRuleEnabledChanged(r, id, r.Enabled);
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "ProgramRule",
        Action: "Update",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "ProgramRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    return Results.Ok(new
    {
        message = "更新しました。",
        rule = store.GetProgramRules().FirstOrDefault(x => x.Id == id)
    });
});

app.MapDelete("/api/program-rules/{id}", (int id, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    store.DeleteProgramRule(id);
    log.Add("PROGRAM_RULE", $"Rule{id}", "ルール削除");
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "ProgramRule",
        Action: "Delete",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "ProgramRule",
        ConflictLogTitle: "Conflict",
        WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    return Results.Ok(new { message = "削除しました。" });
});

static bool FixedTimePasswordEquals(string left, string right)
{
    var leftBytes = System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty);
    var rightBytes = System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty);
    var length = Math.Max(leftBytes.Length, rightBytes.Length);
    var leftPadded = new byte[length];
    var rightPadded = new byte[length];
    Buffer.BlockCopy(leftBytes, 0, leftPadded, 0, leftBytes.Length);
    Buffer.BlockCopy(rightBytes, 0, rightPadded, 0, rightBytes.Length);
    return leftBytes.Length == rightBytes.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftPadded, rightPadded);
}

// ─── 設定 API ────────────────────────────────────────────────────

// 設定取得。保存値の投影はIniSettingsService.ToDtoだけを正本とする。
// 初回Host値も構築時にRuntime/Persisted snapshotへ取り込まれているため、API側で再補完しない。
app.MapGet("/api/settings", (IniSettingsService ini) => Results.Ok(ini.ToWebDto()));

app.MapGet("/api/settings/password/{kind}", (string kind, IniSettingsService ini, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";

    // 保存済み資格情報の平文表示は、このWindowsユーザーが操作するローカルUIだけに限定する。
    // LANセッションには設定済み状態と文字数だけを投影し、平文は境界を越えて返さない。
    if (!NetworkAccessSecurity.IsLoopback(context.Connection.RemoteIpAddress))
        return Results.Json(new { message = "TvAIrを起動しているPCで確認してください。" }, statusCode: StatusCodes.Status403Forbidden);

    string? plain = kind.ToLowerInvariant() switch
    {
        "windows" => CredentialProtector.Decrypt(ini.TaskPasswordEncrypted),
        "network" => CredentialProtector.Decrypt(ini.NetworkPasswordEncrypted),
        _ => null
    };

    if (plain is null)
        return Results.NotFound(new { message = "表示できるパスワードがありません。" });
    return Results.Ok(new { password = plain });
});

app.MapGet("/api/settings-selection-contract", () => Results.Ok(new
{
    epgHours = SettingsDefaults.EpgHourOptions,
    epgMinutes = SettingsDefaults.EpgMinuteOptions,
    epgPreRecordMinutes = SettingsDefaults.EpgPreRecordMinuteOptions,
    epgDepthProfiles = SettingsDefaults.EpgDepthOptions.Select((value, index) => new
    {
        value,
        label = SettingsDefaults.EpgDepthDisplayLabels[index],
        seconds = EpgDurationPolicy.BaseSecondsForDepth(value)
    }),
    preStartMarginSeconds = SettingsDefaults.PreStartMarginSecondOptions,
    postEndMarginSeconds = SettingsDefaults.PostEndMarginSecondOptions,
    recordingAfterActionDelayMinutes = SettingsDefaults.RecordingAfterActionDelayMinuteOptions,
    bounds = new
    {
        portMin = SettingsDefaults.PortMin,
        portMax = SettingsDefaults.PortMax,
        networkSessionLifetimeMinutesMin = SettingsDefaults.NetworkSessionLifetimeMinutesMin,
        networkSessionLifetimeMinutesMax = SettingsDefaults.NetworkSessionLifetimeMinutesMax,
        networkPasswordMinLength = SettingsDefaults.NetworkPasswordMinLength
    },
    defaults = new
    {
        port = SettingsDefaults.Port,
        epgHour = SettingsDefaults.EpgHour,
        epgMinute = SettingsDefaults.EpgMinute,
        epgDepth = SettingsDefaults.EpgDepth,
        epgPreRecordMinutes = SettingsDefaults.EpgPreRecordMinutes,
        preStartMarginSeconds = SettingsDefaults.PreStartMarginSeconds,
        postEndMarginSeconds = SettingsDefaults.PostEndMarginSeconds,
        networkSessionLifetimeMinutes = SettingsDefaults.NetworkSessionLifetimeMinutes,
        recordingAfterActionDelayMinutes = SettingsDefaults.RecordingAfterActionDelayMinutes
    }
}));

app.MapGet("/api/settings-theme-state", (IniSettingsService ini, SettingsRuntimeState runtimeState) =>
{
    var theme = IniSettingsService.NormalizeSystemTheme(ini.SystemTheme);
    return Results.Ok(new
    {
        systemTheme = theme,
        selectedTheme = theme,
        revision = runtimeState.ThemeRevision,
        rule = "release_contract"
    });
});

// 設定保存。Web/WinFormsを問わずSettingsChangeApplicationServiceを単一出口とする。
app.MapPut("/api/settings", (WebSettingsUpdateDto dto, SettingsChangeApplicationService application) =>
{
    try
    {
        return Results.Ok(application.Apply(dto));
    }
    catch (SettingsValidationException ex)
    {
        return Results.BadRequest(new
        {
            message = ex.Message,
            field = ex.Field
        });
    }
});

// アプリ再起動（設定変更後の反映用）
// スタートアップタスクが登録されている場合のみ再起動後に自動復帰する
app.MapPost("/api/restart", (IniSettingsService ini) =>
{
    // レスポンスを返してから終了するために遅延実行
    Task.Run(async () =>
    {
        await Task.Delay(500);
        Environment.Exit(0);
    });
    return Results.Ok(new
    {
        message     = "再起動します。",
        hasStartup  = ini.StartupEnabled
    });
});

// ファイル選択ダイアログ（filter: exe / ch2 / chset / folder）
// BonDriver一覧取得（フォルダパスを直接指定）
// UIのBonDriverフォルダ入力変更時にリアルタイムでプルダウンを更新するために使用
app.MapGet("/api/settings/bondrivers", (string dir) =>
{
    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        return Results.Ok(new { files = Array.Empty<string>(), dlls = Array.Empty<string>() });

    var dlls = Directory.GetFiles(dir, "*.dll")
        .Select(Path.GetFileName)
        .Where(f => f != null)
        .OrderBy(f => f)
        .ToArray();
    return Results.Ok(new { files = dlls, dlls });
});

// ファイル選択ダイアログ（filter: exe / ch2 / chset / folder）
// 多重起動防止フラグ。HTTP要求は並行実行されるため、boolの確認・設定を分離しない。
var browseInProgress = 0;
app.MapGet("/api/settings/browse", (string filter) =>
{
    if (Interlocked.CompareExchange(ref browseInProgress, 1, 0) != 0)
        return Results.Ok(new { cancelled = true, path = (string?)null });

    string? selected = null;
    var thread = new Thread(() =>
    {
        System.Windows.Forms.NativeWindow? owner = null;
        try
        {
            // ダイアログを最前面に出すためのダミーオーナーウィンドウ
            owner = new System.Windows.Forms.NativeWindow();
            owner.CreateHandle(new System.Windows.Forms.CreateParams
            {
                ExStyle = 0x00000008 // WS_EX_TOPMOST
            });

            if (filter == "folder")
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description            = "フォルダを選択してください",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton    = true,
                };
                if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    selected = dlg.SelectedPath;
            }
            else
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog
                {
                    CheckFileExists = true,
                    Multiselect     = false,
                };
                (dlg.Title, dlg.Filter) = filter switch
                {
                    "exe" => ("TVTest.exe を選択", "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*"),
                    "ch2" => ("ch2 ファイルを選択", "チャンネルファイル (*.ch2)|*.ch2|すべてのファイル (*.*)|*.*"),
                    "chset" => ("ChSet.txt を選択", "ChSet.txt (*.txt)|*.txt|すべてのファイル (*.*)|*.*"),
                    _     => ("ファイルを選択", "すべてのファイル (*.*)|*.*"),
                };
                if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    selected = dlg.FileName;
            }
        }
        finally
        {
            try { owner?.DestroyHandle(); } catch { }
            Volatile.Write(ref browseInProgress, 0);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (selected is null)
        return Results.Ok(new { cancelled = true, path = (string?)null });
    return Results.Ok(new { cancelled = false, path = selected });
});

// アプリケーション起動完了時の初期化処理
TvAIr.Core.TrayIconService? trayIconService = null;
app.Lifetime.ApplicationStarted.Register(() =>
{
    // 起動時にスタートアップ登録（HKCU\...\Run）とWakeタスクの状態を同期する
    try
    {
        var startupSvc = app.Services.GetRequiredService<StartupRegistryService>();
        var iniSvc     = app.Services.GetRequiredService<IniSettingsService>();
        startupSvc.Set(iniSvc.StartupEnabled);
        // Wake同期はEpgScheduler StartupFinalizeが、起動時予約Mutation確定後に一度だけ所有する。
    }
    catch { /* スタートアップ・タスクスケジューラー同期失敗は無視 */ }

    // 7日以上経過したログエントリを削除
    try
    {
        var store = app.Services.GetRequiredService<ReservationStore>();
        var purged = store.PurgeOldLogEntries();
        if (purged > 0)
        {
            var log = app.Services.GetRequiredService<LogRepository>();
            log.Add("Startup", "Purge", $"期限切れログエントリを削除しました: {purged}件");
        }
    }
    catch { /* ログ削除失敗は無視 */ }

    // タスクトレイアイコン起動
    try
    {
        trayIconService = new TvAIr.Core.TrayIconService(
            port,
            app.Services.GetRequiredService<ReservationStore>(),
            app.Services.GetRequiredService<TunerPool>(),
            app.Services.GetRequiredService<EpgScheduler>(),
            app.Services.GetRequiredService<LogRepository>(),
            app.Services.GetRequiredService<PluginDefaultMenuActionService>(),
            app.Services.GetRequiredService<IniSettingsService>());
        trayIconService.Start();
    }
    catch { /* トレイアイコン起動失敗は無視 */ }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { trayIconService?.Dispose(); } catch { }
});

app.Lifetime.ApplicationStopped.Register(() =>
{
    try { singleInstanceMutex.Dispose(); } catch { }
});


// release_contract: Windows アプリテーマ取得。current 選択時のフロントテーマ決定に使う。
// AppsUseLightTheme: 0=dark, 1=light
app.MapGet("/api/system-theme", () =>
{
    try
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        var light = value is int i ? i != 0 : value?.ToString() != "0";
        return Results.Json(new
        {
            success = true,
            source = "HKCU\\\\Software\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Themes\\\\Personalize\\\\AppsUseLightTheme",
            theme = light ? "light" : "dark",
            appsUseLightTheme = light
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            success = false,
            source = "fallback",
            theme = "light",
            error = ex.GetType().Name
        });
    }
});





[System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
static void EmitTvAIrEpgRecRuntimePrerequisiteAudit(LogRepository log, TvTestSettings settings)
{
    try
    {
        var baseDir = AppContext.BaseDirectory;
        var tvTestExe = settings.ExecutablePath ?? string.Empty;
        var tvTestDir = string.IsNullOrWhiteSpace(tvTestExe) ? string.Empty : Path.GetDirectoryName(tvTestExe) ?? string.Empty;
        var tvTestIni = string.IsNullOrWhiteSpace(tvTestDir) ? string.Empty : Path.Combine(tvTestDir, "TVTest.ini");

        var workerPath = Path.Combine(baseDir, "TvAIrEpgRec.exe");
        var appLocalB25 = Path.Combine(baseDir, "B25Decoder.dll");
        var tvTestLocalB25 = string.IsNullOrWhiteSpace(tvTestDir) ? string.Empty : Path.Combine(tvTestDir, "B25Decoder.dll");
        var appLocalWinSCard = Path.Combine(baseDir, "winscard.dll");
        var tvTestWinSCard = string.IsNullOrWhiteSpace(tvTestDir) ? string.Empty : Path.Combine(tvTestDir, "winscard.dll");
        var tvTestWinSCardIni = string.IsNullOrWhiteSpace(tvTestDir) ? string.Empty : Path.Combine(tvTestDir, "winscard.ini");

        var b25Exists = File.Exists(appLocalB25) || (!string.IsNullOrWhiteSpace(tvTestLocalB25) && File.Exists(tvTestLocalB25));
        var workerExists = File.Exists(workerPath);
        var tvTestIniExists = !string.IsNullOrWhiteSpace(tvTestIni) && File.Exists(tvTestIni);
        var winSCardExists = File.Exists(appLocalWinSCard) || (!string.IsNullOrWhiteSpace(tvTestWinSCard) && File.Exists(tvTestWinSCard));
        var winSCardIniExists = !string.IsNullOrWhiteSpace(tvTestWinSCardIni) && File.Exists(tvTestWinSCardIni);

        log.Add("TVAIREPGREC_RUNTIME_PREREQUISITE", b25Exists && workerExists ? "OK" : "WARN",
            $"worker={(workerExists ? "OK" : "MISSING")} " +
            $"b25Decoder={(b25Exists ? "OK" : "MISSING")} " +
            $"winscard={(winSCardExists ? "OK_OR_NOT_REQUIRED" : "MISSING_OR_NOT_REQUIRED")} winscardIni={(winSCardIniExists ? "OK" : "MISSING_OR_NOT_REQUIRED")} " +
            $"tvTestIni={(tvTestIniExists ? "OK" : "MISSING")} " +
            $"paths=diagnostic_only note=runtime_prerequisite_summary rule=runtime_prerequisite_release_trim");
    }
    catch (Exception ex)
    {
        log.Add("TVAIREPGREC_RUNTIME_PREREQUISITE", "WARN",
            $"result=CHECK_FAILED error={SafeRuntimePrereqLogValue(ex.GetType().Name)} message={SafeRuntimePrereqLogValue(ex.Message)} rule=runtime_prerequisite_release_trim");
    }
}




static string ProgramGuideServiceKey3(ushort networkId, ushort transportStreamId, ushort serviceId)
    => $"{networkId}:{transportStreamId}:{serviceId}";

static string ProgramGuideChannelServiceKey(ChannelTarget ch)
    => ProgramGuideServiceKey3(ch.OriginalNetworkId, ch.TransportStreamId, ch.ServiceId);

static IReadOnlyList<ChannelTarget> BuildCurrentProgramGuideChannels(ChannelFileLoader channelLoader)
    => channelLoader.Load().Targets.ToList();

static HashSet<string> BuildProgramGuideChannelServiceKeySet(IEnumerable<ChannelTarget> channels)
    => channels.Select(ProgramGuideChannelServiceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

static string SafeProgramGuideProjectionLogValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    return value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Replace("|", "/").Replace("\"", "'").Trim();
}

static string NormalizeProgramGuideServiceName(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var normalized = value.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToUpperInvariant();
    var chars = normalized.Where(c => !char.IsWhiteSpace(c)).ToArray();
    return new string(chars);
}

static ProgramGuideEpgEventDto NormalizeProgramGuideEventForDisplay(EpgEvent e, IReadOnlyDictionary<string, string>? serviceDisplayNameByKey = null)
{
    // release_contract: ProgramGuideの廃止済みbody routeは再導入しない。
    // 番組表セル/API投影はDB raw descriptorから作ったCellTextを正本にする。
    // 番組表セル本文は現在のCellText正本だけから生成する。
    var cellText = ProgramGuideCellTextDecoder.Decode(e);
    var displayServiceName = serviceDisplayNameByKey is not null
        && serviceDisplayNameByKey.TryGetValue(ProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId), out var currentName)
        && !string.IsNullOrWhiteSpace(currentName)
            ? currentName
            : e.ServiceName;
    return new ProgramGuideEpgEventDto(
        e.NetworkId,
        e.TransportStreamId,
        e.ServiceId,
        e.EventId,
        displayServiceName,
        cellText.Title,
        cellText.Outline,
        e.Genre,
        e.GenreCodes,
        e.DurationSeconds,
        e.Start,
        e.End,
        ProgramGuideWaveGroupFromNetworkId(e.NetworkId),
        false,
        null,
        string.Empty,
        e.TableId,
        e.SectionNumber,
        e.VersionNumber,
        e.RawShortEventDescriptorHex,
        e.RawExtendedEventDescriptorHex,
        e.RawDescriptorLoopHex,
        cellText,
        ProjectedEventKey.FromDb(e).Value,
        ProjectedEventStates.DbOnly,
        ProjectedEventSourceKinds.TvAirDb,
        string.Empty,
        $"db:{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}",
        "db.raw_descriptor.common_cell_decoder",
        true);
}


static string ProjectedProgramGuideEventServiceKey(ProjectedProgramEvent e)
    => ProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId);

static bool ProjectedProgramGuideOverlapsDay(ProjectedProgramEvent e, DateTime dayStart, DateTime dayEnd)
    => e.End > dayStart && e.Start < dayEnd;

static bool ProgramGuideProjectedContains(string? value, string keyword)
    => !string.IsNullOrEmpty(value)
       && !string.IsNullOrEmpty(keyword)
       && value.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);

static ProjectedProgramGuideProjectionFallbackContext BuildProjectedProgramGuideProjectionFallbackContext(IReadOnlyList<ProjectedProgramEvent> events)
{
    var ctx = new ProjectedProgramGuideProjectionFallbackContext();

    static List<ProjectedProgramEvent> Ordered(IEnumerable<ProjectedProgramEvent> values)
        => values.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EventId).ToList();

    foreach (var g in events.GroupBy(e => $"{ProgramGuideWaveGroupFromNetworkId(e.NetworkId)}:{e.ServiceId}:{NormalizeProgramGuideServiceName(e.ServiceName)}", StringComparer.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(g.Key) || g.Key.EndsWith(":", StringComparison.Ordinal)) continue;
        var identityCount = g.Select(ProjectedProgramGuideEventServiceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (identityCount == 1)
            ctx.ByNameSid[g.Key] = Ordered(g);
    }

    foreach (var g in events.GroupBy(e => $"{ProgramGuideWaveGroupFromNetworkId(e.NetworkId)}:{e.ServiceId}", StringComparer.OrdinalIgnoreCase))
    {
        var identityCount = g.Select(ProjectedProgramGuideEventServiceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (identityCount == 1)
            ctx.ByGroupSidUnique[g.Key] = Ordered(g);
    }

    // External EPG may use a different ONID/TSID authority from the TvAIr
    // display channel list.  These keys intentionally ignore the external
    // network identity and are used only for runtime projection-to-display
    // assignment; they never write back to epg_events.
    foreach (var g in events.GroupBy(e => $"{e.ServiceId}:{NormalizeProgramGuideServiceName(e.ServiceName)}", StringComparer.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(g.Key) || g.Key.EndsWith(":", StringComparison.Ordinal)) continue;
        ctx.BySidName[g.Key] = Ordered(g);
    }

    foreach (var g in events.GroupBy(e => NormalizeProgramGuideServiceName(e.ServiceName), StringComparer.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(g.Key)) continue;
        ctx.ByNameOnly[g.Key] = Ordered(g);
    }

    foreach (var g in events
        .Where(e => !e.DbEventExists)
        .GroupBy(ProjectedProgramGuideOverlayGroupKey, StringComparer.OrdinalIgnoreCase))
    {
        ctx.OverlayOnlyGroupsByIdentity[g.Key] = Ordered(g);
    }

    return ctx;
}

static IReadOnlyList<ProjectedProgramEvent> ResolveProjectedProgramGuideChannelEvents(
    ChannelTarget ch,
    IReadOnlyDictionary<string, IReadOnlyList<ProjectedProgramEvent>> byKey,
    ProjectedProgramGuideProjectionFallbackContext fallbackContext,
    DateTime dayStart,
    DateTime dayEnd,
    out string resolveSource)
{
    var exactKey = ProgramGuideChannelServiceKey(ch);
    var waveGroup = ProgramGuideWaveGroupFromNetworkId(ch.OriginalNetworkId);
    var nameKey = $"{waveGroup}:{ch.ServiceId}:{NormalizeProgramGuideServiceName(ch.Name)}";
    var groupSidKey = $"{waveGroup}:{ch.ServiceId}";

    var sources = new List<string>();
    var candidates = new List<ProjectedProgramEvent>();

    void AddCandidateSet(string source, IReadOnlyList<ProjectedProgramEvent>? values)
    {
        if (values is null || values.Count == 0) return;
        var dayValues = values
            .Where(e => ProjectedProgramGuideOverlapsDay(e, dayStart, dayEnd))
            .ToList();
        if (dayValues.Count == 0) return;
        candidates.AddRange(dayValues);
        sources.Add(source);
    }

    if (byKey.TryGetValue(exactKey, out var exact))
    {
        var exactDay = exact
            .Where(e => ProjectedProgramGuideOverlapsDay(e, dayStart, dayEnd))
            .ToList();
        if (exactDay.Count > 0)
        {
            // Exact NID/TSID/SID is the display authority. Fallback identities are
            // only a recovery path when the exact service has no event in this day.
            // Mixing fallback rows into an already-resolved exact service creates a
            // second projection authority and can overwrite the canonical timeline.
            resolveSource = "exact";
            return ProjectedProgramGuideNormalizeServiceDayEvents(exactDay, dayStart, dayEnd);
        }
    }

    if (fallbackContext.ByNameSid.TryGetValue(nameKey, out var byNameSid))
        AddCandidateSet("name_sid_unique", byNameSid);

    if (fallbackContext.ByGroupSidUnique.TryGetValue(groupSidKey, out var byGroupSid))
        AddCandidateSet("group_sid_unique", byGroupSid);

    var sidNameKey = $"{ch.ServiceId}:{NormalizeProgramGuideServiceName(ch.Name)}";
    if (fallbackContext.BySidName.TryGetValue(sidNameKey, out var bySidName))
        AddCandidateSet("sid_name_display_identity", bySidName);

    var nameOnlyKey = NormalizeProgramGuideServiceName(ch.Name);
    if (fallbackContext.ByNameOnly.TryGetValue(nameOnlyKey, out var byNameOnly))
        AddCandidateSet("name_display_identity", byNameOnly);

    if (candidates.Count > 0)
    {
        resolveSource = string.Join("+", sources.Distinct(StringComparer.OrdinalIgnoreCase));
        return ProjectedProgramGuideNormalizeServiceDayEvents(candidates, dayStart, dayEnd);
    }

    if (byKey.TryGetValue(exactKey, out var exactAny) && exactAny.Count > 0)
    {
        resolveSource = "exact_no_day_overlap";
        return exactAny;
    }

    resolveSource = "none";
    return Array.Empty<ProjectedProgramEvent>();
}

static string ProjectedProgramGuideOverlayGroupKey(ProjectedProgramEvent e)
{
    var serviceName = NormalizeProgramGuideServiceName(e.ServiceName);
    if (!string.IsNullOrWhiteSpace(serviceName))
        return $"name:{serviceName}";

    return $"identity:{e.SourcePluginId}:{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}";
}

static bool ProjectedProgramGuideHasAnyTimeOverlap(ProjectedProgramEvent overlay, IReadOnlyList<ProjectedProgramEvent> timeline)
{
    foreach (var ev in timeline)
    {
        if (ev.End <= overlay.Start || ev.Start >= overlay.End) continue;
        return true;
    }
    return false;
}

static int ProjectedProgramGuideOverlayFitScore(IReadOnlyList<ProjectedProgramEvent> overlays, IReadOnlyList<ProjectedProgramEvent> baseEvents, ChannelTarget ch, DateTime dayStart, DateTime dayEnd)
{
    if (overlays.Count == 0) return int.MinValue;

    var compatible = 0;
    var overlap = 0;
    var adjacency = 0;
    var normalizedChannelName = NormalizeProgramGuideServiceName(ch.Name);

    foreach (var overlay in overlays)
    {
        if (!ProjectedProgramGuideOverlapsDay(overlay, dayStart, dayEnd)) continue;
        if (ProjectedProgramGuideHasAnyTimeOverlap(overlay, baseEvents))
        {
            overlap++;
            continue;
        }

        compatible++;

        var nearestBoundaryMinutes = double.PositiveInfinity;
        foreach (var ev in baseEvents)
        {
            if (ev.End <= overlay.Start)
                nearestBoundaryMinutes = Math.Min(nearestBoundaryMinutes, (overlay.Start - ev.End).Duration().TotalMinutes);
            if (ev.Start >= overlay.End)
                nearestBoundaryMinutes = Math.Min(nearestBoundaryMinutes, (ev.Start - overlay.End).Duration().TotalMinutes);
        }

        nearestBoundaryMinutes = Math.Min(nearestBoundaryMinutes, Math.Abs((overlay.Start - dayStart).TotalMinutes));
        nearestBoundaryMinutes = Math.Min(nearestBoundaryMinutes, Math.Abs((dayEnd - overlay.End).TotalMinutes));

        if (nearestBoundaryMinutes <= 15) adjacency += 8;
        else if (nearestBoundaryMinutes <= 60) adjacency += 4;
        else if (nearestBoundaryMinutes <= 180) adjacency += 1;
    }

    // Do not reject an overlay-only group only because every candidate row
    // overlaps existing DB-backed timeline rows.  Assignment and DB-overlap
    // suppression are separate steps: first bridge the overlay-only group to a
    // display channel, then let BuildProjectedProgramGuideTimelineEvents count
    // and suppress DB-overlapping rows.  Returning int.MinValue here leaves
    // overlayAssignedCandidates at zero even though overlay-only rows exist.
    var hasInRangeOverlay = compatible > 0 || overlap > 0;
    if (!hasInRangeOverlay) return int.MinValue;

    var identityBonus = 0;
    foreach (var overlay in overlays.Take(3))
    {
        if (overlay.ServiceId == ch.ServiceId) identityBonus += 300;
        if (string.Equals(NormalizeProgramGuideServiceName(overlay.ServiceName), normalizedChannelName, StringComparison.OrdinalIgnoreCase)) identityBonus += 500;
        if (!string.IsNullOrWhiteSpace(overlay.SourceEventKey)
            && !string.IsNullOrWhiteSpace(normalizedChannelName)
            && NormalizeProgramGuideServiceName(overlay.SourceEventKey).Contains(normalizedChannelName, StringComparison.OrdinalIgnoreCase))
        {
            identityBonus += 200;
        }
    }

    // The score is used only for runtime display assignment of overlay-only
    // rows.  Penalize overlaps strongly, but do not require a unique service
    // identity: the caller may use this as a bridge after the normal service
    // fallback table has already proven the visible display services.
    return compatible * 1000 + adjacency * 10 + identityBonus - overlap * 2000;
}

static Dictionary<string, string> BuildProjectedProgramGuideOverlayGroupChannelMap(
    IReadOnlyList<ChannelTarget> channels,
    IReadOnlyDictionary<string, IReadOnlyList<ProjectedProgramEvent>> byKey,
    ProjectedProgramGuideProjectionFallbackContext fallbackContext,
    DateTime dayStart,
    DateTime dayEnd)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (fallbackContext.OverlayOnlyGroupsByIdentity.Count == 0) return result;

    var baseEventsByChannel = new Dictionary<string, IReadOnlyList<ProjectedProgramEvent>>(StringComparer.OrdinalIgnoreCase);
    foreach (var ch in channels)
    {
        var channelEvents = ResolveProjectedProgramGuideChannelEvents(ch, byKey, fallbackContext, dayStart, dayEnd, out _);
        baseEventsByChannel[ProgramGuideChannelServiceKey(ch)] = channelEvents
            .Where(e => e.DbEventExists)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.End)
            .ThenBy(e => e.EventId)
            .ToList();
    }

    foreach (var group in fallbackContext.OverlayOnlyGroupsByIdentity)
    {
        var overlays = group.Value
            .Where(e => !e.DbEventExists && ProjectedProgramGuideOverlapsDay(e, dayStart, dayEnd))
            .OrderBy(e => e.Start)
            .ThenBy(e => e.End)
            .ThenBy(e => e.EventId)
            .ToList();
        if (overlays.Count == 0) continue;

        string? bestChannelKey = null;
        var bestScore = int.MinValue;
        var secondScore = int.MinValue;

        foreach (var ch in channels)
        {
            var channelKey = ProgramGuideChannelServiceKey(ch);
            var baseEvents = baseEventsByChannel.TryGetValue(channelKey, out var values) ? values : Array.Empty<ProjectedProgramEvent>();
            var score = ProjectedProgramGuideOverlayFitScore(overlays, baseEvents, ch, dayStart, dayEnd);
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestChannelKey = channelKey;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        if (bestChannelKey is null || bestScore == int.MinValue) continue;

        // Overlay-only rows have already been proven to exist by the projection
        // merge and the visible service fallback table is built separately.
        // Do not drop the bridge merely because several visible channels have
        // the same temporal score; use the deterministic best channel selected
        // above and let the per-channel DB-overlap suppression reject unsafe
        // rows.  This connects daySorted overlay-only rows to the displayed
        // timeline instead of leaving overlayAssignedCandidates at zero.
        result[group.Key] = bestChannelKey;
    }

    return result;
}


static IReadOnlyList<ProjectedProgramEvent> ProjectedProgramGuideNormalizeServiceDayEvents(
    IEnumerable<ProjectedProgramEvent> events,
    DateTime dayStart,
    DateTime dayEnd)
{
    var candidates = events
        .Where(e => ProjectedProgramGuideOverlapsDay(e, dayStart, dayEnd))
        .GroupBy(e => $"{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}:{e.Start.Ticks}:{e.End.Ticks}:{e.SourceKind}:{e.SourcePluginId}:{e.SourceEventKey}", StringComparer.OrdinalIgnoreCase)
        .Select(g => g
            .OrderBy(e => e.DbEventExists ? 0 : 1)
            .ThenBy(e => e.EventId)
            .ThenBy(e => e.SourceKind, StringComparer.OrdinalIgnoreCase)
            .First())
        .OrderBy(e => e.Start)
        .ThenBy(e => e.End)
        .ThenBy(e => e.EventId)
        .ThenBy(e => e.SourceKind, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var filtered = candidates
        .Where(e => candidates.Count(other =>
            other.EventId != e.EventId &&
            other.Start >= e.Start &&
            other.End <= e.End &&
            other.End > other.Start) < 2)
        .ToList();

    return filtered.Count > 0 ? filtered : candidates;
}

static IReadOnlyList<ProjectedProgramEvent> BuildProjectedProgramGuideTimelineEvents(
    IReadOnlyList<ProjectedProgramEvent> sortedEvents,
    IReadOnlyList<ChannelTarget> channels,
    DateTime dayStart,
    DateTime dayEnd,
    LogRepository log,
    DateOnly baseDate)
{
    var daySortedEvents = sortedEvents
        .Where(e => ProjectedProgramGuideOverlapsDay(e, dayStart, dayEnd))
        .OrderBy(ProjectedProgramGuideEventServiceKey, StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.Start)
        .ThenBy(e => e.End)
        .ThenBy(e => e.EventId)
        .ThenBy(e => e.SourceKind, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var result = new List<ProjectedProgramEvent>(daySortedEvents.Count + channels.Count * 2);
    var byKey = daySortedEvents
        .GroupBy(ProjectedProgramGuideEventServiceKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => g.Key,
            g => ProjectedProgramGuideNormalizeServiceDayEvents(g, dayStart, dayEnd),
            StringComparer.OrdinalIgnoreCase);

    var fallbackContext = BuildProjectedProgramGuideProjectionFallbackContext(daySortedEvents);
    var displaySidCounts = channels
        .GroupBy(ch => ch.ServiceId)
        .ToDictionary(g => g.Key, g => g.Count());
    var displayExactKeys = channels
        .Select(ProgramGuideChannelServiceKey)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var displayNames = channels
        .Select(ch => NormalizeProgramGuideServiceName(ch.Name))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var overlayGroupChannelMap = BuildProjectedProgramGuideOverlayGroupChannelMap(channels, byKey, fallbackContext, dayStart, dayEnd);
    var fallbackHits = new List<string>();
    var overlayTotalInRange = daySortedEvents.Count(e => !e.DbEventExists);
    var overlayAssignedCandidates = 0;
    var overlayAcceptedTotal = 0;
    var overlaySuppressedByDbOverlapTotal = 0;
    var overlayOnlyIdentityExactCandidates = 0;
    var overlayOnlyRejectedIdentityNotExact = 0;
    var overlayOnlyRejectedSidOnly = 0;
    var overlayOnlyRejectedNameOnly = 0;
    var overlayOnlyRejectedBridgeOnly = 0;
    var overlayOnlyRejectedDbOverlap = 0;
    var overlayOnlyRejectedServiceUnmatched = 0;
    var overlayOnlyRejectSamples = new List<string>();

    void CountOverlayOnlyReject(ProjectedProgramEvent ev, string reason, string identityMatch, string matchedChannelKey = "-")
    {
        switch (reason)
        {
            case "bridge_only_not_allowed": overlayOnlyRejectedBridgeOnly++; break;
            case "sid_only_not_allowed": overlayOnlyRejectedSidOnly++; break;
            case "name_only_not_allowed": overlayOnlyRejectedNameOnly++; break;
            case "db_overlap": overlayOnlyRejectedDbOverlap++; break;
            case "service_unmatched": overlayOnlyRejectedServiceUnmatched++; break;
            default: overlayOnlyRejectedIdentityNotExact++; break;
        }

        if (overlayOnlyRejectSamples.Count < 12)
        {
            overlayOnlyRejectSamples.Add(
                $"reason={reason}:identityMatch={identityMatch}:channel={SafeProgramGuideProjectionLogValue(matchedChannelKey)}:nid={ev.NetworkId}:tsid={ev.TransportStreamId}:sid={ev.ServiceId}:eventId={ev.EventId}:start={ev.Start:MMddHHmm}:end={ev.End:MMddHHmm}:title={SafeProgramGuideProjectionLogValue(ev.Title)}");
        }
    }

    foreach (var overlay in daySortedEvents.Where(e => !e.DbEventExists && ProjectedProgramGuideOverlapsDay(e, dayStart, dayEnd)))
    {
        var overlayServiceKey = ProjectedProgramGuideEventServiceKey(overlay);
        if (displayExactKeys.Contains(overlayServiceKey))
        {
            overlayOnlyIdentityExactCandidates++;
            if (ProjectedProgramGuideIsFullyCoveredByDbTimelineInDay(overlay, daySortedEvents, dayStart, dayEnd))
                CountOverlayOnlyReject(overlay, "db_overlap", "exact", overlayServiceKey);
            continue;
        }

        if (overlayGroupChannelMap.TryGetValue(ProjectedProgramGuideOverlayGroupKey(overlay), out var bridgeChannelKey))
        {
            CountOverlayOnlyReject(overlay, "bridge_only_not_allowed", "bridge", bridgeChannelKey);
            continue;
        }

        if (displaySidCounts.ContainsKey(overlay.ServiceId))
        {
            CountOverlayOnlyReject(overlay, "sid_only_not_allowed", "sid", "-");
            continue;
        }

        var normalizedOverlayName = NormalizeProgramGuideServiceName(overlay.ServiceName);
        if (!string.IsNullOrWhiteSpace(normalizedOverlayName) && displayNames.Contains(normalizedOverlayName))
        {
            CountOverlayOnlyReject(overlay, "name_only_not_allowed", "name", "-");
            continue;
        }

        CountOverlayOnlyReject(overlay, "service_unmatched", "none", "-");
    }

    foreach (var ch in channels)
    {
        var key = ProgramGuideChannelServiceKey(ch);
        var list = ResolveProjectedProgramGuideChannelEvents(ch, byKey, fallbackContext, dayStart, dayEnd, out var resolveSource);
        if (!string.Equals(resolveSource, "exact", StringComparison.OrdinalIgnoreCase) && list.Count > 0 && fallbackHits.Count < 12)
        {
            fallbackHits.Add($"{SafeProgramGuideProjectionLogValue(ch.Name)}:{key}->{ProjectedProgramGuideEventServiceKey(list[0])}:{resolveSource}:events={list.Count}");
        }

        var baseEvents = list
            .Where(e => e.DbEventExists)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.End)
            .ThenBy(e => e.EventId)
            .ToList();

        // overlay-only is a hole-fill mechanism only.  Do not adopt rows reached
        // through service fallback, SID-only, name-only, or bridge assignment.
        // A displayed overlay-only row must already belong to this exact
        // NID/TSID/SID service timeline.
        var overlayOnlyEvents = byKey.TryGetValue(key, out var exactChannelEvents)
            ? exactChannelEvents
                .Where(e => !e.DbEventExists)
                .Where(e => ProjectedProgramGuideOverlapsDay(e, dayStart, dayEnd))
                .GroupBy(e => $"{e.SourcePluginId}:{e.SourceEventKey}:{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}:{e.Start.Ticks}:{e.End.Ticks}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EventId).First())
                .OrderBy(e => e.Start)
                .ThenBy(e => e.End)
                .ThenBy(e => e.EventId)
                .ToList()
            : new List<ProjectedProgramEvent>();

        var acceptedForChannel = new List<ProjectedProgramEvent>(baseEvents.Count + overlayOnlyEvents.Count);

        var cursor = dayStart;
        foreach (var ev in baseEvents)
        {
            if (ev.End <= dayStart || ev.Start >= dayEnd) continue;

            var evStart = ev.Start < dayStart ? dayStart : ev.Start;
            var evEnd = ev.End > dayEnd ? dayEnd : ev.End;
            if (evEnd <= evStart) continue;

            var displayEvent = ev;
            if (evStart < cursor)
            {
                if (evEnd <= cursor)
                    continue;

                displayEvent = CloneProjectedProgramGuideTimelineEvent(ev, cursor, evEnd);
                evStart = cursor;
            }

            acceptedForChannel.Add(displayEvent);
            if (evEnd > cursor) cursor = evEnd;
        }

        var overlayAccepted = 0;
        var overlaySuppressedByDbOverlap = 0;
        overlayAssignedCandidates += overlayOnlyEvents.Count;
        foreach (var overlay in overlayOnlyEvents)
        {
            if (overlay.End <= dayStart || overlay.Start >= dayEnd) continue;

            var displayOverlay = ProjectedProgramGuideAlignOverlayToChannel(overlay, ch);
            var uncoveredFragments = ProjectedProgramGuideSubtractDbTimeline(
                displayOverlay,
                acceptedForChannel,
                dayStart,
                dayEnd);

            if (uncoveredFragments.Count == 0)
            {
                overlaySuppressedByDbOverlap++;
                continue;
            }

            foreach (var fragment in uncoveredFragments)
                acceptedForChannel.Add(fragment);

            // Count the source occurrence once even when local DB inserts split its
            // display geometry into multiple fragments. The ProjectedEventKey and
            // SourceEventKey remain the original external occurrence identity, so
            // reservation actions still resolve the authoritative full event.
            overlayAccepted++;
        }

        overlayAcceptedTotal += overlayAccepted;
        overlaySuppressedByDbOverlapTotal += overlaySuppressedByDbOverlap;

        foreach (var ev in acceptedForChannel
            .OrderBy(e => e.Start)
            .ThenBy(e => e.End)
            .ThenBy(e => e.DbEventExists ? 0 : 1)
            .ThenBy(e => e.EventId))
        {
            result.Add(ev);
        }
    }


    if (overlayTotalInRange > 0 || overlayAcceptedTotal > 0 || overlaySuppressedByDbOverlapTotal > 0)
    {
        var overlayUnmatched = Math.Max(0, overlayTotalInRange - overlayAssignedCandidates);
        log.Add("PROGRAM_GUIDE_PROJECTED_OVERLAY_TIMELINE_SUMMARY", "API",
            $"result=OK date={baseDate:yyyy-MM-dd} overlayTotalInRange={overlayTotalInRange} overlayAssignedCandidates={overlayAssignedCandidates} overlayAccepted={overlayAcceptedTotal} overlaySuppressedByDbOverlap={overlaySuppressedByDbOverlapTotal} overlayUnmatchedChannels={overlayUnmatched} displayTimelineEvents={result.Count} dbWrite=none rule=program_guide_projection_contract");

        log.Add("OVERLAY_ONLY_ADOPTION_SUMMARY", "API",
            $"result=OK date={baseDate:yyyy-MM-dd} acceptedExternalEvents=runtime_store mergeCandidates={overlayTotalInRange} overlayOnlyCandidates={overlayTotalInRange} identityExactCandidates={overlayOnlyIdentityExactCandidates} rejectedIdentityNotExact={overlayOnlyRejectedIdentityNotExact} rejectedSidOnly={overlayOnlyRejectedSidOnly} rejectedNameOnly={overlayOnlyRejectedNameOnly} rejectedBridgeOnly={overlayOnlyRejectedBridgeOnly} dbOverlapPreflight={overlayOnlyRejectedDbOverlap} rejectedDbOverlap={overlaySuppressedByDbOverlapTotal} rejectedSameServiceDbEvent={overlaySuppressedByDbOverlapTotal} rejectedServiceUnmatched={overlayOnlyRejectedServiceUnmatched} overlayOnlyDisplayed={overlayAcceptedTotal} dbWithOverlay=see_PROGRAM_GUIDE_PROJECTED_DISPLAY_HANDOFF sample={SafeProgramGuideProjectionLogValue(string.Join('|', overlayOnlyRejectSamples))} policy=exact_nid_tsid_sid_and_no_db_overlap bridge=column_resolution_only_not_adoption dbWrite=none rule=program_guide_overlay_only_strict_adoption");
    }

    if (fallbackHits.Count > 0)
    {
        log.Add("PROGRAMGUIDE_SERVICE_PROJECTION_FALLBACK", "APPLIED",
            $"result=APPLIED date={baseDate:yyyy-MM-dd} count={fallbackHits.Count} sample={SafeProgramGuideProjectionLogValue(string.Join('|', fallbackHits))} rule=release_contract");
    }

    return result;
}

static ProjectedProgramEvent ProjectedProgramGuideAlignOverlayToChannel(ProjectedProgramEvent source, ChannelTarget ch)
{
    if (source.DbEventExists) return source;

    if (source.NetworkId == ch.OriginalNetworkId
        && source.TransportStreamId == ch.TransportStreamId
        && source.ServiceId == ch.ServiceId)
    {
        return source;
    }

    return new ProjectedProgramEvent
    {
        Key = source.Key,
        ProjectionState = source.ProjectionState,
        NetworkId = ch.OriginalNetworkId,
        TransportStreamId = ch.TransportStreamId,
        ServiceId = ch.ServiceId,
        EventId = source.EventId,
        Start = source.Start,
        End = source.End,
        DurationSeconds = source.DurationSeconds,
        CanonicalStart = source.CanonicalStart,
        CanonicalEnd = source.CanonicalEnd,
        IsTimelineFragment = source.IsTimelineFragment,
        TimelineFragmentReason = source.TimelineFragmentReason,
        ServiceName = string.IsNullOrWhiteSpace(ch.Name) ? source.ServiceName : ch.Name,
        Title = source.Title,
        ShortText = source.ShortText,
        ExtendedText = source.ExtendedText,
        CellText = source.CellText,
        Genre = source.Genre,
        GenreCodes = source.GenreCodes,
        DbEventExists = false,
        DbEvent = null,
        SourceKind = source.SourceKind,
        SourcePluginId = source.SourcePluginId,
        SourceEventKey = source.SourceEventKey,
        ProjectionTitleDbPresent = source.ProjectionTitleDbPresent,
        ProjectionTitleOverlayCandidatePresent = source.ProjectionTitleOverlayCandidatePresent,
        ProjectionTitleSource = source.ProjectionTitleSource,
        ProjectionOutlineDbPresent = source.ProjectionOutlineDbPresent,
        ProjectionOutlineOverlayCandidatePresent = source.ProjectionOutlineOverlayCandidatePresent,
        ProjectionOutlineSource = source.ProjectionOutlineSource,
        ProjectionDetailDbPresent = source.ProjectionDetailDbPresent,
        ProjectionDetailOverlayCandidatePresent = source.ProjectionDetailOverlayCandidatePresent,
        ProjectionDetailSource = source.ProjectionDetailSource
    };
}

static IReadOnlyList<ProjectedProgramEvent> ProjectedProgramGuideSubtractDbTimeline(
    ProjectedProgramEvent overlay,
    IReadOnlyList<ProjectedProgramEvent> timeline,
    DateTime dayStart,
    DateTime dayEnd)
{
    var start = overlay.Start < dayStart ? dayStart : overlay.Start;
    var end = overlay.End > dayEnd ? dayEnd : overlay.End;
    if (end <= start) return Array.Empty<ProjectedProgramEvent>();

    var covered = timeline
        .Where(ev => ev.DbEventExists)
        .Where(ev => ev.NetworkId == overlay.NetworkId
                     && ev.TransportStreamId == overlay.TransportStreamId
                     && ev.ServiceId == overlay.ServiceId)
        .Select(ev => (Start: ev.Start < start ? start : ev.Start, End: ev.End > end ? end : ev.End))
        .Where(x => x.End > x.Start)
        .OrderBy(x => x.Start)
        .ThenBy(x => x.End)
        .ToList();

    if (covered.Count == 0)
        return new[] { CloneProjectedProgramGuideTimelineEvent(overlay, start, end) };

    var merged = new List<(DateTime Start, DateTime End)>();
    foreach (var interval in covered)
    {
        if (merged.Count == 0 || interval.Start > merged[^1].End)
        {
            merged.Add(interval);
            continue;
        }

        if (interval.End > merged[^1].End)
            merged[^1] = (merged[^1].Start, interval.End);
    }

    var fragments = new List<ProjectedProgramEvent>();
    var cursor = start;
    foreach (var interval in merged)
    {
        if (interval.Start > cursor)
            fragments.Add(CloneProjectedProgramGuideTimelineEvent(overlay, cursor, interval.Start));

        if (interval.End > cursor)
            cursor = interval.End;
        if (cursor >= end) break;
    }

    if (cursor < end)
        fragments.Add(CloneProjectedProgramGuideTimelineEvent(overlay, cursor, end));

    return fragments;
}

static bool ProjectedProgramGuideIsFullyCoveredByDbTimelineInDay(
    ProjectedProgramEvent overlay,
    IReadOnlyList<ProjectedProgramEvent> dayEvents,
    DateTime dayStart,
    DateTime dayEnd)
{
    return ProjectedProgramGuideSubtractDbTimeline(overlay, dayEvents, dayStart, dayEnd).Count == 0;
}

static ProjectedProgramEvent CloneProjectedProgramGuideTimelineEvent(ProjectedProgramEvent source, DateTime start, DateTime end)
{
    var durationSeconds = (int)Math.Max(0, Math.Round((end - start).TotalSeconds));
    return new ProjectedProgramEvent
    {
        Key = source.Key,
        ProjectionState = source.ProjectionState,
        NetworkId = source.NetworkId,
        TransportStreamId = source.TransportStreamId,
        ServiceId = source.ServiceId,
        EventId = source.EventId,
        Start = start,
        End = end,
        DurationSeconds = durationSeconds,
        CanonicalStart = source.CanonicalStart == default ? source.Start : source.CanonicalStart,
        CanonicalEnd = source.CanonicalEnd == default ? source.End : source.CanonicalEnd,
        IsTimelineFragment = source.IsTimelineFragment || start != source.Start || end != source.End,
        TimelineFragmentReason = source.IsTimelineFragment ? source.TimelineFragmentReason : "display_range_clip",
        ServiceName = source.ServiceName,
        Title = source.Title,
        ShortText = source.ShortText,
        ExtendedText = source.ExtendedText,
        CellText = source.CellText,
        Genre = source.Genre,
        GenreCodes = source.GenreCodes,
        DbEventExists = source.DbEventExists,
        DbEvent = source.DbEvent,
        SourceKind = source.SourceKind,
        SourcePluginId = source.SourcePluginId,
        SourceEventKey = source.SourceEventKey,
        ProjectionTitleDbPresent = source.ProjectionTitleDbPresent,
        ProjectionTitleOverlayCandidatePresent = source.ProjectionTitleOverlayCandidatePresent,
        ProjectionTitleSource = source.ProjectionTitleSource,
        ProjectionOutlineDbPresent = source.ProjectionOutlineDbPresent,
        ProjectionOutlineOverlayCandidatePresent = source.ProjectionOutlineOverlayCandidatePresent,
        ProjectionOutlineSource = source.ProjectionOutlineSource,
        ProjectionDetailDbPresent = source.ProjectionDetailDbPresent,
        ProjectionDetailOverlayCandidatePresent = source.ProjectionDetailOverlayCandidatePresent,
        ProjectionDetailSource = source.ProjectionDetailSource
    };
}

static ProgramGuideEpgEventDto NormalizeProjectedProgramGuideEventForDisplay(ProjectedProgramEvent e, IReadOnlyDictionary<string, string>? serviceDisplayNameByKey = null)
{
    if (e.DbEvent is not null && e.DbEventExists && string.Equals(e.ProjectionState, ProjectedEventStates.DbOnly, StringComparison.OrdinalIgnoreCase))
        return NormalizeProgramGuideEventForDisplay(e.DbEvent, serviceDisplayNameByKey);

    var displayServiceName = serviceDisplayNameByKey is not null
        && serviceDisplayNameByKey.TryGetValue(ProjectedProgramGuideEventServiceKey(e), out var currentName)
        && !string.IsNullOrWhiteSpace(currentName)
            ? currentName
            : e.ServiceName;

    var cellText = BuildProjectedProgramGuideCellText(e);
    return new ProgramGuideEpgEventDto(
        e.NetworkId,
        e.TransportStreamId,
        e.ServiceId,
        e.EventId,
        displayServiceName,
        cellText.Title,
        cellText.Outline,
        e.Genre,
        e.GenreCodes,
        e.DurationSeconds,
        e.Start,
        e.End,
        ProgramGuideWaveGroupFromNetworkId(e.NetworkId),
        false,
        null,
        string.Empty,
        e.DbEvent?.TableId ?? 0,
        e.DbEvent?.SectionNumber ?? 0,
        e.DbEvent?.VersionNumber ?? 0,
        e.DbEvent?.RawShortEventDescriptorHex ?? string.Empty,
        e.DbEvent?.RawExtendedEventDescriptorHex ?? string.Empty,
        e.DbEvent?.RawDescriptorLoopHex ?? string.Empty,
        cellText,
        e.Key.Value,
        e.ProjectionState,
        e.SourceKind,
        e.SourcePluginId,
        e.SourceEventKey,
        e.DbEventExists ? "db.raw_descriptor.with_external_projection" : "external_epg.projected_event",
        e.DbEventExists);
}

static ProgramGuideCellText BuildProjectedProgramGuideCellText(ProjectedProgramEvent e)
{
    if (e.DbEvent is not null && e.DbEventExists && string.Equals(e.ProjectionState, ProjectedEventStates.DbOnly, StringComparison.OrdinalIgnoreCase))
        return ProgramGuideCellTextDecoder.Decode(e.DbEvent);

    var title = FirstProjectedText(e.Title, e.DbEvent is null ? null : EpgProjection.Title(e.DbEvent));
    var outline = FirstProjectedText(e.ShortText, e.DbEvent is null ? null : EpgProjection.ShortText(e.DbEvent));
    var detail = FirstProjectedText(e.ExtendedText);
    if (detail.Length == 0 && e.CellText.Length > 0)
        detail = e.CellText;

    return new ProgramGuideCellText(
        title,
        outline,
        detail,
        string.Empty,
        e.DbEvent?.RawShortEventDescriptorHex ?? string.Empty,
        e.DbEvent?.RawExtendedEventDescriptorHex ?? string.Empty,
        e.DbEventExists ? "db.raw_descriptor.with_external_projection" : "external_epg.projected_event");
}

static string FirstProjectedText(params string?[] values)
{
    foreach (var value in values)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length > 0) return text;
    }
    return string.Empty;
}

#if TVAIR_DEVELOPER_DIAGNOSTICS
static string SafeProgramGuideDiagnosticValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    var text = value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
    return text.Length <= 160 ? text : text[..160] + "…";
}

static int ProgramGuideHexByteLength(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return 0;
    var hexDigits = 0;
    foreach (var ch in value)
    {
        if (Uri.IsHexDigit(ch)) hexDigits++;
    }
    return hexDigits / 2;
}
#endif

static string SafeRuntimePrereqLogValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    return value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
}



app.Run();

enum PluginPresentationLifecycleHint
{
    None,
    Enable,
    Disable
}

sealed class ProjectedProgramGuideProjectionFallbackContext
{
    public Dictionary<string, List<ProjectedProgramEvent>> ByNameSid { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<ProjectedProgramEvent>> ByGroupSidUnique { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<ProjectedProgramEvent>> BySidName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<ProjectedProgramEvent>> ByNameOnly { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<ProjectedProgramEvent>> OverlayOnlyGroupsByIdentity { get; } = new(StringComparer.OrdinalIgnoreCase);
}




public sealed record ProgramGuideEpgEventDto(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    string ServiceName,
    string Title,
    string Description,
    string Genre,
    string GenreCodes,
    int DurationSeconds,
    DateTime Start,
    DateTime End,
    string ProgramGuideWaveGroup,
    bool DisplayRecovered,
    int? DisplayRecoveredReservationId,
    string DisplayRecoveredSource,
    byte TableId,
    byte SectionNumber,
    byte VersionNumber,
    [property: System.Text.Json.Serialization.JsonIgnore] string RawShortEventDescriptorHex,
    [property: System.Text.Json.Serialization.JsonIgnore] string RawExtendedEventDescriptorHex,
    [property: System.Text.Json.Serialization.JsonIgnore] string RawDescriptorLoopHex,
    ProgramGuideCellText CellText,
    string ProjectedEventId,
    string ProjectionState,
    string SourceKind,
    string SourcePluginId,
    string SourceEventKey,
    string TitleSource,
    bool TitleRawPassthrough);

sealed class PluginSeeOtherResult : IResult
{
    private readonly string _location;
    public PluginSeeOtherResult(string location) => _location = string.IsNullOrWhiteSpace(location) ? "/" : location;

    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
        httpContext.Response.Headers.Location = _location;
        return Task.CompletedTask;
    }
}

public sealed record ViewerProfileContractDto(
    string Id,
    string Name,
    bool Enabled,
    bool IsDefault,
    int Order,
    bool IsAuto,
    string TvTestPathKey,
    string Source,
    string Note,
    int TvTestFrameIndex = 0,
    IReadOnlyList<string>? AvailableGroups = null,
    string LogicalViewerSlotId = "",
    bool IsShared = false,
    string ErrorCode = "");


static class ViewerProfileContract
{
    public static IReadOnlyList<ViewerProfileContractDto> BuildProfiles(TvTestSettings settings, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles)
    {
        var profiles = new List<ViewerProfileContractDto>();
        var viewingTuners = TunerRuntimeProfileSource.Build(ini, tunerProfiles)
            .Where(t => string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var order = 0;
        foreach (var tuner in viewingTuners)
        {
            var supportedGroups = SupportedGroupsForTuner(tuner.Group);
            var frameIndex = tuner.DeviceNumber;
            if (frameIndex <= 0) continue;

            var logicalId = NormalizeLogicalViewerSlotId(tuner.LogicalViewerSlotId);
            if (string.IsNullOrWhiteSpace(logicalId)) continue;
            var profileId = $"viewer-slot-{logicalId}";
            var name = $"TVTest{frameIndex}";
            var isShared = supportedGroups.Count == 2;
            var note = $"logicalViewerSlotId={logicalId}; supportedGroups={string.Join(",", supportedGroups)}; tuner={tuner.Name}; deviceNumber={frameIndex}; source=settings_device_number";
            profiles.Add(new ViewerProfileContractDto(profileId, name, true, order == 0, ++order, false,
                $"viewer-profile:{profileId}", "tunerpool-viewing-logical-slot", note,
                frameIndex, supportedGroups, logicalId, isShared));
        }
        return profiles;
    }

    public static ViewerProfileContractDto ResolveRequestedProfile(string? requested, string? requestedGroup, TvTestSettings settings, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles)
    {
        var profiles = BuildProfiles(settings, ini, tunerProfiles);
        var raw = (requested ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("auto", StringComparison.OrdinalIgnoreCase) || raw.Equals("default", StringComparison.OrdinalIgnoreCase))
            return profiles.FirstOrDefault(p => p.IsDefault) ?? Disabled("", "viewer_profile_unavailable");

        var stable = profiles.FirstOrDefault(p => string.Equals(p.Id, raw, StringComparison.OrdinalIgnoreCase));
        if (stable is not null) return stable;

        if (TryParseTvTestOrdinal(raw, out var ordinal))
        {
            var group = NormalizeProfileGroup(requestedGroup);
            var candidates = profiles.Where(p => p.TvTestFrameIndex == ordinal && ProfileSupportsGroup(p, group)).ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1) return Disabled(raw, "ambiguous_legacy_viewer_profile");
            return Disabled(raw, "viewer_profile_unavailable");
        }
        return Disabled(raw, "viewer_profile_unavailable");
    }

    public static bool ProfileSupportsGroup(ViewerProfileContractDto profile, string? requestedGroup)
    {
        if (!profile.Enabled) return false;
        var group = NormalizeProfileGroup(requestedGroup);
        if (string.IsNullOrWhiteSpace(group)) return true;
        var available = profile.AvailableGroups ?? Array.Empty<string>();
        return available.Any(g => string.Equals(NormalizeProfileGroup(g), group, StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildViewerClientId(string pluginId, string viewerProfileId)
        => $"{pluginId}:viewer:{viewerProfileId.Trim().ToLowerInvariant()}";

    public static bool LeaseMatchesProfile(ExternalTunerLeaseDto lease, ViewerProfileContractDto profile)
        => string.Equals(lease.ViewerProfileId?.Trim(), profile.Id, StringComparison.OrdinalIgnoreCase);

    public static string TvTestPathKeyForResolvedProfile(ViewerProfileContractDto profile)
        => !string.IsNullOrWhiteSpace(profile.TvTestPathKey) ? profile.TvTestPathKey : profile.Id;

    private static ViewerProfileContractDto Disabled(string requested, string errorCode)
        => new(requested, requested, false, false, 0, false, string.Empty, "not-configured", errorCode, 0, Array.Empty<string>(), string.Empty, false, errorCode);

    private static IReadOnlyList<string> SupportedGroupsForTuner(string? tunerGroup)
    {
        var group = TunerDisplayName.NormalizeGroup(tunerGroup);
        return group switch
        {
            "GR" => new[] { "GR" },
            "BSCS" => new[] { "BSCS" },
            "HYBRID" => new[] { "GR", "BSCS" },
            _ => Array.Empty<string>()
        };
    }

    private static string NormalizeLogicalViewerSlotId(string? value)
    {
        var raw = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.StartsWith("slot-", StringComparison.Ordinal)) raw = raw[5..];
        return new string(raw.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray()).Trim('-');
    }

    private static string NormalizeProfileGroup(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "BS" or "CS" or "BS/CS" or "BSCS" => "BSCS",
            "地上波" or "GR" or "GROUND" => "GR",
            _ => g
        };
    }

    private static bool TryParseTvTestOrdinal(string value, out int ordinal)
    {
        ordinal = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) && ordinal > 0) return true;
        if (v == "tvtest") { ordinal = 1; return true; }
        const string prefix = "tvtest";
        if (!v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(v[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) && ordinal > 0;
    }
}

file sealed record ChainCandidateEventFrame(
    string Key,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    DateTime Start,
    DateTime End,
    string Title);

file sealed record ChainCandidatePreviewRequest(
    bool LaterProgramPriorityEnabled,
    bool PseudoContinuousRecordingEnabled,
    IReadOnlyList<ChainCandidateEventFrame>? Events);

file sealed record NetworkLoginRequest(string? Password);

sealed class RuntimeUiActionHttpRequest
{
    public string PluginId { get; set; } = string.Empty;
    public string RouteSegment { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ActionToken { get; set; } = string.Empty;
    public string ResponseMode { get; set; } = "json";
    public string WindowId { get; set; } = string.Empty;
    public string RefreshTarget { get; set; } = "content";
    public bool PreserveScroll { get; set; } = true;
}

