using TvAIr.Channel;
using TvAIr.Epg;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIr.Plugin;

namespace TvAIr.Core;

public sealed class SettingsRuntimeState
{
    private long _themeRevision;

    public long ThemeRevision => Interlocked.Read(ref _themeRevision);

    public long AdvanceThemeRevision() => Interlocked.Increment(ref _themeRevision);
}

public sealed class SettingsValidationException : ArgumentException
{
    public string Field { get; }

    public SettingsValidationException(string field, string message)
        : base(message)
    {
        Field = field;
    }
}

public sealed record SettingsChangeResult(
    string Message,
    bool PersistedChanged,
    bool RequiresRestart,
    bool TunerTopologyRestartRequired,
    bool ReservationActionUiHotReloaded,
    long ThemeRevision);

/// <summary>
/// Web設定とWinForms設定が共有する、設定保存後の単一変更適用出口。
/// UI遷移・モーダル・ページ再描画は所有せず、正本保存、差分分類、
/// Runtime反映、再起動要否、呼出元へ返す変更契約だけを所有する。
/// </summary>
public sealed class SettingsChangeApplicationService
{
    private readonly IniSettingsService _ini;
    private readonly ChannelFileLoader _channelLoader;
    private readonly EpgScheduler _epgScheduler;
    private readonly StartupRegistryService _startupService;
    private readonly NetworkAccessSecurity _networkSecurity;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly ReservationScheduler _reservationScheduler;
    private readonly LogRepository _log;
    private readonly SettingsRuntimeState _runtimeState;
    private readonly PluginToolWindowHostService _toolWindows;
    private readonly object _applyGate = new();

    public SettingsChangeApplicationService(
        IniSettingsService ini,
        ChannelFileLoader channelLoader,
        EpgScheduler epgScheduler,
        StartupRegistryService startupService,
        NetworkAccessSecurity networkSecurity,
        ReservationAllocationRouteService allocationRoute,
        ReservationScheduler reservationScheduler,
        LogRepository log,
        SettingsRuntimeState runtimeState,
        PluginToolWindowHostService toolWindows)
    {
        _ini = ini;
        _channelLoader = channelLoader;
        _epgScheduler = epgScheduler;
        _startupService = startupService;
        _networkSecurity = networkSecurity;
        _allocationRoute = allocationRoute;
        _reservationScheduler = reservationScheduler;
        _log = log;
        _runtimeState = runtimeState;
        _toolWindows = toolWindows;
    }

    public SettingsChangeResult Apply(WebSettingsUpdateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // SETTINGS_WEB_EDITABLE_SCOPE_CONTRACT
        // Web画面に存在しない内部設定をブラウザへ持ち回らせない。保存要求は画面で編集可能な値だけを受け取り、
        // Wake/EPG worker/TVTest内部起動オプションは現在の永続正本からここで合成する。
        lock (_applyGate)
        {
            var current = _ini.ToDto();
            var merged = MergeWebRequest(current, dto);
            return ApplyCore(merged);
        }
    }

    private static IniSettingsUpdateDto MergeWebRequest(IniSettingsDto current, WebSettingsUpdateDto requested) => new()
    {
        TvTestExecutablePath = requested.TvTestExecutablePath,
        BonDriverDirectory = requested.BonDriverDirectory,
        ViewingTvTestExecutablePath = requested.ViewingTvTestExecutablePath,
        GrChannelFilePath = requested.GrChannelFilePath,
        GrChSetFilePath = requested.GrChSetFilePath,
        BscsChannelFilePath = requested.BscsChannelFilePath,
        BscsChSetFilePath = requested.BscsChSetFilePath,
        DataDirectory = requested.DataDirectory,
        SystemTheme = requested.SystemTheme,
        Port = requested.Port,
        EpgEnabled = requested.EpgEnabled,
        EpgHour = requested.EpgHour,
        EpgMinute = requested.EpgMinute,
        EpgDepth = requested.EpgDepth,
        EpgPreRecordMinutes = requested.EpgPreRecordMinutes,
        LaterProgramPriority = requested.LaterProgramPriority,
        PseudoContinuousRecording = requested.PseudoContinuousRecording,
        PreStartMarginSeconds = requested.PreStartMarginSeconds,
        PostEndMarginSeconds = requested.PostEndMarginSeconds,
        WakeMinutesBefore = current.WakeMinutesBefore,
        WakeAdditionalSeconds = current.WakeAdditionalSeconds,
        UseMinOption = current.UseMinOption,
        UseNodshowOption = current.UseNodshowOption,
        ShowTvAIrEpgRecTaskbarIcon = requested.ShowTvAIrEpgRecTaskbarIcon,
        StartupEnabled = requested.StartupEnabled,
        NetworkLanAccessEnabled = requested.NetworkLanAccessEnabled,
        NetworkSessionLifetimeMinutes = requested.NetworkSessionLifetimeMinutes,
        RecordingAfterAction = requested.RecordingAfterAction,
        RecordingAfterActionDelayMinutes = requested.RecordingAfterActionDelayMinutes,
        UserLogDetailEnabled = requested.UserLogDetailEnabled,
        UserLogDetailReservationSource = requested.UserLogDetailReservationSource,
        UserLogDetailScheduledTime = requested.UserLogDetailScheduledTime,
        UserLogDetailActualRecordingTime = requested.UserLogDetailActualRecordingTime,
        UserLogDetailRecordingQuality = requested.UserLogDetailRecordingQuality,
        UserLogDetailStateChange = requested.UserLogDetailStateChange,
        UserLogDetailEndOrFailureReason = requested.UserLogDetailEndOrFailureReason,
        ThemeGenrePalettes = requested.ThemeGenrePalettes,
        EpgUseBelowNormalPriority = current.EpgUseBelowNormalPriority,
        EpgDisableImmediateRetry = current.EpgDisableImmediateRetry,
        TaskUserName = requested.TaskUserName,
        Tuners = requested.Tuners,
        NetworkPasswordPlain = requested.NetworkPasswordPlain,
        ClearNetworkPassword = requested.ClearNetworkPassword,
        TaskPasswordPlain = requested.TaskPasswordPlain,
        ClearTaskPassword = requested.ClearTaskPassword
    };

