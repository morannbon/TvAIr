using TvAIr.Core;

namespace TvAIr.Schedule;

public sealed record AutomaticChainEdge(int PreviousId, int ReservationId);
public sealed record AutomaticChainPlan(bool CanJoin, string Reason, int AddedReservationId,
    int RootReservationId, IReadOnlyList<int> OrderedReservationIds, IReadOnlyList<AutomaticChainEdge> AddedEdges);

/// <summary>
/// Pure decision component for the common allocation route, not a route/worker/event subscriber.
/// Input must be a consistent reservation snapshot. A plan is not a persistence or runtime permit.
/// An unchained scheduled reservation is a trigger; existing edges are indivisible.
/// </summary>
public static class AutomaticChainPlanner
{
    public static AutomaticChainPlan Plan(IReadOnlyList<Reservation> reservations, int addedId, DateTime now)
        => PlanCore(reservations,addedId,now,false);

    internal static AutomaticChainPlan PlanConvergence(IReadOnlyList<Reservation> reservations,int addedId,DateTime now)
        => PlanCore(reservations,addedId,now,true);

    private static AutomaticChainPlan PlanCore(IReadOnlyList<Reservation> reservations,int addedId,DateTime now,bool allowExistingComponent)
    {
        AutomaticChainPlan Skip(string reason) => new(false, reason, addedId, 0,
            Array.Empty<int>(), Array.Empty<AutomaticChainEdge>());
        if (reservations.Select(r => r.Id).Distinct().Count() != reservations.Count)
            return Skip("duplicate_identity");
        var byId = reservations.ToDictionary(r => r.Id);
        if (!byId.TryGetValue(addedId, out var added)) return Skip("added_reservation_missing");
        if (added.Status != ReservationStatus.Scheduled || !added.IsEnabled
            || added.Source == ReservationSource.Epg || added.StartTime <= now || added.EndTime <= added.StartTime)
            return Skip("added_reservation_unavailable");
        var active = reservations.Where(r => ChainReservationEligibilityContract.IsActivePredecessorStatus(r.Status)).ToArray();
        // Completed/failed rows are historical members only, never new predecessor candidates.
        var members = reservations.Where(r => ChainReservationEligibilityContract.IsActivePredecessorStatus(r.Status)
            || r.Status is ReservationStatus.Completed or ReservationStatus.Failed).ToArray();
        var children = members.Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue)
            .GroupBy(r => r.UserChainPreviousId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        if (!allowExistingComponent && (added.IsUserChain || added.UserChainPreviousId.HasValue || added.UserChainRootId.HasValue
            || children.ContainsKey(addedId))) return Skip("added_reservation_already_chained");

        List<Reservation>? Component(Reservation seed)
        {
            var visited = new HashSet<int>();
            var root = seed;
            while (root.UserChainPreviousId is { } previous)
            {
                if (!root.IsUserChain || !visited.Add(root.Id) || !byId.TryGetValue(previous, out var parent)) return null;
                root = parent;
            }
            if ((root.UserChainRootId ?? root.Id) != root.Id) return null;
            visited.Clear();
            var result = new List<Reservation>();
            var current = root;
            while (true)
            {
                if (!visited.Add(current.Id) || !current.IsEnabled || current.Source == ReservationSource.Epg
                    || !(ChainReservationEligibilityContract.IsActivePredecessorStatus(current.Status)
                        || current.Status is ReservationStatus.Completed or ReservationStatus.Failed)
                    || current.EndTime <= current.StartTime
                    || !ChainReservationEligibilityContract.IsSameService(root,current.NetworkId,current.TransportStreamId,current.ServiceId)) return null;
                if (current.Id != root.Id && (!current.IsUserChain || current.UserChainRootId != root.Id)) return null;
                result.Add(current);
                if (!children.TryGetValue(current.Id, out var next)) break;
                if (next.Length != 1) return null;
                current = next[0];
            }
            if (members.Any(r => r.UserChainRootId == root.Id && !visited.Contains(r.Id))) return null;
            // History must be a completed/failed prefix followed by a live owner. Do not revive
            // finished chains or permit terminal rows inside the executable suffix.
            if (result.Any(r => r.Status is ReservationStatus.Completed or ReservationStatus.Failed))
            {
                var live = result.SkipWhile(r => r.Status is ReservationStatus.Completed or ReservationStatus.Failed).ToArray();
                if (live.Length == 0 || !live.Any(r => r.Status is ReservationStatus.Starting or ReservationStatus.Recording)
                    || live.Any(r => r.Status is ReservationStatus.Completed or ReservationStatus.Failed)) return null;
            }
            for (var i=1;i<result.Count;i++)
                if (!ChainReservationEligibilityContract.IsPersistedExplicitRuntimePair(result[i-1],result[i])) return null;
            return result;
        }

        var center=allowExistingComponent ? Component(added) : new List<Reservation>{added};
        if(center is null)return Skip("existing_component_invalid");
        var centerIds=center.Select(r=>r.Id).ToHashSet();
        var components = new Dictionary<int,List<Reservation>>();
        foreach (var row in active.Where(r => !centerIds.Contains(r.Id)
            && ChainReservationEligibilityContract.IsSameService(added,r.NetworkId,r.TransportStreamId,r.ServiceId)))
        {
            var component = Component(row);
            if (component is not null && component.All(r => !centerIds.Contains(r.Id)))
                components.TryAdd(component[0].Id, component);
        }
        bool Adjacent(Reservation left, Reservation right)
            => left.StartTime < right.StartTime && left.EndTime == right.StartTime
            && ChainReservationEligibilityContract.EvaluateTopologyPair(left,right.NetworkId,right.TransportStreamId,
                right.ServiceId,right.StartTime,true).IsEligible;
        var centerCanRebase=center.All(r=>r.Status==ReservationStatus.Scheduled && r.StartTime>now);
        var lefts = components.Values.Where(c => centerCanRebase && Adjacent(c[^1],center[0])).ToArray();
        var rights = components.Values.Where(c => c.All(r => r.Status == ReservationStatus.Scheduled && r.StartTime > now)
            && Adjacent(center[^1],c[0])).ToArray();
        if (lefts.Length > 1 || rights.Length > 1) return Skip("ambiguous_neighbour");
        if (lefts.Length == 0 && rights.Length == 0) return Skip("no_adjacent_component");
        var left = lefts.SingleOrDefault();
        var right = rights.SingleOrDefault();
        if (left is not null && right is not null && left[0].Id == right[0].Id)
            return Skip("existing_component_cannot_be_split");
        var ordered = (left ?? new List<Reservation>()).Concat(center)
            .Concat(right ?? new List<Reservation>()).ToArray();
        if (!ordered.Select(r=>r.Id).SequenceEqual(ordered.OrderBy(r=>r.StartTime).ThenBy(r=>r.EndTime).ThenBy(r=>r.Id).Select(r=>r.Id)))
            return Skip("existing_order_mismatch");
        var edges = new List<AutomaticChainEdge>();
        if (left is not null) edges.Add(new(left[^1].Id,center[0].Id));
        if (right is not null) edges.Add(new(center[^1].Id,right[0].Id));
        return new(true,"eligible",addedId,ordered[0].Id,
            Array.AsReadOnly(ordered.Select(r=>r.Id).ToArray()),Array.AsReadOnly(edges.ToArray()));
    }
}
