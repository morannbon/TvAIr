namespace TvAIrPlugin;

/// <summary>
/// プラグインがTvAIr本体機能にアクセスするための正式API。
/// TvAIr本体のDB・内部クラス・UI/CSSへ直接依存しないための唯一の接点。
/// </summary>
public interface IPluginContext
{
    /// <summary>本体の実行ファイルが置かれているディレクトリの絶対パス。</summary>
    string AppDirectory { get; }

    /// <summary>TvAIr本体データの保存先ディレクトリ。直接DBアクセスではなく、原則Context APIを使う。</summary>
    string DataDirectory { get; }

    /// <summary>このプラグイン専用の保存先ディレクトリ。</summary>
    string PluginDataDirectory { get; }

    /// <summary>現在〜指定日数分のEPGを取得する。最大日数は本体側で安全に丸める。</summary>
    IReadOnlyList<PluginEpgEvent> GetEpg(PluginEpgQuery? query = null);

    /// <summary>予約一覧を取得する。</summary>
    IReadOnlyList<PluginReservation> GetReservations(PluginReservationQuery? query = null);

    /// <summary>完了・失敗・キャンセル済みを含む予約履歴を取得する。</summary>
    IReadOnlyList<PluginReservation> GetReservationHistory(PluginReservationHistoryQuery? query = null);

    /// <summary>現在のチューナー状態を取得する。</summary>
    IReadOnlyList<PluginTunerStatus> GetTunerStatus();

    /// <summary>現在の競合一覧を取得する。</summary>
    IReadOnlyList<PluginConflictInfo> GetConflicts();

    /// <summary>登録前に競合・チューナー割当の概算を返す。DBは変更しない。</summary>
    PluginReservationPreview PreviewReservationAllocation(PluginReservationDraft draft);

    /// <summary>チェーン候補を取得する。</summary>
    IReadOnlyList<PluginChainInfo> GetChainCandidates(PluginChainQuery? query = null);

    /// <summary>チェーン予約の成立可否を事前確認する。DBは変更しない。</summary>
    PluginChainPreview PreviewChainReservation(PluginReservationDraft draft);

    /// <summary>予約を追加する。プラグイン作成予約としてAuditLogへ記録する。</summary>
    PluginReservationOperationResult AddReservation(PluginReservationDraft draft);

    /// <summary>予約を更新する。原則として当該プラグイン作成予約、または明示許可された範囲のみ扱う。</summary>
    PluginReservationOperationResult UpdateReservation(PluginReservationUpdate update);

    /// <summary>予約を削除する。原則として当該プラグイン作成予約のみ扱う。</summary>
    PluginReservationOperationResult DeleteReservation(int reservationId, bool force = false);

    /// <summary>プラグイン専用ファイルを読み込む。パスはPluginDataDirectory配下に制限される。</summary>
    string? ReadPluginFile(string relativePath);

    /// <summary>プラグイン専用ファイルを書き込む。パスはPluginDataDirectory配下に制限される。</summary>
    void WritePluginFile(string relativePath, string content);

    /// <summary>プラグイン専用設定を読み込む。</summary>
    string? ReadPluginSettings(string name = "settings.json");

    /// <summary>プラグイン専用設定を書き込む。</summary>
    void WritePluginSettings(string content, string name = "settings.json");

    /// <summary>本体ログへ出力する。</summary>
    void Log(string message);

    /// <summary>レベル付きログを出力する。</summary>
    void Log(PluginLogLevel level, string message);

    /// <summary>情報通知を本体ログ/通知タイムラインへ記録する。</summary>
    void NotifyInfo(string message);

    /// <summary>警告通知を本体ログ/通知タイムラインへ記録する。</summary>
    void NotifyWarning(string message);

    /// <summary>エラー通知を本体ログ/通知タイムラインへ記録する。</summary>
    void NotifyError(string message);

    /// <summary>プラグインの時系列イベントを記録する。</summary>
    void AddTimelineEvent(string title, string message);

    /// <summary>監査ログを記録する。</summary>
    void AddAuditLog(string action, string message);
}
