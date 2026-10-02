namespace TvAIr.Core;

/// <summary>
/// ユーザー明示チェーン予約の共通契約。
/// UI/API/保存トポロジー/共通割当/競合/取消/復旧/実行境界で同じ定義を使用する。
///
/// CURRENT INVARIANTS:
/// 1. トポロジー正本はチェーンボタンで永続化した IsUserChain / UserChainPreviousId / UserChainRootId。
///    同一局・隣接時刻・同一Tunerだけを根拠に自動チェーンを再生成しない。
/// 2. 物理Tunerは、録画開始前はFinalConflictPlanがroot単位で解決し、保存済みTunerNameは再選択可能な候補として扱う。
///    shared capture開始後は現在ownerのActualTunerNameをhard pinし、残る全後続を同じ物理Tunerへ固定する。
/// 3. 実行時は1本のBonDriver/OpenTuner/TS-read workerをチェーン物理終端まで維持する。論理境界ではTuner解放、
///    BonDriver Close、retune、worker restartを行わず、予約ごとの独立Sink/TSファイルと論理ownerだけを切り替える。
/// 4. 各イベントは通常予約と同じ優先順位・前後マージン全量で競合判定する。チェーンであることを優先加点しない。
///    実行時のpre-margin/post-margin重複は同一capture上の別Sinkへ同時出力する。
/// 5. 取消は末尾=単体、途中=その地点以降、先頭=全体。取消/無効化/時刻短縮/rebase後のphysical capture終端は、
///    現在有効なsegment集合から再導出し、session / TunerPool lease / worker deadlineを延長・短縮の両方向へ収束させる。
/// 6. チェーン長に固定段数上限を設けない。保存済み直列トポロジーを終端またはcycle検出まで辿る。
/// 7. physical capture喪失時の復旧は通常の中断録画復旧契約へ戻す。チェーン専用の別worker/別Tuner救済経路を作らない。
/// 8. チェーン機能を有効→無効へ切り替えた場合、未開始(Scheduled)の保存トポロジーは通常予約へdetachする。
///    予約自体は削除せず、TunerNameを未確定へ戻して共通割当へ再投入する。競合は自動削除せず予約リストでユーザー判断に委ねる。
///    Starting/Recordingへ入ったsegmentは設定変更だけで物理captureを切断せず、残る保存トポロジーをruntimeが最後まで実行する。
/// 9. 正本の継ぎ目は Reservation topology → FinalConflictPlan → active ActualTuner/lease/PID → chain control → worker deadline の順で固定する。
///    topology mutation後はactive control/lifetimeを同じcanonical topologyへ再収束させ、その後に共通割当を再評価する。
/// 判定条件、保存トポロジー、割当、競合伝播、取消、設定OFF detach、時間追従、segment境界、owner移管、物理寿命、復旧は一つの契約として扱う。
/// 結果または実行順序を変更する場合は、UI/API/Store/割当/worker実行境界を横断監査し、実境界で再検証する。
/// </summary>
public static class ChainReservationContract
{
    /// <summary>前番組終了より最大5秒早く始まる境界ずれを許容する。</summary>
    public const int AdjacentMinGapSeconds = -5;

    /// <summary>EPG境界ずれ吸収のため、最大120秒後開始までを連続扱いする。</summary>
    public const int AdjacentMaxGapSeconds = 120;

    public const int AdjacentMinGapMilliseconds = AdjacentMinGapSeconds * 1000;
    public const int AdjacentMaxGapMilliseconds = AdjacentMaxGapSeconds * 1000;

    /// <summary>ユーザーが新規チェーンを作成できる設定上の有効状態。</summary>
    public static bool IsFeatureEnabled(bool laterProgramPriorityEnabled, bool chainRecordingEnabled)
        => laterProgramPriorityEnabled && chainRecordingEnabled;

    /// <summary>
    /// FinalConflictPlan / runtime がcontinuous chainとして扱うべきか。
    /// 設定OFF後もStarting/Recordingなど既に実行へ入った保存トポロジーは完走させるため、
    /// persistedPairCount が残る間は設定値より保存トポロジーを優先する。
    /// </summary>
    public static bool ShouldPlanContinuousCapture(
        bool configuredFeatureEnabled,
        int persistedPairCount)
        => configuredFeatureEnabled || persistedPairCount > 0;

    /// <summary>保存済み明示チェーンの後続行か。</summary>
    public static bool IsStoredSuccessor(Reservation reservation)
        => reservation.IsUserChain && reservation.UserChainPreviousId.HasValue;

    public static bool IsAdjacent(DateTime predecessorEnd, DateTime successorStart)
    {
        var gapSeconds = (successorStart - predecessorEnd).TotalSeconds;
        return gapSeconds >= AdjacentMinGapSeconds && gapSeconds <= AdjacentMaxGapSeconds;
    }

    public static bool IsAdjacent(TimeSpan gap)
    {
        var gapSeconds = gap.TotalSeconds;
        return gapSeconds >= AdjacentMinGapSeconds && gapSeconds <= AdjacentMaxGapSeconds;
    }
}
