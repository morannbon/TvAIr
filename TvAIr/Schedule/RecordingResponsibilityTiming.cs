namespace TvAIr.Schedule;

/// <summary>
/// 録画開始責務を高優先度で扱い始める共通時間軸。
/// due監視とPower保持は同じ先読み境界を使い、片方だけが遅れて保護区間に穴を作らない。
/// </summary>
internal static class RecordingResponsibilityTiming
{
    public const int DueLookAheadSeconds = 60;
}
