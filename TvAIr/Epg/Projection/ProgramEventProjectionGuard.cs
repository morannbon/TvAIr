using TvAIr.Epg;

namespace TvAIr.Epg.Projection;

/// <summary>
/// 自動検索予約へ流してよい投影イベントかを判定する。
/// DB由来イベントは DbOnly / DbWithOverlay を問わずDB正本として許可し、
/// overlay-only は外部ソース識別子がある場合だけ許可する。
/// </summary>
public static class ProgramEventProjectionGuard
{
    public static bool IsSafeForAutoReservation(ProjectedProgramEvent ev, out string reason)
    {
        reason = string.Empty;
        if (ev is null)
        {
            reason = "null_event";
            return false;
        }

        if (ev.Start == default || ev.End == default || ev.End <= ev.Start)
        {
            reason = "invalid_time";
            return false;
        }

        if (ev.NetworkId == 0 || ev.TransportStreamId == 0 || ev.ServiceId == 0)
        {
            reason = "invalid_service_identity";
            return false;
        }

        if (!EpgTitleProjectionGuard.IsSafeTitleForCandidate(ev.Title, out reason))
            return false;

        if (ev.DbEventExists || ev.DbEvent is not null)
        {
            if (ev.DbEvent is null)
            {
                reason = "db_event_missing";
                return false;
            }

            return true;
        }

        if (string.Equals(ev.ProjectionState, ProjectedEventStates.OverlayOnly, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ev.SourceKind, ProjectedEventSourceKinds.ExternalEpg, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(ev.SourcePluginId) || string.IsNullOrWhiteSpace(ev.SourceEventKey))
            {
                reason = "external_source_identity_missing";
                return false;
            }

            return true;
        }

        reason = "unsupported_projection_state";
        return false;
    }
}
