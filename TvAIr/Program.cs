/* release_contract epg-progress-panel-theme-role-rebuild-and-light-button-luminance-tuning: no Program.cs behavior changed; WAKE/EPG contracts remain unchanged. */
﻿/* release_contract gr-cdt-data-module-logo-save-bscs-no-deep: Wakeタスク起動時は --wake-task を単一インスタンス合流シグナルとして扱い、既存TvAIrがいる場合は本体二重起動せず signal ファイルを書いて終了する。 */
/* release_contract recording-options-cleanup-worker-launch-policy: 録画オプションUIの説明文を撤去し、TvAIrEpgRec表示ON/OFFの起動ポリシーを共通ヘルパーへ集約。 */
/* release_contract ai-rhythm-core-migration: AI-rhythm正式名・新旧URL/ID互換・旧設定移行を本体側に追加。 */
/* release_contract wake-plan-hash-trigger-limit: limit Wake task rebuild triggers by in-process plan hash and periodic validation. */
/* release_contract wake-task-nochange-skip: skip full Wake task delete/register when the desired plan is unchanged and existing managed tasks match. */
/* release_contract program-guide-reservation-diff-render: reservation state refresh updates existing program cells only, avoiding full guide rerender when EPG is unchanged. */
using System.Globalization;
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
using TvAIr.Plugin;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIrPlugin;
using Microsoft.Win32;

// ─── エンコーディング登録（ARIB文字コード用）───────────────────
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);


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
        var portText = "55884";
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
// 別プロセスで同時に動き、録画開始要求だけ出て TVTest 起動が成立しない危険がある。
// Web常駐coreアプリとして、同一ユーザーセッション内では必ず1プロセスに固定する。
var singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"TvAIr.SingleInstance.v1", createdNew: out var singleInstanceCreated);
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
var enableRouteReplayDebugApi = builder.Configuration.GetValue<bool>("Debug:EnableRouteReplayApi");

// ─── TvAIr.ini 読み込み（appsettings.json より優先） ────────────
var iniSettings = new IniSettingsService(AppContext.BaseDirectory);
builder.Services.AddSingleton(iniSettings);

static string ResolveDataDirectoryPath(string? configuredPath)
{
    var raw = string.IsNullOrWhiteSpace(configuredPath) ? "data" : configuredPath.Trim();
    return Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw));
}

// ─── 設定バインド（ini で上書き） ────────────────────────────────
builder.Services.Configure<AppSettings>(opt =>
{
    var base_ = builder.Configuration.GetSection("App").Get<AppSettings>() ?? new();
    opt.Port          = iniSettings.IsFirstRun ? base_.Port          : iniSettings.Port;
    opt.DataDirectory = iniSettings.IsFirstRun ? base_.DataDirectory : iniSettings.DataDirectory;
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
            };
        })
        .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
        .ToList();
}
// release_contract: 視聴/録画/EPGの隔離はBonDriver名ではなくRoleと論理リソース解決で行う。
// BonDriver未設定行は環境固有fallbackせず、実行候補から外す。
builder.Services.AddSingleton<IReadOnlyList<TunerProfile>>(tunerProfiles.AsReadOnly());

// ─── コアサービス ────────────────────────────────────────────────
builder.Services.AddSingleton<LogRepository>();
builder.Services.AddSingleton<UserEventLogService>();
builder.Services.AddSingleton<Database>(sp =>
{
    var appSettings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    var dataDir = ResolveDataDirectoryPath(appSettings.DataDirectory);
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

// ─── 予約 ────────────────────────────────────────────────────────
builder.Services.AddSingleton<ChainDirectRecorderSessionRegistry>();
builder.Services.AddSingleton<ReservationStore>();
builder.Services.AddSingleton<ReservationAllocationRouteService>();
builder.Services.AddSingleton<ReservationPresentationService>();
builder.Services.AddSingleton<AirhythmProfileService>();
builder.Services.AddSingleton<AirhythmBackupService>();
builder.Services.AddSingleton<AirhythmDashboardService>();
builder.Services.AddSingleton<AirhythmNotificationService>();
builder.Services.AddSingleton<BroadcastClockService>();

// ─── EPG ────────────────────────────────────────────────────────
builder.Services.AddSingleton<EpgStore>();
builder.Services.AddSingleton<ServiceLogoStore>();
builder.Services.AddSingleton<EpgLogoExtractor>();
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
        sp.GetRequiredService<TvTestActivityKeeper>(),
        sp.GetRequiredService<ServiceLogoStore>(),
        sp.GetRequiredService<EpgLogoExtractor>(),
        sp.GetRequiredService<BroadcastClockService>()));
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
        sp.GetRequiredService<EpgStore>(),
        sp.GetRequiredService<TunerPool>(),
        sp.GetRequiredService<LogRepository>(),
        sp.GetRequiredService<UserEventLogService>(),
        sp.GetRequiredService<ReservationAllocationRouteService>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<EpgScheduler>());

// Wakeタスク合流シグナル監視: 既存TvAIrがいる状態で --wake-task 起動された子プロセスが残す signal を拾い、常駐TvAIr側へ処理を合流させる。
builder.Services.AddHostedService<WakeSignalMonitorService>();

// ─── タスクスケジューラーサービス ──────────────────────────────
builder.Services.AddSingleton<TaskSchedulerService>(sp =>
    new TaskSchedulerService(
        sp.GetRequiredService<ReservationStore>(),
        sp.GetRequiredService<IniSettingsService>(),
        sp.GetRequiredService<LogRepository>(),
        sp.GetRequiredService<UserEventLogService>()));

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
        sp.GetRequiredService<EpgStore>(),
        sp.GetRequiredService<EpgCapture>(),
        sp.GetRequiredService<TvTestActivityKeeper>(),
        sp.GetRequiredService<ChainDirectRecorderSessionRegistry>(),
        sp.GetRequiredService<ServiceLogoStore>(),
        sp.GetRequiredService<BroadcastClockService>(),
        sp.GetRequiredService<UserEventLogService>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReservationScheduler>());

// ─── プラグイン ──────────────────────────────────────────────────
// Plugins/ ディレクトリの DLL を起動時に自動ロード。
// プラグイン未配置時は何もしない。例外でも本体を停止させない。
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<PluginActionTokenStore>();
builder.Services.AddSingleton<PluginWindowSessionStore>();
builder.Services.AddSingleton<PluginToolWindowHostService>();
builder.Services.AddSingleton<PluginDefaultMenuActionService>();
builder.Services.AddSingleton<LiveCommentStore>();
builder.Services.AddSingleton<PluginAllowListService>();
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
var port = iniSettings.IsFirstRun
    ? builder.Configuration.GetValue<int>("App:Port", 55884)
    : iniSettings.Port;
// バインドアドレス: 0.0.0.0（全インターフェース）でLANの他端末からもアクセス可能。
// Kestrel直接バインドのため管理者権限・URL予約は不要。
// 初回起動時にWindowsファイアウォールの受信許可ダイアログが出る場合あり。
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();
long settingsThemeRuntimeRevision = 0;

if (wakeInvocation.IsWakeTask)
{
    try
    {
        app.Services.GetRequiredService<LogRepository>().Add("WAKE_TASK_INVOCATION", "PRIMARY",
            $"result=PRIMARY_INSTANCE kind={wakeInvocation.Kind} at={wakeInvocation.At} pid={Environment.ProcessId} action=continue_startup_sync rule=release_contract");
    }
    catch { }
}

MigrateAirrhythmLocalSettings(app.Services.GetRequiredService<LogRepository>());

// TvAIr release_contract cache guard:
// UI差分更新高速化を維持しつつ、ブラウザが旧index.html/旧JS状態を掴んで
// チェーン候補判定だけ遅れて復帰する問題を避ける。
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";

    await next();

    var path = context.Request.Path.Value ?? string.Empty;
    if (path.Equals("/", StringComparison.OrdinalIgnoreCase)
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
TvTestRecordingOptionsInspector.Initialize(effectiveTvTestSettings.ExecutablePath, lifecycleLog);
lifecycleLog.Add("APP_LIFECYCLE", "START",
     $"TvAIr start version={GetTvAIrAppVersion()} baseDir={AppContext.BaseDirectory}");
EmitTvAIrRuntimeIdentityAudit(lifecycleLog);
EmitTvAIrEpgRecRuntimePrerequisiteAudit(lifecycleLog, effectiveTvTestSettings);
var startupTvTestSnapshot = TvTestProcessAuditor.Capture(lifecycleLog, "APP_START", emitLegacyEvents: true);
// release_contract: External TVTest occupancy is checked lightly at viewerStart time only; do not persist it in TunerPool at startup.
RunTvAIrEpgRecStartupOrphanSafety(lifecycleLog);
app.Lifetime.ApplicationStopping.Register(() =>
{
    lifecycleLog.Add("APP_LIFECYCLE", "STOPPING", "TvAIr stopping begin");
    TvTestProcessAuditor.EmitSnapshot(lifecycleLog, "APP_STOPPING");
});
app.Lifetime.ApplicationStopped.Register(() =>
{
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

// プラグイン静的ファイル配信。
// Plugins/{PluginId}/wwwroot 配下のファイルを /plugin-assets/{PluginId}/... で公開する。
// 開発中は自由度を優先し、正式版でManifest権限・ハッシュ許可制と連動させる。
var pluginRootForAssets = Path.Combine(AppContext.BaseDirectory, "Plugins");
Directory.CreateDirectory(pluginRootForAssets);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(pluginRootForAssets),
    RequestPath = "/plugin-assets",
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

// release_contract: プラグイン同梱小型画像等の正式asset URL契約。
// Plugins/{route}/Assets または Plugins/{route}/wwwroot/assets 配下のPNGのみを、同一オリジンURLで返す。
// UIの小型アイコン用途に限定し、file://・外部URL・data URI依存を避ける。
app.MapGet("/plugin-assets/{routeSegment}/{assetName}", (string routeSegment, string assetName, HttpRequest http, PluginRegistry registry, LogRepository log) =>
    ResolvePluginAssetResult(routeSegment, assetName, registry, log, "route"));
app.MapGet("/api/plugins/{pluginId}/assets/{assetName}", (string pluginId, string assetName, HttpRequest http, PluginRegistry registry, LogRepository log) =>
    ResolvePluginAssetResult(pluginId, assetName, registry, log, "pluginId"));

// ─── プラグインUI/API ──────────────────────────────────────────
// TvAIr 1.0.0: AI-rhythm等が本体非依存で作り始められるよう、
// UIルート・Manifest・権限宣言・Context API導線を本体側の正式入口として用意する。
app.MapGet("/api/plugins", (PluginRegistry registry) =>
{
    var plugins = registry.GetAll()
        .Select(p => new
        {
            name = p.Name,
            version = p.Version,
            kinds = new[]
            {
                p is IUiPlugin ? "ui" : null,
                p is IAnalysisPlugin ? "analysis" : null,
                p is IViewerPlugin ? "viewer" : null,
                p is IManifestPlugin ? "manifest" : null
            }.Where(x => x is not null),
            manifest = p is IManifestPlugin mp ? mp.Manifest : null
        })
        .ToList();
    return Results.Ok(new { plugins });
});

app.MapGet("/api/plugins/ui", (PluginRegistry registry) =>
{
    var uiPlugins = registry.GetUiPlugins()
        .Where(p => p.Ui.Enabled)
        .Select(p =>
        {
            var publicRoute = NormalizeAirhythmPublicRoute(p.Ui.RouteSegment);
            var displayName = NormalizeAirhythmDisplayName(string.IsNullOrWhiteSpace(p.Ui.MenuText) ? p.Name : p.Ui.MenuText);
            return new
            {
                pluginName = NormalizeAirhythmPluginName(p.Name),
                name = NormalizeAirhythmPluginName(p.Name),
                version = p.Version,
                enabled = p.Ui.Enabled,
                route = publicRoute,
                menuText = displayName,
                description = NormalizeAirhythmDisplayName(p.Ui.Description),
                icon = p.Ui.Icon,
                displayOrder = p.Ui.DisplayOrder,
                url = $"/plugin/{publicRoute}",
                legacyUrl = IsAirhythmRouteCandidate(publicRoute) ? "/plugin/airithm" : $"/plugin-ui/{publicRoute}",
                manifest = p is IManifestPlugin mp ? mp.Manifest : null
            };
        });

    var manifestOnlyPlugins = registry.GetManifestPlugins()
        .Where(p => !string.IsNullOrWhiteSpace(p.Manifest.Route))
        .Where(p => !registry.GetUiPlugins().Any(u => string.Equals(u.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
        .Select(p =>
        {
            var route = p.Manifest.Route.Trim();
            if (route.StartsWith("/plugin/", StringComparison.OrdinalIgnoreCase)) route = route[8..];
            route = NormalizeAirhythmPublicRoute(route.Trim('/'));
            var menuText = NormalizeAirhythmDisplayName(string.IsNullOrWhiteSpace(p.Manifest.Name) ? p.Name : p.Manifest.Name);
            return new
            {
                pluginName = NormalizeAirhythmPluginName(p.Name),
                name = NormalizeAirhythmPluginName(p.Name),
                version = p.Version,
                enabled = true,
                route,
                menuText,
                description = NormalizeAirhythmDisplayName(p.Manifest.Description),
                icon = string.Empty,
                displayOrder = 1000,
                url = $"/plugin/{route}",
                legacyUrl = IsAirhythmRouteCandidate(route) ? "/plugin/airithm" : $"/plugin-ui/{route}",
                manifest = (PluginManifest?)p.Manifest
            };
        });

    var plugins = uiPlugins
        .Concat(manifestOnlyPlugins)
        .OrderBy(p => p.displayOrder)
        .ThenBy(p => p.menuText)
        .ToList();
    return Results.Ok(new { plugins });
});

app.MapGet("/api/plugins/manifests", (PluginRegistry registry) =>
{
    var manifests = registry.GetAll()
        .OfType<IManifestPlugin>()
        .Select(p => p.Manifest)
        .ToList();
    return Results.Ok(new { manifests });
});

app.MapGet("/api/plugins/menu-actions", (PluginDefaultMenuActionService menuActions) =>
{
    var actions = menuActions.ResolveActions("api");
    return Results.Ok(new { actions, contract = PluginDefaultMenuActionService.ContractVersion, projection = "menu_model_hamburger_context_page", compatAliasIsAdapterOnly = true });
});

app.MapGet("/plugin-menu/{routeSegment}", (string routeSegment, string? source, HttpRequest http, PluginRegistry registry, PluginDefaultMenuActionService menuActions, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, LogRepository log) =>
    DispatchPluginDefaultMenuAction(routeSegment, string.IsNullOrWhiteSpace(source) ? "hamburger" : source!, http, registry, menuActions, windows, toolWindows, log));
app.MapGet("/plugin-menu-info/{routeSegment}", (string routeSegment, PluginRegistry registry, PluginDefaultMenuActionService menuActions, LogRepository log) =>
    RenderPluginDefaultMenuInfoByRoute(routeSegment, registry, menuActions, log));


app.MapGet("/api/plugins/nicojk/live-comments", (string? reservationId, int? count, LiveCommentStore store) =>
{
    store.PruneOlderThan(TimeSpan.FromHours(6));
    var limit = Math.Clamp(count.GetValueOrDefault(100), 1, 300);
    var comments = store.GetRecent(reservationId, limit);
    return Results.Ok(new
    {
        ok = true,
        reservationId = string.IsNullOrWhiteSpace(reservationId) ? null : reservationId,
        count = comments.Count,
        comments
    });
});

app.MapGet("/api/plugins/nicojk/live-comments/groups", (int? count, LiveCommentStore store) =>
{
    store.PruneOlderThan(TimeSpan.FromHours(6));
    var limit = Math.Clamp(count.GetValueOrDefault(50), 1, 300);
    return Results.Ok(new
    {
        ok = true,
        groups = store.GetGroups(limit)
    });
});


static bool IsAirhythmRouteCandidate(string? value)
{
    var v = (value ?? string.Empty).Trim().Trim('/').ToLowerInvariant();
    if (v.StartsWith("plugin/")) v = v[7..];
    if (v.StartsWith("plugin-ui/")) v = v[10..];
    return v.Equals("airhythm", StringComparison.OrdinalIgnoreCase)
        || v.Equals("airithm", StringComparison.OrdinalIgnoreCase) // legacy alias
        || v.Equals("ai-rhythm", StringComparison.OrdinalIgnoreCase)
        || v.Equals("ai-rithm", StringComparison.OrdinalIgnoreCase) // legacy alias
        || v.Contains("airhythm", StringComparison.OrdinalIgnoreCase)
        || v.Contains("airithm", StringComparison.OrdinalIgnoreCase) // legacy alias
        || v.Contains("ai-rhythm", StringComparison.OrdinalIgnoreCase)
        || v.Contains("ai-rithm", StringComparison.OrdinalIgnoreCase); // legacy alias
}

static string NormalizeAirhythmPublicRoute(string? value)
    => IsAirhythmRouteCandidate(value) ? "airhythm" : (value ?? string.Empty).Trim().Trim('/');

static string NormalizeAirhythmPluginName(string? value)
{
    var v = value ?? string.Empty;
    if (v.Contains("AIrithm", StringComparison.OrdinalIgnoreCase)
        || v.Contains("AI-rithm", StringComparison.OrdinalIgnoreCase)
        || v.Contains("airithm", StringComparison.OrdinalIgnoreCase)
        || v.Contains("AIrhythm", StringComparison.OrdinalIgnoreCase)
        || v.Contains("AI-rhythm", StringComparison.OrdinalIgnoreCase)
        || v.Contains("airhythm", StringComparison.OrdinalIgnoreCase))
    {
        return v.Replace("AIrithm", "AIrhythm", StringComparison.OrdinalIgnoreCase)
                .Replace("AI-rithm", "AI-rhythm", StringComparison.OrdinalIgnoreCase)
                .Replace("airithm", "airhythm", StringComparison.OrdinalIgnoreCase);
    }
    return v;
}

static string NormalizeAirhythmDisplayName(string? value)
{
    var v = NormalizeAirhythmPluginName(value);
    if (v.Contains("AIrhythm", StringComparison.OrdinalIgnoreCase))
    {
        v = v.Replace("AIrhythm", "AI-rhythm", StringComparison.OrdinalIgnoreCase);
    }
    return v;
}

static void MigrateAirrhythmLocalSettings(LogRepository log)
{
    try
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return;

        var oldDir = Path.Combine(local, "AIrithm.BasicPlugin"); // legacy alias
        var newDir = Path.Combine(local, "AIrhythm.BasicPlugin");
        var oldFile = Path.Combine(oldDir, "airithm-settings.json"); // legacy alias
        var newFile = Path.Combine(newDir, "airhythm-settings.json");

        if (!File.Exists(oldFile) || File.Exists(newFile)) return;

        Directory.CreateDirectory(newDir);
        File.Copy(oldFile, newFile, overwrite: false);
        log.Add("AI_RHYTHM_SETTINGS_MIGRATED", "OK", "old=AIrithm.BasicPlugin/airithm-settings.json new=AIrhythm.BasicPlugin/airhythm-settings.json action=copy_only_keep_legacy rule=release_contract");
    }
    catch (Exception ex)
    {
        log.Add("AI_RHYTHM_SETTINGS_MIGRATED", "WARN", $"result=FAILED message={ex.Message} rule=release_contract");
    }
}

app.MapPost("/api/plugins/navigation/open", async (HttpContext context, AirhythmNotificationService notificationService, LogRepository log) =>
{
    string route = string.Empty;
    string url = string.Empty;
    string referrerPath = string.Empty;

    try
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;
        if (root.TryGetProperty("route", out var routeElement)) route = routeElement.GetString() ?? string.Empty;
        if (root.TryGetProperty("url", out var urlElement)) url = urlElement.GetString() ?? string.Empty;
        if (root.TryGetProperty("referrerPath", out var referrerElement)) referrerPath = referrerElement.GetString() ?? string.Empty;
    }
    catch
    {
        // 互換性優先。壊れたリクエストでも遷移自体は止めない。
    }

    static string NormalizePluginRoute(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.StartsWith("/plugin-ui/", StringComparison.OrdinalIgnoreCase)) value = value[11..];
        if (value.StartsWith("/plugin/", StringComparison.OrdinalIgnoreCase)) value = value[8..];
        return value.Trim('/');
    }

    route = NormalizePluginRoute(string.IsNullOrWhiteSpace(route) ? url : route);
    var isAirhythm = IsAirhythmRouteCandidate(route);
    if (isAirhythm) route = "airhythm";

    if (isAirhythm)
    {
        notificationService.MarkOpened();
    }

    log.Add("PLUGIN_NAVIGATION", "Open", $"route={route} airhythmRead={isAirhythm} from={referrerPath}");
    return Results.Ok(new { ok = true, route, airhythmRead = isAirhythm });
});

app.MapPost("/api/plugins/action", HandlePluginActionDispatchAsync);
app.MapPost("/plugin-action", HandlePluginActionDispatchAsync);
app.MapPost("/api/plugins/window", HandlePluginWindowDispatchAsync);
app.MapPost("/plugin-window", HandlePluginWindowDispatchAsync);
app.MapGet("/api/plugins/window/capabilities", (PluginToolWindowHostService toolWindows, LogRepository log) => RenderPluginWindowHostCapabilities(toolWindows, log));
app.MapGet("/plugin-window/capabilities", (PluginToolWindowHostService toolWindows, LogRepository log) => RenderPluginWindowHostCapabilities(toolWindows, log));
app.MapGet("/api/plugins/viewer-tuners", (TunerPool tuners, ExternalTunerLeaseService externalTuners, LogRepository log) => RenderPluginViewerTuners(tuners, externalTuners, log));
app.MapGet("/api/plugins/viewer-profiles", (IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, LogRepository log) => RenderPluginViewerProfiles(tvTestOptions, ini, tunerProfiles, log));
app.MapGet("/api/plugins/viewer-control/channels", (ChannelFileLoader channels, LogRepository log) => RenderPluginViewerControlChannels(channels, log));
app.MapGet("/api/plugins/program-guide/wave-filters", (LogRepository log) => RenderPluginProgramGuideWaveFilters(log));
app.MapGet("/api/plugins/viewer-control/contract", (LogRepository log) => RenderPluginViewerControlContract(log));
app.MapGet("/api/plugins/host-contract", (LogRepository log) => RenderPluginHostContract(log));
app.MapGet("/api/plugins/safe-event/client-log", (HttpRequest http, LogRepository log) => RenderPluginSafeEventClientLog(http, log));
app.MapGet("/api/plugins/safe-event/keepalive", (HttpRequest http, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, LogRepository log) => RenderPluginSafeEventKeepAlive(http, actionTokens, windows, log));
app.MapGet("/tvair-safe-event-host.js", (HttpRequest http, LogRepository log) => RenderPluginSafeEventHostScript(http, log));
app.MapGet("/plugin-window/{windowId}/state", (string windowId, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, LogRepository log) => RenderPluginWindowState(windowId, windows, toolWindows, log));
app.MapGet("/plugin-window/{windowId}", (string windowId, HttpRequest http, PluginWindowSessionStore windows, LogRepository log) => RenderPluginWindowHost(windowId, http, windows, log));

static void ApplyManifestToolWindowSizeContract(PluginWindowRequest request, ITvAIrPlugin? plugin, LogRepository? log = null, string source = "", string entryKind = "")
{
    if (request is null || plugin is null) return;
    request.Payload ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    PluginManifest? manifest = plugin is IManifestPlugin mp ? mp.Manifest : null;
    PluginUiDescriptor? ui = plugin is IUiPlugin up ? up.Ui : null;

    var manifestWidth = manifest is not null && manifest.ToolWindowWidth > 0 ? Math.Clamp(manifest.ToolWindowWidth, 240, 2400) : 0;
    var manifestHeight = manifest is not null && manifest.ToolWindowHeight > 0 ? Math.Clamp(manifest.ToolWindowHeight, 240, 1600) : 0;
    var manifestMinWidth = manifest is not null && manifest.ToolWindowMinWidth > 0 ? Math.Clamp(manifest.ToolWindowMinWidth, 160, 2400) : 0;
    var manifestMinHeight = manifest is not null && manifest.ToolWindowMinHeight > 0 ? Math.Clamp(manifest.ToolWindowMinHeight, 160, 1600) : 0;

    var uiWidth = ui is not null && ui.ToolWindowWidth > 0 ? Math.Clamp(ui.ToolWindowWidth, 240, 2400) : 0;
    var uiHeight = ui is not null && ui.ToolWindowHeight > 0 ? Math.Clamp(ui.ToolWindowHeight, 240, 1600) : 0;
    var uiMinWidth = ui is not null && ui.ToolWindowMinWidth > 0 ? Math.Clamp(ui.ToolWindowMinWidth, 160, 2400) : 0;
    var uiMinHeight = ui is not null && ui.ToolWindowMinHeight > 0 ? Math.Clamp(ui.ToolWindowMinHeight, 160, 1600) : 0;

    var contractWidth = Math.Max(manifestWidth, uiWidth);
    var contractHeight = Math.Max(manifestHeight, uiHeight);
    var explicitContractMinWidth = Math.Max(manifestMinWidth, uiMinWidth);
    var explicitContractMinHeight = Math.Max(manifestMinHeight, uiMinHeight);

    // release_contract: Treat a declared tool-window contract size as the generic lower bound when
    // no explicit min-size is exported by older plugins/descriptors. This is not plugin-name
    // specific: hamburger, tray, openWindow, existing-window reuse, and saved-state restore all
    // consume the same resolved request.MinWidth/MinHeight below.
    var contractMinWidth = explicitContractMinWidth > 0 ? explicitContractMinWidth : contractWidth;
    var contractMinHeight = explicitContractMinHeight > 0 ? explicitContractMinHeight : contractHeight;

    var oldWidth = request.Width;
    var oldHeight = request.Height;
    var oldMinWidth = request.MinWidth;
    var oldMinHeight = request.MinHeight;

    if (contractMinWidth > 0)
    {
        request.MinWidth = Math.Max(request.MinWidth, contractMinWidth);
        request.Payload["minWidth"] = request.MinWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    if (contractMinHeight > 0)
    {
        request.MinHeight = Math.Max(request.MinHeight, contractMinHeight);
        request.Payload["minHeight"] = request.MinHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    if (!HasPluginWindowPayload(request, "width", "Width") && contractWidth > 0)
    {
        request.Width = Math.Max(contractWidth, request.MinWidth);
        request.Payload["width"] = request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (request.Width > 0)
    {
        request.Width = Math.Max(request.Width, request.MinWidth);
    }
    if (!HasPluginWindowPayload(request, "height", "Height") && contractHeight > 0)
    {
        request.Height = Math.Max(contractHeight, request.MinHeight);
        request.Payload["height"] = request.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (request.Height > 0)
    {
        request.Height = Math.Max(request.Height, request.MinHeight);
    }

    if (log is not null && (request.Width != oldWidth || request.Height != oldHeight || request.MinWidth != oldMinWidth || request.MinHeight != oldMinHeight))
    {
        log.Add("PLUGIN_TOOL_WINDOW_CONTRACT_RESOLVE", plugin.Name, $"result=APPLIED source={SafePluginActionValue(source)} entryKind={SafePluginActionValue(entryKind)} manifestSize={manifestWidth}x{manifestHeight} manifestMinSize={manifestMinWidth}x{manifestMinHeight} uiSize={uiWidth}x{uiHeight} uiMinSize={uiMinWidth}x{uiMinHeight} oldSize={oldWidth}x{oldHeight} oldMinSize={oldMinWidth}x{oldMinHeight} newSize={request.Width}x{request.Height} newMinSize={request.MinWidth}x{request.MinHeight} rule=release_contract");
    }
}

static string ResolvePluginToolWindowTitle(ITvAIrPlugin plugin, string fallbackTitle)
{
    static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    var uiTitle = plugin is IUiPlugin up ? Clean(up.Ui.ToolWindowTitle) : string.Empty;
    if (!string.IsNullOrWhiteSpace(uiTitle)) return uiTitle;

    var manifestTitle = plugin is IManifestPlugin mp ? Clean(mp.Manifest.ToolWindowTitle) : string.Empty;
    if (!string.IsNullOrWhiteSpace(manifestTitle)) return manifestTitle;

    var fallback = Clean(fallbackTitle);
    return string.IsNullOrWhiteSpace(fallback) ? plugin.Name : fallback;
}

static PluginWindowRequest ResolvePluginToolWindowContract(ITvAIrPlugin plugin, string pluginActionId, string route, PluginWindowRequest request, string source, string entryKind, LogRepository log)
{
    route = (route ?? string.Empty).Trim().Trim('/');
    request.Action = string.IsNullOrWhiteSpace(request.Action) ? "openWindow" : request.Action.Trim();
    request.PluginId = string.IsNullOrWhiteSpace(request.PluginId) ? pluginActionId : request.PluginId.Trim();
    request.RouteSegment = string.IsNullOrWhiteSpace(request.RouteSegment) ? route : request.RouteSegment.Trim().Trim('/');
    request.Title = ResolvePluginToolWindowTitle(plugin, string.IsNullOrWhiteSpace(request.Title) ? plugin.Name : request.Title.Trim());
    request.ContentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? $"/plugin/{Uri.EscapeDataString(route)}" : request.ContentRoute.Trim();
    request.ReuseExisting = true;
    request.ActivateExisting = true;
    request.ResponseMode = string.IsNullOrWhiteSpace(request.ResponseMode) ? "hostHandled" : request.ResponseMode.Trim();
    request.Payload ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    request.Payload["source"] = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    request.Payload["entryKind"] = string.IsNullOrWhiteSpace(entryKind) ? "unknown" : entryKind;
    request.Payload["unifiedToolWindowEntry"] = "true";
    ApplyManifestToolWindowSizeContract(request, plugin, log, source, entryKind);
    request.MinWidth = Math.Clamp(request.MinWidth <= 0 ? 320 : request.MinWidth, 160, 2400);
    request.MinHeight = Math.Clamp(request.MinHeight <= 0 ? 240 : request.MinHeight, 160, 1600);
    request.Width = Math.Max(request.MinWidth, Math.Clamp(request.Width <= 0 ? 620 : request.Width, 240, 2400));
    request.Height = Math.Max(request.MinHeight, Math.Clamp(request.Height <= 0 ? 760 : request.Height, 240, 1600));
    log.Add("PLUGIN_TOOL_WINDOW_ENTRY", plugin.Name, $"result=RESOLVED source={SafePluginActionValue(source)} entryKind={SafePluginActionValue(entryKind)} pluginId={SafePluginActionValue(pluginActionId)} routeSegment={SafePluginActionValue(route)} requestRoute={SafePluginActionValue(request.RouteSegment)} size={request.Width}x{request.Height} minSize={request.MinWidth}x{request.MinHeight} contentRoute={SafePluginActionValue(request.ContentRoute)} reuseExisting={request.ReuseExisting} activateExisting={request.ActivateExisting} rule=release_contract");
    return request;
}



static (PluginWindowSession Session, PluginToolWindowOpenResult HostResult, string WindowUrl, string ContentRoute, string AbsoluteUrl, PluginToolWindowIconSpec IconSpec, bool ReusedSession) OpenOrActivatePluginToolWindowUnified(
    ITvAIrPlugin plugin,
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
    request = ResolvePluginToolWindowContract(plugin, pluginActionId, route, request, source, entryKind, log);
    var session = windows.OpenOrReuse(plugin.Name, pluginActionId, route, request, reuseExisting: true, out var reusedWindowSession);
    var windowUrl = $"/plugin-window/{Uri.EscapeDataString(session.WindowId)}";
    var contentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
    var hostCaps = toolWindows.GetCapabilities();
    var navigationUrl = BuildToolWindowNavigationUrl(windowUrl, contentRoute, hostCaps);
    var absoluteWindowUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
    var iconSpec = ResolvePluginToolWindowIcon(plugin, route, pluginActionId, log);
    var hostResult = toolWindows.OpenOrActivate(session, absoluteWindowUrl, iconSpec);
    log.Add("PLUGIN_TOOL_WINDOW_ENTRY", plugin.Name, $"result=OPEN_OR_ACTIVATE source={SafePluginActionValue(source)} entryKind={SafePluginActionValue(entryKind)} windowId={SafePluginActionValue(session.WindowId)} reusedSession={reusedWindowSession} hostResult={SafePluginActionValue(hostResult.Result)} hostReused={hostResult.Reused} activated={hostResult.Activated} hostKind={SafePluginActionValue(hostResult.HostKind)} size={session.Width}x{session.Height} minSize={session.MinWidth}x{session.MinHeight} contentRoute={SafePluginActionValue(contentRoute)} rule=release_contract");
    return (session, hostResult, windowUrl, contentRoute, absoluteWindowUrl, iconSpec, reusedWindowSession);
}

static async Task<IResult> HandlePluginWindowDispatchAsync(HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, LogRepository log)
{
    var request = await ReadPluginWindowRequestAsync(http);
    NormalizePluginWindowRequestFromPayload(request);
    var pluginId = (request.PluginId ?? string.Empty).Trim();
    var route = !string.IsNullOrWhiteSpace(request.RouteSegment)
        ? request.RouteSegment.Trim()
        : ReadPayload(request.Payload, "Route", "route", "RouteSegment", "routeSegment");
    var action = string.IsNullOrWhiteSpace(request.Action) ? "openWindow" : request.Action.Trim();
    var responseMode = NormalizePluginFormResponseMode(!string.IsNullOrWhiteSpace(request.ResponseMode) ? request.ResponseMode : ReadPayload(request.Payload, "responseMode", "ResponseMode"));
    responseMode = NormalizePluginWindowActionResponseMode(http, request, action, responseMode);
    var plugin = FindPluginByActionIdentity(registry, pluginId, route);
    var pluginName = plugin?.Name ?? pluginId;

    if (plugin is null)
    {
        log.Add("PLUGIN_WINDOW", "DENY", $"plugin={SafePluginActionValue(pluginId)} action={SafePluginActionValue(action)} result=DENIED reason=plugin_not_found endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin not found.", Diagnostics = "plugin_not_found" }, responseMode, StatusCodes.Status404NotFound);
    }

    var hasUiPermission = plugin is IManifestPlugin mp && mp.Manifest.Permissions.Contains(PluginPermission.ShowUi);
    if (!hasUiPermission)
    {
        log.Add("PLUGIN_WINDOW", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason=missing_ShowUi_permission endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "ShowUi permission is required.", Diagnostics = "missing_ShowUi_permission" }, responseMode, StatusCodes.Status403Forbidden);
    }

    ApplyManifestToolWindowSizeContract(request, plugin);

    var token = !string.IsNullOrWhiteSpace(request.WindowToken) ? request.WindowToken : request.Token;
    var pluginActionIdForWindowToken = GetPluginActionIdentity(plugin, route);
    var requestedWindowIdForToken = NormalizePluginWindowId(!string.IsNullOrWhiteSpace(request.WindowId) ? request.WindowId : ReadPayload(request.Payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId"));
    if (!ValidatePluginActionTokenOrRecoverHostWindow(actionTokens, windows, token, pluginActionIdForWindowToken, route, pluginName, action, requestedWindowIdForToken, null, http.Path.Value ?? string.Empty, "window_dispatch", log, out var tokenReason))
    {
        log.Add("PLUGIN_WINDOW", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason={tokenReason} windowId={SafePluginActionValue(requestedWindowIdForToken)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Invalid plugin window token.", Diagnostics = tokenReason }, responseMode, StatusCodes.Status400BadRequest);
    }

    if (action.Equals("openWindow", StringComparison.OrdinalIgnoreCase) || action.Equals("open", StringComparison.OrdinalIgnoreCase))
    {
        var pluginActionId = GetPluginActionIdentity(plugin, route);
        var hostOpenMode = IsPluginWindowHostOpenMode(responseMode);
        var defaultReuseExisting = hostOpenMode;
        var reuseExisting = request.ReuseExisting || defaultReuseExisting;
        var activateExisting = request.ActivateExisting || hostOpenMode;
        var unifiedOpen = OpenOrActivatePluginToolWindowUnified(plugin, pluginActionId, route, request, "openWindow", "api_or_plugin_window", http, windows, toolWindows, log);
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
            RefreshScrollTarget = request.RefreshScrollTarget,
            RefreshScrollMode = request.RefreshScrollMode,
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
        var closedSession = windows.DeleteClosed(request.WindowId, GetPluginActionIdentity(plugin, route));
        var ok = closedSession is not null;
        var toolWindowClosed = toolWindows.Close(request.WindowId);
        log.Add("PLUGIN_WINDOW", pluginName, $"action=closeWindow result={(ok ? "OK" : "NOT_FOUND")} windowId={SafePluginActionValue(request.WindowId)} toolWindowCloseRequested={toolWindowClosed} responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return ok
            ? BuildPluginWindowDispatchResponse(new PluginWindowResult { Success = true, Message = "Plugin window closed.", Diagnostics = "closed", WindowId = request.WindowId }, responseMode, ResolvePluginToolWindowReturnUrl(http, request, route))
            : BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin window not found.", Diagnostics = "window_not_found", WindowId = request.WindowId }, responseMode, StatusCodes.Status404NotFound);
    }

    if (action.Equals("updateWindow", StringComparison.OrdinalIgnoreCase) || action.Equals("update", StringComparison.OrdinalIgnoreCase))
    {
        var requestedAlwaysOnTopPresent = HasPluginWindowPayload(request, "alwaysOnTop", "AlwaysOnTop");
        var refreshAfter = request.RefreshAfter || (TryReadBoolPayload(request.Payload, out var refreshAfterValue, "refreshAfter", "RefreshAfter", "refresh", "Refresh") && refreshAfterValue);
        var refreshTarget = NormalizePluginWindowRefreshTarget(!string.IsNullOrWhiteSpace(request.Target) ? request.Target : request.RefreshTarget);
        request.RefreshTarget = refreshTarget;
        request.Target = refreshTarget;

        var session = windows.Update(request.WindowId, GetPluginActionIdentity(plugin, route), request);
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
            var refreshed = windows.Refresh(session!.WindowId, GetPluginActionIdentity(plugin, route), request);
            if (refreshed is not null)
            {
                session = refreshed;
                responseContentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
                var hostCaps = toolWindows.GetCapabilities();
                var navigationUrl = BuildToolWindowNavigationUrl($"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", responseContentRoute, hostCaps);
                var absoluteNavigationUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
                var hostResult = toolWindows.OpenOrActivate(session, absoluteNavigationUrl);
                hostRefreshResult = hostResult.Result;
                refreshIssued = true;
                if (!string.IsNullOrWhiteSpace(request.RefreshScrollTarget))
                    log.Add("PLUGIN_WINDOW_REFRESH_SCROLL", pluginName, $"result=REQUESTED action=updateWindow windowId={SafePluginActionValue(session.WindowId)} target={SafePluginActionValue(request.RefreshScrollTarget)} mode={SafePluginActionValue(request.RefreshScrollMode)} hostKind={SafePluginActionValue(hostResult.HostKind)} refreshTarget={SafePluginActionValue(refreshTarget)} reason=refresh_after_host_content_rerender rule=release_contract");
            }
            else
            {
                hostRefreshResult = "REFRESH_NOT_FOUND";
            }
        }

        log.Add("PLUGIN_WINDOW", pluginName, $"action=updateWindow result={(ok ? "OK" : "NOT_FOUND")} windowId={SafePluginActionValue(request.WindowId)} revision={session?.Revision ?? 0} payloadAlwaysOnTopPresent={requestedAlwaysOnTopPresent} payloadAlwaysOnTop={request.AlwaysOnTop} sessionAlwaysOnTop={session?.AlwaysOnTop.ToString() ?? "-"} hostUpdated={hostApply.HostAccepted} hostApplied={hostApply.Applied} hostBeforeTopMost={hostApply.BeforeTopMost?.ToString() ?? "-"} hostAfterTopMost={hostApply.AfterTopMost?.ToString() ?? "-"} hostBeforeSize={FormatPluginHostSize(hostApply.BeforeWidth, hostApply.BeforeHeight)} hostAfterSize={FormatPluginHostSize(hostApply.AfterWidth, hostApply.AfterHeight)} hostDiagnostics={SafePluginActionValue(hostApply.Diagnostics)} refreshAfter={refreshAfter} refreshTarget={SafePluginActionValue(refreshTarget)} refreshScrollTarget={SafePluginActionValue(request.RefreshScrollTarget)} refreshScrollMode={SafePluginActionValue(request.RefreshScrollMode)} refreshIssued={refreshIssued} hostRefresh={SafePluginActionValue(hostRefreshResult)} responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return ok
            ? BuildPluginWindowDispatchResponse(new PluginWindowResult { Success = true, Message = "Plugin window updated.", Diagnostics = (hostApply.Applied ? "updated;hostApplied" : $"updated;{hostApply.Diagnostics}") + (refreshIssued ? ";refreshIssued" : string.Empty), WindowId = session!.WindowId, WindowUrl = $"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", ContentRoute = responseContentRoute ?? BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision), RefreshRequested = refreshIssued, RefreshTarget = refreshTarget, PreserveScroll = request.PreserveScroll, RefreshScrollTarget = request.RefreshScrollTarget, RefreshScrollMode = request.RefreshScrollMode, Revision = session.Revision }, responseMode, responseContentRoute ?? BuildHostManagedPluginContentRoute(session!.ContentRoute, session.WindowId, session.Revision))
            : BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin window not found.", Diagnostics = "window_not_found", WindowId = request.WindowId }, responseMode, StatusCodes.Status404NotFound);
    }

    if (action.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase)
        || action.Equals("rerenderWindow", StringComparison.OrdinalIgnoreCase)
        || action.Equals("refresh", StringComparison.OrdinalIgnoreCase)
        || action.Equals("rerender", StringComparison.OrdinalIgnoreCase))
    {
        var refreshTarget = NormalizePluginWindowRefreshTarget(!string.IsNullOrWhiteSpace(request.Target) ? request.Target : request.RefreshTarget);
        request.RefreshTarget = refreshTarget;
        request.Target = refreshTarget;
        var resolvedWindowId = ResolvePluginWindowId(request);
        var session = windows.Refresh(resolvedWindowId, GetPluginActionIdentity(plugin, route), request);
        var ok = session is not null;
        var hostRefreshResult = "-";
        string? refreshedContentRoute = null;
        if (ok)
        {
            refreshedContentRoute = BuildHostManagedPluginContentRoute(session!.ContentRoute, session.WindowId, session.Revision);
            var hostCaps = toolWindows.GetCapabilities();
            var navigationUrl = BuildToolWindowNavigationUrl($"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", refreshedContentRoute, hostCaps);
            var absoluteNavigationUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
            var hostResult = toolWindows.OpenOrActivate(session, absoluteNavigationUrl);
            hostRefreshResult = hostResult.Result;
            if (!string.IsNullOrWhiteSpace(request.RefreshScrollTarget))
                log.Add("PLUGIN_WINDOW_REFRESH_SCROLL", pluginName, $"result=REQUESTED action=refreshWindow windowId={SafePluginActionValue(session.WindowId)} target={SafePluginActionValue(request.RefreshScrollTarget)} mode={SafePluginActionValue(request.RefreshScrollMode)} hostKind={SafePluginActionValue(hostResult.HostKind)} refreshTarget={SafePluginActionValue(refreshTarget)} reason=refresh_window_host_content_rerender rule=release_contract");
        }
        log.Add("PLUGIN_WINDOW", pluginName, $"action=refreshWindow result={(ok ? "ISSUED" : "NOT_FOUND")} windowId={SafePluginActionValue(resolvedWindowId)} target={SafePluginActionValue(refreshTarget)} preserveScroll={request.PreserveScroll} refreshScrollTarget={SafePluginActionValue(request.RefreshScrollTarget)} refreshScrollMode={SafePluginActionValue(request.RefreshScrollMode)} revision={session?.Revision ?? 0} contentRoute={SafePluginActionValue(session?.ContentRoute)} hostRefresh={SafePluginActionValue(hostRefreshResult)} responseMode={SafePluginActionValue(responseMode)} endpoint={SafePluginActionValue(http.Path.Value)} reloadScope=toolwindow-content-document_or_iframe-content-only rule=release_contract");
        if (ok)
        {
            var result = new PluginWindowResult { Success = true, Message = "Plugin window refresh requested.", Diagnostics = "refresh_requested", WindowId = session!.WindowId, WindowUrl = $"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", ContentRoute = refreshedContentRoute ?? BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision), RefreshRequested = true, RefreshTarget = refreshTarget, PreserveScroll = request.PreserveScroll, RefreshScrollTarget = request.RefreshScrollTarget, RefreshScrollMode = request.RefreshScrollMode, Revision = session.Revision };
            return BuildPluginWindowDispatchResponse(result, responseMode, result.ContentRoute);
        }
        return BuildPluginWindowDispatchError(new PluginWindowResult { Success = false, Message = "Plugin window not found.", Diagnostics = "window_not_found", WindowId = resolvedWindowId, RefreshTarget = refreshTarget, PreserveScroll = request.PreserveScroll, RefreshScrollTarget = request.RefreshScrollTarget, RefreshScrollMode = request.RefreshScrollMode }, responseMode, StatusCodes.Status404NotFound);
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
        preserveScroll = session.PreserveScroll,
        refreshScrollTarget = session.RefreshScrollTarget,
        refreshScrollMode = session.RefreshScrollMode,
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

static IReadOnlyList<int> BuildManagedViewerStopPidSet(ExternalTunerLeaseDto lease, bool includeRegistrySameTuner)
{
    var pids = new SortedSet<int>();
    if (lease.ProcessId.HasValue && lease.ProcessId.Value > 0)
        pids.Add(lease.ProcessId.Value);
    if (includeRegistrySameTuner)
    {
        foreach (var viewer in TvAirManagedProcessRegistry.GetViewers(lease.Did, lease.BonDriverFileName))
        {
            if (viewer.ProcessId > 0) pids.Add(viewer.ProcessId);
        }
    }
    return pids.ToList();
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


static PluginViewerControlChannelInfo ToPluginViewerControlChannelInfo(ChannelTarget c, int index)
{
    var filterGroup = PluginProgramGuideFilterGroupFromChannel(c.Group, c.OriginalNetworkId);
    var allocation = NormalizePluginAllocationGroup(c.Group);
    var ready = c.OriginalNetworkId != 0 && c.TransportStreamId != 0 && c.ServiceId != 0;
    return new PluginViewerControlChannelInfo
    {
        ProgramGuideOrder = index,
        ServiceName = c.Name,
        NetworkId = c.OriginalNetworkId,
        TransportStreamId = c.TransportStreamId,
        ServiceId = c.ServiceId,
        Nid = c.OriginalNetworkId,
        Tsid = c.TransportStreamId,
        Sid = c.ServiceId,
        ProgramGuideFilterGroup = filterGroup,
        ProgramGuideFilterKey = filterGroup,
        ProgramGuideFilterLabel = PluginProgramGuideFilterLabel(filterGroup),
        BroadcastGroup = filterGroup,
        AllocationGroup = allocation,
        TunerGroup = allocation,
        ChannelSpace = c.ResolvedSpace,
        ChannelIndex = c.ResolvedChannelIndex,
        ChannelArgument = c.ChannelArgument,
        IdentitySource = "ProgramGuideProjection",
        ViewerStartIdentityReady = ready
    };
}

static string PluginProgramGuideFilterGroupFromChannel(string? group, ushort networkId)
{
    var g = (group ?? string.Empty).Trim().ToUpperInvariant();
    if (g == "GR") return "GR";
    if (g == "BS") return "BS";
    if (g == "CS") return "CS";
    if (g == "BSCS") return networkId == 4 ? "BS" : "CS";
    return g;
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

static string PluginProgramGuideFilterLabel(string? group)
{
    return (group ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "GR" => "地上波",
        "BS" => "BS",
        "CS" => "CS",
        _ => group ?? string.Empty
    };
}

static ViewerProfileProjectionResult BuildViewerProfileProjection(TvTestSettings settings, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles)
{
    var profiles = ViewerProfileContract.BuildProfiles(settings, ini, tunerProfiles);
    var enabledRealCount = profiles.Count(p => p.Enabled && !p.IsAuto);
    var viewingTunerCount = profiles.Count(p => p.Enabled && !p.IsAuto && string.Equals(p.Source, "tunerpool-viewing-tvtest-frame", StringComparison.OrdinalIgnoreCase));
    var selectable = profiles
        .Where(p => p.Enabled && !p.IsAuto)
        .OrderBy(p => p.Order)
        .ToList();
    var defaultProfile = selectable.FirstOrDefault(p => p.IsDefault)?.Id
        ?? selectable.FirstOrDefault()?.Id
        ?? "tvtest1";
    return new ViewerProfileProjectionResult(
        profiles,
        selectable,
        enabledRealCount,
        viewingTunerCount,
        enabledRealCount >= 2,
        defaultProfile,
        string.Join(",", profiles.Select(p => p.Id)),
        string.Join(",", selectable.Select(p => p.Id)));
}


static TvAIrPlugin.PluginViewerProfileInfo ToPluginViewerProfileInfo(ViewerProfileContractDto profile) => new()
{
    Id = profile.Id,
    Name = profile.Name,
    Enabled = profile.Enabled,
    IsDefault = profile.IsDefault,
    Order = profile.Order,
    IsAuto = profile.IsAuto,
    TvTestPathKey = profile.TvTestPathKey,
    Source = profile.Source,
    Note = profile.Note,
    TvTestFrameIndex = profile.TvTestFrameIndex,
    AvailableGroups = string.Join(",", profile.AvailableGroups ?? Array.Empty<string>())
};


static IResult RenderPluginViewerProfiles(IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, LogRepository log)
{
    var projection = BuildViewerProfileProjection(tvTestOptions.Value, ini, tunerProfiles);

    log.Add("VIEWER_PROFILE_PROJECTION", "API",
        $"result=OK source=api viewingTuners={projection.ViewingTunerCount} profiles={projection.Profiles.Count} selectable={projection.SelectableProfiles.Count} enabledReal={projection.EnabledRealProfileCount} selectorVisibleRecommended={projection.SelectorVisibleRecommended} profileIds={SafePluginActionValue(projection.ProfileIds)} selectableIds={SafePluginActionValue(projection.SelectableProfileIds)} default={SafePluginActionValue(projection.DefaultViewerProfile)} endpoint=/api/plugins/viewer-profiles rule=release_contract");

    return Results.Ok(new
    {
        success = true,
        contractVersion = TvAIrVersionContract.PluginHostContractVersion,
        endpoint = "/api/plugins/viewer-profiles",
        displayRule = "select_tvtest_frame_only_no_auto; viewerProfile_is_tvtest_frame_not_single_tuner; keep_min_width_invariant_even_when_selector_hidden",
        payloadField = "viewerProfile",
        defaultViewerProfile = projection.DefaultViewerProfile,
        enabledRealProfileCount = projection.EnabledRealProfileCount,
        selectorVisibleRecommended = projection.SelectorVisibleRecommended,
        minWidthInvariantRequired = true,
        viewerProfiles = projection.Profiles,
        selectableViewerProfiles = projection.SelectableProfiles
    });
}

static IResult RenderPluginViewerTuners(TunerPool tuners, ExternalTunerLeaseService externalTuners, LogRepository log)
{
    var leases = externalTuners.GetActiveLeases().ToList();
    var lastViewerActionResult = externalTuners.GetLastViewerActionResult();
    var items = tuners.GetStatus()
        .Where(t => string.Equals(t.Role, "Viewing", StringComparison.OrdinalIgnoreCase))
        .OrderBy(t => t.Group)
        .ThenBy(t => t.SlotIndex)
        .Select(t =>
        {
            var lease = leases.FirstOrDefault(l => string.Equals(l.Did, t.Did, StringComparison.OrdinalIgnoreCase) || string.Equals(l.TunerName, t.Name, StringComparison.OrdinalIgnoreCase));
            var allocationGroup = NormalizePluginAllocationGroup(t.Group);
            var filterGroup = lease is not null ? PluginProgramGuideFilterGroupFromAllocation(lease.Group, lease.NetworkId) : PluginProgramGuideFilterGroupFromTunerGroup(t.Group);
            return new PluginViewerTunerInfo
            {
                Name = t.Name,
                SlotIndex = t.SlotIndex,
                Did = t.Did,
                ProgramGuideFilterGroup = filterGroup,
                DisplayGroup = filterGroup,
                AllocationGroup = allocationGroup,
                TunerGroup = allocationGroup,
                Role = t.Role,
                UsageKind = t.UsageKind.ToString(),
                Busy = t.UsageKind != TunerUsageKind.Free || lease is not null,
                IsViewingRole = true,
                IsSelectableForViewer = t.UsageKind == TunerUsageKind.Free && lease is null,
                BonDriverFileName = t.BonDriverFileName,
                CurrentLeaseId = lease?.LeaseId ?? string.Empty,
                Availability = t.UsageKind == TunerUsageKind.Free && lease is null ? "Available" : "Busy",
                OccupiedBy = lease is not null ? "TvAIrViewerLease" : string.Empty,
                ExternalPid = null,
                ExternalBusyReason = string.Empty,
                AvailabilityMessage = string.Empty,
                LastViewerActionState = lastViewerActionResult?.State ?? string.Empty,
                LastViewerActionErrorCode = lastViewerActionResult?.ErrorCode ?? string.Empty,
                LastViewerActionMessage = lastViewerActionResult?.Message ?? string.Empty,
                NetworkId = lease?.NetworkId,
                TransportStreamId = lease?.TransportStreamId,
                ServiceId = lease?.ServiceId
            };
        })
        .ToList();
    log.Add("PLUGIN_VIEWER_TUNERS", "API", $"result=OK count={items.Count} source=TunerPool.GetStatus role=Viewing lastViewerActionState={SafePluginActionValue(lastViewerActionResult?.State)} lastViewerActionError={SafePluginActionValue(lastViewerActionResult?.ErrorCode)} rule=release_contract");
    return Results.Ok(new { success = true, contractVersion = TvAIrVersionContract.PluginHostContractVersion, source = "TunerPool.GetStatus", lastViewerActionResult, items });
}

static IResult RenderPluginViewerControlChannels(ChannelFileLoader channels, LogRepository log)
{
    var items = channels.Load().Targets
        .Select((c, index) => ToPluginViewerControlChannelInfo(c, index))
        .ToList();
    var missing = items.Count(x => !x.ViewerStartIdentityReady);
    log.Add("PLUGIN_VIEWER_CONTROL_IDENTITY", "API", $"result=OK count={items.Count} missingTriplet={missing} identitySource=ProgramGuideProjection identityFields=networkId|transportStreamId|serviceId endpoint=/api/plugins/viewer-control/channels rule=release_contract");
    if (missing > 0)
    {
        var sample = string.Join(";", items.Where(x => !x.ViewerStartIdentityReady).Take(5).Select(x => $"{SafePluginActionValue(x.ServiceName)}:{x.NetworkId}/{x.TransportStreamId}/{x.ServiceId}"));
        log.Add("VIEWER_CONTROL_IDENTITY_PROJECTION_WARN", "API", $"result=WARN missingTriplet={missing} sample={sample} action=disable_viewerStart_for_missing_identity rule=release_contract");
    }
    return Results.Ok(new
    {
        success = true,
        contractVersion = TvAIrVersionContract.PluginHostContractVersion,
        source = "ProgramGuideProjection",
        identitySource = "ProgramGuideProjection",
        identityFields = "networkId|transportStreamId|serviceId",
        payloadAttributes = new
        {
            networkId = "data-tvair-payload-networkId",
            transportStreamId = "data-tvair-payload-transportStreamId",
            serviceId = "data-tvair-payload-serviceId"
        },
        items
    });
}

static IResult RenderPluginProgramGuideWaveFilters(LogRepository log)
{
    var filters = BuildPluginProgramGuideWaveFilters();
    log.Add("PLUGIN_PROGRAM_GUIDE_FILTER", "API", $"result=OK count={filters.Count} source=program_guide_wave_filter_module rule=release_contract");
    return Results.Ok(new { success = true, contractVersion = TvAIrVersionContract.PluginHostContractVersion, source = "program_guide_wave_filter_module", field = "programGuideFilterGroup", items = filters });
}

static IResult RenderPluginViewerControlContract(LogRepository log)
{
    var contract = BuildPluginViewerControlHostContract();
    log.Add("PLUGIN_VIEWER_CONTROL_CONTRACT", "API", $"result=OK safeEvents=dblclick|click scriptAllowed=False viewerTunersEndpoint=/api/plugins/viewer-tuners viewerControlChannelsEndpoint=/api/plugins/viewer-control/channels identitySource=ProgramGuideProjection identityFields=networkId|transportStreamId|serviceId rule=release_contract");
    return Results.Ok(contract);
}


static IResult RenderPluginHostContract(LogRepository log)
{
    var contract = new
    {
        success = true,
        contractVersion = TvAIrVersionContract.PluginHostContractVersion,
        source = "TvAIrPluginHostContract",
        purpose = "generic_plugin_host_contract_foundation",
        compatibilityPolicy = new
        {
            currentContract = TvAIrVersionContract.PluginHostContractVersion,
            newPluginsUseDeclaredManifest = true,
            legacyAdaptersRemainIsolated = true,
            preferredOpenModeIsCompatibilityAlias = true,
            defaultMenuActionKindIsOfficial = true,
            unknownManifestFieldsAreIgnored = true,
            unknownCapabilitiesAreReportedNotGranted = true
        },
        pluginModel = new
        {
            supportedKinds = new[]
            {
                "Viewer",
                "UI",
                "Analysis",
                "Utility",
                "Companion",
                "Remote",
                "Headless"
            },
            uiModes = new[]
            {
                "page",
                "toolWindow",
                "headless",
                "actionOnly"
            },
            rule = "kind_classifies_plugin_capability_authorizes_behavior"
        },
        manifestContract = new
        {
            acceptedFiles = new[] { "plugin.json", "<AssemblyName>.plugin.json", "<AssemblyName>.json" },
            requiredForNewPlugins = new[] { "id", "name", "version", "route", "kind", "permissions" },
            optional = new[]
            {
                "description",
                "vendor",
                "entry",
                "icon",
                "hostContractVersion",
                "capabilities",
                "tags",
                "ui",
                "menu",
                "window",
                "actions",
                "assets",
                "compatibility"
            },
            officialMenuField = "defaultMenuActionKind",
            compatibilityMenuAlias = "preferredOpenMode",
            supportedDefaultMenuActionKinds = new[] { "page", "toolWindow", "settings", "versionDialog", "statusDialog", "none" },
            externalManifestMergePolicy = "plugin_json_supplements_missing_manifest_or_ui_fields_only"
        },
        capabilityContract = new
        {
            officialCapabilities = new[]
            {
                "ShowUi",
                "OpenPage",
                "OpenToolWindow",
                "ReadChannels",
                "ReadEpg",
                "ReadReservations",
                "ControlReservations",
                "ReadTunerStatus",
                "ControlViewer",
                "ReadViewerSessions",
                "UseActionApi",
                "UseWindowApi",
                "UseAssetApi",
                "UseSafeEvent",
                "UseRemoteAccess",
                "UsePairing",
                "UseLocalNetwork"
            },
            remoteFuturePolicy = "remote_and_location_free_plugins_must_declare_capability_and_use_host_auth_scope",
            kindDoesNotGrantPermission = true
        },
        uiContract = new
        {
            page = new { hostRoute = "/plugin/{routeSegment}", chromeManagedBy = "TvAIr" },
            toolWindow = new
            {
                hostManaged = true,
                defaultSizeFields = new[] { "toolWindowWidth", "toolWindowHeight" },
                minimumSizeFields = new[] { "toolWindowMinWidth", "toolWindowMinHeight" },
                windowStateOwner = "TvAIrHost",
                pluginMayDeclarePreference = true,
                pluginMustNotForceWindowState = true
            },
            headless = new { uiRequired = false, actionAndStatusContractRequired = true }
        },
        apiPolicy = new
        {
            stableReadContracts = new[]
            {
                "channels",
                "programGuideProjection",
                "programGuideNowNext",
                "viewerSessions",
                "viewerTuners",
                "viewerControlChannels",
                "windowContract",
                "themeAndDpi",
                "hostCapabilities",
                "pluginManifestProjection"
            },
            controlledActionContracts = new[]
            {
                "viewerStart",
                "viewerStop",
                "openWindow",
                "updateWindow",
                "refreshWindow",
                "closeWindow"
            },
            notExposedByDesign = new[]
            {
                "rawTunerPoolMutation",
                "directTVTestProcessOperation",
                "recordingCoreControlFromViewerPlugin",
                "epgWorkerControlFromViewerPlugin",
                "wakeTaskMutationFromPlugin",
                "rawTransportStreamProcessor",
                "arbitraryTVTestMessageBridge"
            }
        },
        actionContract = new
        {
            endpoint = "/api/plugins/action",
            method = "POST",
            tokenRequired = true,
            safeEventSupported = true,
            viewerActionsUseHostAllocationAndViewerRoutes = true,
            pluginDoesNotOwnTvTestOrTunerAllocation = true,
            hostHandledRefreshAfterSupported = true,
            refreshScrollTargetSupported = true
        },
        windowContract = new
        {
            endpoint = "/api/plugins/window",
            stateEndpoint = "/plugin-window/{windowId}/state",
            capabilitiesEndpoint = "/api/plugins/window/capabilities",
            supportedActions = new[] { "openWindow", "closeWindow", "updateWindow", "refreshWindow" },
            hostOwns = new[] { "create", "reuse", "activate", "minSize", "positionPersistence", "statePersistence", "topMost", "showInTaskbar" },
            pluginMayRequest = new[] { "size", "minSize", "topMost", "refreshTarget", "refreshScrollTarget", "refreshScrollMode" },
            pluginMustNotEmulate = new[] { "Win32WindowState", "externalProcessWindowManagement", "hostChrome" }
        },
        assetContract = new
        {
            assetBaseUrlPattern = "/plugin-assets/{routeSegment}",
            apiBaseUrlPattern = "/api/plugins/{pluginId}/assets",
            allowedExtensions = new[] { "png", "jpg", "jpeg", "gif", "svg", "webp", "ico", "css" },
            externalUrlAllowed = false,
            dataUriRecommended = false,
            formIconSourcePriority = "EmbeddedResource>plugin_file>default_TvAIr_icon"
        },
        safeEventContract = new
        {
            hostScript = "/tvair-safe-event-host.js",
            pluginInlineScriptRequired = false,
            supportedAttributes = new[]
            {
                "data-tvair-action",
                "data-tvair-event",
                "data-tvair-payload",
                "data-tvair-refresh-after",
                "data-tvair-refresh-target",
                "data-tvair-refresh-scroll-target",
                "data-tvair-refresh-scroll-mode"
            },
            tokenLongIdleRecovery = "host_window_alive_token_reissue"
        },
        sanitizerContract = new
        {
            scriptTagsAllowed = false,
            inlineEventAttributesAllowed = false,
            javascriptUrlsAllowed = false,
            externalHttpResourcesAllowed = false,
            iframeAllowed = false,
            objectEmbedAllowed = false,
            pluginUiShouldUseSafeEvent = true
        },
        tvTestHeaderReference = new
        {
            sourceHeader = "TVTestPlugin.h ver.0.0.15-pre",
            adoptedConcepts = new[]
            {
                "hostInfo/capability query",
                "channel/service identity",
                "current program and event projection",
                "theme/dark mode/dpi/font hints",
                "viewer window state and command request separation"
            },
            intentionallyNotMirrored = new[]
            {
                "MESSAGE_STARTRECORD",
                "MESSAGE_STOPRECORD",
                "MESSAGE_MODIFYRECORD",
                "MESSAGE_SETSTREAMCALLBACK",
                "MESSAGE_REGISTERTSPROCESSOR",
                "MESSAGE_SETDRIVERNAME",
                "MESSAGE_CLOSE",
                "MESSAGE_RESET",
                "arbitrary MESSAGE_* passthrough"
            },
            reason = "TvAIr plugins use TvAIr host contracts instead of a raw TVTest callback/message bridge."
        },
        recommendedFutureExtensionPoints = new[]
        {
            "remotePairingAndSessionProjection",
            "locationFreeAccessScopeProjection",
            "read-only current service/detail projection",
            "read-only audio/video stream projection",
            "read-only logo availability projection",
            "theme/dark-mode/dpi/font projection for plugin UI",
            "safe plugin command/status item projection"
        }
    };
    log.Add("PLUGIN_HOST_CONTRACT", "API", $"result=OK contractVersion={TvAIrVersionContract.PluginHostContractVersion} source=TvAIrPluginHostContract purpose=generic_plugin_host_contract_foundation manifest=id|name|version|route|kind|permissions capabilities=kind_separated permissions=capability_scoped uiModes=page|toolWindow|headless legacy=adapter_isolated remoteReady=True rule=release_contract");
    return Results.Ok(contract);
}

static IResult RenderPluginSafeEventClientLog(HttpRequest http, LogRepository log)
{
    static string Q(HttpRequest request, string key) => request.Query.TryGetValue(key, out var v) ? (v.FirstOrDefault() ?? string.Empty) : string.Empty;
    var phase = Q(http, "phase");
    var pluginId = Q(http, "pluginId");
    var route = Q(http, "routeSegment");
    var windowId = Q(http, "windowId");
    var eventName = Q(http, "event");
    var action = Q(http, "action");
    var mode = Q(http, "mode");
    var hostKind = Q(http, "hostKind");
    var tag = Q(http, "tag");
    var hasAction = Q(http, "hasAction");
    var hasToken = Q(http, "hasToken");
    var hasTriplet = Q(http, "hasTriplet");
    var networkId = Q(http, "networkId");
    var transportStreamId = Q(http, "transportStreamId");
    var serviceId = Q(http, "serviceId");
    var message = $"phase={SafePluginActionValue(phase)} event={SafePluginActionValue(eventName)} action={SafePluginActionValue(action)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(route)} windowId={SafePluginActionValue(windowId)} mode={SafePluginActionValue(mode)} hostKind={SafePluginActionValue(hostKind)} tag={SafePluginActionValue(tag)} hasAction={SafePluginActionValue(hasAction)} hasToken={SafePluginActionValue(hasToken)} hasTriplet={SafePluginActionValue(hasTriplet)} networkId={SafePluginActionValue(networkId)} transportStreamId={SafePluginActionValue(transportStreamId)} serviceId={SafePluginActionValue(serviceId)} source=client_beacon_no_plugin_js rule=release_contract";
    log.Add("PLUGIN_SAFE_EVENT_BIND", string.IsNullOrWhiteSpace(route) ? "Client" : route, message);
    return Results.Content("", "image/gif");
}



static IResult RenderPluginSafeEventKeepAlive(HttpRequest http, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, LogRepository log)
{
    static string Q(HttpRequest request, string key) => request.Query.TryGetValue(key, out var v) ? (v.FirstOrDefault() ?? string.Empty) : string.Empty;
    var token = Q(http, "token");
    var requestPluginId = Q(http, "pluginId");
    var requestRouteSegment = Q(http, "routeSegment");
    var windowId = NormalizePluginWindowId(Q(http, "windowId"));
    var mode = Q(http, "mode");
    var session = string.IsNullOrWhiteSpace(windowId) ? null : windows.Get(windowId);
    if (session is null || session.IsClosed || !session.HostAlive)
    {
        var reason = session is null ? "window_not_found" : (session.IsClosed ? "window_closed" : "host_not_alive");
        log.Add("PLUGIN_SAFE_EVENT_KEEPALIVE", string.IsNullOrWhiteSpace(requestRouteSegment) ? "Host" : requestRouteSegment, $"result=SKIPPED reason={SafePluginActionValue(reason)} windowId={SafePluginActionValue(windowId)} requestPlugin={SafePluginActionValue(requestPluginId)} requestRoute={SafePluginActionValue(requestRouteSegment)} mode={SafePluginActionValue(mode)} rule=release_contract");
        return Results.NoContent();
    }

    var effectivePluginId = string.IsNullOrWhiteSpace(requestPluginId) ? session.PluginId : requestPluginId.Trim();
    var effectiveRouteSegment = string.IsNullOrWhiteSpace(requestRouteSegment) ? session.RouteSegment : requestRouteSegment.Trim().Trim('/');

    if (!string.Equals(session.PluginId, effectivePluginId, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(session.RouteSegment, effectiveRouteSegment, StringComparison.OrdinalIgnoreCase))
    {
        log.Add("PLUGIN_SAFE_EVENT_KEEPALIVE", string.IsNullOrWhiteSpace(effectiveRouteSegment) ? "Host" : effectiveRouteSegment, $"result=DENIED reason=window_identity_mismatch windowId={SafePluginActionValue(windowId)} sessionPlugin={SafePluginActionValue(session.PluginId)} requestPlugin={SafePluginActionValue(requestPluginId)} effectivePlugin={SafePluginActionValue(effectivePluginId)} sessionRoute={SafePluginActionValue(session.RouteSegment)} requestRoute={SafePluginActionValue(requestRouteSegment)} effectiveRoute={SafePluginActionValue(effectiveRouteSegment)} mode={SafePluginActionValue(mode)} rule=release_contract");
        return Results.NoContent();
    }

    var ok = actionTokens.Renew(token, effectivePluginId, effectiveRouteSegment, out var reason2, out var expiresAt);
    var issuedToken = string.Empty;
    var recovered = false;
    if (!ok && (reason2.Equals("token_not_found", StringComparison.OrdinalIgnoreCase)
        || reason2.Equals("token_expired", StringComparison.OrdinalIgnoreCase)
        || reason2.Equals("missing_token", StringComparison.OrdinalIgnoreCase)))
    {
        var recoveredEntry = actionTokens.Issue(effectivePluginId, effectiveRouteSegment);
        ok = true;
        recovered = true;
        issuedToken = recoveredEntry.Token;
        expiresAt = recoveredEntry.ExpiresAt;
        reason2 = "token_recovered_from_live_host_window";
    }

    log.Add("PLUGIN_SAFE_EVENT_KEEPALIVE", string.IsNullOrWhiteSpace(effectiveRouteSegment) ? "Host" : effectiveRouteSegment, $"result={(ok ? (recovered ? "RECOVERED" : "OK") : "DENIED")} reason={SafePluginActionValue(reason2)} windowId={SafePluginActionValue(windowId)} requestPlugin={SafePluginActionValue(requestPluginId)} effectivePlugin={SafePluginActionValue(effectivePluginId)} requestRoute={SafePluginActionValue(requestRouteSegment)} effectiveRoute={SafePluginActionValue(effectiveRouteSegment)} tokenRenewed={ok} tokenRecovered={recovered} tokenReturned={!string.IsNullOrWhiteSpace(issuedToken)} tokenExpiresAt={(ok ? expiresAt.ToString("O") : "-")} mode={SafePluginActionValue(mode)} rule=release_contract");
    return Results.Json(new
    {
        ok,
        recovered,
        tokenRenewed = ok,
        token = issuedToken,
        expiresAt = ok ? expiresAt.ToString("O") : string.Empty,
        reason = reason2
    });
}

static IResult RenderPluginSafeEventHostScript(HttpRequest http, LogRepository log)
{
    static string Q(HttpRequest request, string key) => request.Query.TryGetValue(key, out var v) ? (v.FirstOrDefault() ?? string.Empty) : string.Empty;
    var mode = Q(http, "mode");
    var route = Q(http, "route");
    log.Add("PLUGIN_SAFE_EVENT_SCRIPT", string.IsNullOrWhiteSpace(route) ? "Host" : route, $"result=REQUESTED mode={SafePluginActionValue(mode)} routeSegment={SafePluginActionValue(route)} source=external_host_script endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
    const string script = """
(function(){
function g(el,name){try{return el&&el.getAttribute?el.getAttribute(name)||'':'';}catch(e){return '';} }
function tok(v,t){return ((' '+(v||'')+' ').indexOf(' '+t+' '))>=0;}
function tag(el){try{return el&&el.tagName?String(el.tagName).toLowerCase():'';}catch(e){return '';} }
function q(name){try{var s=location.search||'';var m=s.match(new RegExp('[?&]'+name+'=([^&]+)'));return m?decodeURIComponent(m[1].replace(/\+/g,' ')):'';}catch(e){return '';} }
function win(){return q('__tvairWindowId')||q('windowId');}
function mode(){try{return (document.body&&document.body.className&&document.body.className.indexOf('tvair-plugin-toolwindow-content-only')>=0)?'directContent':'page';}catch(e){return q('__tvairToolHostContent')?'directContent':'page';} }
function route(){try{return (document.body&&document.body.getAttribute('data-plugin-route'))||'';}catch(e){return '';} }
function firstToken(){try{var a=document.querySelector('[data-tvair-action-token],[data-tvair-token]');var v=resolve(a||document.body,['data-tvair-action-token','data-tvair-token']);if(v)return v;var i=document.querySelector('input[name=\"actionToken\"],input[name=\"token\"]');return i?i.value||'':'';}catch(e){return '';} }
function firstAttr(el,names){for(var i=0;i<names.length;i++){var v=g(el,names[i]);if(v)return v;}return '';}
function nearestForm(el){var n=el;while(n&&n!==document){if(tag(n)==='form')return n;n=n.parentNode;}return null;}
function collectNodes(el){var a=[];var n=el;while(n&&n!==document){a.push(n);if(tag(n)==='form')break;n=n.parentNode;}if(document.body)a.push(document.body);return a;}
function resolve(el,names){var nodes=collectNodes(el);for(var i=0;i<nodes.length;i++){var v=firstAttr(nodes[i],names);if(v)return v;}return '';}
function canonTriplet(el){return {
  networkId:resolve(el,['data-tvair-payload-networkId','data-tvair-payload-NetworkId','data-tvair-payload-nid','data-tvair-network-id','data-network-id','data-nid']),
  transportStreamId:resolve(el,['data-tvair-payload-transportStreamId','data-tvair-payload-TransportStreamId','data-tvair-payload-tsid','data-tvair-transport-stream-id','data-transport-stream-id','data-tsid']),
  serviceId:resolve(el,['data-tvair-payload-serviceId','data-tvair-payload-ServiceId','data-tvair-payload-sid','data-tvair-service-id','data-service-id','data-sid'])
};}
function send(phase,eventName,action,el){
  try{
    var t=canonTriplet(el);
    var url='/api/plugins/safe-event/client-log?phase='+encodeURIComponent(phase||'')+
      '&event='+encodeURIComponent(eventName||'')+
      '&action='+encodeURIComponent(action||'')+
      '&pluginId='+encodeURIComponent(resolve(el,['data-tvair-plugin-id'])||'')+
      '&routeSegment='+encodeURIComponent(resolve(el,['data-tvair-route-segment'])||route()||'')+
      '&windowId='+encodeURIComponent(resolve(el,['data-tvair-window-id'])||win()||'')+
      '&mode='+encodeURIComponent(mode())+
      '&hostKind='+encodeURIComponent('winforms_webbrowser_fallback_direct_content')+
      '&tag='+encodeURIComponent(tag(el))+
      '&hasAction='+encodeURIComponent(action?'true':'false')+
      '&hasToken='+encodeURIComponent((resolve(el,['data-tvair-action-token','data-tvair-token']))?'true':'false')+
      '&hasTriplet='+encodeURIComponent((t.networkId&&t.transportStreamId&&t.serviceId)?'true':'false')+
      '&networkId='+encodeURIComponent(t.networkId||'')+
      '&transportStreamId='+encodeURIComponent(t.transportStreamId||'')+
      '&serviceId='+encodeURIComponent(t.serviceId||'')+
      '&_='+String(new Date().getTime());
    try{var xhr=window.XMLHttpRequest?new XMLHttpRequest():null;if(!xhr&&window.ActiveXObject)xhr=new ActiveXObject('Microsoft.XMLHTTP');if(xhr){xhr.open('GET',url,true);xhr.send(null);return;}}catch(e1){ }
    try{var img=new Image();img.src=url;}catch(e2){ }
    try{var f=document.createElement('iframe');f.style.display='none';f.src=url;document.body.appendChild(f);setTimeout(function(){try{if(f&&f.parentNode)f.parentNode.removeChild(f);}catch(e3){ }},3000);}catch(e4){ }
  }catch(e){ }
}
function find(start,eventName){var n=start;while(n&&n!==document){if(n.getAttribute&&g(n,'data-tvair-action')){var ev=g(n,'data-tvair-event');if(tok(ev,eventName))return n;if(eventName==='click'&&tok(ev,'dblclick')&&g(n,'data-tvair-click-fallback')==='true')return n;}n=n.parentNode;}return null;}
function addHidden(form,name,value){if(!name||value==null||value==='')return;try{var es=form.elements?form.elements[name]:null;if(es){var e=es.length?es[es.length-1]:es;if(e&&e.name){e.value=String(value);return;}}}catch(x){ }var i=document.createElement('input');i.type='hidden';i.name=name;i.value=String(value);form.appendChild(i);}
function addAlias(form,canonical,value){if(!value)return;addHidden(form,canonical,value);if(canonical==='networkId')addHidden(form,'NetworkId',value);if(canonical==='transportStreamId')addHidden(form,'TransportStreamId',value);if(canonical==='serviceId')addHidden(form,'ServiceId',value);if(canonical==='networkId')addHidden(form,'nid',value);if(canonical==='transportStreamId')addHidden(form,'tsid',value);if(canonical==='serviceId')addHidden(form,'sid',value);}
function isExplicitTvTestProfileValue(v){try{v=String(v||'').replace(/^\s+|\s+$/g,'');if(!v)return false;if(/^\d+$/.test(v))return parseInt(v,10)>0;return /^tvtest\d+$/i.test(v);}catch(e){return false;}}
function readControlValue(sel){try{var e=document.querySelector(sel);if(!e)return '';var v=(e.value!=null)?String(e.value):'';if(v)return v;if(e.getAttribute)return e.getAttribute('value')||'';return '';}catch(x){return '';}}
function currentViewerProfileValue(srcForm){try{var names=['viewerProfile','viewer-profile','viewer_profile','viewerProfileId','vtuner','vTuner','viewer','profile'];var selectors=[];for(var i=0;i<names.length;i++){selectors.push('select[name="'+names[i]+'"]');selectors.push('input[name="'+names[i]+'"]');selectors.push('[data-tvair-current-viewer-profile]');selectors.push('[data-viewer-profile-current]');}var best='';for(var j=0;j<selectors.length;j++){var v=readControlValue(selectors[j]);if(isExplicitTvTestProfileValue(v))return v;if(!best&&v)best=v;}if(srcForm&&srcForm.elements){for(var k=0;k<names.length;k++){try{var el=srcForm.elements[names[k]];if(el){var n=el.length?el[el.length-1]:el;var fv=n&&n.value?String(n.value):'';if(isExplicitTvTestProfileValue(fv))return fv;if(!best&&fv)best=fv;}}catch(e1){}}}return best;}catch(e){return '';}}
function addViewerProfileAliases(form,value){try{if(!value)return;addHidden(form,'viewerProfile',value);addHidden(form,'ViewerProfile',value);addHidden(form,'viewer_profile',value);addHidden(form,'viewer-profile',value);addHidden(form,'viewerProfileId',value);addHidden(form,'ViewerProfileId',value);}catch(e){}}
function copyDataPayload(form,node){if(!node||!node.attributes)return;for(var i=0;i<node.attributes.length;i++){var a=node.attributes[i];if(a&&a.name&&a.name.indexOf('data-tvair-payload-')===0)addHidden(form,a.name.substring('data-tvair-payload-'.length),a.value);}}
function copyExistingInputs(form,src){try{if(!src||!src.elements)return;for(var i=0;i<src.elements.length;i++){var e=src.elements[i];if(e&&e.name)addHidden(form,e.name,e.value||'');}}catch(x){ }}
function submit(el,eventName){var action=resolve(el,['data-tvair-action']);send('received_before_validate',eventName,action,el);if(!action){send('denied_missing_action',eventName,action,el);return;}var isWin=(action==='refreshWindow'||action==='updateWindow'||action==='closeWindow'||action==='rerenderWindow'||action==='openWindow');var srcForm=nearestForm(el);var form=document.createElement('form');form.method='post';form.action=resolve(el,['data-tvair-endpoint'])||(srcForm&&srcForm.getAttribute('action'))||(isWin?'/api/plugins/window':'/api/plugins/action');copyExistingInputs(form,srcForm);var currentViewerProfile=currentViewerProfileValue(srcForm);addViewerProfileAliases(form,currentViewerProfile);addHidden(form,'action',action);addHidden(form,'pluginId',resolve(el,['data-tvair-plugin-id']));addHidden(form,'routeSegment',resolve(el,['data-tvair-route-segment'])||route()||'');addHidden(form,'token',resolve(el,['data-tvair-token','data-tvair-action-token']));addHidden(form,'actionToken',resolve(el,['data-tvair-action-token','data-tvair-token']));addHidden(form,'responseMode',resolve(el,['data-tvair-response-mode'])||(isWin?'hostHandled':'refreshWindow'));addHidden(form,'windowId',resolve(el,['data-tvair-window-id'])||win());addHidden(form,'target',resolve(el,['data-tvair-target'])||'content');addHidden(form,'refreshTarget',resolve(el,['data-tvair-refresh-target'])||'content');addHidden(form,'preserveScroll',resolve(el,['data-tvair-preserve-scroll'])||'true');addHidden(form,'refreshScrollTarget',resolve(el,['data-tvair-refresh-scroll-target','data-tvair-scroll-target','data-tvair-focus-target'])||'');addHidden(form,'refreshScrollMode',resolve(el,['data-tvair-refresh-scroll-mode','data-tvair-scroll-mode'])||'center');addHidden(form,'safeEvent',eventName||'unknown');addHidden(form,'safeEventAction',action);addHidden(form,'safeEventSource','external-host-script-no-plugin-js');addHidden(form,'safeEventWindowId',resolve(el,['data-tvair-window-id'])||win());var nodes=collectNodes(el);for(var n=nodes.length-1;n>=0;n--)copyDataPayload(form,nodes[n]);addViewerProfileAliases(form,currentViewerProfile);var t=canonTriplet(el);addAlias(form,'networkId',t.networkId);addAlias(form,'transportStreamId',t.transportStreamId);addAlias(form,'serviceId',t.serviceId);document.body.appendChild(form);send('posting_form',eventName,action,el);form.submit();}
function handle(e,eventName){e=e||window.event;var target=e.target||e.srcElement;var el=find(target,eventName);if(!el)return true;try{if(e.preventDefault)e.preventDefault();e.returnValue=false;}catch(x){ }submit(el,eventName);return false;}
function bind(){if(window.__tvairSafeEventBound){send('bind_skip_already_bound','','',document.body);return;}window.__tvairSafeEventBound=true;send('bind_start','','',document.body);if(document.addEventListener){document.addEventListener('dblclick',function(e){return handle(e,'dblclick');},false);document.addEventListener('click',function(e){return handle(e,'click');},false);}else if(document.attachEvent){document.attachEvent('ondblclick',function(){return handle(window.event,'dblclick');});document.attachEvent('onclick',function(){return handle(window.event,'click');});}else{var od=document.ondblclick;document.ondblclick=function(e){if(handle(e||window.event,'dblclick')===false)return false;return od?od(e):true;};var oc=document.onclick;document.onclick=function(e){if(handle(e||window.event,'click')===false)return false;return oc?oc(e):true;};}send('bind_complete','','',document.body);}
function applyToken(t){try{if(!t)return;var nodes=document.querySelectorAll('[data-tvair-action-token],[data-tvair-token]');for(var i=0;i<nodes.length;i++){try{nodes[i].setAttribute('data-tvair-action-token',t);nodes[i].setAttribute('data-tvair-token',t);}catch(e){}}var inputs=document.querySelectorAll('input[name="actionToken"],input[name="token"],input[name="windowToken"]');for(var j=0;j<inputs.length;j++){try{inputs[j].value=t;}catch(e){}}if(document.body){document.body.setAttribute('data-tvair-action-token',t);document.body.setAttribute('data-tvair-token',t);}}catch(e){} }
function readJsonToken(text){try{if(!text)return'';if(window.JSON&&JSON.parse){var o=JSON.parse(text);return o&&o.token?String(o.token):'';}var m=/"token"\s*:\s*"([^"]+)"/.exec(text);return m?m[1]:'';}catch(e){return '';} }
function keepalive(){try{var w=win();var r=route();var t=firstToken();if(!w||!r)return;var actionNode=document.querySelector('[data-tvair-plugin-id]');var p=resolve(actionNode||document.body,['data-tvair-plugin-id'])||'';var url='/api/plugins/safe-event/keepalive?pluginId='+encodeURIComponent(p)+'&routeSegment='+encodeURIComponent(r)+'&windowId='+encodeURIComponent(w)+'&mode='+encodeURIComponent(mode())+'&token='+encodeURIComponent(t||'')+'&_='+String(new Date().getTime());var xhr=window.XMLHttpRequest?new XMLHttpRequest():null;if(!xhr&&window.ActiveXObject)xhr=new ActiveXObject('Microsoft.XMLHTTP');if(xhr){xhr.onreadystatechange=function(){try{if(xhr.readyState===4){applyToken(readJsonToken(xhr.responseText||''));}}catch(e){}};xhr.open('GET',url,true);xhr.send(null);return;}var img=new Image();img.src=url;}catch(e){} }
function startKeepalive(){try{if(window.__tvairSafeEventKeepaliveStarted)return;window.__tvairSafeEventKeepaliveStarted=true;keepalive();setInterval(keepalive,300000);}catch(e){}}
function boot(){try{bind();startKeepalive();}catch(e){send('bind_error','','',document.body);} }
send('script_loaded','','',document.body||null);
if(document.readyState==='complete'||document.readyState==='interactive')boot();else if(document.addEventListener)document.addEventListener('DOMContentLoaded',boot,false);else if(document.attachEvent)document.attachEvent('onreadystatechange',function(){if(document.readyState==='complete')boot();});else window.onload=boot;
})();
""";
    return Results.Content(script, "application/javascript; charset=utf-8");
}

static IReadOnlyList<PluginProgramGuideWaveFilterInfo> BuildPluginProgramGuideWaveFilters() => new[]
{
    new PluginProgramGuideWaveFilterInfo { Key = "GR", Group = "GR", Label = "地上波", Order = 0, IsProgramGuideFilter = true },
    new PluginProgramGuideWaveFilterInfo { Key = "BS", Group = "BS", Label = "BS", Order = 1, IsProgramGuideFilter = true },
    new PluginProgramGuideWaveFilterInfo { Key = "CS", Group = "CS", Label = "CS", Order = 2, IsProgramGuideFilter = true }
};

static PluginViewerControlHostContract BuildPluginViewerControlHostContract() => new()
{
    ContractVersion = TvAIrVersionContract.PluginHostContractVersion,
    ToolWindowOnlySafeEvents = true,
    PluginScriptAllowed = false,
    SupportedEvents = new[] { "dblclick", "click" },
    SupportedActions = new[] { "viewerStart", "viewerStop", "refreshWindow", "updateWindow" },
    ViewerStartPayloadFields = "networkId|transportStreamId|serviceId|serviceName|programGuideFilterGroup|broadcastGroup|allocationGroup|tunerGroup|channelSpace|channelIndex|viewerProfile|windowId|responseMode|refreshAfter|refreshTarget|preserveViewerWindowState|viewerActivation|retuneExistingViewer; aliases=NetworkId|TransportStreamId|ServiceId|nid|tsid|sid|network_id|transport_stream_id|service_id|viewer_profile|payloadJson|actionPayloadJson|viewerStartPayloadJson",
    ViewerStartPreferredTunerFields = "preferredTunerName|preferredDid|preferredSlot",
    ProgramGuideFilterSource = "TvAIr program guide wave filter module",
    ProgramGuideFilterField = "programGuideFilterGroup",
    TunerGroupField = "tunerGroup",
    ViewerTunersEndpoint = "/api/plugins/viewer-tuners",
    ViewerControlChannelsEndpoint = "/api/plugins/viewer-control/channels",
    ViewerControlIdentitySource = "ProgramGuideProjection",
    ViewerControlIdentityFields = "networkId|transportStreamId|serviceId",
    WaveFiltersEndpoint = "/api/plugins/program-guide/wave-filters",
    AlwaysOnTopAction = "updateWindow payload.alwaysOnTop",
    RefreshReloadScopeDirectContent = "toolwindow-content-document",
    PreferredOpenModeToolWindowSupported = true,
    ToolWindowContentOnly = true,
    ViewerActionRefreshAfter = "viewerStart/viewerStop responseMode=hostHandled refreshAfter=true refreshTarget=content",
    ViewerStartWindowStateContract = "preserveViewerWindowState=true viewerActivation=preserve",
    ViewerStartRetuneExistingContract = "TvAIr-managed viewerStart uses existing managed TVTest internal retune only when viewerProfile/tvTestPathKey, BonDriver, and DID all match; BonDriver/DID changes require profile-scoped restart; retuneExistingViewer remains explicit/diagnostic only",
    ViewerSessionCurrentSource = "GetViewerSessions active leases with networkId/transportStreamId/serviceId plus viewerProfile/viewerProfileName/tvTestPathKey",
    ViewerProfilesEndpoint = "/api/plugins/viewer-profiles",
    ViewerProfilePayloadField = "viewerProfile",
    ViewerProfileReuseContract = "existing viewer reuse is scoped by viewerProfile/tvTestPathKey"
};

static string NormalizePluginAllocationGroup(string? group)
{
    var g = (group ?? string.Empty).Trim().ToUpperInvariant();
    return g switch
    {
        "BS" or "CS" or "BS/CS" or "BSCS" => "BSCS",
        "地上波" or "GR" or "GROUND" => "GR",
        _ => string.IsNullOrWhiteSpace(g) ? string.Empty : g
    };
}

static string PluginProgramGuideFilterGroupFromTunerGroup(string? group)
    => NormalizePluginAllocationGroup(group) == "GR" ? "GR" : "BSCS";

static string PluginProgramGuideFilterGroupFromAllocation(string? allocationGroup, ushort? networkId)
{
    var g = NormalizePluginAllocationGroup(allocationGroup);
    if (g == "GR") return "GR";
    if (g == "BSCS") return networkId == 4 ? "BS" : "CS";
    return g;
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
    return new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
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
    var canRecoverReason = initialReason.Equals("token_not_found", StringComparison.OrdinalIgnoreCase)
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
    return $"<div class=\"tvair-plugin-render-error\" style=\"padding:12px;font-family:system-ui,'Segoe UI',sans-serif;color:var(--tvair-color-text-main);background:#fff;\">" +
           $"<h2 style=\"font-size:15px;margin:0 0 8px;\">Plugin RenderHtml error</h2>" +
           $"<p style=\"margin:0 0 6px;\">plugin={safePlugin} route={safeRoute}</p>" +
           $"<pre style=\"white-space:pre-wrap;font-size:12px;background:var(--tvair-color-surface-subpanel);border:1px solid #ddd;padding:8px;\">{safeMessage}</pre>" +
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
    var normalized = NormalizePluginRouteSegment(pluginOrRoute);
    if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
    var ui = registry.GetUiPlugins().FirstOrDefault(p =>
    {
        var route = NormalizePluginRouteSegment(p.Ui.RouteSegment);
        var identity = NormalizePluginRouteSegment(GetPluginActionIdentity(p, route));
        return string.Equals(route, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity, normalized, StringComparison.OrdinalIgnoreCase);
    });
    return NormalizePluginRouteSegment(ui?.Ui.RouteSegment) is { Length: > 0 } routeSegment ? routeSegment : normalized;
}


static PluginToolWindowIconSpec ResolvePluginToolWindowIcon(ITvAIrPlugin? plugin, string? routeSegment, string pluginActionId, LogRepository log)
{
    if (plugin is null)
    {
        return ResolveDefaultToolWindowIcon(string.Empty, "default_TvAIr_icon", "plugin_null");
    }

    var pluginName = plugin.Name ?? string.Empty;
    var manifestIcon = string.Empty;
    try
    {
        if (plugin is IManifestPlugin mp && !string.IsNullOrWhiteSpace(mp.Manifest.Icon))
            manifestIcon = mp.Manifest.Icon.Trim();
        if (string.IsNullOrWhiteSpace(manifestIcon) && plugin is IUiPlugin ui && !string.IsNullOrWhiteSpace(ui.Ui.Icon))
            manifestIcon = ui.Ui.Icon.Trim();
    }
    catch { }

    var normalizedIcon = NormalizePluginAssetName(manifestIcon);
    if (string.IsNullOrWhiteSpace(normalizedIcon) || !string.Equals(Path.GetExtension(normalizedIcon), ".ico", StringComparison.OrdinalIgnoreCase))
    {
        return ResolveDefaultToolWindowIcon(normalizedIcon, "default_TvAIr_icon", string.IsNullOrWhiteSpace(normalizedIcon) ? "manifest_icon_empty" : "manifest_icon_not_ico");
    }

    try
    {
        var asm = plugin.GetType().Assembly;
        var resourceNames = asm.GetManifestResourceNames();
        var resource = resourceNames.FirstOrDefault(r =>
            string.Equals(r, normalizedIcon, StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith("." + normalizedIcon, StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith(".Assets." + normalizedIcon, StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith(".assets." + normalizedIcon, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(resource))
        {
            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                {
                    return new PluginToolWindowIconSpec(normalizedIcon, "embedded_resource", "embedded_resource_declared_icon", bytes, null);
                }
            }
        }
    }
    catch (Exception ex)
    {
        log.Add("PLUGIN_TOOL_WINDOW_ICON", pluginName, $"phase=resolve source=embedded_resource result=ERROR manifestIcon={SafePluginActionValue(normalizedIcon)} error={SafePluginActionValue(ex.GetType().Name)} rule=release_contract");
    }

    try
    {
        var asmLocation = plugin.GetType().Assembly.Location;
        var pluginDir = string.IsNullOrWhiteSpace(asmLocation) ? string.Empty : Path.GetDirectoryName(asmLocation) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(pluginDir) && Directory.Exists(pluginDir))
        {
            var fullRoot = Path.GetFullPath(pluginDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidates = new[]
            {
                Path.Combine(pluginDir, normalizedIcon),
                Path.Combine(pluginDir, "Assets", normalizedIcon),
                Path.Combine(pluginDir, "assets", normalizedIcon),
                Path.Combine(pluginDir, "wwwroot", normalizedIcon),
                Path.Combine(pluginDir, "wwwroot", "assets", normalizedIcon)
            };
            foreach (var candidate in candidates)
            {
                var full = Path.GetFullPath(candidate);
                if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(full)) continue;
                return new PluginToolWindowIconSpec(normalizedIcon, "plugin_file", "plugin_declared_icon_file", null, full);
            }
        }
    }
    catch (Exception ex)
    {
        log.Add("PLUGIN_TOOL_WINDOW_ICON", pluginName, $"phase=resolve source=plugin_file result=ERROR manifestIcon={SafePluginActionValue(normalizedIcon)} error={SafePluginActionValue(ex.GetType().Name)} rule=release_contract");
    }

    return ResolveDefaultToolWindowIcon(normalizedIcon, "default_TvAIr_icon", "plugin_icon_not_found");
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

static IResult ResolvePluginAssetResult(string pluginOrRoute, string assetName, PluginRegistry registry, LogRepository log, string source)
{
    var route = ResolvePluginAssetRouteSegment(pluginOrRoute, registry);
    var name = NormalizePluginAssetName(assetName);
    if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(name))
    {
        log.Add("PLUGIN_ASSET", "DENY", $"result=BAD_REQUEST source={SafePluginActionValue(source)} pluginOrRoute={SafePluginActionValue(pluginOrRoute)} asset={SafePluginActionValue(assetName)} reason=invalid_route_or_asset rule=release_contract");
        return Results.BadRequest("Invalid plugin asset request.");
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
        return PluginHtmlMessage("Plugin window closed", "このプラグインウィンドウは閉じられています。もう一度プラグイン画面から開いてください。", StatusCodes.Status410Gone, $"/plugin/{Uri.EscapeDataString(session.RouteSegment)}", "プラグインへ戻る");
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
    var titleStyle = "display:none";
    var html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{title}}</title>
<style>
*{box-sizing:border-box;}
html,body{margin:0;padding:0;width:100%;height:100%;min-width:0;min-height:0;overflow:hidden;background:var(--tvair-color-surface-panel);color:var(--tvair-color-text-main);font-family:system-ui,"Segoe UI",sans-serif;}
.tvair-plugin-window-host{position:fixed;inset:0;width:100vw;height:100vh;min-width:0;min-height:0;display:flex;flex-direction:column;background:#fff;overflow:hidden;}
.tvair-plugin-window-host-tool{background:#fff;}
.tvair-plugin-window-title{flex:0 0 32px;height:32px;display:flex;align-items:center;padding:0 10px;font-size:13px;background:#20242b;color:#eee;border-bottom:1px solid #333;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
.tvair-plugin-window-frame{display:block;flex:1 1 auto;min-width:0;min-height:0;border:0;width:100%;height:100%;background:#fff;overflow:auto;}
.tvair-plugin-window-host-tool .tvair-plugin-window-frame{position:absolute;inset:0;width:100%;height:100%;}
</style>
</head>
<body>
<div class="{{hostClass}}" data-window-id="{{encodedWindowId}}" data-window-revision="{{initialRevision}}" data-tool-host="{{toolHost.ToString().ToLowerInvariant()}}">
  <div class="tvair-plugin-window-title" style="{{titleStyle}}">{{title}}</div>
  <iframe id="tvair-plugin-window-frame" class="tvair-plugin-window-frame" src="{{content}}" title="{{title}}" scrolling="auto"></iframe>
</div>
<script>
(() => {
  const stateUrl = "{{stateUrl}}";
  const frame = document.getElementById('tvair-plugin-window-frame');
  let lastRevision = Number(document.querySelector('.tvair-plugin-window-host')?.dataset.windowRevision || '0');
  let refreshing = false;
  async function pollWindowState() {
    if (refreshing || !frame) return;
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
        const restoreScroll = () => {
          if (!preserveScroll) return;
          try { frame.contentWindow?.scrollTo(scrollX, scrollY); } catch {}
        };
        frame.addEventListener('load', restoreScroll, { once: true });
        frame.setAttribute('src', url.pathname + url.search + url.hash);
        window.setTimeout(() => { refreshing = false; }, 800);
      }
    } catch { }
  }
  window.setInterval(pollWindowState, 1000);
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
    var mode = (value ?? string.Empty).Trim();
    if (mode.Equals("redirect", StringComparison.OrdinalIgnoreCase)) return "redirect";
    if (mode.Equals("redirectBack", StringComparison.OrdinalIgnoreCase) || mode.Equals("redirect-back", StringComparison.OrdinalIgnoreCase)) return "redirectBack";
    if (mode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase) || mode.Equals("host-handled", StringComparison.OrdinalIgnoreCase)) return "hostHandled";
    if (mode.Equals("html", StringComparison.OrdinalIgnoreCase)) return "html";
    if (mode.Equals("noContent", StringComparison.OrdinalIgnoreCase) || mode.Equals("nocontent", StringComparison.OrdinalIgnoreCase)) return "noContent";
    if (mode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase) || mode.Equals("hostWindow", StringComparison.OrdinalIgnoreCase) || mode.Equals("toolWindowRedirectBack", StringComparison.OrdinalIgnoreCase)) return "toolWindow";
    if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "toolWindow";
    if (mode.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase) || mode.Equals("refresh", StringComparison.OrdinalIgnoreCase)) return "refreshWindow";
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
    var explicitReturn = !string.IsNullOrWhiteSpace(request.ReturnUrl) ? request.ReturnUrl : ReadPayload(request.Payload, "returnUrl", "ReturnUrl", "redirectBackUrl", "RedirectBackUrl", "sourceUrl", "SourceUrl");
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
    if (text.StartsWith("//", StringComparison.Ordinal)) return string.Empty;
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

static IResult DispatchPluginDefaultMenuAction(string routeSegment, string source, HttpRequest http, PluginRegistry registry, PluginDefaultMenuActionService menuActions, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, LogRepository log)
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
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", actionInfo.Name, $"result=PLUGIN_NOT_FOUND kind={SafePluginActionValue(actionInfo.Kind)} route={SafePluginActionValue(route)} source={SafePluginActionValue(source)} rule=release_contract");
        return PluginHtmlMessage("プラグイン操作", "対象プラグインが読み込まれていません。", StatusCodes.Status404NotFound, "/", "番組表へ戻る");
    }

    if (actionInfo.Kind.Equals(PluginMenuActionKinds.ToolWindow, StringComparison.OrdinalIgnoreCase))
    {
        if (plugin is not IUiPlugin uiPlugin)
        {
            log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Name, $"result=DENIED kind=toolWindow reason=not_ui_plugin route={SafePluginActionValue(route)} source={SafePluginActionValue(source)} rule=release_contract");
            return PluginHtmlMessage("プラグイン操作", "このプラグインはツールウィンドウを持っていません。", StatusCodes.Status400BadRequest, "/", "番組表へ戻る");
        }

        var manifest = plugin is IManifestPlugin mp ? mp.Manifest : null;
        var pluginActionId = GetPluginActionIdentity(plugin, route);
        var request = new PluginWindowRequest
        {
            Action = "openWindow",
            PluginId = pluginActionId,
            RouteSegment = route,
            Title = string.IsNullOrWhiteSpace(uiPlugin.Ui.MenuText) ? plugin.Name : uiPlugin.Ui.MenuText,
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
                ["showInTaskbar"] = actionInfo.ShowInTaskbar ? "true" : "false"
            }
        };

        var unifiedOpen = OpenOrActivatePluginToolWindowUnified(plugin, pluginActionId, route, request, source, "default_menu_toolwindow", http, windows, toolWindows, log);
        var session = unifiedOpen.Session;
        var reusedWindowSession = unifiedOpen.ReusedSession;
        var hostResult = unifiedOpen.HostResult;

        var returnUrl = ResolvePluginMenuReturnUrl(http);
        var browserNavigation = isTraySource ? "none" : "redirectBack";
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Name, $"result=OK kind=toolWindow source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} windowId={SafePluginActionValue(session.WindowId)} reusedSession={reusedWindowSession} hostResult={SafePluginActionValue(hostResult.Result)} hostReused={hostResult.Reused} activated={hostResult.Activated} size={session.Width}x{session.Height} minSize={session.MinWidth}x{session.MinHeight} showInTaskbar={actionInfo.ShowInTaskbar} browserNavigation={browserNavigation} mainBrowserOpened=False programGuideOpened=False redirectBack={!isTraySource} returnUrl={(isTraySource ? "-" : SafePluginActionValue(returnUrl))} rule=release_contract");
        log.Add("PLUGIN_TOOL_WINDOW_ACTIVATE", plugin.Name, $"result={SafePluginActionValue(hostResult.Result)} source={SafePluginActionValue(source)} windowId={SafePluginActionValue(session.WindowId)} reused={hostResult.Reused} activated={hostResult.Activated} showInTaskbar={actionInfo.ShowInTaskbar} reason=default_menu_action browserNavigationSuppressed=True rule=release_contract");
        return isTraySource ? Results.NoContent() : PluginSeeOther(returnUrl);
    }

    var kind = (actionInfo.Kind ?? string.Empty).Trim();
    var returnUrlForLog = ResolvePluginMenuReturnUrl(http);

    if (kind.Equals(PluginMenuActionKinds.Page, StringComparison.OrdinalIgnoreCase))
    {
        var target = $"/plugin/{Uri.EscapeDataString(route)}";
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Name, $"result=REDIRECT kind=page source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} target={SafePluginActionValue(target)} browserNavigation={(isTraySource ? "none" : "plugin_page")} mainBrowserOpened={!isTraySource} programGuideOpened=False redirectBack=False returnUrl={(isTraySource ? "-" : SafePluginActionValue(returnUrlForLog))} rule=release_contract");
        return isTraySource ? Results.NoContent() : PluginSeeOther(target);
    }

    if (kind.Equals(PluginMenuActionKinds.Settings, StringComparison.OrdinalIgnoreCase))
    {
        var target = $"/plugin/{Uri.EscapeDataString(route)}?mode=settings";
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Name, $"result=REDIRECT kind=settings source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} target={SafePluginActionValue(target)} browserNavigation={(isTraySource ? "none" : "plugin_settings")} mainBrowserOpened={!isTraySource} programGuideOpened=False redirectBack=False returnUrl={(isTraySource ? "-" : SafePluginActionValue(returnUrlForLog))} rule=release_contract");
        return isTraySource ? Results.NoContent() : PluginSeeOther(target);
    }

    if (kind.Equals(PluginMenuActionKinds.VersionDialog, StringComparison.OrdinalIgnoreCase) || kind.Equals(PluginMenuActionKinds.StatusDialog, StringComparison.OrdinalIgnoreCase))
    {
        log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Name, $"result=VERSION_INFO kind=info source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} version={SafePluginActionValue(actionInfo.Version)} browserNavigation={(isTraySource ? "none" : "version_dialog_fallback")} mainBrowserOpened=False programGuideOpened=False redirectBack={!isTraySource} returnUrl={(isTraySource ? "-" : SafePluginActionValue(returnUrlForLog))} rule=release_contract");
        return isTraySource ? Results.NoContent() : RenderPluginVersionInfoPage(actionInfo, returnUrlForLog);
    }

    log.Add("PLUGIN_MENU_ACTION_DISPATCH", plugin.Name, $"result=DENIED kind={SafePluginActionValue(kind)} reason=unsupported_action_kind source={SafePluginActionValue(source)} route={SafePluginActionValue(route)} rule=release_contract");
    return PluginHtmlMessage("プラグイン操作", "このプラグインの既定アクション種別はTvAIr本体で実行できません。", StatusCodes.Status400BadRequest, ResolvePluginMenuReturnUrl(http), "戻る");
}

static ITvAIrPlugin? FindPluginForDefaultMenuAction(PluginRegistry registry, string pluginId, string route)
{
    var ui = registry.FindUiPlugin(route);
    if (ui is not null) return ui;
    return registry.GetAll().FirstOrDefault(p =>
    {
        if (p is IManifestPlugin mp)
        {
            var manifestRoute = (mp.Manifest.Route ?? string.Empty).Trim().Trim('/');
            if (manifestRoute.StartsWith("plugin/", StringComparison.OrdinalIgnoreCase)) manifestRoute = manifestRoute[7..];
            if (string.Equals(mp.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(manifestRoute, route, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return string.Equals(p.Name, pluginId, StringComparison.OrdinalIgnoreCase);
    });
}

static string ResolvePluginMenuReturnUrl(HttpRequest http)
{
    var referrer = NormalizeLocalPluginReturnUrl(http, http.Headers.Referer.ToString());
    if (!string.IsNullOrWhiteSpace(referrer)
        && !referrer.StartsWith("/plugin-menu/", StringComparison.OrdinalIgnoreCase)
        && !referrer.StartsWith("/plugin-menu-info/", StringComparison.OrdinalIgnoreCase)
        && !referrer.StartsWith("/plugin-window/", StringComparison.OrdinalIgnoreCase)
        && !referrer.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        return referrer;
    }
    return "/";
}


static IResult RenderPluginVersionInfoPage(PluginDefaultMenuActionInfo actionInfo, string? returnUrl)
{
    var safeTitle = HtmlEncoder.Default.Encode(actionInfo.Name);
    var safeVersion = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(actionInfo.Version) ? "不明" : actionInfo.Version);
    var safeReturn = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    var html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{safeTitle}}</title>
<link rel="stylesheet" href="/tvair-notification.css?v=1.0.8">
</head>
<body>
<script src="/tvair-notification.js?v=1.0.8"></script>
<script>
document.addEventListener('DOMContentLoaded',function(){
  if(window.TvAIrNotify){ TvAIrNotify({ title:'{{safeTitle}}', message:'バージョン: {{safeVersion}}', onOk:function(){ location.replace('{{safeReturn}}'); } }); }
  else { alert('{{safeTitle}}\nバージョン: {{safeVersion}}'); location.replace('{{safeReturn}}'); }
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
        log.Add("PLUGIN_MENU_INFO_RENDER", actionInfo.Name, $"result=PLUGIN_NOT_FOUND route={SafePluginActionValue(actionInfo.RouteSegment)} rule=release_contract");
        return PluginHtmlMessage("プラグイン情報", "対象プラグインが読み込まれていません。", StatusCodes.Status404NotFound, "/", "番組表へ戻る");
    }

    log.Add("PLUGIN_MENU_INFO_RENDER", actionInfo.Name, $"result=OK route={SafePluginActionValue(actionInfo.RouteSegment)} version={SafePluginActionValue(actionInfo.Version)} rule=release_contract");
    return RenderPluginDefaultMenuInfo(actionInfo, plugin);
}

static IResult RenderPluginDefaultMenuInfo(PluginDefaultMenuActionInfo actionInfo, ITvAIrPlugin plugin)
{
    var manifest = plugin is IManifestPlugin mp ? mp.Manifest : null;
    var safeName = HtmlEncoder.Default.Encode(actionInfo.Name);
    var safeVersion = HtmlEncoder.Default.Encode(actionInfo.Version);
    var safeRoute = HtmlEncoder.Default.Encode(actionInfo.RouteSegment);
    var safeKind = HtmlEncoder.Default.Encode(actionInfo.Kind);
    var safeDescription = HtmlEncoder.Default.Encode(string.IsNullOrWhiteSpace(actionInfo.Description) ? (manifest?.Description ?? string.Empty) : actionInfo.Description);
    var html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{safeName}} 情報</title>
<style>
body{font-family:Meiryo,"Yu Gothic UI",sans-serif;margin:24px;background:#f5f7fb;color:#172333}.card{max-width:560px;border:1px solid #c9d2df;background:#fff;border-radius:12px;padding:18px;box-shadow:0 8px 24px rgba(20,30,50,.12)}h1{font-size:20px;margin:0 0 14px}.row{display:flex;gap:12px;margin:8px 0}.k{width:110px;color:#526275}.v{font-weight:600}.button{display:inline-block;margin-top:16px;padding:8px 12px;border:1px solid #8aa0b8;border-radius:8px;color:#102334;text-decoration:none;background:#eef4fb}
</style>
</head>
<body><div class="card"><h1>{{safeName}} 情報</h1><div class="row"><div class="k">Version</div><div class="v">{{safeVersion}}</div></div><div class="row"><div class="k">Route</div><div class="v">{{safeRoute}}</div></div><div class="row"><div class="k">Action</div><div class="v">{{safeKind}}</div></div><p>{{safeDescription}}</p><a class="button" href="/">番組表へ戻る</a></div></body>
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
<style>body{font-family:system-ui,"Segoe UI",sans-serif;margin:24px;line-height:1.6}.card{max-width:720px;border:1px solid #ddd;border-radius:12px;padding:18px}a.button{display:inline-block;margin-top:12px;padding:8px 12px;border:1px solid #888;border-radius:8px;text-decoration:none;color:#111}</style>
</head>
<body><div class="card"><h1>{{safeTitle}}</h1><p>{{safeMessage}}</p><a class="button" href="{{safeHref}}">{{safeLinkText}}</a></div></body>
</html>
""";
    return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
}

static IResult BuildPluginWindowDispatchResponse(PluginWindowResult result, string responseMode, string redirectUrl)
{
    if (responseMode.Equals("redirect", StringComparison.OrdinalIgnoreCase)) return PluginSeeOther(redirectUrl);
    if (responseMode.Equals("redirectBack", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase)) return Results.NoContent();
    if (responseMode.Equals("html", StringComparison.OrdinalIgnoreCase)) return PluginHtmlMessage("Plugin window", result.Message, StatusCodes.Status200OK, result.WindowUrl, "Plugin windowを開く");
    if (responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase)) return Results.NoContent();
    return Results.Ok(result);
}

static IResult BuildPluginWindowDispatchError(PluginWindowResult result, string responseMode, int statusCode)
{
    if (responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase) || responseMode.Equals("redirectBack", StringComparison.OrdinalIgnoreCase))
        return Results.NoContent();
    if (!responseMode.Equals("json", StringComparison.OrdinalIgnoreCase))
        return PluginHtmlMessage("Plugin window error", string.IsNullOrWhiteSpace(result.Message) ? result.Diagnostics : result.Message, statusCode, "/", "番組表へ戻る");
    return Results.Json(result, statusCode: statusCode);
}

static IResult BuildPluginActionDispatchError(PluginActionResult result, string responseMode, string windowId, int statusCode)
{
    if (responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase))
        return Results.NoContent();

    if (!responseMode.Equals("json", StringComparison.OrdinalIgnoreCase))
    {
        var href = string.IsNullOrWhiteSpace(windowId) ? "/" : $"/plugin-window/{Uri.EscapeDataString(windowId)}";
        return PluginHtmlMessage("Plugin action error", string.IsNullOrWhiteSpace(result.Message) ? result.Diagnostics : result.Message, statusCode, href, "戻る");
    }
    return Results.Json(result, statusCode: statusCode);
}

static IResult BuildPluginViewerActionResponse(PluginActionResult result, string responseMode, string windowId, string refreshTarget, bool preserveScroll, PluginWindowSessionStore windows, string pluginId, LogRepository log, string pluginName, string action, bool refreshAfter = false, PluginToolWindowHostService? toolWindows = null, HttpRequest? http = null, string refreshScrollTarget = "", string refreshScrollMode = "center")
{
    if (responseMode.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase))
    {
        var resolvedWindowId = NormalizePluginWindowId(windowId);
        var refreshRequest = new PluginWindowRequest
        {
            WindowId = resolvedWindowId,
            RefreshTarget = NormalizePluginWindowRefreshTarget(refreshTarget),
            Target = NormalizePluginWindowRefreshTarget(refreshTarget),
            PreserveScroll = preserveScroll,
            RefreshScrollTarget = NormalizePluginRefreshScrollTarget(refreshScrollTarget),
            RefreshScrollMode = NormalizePluginRefreshScrollMode(refreshScrollMode),
            ForceReload = true
        };
        var session = windows.Refresh(resolvedWindowId, pluginId, refreshRequest);
        if (session is null)
        {
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action={SafePluginActionValue(action)} responseMode=refreshWindow result=REFRESH_NOT_FOUND windowId={SafePluginActionValue(resolvedWindowId)} rule=release_contract");
            return PluginHtmlMessage("Plugin action accepted", result.Message, StatusCodes.Status200OK, "/", "番組表へ戻る");
        }

        var contentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
        log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action={SafePluginActionValue(action)} responseMode=refreshWindow result=REDIRECT windowId={SafePluginActionValue(session.WindowId)} target=content preserveScroll={preserveScroll} revision={session.Revision} contentRoute={SafePluginActionValue(contentRoute)} rule=release_contract");
        return PluginSeeOther(contentRoute);
    }

    if (responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("noContent", StringComparison.OrdinalIgnoreCase)
        || responseMode.Equals("toolWindow", StringComparison.OrdinalIgnoreCase))
    {
        var refreshIssued = false;
        var hostRefresh = "-";
        var resolvedWindowId = NormalizePluginWindowId(windowId);
        var normalizedTarget = NormalizePluginWindowRefreshTarget(refreshTarget);
        if (refreshAfter && !string.IsNullOrWhiteSpace(resolvedWindowId))
        {
            var refreshRequest = new PluginWindowRequest
            {
                WindowId = resolvedWindowId,
                RefreshTarget = normalizedTarget,
                Target = normalizedTarget,
                PreserveScroll = preserveScroll,
                RefreshScrollTarget = NormalizePluginRefreshScrollTarget(refreshScrollTarget),
                RefreshScrollMode = NormalizePluginRefreshScrollMode(refreshScrollMode),
                ForceReload = true
            };
            var session = windows.Refresh(resolvedWindowId, pluginId, refreshRequest);
            if (session is not null && toolWindows is not null && http is not null)
            {
                var contentRoute = BuildHostManagedPluginContentRoute(session.ContentRoute, session.WindowId, session.Revision);
                var hostCaps = toolWindows.GetCapabilities();
                var navigationUrl = BuildToolWindowNavigationUrl($"/plugin-window/{Uri.EscapeDataString(session.WindowId)}", contentRoute, hostCaps);
                var absoluteNavigationUrl = BuildAbsoluteLocalUrl(http, navigationUrl);
                var hostResult = toolWindows.OpenOrActivate(session, absoluteNavigationUrl);
                hostRefresh = hostResult.Result;
                refreshIssued = true;
                var normalizedScrollTarget = NormalizePluginRefreshScrollTarget(refreshScrollTarget);
                if (!string.IsNullOrWhiteSpace(normalizedScrollTarget))
                    log.Add("PLUGIN_WINDOW_REFRESH_SCROLL", pluginName, $"result=REQUESTED action={SafePluginActionValue(action)} windowId={SafePluginActionValue(session.WindowId)} target={SafePluginActionValue(normalizedScrollTarget)} mode={SafePluginActionValue(NormalizePluginRefreshScrollMode(refreshScrollMode))} hostKind={SafePluginActionValue(hostResult.HostKind)} refreshTarget={SafePluginActionValue(normalizedTarget)} reason=viewer_action_refresh_after_host_content_rerender rule=release_contract");
            }
            else
            {
                hostRefresh = session is null ? "REFRESH_NOT_FOUND" : "HOST_UNAVAILABLE";
            }
        }
        log.Add("PLUGIN_ACTION_RESPONSE_CONTRACT", pluginName, $"action={SafePluginActionValue(action)} result=NO_CONTENT responseMode={SafePluginActionValue(responseMode)} windowId={SafePluginActionValue(windowId)} refreshAfter={refreshAfter} refreshTarget={SafePluginActionValue(normalizedTarget)} refreshScrollTarget={SafePluginActionValue(NormalizePluginRefreshScrollTarget(refreshScrollTarget))} refreshScrollMode={SafePluginActionValue(NormalizePluginRefreshScrollMode(refreshScrollMode))} refreshIssued={refreshIssued} hostRefresh={SafePluginActionValue(hostRefresh)} jsonSuppressed=True contract=plugin_action_hosthandled_refresh_after_content rule=release_contract");
        return Results.NoContent();
    }
    if (responseMode.Equals("html", StringComparison.OrdinalIgnoreCase)) return PluginHtmlMessage("Plugin action accepted", result.Message, StatusCodes.Status200OK, string.IsNullOrWhiteSpace(windowId) ? "/" : $"/plugin-window/{Uri.EscapeDataString(windowId)}", "戻る");
    return Results.Ok(result);
}

static IResult BuildPluginViewerExpectedDeniedResponse(
    ExternalTunerLeaseService externalTuners,
    PluginActionResult result,
    string state,
    string errorCode,
    string responseMode,
    string windowId,
    string refreshTarget,
    bool preserveScroll,
    PluginWindowSessionStore windows,
    string pluginId,
    LogRepository log,
    string pluginName,
    string action,
    string? leaseId = null,
    string? tunerName = null,
    string? did = null,
    string? bonDriverFileName = null,
    int? processId = null,
    ushort? networkId = null,
    ushort? transportStreamId = null,
    ushort? serviceId = null,
    string refreshScrollTarget = "",
    string refreshScrollMode = "center")
{
    externalTuners.SetLastViewerActionResult(pluginName, action, result.Success, state, errorCode, result.Message, leaseId, tunerName, did, bonDriverFileName, processId, networkId, transportStreamId, serviceId, result.Diagnostics);
    log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action={SafePluginActionValue(action)} result=EXPECTED_DENIED state={SafePluginActionValue(state)} errorCode={SafePluginActionValue(errorCode)} responseMode={SafePluginActionValue(responseMode)} windowId={SafePluginActionValue(windowId)} action=refresh_or_json_no_plugin_error_screen rule=release_contract");
    return BuildPluginViewerActionResponse(result, responseMode, windowId, refreshTarget, preserveScroll, windows, pluginId, log, pluginName, action, refreshScrollTarget: refreshScrollTarget, refreshScrollMode: refreshScrollMode);
}

static bool IsProcessAlive(int processId)
{
    if (processId <= 0) return false;
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch
    {
        return false;
    }
}

static async Task<IResult> HandlePluginActionDispatchAsync(HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, ChannelFileLoader channelLoader, TunerPool tunerPool, ExternalTunerLeaseService externalTuners, TvTestLauncher tvTestLauncher, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, LogRepository log)
{
    var request = await ReadPluginActionRequestAsync(http);
    var pluginId = (request.PluginId ?? string.Empty).Trim();
    var action = (request.Action ?? string.Empty).Trim();
    var payload = request.Payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var route = !string.IsNullOrWhiteSpace(request.RouteSegment)
        ? request.RouteSegment.Trim()
        : ReadPayload(payload, "Route", "route", "RouteSegment", "routeSegment");
    if (!string.IsNullOrWhiteSpace(route)) payload["RouteSegment"] = route;

    var responseMode = NormalizePluginFormResponseMode(!string.IsNullOrWhiteSpace(request.ResponseMode) ? request.ResponseMode : ReadPayload(payload, "responseMode", "ResponseMode"));
    var requestedWindowId = NormalizePluginWindowId(!string.IsNullOrWhiteSpace(request.WindowId) ? request.WindowId : ReadPayload(payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId"));
    var requestedRefreshTarget = NormalizePluginWindowRefreshTarget(!string.IsNullOrWhiteSpace(request.RefreshTarget) ? request.RefreshTarget : ReadPayload(payload, "refreshTarget", "RefreshTarget", "target", "Target"));
    var requestedRefreshScrollTarget = NormalizePluginRefreshScrollTarget(!string.IsNullOrWhiteSpace(request.RefreshScrollTarget) ? request.RefreshScrollTarget : ReadPayload(payload, "refreshScrollTarget", "RefreshScrollTarget", "scrollTarget", "ScrollTarget", "focusTarget", "FocusTarget"));
    var requestedRefreshScrollMode = NormalizePluginRefreshScrollMode(!string.IsNullOrWhiteSpace(request.RefreshScrollMode) ? request.RefreshScrollMode : ReadPayload(payload, "refreshScrollMode", "RefreshScrollMode", "scrollMode", "ScrollMode"));
    var safeEvent = ReadPayload(payload, "safeEvent", "SafeEvent");
    var safeEventAction = ReadPayload(payload, "safeEventAction", "SafeEventAction");
    var safeEventSource = ReadPayload(payload, "safeEventSource", "SafeEventSource");
    var safeEventWindowId = NormalizePluginWindowId(ReadPayload(payload, "safeEventWindowId", "SafeEventWindowId"));
    var requestedPreserveScroll = true;
    if (TryReadBoolPayload(payload, out var parsedPreserveScroll, "preserveScroll", "PreserveScroll")) requestedPreserveScroll = parsedPreserveScroll;
    var requestedRefreshAfterExplicit = TryReadBoolPayload(payload, out var parsedRefreshAfter, "refreshAfter", "RefreshAfter");
    var requestedRefreshAfter = requestedRefreshAfterExplicit && parsedRefreshAfter;
    var preserveViewerWindowStateExplicit = TryReadBoolPayload(payload, out var parsedPreserveViewerWindowState, "preserveViewerWindowState", "PreserveViewerWindowState", "preserveFullscreen", "PreserveFullscreen");
    var preserveViewerWindowState = preserveViewerWindowStateExplicit && parsedPreserveViewerWindowState;
    var viewerActivation = ReadPayload(payload, "viewerActivation", "ViewerActivation", "activateMode", "ActivateMode", "focusMode", "FocusMode");
    var retuneExistingViewerExplicit = TryReadBoolPayload(payload, out var parsedRetuneExistingViewer, "retuneExistingViewer", "RetuneExistingViewer", "reuseViewerProcess", "ReuseViewerProcess");
    var retuneExistingViewer = retuneExistingViewerExplicit && parsedRetuneExistingViewer;

    var plugin = FindPluginByActionIdentity(registry, pluginId, route);
    var pluginName = plugin?.Name ?? pluginId;
    var actionAllowed = plugin is IManifestPlugin mp && mp.Manifest.Permissions.Contains(PluginPermission.ControlViewer);
    var isViewerStartAction = action.Equals("viewerStart", StringComparison.OrdinalIgnoreCase) || action.Equals("RequestViewerStart", StringComparison.OrdinalIgnoreCase);
    var isViewerStopAction = action.Equals("viewerStop", StringComparison.OrdinalIgnoreCase) || action.Equals("RequestViewerStop", StringComparison.OrdinalIgnoreCase);
    var isHostManagedToolWindowViewerAction = (isViewerStartAction || isViewerStopAction)
        && !string.IsNullOrWhiteSpace(requestedWindowId)
        && !string.IsNullOrWhiteSpace(safeEvent)
        && responseMode.Equals("hostHandled", StringComparison.OrdinalIgnoreCase);
    if (!requestedRefreshAfterExplicit && isHostManagedToolWindowViewerAction)
    {
        requestedRefreshAfter = true;
        if (string.IsNullOrWhiteSpace(requestedRefreshTarget)) requestedRefreshTarget = "content";
        log.Add("PLUGIN_ACTION_VIEWER_DEFAULT", pluginName, $"action={SafePluginActionValue(action)} result=APPLIED defaultRefreshAfter=True refreshTarget={SafePluginActionValue(requestedRefreshTarget)} windowId={SafePluginActionValue(requestedWindowId)} reason=host_managed_toolwindow_viewer_action_no_plugin_update_needed rule=release_contract");
    }
    if (!preserveViewerWindowStateExplicit && isViewerStartAction && isHostManagedToolWindowViewerAction)
    {
        preserveViewerWindowState = true;
        if (string.IsNullOrWhiteSpace(viewerActivation)) viewerActivation = "preserve";
        log.Add("PLUGIN_ACTION_VIEWER_DEFAULT", pluginName, $"action={SafePluginActionValue(action)} result=APPLIED defaultPreserveViewerWindowState=True viewerActivation={SafePluginActionValue(viewerActivation)} windowId={SafePluginActionValue(requestedWindowId)} reason=host_managed_toolwindow_viewer_action_no_plugin_update_needed rule=release_contract");
    }
    if (!retuneExistingViewerExplicit && isViewerStartAction && isHostManagedToolWindowViewerAction && preserveViewerWindowState)
    {
        // release_contract: preserveViewerWindowState は通常ウィンドウ化抑制であり、retuneExistingViewer の明示要求とは分離する。
        // AIrCon管理viewerが生存している場合でも、後段の internal retune は同一BonDriver/DID内に限定する。
        log.Add("PLUGIN_ACTION_VIEWER_DEFAULT", pluginName, $"action={SafePluginActionValue(action)} result=SKIPPED defaultRetuneExistingViewer=False windowId={SafePluginActionValue(requestedWindowId)} reason=preserve_window_state_is_not_retune_flag rule=release_contract");
    }
    if (!string.IsNullOrWhiteSpace(safeEvent))
    {
        var missingTriplet = string.Join(",", new[]
        {
            string.IsNullOrWhiteSpace(ReadPayload(payload, "NetworkId", "networkId", "network_id", "network-id", "nid")) ? "networkId" : string.Empty,
            string.IsNullOrWhiteSpace(ReadPayload(payload, "TransportStreamId", "transportStreamId", "transport_stream_id", "transport-stream-id", "tsid")) ? "transportStreamId" : string.Empty,
            string.IsNullOrWhiteSpace(ReadPayload(payload, "ServiceId", "serviceId", "service_id", "service-id", "sid")) ? "serviceId" : string.Empty
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        log.Add("PLUGIN_SAFE_EVENT", pluginName, $"event={SafePluginActionValue(safeEvent)} action={SafePluginActionValue(action)} safeEventAction={SafePluginActionValue(safeEventAction)} result=RECEIVED source={SafePluginActionValue(safeEventSource)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(route)} windowId={SafePluginActionValue(requestedWindowId)} safeEventWindowId={SafePluginActionValue(safeEventWindowId)} responseMode={SafePluginActionValue(responseMode)} refreshAfter={requestedRefreshAfter} refreshTarget={SafePluginActionValue(requestedRefreshTarget)} refreshScrollTarget={SafePluginActionValue(requestedRefreshScrollTarget)} refreshScrollMode={SafePluginActionValue(requestedRefreshScrollMode)} missingPayload={SafePluginActionValue(string.IsNullOrWhiteSpace(missingTriplet) ? "-" : missingTriplet)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        log.Add("PLUGIN_SAFE_EVENT_PAYLOAD_AUDIT", pluginName, $"result=RECEIVED action={SafePluginActionValue(action)} queryKeys={SafePluginActionValue(FormatPluginQueryKeys(http))} payloadKeys={SafePluginActionValue(FormatPluginPayloadKeys(payload))} networkId={ReadUShortPayload(payload, "NetworkId", "networkId", "network_id", "network-id", "nid")} transportStreamId={ReadUShortPayload(payload, "TransportStreamId", "transportStreamId", "transport_stream_id", "transport-stream-id", "tsid")} serviceId={ReadUShortPayload(payload, "ServiceId", "serviceId", "service_id", "service-id", "sid")} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
    }

    if (plugin is null)
    {
        log.Add("PLUGIN_ACTION", "DENY", $"plugin={SafePluginActionValue(pluginId)} action={SafePluginActionValue(action)} result=DENIED reason=plugin_not_found endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginActionDispatchError(new PluginActionResult { Success = false, Message = "Plugin not found.", Diagnostics = "plugin_not_found" }, responseMode, requestedWindowId, StatusCodes.Status404NotFound);
    }

    var token = !string.IsNullOrWhiteSpace(request.ActionToken) ? request.ActionToken : request.Token;
    var pluginActionIdForToken = GetPluginActionIdentity(plugin, route);
    if (!ValidatePluginActionTokenOrRecoverHostWindow(actionTokens, windows, token, pluginActionIdForToken, route, pluginName, action, requestedWindowId, safeEventWindowId, http.Path.Value ?? string.Empty, "action_dispatch", log, out var tokenReason))
    {
        if (!string.IsNullOrWhiteSpace(safeEvent))
            log.Add("PLUGIN_SAFE_EVENT", pluginName, $"event={SafePluginActionValue(safeEvent)} action={SafePluginActionValue(action)} result=DENIED reason={tokenReason} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(route)} windowId={SafePluginActionValue(requestedWindowId)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason={tokenReason} windowId={SafePluginActionValue(requestedWindowId)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginActionDispatchError(new PluginActionResult { Success = false, Message = "Invalid plugin action token.", Diagnostics = tokenReason }, responseMode, requestedWindowId, StatusCodes.Status400BadRequest);
    }

    if (!actionAllowed)
    {
        log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason=missing_ControlViewer_permission endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        return BuildPluginActionDispatchError(new PluginActionResult { Success = false, Message = "ControlViewer permission is required.", Diagnostics = "missing_ControlViewer_permission" }, responseMode, requestedWindowId, StatusCodes.Status403Forbidden);
    }

    if (action.Equals("viewerStart", StringComparison.OrdinalIgnoreCase) || action.Equals("RequestViewerStart", StringComparison.OrdinalIgnoreCase))
    {
        var nid = ReadUShortPayload(payload, "NetworkId", "networkId", "network_id", "network-id", "nid");
        var tsid = ReadUShortPayload(payload, "TransportStreamId", "transportStreamId", "transport_stream_id", "transport-stream-id", "tsid");
        var sid = ReadUShortPayload(payload, "ServiceId", "serviceId", "service_id", "service-id", "sid");
        var serviceName = ReadPayload(payload, "ServiceName", "serviceName", "service", "channelName", "name");
        var groupHint = ReadPayload(payload, "Group", "group", "tunerGroup", "TunerGroup", "allocationGroup", "AllocationGroup", "broadcastGroup", "BroadcastGroup", "programGuideFilterGroup", "ProgramGuideFilterGroup");
        var preferredTunerName = ReadPayload(payload, "preferredTunerName", "PreferredTunerName");
        var preferredDid = ReadPayload(payload, "preferredDid", "PreferredDid");
        var preferredSlot = ReadIntPayload(payload, "preferredSlot", "PreferredSlot");
        var requestedViewerProfileRaw = ReadPayload(payload, "viewerProfile", "ViewerProfile", "viewer_profile", "viewer-profile", "viewerProfileId", "ViewerProfileId", "vtuner", "vTuner", "viewer", "profile");
        var requestedViewerProfile = ViewerProfileContract.ResolveRequestedProfile(requestedViewerProfileRaw, tvTestOptions.Value, ini, tunerProfiles);
        var viewerProfilePathKey = ViewerProfileContract.TvTestPathKeyForResolvedProfile(requestedViewerProfile);

        if (!requestedViewerProfile.Enabled)
        {
            var denied = new PluginActionResult { Success = false, Message = "Requested viewer profile is not configured.", Diagnostics = $"state=denied;errorCode=viewerProfileUnavailable;viewerProfile={requestedViewerProfile.Id}" };
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=DENIED reason=viewerProfileUnavailable viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, denied, "denied", "viewerProfileUnavailable", responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart");
        }

        if (nid == 0 || tsid == 0 || sid == 0)
        {
            var denied = new PluginActionResult { Success = false, Message = "ViewerStart payload is incomplete.", Diagnostics = $"state=denied;errorCode=missingViewerPayload;networkId={nid};transportStreamId={tsid};serviceId={sid}" };
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=DENIED reason=missingViewerPayload networkId={nid} transportStreamId={tsid} serviceId={sid} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, denied, "denied", "missingViewerPayload", responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", networkId: nid, transportStreamId: tsid, serviceId: sid);
        }

        var channelMap = channelLoader.Load();
        var channel = channelMap.Targets.FirstOrDefault(c =>
            c.OriginalNetworkId == nid && c.TransportStreamId == tsid && c.ServiceId == sid);
        if (channel is null)
        {
            var denied = new PluginActionResult { Success = false, Message = "Viewer target channel was not found in TvAIr channel map.", Diagnostics = $"state=denied;errorCode=channelNotFound;networkId={nid};transportStreamId={tsid};serviceId={sid}" };
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=DENIED reason=channel_not_found nid={nid} tsid={tsid} sid={sid} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, denied, "denied", "channelNotFound", responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", networkId: nid, transportStreamId: tsid, serviceId: sid);
        }

        var group = !string.IsNullOrWhiteSpace(channel.Group) ? channel.Group : groupHint ?? string.Empty;
        if (!ViewerProfileContract.ProfileSupportsGroup(requestedViewerProfile, group))
        {
            var availableGroups = string.Join(",", requestedViewerProfile.AvailableGroups ?? Array.Empty<string>());
            var denied = new PluginActionResult { Success = false, Message = "Requested viewer profile has no viewer tuner for this broadcast group.", Diagnostics = $"state=denied;errorCode=viewerProfileGroupUnavailable;viewerProfile={requestedViewerProfile.Id};group={group};availableGroups={availableGroups}" };
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=DENIED reason=viewerProfileGroupUnavailable viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} group={SafePluginActionValue(group)} availableGroups={SafePluginActionValue(availableGroups)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, denied, "denied", "viewerProfileGroupUnavailable", responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", networkId: nid, transportStreamId: tsid, serviceId: sid);
        }
        var requestedServiceName = serviceName;
        serviceName = string.IsNullOrWhiteSpace(serviceName) ? channel.Name : serviceName;
        var requestedChspace = ReadIntPayload(payload, "channelSpace", "ChannelSpace", "chspace", "ChSpace");
        var requestedChi = ReadIntPayload(payload, "channelIndex", "ChannelIndex", "chi", "Chi", "channel", "Channel");
        var requestedProgramGuideGroup = ReadPayload(payload, "programGuideFilterGroup", "ProgramGuideFilterGroup");
        var requestedBroadcastGroup = ReadPayload(payload, "broadcastGroup", "BroadcastGroup");
        var sameTransportServices = channelMap.Targets
            .Where(c => c.OriginalNetworkId == nid && c.TransportStreamId == tsid)
            .OrderBy(c => c.ServiceId)
            .ToList();
        var sameTransportServiceIds = string.Join(",", sameTransportServices.Select(c => c.ServiceId.ToString(CultureInfo.InvariantCulture)));
        var selectedChannelSource = "ProgramGuideProjectionTriplet(.ch2/chset resolved)";
        var baseViewerChannelArgument = $"/chspace {channel.ResolvedSpace} /chi {channel.ResolvedChannelIndex}";
        var viewerChannelArgument = $"{baseViewerChannelArgument} /sid {sid}";
        var identityArgument = $"/nid {nid} /tsid {tsid} /sid {sid}";
        var viewerClientId = ViewerProfileContract.BuildViewerClientId(pluginId, requestedViewerProfile.Id);
        var activeClientLeasesBeforeStart = externalTuners.GetActiveLeases()
            .Where(l => string.Equals(l.ClientId ?? string.Empty, viewerClientId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.AcquiredAt)
            .ToList();
        var stoppedBeforeStartPids = new HashSet<int>();

        ExternalTunerLeaseDto? existingManagedViewer = null;
        var existingManagedViewerPid = 0;
        foreach (var prior in activeClientLeasesBeforeStart)
        {
            if (!ViewerProfileContract.LeaseMatchesProfile(prior, requestedViewerProfile))
                continue;
            if (prior.ProcessId.HasValue && prior.ProcessId.Value > 0 && IsProcessAlive(prior.ProcessId.Value) &&
                TvAirManagedProcessRegistry.TryGet(prior.ProcessId.Value, out var managedViewerProcess) && managedViewerProcess.IsViewer)
            {
                existingManagedViewer = prior;
                existingManagedViewerPid = prior.ProcessId.Value;
                break;
            }
        }

        foreach (var stale in activeClientLeasesBeforeStart)
        {
            var stalePid = stale.ProcessId.GetValueOrDefault();
            if (stalePid > 0 && !IsProcessAlive(stalePid))
            {
                var delayedDeath = ViewerRetuneDelayedDeathAudit.MarkDetectedDead(stalePid);
                log.Add("VIEWER_RETUNE_DELAYED_DEATH_AUDIT", pluginName, $"result={SafePluginActionValue(delayedDeath.Result)} stalePid={stalePid} leaseId={SafePluginActionValue(stale.LeaseId)} lastRetuneAt={SafePluginActionValue(delayedDeath.LastRetuneAtText)} detectedDeadAt={SafePluginActionValue(delayedDeath.DetectedDeadAtText)} elapsedSinceRetuneMs={SafePluginActionValue(delayedDeath.ElapsedMsText)} lastRetuneNid={SafePluginActionValue(delayedDeath.NetworkIdText)} lastRetuneTsid={SafePluginActionValue(delayedDeath.TransportStreamIdText)} lastRetuneSid={SafePluginActionValue(delayedDeath.ServiceIdText)} lastRetuneGroup={SafePluginActionValue(delayedDeath.Group)} lastRetuneDid={SafePluginActionValue(delayedDeath.Did)} lastRetuneBonDriver={SafePluginActionValue(delayedDeath.BonDriver)} reason=existing_process_not_alive_before_light_retune rule=release_contract");
                externalTuners.Release(stale.LeaseId, $"Plugin:{pluginName}:stale_viewer_lease_cleanup_before_start");
                log.Add("VIEWER_INTERNAL_RETUNE", pluginName, $"result=STALE_LEASE_RELEASED leaseId={SafePluginActionValue(stale.LeaseId)} stalePid={stalePid} reason=existing_process_not_alive_before_light_retune rule=release_contract");
            }
        }

        var internalRetunePreferred = existingManagedViewer is not null && existingManagedViewerPid > 0;
        log.Add("VIEWER_INTERNAL_RETUNE_DECISION", pluginName,
            $"result={(internalRetunePreferred ? "PREFERRED" : "NEW_VIEWER_REQUIRED")} reason={(internalRetunePreferred ? "existing_tvair_managed_viewer_alive" : "no_alive_tvair_managed_viewer")} sameClient=True viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} tvTestPathKey={SafePluginActionValue(viewerProfilePathKey)} existingPid={(existingManagedViewerPid > 0 ? existingManagedViewerPid.ToString(CultureInfo.InvariantCulture) : "-")} existingGroup={SafePluginActionValue(existingManagedViewer?.Group)} requestedGroup={SafePluginActionValue(group)} existingDid={SafePluginActionValue(existingManagedViewer?.Did)} requestedNid={nid} requestedTsid={tsid} requestedSid={sid} requestedChspace={channel.ResolvedSpace} requestedChi={channel.ResolvedChannelIndex} processRestartPreferred=False retuneExistingViewerPayload={retuneExistingViewer} preserveViewerWindowState={preserveViewerWindowState} rule=release_contract");

        log.Add("VIEWER_START_REQUEST", pluginName, $"requestedServiceName={SafePluginActionValue(requestedServiceName)} requestedNid={nid} requestedTsid={tsid} requestedSid={sid} requestedChspace={(requestedChspace.HasValue ? requestedChspace.Value.ToString(CultureInfo.InvariantCulture) : "-")} requestedChi={(requestedChi.HasValue ? requestedChi.Value.ToString(CultureInfo.InvariantCulture) : "-")} requestedProgramGuideGroup={SafePluginActionValue(requestedProgramGuideGroup)} requestedBroadcastGroup={SafePluginActionValue(requestedBroadcastGroup)} resolvedServiceName={SafePluginActionValue(channel.Name)} resolvedNid={channel.OriginalNetworkId} resolvedTsid={channel.TransportStreamId} resolvedSid={channel.ServiceId} resolvedChspace={channel.ResolvedSpace} resolvedChi={channel.ResolvedChannelIndex} group={SafePluginActionValue(group)} viewerChannelArgument={SafePluginActionValue(viewerChannelArgument)} identityArgument={SafePluginActionValue(identityArgument)} selectedChannelSource={SafePluginActionValue(selectedChannelSource)} sameTransportServiceCount={sameTransportServices.Count} sameTransportServiceIds={SafePluginActionValue(sameTransportServiceIds)} requestedWindowId={SafePluginActionValue(requestedWindowId)} viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} tvTestPathKey={SafePluginActionValue(viewerProfilePathKey)} contract=viewer_start_triplet_sid_launch_profile_scoped rule=release_contract");
        if (!string.IsNullOrWhiteSpace(preferredTunerName) || !string.IsNullOrWhiteSpace(preferredDid) || preferredSlot.HasValue)
        {
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart preferredTunerContract=ignored_light_api preferredTunerName={SafePluginActionValue(preferredTunerName)} preferredDid={SafePluginActionValue(preferredDid)} preferredSlot={(preferredSlot.HasValue ? preferredSlot.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")} rule=release_contract");
        }

        var lease = externalTuners.Request(new ExternalTunerLeaseRequest
        {
            Group = group,
            RequiredGroup = group,
            Source = $"Plugin:{pluginName}",
            ClientId = viewerClientId,
            Note = $"viewerStart service={serviceName} nid={nid} tsid={tsid} sid={sid}",
            NetworkId = nid,
            TransportStreamId = tsid,
            ServiceId = sid,
            ChannelSpace = channel.ResolvedSpace,
            ChannelIndex = channel.ResolvedChannelIndex,
            ViewerProfileId = requestedViewerProfile.Id,
            ViewerProfileName = requestedViewerProfile.Name,
            TvTestPathKey = viewerProfilePathKey,
            ViewerProfileFrameIndex = requestedViewerProfile.TvTestFrameIndex
        });

        if (!lease.Success || lease.Lease is null)
        {
            var reason = string.IsNullOrWhiteSpace(lease.Reason) ? "tunerUnavailable" : lease.Reason!;
            var denied = new PluginActionResult { Success = false, Message = "No viewer tuner is available.", Diagnostics = $"state=denied;errorCode={reason};status={lease.Status}" };
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=DENIED reason={SafePluginActionValue(reason)} service={SafePluginActionValue(serviceName)} group={SafePluginActionValue(group)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, denied, "denied", reason, responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", networkId: nid, transportStreamId: tsid, serviceId: sid);
        }

        var leaseId = lease.Lease.LeaseId;
        var existingViewerGroup = NormalizePluginAllocationGroup(existingManagedViewer?.Group);
        var requestedLeaseGroup = NormalizePluginAllocationGroup(lease.Lease.Group);
        var sameRetuneGroup = internalRetunePreferred && string.Equals(existingViewerGroup, requestedLeaseGroup, StringComparison.OrdinalIgnoreCase);
        var sameRetuneDid = internalRetunePreferred && string.Equals((existingManagedViewer?.Did ?? string.Empty).Trim(), (lease.Lease.Did ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        var sameRetuneBonDriver = internalRetunePreferred && string.Equals(System.IO.Path.GetFileName(existingManagedViewer?.BonDriverFileName ?? string.Empty), System.IO.Path.GetFileName(lease.Lease.BonDriverFileName ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        // release_contract: TVTest1/TVTest2 の意味を「TvAIrが最初に紐付けた viewerProfile 専属PID」として扱う。
        // TVTest の /s は PID 指定ではないが、コマンド投入前に対象 profile の所有PIDを前面化し、
        // アクティブウィンドウへ吸われる環境でも「選択中TVTest枠」を受け口にする。
        // exe名変更/ini複製はタスクバーアイコンを増やすため採用しない。
        var retuneScopeStable = internalRetunePreferred && sameRetuneDid && sameRetuneBonDriver;
        var pidScopedRetuneAvailable = false;
        var internalRetuneAllowed = internalRetunePreferred;
        var internalRetuneGuardReason = internalRetunePreferred
            ? "profile_owned_pid_foreground_binding"
            : "no_alive_tvair_managed_viewer";
        if (internalRetunePreferred)
        {
            log.Add("VIEWER_RETUNE_SCOPE_DECISION", pluginName, $"result=ALLOW_PROFILE_PID_BINDING reason={SafePluginActionValue(internalRetuneGuardReason)} viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} existingPid={existingManagedViewerPid} existingGroup={SafePluginActionValue(existingViewerGroup)} requestedGroup={SafePluginActionValue(requestedLeaseGroup)} sameGroup={sameRetuneGroup} sameDid={sameRetuneDid} sameBonDriver={sameRetuneBonDriver} existingDid={SafePluginActionValue(existingManagedViewer?.Did)} requestedDid={SafePluginActionValue(lease.Lease.Did)} existingBonDriver={SafePluginActionValue(existingManagedViewer?.BonDriverFileName)} requestedBonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} retuneCommandScope=tvtest_single_instance_target_foreground_binding pidScopedRetuneAvailable={pidScopedRetuneAvailable} retuneScopeStable={retuneScopeStable} processRestartRequiredByScope=False preserveViewerWindowState={preserveViewerWindowState} policy=profile_owned_pid_binding_no_exe_rename rule=release_contract");
            log.Add("VIEWER_INTERNAL_RETUNE_GUARD", pluginName, $"result=ALLOW reason={SafePluginActionValue(internalRetuneGuardReason)} existingPid={existingManagedViewerPid} existingGroup={SafePluginActionValue(existingViewerGroup)} requestedGroup={SafePluginActionValue(requestedLeaseGroup)} sameGroup={sameRetuneGroup} sameDid={sameRetuneDid} sameBonDriver={sameRetuneBonDriver} existingDid={SafePluginActionValue(existingManagedViewer?.Did)} requestedDid={SafePluginActionValue(lease.Lease.Did)} existingBonDriver={SafePluginActionValue(existingManagedViewer?.BonDriverFileName)} requestedBonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} policy=foreground_target_before_unscoped_command noExeRename=True noIniClone=True rule=release_contract");
        }
        ViewerWindowStateSnapshot? restartFallbackWindowState = null;
        if (internalRetunePreferred && !internalRetuneAllowed && existingManagedViewerPid > 0 && preserveViewerWindowState)
        {
            restartFallbackWindowState = tvTestLauncher.CaptureViewerWindowState(existingManagedViewerPid, "viewerStart_scope_guard_restart_before_stop");
        }
        if (lease.Reused && lease.Lease.ProcessId.HasValue && lease.Lease.ProcessId.Value > 0)
        {
            var previousPid = lease.Lease.ProcessId.Value;
            if (internalRetuneAllowed && previousPid == existingManagedViewerPid)
            {
                log.Add("VIEWER_START_PREVIOUS_VIEWER_STOP", pluginName, $"action=viewerStart leaseId={SafePluginActionValue(leaseId)} previousPid={previousPid} stopSkipped=True clientId={SafePluginActionValue(viewerClientId)} reusedLease=True reason=internal_retune_keeps_existing_viewer_process beforeNewLaunch=False rule=release_contract");
            }
            else if (stoppedBeforeStartPids.Add(previousPid))
            {
                var stop = tvTestLauncher.StopManagedViewerProcess(previousPid, internalRetunePreferred && !internalRetuneAllowed ? "viewerStart_scope_guard_profile_restart" : "viewerStart_reuse_previous_pid_guard");
                log.Add("VIEWER_START_PREVIOUS_VIEWER_STOP", pluginName, $"action=viewerStart leaseId={SafePluginActionValue(leaseId)} previousPid={previousPid} stopSuccess={stop.Success} stopMessage={SafePluginActionValue(stop.Message)} clientId={SafePluginActionValue(viewerClientId)} reusedLease=True reason={(internalRetunePreferred && !internalRetuneAllowed ? "scope_guard_profile_restart" : "reuse_previous_pid_guard")} beforeNewLaunch=True rule=release_contract");
            }
        }
        var viewerStartExternalSnapshot = TvTestProcessAuditor.Capture(log, "VIEWER_START_LIGHT_EXTERNAL_SCAN", emitLegacyEvents: false);
        var unknownExternalLive = viewerStartExternalSnapshot.Processes
            .Where(p => !p.IsTvAirManaged && p.IsLiveViewing && string.IsNullOrWhiteSpace(p.Did))
            .ToList();
        var blocking = viewerStartExternalSnapshot.Processes.FirstOrDefault(p =>
            !p.IsTvAirManaged && p.IsLiveViewing &&
            string.Equals((p.Did ?? string.Empty).Trim(), lease.Lease.Did, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(System.IO.Path.GetFileName(p.BonDriverFileName ?? string.Empty), System.IO.Path.GetFileName(lease.Lease.BonDriverFileName ?? string.Empty), StringComparison.OrdinalIgnoreCase));
        if (blocking is null && unknownExternalLive.Count > 0)
        {
            log.Add("VIEWER_START_EXTERNAL_IDENTITY_SNAPSHOT", pluginName,
                $"result=INFO unknownExternalLive={unknownExternalLive.Count} targetDid={SafePluginActionValue(lease.Lease.Did)} targetBonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} action=do_not_deny_viewer_start_on_unknown_external_identity policy=snapshot_only_recording_slot_guard_no_external_process_touch rule=release_contract");
        }
        if (blocking is not null)
        {
            externalTuners.Release(leaseId, $"Plugin:{pluginName}:external_did_guard");
            var message = $"External TVTest is using DID {lease.Lease.Did}.";
            var denied = new PluginActionResult { Success = false, Message = message, Diagnostics = $"state=denied;errorCode=externalViewerOccupyingDid;tuner={lease.Lease.TunerName};did={lease.Lease.Did};bonDriver={lease.Lease.BonDriverFileName};blockingPid={blocking.ProcessId}" };
            log.Add("VIEWER_START_EXTERNAL_DID_GUARD", pluginName, $"result=DENIED reason=externalViewerOccupyingDid tuner={SafePluginActionValue(lease.Lease.TunerName)} did={SafePluginActionValue(lease.Lease.Did)} bonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} blockingPid={blocking.ProcessId} action=release_viewer_lease_no_external_process_touch rule=release_contract");
            log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=DENIED reason=externalViewerOccupyingDid service={SafePluginActionValue(serviceName)} group={SafePluginActionValue(group)} tuner={SafePluginActionValue(lease.Lease.TunerName)} did={SafePluginActionValue(lease.Lease.Did)} blockingPid={blocking.ProcessId} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, denied, "denied", "externalViewerOccupyingDid", responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", leaseId, lease.Lease.TunerName, lease.Lease.Did, lease.Lease.BonDriverFileName, blocking.ProcessId, nid, tsid, sid);
        }

        var leaseBonDriverFileName = lease.Lease.BonDriverFileName ?? string.Empty;
        var leaseDid = lease.Lease.Did ?? string.Empty;

        if (internalRetunePreferred && !internalRetuneAllowed && existingManagedViewerPid > 0 && stoppedBeforeStartPids.Add(existingManagedViewerPid))
        {
            var stop = tvTestLauncher.StopManagedViewerProcess(existingManagedViewerPid, "viewerStart_scope_guard_previous_viewer_stop");
            log.Add("VIEWER_START_PREVIOUS_VIEWER_STOP", pluginName, $"action=viewerStart leaseId={SafePluginActionValue(leaseId)} previousPid={existingManagedViewerPid} stopSuccess={stop.Success} stopMessage={SafePluginActionValue(stop.Message)} clientId={SafePluginActionValue(viewerClientId)} reusedLease={lease.Reused} reason=scope_guard_previous_viewer_stop beforeNewLaunch=True afterExternalGuard=True guardReason={SafePluginActionValue(internalRetuneGuardReason)} restoreStateCaptured={(restartFallbackWindowState?.Captured == true)} rule=release_contract");
        }
        if (internalRetuneAllowed && existingManagedViewer is not null && existingManagedViewerPid > 0)
        {
            log.Add("VIEWER_CHANNEL_ARGUMENT", pluginName, $"source=ProgramGuideProjectionTriplet finalChannelArgument={SafePluginActionValue(viewerChannelArgument)} identityArgument={SafePluginActionValue(identityArgument)} requestedServiceName={SafePluginActionValue(requestedServiceName)} resolvedServiceName={SafePluginActionValue(channel.Name)} nid={nid} tsid={tsid} sid={sid} chspace={channel.ResolvedSpace} chi={channel.ResolvedChannelIndex} sameTransportServiceCount={sameTransportServices.Count} sameTransportServiceIds={SafePluginActionValue(sameTransportServiceIds)} selectedChannelSource={SafePluginActionValue(selectedChannelSource)} preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafePluginActionValue(viewerActivation)} internalRetunePreferred=True internalRetuneAllowed=True targetBonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} targetDid={SafePluginActionValue(lease.Lease.Did)} rule=release_contract");
            var targetPrepared = tvTestLauncher.PrepareViewerProfileCommandTarget(existingManagedViewerPid, requestedViewerProfile.Id, "viewerStart_before_single_instance_command");
            log.Add("VIEWER_PROFILE_PID_BIND", pluginName, $"result={(targetPrepared ? "OK" : "WARN")} action=before_retune viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} existingPid={existingManagedViewerPid} requestedGroup={SafePluginActionValue(requestedLeaseGroup)} requestedDid={SafePluginActionValue(lease.Lease.Did)} requestedBonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} policy=selector_id_to_owned_pid_then_command rule=release_contract");
            var retune = tvTestLauncher.RetuneExistingViewer(existingManagedViewerPid, leaseBonDriverFileName, leaseDid, viewerChannelArgument, true, string.IsNullOrWhiteSpace(viewerActivation) ? "preserve" : viewerActivation);
            if (retune.Success)
            {
                ViewerRetuneDelayedDeathAudit.MarkRetuned(existingManagedViewerPid, nid, tsid, sid, requestedLeaseGroup, leaseDid, leaseBonDriverFileName);
                externalTuners.AttachViewerProcess(leaseId, existingManagedViewerPid, viewerChannelArgument, "retuned", "reused", "internalRetuneCommandSent", "preserved", nid, tsid, sid, channel.ResolvedSpace, channel.ResolvedChannelIndex);
                var internalRetuneDiag = $"state=retuned;leaseId={leaseId};tuner={lease.Lease.TunerName};did={lease.Lease.Did};bonDriver={lease.Lease.BonDriverFileName};viewerProcessId={existingManagedViewerPid};channelArgument={viewerChannelArgument};identityArgument={identityArgument};selectedChannelSource={selectedChannelSource};sameTransportServiceIds={sameTransportServiceIds};launchResult=reused;tuneResult=internalRetuneCommandSent;activateResult=preserved;processRestarted=False;preserveViewerWindowState=True;viewerActivation={viewerActivation};internalRetune=True;retuneScope=profileOwnedPidForegroundBinding";
                externalTuners.SetLastViewerActionResult(pluginName, "viewerStart", true, "retuned", "-", "Viewer retuned inside existing TvAIr managed TVTest.", leaseId, lease.Lease.TunerName, lease.Lease.Did, lease.Lease.BonDriverFileName, existingManagedViewerPid, nid, tsid, sid, internalRetuneDiag);
                log.Add("VIEWER_RETUNE_SURVIVAL", pluginName, $"result=OK source=viewerStart_action_context existingPid={existingManagedViewerPid} leaseId={SafePluginActionValue(leaseId)} previousNid={(existingManagedViewer.NetworkId.HasValue ? existingManagedViewer.NetworkId.Value.ToString(CultureInfo.InvariantCulture) : "-")} previousTsid={(existingManagedViewer.TransportStreamId.HasValue ? existingManagedViewer.TransportStreamId.Value.ToString(CultureInfo.InvariantCulture) : "-")} previousSid={(existingManagedViewer.ServiceId.HasValue ? existingManagedViewer.ServiceId.Value.ToString(CultureInfo.InvariantCulture) : "-")} requestedNid={nid} requestedTsid={tsid} requestedSid={sid} requestedGroup={SafePluginActionValue(requestedLeaseGroup)} requestedDid={SafePluginActionValue(lease.Lease.Did)} requestedBonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} processRestarted=False retuneScope=profileOwnedPidForegroundBinding rule=release_contract");
                log.Add("VIEWER_INTERNAL_RETUNE", pluginName, $"result=OK method=tvtest_single_instance_commandline pid={existingManagedViewerPid} leaseId={SafePluginActionValue(leaseId)} previousLeaseId={SafePluginActionValue(existingManagedViewer.LeaseId)} previousNid={(existingManagedViewer.NetworkId.HasValue ? existingManagedViewer.NetworkId.Value.ToString(CultureInfo.InvariantCulture) : "-")} previousTsid={(existingManagedViewer.TransportStreamId.HasValue ? existingManagedViewer.TransportStreamId.Value.ToString(CultureInfo.InvariantCulture) : "-")} previousSid={(existingManagedViewer.ServiceId.HasValue ? existingManagedViewer.ServiceId.Value.ToString(CultureInfo.InvariantCulture) : "-")} requestedNid={nid} requestedTsid={tsid} requestedSid={sid} requestedChspace={channel.ResolvedSpace} requestedChi={channel.ResolvedChannelIndex} bonDriverChanged={!string.Equals(System.IO.Path.GetFileName(existingManagedViewer.BonDriverFileName), System.IO.Path.GetFileName(lease.Lease.BonDriverFileName), StringComparison.OrdinalIgnoreCase)} didChanged={!string.Equals(existingManagedViewer.Did, lease.Lease.Did, StringComparison.OrdinalIgnoreCase)} processRestarted=False retuneScope=profileOwnedPidForegroundBinding rule=release_contract");
                if (!string.IsNullOrWhiteSpace(safeEvent))
                    log.Add("PLUGIN_SAFE_EVENT", pluginName, $"event={SafePluginActionValue(safeEvent)} action=viewerStart result=POSTED_TO_VIEWER_INTERNAL_RETUNE service={SafePluginActionValue(serviceName)} nid={nid} tsid={tsid} sid={sid} windowId={SafePluginActionValue(requestedWindowId)} responseMode={SafePluginActionValue(responseMode)} state=retuned rule=release_contract");
                log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=ACCEPTED state=retuned service={SafePluginActionValue(serviceName)} nid={nid} tsid={tsid} sid={sid} group={SafePluginActionValue(group)} tuner={SafePluginActionValue(lease.Lease.TunerName)} did={SafePluginActionValue(lease.Lease.Did)} pid={existingManagedViewerPid} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
                var retuneActionResult = new PluginActionResult { Success = true, Message = "Viewer retuned inside existing TvAIr managed TVTest.", Diagnostics = internalRetuneDiag };
                return BuildPluginViewerActionResponse(retuneActionResult, responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", requestedRefreshAfter, toolWindows, http, requestedRefreshScrollTarget, requestedRefreshScrollMode);
            }

            var retuneFailureMessage = retune.Message ?? string.Empty;
            var retuneProcessLost = retuneFailureMessage.Contains("disappeared", StringComparison.OrdinalIgnoreCase)
                || retuneFailureMessage.Contains("exited", StringComparison.OrdinalIgnoreCase)
                || retuneFailureMessage.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || retuneFailureMessage.Contains("lost", StringComparison.OrdinalIgnoreCase);
            log.Add("VIEWER_INTERNAL_RETUNE", pluginName, $"result=FAILED method=tvtest_single_instance_commandline pid={existingManagedViewerPid} leaseId={SafePluginActionValue(leaseId)} requestedNid={nid} requestedTsid={tsid} requestedSid={sid} message={SafePluginActionValue(retune.Message)} processLost={retuneProcessLost} action={(retuneProcessLost ? "stale_release_then_restart_recovery" : "deny_without_restart")} reason=internal_retune_failed rule=release_contract");
            if (retuneProcessLost)
            {
                TvAirManagedProcessRegistry.Unregister(existingManagedViewerPid);
                externalTuners.SetViewerState(leaseId, "starting", "restartRecovery", "retuneProcessLost", "requested", "processLost");
                goto StartNewViewer;
            }
            if (lease.Reused && string.Equals(existingManagedViewer.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
            {
                externalTuners.SetViewerState(leaseId, "active", "reused", "retuneFailed", "preserved", "notNeeded");
                log.Add("VIEWER_INTERNAL_RETUNE", pluginName, $"result=KEEP_EXISTING_LEASE leaseId={SafePluginActionValue(leaseId)} pid={existingManagedViewerPid} reason=retune_failed_same_lease_no_restart rule=release_contract");
            }
            else
            {
                externalTuners.Release(leaseId, $"Plugin:{pluginName}:internal_retune_failed_no_restart");
            }
            var failedRetune = new PluginActionResult { Success = false, Message = "Existing TVTest retune failed. TvAIr kept the existing viewer process alive and did not restart TVTest.", Diagnostics = $"state=denied;errorCode=internalRetuneFailed;viewerProcessId={existingManagedViewerPid};message={retune.Message}" };
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, failedRetune, "denied", "internalRetuneFailed", responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", leaseId, lease.Lease.TunerName, lease.Lease.Did, lease.Lease.BonDriverFileName, existingManagedViewerPid, nid, tsid, sid);
        }

StartNewViewer:
        log.Add("VIEWER_CHANNEL_ARGUMENT", pluginName, $"source=ProgramGuideProjectionTriplet finalChannelArgument={SafePluginActionValue(viewerChannelArgument)} identityArgument={SafePluginActionValue(identityArgument)} requestedServiceName={SafePluginActionValue(requestedServiceName)} resolvedServiceName={SafePluginActionValue(channel.Name)} nid={nid} tsid={tsid} sid={sid} chspace={channel.ResolvedSpace} chi={channel.ResolvedChannelIndex} sameTransportServiceCount={sameTransportServices.Count} sameTransportServiceIds={SafePluginActionValue(sameTransportServiceIds)} selectedChannelSource={SafePluginActionValue(selectedChannelSource)} preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafePluginActionValue(viewerActivation)} viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} tvTestPathKey={SafePluginActionValue(viewerProfilePathKey)} internalRetunePreferred={internalRetunePreferred} internalRetuneAllowed={internalRetuneAllowed} launchReason={(internalRetunePreferred && !internalRetuneAllowed ? "scope_guard_restart" : internalRetunePreferred ? "retune_failed_or_no_alive_viewer" : "no_alive_viewer")} rule=release_contract");
        var restartReason = internalRetunePreferred && !internalRetuneAllowed ? "scope_guard_unscoped_retune_banned" : internalRetunePreferred ? "retune_failed_restart_recovery" : "no_alive_tvair_managed_viewer";
        var restartPreviousPid = internalRetunePreferred && existingManagedViewerPid > 0 ? existingManagedViewerPid.ToString(CultureInfo.InvariantCulture) : "-";
        log.Add("VIEWER_PROCESS_RESTART", pluginName, $"reason={SafePluginActionValue(restartReason)} previousPid={restartPreviousPid} newPid=- unavoidable={(internalRetunePreferred && !internalRetuneAllowed ? "True" : internalRetunePreferred ? "False" : "True")} processRestarted=True restoreRequested={(restartFallbackWindowState?.Captured == true)} restoreSourcePid={(restartFallbackWindowState?.ProcessId.ToString(CultureInfo.InvariantCulture) ?? "-")} policy=profile_scoped_restart_on_unscoped_retune_ban rule=release_contract");
        var viewerLaunch = tvTestLauncher.StartViewer(leaseBonDriverFileName, leaseDid, viewerChannelArgument, preserveViewerWindowState, viewerActivation, restartFallbackWindowState);
        if (!viewerLaunch.Success)
        {
            externalTuners.Release(leaseId, $"Plugin:{pluginName}:viewer_launch_failed");
            var failed = new PluginActionResult { Success = false, Message = "Failed to launch TVTest viewer.", Diagnostics = $"state=failed;errorCode=viewerLaunchFailed;leaseId={leaseId};tuner={lease.Lease.TunerName};did={lease.Lease.Did};bonDriver={lease.Lease.BonDriverFileName};message={viewerLaunch.Message}" };
            log.Add("VIEWER_START_RESULT", pluginName, $"success=False state=failed errorCode=viewerLaunchFailed leaseId={SafePluginActionValue(leaseId)} tuner={SafePluginActionValue(lease.Lease.TunerName)} did={SafePluginActionValue(lease.Lease.Did)} bonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} nid={nid} tsid={tsid} sid={sid} channelArgument={SafePluginActionValue(viewerChannelArgument)} identityArgument={SafePluginActionValue(identityArgument)} launchResult=failed rollbackResult=released rule=release_contract");
            return BuildPluginViewerExpectedDeniedResponse(externalTuners, failed, "failed", "viewerLaunchFailed", responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", leaseId, lease.Lease.TunerName, lease.Lease.Did, lease.Lease.BonDriverFileName, viewerLaunch.ProcessId, nid, tsid, sid);
        }

        externalTuners.AttachViewerProcess(leaseId, viewerLaunch.ProcessId, viewerChannelArgument, "launched", "started", "argumentPassed", "requested", nid, tsid, sid, channel.ResolvedSpace, channel.ResolvedChannelIndex);
        var diag = $"state=launched;leaseId={leaseId};tuner={lease.Lease.TunerName};did={lease.Lease.Did};bonDriver={lease.Lease.BonDriverFileName};viewerProcessId={viewerLaunch.ProcessId};channelArgument={viewerChannelArgument};identityArgument={identityArgument};selectedChannelSource={selectedChannelSource};sameTransportServiceIds={sameTransportServiceIds};launchResult=started;tuneResult=argumentPassed;activateResult=requested;preserveViewerWindowState={preserveViewerWindowState};viewerActivation={viewerActivation};viewerProfile={requestedViewerProfile.Id};viewerProfileName={requestedViewerProfile.Name};tvTestPathKey={viewerProfilePathKey};restoreWindowStateRequested={(restartFallbackWindowState?.Captured == true)}";
        log.Add("VIEWER_START_RESULT", pluginName, $"success=True state=launched errorCode=- leaseId={SafePluginActionValue(leaseId)} tuner={SafePluginActionValue(lease.Lease.TunerName)} did={SafePluginActionValue(lease.Lease.Did)} bonDriver={SafePluginActionValue(lease.Lease.BonDriverFileName)} pid={viewerLaunch.ProcessId} nid={nid} tsid={tsid} sid={sid} channelArgument={SafePluginActionValue(viewerChannelArgument)} identityArgument={SafePluginActionValue(identityArgument)} launchResult=started tuneResult=argumentPassed activateResult=requested viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} tvTestPathKey={SafePluginActionValue(viewerProfilePathKey)} rule=release_contract");
        if (!string.IsNullOrWhiteSpace(safeEvent))
            log.Add("PLUGIN_SAFE_EVENT", pluginName, $"event={SafePluginActionValue(safeEvent)} action=viewerStart result=POSTED_TO_VIEWER_START service={SafePluginActionValue(serviceName)} nid={nid} tsid={tsid} sid={sid} windowId={SafePluginActionValue(requestedWindowId)} responseMode={SafePluginActionValue(responseMode)} state=launched rule=release_contract");
        log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStart result=ACCEPTED state=launched service={SafePluginActionValue(serviceName)} nid={nid} tsid={tsid} sid={sid} group={SafePluginActionValue(group)} tuner={SafePluginActionValue(lease.Lease.TunerName)} did={SafePluginActionValue(lease.Lease.Did)} pid={viewerLaunch.ProcessId} viewerProfile={SafePluginActionValue(requestedViewerProfile.Id)} viewerProfileName={SafePluginActionValue(requestedViewerProfile.Name)} endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        var actionResult = new PluginActionResult { Success = true, Message = "Viewer launch requested by TvAIr host.", Diagnostics = diag };
        return BuildPluginViewerActionResponse(actionResult, responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStart", requestedRefreshAfter, toolWindows, http, requestedRefreshScrollTarget, requestedRefreshScrollMode);
    }

    if (action.Equals("viewerStop", StringComparison.OrdinalIgnoreCase) || action.Equals("RequestViewerStop", StringComparison.OrdinalIgnoreCase))
    {
        var requestedLeaseId = ReadPayload(payload, "LeaseId", "leaseId", "payload-lease-id", "payload-leaseId");
        var requestedViewerProfileRaw = ReadPayload(payload,
            "viewerProfile", "ViewerProfile", "viewer_profile", "viewer-profile",
            "viewerProfileId", "ViewerProfileId", "selectedViewerProfile", "SelectedViewerProfile",
            "vtuner", "vTuner", "viewer", "profile");
        var requestedViewerProfileId = NormalizeViewerProfileActionId(requestedViewerProfileRaw);
        var hasRequestedViewerProfile = !string.IsNullOrWhiteSpace(requestedViewerProfileId);
        var clientId = hasRequestedViewerProfile
            ? $"{pluginId}:viewer:{requestedViewerProfileId}"
            : ReadPayload(payload, "clientId", "ClientId");
        if (string.IsNullOrWhiteSpace(clientId)) clientId = $"{pluginId}:viewer";

        var stopResolveMode = "none";
        var stopDeniedReason = string.Empty;
        var activeLeasesBeforeStop = externalTuners.GetActiveLeases().ToList();
        ExternalTunerLeaseDto? targetLease = null;

        if (!string.IsNullOrWhiteSpace(requestedLeaseId))
        {
            var leaseById = activeLeasesBeforeStop.FirstOrDefault(x => string.Equals(x.LeaseId, requestedLeaseId, StringComparison.OrdinalIgnoreCase));
            if (leaseById is not null && hasRequestedViewerProfile && !string.Equals(NormalizeViewerProfileActionId(leaseById.ViewerProfileId), requestedViewerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                stopResolveMode = "leaseId_profile_mismatch_denied";
                stopDeniedReason = $"lease_profile_mismatch requestedProfile={requestedViewerProfileId} leaseProfile={NormalizeViewerProfileActionId(leaseById.ViewerProfileId)}";
                log.Add("VIEWER_STOP_PROFILE_GUARD", pluginName, $"result=DENY reason=lease_profile_mismatch requestedLeaseId={SafePluginActionValue(requestedLeaseId)} requestedViewerProfile={SafePluginActionValue(requestedViewerProfileId)} leaseViewerProfile={SafePluginActionValue(leaseById.ViewerProfileId)} leasePid={(leaseById.ProcessId.HasValue ? leaseById.ProcessId.Value.ToString(CultureInfo.InvariantCulture) : "-")} policy=selected_viewer_profile_must_match_lease rule=release_contract");
            }
            else if (leaseById is not null)
            {
                targetLease = leaseById;
                stopResolveMode = hasRequestedViewerProfile ? "leaseId_profile_verified" : "leaseId_no_profile_legacy";
            }
            else if (hasRequestedViewerProfile)
            {
                stopResolveMode = "stale_lease_profile_fallback";
            }
            else
            {
                stopResolveMode = "stale_lease_no_profile_denied";
                stopDeniedReason = "stale_lease_without_viewer_profile";
            }
        }

        if (targetLease is null && string.IsNullOrWhiteSpace(stopDeniedReason) && hasRequestedViewerProfile)
        {
            targetLease = activeLeasesBeforeStop
                .Where(x => string.Equals(NormalizeViewerProfileActionId(x.ViewerProfileId), requestedViewerProfileId, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(x.ClientId, $"{pluginId}:viewer:{requestedViewerProfileId}", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(x.Source, $"Plugin:{pluginName}", StringComparison.OrdinalIgnoreCase)
                         || (x.ClientId ?? string.Empty).StartsWith($"{pluginId}:viewer", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.AcquiredAt)
                .FirstOrDefault();
            if (targetLease is not null)
                stopResolveMode = stopResolveMode == "stale_lease_profile_fallback" ? "stale_lease_profile_resolved" : "viewerProfile";
        }

        if (targetLease is null && string.IsNullOrWhiteSpace(stopDeniedReason))
        {
            stopResolveMode = string.IsNullOrWhiteSpace(stopResolveMode) || stopResolveMode == "none"
                ? "missing_viewer_profile_denied"
                : stopResolveMode;
            stopDeniedReason = hasRequestedViewerProfile ? "viewer_profile_session_not_found" : "missing_viewer_profile_and_lease";
            log.Add("VIEWER_STOP_PROFILE_GUARD", pluginName, $"result=DENY reason={SafePluginActionValue(stopDeniedReason)} requestedLeaseId={SafePluginActionValue(requestedLeaseId)} requestedViewerProfile={SafePluginActionValue(requestedViewerProfileId)} clientId={SafePluginActionValue(clientId)} policy=no_generic_client_or_active_window_fallback rule=release_contract");
        }

        var resolvedLeaseId = targetLease?.LeaseId ?? string.Empty;
        var stopPids = targetLease is not null
            ? BuildManagedViewerStopPidSet(targetLease, includeRegistrySameTuner: false)
            : Array.Empty<int>();
        var stopDiagParts = new List<string>();
        var ok = false;
        var stopDiag = "not_attempted";
        if (targetLease is not null)
        {
            foreach (var pid in stopPids)
            {
                var stop = tvTestLauncher.StopManagedViewerProcess(pid, "viewerStop_profile_bound_cleanup");
                stopDiagParts.Add($"pid={pid}:{stop.Message}");
            }
            stopDiag = stopDiagParts.Count > 0 ? string.Join(",", stopDiagParts) : "no_process";
            ok = !string.IsNullOrWhiteSpace(resolvedLeaseId) && externalTuners.Release(resolvedLeaseId, $"Plugin:{pluginName}:viewerStop:{stopResolveMode}");
        }

        var activeViewerSessionsAfterStop = hasRequestedViewerProfile
            ? externalTuners.GetActiveLeases().Count(l => string.Equals(NormalizeViewerProfileActionId(l.ViewerProfileId), requestedViewerProfileId, StringComparison.OrdinalIgnoreCase))
            : externalTuners.GetActiveLeases().Count(l => string.Equals(l.ClientId ?? string.Empty, clientId, StringComparison.OrdinalIgnoreCase));
        log.Add("VIEWER_STOP_RESOLVE", pluginName, $"action=viewerStop requestedLeaseId={SafePluginActionValue(requestedLeaseId)} requestedViewerProfile={SafePluginActionValue(requestedViewerProfileId)} resolvedLeaseId={SafePluginActionValue(resolvedLeaseId)} resolvedViewerProfile={SafePluginActionValue(targetLease?.ViewerProfileId)} resolveMode={SafePluginActionValue(stopResolveMode)} clientId={SafePluginActionValue(clientId)} currentWindowId={SafePluginActionValue(requestedWindowId)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(route)} resolvedGroup={SafePluginActionValue(targetLease?.Group)} resolvedTuner={SafePluginActionValue(targetLease?.TunerName)} resolvedDid={SafePluginActionValue(targetLease?.Did)} resolvedPid={(targetLease?.ProcessId.HasValue == true ? targetLease.ProcessId.Value.ToString(CultureInfo.InvariantCulture) : "-")} cleanupPids={SafePluginActionValue(string.Join(",", stopPids))} deniedReason={SafePluginActionValue(stopDeniedReason)} result={(ok ? "OK" : "NOT_FOUND")} rule=release_contract");
        log.Add("PLUGIN_ACTION_VIEWER", pluginName, $"action=viewerStop result={(ok ? "OK" : "NOT_FOUND")} requestedLeaseId={SafePluginActionValue(requestedLeaseId)} requestedViewerProfile={SafePluginActionValue(requestedViewerProfileId)} resolvedLeaseId={SafePluginActionValue(resolvedLeaseId)} resolvedViewerProfile={SafePluginActionValue(targetLease?.ViewerProfileId)} leaseResolveMode={SafePluginActionValue(stopResolveMode)} processStop={SafePluginActionValue(stopDiag)} activeViewerSessionsAfterStop={activeViewerSessionsAfterStop} cleanupPids={SafePluginActionValue(string.Join(",", stopPids))} uiStateSource=GetViewerSessions current_active_sessions_only notFoundProcessPolicy=deny_if_profile_or_lease_not_resolved endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
        var actionResult = ok
            ? new PluginActionResult { Success = true, Message = "Viewer lease released.", Diagnostics = $"released;processStop={stopDiag};leaseResolveMode={stopResolveMode};viewerProfile={requestedViewerProfileId}" }
            : new PluginActionResult { Success = false, Message = "Viewer lease not found or viewerProfile mismatch.", Diagnostics = $"lease_not_found_or_profile_mismatch;leaseResolveMode={stopResolveMode};reason={stopDeniedReason};viewerProfile={requestedViewerProfileId}" };
        return ok
            ? BuildPluginViewerActionResponse(actionResult, responseMode, requestedWindowId, requestedRefreshTarget, requestedPreserveScroll, windows, GetPluginActionIdentity(plugin, route), log, pluginName, "viewerStop", requestedRefreshAfter, toolWindows, http, requestedRefreshScrollTarget, requestedRefreshScrollMode)
            : BuildPluginActionDispatchError(actionResult, responseMode, requestedWindowId, StatusCodes.Status404NotFound);
    }

    log.Add("PLUGIN_ACTION", pluginName, $"action={SafePluginActionValue(action)} result=DENIED reason=unsupported_action endpoint={SafePluginActionValue(http.Path.Value)} rule=release_contract");
    return BuildPluginActionDispatchError(new PluginActionResult { Success = false, Message = "Unsupported plugin action.", Diagnostics = "unsupported_action" }, responseMode, requestedWindowId, StatusCodes.Status400BadRequest);
}


static async Task<PluginWindowRequest> ReadPluginWindowRequestAsync(HttpRequest http)
{
    var result = new PluginWindowRequest();
    try
    {
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
        case "routeSegment": case "RouteSegment": case "route": case "Route": result.RouteSegment = value; break;
        case "action": case "Action": result.Action = value; break;
        case "windowId": case "WindowId": result.WindowId = value; break;
        case "title": case "Title": result.Title = value; result.Payload[key] = value; break;
        case "width": case "Width": if (int.TryParse(value, out var w)) { result.Width = Math.Clamp(w, 240, 2400); result.Payload[key] = value; } break;
        case "height": case "Height": if (int.TryParse(value, out var h)) { result.Height = Math.Clamp(h, 240, 1600); result.Payload[key] = value; } break;
        case "minWidth": case "MinWidth": if (int.TryParse(value, out var mw)) { result.MinWidth = Math.Clamp(mw, 160, 2400); result.Payload[key] = value; } break;
        case "minHeight": case "MinHeight": if (int.TryParse(value, out var mh)) { result.MinHeight = Math.Clamp(mh, 160, 1600); result.Payload[key] = value; } break;
        case "resizable": case "Resizable": if (bool.TryParse(value, out var rz)) { result.Resizable = rz; result.Payload[key] = value; } break;
        case "movable": case "Movable": if (bool.TryParse(value, out var mv)) { result.Movable = mv; result.Payload[key] = value; } break;
        case "alwaysOnTop": case "AlwaysOnTop": if (bool.TryParse(value, out var aot)) { result.AlwaysOnTop = aot; result.Payload[key] = value; } break;
        case "contentRoute": case "ContentRoute": result.ContentRoute = value; result.Payload[key] = value; break;
        case "refreshTarget": case "RefreshTarget": result.RefreshTarget = value; break;
        case "target": case "Target": result.Target = value; break;
        case "preserveScroll": case "PreserveScroll": if (bool.TryParse(value, out var ps)) { result.PreserveScroll = ps; result.Payload[key] = value; } break;
        case "refreshScrollTarget": case "RefreshScrollTarget": case "scrollTarget": case "ScrollTarget": case "focusTarget": case "FocusTarget": result.RefreshScrollTarget = value; result.Payload[key] = value; break;
        case "refreshScrollMode": case "RefreshScrollMode": case "scrollMode": case "ScrollMode": result.RefreshScrollMode = value; result.Payload[key] = value; break;
        case "forceReload": case "ForceReload": if (bool.TryParse(value, out var fr)) { result.ForceReload = fr; result.Payload[key] = value; } break;
        case "refreshAfter": case "RefreshAfter": if (bool.TryParse(value, out var ra)) { result.RefreshAfter = ra; result.Payload[key] = value; } break;
        case "reuseExisting": case "ReuseExisting": if (bool.TryParse(value, out var re)) { result.ReuseExisting = re; result.Payload[key] = value; } break;
        case "activateExisting": case "ActivateExisting": if (bool.TryParse(value, out var ae)) { result.ActivateExisting = ae; result.Payload[key] = value; } break;
        case "windowToken": case "WindowToken": result.WindowToken = value; break;
        case "token": case "Token": result.Token = value; break;
        case "responseMode": case "ResponseMode": result.ResponseMode = value; result.Payload[key] = value; break;
        case "returnUrl": case "ReturnUrl": case "redirectBackUrl": case "RedirectBackUrl": case "sourceUrl": case "SourceUrl": result.ReturnUrl = value; result.Payload[key] = value; break;
        default:
            result.Payload[key] = value;
            break;
    }
}


static void NormalizePluginWindowRequestFromPayload(PluginWindowRequest request)
{
    if (string.IsNullOrWhiteSpace(request.PluginId)) request.PluginId = ReadPayload(request.Payload, "pluginId", "PluginId");
    if (string.IsNullOrWhiteSpace(request.RouteSegment)) request.RouteSegment = ReadPayload(request.Payload, "routeSegment", "RouteSegment", "route", "Route");
    if (string.IsNullOrWhiteSpace(request.Action)) request.Action = ReadPayload(request.Payload, "action", "Action");
    if (string.IsNullOrWhiteSpace(request.WindowId)) request.WindowId = ReadPayload(request.Payload, "windowId", "WindowId", "currentWindowId", "CurrentWindowId");
    if (string.IsNullOrWhiteSpace(request.Title)) request.Title = ReadPayload(request.Payload, "title", "Title");
    if (TryReadIntPayload(request.Payload, out var width, "width", "Width")) request.Width = Math.Clamp(width, 240, 2400);
    if (TryReadIntPayload(request.Payload, out var height, "height", "Height")) request.Height = Math.Clamp(height, 240, 1600);
    if (TryReadIntPayload(request.Payload, out var minWidth, "minWidth", "MinWidth")) request.MinWidth = Math.Clamp(minWidth, 160, 2400);
    if (TryReadIntPayload(request.Payload, out var minHeight, "minHeight", "MinHeight")) request.MinHeight = Math.Clamp(minHeight, 160, 1600);
    if (TryReadBoolPayload(request.Payload, out var resizable, "resizable", "Resizable")) request.Resizable = resizable;
    if (TryReadBoolPayload(request.Payload, out var movable, "movable", "Movable")) request.Movable = movable;
    if (TryReadBoolPayload(request.Payload, out var alwaysOnTop, "alwaysOnTop", "AlwaysOnTop")) request.AlwaysOnTop = alwaysOnTop;
    if (string.IsNullOrWhiteSpace(request.ContentRoute)) request.ContentRoute = ReadPayload(request.Payload, "contentRoute", "ContentRoute");
    var target = ReadPayload(request.Payload, "target", "Target", "refreshTarget", "RefreshTarget");
    if (!string.IsNullOrWhiteSpace(target))
    {
        request.Target = target;
        request.RefreshTarget = target;
    }
    if (TryReadBoolPayload(request.Payload, out var preserveScroll, "preserveScroll", "PreserveScroll")) request.PreserveScroll = preserveScroll;
    var refreshScrollTarget = ReadPayload(request.Payload, "refreshScrollTarget", "RefreshScrollTarget", "scrollTarget", "ScrollTarget", "focusTarget", "FocusTarget");
    if (!string.IsNullOrWhiteSpace(refreshScrollTarget)) request.RefreshScrollTarget = NormalizePluginRefreshScrollTarget(refreshScrollTarget);
    var refreshScrollMode = ReadPayload(request.Payload, "refreshScrollMode", "RefreshScrollMode", "scrollMode", "ScrollMode");
    if (!string.IsNullOrWhiteSpace(refreshScrollMode)) request.RefreshScrollMode = NormalizePluginRefreshScrollMode(refreshScrollMode);
    if (TryReadBoolPayload(request.Payload, out var forceReload, "forceReload", "ForceReload")) request.ForceReload = forceReload;
    if (TryReadBoolPayload(request.Payload, out var refreshAfter, "refreshAfter", "RefreshAfter")) request.RefreshAfter = refreshAfter;
    if (TryReadBoolPayload(request.Payload, out var reuseExisting, "reuseExisting", "ReuseExisting")) request.ReuseExisting = reuseExisting;
    if (TryReadBoolPayload(request.Payload, out var activateExisting, "activateExisting", "ActivateExisting")) request.ActivateExisting = activateExisting;
    if (string.IsNullOrWhiteSpace(request.WindowToken)) request.WindowToken = ReadPayload(request.Payload, "windowToken", "WindowToken");
    if (string.IsNullOrWhiteSpace(request.Token)) request.Token = ReadPayload(request.Payload, "token", "Token");
    if (string.IsNullOrWhiteSpace(request.ReturnUrl)) request.ReturnUrl = ReadPayload(request.Payload, "returnUrl", "ReturnUrl", "redirectBackUrl", "RedirectBackUrl", "sourceUrl", "SourceUrl");
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

static string NormalizePluginRefreshScrollTarget(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var trimmed = value.Trim().TrimStart('#');
    return new string(trimmed.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.').ToArray());
}

static string NormalizePluginRefreshScrollMode(string? value)
{
    var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
    return mode is "top" or "nearest" or "center" ? mode : "center";
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

static async Task<PluginActionRequest> ReadPluginActionRequestAsync(HttpRequest http)
{
    var result = new PluginActionRequest();
    try
    {
        foreach (var kv in http.Query)
        {
            var key = kv.Key ?? string.Empty;
            var value = kv.Value.ToString();
            AssignPluginActionField(result, key, value);
        }

        if (http.HasFormContentType)
        {
            var form = await http.ReadFormAsync();
            foreach (var kv in form)
            {
                var key = kv.Key ?? string.Empty;
                var value = kv.Value.ToString();
                AssignPluginActionField(result, key, value);
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
            AssignPluginActionField(result, prop.Name, JsonElementToPluginActionString(prop.Value));
        }
        NormalizePluginActionPayloadAliases(result.Payload);
    }
    catch
    {
        // 不正なAction要求は後段の必須項目/トークン検証で拒否する。
    }
    return result;
}

static void AssignPluginActionField(PluginActionRequest target, string key, string value)
{
    key = (key ?? string.Empty).Trim();
    value = (value ?? string.Empty).Trim();
    if (key.Equals("pluginId", StringComparison.OrdinalIgnoreCase) || key.Equals("plugin", StringComparison.OrdinalIgnoreCase))
        target.PluginId = value;
    else if (key.Equals("routeSegment", StringComparison.OrdinalIgnoreCase) || key.Equals("route", StringComparison.OrdinalIgnoreCase))
        target.RouteSegment = value;
    else if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
        target.Action = value;
    else if (key.Equals("actionToken", StringComparison.OrdinalIgnoreCase))
        target.ActionToken = value;
    else if (key.Equals("token", StringComparison.OrdinalIgnoreCase))
        target.Token = value;
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
    else if (key.Equals("refreshTarget", StringComparison.OrdinalIgnoreCase) || key.Equals("target", StringComparison.OrdinalIgnoreCase))
    {
        target.RefreshTarget = value;
        target.Payload[key] = value;
    }
    else if (key.Equals("preserveScroll", StringComparison.OrdinalIgnoreCase))
    {
        if (bool.TryParse(value, out var preserveScroll)) target.PreserveScroll = preserveScroll;
        target.Payload[key] = value;
    }
    else if (key.Equals("refreshScrollTarget", StringComparison.OrdinalIgnoreCase) || key.Equals("scrollTarget", StringComparison.OrdinalIgnoreCase) || key.Equals("focusTarget", StringComparison.OrdinalIgnoreCase))
    {
        target.RefreshScrollTarget = value;
        target.Payload[key] = value;
    }
    else if (key.Equals("refreshScrollMode", StringComparison.OrdinalIgnoreCase) || key.Equals("scrollMode", StringComparison.OrdinalIgnoreCase))
    {
        target.RefreshScrollMode = value;
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
    MergePluginActionPayloadJson(payload, ReadPayload(payload, "payloadJson", "PayloadJson", "actionPayloadJson", "ActionPayloadJson", "viewerStartPayloadJson", "ViewerStartPayloadJson"));
    CopyPluginActionPayloadAlias(payload, "networkId", "NetworkId", "network_id", "network-id", "nid", "Nid", "NID");
    CopyPluginActionPayloadAlias(payload, "transportStreamId", "TransportStreamId", "transport_stream_id", "transport-stream-id", "tsid", "Tsid", "TSID");
    CopyPluginActionPayloadAlias(payload, "serviceId", "ServiceId", "service_id", "service-id", "sid", "Sid", "SID");
    CopyPluginActionPayloadAlias(payload, "serviceName", "ServiceName", "service", "name", "channelName");
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
    payload["ViewerProfile"] = selected;
    payload["viewer_profile"] = selected;
    payload["viewer-profile"] = selected;
    payload["viewerProfileId"] = selected;
    payload["ViewerProfileId"] = selected;
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

static string NormalizeViewerProfileActionId(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var v = value.Trim();
    if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)
        || v.Equals("default", StringComparison.OrdinalIgnoreCase)
        || v.Equals("tvtest", StringComparison.OrdinalIgnoreCase))
        return "tvtest1";
    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
        return $"tvtest{n}";
    if (v.StartsWith("tvtest", StringComparison.OrdinalIgnoreCase)
        && v.Length > "tvtest".Length
        && int.TryParse(v["tvtest".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
        && ordinal > 0)
        return $"tvtest{ordinal}";
    return string.Empty;
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

static string FormatPluginQueryKeys(HttpRequest http)
{
    if (http.Query.Count == 0) return "-";
    return string.Join("|", http.Query.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Take(80));
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

static string GetPluginActionIdentity(ITvAIrPlugin plugin, string? routeSegment)
    => plugin is IManifestPlugin mp && !string.IsNullOrWhiteSpace(mp.Manifest.Id)
        ? mp.Manifest.Id.Trim()
        : !string.IsNullOrWhiteSpace(routeSegment) ? routeSegment.Trim().Trim('/') : plugin.Name;

static ITvAIrPlugin? FindPluginByActionIdentity(PluginRegistry registry, string? pluginId, string? routeSegment)
{
    var id = (pluginId ?? string.Empty).Trim().Trim('/');
    var route = (routeSegment ?? string.Empty).Trim().Trim('/');
    foreach (var plugin in registry.GetAll())
    {
        if (plugin is IManifestPlugin mp && !string.IsNullOrWhiteSpace(mp.Manifest.Id)
            && string.Equals(mp.Manifest.Id.Trim(), id, StringComparison.OrdinalIgnoreCase))
            return plugin;
        if (plugin is IUiPlugin ui)
        {
            if (!string.IsNullOrWhiteSpace(route)
                && string.Equals(NormalizeAirhythmPublicRoute(ui.Ui.RouteSegment), NormalizeAirhythmPublicRoute(route), StringComparison.OrdinalIgnoreCase))
                return plugin;
            if (!string.IsNullOrWhiteSpace(id)
                && string.Equals(NormalizeAirhythmPublicRoute(ui.Ui.RouteSegment), NormalizeAirhythmPublicRoute(id), StringComparison.OrdinalIgnoreCase))
                return plugin;
        }
        if (!string.IsNullOrWhiteSpace(id) && string.Equals(plugin.Name, id, StringComparison.OrdinalIgnoreCase))
            return plugin;
    }
    return null;
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

static ushort ReadUShortPayload(Dictionary<string, string> payload, params string[] keys)
{
    var raw = ReadPayload(payload, keys);
    if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
    foreach (var part in raw.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
    {
        if (ushort.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
    }
    return 0;
}

static int? ReadIntPayload(Dictionary<string, string> payload, params string[] keys)
    => int.TryParse(ReadPayload(payload, keys), out var value) ? value : null;

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

static string BuildPluginShellHtml(string title, string route, string pluginBody, bool toolWindowContentOnly = false)
{
    if (toolWindowContentOnly)
    {
        return BuildPluginToolWindowContentHtml(title, route, pluginBody);
    }

    var safeTitle = System.Net.WebUtility.HtmlEncode(title);
    var safeRoute = System.Net.WebUtility.HtmlEncode(route);
    var contentOnlyClass = string.Empty;
    return $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta http-equiv="X-UA-Compatible" content="IE=edge">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'">
<title>{{safeTitle}} - TvAIr</title>
<link rel="icon" type="image/x-icon" href="/favicon.ico?v=1.0.8">
<link rel="shortcut icon" type="image/x-icon" href="/favicon.ico?v=1.0.8">
<link rel="stylesheet" href="/tvair-notification.css?v=1.0.8">
<link rel="stylesheet" href="/tvair-epg-panel.css?v=1.0.8">
<link rel="stylesheet" href="/tvair-ui-foundation.css?v=1.0.8">
<link rel="stylesheet" href="/tvair-ui-modules.css?v=1.0.8">
<script src="/tvair-theme.js?v=1.0.8"></script>

<style>
*{box-sizing:border-box;margin:0;padding:0}
html,body{width:100%;height:100%}
body{font-family:'Meiryo',sans-serif;font-size:12px;background:var(--tvair-bg-page,#f0f0f0);color:var(--tvair-text-main,#222);overflow:hidden;height:100vh;display:flex;flex-direction:column}
#nav{background:var(--nav-bg);display:flex;align-items:center;gap:4px;padding:3px 6px;flex-shrink:0;height:30px;position:relative;z-index:1000;isolation:isolate}
#nav .nav-btn{background:var(--nav-btn-bg);color:var(--tvair-nav-button-text,#222);border:none;padding:3px 0;cursor:pointer;font-size:12px;border-radius:2px;white-space:nowrap;min-width:110px;text-align:center;display:inline-flex;align-items:center;justify-content:center;height:24px}.nav-btn,.nav-btn:link,.nav-btn:visited,.nav-btn:hover,.nav-btn:active,.nav-btn:focus{color:var(--tvair-nav-button-text,#222) !important;text-decoration:none}
#nav .nav-btn:hover{background:var(--nav-btn-hover)}
#nav .spacer{flex:1}



#menu-wrap{position:relative}
#menu-btn{background:var(--nav-btn-bg);color:var(--tvair-nav-button-text,#222);border:none;padding:3px 10px;cursor:pointer;font-size:15px;border-radius:2px;line-height:1;letter-spacing:1px}
#menu-btn:hover{background:var(--nav-btn-hover)}
/* release_contract MenuLegacyEntryCleanupContract: plugin shell menu behavior is owned by tvair-menu-spine.js; this CSS only keeps host-frame placement. */
#menu-dropdown{display:none;position:absolute;top:100%;right:0;z-index:500;min-width:160px;margin-top:2px}
#menu-dropdown.open{display:block}
.menu-item{display:block;width:100%;padding:8px 14px;background:transparent;border:none;text-align:left;font-size:12px;cursor:pointer;color:var(--tvair-content-primary,#333);white-space:nowrap;text-decoration:none}
.menu-item:hover{background:var(--tvair-surface-soft-hover,#eef4ff);color:var(--tvair-content-link,var(--nav-bg))}
.menu-sep{height:1px;background:var(--timecol-bg);margin:3px 0}

/* release_contract: EPG操作を1階層目から退避し、共通サブメニュー化 */
.menu-group{margin:0;padding:0}
.menu-group>summary{list-style:none}
.menu-group>summary::-webkit-details-marker{display:none}
.menu-summary{position:relative;user-select:none}
.menu-summary::after{content:'▸';float:right;opacity:.72}
.menu-group[open]>.menu-summary::after{content:'▾'}
.menu-subitem{padding-left:26px;font-size:11.5px;background:var(--tvair-surface-subtle,#fbfcff)}
.menu-subitem:hover{background:var(--tvair-surface-soft-hover,#eef4ff)}
.nav-brand-text{font-weight:bold;color:var(--tvair-nav-text,#222);letter-spacing:.03em;margin-right:6px;white-space:nowrap;position:relative;z-index:20}
#nav .nav-btn,#menu-wrap{position:relative;z-index:20}
.plugin-shell-content{flex:1;min-height:0;overflow:auto;background:var(--tvair-bg-page,#f4f6f8)}
.plugin-shell-inner{min-height:100%;box-sizing:border-box}
.tvair-plugin-toolwindow-content-only #nav{display:none !important}
.tvair-plugin-toolwindow-content-only .plugin-shell-content{height:100vh;min-height:0;overflow:auto;background:#fff}
.tvair-plugin-toolwindow-content-only .plugin-shell-inner{min-height:100%;background:#fff}
</style>
</head>
<body class="tvair-non-program-page tvair-plugin-shell-page{{contentOnlyClass}}" data-plugin-route="{{safeRoute}}">
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
{{pluginBody}}
  </main>
</div>
<script src="/tvair-notification.js?v=1.0.8"></script>
<script src="/tvair-epg-run-contract.js?v=1.0.8"></script>
<script src="/tvair-epg-widget.js?v=1.0.8"></script>
<script src="/tvair-safe-event-host.js?v=1.0.8"></script>
<script src="/tvair-menu-spine.js?v=1.0.8"></script>
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
function tvairClientBeacon(phase,eventName,action,el){
  try{
    var img=new Image();
    var networkId=tvairGetAttr(el,'data-tvair-payload-networkId')||tvairGetAttr(el,'data-tvair-payload-nid');
    var tsid=tvairGetAttr(el,'data-tvair-payload-transportStreamId')||tvairGetAttr(el,'data-tvair-payload-tsid');
    var sid=tvairGetAttr(el,'data-tvair-payload-serviceId')||tvairGetAttr(el,'data-tvair-payload-sid');
    var url='/api/plugins/safe-event/client-log?phase='+encodeURIComponent(phase||'')+
      '&event='+encodeURIComponent(eventName||'')+
      '&action='+encodeURIComponent(action||'')+
      '&pluginId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-plugin-id')||'')+
      '&routeSegment='+encodeURIComponent(tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'')+
      '&windowId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId()||'')+
      '&mode='+encodeURIComponent(document.body.className.indexOf('tvair-plugin-toolwindow-content-only')>=0?'directContent':'page')+
      '&hostKind='+encodeURIComponent('winforms_webbrowser_fallback_direct_content')+
      '&tag='+encodeURIComponent(tvairTagName(el))+
      '&hasAction='+encodeURIComponent(action?'true':'false')+
      '&hasToken='+encodeURIComponent((tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'))?'true':'false')+
      '&hasTriplet='+encodeURIComponent((networkId&&tsid&&sid)?'true':'false')+
      '&_='+String(new Date().getTime());
    img.src=url;
  }catch(_){ }
}
function tvairFindSafeEventTarget(start,eventName){
  var n=start;
  while(n&&n!==document){
    if(n.getAttribute&&tvairGetAttr(n,'data-tvair-action')){
      var events=tvairGetAttr(n,'data-tvair-event');
      if(tvairHasToken(events,eventName))return n;
      // IE互換WebBrowserで dblclick が拾えない場合に備え、viewerStartのclick診断にも反応できるようにする。
      if(eventName==='click'&&tvairHasToken(events,'dblclick')&&tvairGetAttr(n,'data-tvair-click-fallback')==='true')return n;
    }
    n=n.parentNode;
  }
  return null;
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
function tvairSubmitSafeAction(el,eventName){
  var action=tvairGetAttr(el,'data-tvair-action');
  tvairClientBeacon('received_before_validate',eventName,action,el);
  if(!action){tvairClientBeacon('denied_missing_action',eventName,action,el);return;}
  var isWindowAction=(action==='refreshWindow'||action==='updateWindow'||action==='closeWindow'||action==='rerenderWindow'||action==='openWindow');
  var form=document.createElement('form');form.method='post';form.action=tvairGetAttr(el,'data-tvair-endpoint')||(isWindowAction?'/api/plugins/window':'/api/plugins/action');
  tvairAppendHidden(form,'action',action);
  tvairAppendHidden(form,'pluginId',tvairGetAttr(el,'data-tvair-plugin-id'));
  tvairAppendHidden(form,'routeSegment',tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'');
  tvairAppendHidden(form,'token',tvairGetAttr(el,'data-tvair-token')||tvairGetAttr(el,'data-tvair-action-token'));
  tvairAppendHidden(form,'actionToken',tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'));
  tvairAppendHidden(form,'responseMode',tvairGetAttr(el,'data-tvair-response-mode')||(isWindowAction?'hostHandled':'refreshWindow'));
  tvairAppendHidden(form,'windowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());
  tvairAppendHidden(form,'target',tvairGetAttr(el,'data-tvair-target')||'content');
  tvairAppendHidden(form,'refreshTarget',tvairGetAttr(el,'data-tvair-refresh-target')||'content');
  tvairAppendHidden(form,'preserveScroll',tvairGetAttr(el,'data-tvair-preserve-scroll')||'true');tvairAppendHidden(form,'refreshScrollTarget',tvairGetAttr(el,'data-tvair-refresh-scroll-target')||tvairGetAttr(el,'data-tvair-scroll-target')||tvairGetAttr(el,'data-tvair-focus-target')||'');tvairAppendHidden(form,'refreshScrollMode',tvairGetAttr(el,'data-tvair-refresh-scroll-mode')||tvairGetAttr(el,'data-tvair-scroll-mode')||'center');
  tvairAppendHidden(form,'safeEvent',eventName||'unknown');
  tvairAppendHidden(form,'safeEventAction',action);
  tvairAppendHidden(form,'safeEventSource','host-script-no-plugin-js');
  tvairAppendHidden(form,'safeEventWindowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());
  if(el.attributes){
    for(var i=0;i<el.attributes.length;i++){
      var a=el.attributes[i];
      if(a&&a.name&&a.name.indexOf('data-tvair-payload-')===0){tvairAppendHidden(form,a.name.substring('data-tvair-payload-'.length),a.value);}
    }
  }
  document.body.appendChild(form);
  tvairClientBeacon('posting_form',eventName,action,el);
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
    document.attachEvent('onclick',function(){return tvairHandleSafeEvent(window.event,'click');});
  }else{
    var oldDbl=document.ondblclick;document.ondblclick=function(e){if(tvairHandleSafeEvent(e||window.event,'dblclick')===false)return false;return oldDbl?oldDbl(e):true;};
    var oldClick=document.onclick;document.onclick=function(e){if(tvairHandleSafeEvent(e||window.event,'click')===false)return false;return oldClick?oldClick(e):true;};
  }
  tvairClientBeacon('bind_complete','','',document.body);
}
tvairBindSafeEvents();</script>

</body>
</html>
""";
}

static string BuildPluginToolWindowContentHtml(string title, string route, string pluginBody)
{
    var safeTitle = System.Net.WebUtility.HtmlEncode(title);
    var safeRoute = System.Net.WebUtility.HtmlEncode(route);
    var normalized = NormalizePluginToolWindowContent(pluginBody);
    var pluginStyles = normalized.Styles;
    var pluginContent = normalized.Body;
    var template = """
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta http-equiv="X-UA-Compatible" content="IE=edge">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'">
<title>{{safeTitle}} - TvAIr Tool Window</title>
<style>
*{box-sizing:border-box}
html,body{margin:0;padding:0;width:100%;height:100%;min-width:0;min-height:0;overflow:hidden;background:var(--tvair-color-surface-panel);color:var(--tvair-color-text-main)}
body{font-family:Meiryo,"Yu Gothic",Arial,sans-serif;font-size:12px}
.tvair-toolwindow-content-root{position:relative;width:100%;height:100%;min-width:0;min-height:0;overflow:hidden;background:#fff}
img{max-width:100%}
button,input,select,textarea{font-family:inherit;font-size:inherit}
</style>
{{pluginStyles}}
</head>
<body class="tvair-plugin-toolwindow-content-only" data-plugin-route="{{safeRoute}}" data-tvair-host-kind="winforms_webbrowser_fallback_direct_content" data-tvair-toolwindow-contract="release_contract">
<div class="tvair-toolwindow-content-root">
{{pluginContent}}
</div>
<script src="/tvair-safe-event-host.js?v=1.0.8"></script>
<script>
function tvairAppendHidden(form,name,value){if(!name||value==null||value==='')return;var i=document.createElement('input');i.type='hidden';i.name=name;i.value=String(value);form.appendChild(i);}
function tvairGetAttr(el,name){try{return el&&el.getAttribute?el.getAttribute(name)||'':'';}catch(_){return '';} }
function tvairHasToken(value, token){return ((' '+(value||'')+' ').indexOf(' '+token+' '))>=0;}
function tvairTagName(el){try{return el&&el.tagName?String(el.tagName).toLowerCase():'';}catch(_){return '';} }
function tvairCurrentWindowId(){var q=location.search||'';var m=q.match(/[?&]__tvairWindowId=([^&]+)/)||q.match(/[?&]windowId=([^&]+)/);return m?decodeURIComponent(m[1].replace(/\+/g,' ')):'';}
function tvairCurrentRevision(){var q=location.search||'';var m=q.match(/[?&]_tvairWindowRevision=([^&]+)/);return m?decodeURIComponent(m[1].replace(/\+/g,' ')):'';}
function tvairClientBeacon(phase,eventName,action,el){try{var img=new Image();var networkId=tvairGetAttr(el,'data-tvair-payload-networkId')||tvairGetAttr(el,'data-tvair-payload-nid');var tsid=tvairGetAttr(el,'data-tvair-payload-transportStreamId')||tvairGetAttr(el,'data-tvair-payload-tsid');var sid=tvairGetAttr(el,'data-tvair-payload-serviceId')||tvairGetAttr(el,'data-tvair-payload-sid');var url='/api/plugins/safe-event/client-log?phase='+encodeURIComponent(phase||'')+'&event='+encodeURIComponent(eventName||'')+'&action='+encodeURIComponent(action||'')+'&pluginId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-plugin-id')||'')+'&routeSegment='+encodeURIComponent(tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'')+'&windowId='+encodeURIComponent(tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId()||'')+'&mode='+encodeURIComponent('directContent')+'&hostKind='+encodeURIComponent('winforms_webbrowser_fallback_direct_content')+'&tag='+encodeURIComponent(tvairTagName(el))+'&hasAction='+encodeURIComponent(action?'true':'false')+'&hasToken='+encodeURIComponent((tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'))?'true':'false')+'&hasTriplet='+encodeURIComponent((networkId&&tsid&&sid)?'true':'false')+'&_='+String(new Date().getTime());img.src=url;}catch(_){ }}
function tvairFindSafeEventTarget(start,eventName){var n=start;while(n&&n!==document){if(n.getAttribute&&tvairGetAttr(n,'data-tvair-action')){var events=tvairGetAttr(n,'data-tvair-event');if(tvairHasToken(events,eventName))return n;if(eventName==='click'&&tvairHasToken(events,'dblclick')&&tvairGetAttr(n,'data-tvair-click-fallback')==='true')return n;}n=n.parentNode;}return null;}
function tvairPreserveToolWindowLink(href){if(!href)return href;var route=document.body.getAttribute('data-plugin-route')||'';var currentWindow=tvairCurrentWindowId();if(!currentWindow||!route)return href;var a=document.createElement('a');a.href=href;if(a.pathname!=='/plugin/'+route)return href;if(a.search.indexOf('__tvairWindowId=')<0)a.search+=(a.search?'&':'?')+'__tvairWindowId='+encodeURIComponent(currentWindow);if(a.search.indexOf('__tvairHostWindow=')<0)a.search+='&__tvairHostWindow=1';var rev=tvairCurrentRevision(); if(rev&&a.search.indexOf('_tvairWindowRevision=')<0)a.search+='&_tvairWindowRevision='+encodeURIComponent(rev);return a.pathname+a.search+a.hash;}
function tvairSubmitSafeAction(el,eventName){var action=tvairGetAttr(el,'data-tvair-action');tvairClientBeacon('received_before_validate',eventName,action,el);if(!action){tvairClientBeacon('denied_missing_action',eventName,action,el);return;}var isWindowAction=(action==='refreshWindow'||action==='updateWindow'||action==='closeWindow'||action==='rerenderWindow'||action==='openWindow');var form=document.createElement('form');form.method='post';form.action=tvairGetAttr(el,'data-tvair-endpoint')||(isWindowAction?'/api/plugins/window':'/api/plugins/action');tvairAppendHidden(form,'action',action);tvairAppendHidden(form,'pluginId',tvairGetAttr(el,'data-tvair-plugin-id'));tvairAppendHidden(form,'routeSegment',tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'');tvairAppendHidden(form,'token',tvairGetAttr(el,'data-tvair-token')||tvairGetAttr(el,'data-tvair-action-token'));tvairAppendHidden(form,'actionToken',tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token'));tvairAppendHidden(form,'responseMode',tvairGetAttr(el,'data-tvair-response-mode')||(isWindowAction?'hostHandled':'refreshWindow'));tvairAppendHidden(form,'windowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());tvairAppendHidden(form,'target',tvairGetAttr(el,'data-tvair-target')||'content');tvairAppendHidden(form,'refreshTarget',tvairGetAttr(el,'data-tvair-refresh-target')||'content');tvairAppendHidden(form,'preserveScroll',tvairGetAttr(el,'data-tvair-preserve-scroll')||'true');tvairAppendHidden(form,'refreshScrollTarget',tvairGetAttr(el,'data-tvair-refresh-scroll-target')||tvairGetAttr(el,'data-tvair-scroll-target')||tvairGetAttr(el,'data-tvair-focus-target')||'');tvairAppendHidden(form,'refreshScrollMode',tvairGetAttr(el,'data-tvair-refresh-scroll-mode')||tvairGetAttr(el,'data-tvair-scroll-mode')||'center');tvairAppendHidden(form,'safeEvent',eventName||'unknown');tvairAppendHidden(form,'safeEventAction',action);tvairAppendHidden(form,'safeEventSource','host-script-no-plugin-js');tvairAppendHidden(form,'safeEventWindowId',tvairGetAttr(el,'data-tvair-window-id')||tvairCurrentWindowId());if(el.attributes){for(var i=0;i<el.attributes.length;i++){var a=el.attributes[i];if(a&&a.name&&a.name.indexOf('data-tvair-payload-')===0){tvairAppendHidden(form,a.name.substring('data-tvair-payload-'.length),a.value);}}}document.body.appendChild(form);tvairClientBeacon('posting_form',eventName,action,el);form.submit();}
function tvairHandleSafeEvent(e,eventName){e=e||window.event;var target=e.target||e.srcElement;var el=tvairFindSafeEventTarget(target,eventName);if(!el)return true;try{if(e.preventDefault)e.preventDefault();e.returnValue=false;}catch(_){ }tvairSubmitSafeAction(el,eventName);return false;}
function tvairBindSafeEvents(){if(window.__tvairSafeEventBound){tvairClientBeacon('bind_skip_already_bound','','',document.body);return;}window.__tvairSafeEventBound=true;tvairClientBeacon('bind_start','','',document.body);if(document.addEventListener){document.addEventListener('dblclick',function(e){return tvairHandleSafeEvent(e,'dblclick');},false);document.addEventListener('click',function(e){if(tvairHandleSafeEvent(e,'click')===false)return false;var a=e.target;while(a&&a!==document&&!(a.tagName&&String(a.tagName).toLowerCase()==='a'))a=a.parentNode;if(a&&a.href){var next=tvairPreserveToolWindowLink(a.getAttribute('href')||'');if(next&&(next!==a.getAttribute('href'))){if(e.preventDefault)e.preventDefault();location.href=next;}}},false);}else if(document.attachEvent){document.attachEvent('ondblclick',function(){return tvairHandleSafeEvent(window.event,'dblclick');});document.attachEvent('onclick',function(){return tvairHandleSafeEvent(window.event,'click');});}else{var oldDbl=document.ondblclick;document.ondblclick=function(e){if(tvairHandleSafeEvent(e||window.event,'dblclick')===false)return false;return oldDbl?oldDbl(e):true;};var oldClick=document.onclick;document.onclick=function(e){if(tvairHandleSafeEvent(e||window.event,'click')===false)return false;return oldClick?oldClick(e):true;};}tvairClientBeacon('bind_complete','','',document.body);}
tvairBindSafeEvents();
</script>
</body>
</html>
""";
    return template
        .Replace("{{safeTitle}}", safeTitle, StringComparison.Ordinal)
        .Replace("{{safeRoute}}", safeRoute, StringComparison.Ordinal)
        .Replace("{{pluginStyles}}", pluginStyles, StringComparison.Ordinal)
        .Replace("{{pluginContent}}", pluginContent, StringComparison.Ordinal);
}

static (string Styles, string Body) NormalizePluginToolWindowContent(string? html)
{
    var source = html ?? string.Empty;
    var styles = string.Join("\n", System.Text.RegularExpressions.Regex.Matches(source, "<\\s*style\\b[^>]*>.*?<\\s*/\\s*style\\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value));
    var body = source;
    if (LooksLikeFullHtmlDocument(source)) body = ExtractPluginBodyFragment(source);
    body = System.Text.RegularExpressions.Regex.Replace(body, "<\\s*style\\b[^>]*>.*?<\\s*/\\s*style\\s*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    body = System.Text.RegularExpressions.Regex.Replace(body, "<\\s*/?\\s*(html|head|body)\\b[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    return (styles, body);
}

static bool LooksLikeFullHtmlDocument(string? html)
{
    if (string.IsNullOrWhiteSpace(html)) return false;
    return html.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
        || html.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0
        || html.IndexOf("<head", StringComparison.OrdinalIgnoreCase) >= 0;
}

static IResult RenderPluginHtml(string route, HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, LogRepository log)
{
    var requestedRoute = string.Join("", route.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    if (string.IsNullOrWhiteSpace(requestedRoute))
        return Results.NotFound("Plugin UI not found.");

    var publicRoute = NormalizeAirhythmPublicRoute(requestedRoute);
    var plugin = registry.FindUiPlugin(requestedRoute);
    var title = IsAirhythmRouteCandidate(requestedRoute) ? "AI-rhythm" : publicRoute;
    if (plugin is not null)
        title = NormalizeAirhythmDisplayName(plugin.Ui.MenuText.Length > 0 ? plugin.Ui.MenuText : plugin.Name);

    var currentWindowId = NormalizePluginWindowId(http.Query["__tvairWindowId"].FirstOrDefault()
        ?? http.Query["_tvairWindowId"].FirstOrDefault()
        ?? http.Query["windowId"].FirstOrDefault());
    var hostManagedWindow = !string.IsNullOrWhiteSpace(currentWindowId) ? windows.Get(currentWindowId) : null;
    var expectedWindowPluginId = plugin is not null
        ? GetPluginActionIdentity(plugin, string.IsNullOrWhiteSpace(plugin.Ui.RouteSegment) ? publicRoute : plugin.Ui.RouteSegment)
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

    // release_contract: Window state absolute URL contract must be derived before PluginUiContext construction.
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
    log.Add("PLUGIN_RENDER_ENTER", plugin?.Name ?? publicRoute, $"routeSegment={SafePluginActionValue(publicRoute)} requestedRoute={SafePluginActionValue(requestedRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} requestPath={SafePluginActionValue(currentRequestPath)} query={SafePluginActionValue(currentRequestQueryString)} rule=release_contract");

    try
    {
        string body;

        // TvAIr 1.0.0: プラグインページはTvAIr共通ヘッダー付きの拡張画面として表示する。
        // release_contract: AI-rhythm は /plugin/airhythm を正式入口とし、旧 /plugin/airithm は legacy alias として解決する。
        var physicalRoute = plugin?.Ui.RouteSegment ?? publicRoute;
        var indexPath = Path.Combine(AppContext.BaseDirectory, "Plugins", physicalRoute, "wwwroot", "index.html");
        if (!File.Exists(indexPath) && IsAirhythmRouteCandidate(publicRoute))
        {
            var legacyIndexPath = Path.Combine(AppContext.BaseDirectory, "Plugins", "airithm", "wwwroot", "index.html");
            if (File.Exists(legacyIndexPath)) indexPath = legacyIndexPath;
        }
        if (File.Exists(indexPath))
        {
            var staticHtml = File.ReadAllText(indexPath);
            body = ExtractPluginBodyFragment(staticHtml);
            log.Add("PLUGIN_RENDER_RESULT", plugin?.Name ?? publicRoute, $"source=static_index routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} {BuildPluginRenderHtmlAudit(body)} rule=release_contract");
        }
        else if (plugin is not null)
        {
            var pluginId = GetPluginActionIdentity(plugin, physicalRoute);
            var token = actionTokens.Issue(pluginId, publicRoute);
            var supportedActions = new[] { "viewerStart", "viewerStop" };
            var pluginAssetBaseUrl = $"/plugin-assets/{Uri.EscapeDataString(publicRoute)}";
            var pluginAssetApiBaseUrl = $"/api/plugins/{Uri.EscapeDataString(pluginId)}/assets";
            var toolWindowCaps = toolWindows.GetCapabilities();
            var viewerProfileProjection = BuildViewerProfileProjection(tvTestOptions.Value, ini, tunerProfiles);
            var viewerProfilesForContext = viewerProfileProjection.Profiles.Select(ToPluginViewerProfileInfo).ToList();
            var selectableViewerProfilesForContext = viewerProfileProjection.SelectableProfiles.Select(ToPluginViewerProfileInfo).ToList();
            log.Add("VIEWER_PROFILE_PROJECTION", plugin.Name,
                $"result=OK source=PluginUiContext routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} viewingTuners={viewerProfileProjection.ViewingTunerCount} profiles={viewerProfileProjection.Profiles.Count} selectable={viewerProfileProjection.SelectableProfiles.Count} enabledReal={viewerProfileProjection.EnabledRealProfileCount} selectorVisibleRecommended={viewerProfileProjection.SelectorVisibleRecommended} profileIds={SafePluginActionValue(viewerProfileProjection.ProfileIds)} selectableIds={SafePluginActionValue(viewerProfileProjection.SelectableProfileIds)} default={SafePluginActionValue(viewerProfileProjection.DefaultViewerProfile)} rule=release_contract");
            var actionContext = new TvAIrPlugin.PluginUiContext
            {
                RequestedAt = DateTime.Now,
                IsClosedNetwork = true,
                ActionEndpoint = "/api/plugins/action",
                ActionRoute = "/plugin-action",
                PluginActionRoute = "/plugin-action",
                PluginActionEndpoint = "/api/plugins/action",
                ActionMethod = "POST",
                PluginActionMethod = "POST",
                SupportedActions = supportedActions,
                PluginSupportedActions = supportedActions,
                ViewerProfiles = viewerProfilesForContext,
                SelectableViewerProfiles = selectableViewerProfilesForContext,
                DefaultViewerProfile = viewerProfileProjection.DefaultViewerProfile,
                EnabledRealViewerProfileCount = viewerProfileProjection.EnabledRealProfileCount,
                SelectorVisibleRecommended = viewerProfileProjection.SelectorVisibleRecommended,
                ViewerProfileSelectorVisibleRecommended = viewerProfileProjection.SelectorVisibleRecommended,
                MinWidthInvariantRequired = true,
                ActionToken = token.Token,
                PluginActionToken = token.Token,
                PluginId = pluginId,
                RouteSegment = publicRoute,
                ActionContract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["route"] = "/plugin-action",
                    ["endpoint"] = "/api/plugins/action",
                    ["method"] = "POST",
                    ["actions"] = string.Join(",", supportedActions),
                    ["token"] = token.Token,
                    ["pluginId"] = pluginId,
                    ["routeSegment"] = publicRoute,
                    ["responseMode"] = "json|refreshWindow|hostHandled|noContent",
                    ["formResponseMode"] = "viewerStart/viewerStop support responseMode=hostHandled for no JSON/no rerender; refreshWindow for explicit rerender",
                    ["viewerStartPayloadFields"] = "serviceId|networkId|transportStreamId|programGuideFilterGroup|broadcastGroup|allocationGroup|tunerGroup|channelSpace|channelIndex|viewerProfile|windowId|responseMode|refreshAfter|refreshTarget|refreshScrollTarget|refreshScrollMode|preserveViewerWindowState|viewerActivation|retuneExistingViewer",
                    ["viewerStartPreferredTunerFields"] = "preferredTunerName|preferredDid|preferredSlot",
                    ["viewerProfilesEndpoint"] = "/api/plugins/viewer-profiles",
                    ["viewerProfileReuseContract"] = "viewerProfile/tvTestPathKey plus BonDriver/DID must match before internal retune",
                    ["viewerProfiles"] = viewerProfileProjection.ProfileIds,
                    ["selectableViewerProfiles"] = viewerProfileProjection.SelectableProfileIds,
                    ["defaultViewerProfile"] = viewerProfileProjection.DefaultViewerProfile,
                    ["enabledRealViewerProfileCount"] = viewerProfileProjection.EnabledRealProfileCount.ToString(CultureInfo.InvariantCulture),
                    ["selectorVisibleRecommended"] = viewerProfileProjection.SelectorVisibleRecommended ? "true" : "false",
                    ["viewerProfileSelectorVisibleRecommended"] = viewerProfileProjection.SelectorVisibleRecommended ? "true" : "false",
                    ["minWidthInvariantRequired"] = "true"
                },
                WindowRoute = "/plugin-window",
                WindowEndpoint = "/api/plugins/window",
                WindowStateEndpointTemplate = "/plugin-window/{windowId}/state",
                CurrentWindowId = currentWindowId,
                WindowId = currentWindowId,
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
                    ["supportsRefreshScrollTarget"] = toolWindowCaps.SupportsRefreshScrollTarget ? "true" : "false",
                    ["refreshScrollModes"] = toolWindowCaps.RefreshScrollModes,
                    ["scriptExecutionAllowed"] = toolWindowCaps.ScriptExecutionAllowed ? "true" : "false",
                    ["supportsManifestFormIcon"] = toolWindowCaps.SupportsManifestFormIcon ? "true" : "false",
                    ["formIconSourcePriority"] = toolWindowCaps.FormIconSourcePriority
                },
                ViewerControlActionContract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["contractVersion"] = "1.0.0",
                    ["toolWindowOnlySafeEvents"] = "true",
                    ["pluginScriptAllowed"] = "false",
                    ["safeEventAttributePrefix"] = "data-tvair-",
                    ["supportedEvents"] = "dblclick,click",
                    ["supportedActions"] = "viewerStart,viewerStop,refreshWindow,updateWindow",
                    ["viewerStartPayloadFields"] = "serviceId|networkId|transportStreamId|programGuideFilterGroup|broadcastGroup|allocationGroup|tunerGroup|channelSpace|channelIndex|viewerProfile|windowId|responseMode|refreshAfter|refreshTarget|refreshScrollTarget|refreshScrollMode|preserveViewerWindowState|viewerActivation|retuneExistingViewer",
                    ["viewerStartPreferredTunerFields"] = "preferredTunerName|preferredDid|preferredSlot",
                    ["viewerProfilesEndpoint"] = "/api/plugins/viewer-profiles",
                    ["viewerProfileReuseContract"] = "viewerProfile/tvTestPathKey plus BonDriver/DID must match before internal retune",
                    ["viewerProfiles"] = viewerProfileProjection.ProfileIds,
                    ["selectableViewerProfiles"] = viewerProfileProjection.SelectableProfileIds,
                    ["defaultViewerProfile"] = viewerProfileProjection.DefaultViewerProfile,
                    ["enabledRealViewerProfileCount"] = viewerProfileProjection.EnabledRealProfileCount.ToString(CultureInfo.InvariantCulture),
                    ["selectorVisibleRecommended"] = viewerProfileProjection.SelectorVisibleRecommended ? "true" : "false",
                    ["viewerProfileSelectorVisibleRecommended"] = viewerProfileProjection.SelectorVisibleRecommended ? "true" : "false",
                    ["minWidthInvariantRequired"] = "true",
                    ["programGuideFilterSource"] = "TvAIrProgramGuideWaveFilter",
                    ["programGuideFilterField"] = "programGuideFilterGroup",
                    ["tunerGroupField"] = "tunerGroup",
                    ["viewerTunersEndpoint"] = "/api/plugins/viewer-tuners",
                    ["waveFiltersEndpoint"] = "/api/plugins/program-guide/wave-filters",
                    ["alwaysOnTopAction"] = "updateWindow payload.alwaysOnTop refreshAfter=true refreshTarget=content",
                    ["updateWindowRefreshAfter"] = "refreshAfter=true refreshTarget=content responseMode=hostHandled refreshScrollTarget=<elementId> refreshScrollMode=center|nearest|top",
                    ["refreshScrollTarget"] = "element id only; no CSS selector; host applies after directContent rerender",
                    ["refreshScrollModes"] = "center|nearest|top",
                    ["updateWindowRefreshAfterAutoRender"] = "same_window_content_only",
                    ["alwaysOnTopStateSource"] = "PluginUiContext.CurrentWindowAlwaysOnTop first; /plugin-window/{windowId}/state is authoritative for browser-side refresh",
                    ["currentWindowAlwaysOnTop"] = currentWindowAlwaysOnTop ? "true" : "false",
                    ["currentWindowRevision"] = currentWindowRevision.ToString(CultureInfo.InvariantCulture),
                    ["currentWindowHostAlive"] = currentWindowHostAlive ? "true" : "false",
                    ["renderHtmlStateReadPolicy"] = "do_not_http_self_call; use PluginUiContext direct state values",
                    ["updateWindowPreferredResponseMode"] = "hostHandled",
                    ["updateWindowAutoRender"] = "not_guaranteed; call refreshWindow responseMode=hostHandled if immediate rerender is required",
                    ["directContentRefreshReloadScope"] = "toolwindow-content-document",
                    ["preferredOpenModeToolWindowSupported"] = "true",
                    ["toolWindowContentOnly"] = "true",
                    ["currentRequestPath"] = currentRequestPath,
                    ["currentRequestQueryString"] = currentRequestQueryString,
                    ["currentRequestPathAndQuery"] = currentRequestPathAndQuery,
                    ["currentRequestQueryKeys"] = currentRequestQueryKeys,
                    ["currentRequestWave"] = currentRequestWave,
                    ["pluginAssetBaseUrl"] = pluginAssetBaseUrl,
                    ["pluginAssetAllowedExtensions"] = "png",
                    ["pluginAssetResolveMethod"] = "PluginUiContext.ResolveAssetUrl(assetName)",
                    ["toolWindowFormIconContract"] = "manifest/Ui.Icon .ico -> host-managed Form.Icon",
                    ["toolWindowFormIconAllowedExtension"] = "ico",
                    ["toolWindowFormIconSourcePriority"] = "EmbeddedResource>plugin_file>default_TvAIr_icon"
                },
                CurrentRequestPath = currentRequestPath,
                CurrentRequestQueryString = currentRequestQueryString,
                CurrentRequestPathAndQuery = currentRequestPathAndQuery,
                CurrentRequestQuery = currentRequestQuery,
                PluginAssetBaseUrl = pluginAssetBaseUrl,
                PluginAssetContract = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                    ["currentWindowStateDirectValues"] = "PluginUiContext.CurrentWindowAlwaysOnTop|CurrentWindowRevision|CurrentWindowHostAlive; no RenderHtml self HTTP call required",
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
                    ["toolWindowFormIconContract"] = "manifest/Ui.Icon .ico -> host-managed Form.Icon",
                    ["toolWindowFormIconAllowedExtension"] = "ico",
                    ["toolWindowFormIconSourcePriority"] = "EmbeddedResource>plugin_file>default_TvAIr_icon",
                    ["toolWindowFormResponse"] = "303_redirect_back_to_returnUrl_or_referrer_no_json_no_blank",
                    ["viewerControlEventContract"] = "toolWindow-only safe host binding; data-tvair-event=dblclick; data-tvair-action=viewerStart; plugin JS remains forbidden",
                    ["viewerTunersEndpoint"] = "/api/plugins/viewer-tuners",
                    ["viewerProfilesEndpoint"] = "/api/plugins/viewer-profiles",
                    ["viewerProfiles"] = viewerProfileProjection.ProfileIds,
                    ["selectableViewerProfiles"] = viewerProfileProjection.SelectableProfileIds,
                    ["defaultViewerProfile"] = viewerProfileProjection.DefaultViewerProfile,
                    ["enabledRealViewerProfileCount"] = viewerProfileProjection.EnabledRealProfileCount.ToString(CultureInfo.InvariantCulture),
                    ["selectorVisibleRecommended"] = viewerProfileProjection.SelectorVisibleRecommended ? "true" : "false",
                    ["viewerProfileSelectorVisibleRecommended"] = viewerProfileProjection.SelectorVisibleRecommended ? "true" : "false",
                    ["minWidthInvariantRequired"] = "true",
                    ["programGuideWaveFiltersEndpoint"] = "/api/plugins/program-guide/wave-filters",
                    ["preferredOpenMode"] = "toolWindow",
                    ["toolWindowContentOnly"] = "true",
                    ["actionForm"] = "form POST /api/plugins/action responseMode=hostHandled windowId=currentWindowId; use refreshWindow only when immediate rerender is required",
                    ["viewerActionPreferredResponseMode"] = "hostHandled",
                    ["viewerActionAutoRender"] = "viewerStart/viewerStop support responseMode=hostHandled refreshAfter=true refreshTarget=content refreshScrollTarget=<elementId> refreshScrollMode=center|nearest|top for same-window content rerender and host scroll",
                    ["viewerStopSuccessState"] = "PLUGIN_ACTION_VIEWER result=OK plus no active GetViewerSessions entry means UI OFF; VIEWER_PROCESS_STOP NOT_FOUND is acceptable when lease is released",
                    ["viewerStartPreserveWindowState"] = "preserveViewerWindowState=true viewerActivation=preserve prevents normal-window activation request; plugin must not call Win32/Alt+Enter",
                    ["viewerActionRefreshAfter"] = "responseMode=hostHandled refreshAfter=true refreshTarget=content refreshScrollTarget=<elementId> refreshScrollMode=center|nearest|top rerenders same host-managed tool window content and scrolls after successful viewerStart/viewerStop",
                    ["refreshScrollTarget"] = "element id only; no CSS selector; applied by TvAIr host after directContent rerender",
                    ["currentRequestPath"] = currentRequestPath,
                    ["currentRequestQueryString"] = currentRequestQueryString,
                    ["currentRequestPathAndQuery"] = currentRequestPathAndQuery,
                    ["currentRequestQueryKeys"] = currentRequestQueryKeys,
                    ["currentRequestWave"] = currentRequestWave,
                    ["pluginAssetBaseUrl"] = pluginAssetBaseUrl,
                    ["pluginAssetAllowedExtensions"] = "png",
                    ["pluginAssetResolveMethod"] = "PluginUiContext.ResolveAssetUrl(assetName)",
                    ["toolWindowFormIconContract"] = "manifest/Ui.Icon .ico -> host-managed Form.Icon",
                    ["toolWindowFormIconAllowedExtension"] = "ico",
                    ["toolWindowFormIconSourcePriority"] = "EmbeddedResource>plugin_file>default_TvAIr_icon"
                }
            };
            log.Add("PLUGIN_UI_CONTEXT_ACTION_CONTRACT", plugin.Name, $"result=ISSUED route={SafePluginActionValue(actionContext.ActionRoute)} pluginActionRoute={SafePluginActionValue(actionContext.PluginActionRoute)} endpoint={SafePluginActionValue(actionContext.ActionEndpoint)} method={SafePluginActionValue(actionContext.ActionMethod)} actions={SafePluginActionValue(string.Join(",", supportedActions))} tokenPresent={!string.IsNullOrWhiteSpace(actionContext.ActionToken)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(publicRoute)} rule=release_contract");
            log.Add("PLUGIN_UI_CONTEXT_WINDOW_CONTRACT", plugin.Name, $"result=ISSUED route={SafePluginActionValue(actionContext.WindowRoute)} endpoint={SafePluginActionValue(actionContext.WindowEndpoint)} method={SafePluginActionValue(actionContext.WindowMethod)} actions={SafePluginActionValue(string.Join(",", actionContext.SupportedWindowActions))} tokenPresent={!string.IsNullOrWhiteSpace(actionContext.WindowToken)} pluginId={SafePluginActionValue(pluginId)} routeSegment={SafePluginActionValue(publicRoute)} hostManaged=True currentWindowId={SafePluginActionValue(currentWindowId)} isHostManagedWindowContent={isHostManagedWindowContent} refreshTarget=content toolWindowSupported={toolWindowCaps.ToolWindowSupported} webView2Runtime={toolWindowCaps.WebView2RuntimeAvailable} hostKind={SafePluginActionValue(toolWindowCaps.HostKind)} reuseKey={SafePluginActionValue(toolWindowCaps.ReuseKey)} positionPersistence={toolWindowCaps.SupportsPositionPersistence} statePersistence={toolWindowCaps.SupportsStatePersistence} closeSync=closeWindow_and_host_x_button rule=release_contract");
            log.Add("WINDOW_STATE_ENDPOINT_CONTRACT", plugin.Name, $"result=ISSUED currentWindowId={SafePluginActionValue(currentWindowId)} endpoint={SafePluginActionValue(currentWindowStateEndpoint)} absoluteUrl={SafePluginActionValue(currentWindowStateUrl)} currentWindowAlwaysOnTop={currentWindowAlwaysOnTop} currentWindowRevision={currentWindowRevision} currentWindowHostAlive={currentWindowHostAlive} csharpReadable={(!string.IsNullOrWhiteSpace(currentWindowStateUrl)).ToString()} stateDirectValues=PluginUiContext source=PluginUiContext.WindowContract rule=release_contract");
            log.Add("PLUGIN_UI_CONTEXT_REQUEST_CONTRACT", plugin.Name, $"result=ISSUED routeSegment={SafePluginActionValue(publicRoute)} requestPath={SafePluginActionValue(actionContext.CurrentRequestPath)} requestQuery={SafePluginActionValue(actionContext.CurrentRequestQueryString)} pathAndQuery={SafePluginActionValue(actionContext.CurrentRequestPathAndQuery)} queryKeys={SafePluginActionValue(currentRequestQueryKeys)} wave={SafePluginActionValue(currentRequestWave)} toolWindow={isHostManagedWindowContent} directContent={toolWindowContentOnly} currentWindowId={SafePluginActionValue(currentWindowId)} rule=release_contract");
            log.Add("PLUGIN_UI_CONTEXT_ASSET_CONTRACT", plugin.Name, $"result=ISSUED routeSegment={SafePluginActionValue(publicRoute)} pluginId={SafePluginActionValue(pluginId)} assetBaseUrl={SafePluginActionValue(pluginAssetBaseUrl)} apiBaseUrl={SafePluginActionValue(pluginAssetApiBaseUrl)} allowedExtensions=png imgTagAllowed=True externalUrlAllowed=False dataUriRecommended=False formIconAllowedExtensions=ico formIconSourcePriority=EmbeddedResource>plugin_file>default_TvAIr_icon rule=release_contract");
            var renderHtml = plugin.RenderHtml(actionContext);
            log.Add("PLUGIN_RENDER_RESULT", plugin.Name, $"source=plugin_render_raw routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} {BuildPluginRenderHtmlAudit(renderHtml)} rule=release_contract");
            body = PluginHtmlSanitizer.Sanitize(renderHtml ?? string.Empty);
            log.Add("PLUGIN_RENDER_RESULT", plugin.Name, $"source=plugin_render_sanitized routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} {BuildPluginRenderHtmlAudit(body)} rule=release_contract");
        }
        else
        {
            return Results.NotFound("Plugin UI not found.");
        }

        log.Add("PLUGIN_SAFE_EVENT_INJECT", plugin?.Name ?? publicRoute, $"action=render result=INJECTED routeSegment={SafePluginActionValue(publicRoute)} toolWindowContentOnly={toolWindowContentOnly} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} script=external_and_inline_guarded hostKind={SafePluginActionValue(toolWindows.GetCapabilities().HostKind)} rule=release_contract");
        return Results.Content(BuildPluginShellHtml(title, publicRoute, body, toolWindowContentOnly), "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        var exMessage = $"{ex.GetType().Name}: {ex.Message}";
        var stackSummary = (ex.StackTrace ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        if (stackSummary.Length > 500) stackSummary = stackSummary[..500];
        log.Add("PLUGIN_RENDER_EXCEPTION", plugin?.Name ?? publicRoute, $"routeSegment={SafePluginActionValue(publicRoute)} toolWindow={isHostManagedWindowContent} currentWindowId={SafePluginActionValue(currentWindowId)} directContent={toolWindowContentOnly} exceptionType={SafePluginActionValue(ex.GetType().Name)} message={SafePluginActionValue(ex.Message)} stack={SafePluginActionValue(stackSummary)} rule=release_contract");
        var errorBody = BuildPluginRenderErrorBody(plugin?.Name ?? publicRoute, publicRoute, exMessage);
        return Results.Content(BuildPluginShellHtml(title, publicRoute, errorBody, toolWindowContentOnly), "text/html; charset=utf-8");
    }
}

// release_contract: 旧AI-rithm URLは legacy alias。UI露出は常に /plugin/airhythm に統一する。
app.MapGet("/plugin/airithm", () => Results.Redirect("/plugin/airhythm", permanent: false));
app.MapGet("/plugin-ui/airithm", () => Results.Redirect("/plugin/airhythm", permanent: false));
app.MapGet("/plugin/{route}", (string route, HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, LogRepository log) => RenderPluginHtml(route, http, registry, actionTokens, windows, toolWindows, tvTestOptions, ini, tunerProfiles, log));

// 1.0.0互換URL。今後は /plugin/{route} を正式入口とする。
app.MapGet("/plugin-ui/{route}", (string route, HttpRequest http, PluginRegistry registry, PluginActionTokenStore actionTokens, PluginWindowSessionStore windows, PluginToolWindowHostService toolWindows, IOptions<TvTestSettings> tvTestOptions, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles, LogRepository log) => RenderPluginHtml(route, http, registry, actionTokens, windows, toolWindows, tvTestOptions, ini, tunerProfiles, log));


static DateTime? ParseAirhythmDateTime(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    if (DateTime.TryParse(value, out var dt)) return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
    if (DateOnly.TryParse(value, out var d)) return d.ToDateTime(TimeOnly.MinValue);
    return null;
}

static bool ContainsAirhythmText(string? source, string keyword)
    => !string.IsNullOrEmpty(source) && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);

static Dictionary<string, int> BuildAirhythmChannelOrder(ChannelFileLoader channelLoader)
{
    var channels = channelLoader.Load().Targets;
    var chOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < channels.Count; i++)
    {
        var ch = channels[i];
        chOrder.TryAdd($"{ch.OriginalNetworkId}:{ch.TransportStreamId}:{ch.ServiceId}", i);
    }
    return chOrder;
}

static int GetAirhythmChannelOrder(Dictionary<string, int> chOrder, ushort networkId, ushort tsId, ushort serviceId)
    => chOrder.TryGetValue($"{networkId}:{tsId}:{serviceId}", out var idx) ? idx : int.MaxValue;

static object ToAirhythmReservationDto(Reservation r) => new
{
    reservationId = r.Id,
    programId = $"{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}",
    r.NetworkId,
    r.TransportStreamId,
    r.ServiceId,
    r.EventId,
    r.Title,
    r.ServiceName,
    r.StartTime,
    r.EndTime,
    scheduledStartTime = r.ScheduledStartTime,
    status = r.Status.ToString(),
    source = r.Source.ToString(),
    r.IsEnabled,
    r.IsConflicted,
    r.TunerName,
    r.ActualTunerName,
    r.RecordingStartedAt,
    r.RecordingFinishedAt,
    r.SourceRuleId,
    r.SourceRuleName,
    r.IsUserChain,
    r.UserChainPreviousId,
    r.UserChainRootId,
    r.CreatedAt,
    r.UpdatedAt
};

static object DetectAirhythmProgramFlags(EpgEvent e)
{
    var text = $"{EpgProjection.Title(e)} {EpgProjection.ShortText(e)} {EpgProjection.ExtendedText(e)}";
    var projectionSafe = EpgTitleProjectionGuard.IsSafeForSpecialProjection(e, out _);
    return new
    {
        isNewProgram = projectionSafe && (text.Contains("[新]") || text.Contains("［新］") || text.Contains("【新】") || text.Contains("新番組")),
        isFinalEpisode = projectionSafe && (text.Contains("[終]") || text.Contains("［終］") || text.Contains("【終】") || text.Contains("最終回")),
        isMovie = ContainsAirhythmText(e.Genre, "映画") || ContainsAirhythmText(EpgProjection.Title(e), "映画") || ContainsAirhythmText(EpgProjection.ShortText(e), "映画"),
        isLive = text.Contains("[生]") || text.Contains("［生］") || text.Contains("【生】") || text.Contains("生中継") || text.Contains("生放送"),
        isFirstRun = text.Contains("[初]") || text.Contains("［初］") || text.Contains("【初】") || text.Contains("初放送")
    };
}

static int ScoreAirhythmCandidate(EpgEvent e)
{
    if (!EpgTitleProjectionGuard.IsSafeForSpecialProjection(e, out _)) return 0;
    var score = 0;
    var text = $"{EpgProjection.Title(e)} {EpgProjection.ShortText(e)} {EpgProjection.ExtendedText(e)} {e.Genre}";
    if (text.Contains("ドラマ")) score += 20;
    if (text.Contains("映画")) score += 20;
    if (text.Contains("アニメ")) score += 10;
    if (text.Contains("ドキュメンタリー")) score += 8;
    if (text.Contains("[新]") || text.Contains("［新］") || text.Contains("【新】") || text.Contains("新番組")) score += 30;
    if (text.Contains("[初]") || text.Contains("［初］") || text.Contains("【初】") || text.Contains("初放送")) score += 25;
    if (text.Contains("[終]") || text.Contains("［終］") || text.Contains("【終】") || text.Contains("最終回")) score += 15;
    if (e.Start.Hour >= 19 && e.Start.Hour <= 23) score += 5;
    return score;
}
// ─── バージョン情報 ───────────────────────────────────────────────

// release_contract: DirectRecorderBridge切り離し後の残存診断APIを削除。復号実行・監視はTvAIrEpgRecへ集約。
app.MapGet("/api/chain-reservation-contract", () => Results.Json(new
{
    adjacentMinGapSeconds = ChainReservationContract.AdjacentMinGapSeconds,
    adjacentMaxGapSeconds = ChainReservationContract.AdjacentMaxGapSeconds,
    adjacentMinGapMilliseconds = ChainReservationContract.AdjacentMinGapMilliseconds,
    adjacentMaxGapMilliseconds = ChainReservationContract.AdjacentMaxGapMilliseconds,
    rule = "chain_reservation_contract_shared_boundary_release_contract"
}));

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
    const string Rule = "broadcast_clock_passive_only_epg_orphan_safety";
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

static void EmitTvAIrRuntimeIdentityAudit(LogRepository log)
{
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
        log.Add("APP_BINARY_IDENTITY", "START",
            $"tvairVersion={GetTvAIrAppVersion()} tvairExe={Path.GetFileName(tvairExe)} tvairFile={Stamp(tvairExe)} " +
            $"workerFileName={Path.GetFileName(workerPath)} workerFile={Stamp(workerPath)} {releaseNotesAudit} " +
            $"baseDir=app_base rule=release_contract rollbackPoint=True rollbackBase=release_contract timePolicy=BROADCAST_CLOCK_PASSIVE_ONLY ntp=removed broadcastClock=observe_only_no_internal_offset titleQuality=record_filename_event_name_nfkc_guard pluginUiAction=host_action_dispatch_value_contract logPolicy=release_candidate_noise_reduce");
    }
    catch (Exception ex)
    {
        log.Add("APP_BINARY_IDENTITY", "ERROR", $"error={ex.GetType().Name}:{ex.Message} rule=release_contract");
    }
}


// release_contract: TvAIrEpgRec.exe のProbe/EPGジョブ契約確認API。
// --mode probe の入口だけを提供し、録画・実EPG取得・録画前EPG確認の本線には接続しない。
app.MapPost("/api/tvairepgrec/probe", async (int? keepAliveMs, LogRepository log) =>
    await RunTvAIrEpgRecProbeAsync(keepAliveMs, log));
app.MapGet("/api/tvairepgrec/probe", async (int? keepAliveMs, LogRepository log) =>
    await RunTvAIrEpgRecProbeAsync(keepAliveMs, log));


// release_contract: TvAIrEpgRec.exe --mode epg のジョブ契約確認API。
// BonDriverは開かず、通常EPG取得に必要な group/tuner/did/bonDriver/channel 情報をWorkerが解釈して返すだけに限定する。
// TvAIr release_contract cleanup:
// Obsolete TvAIrEpgRec diagnostic/plan/probe endpoints were removed from Program.cs.
// Keep only /api/tvairepgrec/probe because it is referenced by bundled documentation and remains a safe worker launch probe.

static async Task<IResult> RunTvAIrEpgRecProbeAsync(int? keepAliveMs, LogRepository log)
{
    var boundedKeepAliveMs = Math.Clamp(keepAliveMs ?? 3000, 0, 30000);
    var jobId = $"probe_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
    var runtimeDir = Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-probe");
    Directory.CreateDirectory(runtimeDir);

    var jobPath = Path.Combine(runtimeDir, $"job_{jobId}.json");
    var progressPath = Path.Combine(runtimeDir, $"progress_{jobId}.jsonl");
    var resultPath = Path.Combine(runtimeDir, $"result_{jobId}.json");
    var cancelPath = Path.Combine(runtimeDir, $"cancel_{jobId}.signal");

    var workerExe = ResolveTvAIrEpgRecExecutablePath();
    if (string.IsNullOrWhiteSpace(workerExe) || !File.Exists(workerExe))
    {
        log.Add("TVAIREPGREC_PROBE", "NG", $"result=NG reason=worker_exe_not_found checked=AppContextBaseDirectory worker=TvAIrEpgRec.exe rule=release_contract");
        return Results.NotFound(new
        {
            result = "NG",
            reason = "worker_exe_not_found",
            expectedProcessName = "TvAIrEpgRec.exe",
            checkedBaseDirectory = AppContext.BaseDirectory,
            rule = "release_contract"
        });
    }

    var job = new
    {
        jobId,
        mode = "probe",
        progressPath,
        resultPath,
        outputPath = (string?)null,
        cancelSignalPath = cancelPath,
        metadata = new Dictionary<string, string>
        {
            ["caller"] = "TvAIr",
            ["purpose"] = "worker_process_shell_probe",
            ["tvairVersion"] = GetTvAIrAppVersion(),
            ["rule"] = "release_contract"
        }
    };

    await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }));

    var startedAt = DateTimeOffset.Now;
    log.Add("TVAIREPGREC_PROBE", "START", $"jobId={jobId} exe={workerExe} keepAliveMs={boundedKeepAliveMs} rule=release_contract");

    try
    {
        using var process = new Process();
        process.StartInfo = TvAIr.Core.WorkerProcessStartInfoFactory.CreateTvAIrEpgRec(
            workerExe,
            TvAIr.Core.TvAIrEpgRecLaunchKind.DiagnosticProbe,
            showTaskbarIconSetting: false);
        process.StartInfo.ArgumentList.Add("--mode");
        process.StartInfo.ArgumentList.Add("probe");
        process.StartInfo.ArgumentList.Add("--job");
        process.StartInfo.ArgumentList.Add(jobPath);
        process.StartInfo.ArgumentList.Add("--progress");
        process.StartInfo.ArgumentList.Add(progressPath);
        process.StartInfo.ArgumentList.Add("--result");
        process.StartInfo.ArgumentList.Add(resultPath);
        process.StartInfo.ArgumentList.Add("--cancel");
        process.StartInfo.ArgumentList.Add(cancelPath);
        process.StartInfo.ArgumentList.Add("--keep-alive-ms");
        process.StartInfo.ArgumentList.Add(boundedKeepAliveMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!process.Start())
        {
            log.Add("TVAIREPGREC_PROBE", "NG", $"jobId={jobId} result=NG reason=process_start_returned_false exe={workerExe} rule=release_contract");
            return Results.Problem("TvAIrEpgRec.exe start returned false.");
        }

        var pid = process.Id;
        var timeoutMs = Math.Clamp(boundedKeepAliveMs + 5000, 5000, 40000);
        var completed = await WaitForExitWithTimeoutAsync(process, timeoutMs).ConfigureAwait(false);
        var endedAt = DateTimeOffset.Now;

        if (!completed)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            log.Add("TVAIREPGREC_PROBE", "TIMEOUT", $"jobId={jobId} pid={pid} timeoutMs={timeoutMs} action=kill_requested rule=release_contract");
            return Results.Ok(new
            {
                result = "TIMEOUT",
                jobId,
                pid,
                workerExe,
                keepAliveMs = boundedKeepAliveMs,
                timeoutMs,
                elapsedMs = (int)(endedAt - startedAt).TotalMilliseconds,
                progress = ReadRecentTextLines(progressPath, 20),
                resultPath,
                progressPath,
                rule = "release_contract"
            });
        }

        var resultJson = TryReadJsonElement(resultPath);
        var progressLines = ReadRecentTextLines(progressPath, 20);
        log.Add("TVAIREPGREC_PROBE", process.ExitCode == 0 ? "OK" : "NG", $"jobId={jobId} pid={pid} exitCode={process.ExitCode} elapsedMs={(int)(endedAt - startedAt).TotalMilliseconds} resultExists={File.Exists(resultPath)} progressLines={progressLines.Length} rule=release_contract");

        return Results.Ok(new
        {
            result = process.ExitCode == 0 ? "OK" : "NG",
            jobId,
            pid,
            exitCode = process.ExitCode,
            workerExe,
            keepAliveMs = boundedKeepAliveMs,
            elapsedMs = (int)(endedAt - startedAt).TotalMilliseconds,
            workerResult = resultJson,
            progress = progressLines,
            resultPath,
            progressPath,
            jobPath,
            expectedProcessName = "TvAIrEpgRec.exe",
            rule = "release_contract"
        });
    }
    catch (Exception ex)
    {
        log.Add("TVAIREPGREC_PROBE", "ERROR", $"jobId={jobId} error={ex.GetType().Name} message={ex.Message} rule=release_contract");
        return Results.Problem(ex.Message);
    }
}


static string? ResolveTvAIrEpgRecExecutablePath()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "TvAIrEpgRec.exe"),
        Path.Combine(AppContext.BaseDirectory, "TvAIrEpgRec", "TvAIrEpgRec.exe"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TvAIrEpgRec.exe"))
    };

    return candidates.FirstOrDefault(File.Exists);
}

static async Task<bool> WaitForExitWithTimeoutAsync(Process process, int timeoutMs)
{
    using var cts = new CancellationTokenSource(timeoutMs);
    try
    {
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        return true;
    }
    catch (OperationCanceledException)
    {
        return false;
    }
}

static JsonElement? TryReadJsonElement(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }
    catch
    {
        return null;
    }
}

static string[] ReadRecentTextLines(string path, int maxLines)
{
    try
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadLines(path).TakeLast(maxLines).ToArray();
    }
    catch
    {
        return Array.Empty<string>();
    }
}


// ─── AI-rhythm API ────────────────────────────────────────────────
// AI-rhythm本体は別DLL。TvAIr本体側はホストUI/APIのみを保持する。
app.MapGet("/api/airhythm/dashboard", (AirhythmDashboardService dashboard) =>
    Results.Ok(dashboard.Build()));

app.MapGet("/api/airhythm/profile", (AirhythmProfileService profileService) =>
    Results.Ok(profileService.Get()));

app.MapPost("/api/airhythm/profile", (AirhythmProfileSettings request, AirhythmProfileService profileService) =>
    Results.Ok(profileService.Save(request)));

app.MapPost("/api/airhythm/backup", (AirhythmBackupService backupService) =>
    Results.Ok(backupService.CreateSnapshot()));

app.MapGet("/api/airhythm/notification", (AirhythmNotificationService notificationService) =>
    Results.Ok(notificationService.GetStatus()));

app.MapPost("/api/airhythm/notification/open", (AirhythmNotificationService notificationService) =>
    Results.Ok(notificationService.MarkOpened()));

// ─── AI-rhythm 読み取り専用データAPI ─────────────────────────────
// AI-rhythmが「判断補助」に必要なTvAIr本体データを取得する正式入口。
// ここでは録画予約・チューナー操作・DB変更を一切行わない。
app.MapGet("/api/airhythm/data/summary", (
    EpgStore epgStore,
    ReservationStore reservationStore,
    TunerPool tunerPool,
    ChannelFileLoader channelLoader) =>
{
    var now = DateTime.Now;
    var epgTo = now.AddDays(7);
    var epgCount = epgStore.GetByRange(now, epgTo).Count;
    var reservations = reservationStore.GetAll()
        .Where(r => r.Source != ReservationSource.Epg)
        .ToList();
    var tuners = tunerPool.GetStatus();
    var channels = channelLoader.Load().Targets;

    return Results.Ok(new
    {
        generatedAt = now,
        mode = "readOnly",
        writeAccess = false,
        available = new
        {
            epg = true,
            reservations = true,
            reservationHistory = true,
            tunerStatus = true,
            conflicts = true,
            viewingHistory = false
        },
        counts = new
        {
            epgNext7Days = epgCount,
            reservations = reservations.Count,
            conflicts = reservations.Count(r => r.IsConflicted),
            history = reservations.Count(r => r.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed),
            tuners = tuners.Count,
            tunerFree = tuners.Count(t => t.UsageKind == TunerUsageKind.Free),
            channels = channels.Count
        },
        notes = new[]
        {
            "AI-rhythm用の正式データ入口です。DB直接参照は禁止です。",
            "このAPI群は読み取り専用です。予約登録・削除・チューナー操作は行いません。",
            "視聴履歴はTvAIr本体が保持していないため、現時点では未提供です。"
        }
    });
});

app.MapGet("/api/airhythm/data/epg", (
    string? from,
    string? to,
    int? days,
    int? limit,
    string? keyword,
    ushort? networkId,
    ushort? tsId,
    ushort? serviceId,
    string? genre,
    EpgStore epgStore,
    ChannelFileLoader channelLoader) =>
{
    var start = ParseAirhythmDateTime(from) ?? DateTime.Now;
    var maxDays = Math.Clamp(days ?? 7, 1, 14);
    var end = ParseAirhythmDateTime(to) ?? start.AddDays(maxDays);
    var take = Math.Clamp(limit ?? 5000, 1, 20000);

    IEnumerable<EpgEvent> events = epgStore.GetByRange(start, end);
    if (networkId.HasValue) events = events.Where(e => e.NetworkId == networkId.Value);
    if (tsId.HasValue) events = events.Where(e => e.TransportStreamId == tsId.Value);
    if (serviceId.HasValue) events = events.Where(e => e.ServiceId == serviceId.Value);
    if (!string.IsNullOrWhiteSpace(keyword))
    {
        var kw = keyword.Trim();
        events = events.Where(e => ContainsAirhythmText(EpgProjection.Title(e), kw) || ContainsAirhythmText(EpgProjection.ShortText(e), kw) || ContainsAirhythmText(EpgProjection.ExtendedText(e), kw));
    }
    if (!string.IsNullOrWhiteSpace(genre))
    {
        var g = genre.Trim();
        events = events.Where(e => ContainsAirhythmText(e.Genre, g) || ContainsAirhythmText(e.GenreCodes, g));
    }

    var chOrder = BuildAirhythmChannelOrder(channelLoader);
    var result = events
        .OrderBy(e => e.Start)
        .ThenBy(e => GetAirhythmChannelOrder(chOrder, e.NetworkId, e.TransportStreamId, e.ServiceId))
        .Take(take)
        .Select(e => new
        {
            programId = $"{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}",
            e.NetworkId,
            e.TransportStreamId,
            e.ServiceId,
            e.EventId,
            e.ServiceName,
            Title = EpgProjection.Title(e),
            Description = EpgProjection.ShortText(e),
            ExtendedText = EpgProjection.ExtendedText(e),
            e.Genre,
            e.GenreCodes,
            e.DurationSeconds,
            startTime = e.Start,
            endTime = e.End,
            e.UpdatedAt,
            flags = DetectAirhythmProgramFlags(e)
        })
        .ToList();

    return Results.Ok(new { generatedAt = DateTime.Now, mode = "readOnly", from = start, to = end, count = result.Count, events = result });
});

app.MapGet("/api/airhythm/data/reservations", (
    bool? includeEpgSystemEntries,
    bool? enabled,
    bool? conflicted,
    string? source,
    string? status,
    string? from,
    string? to,
    ReservationStore reservationStore) =>
{
    var start = ParseAirhythmDateTime(from);
    var end = ParseAirhythmDateTime(to);
    IEnumerable<Reservation> reservations = reservationStore.GetAll();

    if (includeEpgSystemEntries != true) reservations = reservations.Where(r => r.Source != ReservationSource.Epg);
    if (enabled.HasValue) reservations = reservations.Where(r => r.IsEnabled == enabled.Value);
    if (conflicted.HasValue) reservations = reservations.Where(r => r.IsConflicted == conflicted.Value);
    if (!string.IsNullOrWhiteSpace(source)) reservations = reservations.Where(r => string.Equals(r.Source.ToString(), source.Trim(), StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(status)) reservations = reservations.Where(r => string.Equals(r.Status.ToString(), status.Trim(), StringComparison.OrdinalIgnoreCase));
    if (start.HasValue) reservations = reservations.Where(r => r.EndTime > start.Value);
    if (end.HasValue) reservations = reservations.Where(r => r.StartTime < end.Value);

    var result = reservations.OrderBy(r => r.StartTime).Select(ToAirhythmReservationDto).ToList();
    return Results.Ok(new { generatedAt = DateTime.Now, mode = "readOnly", count = result.Count, reservations = result });
});

app.MapGet("/api/airhythm/data/history", (
    string? from,
    string? to,
    int? limit,
    ReservationStore reservationStore) =>
{
    var start = ParseAirhythmDateTime(from);
    var end = ParseAirhythmDateTime(to);
    var take = Math.Clamp(limit ?? 1000, 1, 10000);

    IEnumerable<Reservation> history = reservationStore.GetAll()
        .Where(r => r.Source != ReservationSource.Epg)
        .Where(r => r.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed || r.RecordingStartedAt.HasValue || r.RecordingFinishedAt.HasValue);
    if (start.HasValue) history = history.Where(r => r.EndTime >= start.Value);
    if (end.HasValue) history = history.Where(r => r.StartTime < end.Value);

    var result = history.OrderByDescending(r => r.EndTime).Take(take).Select(ToAirhythmReservationDto).ToList();
    return Results.Ok(new { generatedAt = DateTime.Now, mode = "readOnly", count = result.Count, history = result, notes = new[] { "再生履歴・視聴時間はTvAIr本体が保持していないため、この履歴には含まれません。" } });
});

app.MapGet("/api/airhythm/data/tuners", (TunerPool tunerPool) =>
{
    var slots = tunerPool.GetStatus()
        .OrderBy(t => t.Group)
        .ThenBy(t => t.SlotIndex)
        .Select(t => new
        {
            t.Name,
            t.BonDriverFileName,
            t.Did,
            t.Group,
            t.Role,
            t.SlotIndex,
            usageKind = t.UsageKind.ToString(),
            isFree = t.UsageKind == TunerUsageKind.Free,
            t.ReservationId,
            t.ProcessId,
            t.PlannedEndTime
        })
        .ToList();
    return Results.Ok(new { generatedAt = DateTime.Now, mode = "readOnly", total = slots.Count, free = slots.Count(s => s.isFree), busy = slots.Count(s => !s.isFree), slots });
});

// ─── 外部視聴チューナー要求 API（AIrCon 連携用）────────────────
// 録画・チェーン予約を最優先し、空きチューナーがある場合だけ Viewing として貸し出す。
app.MapPost("/api/tuners/external/request", (ExternalTunerLeaseRequest request, ExternalTunerLeaseService externalTuners) =>
{
    var result = externalTuners.Request(request);
    if (!result.Success)
    {
        return Results.Conflict(new
        {
            ok = false,
            result.Reason,
            result.Status,
            message = "空きチューナーがないため、外部視聴用チューナー要求を拒否しました。録画・チェーン予約は保護されています。"
        });
    }

    return Results.Ok(new
    {
        ok = true,
        result.Reused,
        lease = result.Lease,
        launch = result.Lease is null ? null : new
        {
            bonDriver = result.Lease.BonDriverFileName,
            did = result.Lease.Did,
            tunerName = result.Lease.TunerName,
            note = "AIrCon側でTVTest/LIVETestを起動する場合は、このBonDriver/DIDを使用してください。TvAIr本体は起動しません。"
        }
    });
});

app.MapPost("/api/tuners/external/release", (ExternalTunerReleaseRequest request, ExternalTunerLeaseService externalTuners) =>
{
    var ok = externalTuners.Release(request.LeaseId ?? string.Empty, request.Source);
    return ok
        ? Results.Ok(new { ok = true, message = "外部視聴用チューナーを解放しました。" })
        : Results.NotFound(new { ok = false, message = "指定された外部視聴用チューナー貸出は見つかりません。" });
});

app.MapGet("/api/tuners/external/leases", (ExternalTunerLeaseService externalTuners) =>
{
    var leases = externalTuners.GetActiveLeases();
    return Results.Ok(new { generatedAt = DateTime.Now, count = leases.Count, leases });
});

app.MapGet("/api/airhythm/data/conflicts", (ReservationStore reservationStore) =>
{
    var conflicts = reservationStore.GetAll()
        .Where(r => r.Source != ReservationSource.Epg && r.IsConflicted)
        .OrderBy(r => r.StartTime)
        .Select(r => new
        {
            reservationId = r.Id,
            programId = $"{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}",
            r.Title,
            r.ServiceName,
            r.StartTime,
            r.EndTime,
            r.TunerName,
            r.ActualTunerName,
            r.IsEnabled,
            r.IsUserChain,
            r.UserChainPreviousId,
            r.UserChainRootId,
            reason = "チューナー割当競合"
        })
        .ToList();
    return Results.Ok(new { generatedAt = DateTime.Now, mode = "readOnly", count = conflicts.Count, conflicts });
});

app.MapGet("/api/airhythm/data/channels", (ChannelFileLoader channelLoader) =>
{
    var loaded = channelLoader.Load();
    return Results.Ok(new
    {
        generatedAt = DateTime.Now,
        mode = "readOnly",
        loaded.Message,
        loaded.Files,
        loaded.Warnings,
        count = loaded.Targets.Count,
        channels = loaded.Targets.Select((t, index) => new { index, t.Group, t.ServiceId, t.OriginalNetworkId, t.TransportStreamId, t.Name, t.ChannelArgument })
    });
});

app.MapGet("/api/airhythm/data/candidates", (
    int? days,
    int? limit,
    EpgStore epgStore,
    ReservationStore reservationStore,
    ChannelFileLoader channelLoader) =>
{
    var now = DateTime.Now;
    var to = now.AddDays(Math.Clamp(days ?? 7, 1, 14));
    var take = Math.Clamp(limit ?? 300, 1, 2000);
    var reservedProgramIds = reservationStore.GetAll()
        .Where(r => r.Source != ReservationSource.Epg)
        .Select(r => $"{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var chOrder = BuildAirhythmChannelOrder(channelLoader);

    var candidates = epgStore.GetByRange(now, to)
        .Where(e => !reservedProgramIds.Contains($"{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}"))
        .Select(e => new { Event = e, Flags = DetectAirhythmProgramFlags(e), Score = ScoreAirhythmCandidate(e) })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Event.Start)
        .ThenBy(x => GetAirhythmChannelOrder(chOrder, x.Event.NetworkId, x.Event.TransportStreamId, x.Event.ServiceId))
        .Take(take)
        .Select(x => new
        {
            programId = $"{x.Event.NetworkId}:{x.Event.TransportStreamId}:{x.Event.ServiceId}:{x.Event.EventId}",
            x.Event.NetworkId,
            x.Event.TransportStreamId,
            x.Event.ServiceId,
            x.Event.EventId,
            x.Event.ServiceName,
            Title = EpgProjection.Title(x.Event),
            Description = EpgProjection.ShortText(x.Event),
            ExtendedText = EpgProjection.ExtendedText(x.Event),
            x.Event.Genre,
            x.Event.GenreCodes,
            startTime = x.Event.Start,
            endTime = x.Event.End,
            score = x.Score,
            flags = x.Flags
        })
        .ToList();

    return Results.Ok(new { generatedAt = DateTime.Now, mode = "readOnly", count = candidates.Count, candidates, notes = new[] { "このscoreはAI-rhythm本体の最終判断ではなく、TvAIr側の軽量候補抽出用スコアです。" } });
});

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

// EPG取得ジョブ実行契約状態（メニューガード用）
app.MapGet("/api/epg/run-state", (EpgScheduler scheduler) =>
    Results.Ok(scheduler.GetRunState()));

// TvAIr終了（メニュー操作用）
app.MapPost("/api/app/exit", (string? source, LogRepository log) =>
{
    var safeSource = string.IsNullOrWhiteSpace(source) ? "WebMenu" : source.Trim().Replace("\r", " ").Replace("\n", " ");
    try { log.Add("APP_EXIT_REQUEST", "Menu", $"source={safeSource} action=EnvironmentExit rule=release_contract"); } catch { }
    _ = Task.Run(async () =>
    {
        await Task.Delay(150).ConfigureAwait(false);
        Environment.Exit(0);
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
    var accepted = scheduler.Cancel(source);
    var runState = scheduler.GetRunState();
    return Results.Ok(new { accepted, runState, message = accepted ? "キャンセル要求を送信しました。" : "実行中のEPG取得はありません。" });
});

// 番組表データ取得（日付指定）
app.MapGet("/api/epg/events", (string? date, EpgStore store, ReservationStore reservations, ChannelFileLoader channelLoader, LogRepository log) =>
{
    var baseDate = DateOnly.TryParse(date, out var parsed)
        ? parsed
        : DateOnly.FromDateTime(DateTime.Now);

    var dayStart = baseDate.ToDateTime(new TimeOnly(4, 0));  // 4:00 開始（TvRock準拠）
    var dayEnd   = dayStart.AddDays(1);

    var rawEvents = store.GetAllRaw();
    var events = rawEvents;
    var dayEvents = rawEvents.Where(e => e.End > dayStart && e.Start < dayEnd).ToList();
    var displayDate = dayStart.ToString("M月d日・dddd",
        System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));

    // ch2ファイルのState=1チャンネルのみを対象にフィルタ＆ソート（TVTest準拠）
    var channels = channelLoader.Load().Targets.ToList();
    var chOrder = new Dictionary<string, int>();
    for (var ci = 0; ci < channels.Count; ci++)
    {
        var ch = channels[ci];
        var key = ProgramGuideServiceKey3(ch.OriginalNetworkId, ch.TransportStreamId, ch.ServiceId);
        chOrder.TryAdd(key, ci);
    }
    foreach (var evGroup in rawEvents.GroupBy(e => ProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId)))
    {
        if (chOrder.ContainsKey(evGroup.Key)) continue;
        var first = evGroup.First();
        channels.Add(new ChannelTarget
        {
            Group = ProgramGuideWaveGroupFromNetworkId(first.NetworkId),
            OriginalNetworkId = first.NetworkId,
            TransportStreamId = first.TransportStreamId,
            ServiceId = first.ServiceId,
            Name = string.IsNullOrEmpty(first.ServiceName) ? $"SID {first.ServiceId}" : first.ServiceName,
            ChannelBuildSource = "epg_db_raw_unsealed"
        });
        chOrder.TryAdd(evGroup.Key, chOrder.Count);
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
        .ThenBy(e => e.TableId)
        .ThenBy(e => e.EventId)
        .ToList();

    var timelineEvents = BuildProgramGuideTimelineEvents(
        sortedEvents,
        channels,
        dayStart,
        dayEnd,
        store,
        log,
        baseDate);

    var displayEvents = timelineEvents
        .Select(e => NormalizeProgramGuideEventForDisplay(e, serviceDisplayNameByKey))
        .ToList();

    var rawBlankTitleCount = displayEvents.Count(e => string.IsNullOrEmpty(e.CellText.Title));
    var titleLessDescriptorCount = displayEvents.Count(e => string.IsNullOrEmpty(e.CellText.Title) && string.IsNullOrEmpty(e.RawShortEventDescriptorHex));
    var rawExtendedDescriptorHexCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.RawExtendedEventDescriptorHex));
    var cellTitleCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Title));
    var cellOutlineCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Outline));
    var cellDetailCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Detail));
    var cellItemsCount = displayEvents.Count(e => !string.IsNullOrEmpty(e.CellText.Items));

    log.Add("PROGRAMGUIDE_CELL_TEXT_DIRECT_HANDOFF", "API",
        $"result=OK date={baseDate:yyyy-MM-dd} events={displayEvents.Count} blankTitle={rawBlankTitleCount} titleLessDescriptor={titleLessDescriptorCount} rawExtendedDescriptorHex={rawExtendedDescriptorHexCount} cellTitle={cellTitleCount} cellOutline={cellOutlineCount} cellDetail={cellDetailCount} cellItems={cellItemsCount} cellTextSource=db_raw_descriptor_common_decoder boundary=outline_detail_separator_kept dbReadFilters=none ch2Filter=removed reservationTitleBorrow=removed dtoDirectField=cellText legacyBodyFields=deleted rule=release_contract");

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
    EpgStore store) =>
{
    var ev = store.GetOne(networkId, tsId, serviceId, eventId);
    return ev is null ? Results.NotFound() : Results.Ok(ev);
});

// 番組検索
app.MapGet("/api/epg/search", (
    string?  q,            // キーワード
    bool?    desc,         // 説明文も検索
    string?  sids,         // サービスID カンマ区切り
    string?  dow,          // 曜日 カンマ区切り (0=日〜6=土)
    int?     timeFrom,     // 開始時間 (0〜23)
    int?     timeTo,       // 終了時間 (1〜24)
    string?  dateFrom,     // 期間開始 yyyy-MM-dd
    string?  dateTo,       // 期間終了 yyyy-MM-dd
    EpgStore store,
    ChannelFileLoader channelLoader) =>
{
    var serviceIds = sids?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => ushort.TryParse(s.Trim(), out var v) ? (ushort?)v : null)
        .Where(v => v.HasValue).Select(v => v!.Value);
    var days = dow?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(d => int.TryParse(d.Trim(), out var v) ? (int?)v : null)
        .Where(v => v.HasValue).Select(v => v!.Value);
    var from = DateOnly.TryParse(dateFrom, out var df)
        ? df.ToDateTime(TimeOnly.MinValue) : (DateTime?)null;
    var to   = DateOnly.TryParse(dateTo,   out var dt)
        ? dt.ToDateTime(TimeOnly.MaxValue) : (DateTime?)null;

    var events = store.Search(q, desc ?? false, serviceIds, days, timeFrom, timeTo, from, to);

    // チャンネル順でソート
    var channels = channelLoader.Load().Targets;
    var chOrder  = new Dictionary<string, int>();
    for (var i = 0; i < channels.Count; i++)
    {
        var ch  = channels[i];
        var key = $"{ch.OriginalNetworkId}:{ch.TransportStreamId}:{ch.ServiceId}";
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
            e.ServiceName,
            Title = EpgProjection.Title(e),
            Description = EpgProjection.ShortText(e),
            e.Genre,
            GenreCodes = e.GenreCodes,
            e.DurationSeconds, e.Start, e.End
        });

    return Results.Ok(new { count = events.Count, events = sorted });
});

app.MapGet("/api/epg/tagged", (
    string kind,
    EpgStore store,
    ChannelFileLoader channelLoader) =>
{
    var now = DateTime.Now;
    var to = now.AddDays(7);
    var events = store.GetByRange(now, to);

    Func<EpgEvent, bool> match = kind?.ToLowerInvariant() switch
    {
        "newprogram" => e =>
        {
            var title = EpgProjection.Title(e) ?? string.Empty;
            return title.Contains("[新]") || title.Contains("［新］") || title.Contains("【新】") || title.Contains("新番組");
        },
        "finalepisode" => e =>
        {
            var title = EpgProjection.Title(e) ?? string.Empty;
            return title.Contains("[終]") || title.Contains("［終］") || title.Contains("【終】") || title.Contains("最終回");
        },
        _ => _ => false
    };

    var channels = channelLoader.Load().Targets;
    var chOrder = new Dictionary<string, int>();
    for (var i = 0; i < channels.Count; i++)
    {
        var ch = channels[i];
        var key = $"{ch.OriginalNetworkId}:{ch.TransportStreamId}:{ch.ServiceId}";
        chOrder.TryAdd(key, i);
    }

    var filtered = events
        .Where(match)
        .GroupBy(e => new { e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId })
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
            Title = EpgProjection.Title(e),
            Description = EpgProjection.ShortText(e),
            e.Genre,
            GenreCodes = e.GenreCodes,
            e.DurationSeconds, e.Start, e.End,
            titleProjectionSafe = false, titleProjectionReason = "raw_unsealed"
        })
        .ToList();

    return Results.Ok(new { count = filtered.Count, events = filtered, rule = "release_contract" });
});

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
            RefreshWakeTask: false));

        var json = store.ReadTunerAllocationDebugJson();
        return json is null
            ? Results.NotFound(new { message = "keyword route replay failed." })
            : Results.Text(json, "application/json; charset=utf-8");
    });
}



app.MapGet("/api/log", (int? count, LogRepository log) =>
{
    var entries = count.HasValue
        ? log.GetRecent(count.Value)
        : log.GetAll();
    return Results.Ok(entries);
});

app.MapGet("/api/user-events", (int? count, string? severity, string? category, UserEventLogService userEvents) =>
{
    var max = Math.Clamp(count ?? 100, 1, 1000);
    return Results.Ok(userEvents.GetRecent(max, severity, category));
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

app.MapPost("/api/reservations/refresh", (ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    try
    {
        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "ReservationList",
            Action: "ManualRefresh",
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: true,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: true,
            RefreshWakeTask: true,
            EmitConflictLogs: true,
            ConflictLogCategory: "ReservationAPI",
            ConflictLogTitle: "RefreshConflict"));
        log.Add("ReservationAPI", "Refresh", "予約一覧の手動更新で再割り当てを実行しました。");
        return Results.Ok(new { message = "予約一覧を更新しました。" });
    }
    catch (Exception ex)
    {
        log.Add("ReservationAPI", "Refresh", $"/api/reservations/refresh 失敗: {ex}");
        return Results.BadRequest(new { message = "予約一覧の更新に失敗しました。" });
    }
});

// 予約追加（番組表からの直接予約）
app.MapPost("/api/reservations", (HttpRequest request, Reservation r, ReservationStore store, EpgStore epgStore, ChannelFileLoader channelLoader, ReservationAllocationRouteService allocationRoute, EpgScheduler epgScheduler, IOptions<EpgSettings> epgSettings, LogRepository log) =>
{
    try
    {
        // release_contract: 番組表の「予約」と「今すぐ録画」は同じAPI入口を通るが、
        // ファイル名タイムスタンプ基準が異なるため source をここで確定する。
        // フロントから Immediate が明示された場合だけ今すぐ録画、それ以外は番組表予約。
        r.Source = r.Source == ReservationSource.Immediate
            ? ReservationSource.Immediate
            : ReservationSource.Manual;

        var requestEvent = r.EventId == 0 ? null : epgStore.GetOne(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);
        var sourceText = r.Source == ReservationSource.Immediate ? "Immediate" : "Manual";
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
        // REC_FOLLOW_UPDATE 側の巻き戻りガードより前に、初期予約作成時点で過去開始を作らない。
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

        // release_contract:
        // 番組表/局別リスト/予約ボタンの正規Add入口で同一親予約を1件に正規化する。
        // SystemEpg/PreRecEpg子予約はReservationStore側で親予約扱いしない。
        // 既存予約がある場合は新規IDを作らず、以後の割当は既存の共通ALLOC_ROUTEに任せる。
        var duplicate = store.FindActiveParentDuplicate(r);
        if (duplicate is not null)
        {
            log.Add("RESERVATION_DEDUPE", sourceText,
                $"result=REUSE_EXISTING existing=R{duplicate.Id} requestedSource={sourceText} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(r.Title)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} start={r.StartTime:MM/dd HH:mm} end={r.EndTime:MM/dd HH:mm} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
            var existing = store.GetById(duplicate.Id);
            return Results.Ok(new { id = duplicate.Id, message = "既に予約済みです。", isConflicted = existing?.IsConflicted ?? false, reused = true });
        }

        var cancelEpgConfirmed = request.Query.TryGetValue("cancelEpgIfRunning", out var cancelQ)
            && string.Equals(cancelQ.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var preAddRunState = epgScheduler.GetRunState();
        if (cancelEpgConfirmed && preAddRunState.IsRunning)
        {
            var accepted = epgScheduler.Cancel($"ReservationAdd.EpgCancelConfirmed.{sourceText}");
            log.Add("EPG_RESERVATION_ADD_CANCEL_CONFIRMED", sourceText,
                $"result={(accepted ? "REQUESTED" : "NO_RUNNING_EPG")} targetScope={preAddRunState.TargetScope} source={preAddRunState.Source} uiMode={preAddRunState.UiMode} action=cancel_epg_before_reservation_add rule=release_contract");
        }
        else if (preAddRunState.IsRunning)
        {
            log.Add("EPG_RESERVATION_ADD_CANCEL_DECLINED", sourceText,
                $"targetScope={preAddRunState.TargetScope} source={preAddRunState.Source} uiMode={preAddRunState.UiMode} action=continue_epg_and_add_reservation rule=release_contract");
        }

        log.Add("RESERVE_ENTRY", sourceText, $"共通入口要求 source={sourceText} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(r.Title)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm} end={r.EndTime:MM/dd HH:mm} rule=release_contract");
        var id = store.Add(r);
        log.Add("Reservation", "Add", $"予約追加: service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] id=R{id} source={sourceText} {r.StartTime:HH:mm}〜{r.EndTime:HH:mm} rule=release_contract");

        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: routeSource,
            Action: "Add",
            RunKeywordMatcher: true,
            SyncProgramRuleReservations: true,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: true,
            RefreshWakeTask: true,
            EmitConflictLogs: true,
            ConflictLogCategory: "Reservation",
            ConflictLogTitle: "Conflict"));

        var added = store.GetById(id);

        // release_contract: EPG取得中の新規予約は、UI確認で了承された場合だけEPG全体をキャンセルする。
        // 了承しない場合は予約追加のみ行い、既存の実行中EPGは継続する。


        return Results.Ok(new { id, message = "予約しました。", isConflicted = added?.IsConflicted ?? false });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});


// ユーザー明示チェーン予約（番組表の緑「チェーン」ボタンからのみ使用）
app.MapPost("/api/reservations/chain", (Reservation r, ReservationStore store, EpgStore epgStore, ChannelFileLoader channelLoader, ReservationAllocationRouteService allocationRoute, IniSettingsService ini, LogRepository log, UserEventLogService userEvents) =>
{
    try
    {
        // release_contract: チェーン予約は「後番組優先ON＋チェーン録画ON」を利用条件にしたうえで、
        // ユーザーが番組表のチェーンボタンで明示指定した場合だけ成立する予約契約。
        // 自動救済ではなく、同一SID連続番組だけをAPI入口でも強制検証する。
        log.Add("RESERVE_ENTRY", "UserChainPolicy",
            $"later={ini.LaterProgramPriority} chain={ini.PseudoContinuousRecording} explicitButton=True title=[{ReservationUserTitleLogValue(r.Title)}] service=[{r.ServiceName}] rule=release_contract");

        var requestEvent = r.EventId == 0 ? null : epgStore.GetOne(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);

        if (!(ini.LaterProgramPriority && ini.PseudoContinuousRecording))
        {
            log.Add("RESERVE_ENTRY", "UserChainRejected",
                $"チェーン予約拒否: reason=chain_requires_prefer_later_program_and_pseudo_continuous later={ini.LaterProgramPriority} chain={ini.PseudoContinuousRecording}");
            return Results.Conflict(new { message = "チェーン予約は後番組優先＋チェーン予約オプションが有効な場合のみ使用できます。" });
        }

        if (!r.UserChainPreviousId.HasValue)
            return Results.BadRequest(new { message = "チェーン元予約が指定されていません。" });

        var predecessor = store.GetById(r.UserChainPreviousId.Value);
        if (predecessor is null)
            return Results.BadRequest(new { message = "チェーン元予約が見つかりません。" });
        if (predecessor.Status is not (ReservationStatus.Scheduled or ReservationStatus.Recording))
            return Results.Conflict(new { message = "チェーン元予約が有効な予約状態ではありません。" });
        if (!predecessor.IsEnabled)
            return Results.Conflict(new { message = "チェーン元予約が無効化されています。" });
        var predecessorTuner = !string.IsNullOrWhiteSpace(predecessor.ActualTunerName)
            ? predecessor.ActualTunerName
            : predecessor.TunerName;
        if (predecessor.IsConflicted || string.IsNullOrWhiteSpace(predecessorTuner))
            return Results.Conflict(new { message = "チェーン元予約のチューナーが未確定です。" });

        var predecessorEvent = predecessor.EventId == 0
            ? null
            : epgStore.GetOne(predecessor.NetworkId, predecessor.TransportStreamId, predecessor.ServiceId, predecessor.EventId);

        r.StartTime = r.StartTime.ToLocalTime();
        r.EndTime   = r.EndTime.ToLocalTime();

        if (!ApplyReservationTitleQualityGuard(r, requestEvent, "UserChain", log, out var rawTitleError))
            return Results.BadRequest(new { message = rawTitleError });

        var chainGapSeconds = (int)Math.Round((r.StartTime - predecessor.EndTime).TotalSeconds);
        var chainSameNetwork = predecessor.NetworkId == r.NetworkId;
        var chainSameTransport = predecessor.TransportStreamId == r.TransportStreamId;
        var chainSameService = predecessor.ServiceId == r.ServiceId;
        var chainSameChannel = chainSameNetwork && chainSameTransport && chainSameService;
        var chainAdjacent = ChainReservationContract.IsAdjacent(predecessor.EndTime, r.StartTime);

        if (!chainSameChannel)
        {
            log.Add("RESERVE_ENTRY", "UserChainRejected",
                $"reason=chain_requires_same_sid predecessor=R{predecessor.Id} sameNetwork={chainSameNetwork} sameTransport={chainSameTransport} sameService={chainSameService} prevNid={predecessor.NetworkId} prevTsid={predecessor.TransportStreamId} prevSid={predecessor.ServiceId} nextNid={r.NetworkId} nextTsid={r.TransportStreamId} nextSid={r.ServiceId} rule=release_contract");
            return Results.Conflict(new { message = "チェーン予約は同一局（同一NID/TSID/SID）の連続番組のみ使用できます。" });
        }

        if (!chainAdjacent)
        {
            log.Add("RESERVE_ENTRY", "UserChainRejected",
                $"reason=chain_requires_adjacent_program predecessor=R{predecessor.Id} gapSec={chainGapSeconds} prevEnd={predecessor.EndTime:MM/dd HH:mm:ss} nextStart={r.StartTime:MM/dd HH:mm:ss} rule=release_contract");
            return Results.Conflict(new { message = "チェーン予約は同一時刻を跨ぐ連続番組のみ使用できます。" });
        }

        var existingReservation = store.GetActiveByEvent(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);

        r.Source = ReservationSource.Manual;
        r.IsUserChain = true;
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

        log.Add("RESERVE_ENTRY", "UserChain", $"共通入口要求 source=UserChain service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] predecessor=R{predecessor.Id} root=R{r.UserChainRootId} inheritTuner=[{predecessorTuner}] nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm} end={r.EndTime:MM/dd HH:mm} executionMode={chainExecutionMode} commonRoute=ALLOC_ROUTE rule=release_contract");
        log.Add("CHAIN_COMMON_ENTRY", $"R{predecessor.Id}->pending",
            $"button=Chain route=ALLOC_ROUTE executionMode={chainExecutionMode} normalRecordingRouteTouched=False prevService=[{predecessor.ServiceName}] prevTitle=[{ReservationUserTitleLogValue(predecessor.Title)}] nextService=[{r.ServiceName}] nextTitle=[{ReservationUserTitleLogValue(r.Title)}] prevTuner={predecessorTuner} prevActualTuner={(string.IsNullOrWhiteSpace(predecessor.ActualTunerName) ? "-" : predecessor.ActualTunerName)} root=R{r.UserChainRootId} rule=release_contract");
        log.Add("CHAIN_PAIR_EVAL", $"R{predecessor.Id}->pending",
            $"result=READY_FOR_COMMON_ALLOC_ROUTE sameNetwork={chainSameNetwork} sameTransport={chainSameTransport} sameService={chainSameService} sameChannel={chainSameChannel} adjacent={chainAdjacent} gapSec={chainGapSeconds} userChain=True executionMode={chainExecutionMode} contract=same_sid_adjacent_explicit_button note=execution_layer_scaffold_only prevStart={predecessor.StartTime:MM/dd HH:mm:ss} prevEnd={predecessor.EndTime:MM/dd HH:mm:ss} nextStart={r.StartTime:MM/dd HH:mm:ss} nextEnd={r.EndTime:MM/dd HH:mm:ss} rule=release_contract");
        log.Add("CHAIN_CONTRACT_WARNING", $"R{predecessor.Id}->pending",
            $"accepted=True frontSegmentMayBeCut=True successorCompletenessPriority=True sameSidOnly=True userExplicitButton=True message=チェーン予約では前番組の後半がカットされる可能性があります rule=release_contract");

        int id;
        if (existingReservation is not null)
        {
            id = existingReservation.Id;
            store.UpdateUserChainLink(id, predecessor.Id, r.UserChainRootId.Value);
            log.Add("Reservation", "ChainConvert", $"既存予約をチェーン予約へ昇格: service=[{existingReservation.ServiceName}] title=[{ReservationUserTitleLogValue(existingReservation.Title)}] id=R{id} predecessor=R{predecessor.Id} tuner=[{predecessorTuner}] executionMode={chainExecutionMode} wasConflicted={existingReservation.IsConflicted} {existingReservation.StartTime:HH:mm}〜{existingReservation.EndTime:HH:mm} rule=release_contract");
            log.Add("CHAIN_EXECUTION_MODE", $"R{id}", $"mode={chainExecutionMode} stage=existing_reservation_converted commonRoute=ALLOC_ROUTE normalExecutorFrozen=True bridgeHandoffImplemented=False predecessor=R{predecessor.Id} rule=release_contract");
        }
        else
        {
            id = store.Add(r);
            log.Add("Reservation", "ChainAdd", $"チェーン予約追加: service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] id=R{id} predecessor=R{predecessor.Id} tuner=[{predecessorTuner}] executionMode={chainExecutionMode} {r.StartTime:HH:mm}〜{r.EndTime:HH:mm} rule=release_contract");
            log.Add("CHAIN_EXECUTION_MODE", $"R{id}", $"mode={chainExecutionMode} stage=new_chain_reservation_added commonRoute=ALLOC_ROUTE normalExecutorFrozen=True bridgeHandoffImplemented=False predecessor=R{predecessor.Id} rule=release_contract");
        }

        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "UserChainReservation",
            Action: "Add",
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: true,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: true,
            RefreshWakeTask: true,
            EmitConflictLogs: true,
            ConflictLogCategory: "Reservation",
            ConflictLogTitle: "Conflict",
            ExecutionMode: chainExecutionMode));

        var added = store.GetById(id);
        log.Add("CHAIN_COMMON_ENTRY", $"R{id}", $"result=ROUTED_TO_ALLOC_ROUTE executionMode={chainExecutionMode} isConflicted={(added?.IsConflicted.ToString() ?? "-")} assignedTuner={(string.IsNullOrWhiteSpace(added?.TunerName) ? "-" : added!.TunerName)} actualTuner={(string.IsNullOrWhiteSpace(added?.ActualTunerName) ? "-" : added!.ActualTunerName)} predecessor=R{predecessor.Id} normalRecordingRouteTouched=False rule=release_contract");
        if (added is not null)
            userEvents.AddChainReservationAdded(predecessor, added, id, existingReservation is not null);
        return Results.Ok(new { id, message = existingReservation is null ? "チェーン予約しました。" : "既存予約をチェーン予約に変更しました。", isConflicted = added?.IsConflicted ?? false, tunerName = added?.TunerName ?? "" });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});
// 予約キャンセル/物理削除
// scheduled はキャンセル状態へ移行し、completed/failed/cancelled は物理削除する。
// ユーザー明示チェーンは GetUserChainCancelTargets で対象範囲を決定し、
// 解除後も必ず共通割り当てルートで再評価する。
app.MapDelete("/api/reservations/{id}", (int id, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log, UserEventLogService userEvents) =>
{
    var r = store.GetById(id);
    if (r is null) return Results.NotFound();

    // release_contract: 番組表セルが内部用の録画前EPG確認(SystemEpg)を拾ってしまっても、
    // 取消対象は親の実録画予約へ向ける。SystemEpgは番組表上の通常予約として扱わない。
    if (r.Source == ReservationSource.Epg
        && r.SourceRuleId.HasValue
        && (string.Equals(r.SourceRuleName, "PreRecEpg", StringComparison.OrdinalIgnoreCase)
            || r.Title.StartsWith("EPG確認", StringComparison.Ordinal)))
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
        store.Delete(id);
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
    store.UpdateStatusMany(targetIds, ReservationStatus.Cancelled);

    var isChainCancel = targetIds.Count > 1 || chainTargets.Any(x => x.IsUserChain || x.UserChainPreviousId.HasValue || x.UserChainRootId.HasValue);
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
        Source: "ReservationList",
        Action: targetIds.Count > 1 ? "ChainCancel" : "Cancel",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "Reservation",
        ConflictLogTitle: "Conflict"));

    return Results.Ok(new { message = targetIds.Count > 1 ? $"チェーン予約を{targetIds.Count}件キャンセルしました。" : "キャンセルしました。", cancelledIds = targetIds });
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

    if (r.Source == ReservationSource.Epg
        && r.SourceRuleId.HasValue
        && (string.Equals(r.SourceRuleName, "PreRecEpg", StringComparison.OrdinalIgnoreCase)
            || r.Title.StartsWith("EPG確認", StringComparison.Ordinal)
            || EpgTitleProjectionGuard.IsInternalPurposeTitle(r.Title)))
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

    log.Add("REC_STOP_ROUTE", $"R{id}",
        $"result=REQUESTED source={r.Source} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] status={r.Status} tuner=[{r.TunerName}] actualTuner=[{r.ActualTunerName}] route=ReservationApiStop->ReservationScheduler.StopRecording commonRoute=recording_stop_all_sources rule=release_contract");

    scheduler.StopRecording(id);

    // StopSessionAsync側が停止完了後の再評価/Wake再構築を正本として実行する。
    // API入口では停止要求を即時受理し、二重の同期再評価でUI応答を待たせない。
    log.Add("REC_STOP_ROUTE", $"R{id}",
        $"result=ACCEPTED_DEFER_REALLOCATION source={r.Source} service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] route=ReservationApiStop->StopSessionAsyncReevaluation apiSynchronousAllocation=False rule=release_contract");

    return Results.Ok(new { message = "録画を停止しました。", stoppedId = id, source = r.Source.ToString(), accepted = true });
});

// 録画ON/OFF切り替え（ユーザーによる能動的な有効/無効化）
app.MapPatch("/api/reservations/{id}/enabled", (int id, EnabledRequest req, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    var r = store.GetById(id);
    if (r is null) return Results.NotFound(new { message = "予約が見つかりません。" });
    log.Add("RESERVE_ENTRY", "EnabledToggle", $"共通入口要求 action=EnabledToggle service=[{r.ServiceName}] title=[{ReservationUserTitleLogValue(r.Title)}] id=R{id} source={r.Source} enabled={r.IsEnabled}->{req.IsEnabled} start={r.StartTime:MM/dd HH:mm}");
    store.UpdateEnabled(id, req.IsEnabled);
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
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: true,
        RefreshWakeTask: true,
        EmitConflictLogs: true,
        ConflictLogCategory: "ReservationAPI",
        ConflictLogTitle: "EnabledToggleConflict"));
    var updated = store.GetById(id);
    if (updated is not null)
        log.Add("RESERVE_ENTRY", "EnabledToggle", $"共通入口結果 action=EnabledToggle service=[{updated.ServiceName}] title=[{ReservationUserTitleLogValue(updated.Title)}] id=R{id} source={updated.Source} enabled={updated.IsEnabled} tuner=[{updated.TunerName}] conflicted={updated.IsConflicted}");
    return Results.Ok(new { message = req.IsEnabled ? "録画ONにしました。" : "録画OFFにしました。" });
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

app.MapPost("/api/keyword-rules/import", async (HttpRequest request, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
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
        NormalizeKeywordRule(ordered[i]);
        ordered[i].SortOrder = i + 1;
        if (ordered[i].Id <= 0) ordered[i].Id = nextId++;
        while (!usedIds.Add(ordered[i].Id)) ordered[i].Id = nextId++;
        var err = ValidateKeywordRule(ordered[i]);
        if (err is not null)
            return Results.BadRequest(new { message = $"{i + 1}件目: {err}" });
    }

    var removed = store.DeleteAllScheduledKeywordReservations();
    store.ReplaceKeywordRules(ordered);
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
        ConflictLogTitle: "Conflict"));
    log.Add("KEYWORD_RULE", "Import", $"自動検索予約ルールをインポート: {ordered.Count}件 / scheduled再生成対象 {removed}件 / file={file.FileName}");

    return Results.Ok(new
    {
        importedCount = ordered.Count,
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

app.MapPost("/api/keyword-rules", (KeywordRule r, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    var err = ValidateKeywordRule(r);
    if (err is not null) return Results.BadRequest(new { message = err });
    NormalizeKeywordRule(r);
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
        ConflictLogTitle: "Conflict"));
    return Results.Ok(new { id, message = "自動検索予約ルールを登録しました。" });
});

app.MapPost("/api/keyword-rules/preview", (KeywordRule r, KeywordMatcher matcher) =>
{
    var err = ValidateKeywordRule(r);
    if (err is not null) return Results.BadRequest(new { message = err });
    NormalizeKeywordRule(r);
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

app.MapPut("/api/keyword-rules/{id}", (int id, KeywordRule r, ReservationStore store, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    var err = ValidateKeywordRule(r);
    if (err is not null) return Results.BadRequest(new { message = err });
    NormalizeKeywordRule(r);
    r.Id = id;

    // ルール更新時はリアルタイム整合性のため、
    // 旧ルール由来の scheduled キーワード予約を物理削除してから更新・再マッチングする。
    // これによりルール内容変更・有効/無効切り替え問わず予約リストが最新状態に保たれる。
    // recording/completed/failed/cancelled は触らない(ユーザーの録画行為や履歴は保持)。
    var removed = store.DeleteScheduledByRuleId(id);
    store.UpdateKeywordRule(r);
    log.Add("KEYWORD_RULE", $"Rule{id}", $"ルール更新: enabled={r.Enabled} name=[{r.Name}] pattern=[{r.Pattern}] removedScheduled={removed}");

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
        ConflictLogTitle: "Conflict"));

    if (removed > 0)
        log.Add("KEYWORD_RULE", $"Rule{id}",
            $"ルール更新に伴い旧ヒット予約を解放: {removed}件 (有効={r.Enabled})");

    return Results.Ok(new { message = "更新しました。" });
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
        ConflictLogTitle: "Conflict"));
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
        ConflictLogTitle: "Conflict"));

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

void NormalizeKeywordRule(KeywordRule r)
{
    r.SearchFields = "title";
    r.Pattern = r.Pattern?.Trim() ?? "";
    r.ExcludePattern = r.ExcludePattern?.Trim() ?? "";
    r.TargetServices = string.Join(",", (r.TargetServices ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (r.UseAllChannels) r.TargetServices = "";
    r.TargetGenres = string.Join(",", (r.TargetGenres ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()));
    r.TargetDays = string.Join(",", (r.TargetDays ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    r.StartTime = string.IsNullOrWhiteSpace(r.StartTime) ? "00:00" : r.StartTime;
    r.EndTime = string.IsNullOrWhiteSpace(r.EndTime) ? "23:59" : r.EndTime;
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
        ConflictLogTitle: "Conflict"));
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
        ConflictLogTitle: "Conflict"));
    return Results.Ok(new { message = "更新しました。" });
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
        ConflictLogTitle: "Conflict"));
    return Results.Ok(new { message = "削除しました。" });
});

// ─── 設定 API ────────────────────────────────────────────────────

// 設定取得（iniの現在値を返す。初回起動時も個人環境由来のパスは自動入力しない）
app.MapGet("/api/settings", (IniSettingsService ini, IOptions<TvTestSettings> tvTestOpts, IOptions<AppSettings> appOpts) =>
{
    var dto = ini.ToDto();
    // release_contract: 初回設定画面で TVTest/BonDriver/.ch2 の個人環境パスを推定表示しない。
    // appsettings.json は安全な空欄既定値のみ保持し、サンプルは appsettings.example.json へ分離する。
    if (ini.IsFirstRun)
    {
        var app = appOpts.Value;
        if (string.IsNullOrWhiteSpace(dto.DataDirectory))
            dto.DataDirectory = string.IsNullOrWhiteSpace(app.DataDirectory) ? "data" : app.DataDirectory;
    }
    dto.EffectiveDataDirectory = ini.ResolveDataDirectory(dto.DataDirectory);
    return Results.Ok(dto);
});

app.MapGet("/api/settings-theme-state", (IniSettingsService ini) =>
{
    var theme = IniSettingsService.NormalizeSystemTheme(ini.SystemTheme);
    return Results.Ok(new
    {
        systemTheme = theme,
        selectedTheme = theme,
        revision = Interlocked.Read(ref settingsThemeRuntimeRevision),
        rule = "release_contract"
    });
});

// 設定保存（iniファイルに書き込む。ポート変更は次回起動時に有効）
app.MapPut("/api/settings", (IniSettingsDto dto, IniSettingsService ini, ChannelFileLoader channelLoader, EpgScheduler epgScheduler, StartupRegistryService startupSvc, ReservationAllocationRouteService allocationRoute, LogRepository log) =>
{
    var before = ini.ToDto();
    var channelMapChanged =
        !string.Equals(before.GrChannelFilePath, dto.GrChannelFilePath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.GrChSetFilePath, dto.GrChSetFilePath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.BscsChannelFilePath, dto.BscsChannelFilePath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.BscsChSetFilePath, dto.BscsChSetFilePath, StringComparison.OrdinalIgnoreCase);

    var tunerTopologyChanged =
        !string.Equals(before.TvTestExecutablePath, dto.TvTestExecutablePath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.ViewingTvTestExecutablePath, dto.ViewingTvTestExecutablePath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.BonDriverDirectory, dto.BonDriverDirectory, StringComparison.OrdinalIgnoreCase) ||
        before.UseMinOption != dto.UseMinOption ||
        before.UseNodshowOption != dto.UseNodshowOption ||
        !string.Equals(
            BuildTunerTopologySignature(before.Tuners),
            BuildTunerTopologySignature(dto.Tuners),
            StringComparison.OrdinalIgnoreCase);

    var requiresRestart =
        !string.Equals(before.TvTestExecutablePath, dto.TvTestExecutablePath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.BonDriverDirectory, dto.BonDriverDirectory, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.DataDirectory, dto.DataDirectory, StringComparison.OrdinalIgnoreCase) ||
        before.Port != dto.Port ||
        before.UseMinOption != dto.UseMinOption ||
        before.UseNodshowOption != dto.UseNodshowOption ||
        !string.Equals(before.ViewingTvTestExecutablePath, dto.ViewingTvTestExecutablePath, StringComparison.OrdinalIgnoreCase) ||
        tunerTopologyChanged;

    var recordingPolicyChanged =
        before.LaterProgramPriority != dto.LaterProgramPriority ||
        before.PseudoContinuousRecording != dto.PseudoContinuousRecording ||
        before.PreStartMarginSeconds != dto.PreStartMarginSeconds ||
        before.PostEndMarginSeconds != dto.PostEndMarginSeconds ||
        !string.Equals(before.RecordingAfterAction, dto.RecordingAfterAction, StringComparison.OrdinalIgnoreCase) ||
        before.RecordingAfterActionDelayMinutes != dto.RecordingAfterActionDelayMinutes;

    var beforeSystemTheme = IniSettingsService.NormalizeSystemTheme(before.SystemTheme);
    var afterSystemTheme = IniSettingsService.NormalizeSystemTheme(dto.SystemTheme);
    var systemThemeChanged = !string.Equals(beforeSystemTheme, afterSystemTheme, StringComparison.OrdinalIgnoreCase);

    // release_contract: チューナー変更は再起動必須。保存はするが、稼働中RuntimeTopologyへは即時反映しない。
    // release_contract: ch2/ChSetはRuntimeTopologyではなくChannelMap契約として扱い、保存直後にChannelFileLoaderのcacheを明示破棄する。
    ini.Save(dto, applyTunerTopologyToRuntime: !tunerTopologyChanged);
    if (channelMapChanged)
    {
        channelLoader.Invalidate();
        log.Add("CHANNEL_MAP_SETTINGS_HOT_RELOAD", "Settings",
            $"changed=True grCh2={SafePathForLog(before.GrChannelFilePath)}->{SafePathForLog(dto.GrChannelFilePath)} grChSet={SafePathForLog(before.GrChSetFilePath)}->{SafePathForLog(dto.GrChSetFilePath)} bscsCh2={SafePathForLog(before.BscsChannelFilePath)}->{SafePathForLog(dto.BscsChannelFilePath)} bscsChSet={SafePathForLog(before.BscsChSetFilePath)}->{SafePathForLog(dto.BscsChSetFilePath)} action=invalidate_channel_cache dbMutation=none tunerTopologyMutation={tunerTopologyChanged} rule=release_contract");
    }
    var themeRevision = systemThemeChanged ? Interlocked.Increment(ref settingsThemeRuntimeRevision) : Interlocked.Read(ref settingsThemeRuntimeRevision);
    log.Add("SETTINGS_THEME_HOT_RELOAD", "Theme",
        $"changed={systemThemeChanged} before={beforeSystemTheme} after={afterSystemTheme} revision={themeRevision} bridge=settings-theme-state frontend=TvAIrTheme.syncRuntime rule=release_contract");
    log.Add("SETTINGS_HOT_RELOAD", "RecordingPolicy",
        $"changed={recordingPolicyChanged} beforeLater={before.LaterProgramPriority} afterLater={dto.LaterProgramPriority} beforeChain={before.PseudoContinuousRecording} afterChain={dto.PseudoContinuousRecording} pre={before.PreStartMarginSeconds}->{dto.PreStartMarginSeconds} post={before.PostEndMarginSeconds}->{dto.PostEndMarginSeconds} afterAction={before.RecordingAfterAction}->{IniSettingsService.NormalizeRecordingAfterAction(dto.RecordingAfterAction)} afterActionDelayMin={before.RecordingAfterActionDelayMinutes}->{IniSettingsService.NormalizeRecordingAfterActionDelayMinutes(dto.RecordingAfterActionDelayMinutes)}");
    log.Add("SETTINGS_EFFECTIVE_STATE", "Server",
        $"afterSave later={ini.LaterProgramPriority} chain={ini.PseudoContinuousRecording} afterAction={ini.RecordingAfterAction} afterActionDelayMin={ini.RecordingAfterActionDelayMinutes} dtoLater={dto.LaterProgramPriority} dtoChain={dto.PseudoContinuousRecording} dtoAfterAction={dto.RecordingAfterAction} dtoAfterActionDelayMin={dto.RecordingAfterActionDelayMinutes}");
    log.Add("SETTINGS_RECORDING_OPTIONS", "Recording",
        $"curServiceOnly={before.TvTestRecordCurServiceOnly}->{dto.TvTestRecordCurServiceOnly} subtitle={before.TvTestRecordSubtitle}->{dto.TvTestRecordSubtitle} dataCarrousel={before.TvTestRecordDataCarrousel}->{dto.TvTestRecordDataCarrousel} showTvAIrEpgRecTaskbarIcon={before.ShowTvAIrEpgRecTaskbarIcon}->{dto.ShowTvAIrEpgRecTaskbarIcon} trayBlinkContract=recording_epg_epgcheck rule=release_contract");
    log.Add("EPG_SETTINGS_SAVE_AUDIT", "Settings",
        $"enabled={before.EpgEnabled}->{dto.EpgEnabled} time={before.EpgHour:D2}:{before.EpgMinute:D2}->{Math.Clamp(dto.EpgHour,0,23):D2}:{Math.Clamp(dto.EpgMinute,0,59):D2} depth={before.EpgDepth}->{dto.EpgDepth} preRecordMinutes={before.EpgPreRecordMinutes}->{dto.EpgPreRecordMinutes} timePolicy=BROADCAST_CLOCK_PASSIVE_ONLY ntp=removed_code_path broadcastClock=observe_only_no_internal_offset route=SettingsSave->EpgScheduler.UpdateConfig->ALLOC_ROUTE/Wake rule=release_contract");
    if (tunerTopologyChanged)
    {
        log.Add("TUNER_TOPOLOGY_RESTART_REQUIRED", "Settings",
            $"result=PENDING_RESTART applyRuntimeTopology=False runtimeTunerCount={before.Tuners.Count} savedTunerCount={(dto.Tuners?.Count ?? 0)} runtimeSignature={SafePathForLog(BuildTunerTopologySignature(before.Tuners))} pendingSignature={SafePathForLog(BuildTunerTopologySignature(dto.Tuners))} affected=Wake,TunerPool,EPG,PreRecEpg,PluginUiContext,ExternalTuner,ViewingProtection rule=release_contract");
    }

    // EPGスケジュール設定を動的反映（再起動不要）
    epgScheduler.UpdateConfig(dto.EpgEnabled, dto.EpgHour, dto.EpgMinute, dto.EpgDepth);
    // スタートアップ登録を動的反映（HKCU\...\Run）
    startupSvc.Set(dto.StartupEnabled);
    allocationRoute.Run(new ReservationAllocationRouteRequest(
        Source: "Settings",
        Action: "Save",
        RunKeywordMatcher: false,
        SyncProgramRuleReservations: true,
        ReevaluateAllocations: true,
        RefreshPreRecordEpgEntries: !tunerTopologyChanged,
        RefreshWakeTask: !tunerTopologyChanged,
        EmitConflictLogs: true,
        ConflictLogCategory: "Settings",
        ConflictLogTitle: "Conflict"));
    return Results.Ok(new
    {
        message = tunerTopologyChanged
            ? "設定を保存しました。チューナー変更はTvAIrの再起動後に反映されます。"
            : (requiresRestart ? "設定を保存しました。今回の変更は再起動後に有効になります。" : "設定を保存しました。"),
        requiresRestart,
        tunerTopologyChanged,
        tunerTopologyRestartRequired = tunerTopologyChanged,
        runtimeTopologyUpdated = !tunerTopologyChanged,
        recordingPolicyHotReloaded = !tunerTopologyChanged,
        themeHotReloaded = true,
        systemTheme = afterSystemTheme,
        selectedTheme = afterSystemTheme,
        themeRevision,
        laterProgramPriority = ini.LaterProgramPriority,
        pseudoContinuousRecording = ini.PseudoContinuousRecording,
        effectiveDataDirectory = ini.ResolveDataDirectory(dto.DataDirectory)
    });
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
// 多重起動防止フラグ
var _browseInProgress = false;
app.MapGet("/api/settings/browse", (string filter) =>
{
    // 多重起動防止
    if (_browseInProgress)
        return Results.Ok(new { cancelled = true, path = (string?)null });
    _browseInProgress = true;

    string? selected = null;
    var thread = new Thread(() =>
    {
        try
        {
            // ダイアログを最前面に出すためのダミーオーナーウィンドウ
            var owner = new System.Windows.Forms.NativeWindow();
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
            owner.DestroyHandle();
        }
        finally
        {
            _browseInProgress = false;
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
        var taskSvc    = app.Services.GetRequiredService<TaskSchedulerService>();
        var startupSvc = app.Services.GetRequiredService<StartupRegistryService>();
        var iniSvc     = app.Services.GetRequiredService<IniSettingsService>();
        var log        = app.Services.GetRequiredService<LogRepository>();
        startupSvc.Set(iniSvc.StartupEnabled);
        taskSvc.UpdateWakeTask();
        log.Add("Startup", "Wake(StartupSync)", "起動時のWakeタスク同期を実行しました。");
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
            app.Services.GetRequiredService<PluginDefaultMenuActionService>());
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


static string BuildTunerTopologySignature(IEnumerable<TunerProfileDto>? tuners)
{
    return string.Join(";", (tuners ?? Enumerable.Empty<TunerProfileDto>())
        .Select(t =>
        {
            var group = TunerDisplayName.NormalizeGroup(t.Group);
            var did = (t.Did ?? string.Empty).Trim().ToUpperInvariant();
            var role = IniSettingsService.NormalizeTunerRole(t.Role);
            var bon = (t.BonDriverFileName ?? string.Empty).Trim();
            var name = TunerDisplayName.ForUi(t.Name, group, did);
            return $"{name}|{bon}|{group}|{did}|{role}";
        })
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
}

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
            $"paths=diagnostic_only note=runtime_prerequisite_summary rule=runtime_prerequisite_release_candidate_trim");
    }
    catch (Exception ex)
    {
        log.Add("TVAIREPGREC_RUNTIME_PREREQUISITE", "WARN",
            $"result=CHECK_FAILED error={SafeRuntimePrereqLogValue(ex.GetType().Name)} message={SafeRuntimePrereqLogValue(ex.Message)} rule=runtime_prerequisite_release_candidate_trim");
    }
}




static string ProgramGuideServiceKey3(ushort networkId, ushort transportStreamId, ushort serviceId)
    => $"{networkId}:{transportStreamId}:{serviceId}";

static string ProgramGuideEventServiceKey(EpgEvent e)
    => ProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId);

static string ProgramGuideChannelServiceKey(ChannelTarget ch)
    => ProgramGuideServiceKey3(ch.OriginalNetworkId, ch.TransportStreamId, ch.ServiceId);

static string NormalizeProgramGuideServiceName(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var normalized = value.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToUpperInvariant();
    var chars = normalized.Where(c => !char.IsWhiteSpace(c)).ToArray();
    return new string(chars);
}

static ProgramGuideProjectionFallbackContext BuildProgramGuideProjectionFallbackContext(IReadOnlyList<EpgEvent> events)
{
    var ctx = new ProgramGuideProjectionFallbackContext();

    foreach (var g in events.GroupBy(e => $"{ProgramGuideWaveGroupFromNetworkId(e.NetworkId)}:{e.ServiceId}:{NormalizeProgramGuideServiceName(e.ServiceName)}", StringComparer.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(g.Key) || g.Key.EndsWith(":", StringComparison.Ordinal)) continue;
        var identityCount = g.Select(e => ProgramGuideEventServiceKey(e)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (identityCount == 1)
            ctx.ByNameSid[g.Key] = g.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EventId).ToList();
    }

    foreach (var g in events.GroupBy(e => $"{ProgramGuideWaveGroupFromNetworkId(e.NetworkId)}:{e.ServiceId}", StringComparer.OrdinalIgnoreCase))
    {
        var identityCount = g.Select(e => ProgramGuideEventServiceKey(e)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (identityCount == 1)
            ctx.ByGroupSidUnique[g.Key] = g.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EventId).ToList();
    }

    return ctx;
}


static bool ProgramGuideOverlapsDay(EpgEvent e, DateTime dayStart, DateTime dayEnd)
    => e.End > dayStart && e.Start < dayEnd;

static IReadOnlyList<EpgEvent> ResolveProgramGuideChannelEvents(
    ChannelTarget ch,
    IReadOnlyDictionary<string, IReadOnlyList<EpgEvent>> byKey,
    ProgramGuideProjectionFallbackContext fallbackContext,
    DateTime dayStart,
    DateTime dayEnd,
    out string resolveSource)
{
    var exactKey = ProgramGuideChannelServiceKey(ch);
    if (byKey.TryGetValue(exactKey, out var exact) && exact.Any(e => ProgramGuideOverlapsDay(e, dayStart, dayEnd)))
    {
        resolveSource = "exact";
        return exact;
    }

    var waveGroup = ProgramGuideWaveGroupFromNetworkId(ch.OriginalNetworkId);
    var nameKey = $"{waveGroup}:{ch.ServiceId}:{NormalizeProgramGuideServiceName(ch.Name)}";
    if (fallbackContext.ByNameSid.TryGetValue(nameKey, out var byNameSid) && byNameSid.Any(e => ProgramGuideOverlapsDay(e, dayStart, dayEnd)))
    {
        resolveSource = "name_sid_unique";
        return byNameSid;
    }

    var groupSidKey = $"{waveGroup}:{ch.ServiceId}";
    if (fallbackContext.ByGroupSidUnique.TryGetValue(groupSidKey, out var byGroupSid) && byGroupSid.Any(e => ProgramGuideOverlapsDay(e, dayStart, dayEnd)))
    {
        resolveSource = "group_sid_unique";
        return byGroupSid;
    }

    if (byKey.TryGetValue(exactKey, out var exactAny))
    {
        resolveSource = "exact_no_day_overlap";
        return exactAny;
    }

    resolveSource = "none";
    return Array.Empty<EpgEvent>();
}



static string SafeProgramGuideProjectionLogValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    return value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Replace("|", "/").Replace("\"", "'").Trim();
}


static IReadOnlyList<EpgEvent> ProgramGuideNormalizeServiceDayEvents(
    IEnumerable<EpgEvent> events,
    DateTime dayStart,
    DateTime dayEnd)
{
    // release_contract: keep one row per broadcast event identity.  Collapse only
    // duplicate table rows for the exact same event/time; never collapse by
    // service key alone.
    var candidates = events
        .Where(e => ProgramGuideOverlapsDay(e, dayStart, dayEnd))
        .GroupBy(e => $"{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}:{e.Start.Ticks}:{e.End.Ticks}", StringComparer.OrdinalIgnoreCase)
        .Select(g => g
            .OrderBy(e => e.TableId == 0x4E ? 0 : 1)
            .ThenBy(e => e.TableId)
            .ThenBy(e => e.EventId)
            .First())
        .OrderBy(e => e.Start)
        .ThenBy(e => e.End)
        .ThenBy(e => e.TableId)
        .ThenBy(e => e.EventId)
        .ToList();

    // If one row encloses several other rows for the same service, keeping the
    // enclosing row first makes the later per-column cursor suppress the real
    // schedule.  Treat that as a projection duplicate/container and drop only
    // the container row.  Long genuine programmes are preserved when they do
    // not contain multiple independent events.
    var filtered = candidates
        .Where(e => candidates.Count(other =>
            other.EventId != e.EventId &&
            other.Start >= e.Start &&
            other.End <= e.End &&
            other.End > other.Start) < 2)
        .ToList();

    return filtered.Count > 0 ? filtered : candidates;
}

static IReadOnlyList<EpgEvent> BuildProgramGuideTimelineEvents(
    IReadOnlyList<EpgEvent> sortedEvents,
    IReadOnlyList<ChannelTarget> channels,
    DateTime dayStart,
    DateTime dayEnd,
    EpgStore store,
    LogRepository log,
    DateOnly baseDate)
{
    // release_contract: use the actual day-overlapping event list as the unit of
    // projection.  release_contract showed NHK General had dbEvents=61 but
    // renderedCells=1, which means the service-level join was present but
    // event multiplicity was lost in the timeline projection.
    var daySortedEvents = sortedEvents
        .Where(e => ProgramGuideOverlapsDay(e, dayStart, dayEnd))
        .OrderBy(e => ProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId), StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.Start)
        .ThenBy(e => e.End)
        .ThenBy(e => e.TableId)
        .ThenBy(e => e.EventId)
        .ToList();

    var result = new List<EpgEvent>(daySortedEvents.Count + channels.Count * 2);
    var byKey = daySortedEvents
        .GroupBy(e => ProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => g.Key,
            g => ProgramGuideNormalizeServiceDayEvents(g, dayStart, dayEnd),
            StringComparer.OrdinalIgnoreCase);

    var fallbackContext = BuildProgramGuideProjectionFallbackContext(daySortedEvents);
    var fallbackHits = new List<string>();

    foreach (var ch in channels)
    {
        var key = ProgramGuideServiceKey3(ch.OriginalNetworkId, ch.TransportStreamId, ch.ServiceId);
        var list = ResolveProgramGuideChannelEvents(ch, byKey, fallbackContext, dayStart, dayEnd, out var resolveSource);
        if (!string.Equals(resolveSource, "exact", StringComparison.OrdinalIgnoreCase) && list.Count > 0 && fallbackHits.Count < 12)
        {
            fallbackHits.Add($"{SafeProgramGuideProjectionLogValue(ch.Name)}:{key}->{ProgramGuideServiceKey3(list[0].NetworkId, list[0].TransportStreamId, list[0].ServiceId)}:{resolveSource}:events={list.Count}");
        }
        var cursor = dayStart;
        foreach (var ev in list)
        {
            if (ev.End <= dayStart || ev.Start >= dayEnd) continue;

            var evStart = ev.Start < dayStart ? dayStart : ev.Start;
            var evEnd = ev.End > dayEnd ? dayEnd : ev.End;
            if (evEnd <= evStart) continue;

            var displayEvent = ev;

            // release_contract: ProgramGuide timeline projection must not render overlapping
            // cells in the same service column.  DB/raw EPG facts are left intact;
            // only the display timeline is裁定済みにする。
            if (evStart < cursor)
            {
                if (evEnd <= cursor)
                    continue;

                displayEvent = CloneProgramGuideTimelineEvent(ev, cursor, evEnd);
                evStart = cursor;
            }

            if (evStart > cursor)
                AddProgramGuideGapFrame(result, ch, cursor, evStart);

            result.Add(displayEvent);
            if (evEnd > cursor) cursor = evEnd;
        }
        if (cursor < dayEnd)
            AddProgramGuideGapFrame(result, ch, cursor, dayEnd);
    }

    if (fallbackHits.Count > 0)
    {
        log.Add("PROGRAMGUIDE_SERVICE_PROJECTION_FALLBACK", "APPLIED",
            $"result=APPLIED date={baseDate:yyyy-MM-dd} count={fallbackHits.Count} sample={SafeProgramGuideProjectionLogValue(string.Join('|', fallbackHits))} rule=release_contract");
    }

    return result;
}


static EpgEvent CloneProgramGuideTimelineEvent(EpgEvent source, DateTime start, DateTime end)
{
    var durationSeconds = (int)Math.Max(0, Math.Round((end - start).TotalSeconds));
    return new EpgEvent
    {
        NetworkId = source.NetworkId,
        TransportStreamId = source.TransportStreamId,
        ServiceId = source.ServiceId,
        EventId = source.EventId,
        ServiceName = source.ServiceName,
        Title = source.Title,
        Description = source.Description,
        Genre = source.Genre,
        GenreCodes = source.GenreCodes,
        TableId = source.TableId,
        SectionNumber = source.SectionNumber,
        VersionNumber = source.VersionNumber,
        RawDescriptorLoopHex = source.RawDescriptorLoopHex,
        RawShortEventDescriptorHex = source.RawShortEventDescriptorHex,
        RawExtendedEventDescriptorHex = source.RawExtendedEventDescriptorHex,
        RawContentDescriptorHex = source.RawContentDescriptorHex,
        DurationSeconds = durationSeconds,
        Start = start,
        End = end,
        UpdatedAt = source.UpdatedAt
    };
}

static void AddProgramGuideGapFrame(
    List<EpgEvent> result,
    ChannelTarget ch,
    DateTime gapStart,
    DateTime gapEnd)
{
    return;
}


static ProgramGuideEpgEventDto NormalizeProgramGuideEventForDisplay(EpgEvent e, IReadOnlyDictionary<string, string>? serviceDisplayNameByKey = null)
{
    // release_contract: ProgramGuide legacy body route purge.
    // 番組表セル/API投影はDB raw descriptorから作ったCellTextを正本にする。
    // 旧description/extendedDescription/decodedExtendedTextの本文経路はここで切断する。
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
        "db.raw_descriptor.common_cell_decoder",
        true);
}

static string SafeRuntimePrereqLogValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    return value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
}



app.Run();

sealed class ProgramGuideProjectionFallbackContext
{
    public Dictionary<string, List<EpgEvent>> ByNameSid { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<EpgEvent>> ByGroupSidUnique { get; } = new(StringComparer.OrdinalIgnoreCase);
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
    string TitleSource,
    bool TitleRawPassthrough);

sealed class ViewerRetuneDelayedDeathAudit
{
    private sealed record State(DateTime RetunedAt, ushort NetworkId, ushort TransportStreamId, ushort ServiceId, string Group, string Did, string BonDriver);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, State> LastRetuneByPid = new();

    public static void MarkRetuned(int pid, ushort networkId, ushort transportStreamId, ushort serviceId, string? group, string? did, string? bonDriver)
    {
        if (pid <= 0) return;
        LastRetuneByPid[pid] = new State(
            DateTime.Now,
            networkId,
            transportStreamId,
            serviceId,
            string.IsNullOrWhiteSpace(group) ? "-" : group.Trim(),
            string.IsNullOrWhiteSpace(did) ? "-" : did.Trim(),
            string.IsNullOrWhiteSpace(bonDriver) ? "-" : Path.GetFileName(bonDriver.Trim()));
    }

    public static DelayedDeathResult MarkDetectedDead(int pid)
    {
        var detectedAt = DateTime.Now;
        if (pid <= 0 || !LastRetuneByPid.TryRemove(pid, out var state))
        {
            return new DelayedDeathResult(
                "UNKNOWN_LAST_RETUNE",
                "-",
                detectedAt.ToString("O", CultureInfo.InvariantCulture),
                "-",
                "-",
                "-",
                "-",
                "-",
                "-",
                "-");
        }

        var elapsedMs = Math.Max(0, (long)(detectedAt - state.RetunedAt).TotalMilliseconds);
        return new DelayedDeathResult(
            "DETECTED",
            state.RetunedAt.ToString("O", CultureInfo.InvariantCulture),
            detectedAt.ToString("O", CultureInfo.InvariantCulture),
            elapsedMs.ToString(CultureInfo.InvariantCulture),
            state.NetworkId.ToString(CultureInfo.InvariantCulture),
            state.TransportStreamId.ToString(CultureInfo.InvariantCulture),
            state.ServiceId.ToString(CultureInfo.InvariantCulture),
            state.Group,
            state.Did,
            state.BonDriver);
    }
}

public sealed record DelayedDeathResult(
    string Result,
    string LastRetuneAtText,
    string DetectedDeadAtText,
    string ElapsedMsText,
    string NetworkIdText,
    string TransportStreamIdText,
    string ServiceIdText,
    string Group,
    string Did,
    string BonDriver);

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

public sealed record ViewerProfileProjectionResult(
    IReadOnlyList<ViewerProfileContractDto> Profiles,
    IReadOnlyList<ViewerProfileContractDto> SelectableProfiles,
    int EnabledRealProfileCount,
    int ViewingTunerCount,
    bool SelectorVisibleRecommended,
    string DefaultViewerProfile,
    string ProfileIds,
    string SelectableProfileIds);

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
    IReadOnlyList<string>? AvailableGroups = null);


static class ViewerProfileContract
{
    public static IReadOnlyList<ViewerProfileContractDto> BuildProfiles(TvTestSettings settings, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles)
    {
        var path = !string.IsNullOrWhiteSpace(ini.ViewingTvTestExecutablePath) ? ini.ViewingTvTestExecutablePath : ini.TvTestExecutablePath;
        if (string.IsNullOrWhiteSpace(path))
            path = !string.IsNullOrWhiteSpace(settings.ViewingTvTestExecutablePath) ? settings.ViewingTvTestExecutablePath : settings.ExecutablePath;
        var executableKey = NormalizePathKey(path);

        var profiles = new List<ViewerProfileContractDto>();
        var effectiveTuners = BuildEffectiveTunerProfiles(ini, tunerProfiles);
        var viewingTuners = effectiveTuners
            .Where(t => string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => TunerDisplayName.NormalizeGroup(t.Group))
            .ThenBy(t => string.IsNullOrWhiteSpace(t.Name) ? "~" : t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => string.IsNullOrWhiteSpace(t.Did) ? "~" : t.Did, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groupedViewingTuners = viewingTuners
            .GroupBy(t => TunerDisplayName.NormalizeGroup(t.Group), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(t => string.IsNullOrWhiteSpace(t.Name) ? "~" : t.Name, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(t => string.IsNullOrWhiteSpace(t.Did) ? "~" : t.Did, StringComparer.OrdinalIgnoreCase)
                      .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var frameCount = groupedViewingTuners.Values.Select(v => v.Count).DefaultIfEmpty(0).Max();
        for (var frameIndex = 1; frameIndex <= frameCount; frameIndex++)
        {
            var id = $"tvtest{frameIndex}";
            var name = $"TVTest{frameIndex}";
            var availableGroups = BuildAvailableGroups(groupedViewingTuners, frameIndex);
            var note = BuildTvTestFrameNote(groupedViewingTuners, frameIndex, availableGroups);
            var key = $"viewer-profile:{id}:tvtest-frame:{frameIndex}";
            profiles.Add(new ViewerProfileContractDto(id, name, true, frameIndex == 1, frameIndex, false, key, "tunerpool-viewing-tvtest-frame", note, frameIndex, availableGroups));
        }

        if (profiles.Count == 0 && !string.IsNullOrWhiteSpace(executableKey))
            profiles.Add(new ViewerProfileContractDto("tvtest1", "TVTest1", true, true, 1, false, "viewer-profile:tvtest1:legacy-default", "legacy-default-viewer", "Viewing role未定義の後方互換profile", 1, new[] { "GR", "BSCS", "HYBRID" }));

        return profiles;
    }

    public static ViewerProfileContractDto ResolveRequestedProfile(string? requested, TvTestSettings settings, IniSettingsService ini, IReadOnlyList<TunerProfile> tunerProfiles)
    {
        var id = NormalizeProfileId(requested);
        var profiles = BuildProfiles(settings, ini, tunerProfiles);
        var match = profiles.FirstOrDefault(p => string.Equals(NormalizeProfileId(p.Id), id, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        if (TryParseTvTestOrdinal(id, out var ordinal))
            return new ViewerProfileContractDto(id, $"TVTest{ordinal}", false, false, ordinal, false, string.Empty, "not-configured", "TVTest枠数を超える未設定profile", ordinal, Array.Empty<string>());

        return profiles.FirstOrDefault(p => string.Equals(p.Id, "tvtest1", StringComparison.OrdinalIgnoreCase))
            ?? new ViewerProfileContractDto("tvtest1", "TVTest1", false, true, 1, false, string.Empty, "not-configured", "TVTest1が構成されていません", 1, Array.Empty<string>());
    }

    public static bool ProfileSupportsGroup(ViewerProfileContractDto profile, string? requestedGroup)
    {
        if (!profile.Enabled) return false;
        var group = NormalizeProfileGroup(requestedGroup);
        var available = profile.AvailableGroups ?? Array.Empty<string>();
        if (available.Count == 0) return true;
        return available.Any(g => string.Equals(NormalizeProfileGroup(g), group, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(NormalizeProfileGroup(g), "HYBRID", StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildViewerClientId(string pluginId, string viewerProfileId)
    {
        var profile = NormalizeProfileId(viewerProfileId);
        return $"{pluginId}:viewer:{profile}";
    }

    public static bool LeaseMatchesProfile(ExternalTunerLeaseDto lease, ViewerProfileContractDto profile)
        => string.Equals(NormalizeProfileId(lease.ViewerProfileId), NormalizeProfileId(profile.Id), StringComparison.OrdinalIgnoreCase);

    public static string TvTestPathKeyForResolvedProfile(ViewerProfileContractDto profile)
        => !string.IsNullOrWhiteSpace(profile.TvTestPathKey) ? profile.TvTestPathKey : NormalizeProfileId(profile.Id);

    private static IReadOnlyList<string> BuildAvailableGroups(IReadOnlyDictionary<string, List<TunerProfile>> groupedViewingTuners, int frameIndex)
    {
        var groups = new List<string>();
        var hasHybrid = groupedViewingTuners.TryGetValue("HYBRID", out var hybridTuners)
            && frameIndex >= 1 && frameIndex <= hybridTuners.Count;

        // HYBRID is a single TVTest frame capable of GR and BS/CS.
        // Keep HYBRID as a capability marker, and also expose GR/BSCS so plugin UIs can
        // enable ordinary wave buttons without knowing the underlying tuner topology.
        if (hasHybrid)
        {
            groups.Add("GR");
            groups.Add("BSCS");
            groups.Add("HYBRID");
            return groups;
        }

        foreach (var group in new[] { "GR", "BSCS" })
        {
            if (groupedViewingTuners.TryGetValue(group, out var tuners) && frameIndex >= 1 && frameIndex <= tuners.Count)
                groups.Add(group);
        }
        return groups;
    }

    private static string BuildTvTestFrameNote(IReadOnlyDictionary<string, List<TunerProfile>> groupedViewingTuners, int frameIndex, IReadOnlyList<string> availableGroups)
    {
        static string Part(IReadOnlyDictionary<string, List<TunerProfile>> groups, string group, int index)
        {
            if (!groups.TryGetValue(group, out var tuners) || index < 1 || index > tuners.Count)
                return $"{group}=-";
            var tuner = tuners[index - 1];
            var did = string.IsNullOrWhiteSpace(tuner.Did) ? "-" : tuner.Did.Trim().ToUpperInvariant();
            var name = string.IsNullOrWhiteSpace(tuner.Name) ? "-" : tuner.Name.Trim();
            var bon = Path.GetFileName(tuner.BonDriverFileName ?? string.Empty);
            return $"{TunerDisplayName.GroupLabel(group)}={name}/DID {did}/{bon}";
        }

        var parts = new[]
        {
            Part(groupedViewingTuners, "GR", frameIndex),
            Part(groupedViewingTuners, "BSCS", frameIndex),
            Part(groupedViewingTuners, "HYBRID", frameIndex),
            "availableGroups=" + (availableGroups.Count == 0 ? "-" : string.Join(",", availableGroups))
        };
        return $"TVTest frame {frameIndex}: " + string.Join("; ", parts);
    }

    private static IReadOnlyList<TunerProfile> BuildEffectiveTunerProfiles(IniSettingsService ini, IReadOnlyList<TunerProfile> fallback)
    {
        if (ini?.Tuners is { Count: > 0 })
        {
            return ini.Tuners.Select(t =>
            {
                var group = TunerDisplayName.NormalizeGroup(t.Group);
                var role = IniSettingsService.NormalizeTunerRole(t.Role);
                var did = (t.Did ?? string.Empty).Trim().ToUpperInvariant();
                return new TunerProfile
                {
                    Name = TunerDisplayName.ForUi(t.Name, group, did),
                    BonDriverFileName = TunerIsolationPolicy.NormalizeBonDriverForRole(t.BonDriverFileName, group, role),
                    Group = group,
                    Did = did,
                    Role = role,
                };
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
            .ToList();
        }

        return fallback ?? Array.Empty<TunerProfile>();
    }


    private static string NormalizeProfileId(string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "tvtest1" : value.Trim().ToLowerInvariant();
        if (v is "default" or "auto" or "") return "tvtest1";
        if (v is "tvtest") return "tvtest1";
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0) return $"tvtest{n}";
        if (TryParseTvTestOrdinal(v, out var ordinal)) return $"tvtest{ordinal}";
        return "tvtest1";
    }

    private static string NormalizeProfileGroup(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "BS" or "CS" or "BS/CS" or "BSCS" => "BSCS",
            "地上波" or "GR" or "GROUND" => "GR",
            "HYBRID" or "GRBSCS" or "GR/BSCS" or "GR/BS/CS" or "地/BS/CS" or "地デジ/BS/CS" or "地上波/BS/CS" => "HYBRID",
            _ => g
        };
    }

    private static bool TryParseTvTestOrdinal(string value, out int ordinal)
    {
        ordinal = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        const string prefix = "tvtest";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = value[prefix.Length..];
        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) && ordinal > 0;
    }

    private static string NormalizePathKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(value.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
        catch { return value.Trim().ToUpperInvariant(); }
    }
}

