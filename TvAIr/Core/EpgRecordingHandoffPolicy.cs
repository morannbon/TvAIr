namespace TvAIr.Core;

/// <summary>
/// 通常EPG（定時・手動・サイレント）から予約録画へ物理チューナーを確実に引き渡すための共通境界。
/// EPG取得時間そのものの見積り余裕とは別責務であり、録画開始マージン込みのDue境界より前に
/// 完全な空白を確保する。PreRec EPGは録画直前確認という別契約のため、この境界を適用しない。
/// </summary>
internal static class EpgRecordingHandoffPolicy
{
    internal static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(5);

    internal static DateTime RecordingDueStart(DateTime reservationStart, int preStartMarginSeconds)
        => reservationStart.AddSeconds(-Math.Max(0, preStartMarginSeconds));

    internal static DateTime LatestNormalEpgEndBeforeRecording(DateTime reservationStart, int preStartMarginSeconds)
        => RecordingDueStart(reservationStart, preStartMarginSeconds) - SafetyMargin;
}