    public SettingsChangeResult Apply(IniSettingsUpdateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // SETTINGS_SAVE_SERIALIZATION_CONTRACT
        // IniSettingsServiceはSingletonであり、設定保存は差分取得→INI確定→Runtime/EPG/Allocation/Wake反映までを
        // 一つの変更単位として扱う。同時保存を許すと、後着要求が先着要求のbefore/afterや副作用を上書きするため直列化する。
        lock (_applyGate)
        {
            return ApplyCore(dto);
        }
    }

    private SettingsChangeResult ApplyCore(IniSettingsUpdateDto dto)
    {
        ValidateRequest(dto);

        var before = _ini.ToDto();
        var topologyRestartPendingBefore = _ini.IsTunerTopologyRestartPending();
        var hostRestartPendingBefore = _ini.IsHostSettingsRestartPending();
        ValidateNetworkAccessRequest(before, dto);

        // SETTINGS_CANONICAL_DIFF_CONTRACT
        // UI上の表示状態や読取専用投影値ではなく、永続化される設定正本だけを比較する。
        // 差分が0件なら保存・Runtime反映・EPG再評価・割当・Wake更新を一切実行しない。
        if (!HasPersistedDifference(before, dto))
        {
            // SETTINGS_STARTUP_RECONCILIATION_CONTRACT
            // StartupEnabledはINIだけでなくHKCU Runキーへの外部投影を持つ。
            // 前回の反映失敗や利用者によるRunキー削除後、保存値が同じという理由だけで
            // NO_CHANGE終了すると設定画面から再試行できないため、差分0件でも保存済み値へ再同期する。
            var noChangeStartupApplySucceeded = _startupService.Set(before.StartupEnabled);
            _log.Add("SETTINGS_SAVE", "Settings",
                $"result=NO_CHANGE persistedChanges=0 save=False runtimeApply=False epgUpdate=False allocation=False wake=False startupApply={noChangeStartupApplySucceeded} rule=settings_canonical_diff_contract");

            var noChangeMessageParts = new List<string> { "変更はありません。" };
            if (!noChangeStartupApplySucceeded)
                noChangeMessageParts.Add("Windows自動起動設定の反映に失敗しました。ログを確認してください。");
            if (topologyRestartPendingBefore)
                noChangeMessageParts.Add("保存済みのチューナー変更はTvAIrの再起動後に反映されます。");
            if (hostRestartPendingBefore)
                noChangeMessageParts.Add("保存済みのデータ保存先またはポート変更はTvAIrの再起動後に反映されます。");

            return new SettingsChangeResult(
                Message: string.Join(string.Empty, noChangeMessageParts),
                PersistedChanged: false,
                RequiresRestart: topologyRestartPendingBefore || hostRestartPendingBefore,
                TunerTopologyRestartRequired: topologyRestartPendingBefore,
                ReservationActionUiHotReloaded: false,
                ThemeRevision: _runtimeState.ThemeRevision);
        }

        var tunerTopologyChanged =
            !EqualsPath(before.TvTestExecutablePath, dto.TvTestExecutablePath) ||
            !EqualsPath(before.ViewingTvTestExecutablePath, dto.ViewingTvTestExecutablePath) ||
            !EqualsPath(before.BonDriverDirectory, dto.BonDriverDirectory) ||
            before.UseMinOption != dto.UseMinOption ||
            before.UseNodshowOption != dto.UseNodshowOption ||
            !EqualsIgnoreCase(IniSettingsService.BuildTunerTopologySignature(before.Tuners), IniSettingsService.BuildTunerTopologySignature(dto.Tuners));

        // SETTINGS_PERSISTED_RUNTIME_TOPOLOGY_SEPARATION
        // Persisted差分とRuntime差分を分ける。保留中の構成と同じDTOを再保存しても、
        // RuntimeTopologyへ後段適用してはならない。現在Runtimeと一致する構成へ戻した場合だけ保留を解消できる。
        var requestedTopologyMatchesRuntime = _ini.MatchesRuntimeTunerTopology(dto);
        var requestedHostMatchesRuntime = _ini.MatchesRuntimeHostSettings(dto);

        // DAILY_EPG_RUNNING_SETTINGS_IMMUTABILITY_CONTRACT
        // Daily AllのRunning永続化が正式な実行開始境界である。開始済みrunへ設定変更を後段適用しない。
        // Running中のDaily実行snapshotを不変に保つため、Daily設定変更はINI commit前に拒否する。
        // 手動Normal EPGについては従来どおり深度だけを実行中変更不可とし、Daily固有設定へ巻き込まない。
        var requestedDailyScheduleChanged =
            before.EpgEnabled != dto.EpgEnabled ||
            before.EpgHour != SettingsDefaults.NormalizeEpgHour(dto.EpgHour) ||
            before.EpgMinute != SettingsDefaults.NormalizeEpgMinute(dto.EpgMinute) ||
            !EqualsIgnoreCase(before.EpgDepth, SettingsDefaults.NormalizeEpgDepth(dto.EpgDepth));
        var requestedEpgDepthChanged =
            !EqualsIgnoreCase(before.EpgDepth, SettingsDefaults.NormalizeEpgDepth(dto.EpgDepth));

        if (requestedDailyScheduleChanged && _epgScheduler.IsDailyRunning)
        {
            _log.Add("SETTINGS_SAVE", "EPG",
                "result=REJECTED reason=daily_epg_running fields=enabled,time,depth persisted=False rule=daily_epg_running_settings_immutability_contract");
            throw new SettingsValidationException("epgDaily", "定時EPG実行中は、定時EPGの有効/無効・実行時刻・取得深度を変更できません。完了後に変更してください。");
        }

        if (requestedEpgDepthChanged && _epgScheduler.IsRunning)
        {
            _log.Add("SETTINGS_SAVE", "EPG",
                "result=REJECTED reason=normal_epg_running field=depth persisted=False rule=epg_depth_runtime_contract");
            throw new SettingsValidationException("epgDepth", "EPG取得中は取得深度を変更できません。完了後に変更してください。");
        }

        _ini.SaveFromApplicationService(
            dto,
            applyTunerTopologyToRuntime: requestedTopologyMatchesRuntime,
            applyHostSettingsToRuntime: requestedHostMatchesRuntime);
        var after = _ini.ToDto();
        var topologyRestartPendingAfter = _ini.IsTunerTopologyRestartPending();
        var hostRestartPendingAfter = _ini.IsHostSettingsRestartPending();
        var changedAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // SETTINGS_CHANNEL_MAP_SCOPE_CONTRACT
        // ch2 はサービス一覧・表示順、ChSet は物理選局引数の正本であり、どちらもChannelFileLoaderのcache無効化で即時反映する。
        // 通常予約は録画開始時に現行ChannelMapから再解決し、PreRecEpg行は親予約ID・時刻・Tunerだけを保持するため、
        // ChannelMap変更だけでFinalAllocation／PreRec再構築／Wake再計算を走らせてはならない。
        var channelServiceListChanged =
            !EqualsPath(before.GrChannelFilePath, after.GrChannelFilePath) ||
            !EqualsPath(before.BscsChannelFilePath, after.BscsChannelFilePath);
        var channelTuningMapChanged =
            !EqualsPath(before.GrChSetFilePath, after.GrChSetFilePath) ||
            !EqualsPath(before.BscsChSetFilePath, after.BscsChSetFilePath);
        var channelMapChanged = channelServiceListChanged || channelTuningMapChanged;
        // SETTINGS_CREDENTIAL_CHANGE_SCOPE_CONTRACT
        // パスワード変更有無はSaveFromApplicationServiceと同じ入力契約で判定する。
        // 他項目と同時保存された際、未編集の空文字列や既に未設定のClearを
        // NetworkAccess／Wake変更へ後段上塗りしてはならない。
        var networkPasswordChanged =
            !string.IsNullOrWhiteSpace(dto.NetworkPasswordPlain) ||
            (dto.ClearNetworkPassword && before.NetworkHasPassword);
        var taskPasswordChanged =
            !string.IsNullOrEmpty(dto.TaskPasswordPlain) ||
            (dto.ClearTaskPassword && before.TaskHasPassword);
        var networkAccessChanged =
            before.NetworkLanAccessEnabled != after.NetworkLanAccessEnabled ||
            before.NetworkSessionLifetimeMinutes != after.NetworkSessionLifetimeMinutes ||
            before.NetworkHasPassword != after.NetworkHasPassword ||
            networkPasswordChanged;
        // SETTINGS_RECORDING_POLICY_SCOPE_CONTRACT
        // 予約割当・PreRec・Wakeへ影響する録画スケジュール方針と、録画完了後動作／worker表示設定を分離する。
        // 後者だけの変更でFinalAllocationやPreRec再構築を走らせてはならない。
        var reservationActionUiPolicyChanged =
            before.LaterProgramPriority != after.LaterProgramPriority ||
            before.PseudoContinuousRecording != after.PseudoContinuousRecording;
        var recordingSchedulePolicyChanged =
            reservationActionUiPolicyChanged ||
            before.PreStartMarginSeconds != after.PreStartMarginSeconds ||
            before.PostEndMarginSeconds != after.PostEndMarginSeconds;
        var recordingCompletionPolicyChanged =
            !EqualsIgnoreCase(before.RecordingAfterAction, after.RecordingAfterAction) ||
            before.RecordingAfterActionDelayMinutes != after.RecordingAfterActionDelayMinutes;
        var recordingWorkerPresentationChanged =
            before.ShowTvAIrEpgRecTaskbarIcon != after.ShowTvAIrEpgRecTaskbarIcon;
        // SETTINGS_EPG_POLICY_SCOPE_CONTRACT
        // Daily EPGの予定表、録画前EPG確認、worker実行方針を分離する。
        // worker優先度／即時再試行抑止だけの変更でDaily行再生成やAllocation／PreRec／Wakeを走らせてはならない。
        var dailyEpgScheduleChanged =
            before.EpgEnabled != after.EpgEnabled ||
            before.EpgHour != after.EpgHour ||
            before.EpgMinute != after.EpgMinute ||
            !EqualsIgnoreCase(before.EpgDepth, after.EpgDepth);
        var preRecordEpgPolicyChanged =
            before.EpgPreRecordMinutes != after.EpgPreRecordMinutes;
        var epgWorkerPolicyChanged =
            before.EpgUseBelowNormalPriority != after.EpgUseBelowNormalPriority ||
            before.EpgDisableImmediateRetry != after.EpgDisableImmediateRetry;
        var epgPolicyChanged =
            dailyEpgScheduleChanged ||
            preRecordEpgPolicyChanged ||
            epgWorkerPolicyChanged;
        var userLogChanged =
            before.UserLogDetailEnabled != after.UserLogDetailEnabled ||
            before.UserLogDetailReservationSource != after.UserLogDetailReservationSource ||
            before.UserLogDetailScheduledTime != after.UserLogDetailScheduledTime ||
            before.UserLogDetailActualRecordingTime != after.UserLogDetailActualRecordingTime ||
            before.UserLogDetailRecordingQuality != after.UserLogDetailRecordingQuality ||
            before.UserLogDetailStateChange != after.UserLogDetailStateChange ||
            before.UserLogDetailEndOrFailureReason != after.UserLogDetailEndOrFailureReason;
        var beforeSystemTheme = IniSettingsService.NormalizeSystemTheme(before.SystemTheme);
        var afterSystemTheme = IniSettingsService.NormalizeSystemTheme(after.SystemTheme);
        var systemThemeChanged = !EqualsIgnoreCase(beforeSystemTheme, afterSystemTheme);
        var startupChanged = before.StartupEnabled != after.StartupEnabled;
        var hostChanged = !EqualsPath(before.DataDirectory, after.DataDirectory) || before.Port != after.Port;
        var taskCredentialChanged =
            !EqualsTrimmed(before.TaskUserName, after.TaskUserName) ||
            taskPasswordChanged;
        var wakePolicyChanged =
            before.WakeMinutesBefore != after.WakeMinutesBefore ||
            before.WakeAdditionalSeconds != after.WakeAdditionalSeconds ||
            taskCredentialChanged;
        var genrePaletteChanged = !EqualsIgnoreCase(
            IniSettingsService.BuildThemeGenrePaletteSignature(before.ThemeGenrePalettes),
            IniSettingsService.BuildThemeGenrePaletteSignature(after.ThemeGenrePalettes));

        if (tunerTopologyChanged) changedAreas.Add("TunerTopology");
        if (channelMapChanged) changedAreas.Add("ChannelMap");
        if (networkAccessChanged) changedAreas.Add("NetworkAccess");
        if (recordingSchedulePolicyChanged) changedAreas.Add("RecordingSchedulePolicy");
        if (recordingCompletionPolicyChanged) changedAreas.Add("RecordingCompletionPolicy");
        if (recordingWorkerPresentationChanged) changedAreas.Add("RecordingWorkerPresentation");
        if (epgPolicyChanged) changedAreas.Add("EpgPolicy");
        if (userLogChanged) changedAreas.Add("UserLog");
        if (systemThemeChanged) changedAreas.Add("Theme");
        if (genrePaletteChanged) changedAreas.Add("GenrePalette");
        if (startupChanged) changedAreas.Add("Startup");
        if (hostChanged) changedAreas.Add("Host");
        if (wakePolicyChanged) changedAreas.Add("Wake");

        // SETTINGS_POST_COMMIT_SIDE_EFFECT_CONTRACT
        // SaveFromApplicationService成功後はTvAIr.iniが新しい永続正本である。
        // 以降の派生反映失敗をHTTP 500へ漏らして「保存自体が失敗した」と誤表示してはならない。
        // 再試行可能な派生処理は個別に記録し、保存成功結果へ警告として合成する。
        var postCommitWarnings = new List<string>();
        var revokedNetworkSessions = 0;
        var clearedNetworkLoginFailures = 0;
        if ((before.NetworkLanAccessEnabled && !after.NetworkLanAccessEnabled) || networkPasswordChanged)
        {
            // NETWORK_ACCESS_IMMEDIATE_SESSION_INVALIDATION_CONTRACT
            // LAN無効化は新規要求を拒否するだけでなく、既存セッションもその場で失効させる。
            // 無効化後に再度LANを有効化した際、以前のCookieが復活してはならない。
            // パスワード変更時は旧資格情報で発行したセッションに加え、旧パスワード由来の
            // login failure/blockも破棄し、新しい資格情報を保存直後から利用可能にする。
            var invalidation = _networkSecurity.InvalidateAccessState(
                clearLoginFailures: networkPasswordChanged);
            revokedNetworkSessions = invalidation.RevokedSessions;
            clearedNetworkLoginFailures = invalidation.ClearedLoginFailures;
            _log.Add("NETWORK_ACCESS_SESSION_INVALIDATED", "Settings",
                $"reason={(networkPasswordChanged ? "credential_changed" : "lan_disabled")} revoked={revokedNetworkSessions} clearedLoginFailures={clearedNetworkLoginFailures} lan={before.NetworkLanAccessEnabled}->{after.NetworkLanAccessEnabled} rule=network_access_immediate_apply_contract");
        }

        if (channelMapChanged)
        {
            _channelLoader.Invalidate();
            _log.Add("CHANNEL_MAP_SETTINGS_HOT_RELOAD", "Settings",
                $"changed=True serviceList={channelServiceListChanged} tuningMap={channelTuningMapChanged} action=invalidate_channel_cache dataRefresh=False displayRefresh=next_regular_fetch allocation=False preRecRefresh=False wake=False dbMutation=none tunerTopologyMutation={tunerTopologyChanged} rule=release_contract");
        }

        var themeRevision = systemThemeChanged ? _runtimeState.AdvanceThemeRevision() : _runtimeState.ThemeRevision;
        if (systemThemeChanged)
        {
            // ThemeRevisionとINIは既に確定済みである。開いているToolWindowへの通知失敗を
            // 保存失敗としてHTTP 500へ漏らさず、次回再描画・再オープンで確定テーマへ収束させる。
            try
            {
                var refreshedToolWindows = _toolWindows.RefreshAllForThemeChange(themeRevision, afterSystemTheme);
                _log.Add("SETTINGS_THEME_HOT_RELOAD", "Theme",
                    $"changed=True before={beforeSystemTheme} after={afterSystemTheme} revision={themeRevision} openWindows={refreshedToolWindows} rule=release_contract");
            }
            catch (Exception ex)
            {
                postCommitWarnings.Add("開いているプラグイン画面へのテーマ反映に失敗しました。画面を開き直してください。");
                _log.Add("SETTINGS_POST_COMMIT_WARNING", "Theme",
                    $"result=FAILED stage=tool_window_theme_refresh persisted=True revision={themeRevision} exception={ex.GetType().Name} reason={ex.Message} action=reopen_tool_window rule=settings_post_commit_side_effect_contract");
            }
        }

        if (recordingSchedulePolicyChanged)
        {
            // 保存後の再割当・PreRec・Wakeは新設定で再評価するが、開始済み録画の終了マージンは
            // RecordingSessionが開始時snapshotを保持する。保存値を実行中sessionへ後段上塗りしない。
            _log.Add("SETTINGS_HOT_RELOAD", "RecordingSchedulePolicy",
                $"changed=True beforeLater={before.LaterProgramPriority} afterLater={after.LaterProgramPriority} beforeChain={before.PseudoContinuousRecording} afterChain={after.PseudoContinuousRecording} pre={before.PreStartMarginSeconds}->{after.PreStartMarginSeconds} post={before.PostEndMarginSeconds}->{after.PostEndMarginSeconds} activeRecordingMarginApply=future_session_only rule=release_contract");
        }

        if (recordingCompletionPolicyChanged)
        {
            // 録画終了後アクションの待機planは作成時snapshot。設定保存後に旧planを保持せず、
            // 単一出口で即時破棄し、次に最後まで残った録画がCompleted（正常完了）した時点から新設定で再作成する。
            // 手動停止・録画失敗などの非Completed終端では新しい録画終了後planを作成しない。
            _reservationScheduler.OnRecordingAfterActionSettingsChanged();
        }

        if (epgPolicyChanged)
        {
            _log.Add("EPG_SETTINGS_SAVE_AUDIT", "Settings",
                $"changed=True dailySchedule={dailyEpgScheduleChanged} preRecord={preRecordEpgPolicyChanged} workerPolicy={epgWorkerPolicyChanged} enabled={before.EpgEnabled}->{after.EpgEnabled} time={before.EpgHour:D2}:{before.EpgMinute:D2}->{after.EpgHour:D2}:{after.EpgMinute:D2} depth={before.EpgDepth}->{after.EpgDepth} preRecordMinutes={before.EpgPreRecordMinutes}->{after.EpgPreRecordMinutes} rule=release_contract");
        }
        if (dailyEpgScheduleChanged)
        {
            // Daily設定はINI commit前のRunningガードを通過済み。UpdateConfigは確定した設定を
            // 未開始DailyのPlanner/Projectionへ反映するだけで、開始済みrunへ後段上塗りしない。
            try
            {
                _epgScheduler.UpdateConfig(
                    after.EpgEnabled,
                    after.EpgHour,
                    after.EpgMinute,
                    after.EpgDepth,
                    refreshAllocationRoute: false);
            }
            catch (Exception ex)
            {
                postCommitWarnings.Add("定時EPG予定の反映に失敗しました。ログを確認してください。");
                _log.Add("SETTINGS_POST_COMMIT_WARNING", "EpgSchedule",
                    $"result=FAILED stage=epg_update_config persisted=True exception={ex.GetType().Name} reason={ex.Message} action=retry_from_persisted_settings rule=settings_post_commit_side_effect_contract");
            }
        }

        // SETTINGS_STARTUP_PROJECTION_RECONCILIATION_CONTRACT
        // StartupEnabledの正本は保存済みINI。Runキーは外部投影なので、Startup値を変更した時だけでなく
        // すべての設定保存成功後に正本へ再同期する。実行中にRunキーが削除・変更されていても、
        // unrelated setting saveで古い外部状態を残さない。Setは同値ならno-op。
        // 反映失敗は保存済みINIを巻き戻さず、部分適用として呼出元へ明示する。
        var startupApplySucceeded = _startupService.Set(after.StartupEnabled);

        // チューナー構成は保存のみでRuntime未変更、ChannelMapはChannelFileLoaderの即時再読込責務であるため、
        // どちらも共通割当・PreRec・Wakeの再実行理由にしない。
        var epgScheduleRefreshRequired = dailyEpgScheduleChanged || preRecordEpgPolicyChanged;
        var allocationRefreshRequired = recordingSchedulePolicyChanged || epgScheduleRefreshRequired;
        // SETTINGS_PROGRAM_RULE_SYNC_SCOPE_CONTRACT
        // ProgramRule予約の発生時刻・曜日・期限・局指定はProgramRule自身が正本であり、
        // LaterProgramPriority／PseudoContinuousRecording／録画前後マージン／EPG設定を参照しない。
        // したがって設定保存を理由にProgramRule予約を再生成しない。録画方針変更は既存予約の
        // Allocation／PreRec／Wakeだけを新設定で再評価する。ProgramRuleの作成・変更・削除側が同期責務を持つ。
        const bool programRuleSyncRequired = false;
        // Daily EPGのON/OFF・時刻・深度変更はDaily行だけの責務であり、
        // 録画前EPG確認行を削除・再生成する理由にはしない。
        // PreRec再構築は録画スケジュール方針または録画前EPG確認時刻が変わった場合だけ行う。
        var preRecordRefreshRequired = recordingSchedulePolicyChanged || preRecordEpgPolicyChanged;
        var wakeRefreshRequired = allocationRefreshRequired || wakePolicyChanged;
        var conflictLogRequired = recordingSchedulePolicyChanged;
        // Route起動条件は、現在の包含関係（WakeがAllocationを内包する等）へ依存させない。
        // 各副作用フラグのどれかがtrueなら必ず共通Routeを通し、将来の責務分離で
        // PreRecやProgramRuleだけがtrueになっても処理が黙って脱落しないようにする。
        var routeExecutionRequired =
            programRuleSyncRequired ||
            allocationRefreshRequired ||
            preRecordRefreshRequired ||
            wakeRefreshRequired ||
            conflictLogRequired;
        var allocationRouteSucceeded = true;
        if (routeExecutionRequired)
        {
            try
            {
                _allocationRoute.Run(new ReservationAllocationRouteRequest(
                    Source: "Settings",
                    Action: "Save",
                    RunKeywordMatcher: false,
                    SyncProgramRuleReservations: programRuleSyncRequired,
                    ReevaluateAllocations: allocationRefreshRequired,
                    RefreshPreRecordEpgEntries: preRecordRefreshRequired,
                    RefreshWakeTask: wakeRefreshRequired,
                    EmitConflictLogs: conflictLogRequired,
                    ConflictLogCategory: "Settings",
                    ConflictLogTitle: "Conflict"));
            }
            catch (Exception ex)
            {
                allocationRouteSucceeded = false;
                postCommitWarnings.Add("予約割当・録画前EPG・Wakeの再反映に失敗しました。ログを確認してください。");
                _log.Add("SETTINGS_POST_COMMIT_WARNING", "AllocationRoute",
                    $"result=FAILED stage=allocation_route persisted=True exception={ex.GetType().Name} reason={ex.Message} action=retry_from_persisted_settings rule=settings_post_commit_side_effect_contract");
            }
        }

        // TunerTopologyの再起動要否は「今回保存値が変わったか」ではなく、
        // 保存済みTopologyが現在Runtimeと不一致かだけで決める。
        // 保留中の保存値を現在Runtimeへ戻した場合は、その保存操作で差分が発生しても再起動不要となる。
        // SETTINGS_NETWORK_RUNTIME_APPLY_CONTRACT
        // LAN公開と認証パスワードは要求処理がSingleton正本を都度参照するため保存直後から反映する。
        // セッション有効期間はログイン時にsession expiryへsnapshotし、既存sessionの期限を後段で書き換えない。
        // NetworkAccess変更を再起動要否へ後段上塗りしてはならない。
        var requiresRestart = topologyRestartPendingAfter || hostRestartPendingAfter;

        _log.Add("SETTINGS_SAVE", "Settings",
            $"result=APPLIED persistedChanges={changedAreas.Count} areas=[{string.Join(',', changedAreas.OrderBy(x => x, StringComparer.Ordinal))}] persistedTopologyChanged={tunerTopologyChanged} runtimeTopologyPending={topologyRestartPendingAfter} runtimeHostPending={hostRestartPendingAfter} recordingSchedule={recordingSchedulePolicyChanged} recordingCompletion={recordingCompletionPolicyChanged} workerPresentation={recordingWorkerPresentationChanged} epgDaily={dailyEpgScheduleChanged} epgPreRecord={preRecordEpgPolicyChanged} epgWorker={epgWorkerPolicyChanged} networkImmediate={networkAccessChanged} revokedSessions={revokedNetworkSessions} clearedLoginFailures={clearedNetworkLoginFailures} route={routeExecutionRequired} routeApply={allocationRouteSucceeded} syncProgram={programRuleSyncRequired} allocation={allocationRefreshRequired} preRecRefresh={preRecordRefreshRequired} wake={wakeRefreshRequired} conflictLog={conflictLogRequired} startupApply={startupApplySucceeded} postCommitWarnings={postCommitWarnings.Count} restart={requiresRestart} rule=settings_canonical_diff_contract");

        // SETTINGS_SAVE_RESULT_MESSAGE_COMPOSITION_CONTRACT
        // 外部副作用の部分失敗と再起動待ちは同時に成立し得るため、優先順位で片方を隠さず加算して通知する。
        // 特にStartup反映失敗とTuner topology保留が同時に起きた保存で、再起動条件をメッセージから脱落させない。
        var messageParts = new List<string> { "設定を保存しました。" };
        if (!startupApplySucceeded)
            messageParts.Add("Windows自動起動設定の反映に失敗しました。ログを確認してください。");
        messageParts.AddRange(postCommitWarnings.Distinct(StringComparer.Ordinal));
        if (topologyRestartPendingAfter)
            messageParts.Add("チューナー変更はTvAIrの再起動後に反映されます。");
        if (hostRestartPendingAfter)
            messageParts.Add("データ保存先またはポート変更はTvAIrの再起動後に反映されます。");
        var message = string.Join(string.Empty, messageParts);

        return new SettingsChangeResult(
            Message: message,
            PersistedChanged: true,
            RequiresRestart: requiresRestart,
            TunerTopologyRestartRequired: topologyRestartPendingAfter,
            ReservationActionUiHotReloaded: reservationActionUiPolicyChanged,
            ThemeRevision: themeRevision);
    }

