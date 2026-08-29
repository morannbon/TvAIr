using TvAIr.Core;

namespace TvAIr.Plugin;

internal sealed record PluginReservationConflict(
    int ReservationId,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string Title,
    string ServiceName,
    DateTime StartTime,
    DateTime EndTime,
    string Reason);

internal sealed record PluginChainCandidate(
    int PreviousReservationId,
    int CurrentReservationId,
    string CurrentProgramId,
    bool SameTuner,
    string LossTarget,
    string LossPart,
    string LossDescription,
    bool IsAllowed);

internal sealed record PluginReservationPlanningDraft(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    DateTime StartTime,
    DateTime EndTime,
    int? ChainPreviousReservationId);

internal sealed record PluginChainCandidateQuery(
    DateTime? From,
    DateTime? To,
    ushort? NetworkId,
    ushort? TransportStreamId,
    ushort? ServiceId);

internal sealed record PluginReservationPlanningPreview(
    bool CanReserve,
    bool HasConflict,
    string Reason,
    string SuggestedTunerName,
    IReadOnlyList<PluginReservationConflict> Conflicts,
    IReadOnlyList<PluginChainCandidate> ChainCandidates);

internal sealed record PluginChainPlanningPreview(
    bool CanChain,
    string Message,
    PluginChainCandidate? ChainInfo);

/// <summary>
/// Reservation conflict and user-chain planning shared by the Runtime reservations API.
/// This service is read-only; final allocation remains owned by the common reservation allocation route after mutation.
/// </summary>
internal sealed class PluginReservationPlanningService
{
    private readonly PluginReadModelSource _readModels;
    private readonly IniSettingsService _ini;

    public PluginReservationPlanningService(PluginReadModelSource readModels, IniSettingsService ini)
    {
        _readModels = readModels;
        _ini = ini;
    }

    public IReadOnlyList<PluginReservationConflict> GetConflicts()
        => _readModels.GetReservations()
            .Where(r => r.Source != ReservationSource.Epg && r.IsConflicted)
            .OrderBy(r => r.StartTime)
            .Select(r => new PluginReservationConflict(
                r.Id,
                r.NetworkId,
                r.TransportStreamId,
                r.ServiceId,
                r.Title,
                _readModels.ResolveCurrentServiceName(r),
                r.StartTime,
                r.EndTime,
                "チューナー割当競合"))
            .ToList();

    public PluginReservationPlanningPreview PreviewReservation(PluginReservationPlanningDraft draft)
    {
        var overlap = _readModels.GetReservations()
            .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled)
            .Where(r => r.EndTime > draft.StartTime && r.StartTime < draft.EndTime)
            .ToList();

        var sameServiceChain = GetChainCandidates(new PluginChainCandidateQuery(
            draft.StartTime.AddHours(-4),
            draft.EndTime.AddHours(4),
            draft.NetworkId == 0 ? null : draft.NetworkId,
            draft.TransportStreamId == 0 ? null : draft.TransportStreamId,
            draft.ServiceId == 0 ? null : draft.ServiceId));

        var conflicts = overlap
            .Where(r => r.IsConflicted)
            .Select(r => new PluginReservationConflict(
                r.Id,
                r.NetworkId,
                r.TransportStreamId,
                r.ServiceId,
                r.Title,
                _readModels.ResolveCurrentServiceName(r),
                r.StartTime,
                r.EndTime,
                "既存予約が競合状態"))
            .ToList();

        return new PluginReservationPlanningPreview(
            conflicts.Count == 0,
            conflicts.Count > 0,
            conflicts.Count == 0
                ? "登録可能見込みです。最終割当は本登録後にTvAIr本体が再評価します。"
                : "既存競合があります。",
            overlap.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.TunerName))?.TunerName ?? string.Empty,
            conflicts,
            sameServiceChain);
    }

    public IReadOnlyList<PluginChainCandidate> GetChainCandidates(PluginChainCandidateQuery? query = null)
    {
        query ??= new PluginChainCandidateQuery(null, null, null, null, null);
        var from = query.From ?? DateTime.Now.AddHours(-1);
        var to = query.To ?? DateTime.Now.AddDays(7);

        var reservations = _readModels.GetReservations()
            .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled)
            .Where(r => r.EndTime > from && r.StartTime < to)
            .Where(r => !query.NetworkId.HasValue || r.NetworkId == query.NetworkId.Value)
            .Where(r => !query.TransportStreamId.HasValue || r.TransportStreamId == query.TransportStreamId.Value)
            .Where(r => !query.ServiceId.HasValue || r.ServiceId == query.ServiceId.Value)
            .OrderBy(r => r.StartTime)
            .ToList();

        var result = new List<PluginChainCandidate>();
        var featureEnabled = _ini.LaterProgramPriority && _ini.PseudoContinuousRecording;
        foreach (var serviceGroup in reservations
                     .GroupBy(r => (r.NetworkId, r.TransportStreamId, r.ServiceId)))
        {
            var ordered = serviceGroup.OrderBy(r => r.StartTime).ThenBy(r => r.Id).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1];
                var current = ordered[i];
                var eligibility = ChainReservationEligibilityContract.EvaluatePair(
                    previous,
                    current.NetworkId,
                    current.TransportStreamId,
                    current.ServiceId,
                    current.StartTime,
                    featureEnabled);
                if (!eligibility.IsEligible && current.UserChainPreviousId != previous.Id)
                    continue;

                var sameTuner = !string.IsNullOrWhiteSpace(previous.TunerName)
                    && string.Equals(previous.TunerName, current.TunerName, StringComparison.OrdinalIgnoreCase);

                result.Add(new PluginChainCandidate(
                    previous.Id,
                    current.Id,
                    $"{current.NetworkId}:{current.TransportStreamId}:{current.ServiceId}:{current.EventId}",
                    sameTuner,
                    "previous",
                    "end",
                    "前番組後半がカットされます",
                    eligibility.IsEligible || current.UserChainPreviousId == previous.Id));
            }
        }

        return result;
    }

    public PluginChainPlanningPreview PreviewChain(PluginReservationPlanningDraft draft)
    {
        var candidates = GetChainCandidates(new PluginChainCandidateQuery(
            draft.StartTime.AddHours(-4),
            draft.EndTime,
            draft.NetworkId == 0 ? null : draft.NetworkId,
            draft.TransportStreamId == 0 ? null : draft.TransportStreamId,
            draft.ServiceId == 0 ? null : draft.ServiceId));
        var selected = candidates.FirstOrDefault(c => c.PreviousReservationId == draft.ChainPreviousReservationId)
            ?? candidates.LastOrDefault(c => c.IsAllowed);

        return new PluginChainPlanningPreview(
            selected is not null && selected.IsAllowed,
            selected is null ? "チェーン候補はありません。" : selected.LossDescription,
            selected);
    }
}
