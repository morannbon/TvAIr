namespace TvAIr.Core;

/// <summary>
/// ユーザー明示チェーン予約の共通契約。
/// UI/API/保存トポロジー/共通割当/競合/取消/復旧/実行境界で同じ定義を使用する。
///
/// CHAIN_DEVELOPER_APPROVAL_REQUIRED — 変更禁止:
/// チェーン予約に関する仕様、判定条件、保存値、優先順位、物理Tuner固定、競合伝播、取消時の再構成、
/// 30秒前停止、stop/restart handoff、失敗時の終端・復旧、監査ログの意味を変更する場合は、
/// 実装前にTvAIr開発者の明示承認を必須とする。局所的な不具合修正、リファクタリング、最適化、
/// デッドコード削除、ログ整理であっても、上記契約の結果または実行順序を変える可能性がある変更を
/// 承認なしで行ってはならない。変更時は全チェーン経路の横断監査と実境界検証を再実施する。
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
