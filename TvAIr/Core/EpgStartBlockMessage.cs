namespace TvAIr.Core;

/// <summary>
/// Resolve only recognized admission reasons. Unknown reasons must not be
/// guessed from substrings (for example "recording" in an unrelated code).
/// This owner is used both by the UI and the persisted user-event message.
/// </summary>
internal static class EpgStartBlockMessage
{
    internal static string FromReason(string? reason)
        => (reason ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "target_duration_unavailable" => "EPG取得に必要なチャンネルまたはチューナーの設定を確認してください",
            "no_free_tuner" or "no_tuner" or "no_free" => "空きチューナーがないためEPG取得を開始できません",
            "warmup" or "recording_warmup" => "録画開始直後のためEPG取得を開始できません",
            "recording_occupancy_overlap" or "window_insufficient" or "recording_timeline_changed" => "録画予約が重なるためEPG取得を開始できません",
            "epg_already_running" => "EPG取得中のため新たに開始できません",
            "scheduler_stopping" => "EPG取得の終了処理中のため開始できません",
            "system_sleep_inhibition_unavailable" => "スリープ抑止を設定できないためEPG取得を開始できません",
            _ => "EPG取得を開始できません"
        };
}
