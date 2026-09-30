namespace TvAIr.Schedule;

/// <summary>
/// release_contract: チェーン実行セッションの読み取り用レジストリ。
/// CHAIN_AUDIT が共通割り当てスナップショットだけでは録画中の前番組を
/// 評価対象外として見失う場合、実行中セッションの actualTuner / DID / pid を参照する。
/// 録画実行やcontinuous captureのsegment切替には介入せず、実行中セッションの参照だけを提供する。
/// </summary>
public sealed class ChainRuntimeSnapshotRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<int, ChainRuntimeSnapshot> byCurrentReservationId = new();

    public bool Bind(ChainRuntimeSnapshot session)
    {
        lock (gate)
        {
            var isNew = !byCurrentReservationId.ContainsKey(session.CurrentReservationId);
            byCurrentReservationId[session.CurrentReservationId] = session;
            return isNew;
        }
    }

    public bool Remove(int currentReservationId, out ChainRuntimeSnapshot? removed)
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

    public bool TryGetByCurrentReservationId(int currentReservationId, out ChainRuntimeSnapshot? session)
    {
        lock (gate)
        {
            return byCurrentReservationId.TryGetValue(currentReservationId, out session);
        }
    }

    public IReadOnlyList<ChainRuntimeSnapshot> Snapshot()
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
