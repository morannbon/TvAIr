namespace TvAIr.Schedule;

/// <summary>
/// release_contract: チェーン実行セッションの読み取り用レジストリ。
/// CHAIN_AUDIT が共通割り当てスナップショットだけでは録画中の前番組を
/// 評価対象外として見失う場合、実行中セッションの actualTuner / DID / pid を参照する。
/// 録画実行・停止・Bridge継続・ファイル切替には介入しない。
/// </summary>
public sealed class ChainDirectRecorderSessionRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<int, ChainDirectRecorderSession> byCurrentReservationId = new();

    public bool Bind(ChainDirectRecorderSession session)
    {
        lock (gate)
        {
            var isNew = !byCurrentReservationId.ContainsKey(session.CurrentReservationId);
            byCurrentReservationId[session.CurrentReservationId] = session;
            return isNew;
        }
    }

    public bool Remove(int currentReservationId, out ChainDirectRecorderSession? removed)
    {
        lock (gate)
        {
            if (byCurrentReservationId.TryGetValue(currentReservationId, out removed))
            {
                byCurrentReservationId.Remove(currentReservationId);
                return true;
            }
            removed = null;
            return false;
        }
    }

    public bool TryGetByCurrentReservationId(int currentReservationId, out ChainDirectRecorderSession? session)
    {
        lock (gate)
        {
            return byCurrentReservationId.TryGetValue(currentReservationId, out session);
        }
    }

    public IReadOnlyList<ChainDirectRecorderSession> Snapshot()
    {
        lock (gate)
        {
            return byCurrentReservationId.Values
                .OrderBy(x => x.ChainRootReservationId)
                .ThenBy(x => x.CurrentReservationId)
                .ToList();
        }
    }
}
