namespace TvAIrPlugin;

/// <summary>
/// TvAIr プラグインの契約インターフェース。
/// Plugins/ ディレクトリに配置した DLL 内でこのインターフェースを実装すると
/// 起動時に自動ロードされる。
/// </summary>
public interface ITvAIrPlugin
{
    /// <summary>プラグイン名（ログ表示用）。</summary>
    string Name { get; }

    /// <summary>プラグインバージョン（ログ表示用）。</summary>
    string Version { get; }

    /// <summary>
    /// ローダーがプラグインを検出した直後に呼ばれる。
    /// context 経由でログ出力・AppDirectory 取得ができる。
    /// </summary>
    void Initialize(IPluginContext context);

    /// <summary>本体の IHost が起動完了した後に呼ばれる。</summary>
    void OnStart();

    /// <summary>本体がシャットダウンを開始する前に呼ばれる。</summary>
    void OnStop();

    /// <summary>録画開始時に本体から通知される。既存プラグイン互換のため既定実装は何もしない。</summary>
    void OnRecordingStarted(PluginRecordingInfo info) { }

    /// <summary>録画終了時に本体から通知される。既存プラグイン互換のため既定実装は何もしない。</summary>
    void OnRecordingStopped(PluginRecordingInfo info) { }

    /// <summary>TSファイル再生開始時に本体から通知される。既存プラグイン互換のため既定実装は何もしない。</summary>
    void OnPlaybackStarted(PluginPlaybackInfo info) { }

    /// <summary>TSファイル再生停止時に本体から通知される。既存プラグイン互換のため既定実装は何もしない。</summary>
    void OnPlaybackStopped(PluginPlaybackInfo info) { }

    /// <summary>TSファイル再生位置変更時に本体から通知される。既存プラグイン互換のため既定実装は何もしない。</summary>
    void OnPlaybackPositionChanged(PluginPlaybackPosition position) { }
}