    private static bool HasPersistedDifference(IniSettingsDto current, IniSettingsUpdateDto requested)
    {
        if (!EqualsPath(current.TvTestExecutablePath, requested.TvTestExecutablePath) ||
            !EqualsPath(current.ViewingTvTestExecutablePath, requested.ViewingTvTestExecutablePath) ||
            !EqualsPath(current.BonDriverDirectory, requested.BonDriverDirectory) ||
            !EqualsPath(current.GrChannelFilePath, requested.GrChannelFilePath) ||
            !EqualsPath(current.GrChSetFilePath, requested.GrChSetFilePath) ||
            !EqualsPath(current.BscsChannelFilePath, requested.BscsChannelFilePath) ||
            !EqualsPath(current.BscsChSetFilePath, requested.BscsChSetFilePath) ||
            !EqualsPath(current.DataDirectory, requested.DataDirectory) ||
            !EqualsIgnoreCase(IniSettingsService.NormalizeSystemTheme(current.SystemTheme), IniSettingsService.NormalizeSystemTheme(requested.SystemTheme)) ||
            current.Port != SettingsDefaults.NormalizePort(requested.Port) || current.EpgEnabled != requested.EpgEnabled ||
            current.EpgHour != SettingsDefaults.NormalizeEpgHour(requested.EpgHour) ||
            current.EpgMinute != SettingsDefaults.NormalizeEpgMinute(requested.EpgMinute) ||
            !EqualsIgnoreCase(current.EpgDepth, SettingsDefaults.NormalizeEpgDepth(requested.EpgDepth)) ||
            current.EpgPreRecordMinutes != SettingsDefaults.NormalizeEpgPreRecordMinutes(requested.EpgPreRecordMinutes) ||
            current.LaterProgramPriority != requested.LaterProgramPriority ||
            current.PseudoContinuousRecording != requested.PseudoContinuousRecording ||
            current.PreStartMarginSeconds != SettingsDefaults.NormalizePreStartMarginSeconds(requested.PreStartMarginSeconds) ||
            current.PostEndMarginSeconds != SettingsDefaults.NormalizePostEndMarginSeconds(requested.PostEndMarginSeconds) ||
            current.WakeMinutesBefore != SettingsDefaults.NormalizeWakeMinutesBefore(requested.WakeMinutesBefore) ||
            current.WakeAdditionalSeconds != SettingsDefaults.NormalizeWakeAdditionalSeconds(requested.WakeAdditionalSeconds) ||
            current.UseMinOption != requested.UseMinOption || current.UseNodshowOption != requested.UseNodshowOption ||
            current.ShowTvAIrEpgRecTaskbarIcon != requested.ShowTvAIrEpgRecTaskbarIcon ||
            current.StartupEnabled != requested.StartupEnabled ||
            current.NetworkLanAccessEnabled != requested.NetworkLanAccessEnabled ||
            current.NetworkSessionLifetimeMinutes != SettingsDefaults.NormalizeNetworkSessionLifetimeMinutes(requested.NetworkSessionLifetimeMinutes) ||
            !EqualsIgnoreCase(current.RecordingAfterAction, IniSettingsService.NormalizeRecordingAfterAction(requested.RecordingAfterAction)) ||
            current.RecordingAfterActionDelayMinutes != IniSettingsService.NormalizeRecordingAfterActionDelayMinutes(requested.RecordingAfterActionDelayMinutes) ||
            current.UserLogDetailEnabled != requested.UserLogDetailEnabled ||
            current.UserLogDetailReservationSource != requested.UserLogDetailReservationSource ||
            current.UserLogDetailScheduledTime != requested.UserLogDetailScheduledTime ||
            current.UserLogDetailActualRecordingTime != requested.UserLogDetailActualRecordingTime ||
            current.UserLogDetailRecordingQuality != requested.UserLogDetailRecordingQuality ||
            current.UserLogDetailStateChange != requested.UserLogDetailStateChange ||
            current.UserLogDetailEndOrFailureReason != requested.UserLogDetailEndOrFailureReason ||
            current.EpgUseBelowNormalPriority != requested.EpgUseBelowNormalPriority ||
            current.EpgDisableImmediateRetry != requested.EpgDisableImmediateRetry ||
            !EqualsTrimmed(current.TaskUserName, requested.TaskUserName) ||
            !EqualsIgnoreCase(IniSettingsService.BuildTunerTopologySignature(current.Tuners), IniSettingsService.BuildTunerTopologySignature(requested.Tuners)) ||
            !EqualsIgnoreCase(
                IniSettingsService.BuildThemeGenrePaletteSignature(current.ThemeGenrePalettes),
                IniSettingsService.BuildThemeGenrePaletteSignature(requested.ThemeGenrePalettes)))
            return true;

        // SETTINGS_PASSWORD_DIFF_CONTRACT
        // 平文欄は null／空欄の意味が保存処理ごとに異なるため、SaveFromApplicationServiceと同じ条件で判定する。
        // 未入力文字列や、既に未設定の資格情報に対するClearだけでINI保存・後段反映を起動してはならない。
        var taskPasswordChanged =
            !string.IsNullOrEmpty(requested.TaskPasswordPlain) ||
            (requested.ClearTaskPassword && current.TaskHasPassword);
        var networkPasswordChanged =
            !string.IsNullOrWhiteSpace(requested.NetworkPasswordPlain) ||
            (requested.ClearNetworkPassword && current.NetworkHasPassword);
        return taskPasswordChanged || networkPasswordChanged;
    }

