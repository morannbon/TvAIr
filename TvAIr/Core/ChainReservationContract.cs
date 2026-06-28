namespace TvAIr.Core;

/// <summary>
/// ユーザー明示チェーン予約の隣接判定契約。
/// UI/API/共通割当で同じ境界を使うため、秒数をここに集約する。
/// </summary>
public static class ChainReservationContract
{
    /// <summary>前番組終了より最大5秒早く始まる境界ずれを許容する。</summary>
    public const int AdjacentMinGapSeconds = -5;

    /// <summary>EPG境界ずれ吸収のため、最大120秒後開始までを連続扱いする。</summary>
    public const int AdjacentMaxGapSeconds = 120;

    public const int AdjacentMinGapMilliseconds = AdjacentMinGapSeconds * 1000;
    public const int AdjacentMaxGapMilliseconds = AdjacentMaxGapSeconds * 1000;

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