    private static bool EqualsTrimmed(string? left, string? right) =>
        string.Equals(IniSettingsService.NormalizeTaskUserName(left), IniSettingsService.NormalizeTaskUserName(right), StringComparison.Ordinal);

    private static bool EqualsPath(string? left, string? right) =>
        string.Equals(IniSettingsService.NormalizePathValue(left), IniSettingsService.NormalizePathValue(right), StringComparison.OrdinalIgnoreCase);

    private static void ValidateRequest(IniSettingsUpdateDto requested)
    {
        if (string.IsNullOrWhiteSpace(requested.DataDirectory))
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.DataDirectory), "データ保存先フォルダが未入力です。");
        if (!SettingsDefaults.EpgHourOptions.Contains(requested.EpgHour))
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.EpgHour), "定期取得時刻の時を選択してください。");
        if (!SettingsDefaults.EpgMinuteOptions.Contains(requested.EpgMinute))
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.EpgMinute), "定期取得時刻の分を選択してください。");
        if (!SettingsDefaults.IsPreStartMarginSecondsAllowed(requested.PreStartMarginSeconds))
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.PreStartMarginSeconds), "録画開始マージンを選択してください。");
        if (!SettingsDefaults.IsPostEndMarginSecondsAllowed(requested.PostEndMarginSeconds))
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.PostEndMarginSeconds), "録画終了マージンを選択してください。");
        if (!SettingsDefaults.IsRecordingAfterActionDelayMinutesAllowed(requested.RecordingAfterActionDelayMinutes))
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.RecordingAfterActionDelayMinutes), "録画終了後の待機時間を選択してください。");
    }

    private static void ValidateNetworkAccessRequest(IniSettingsDto before, IniSettingsUpdateDto requested)
    {
        var hasNewPassword = !string.IsNullOrWhiteSpace(requested.NetworkPasswordPlain);
        if (hasNewPassword && requested.NetworkPasswordPlain!.Length < SettingsDefaults.NetworkPasswordMinLength)
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.NetworkPasswordPlain), $"接続用パスワードは{SettingsDefaults.NetworkPasswordMinLength}文字以上で入力してください。");

        var willHavePassword = !requested.ClearNetworkPassword && (before.NetworkHasPassword || hasNewPassword);
        if (requested.NetworkLanAccessEnabled && !willHavePassword)
            throw new SettingsValidationException(nameof(IniSettingsUpdateDto.NetworkPasswordPlain), "LANからの接続を許可するには、接続用パスワードを設定してください。");
    }

    private static bool EqualsIgnoreCase(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string SafeLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
    }

}
