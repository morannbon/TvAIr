using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>Only the common allocation core cycle calls this manual rule-option policy.</summary>
internal static class ManualKeywordChainPolicy
{
    internal static void Apply(ReservationStore store, IniSettingsService ini, LogRepository log, DateTime now)
    {
        var rules = store.GetKeywordRules().Where(r => r.Enabled).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToList();
        // MANUAL_KEYWORD_CHAIN_OPTION_CONTRACT:
        // This is not a new chain policy. It automates explicit chain topology construction only
        // for Keyword rules that opted in. A contiguous existing Scheduled sequence is validated
        // and committed atomically inside the common allocation core cycle, so a transient
        // pre-chain conflict cannot split the intended topology halfway through construction.
        // Priority, margins, conflict propagation/restoration, cancellation, Wake, PreRec and
        // recording execution remain owned by the existing chain/allocation contracts.
        if (!ini.ManualChainRecordingEnabled)
        {
            var requested = rules.Count(r => r.AutoChainReservations);
            if (requested > 0)
                log.Add("KEYWORD_AUTO_CHAIN", "ManualKeywordOption",
                    $"result=DISABLED requestedRules={requested} reason=manual_mode_required required=later_program_priority_and_manual_mode action=preserve_rule_preference_no_topology_mutation rule=automatic_keyword_chain_assist_contract");
            return;
        }

        var enabledRuleIds = rules
            .Where(r => r.Enabled && r.AutoChainReservations)
            .Select(r => r.Id)
            .ToHashSet();
        if (enabledRuleIds.Count == 0) return;

        var reservations = store.GetAll()
            .Where(r => r.Source == ReservationSource.Keyword
                && r.SourceRuleId.HasValue
                && enabledRuleIds.Contains(r.SourceRuleId.Value)
                && r.IsEnabled
                && r.Status == ReservationStatus.Scheduled
                && r.EndTime > now)
            .OrderBy(r => r.SourceRuleId)
            .ThenBy(r => r.NetworkId)
            .ThenBy(r => r.TransportStreamId)
            .ThenBy(r => r.ServiceId)
            .ThenBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        var attempted = 0;
        var applied = 0;
        var alreadyLinked = 0;
        var rejected = 0;
        var sequences = 0;
        var samples = new List<string>();

        foreach (var group in reservations.GroupBy(r => new
                 {
                     RuleId = r.SourceRuleId!.Value,
                     r.NetworkId,
                     r.TransportStreamId,
                     r.ServiceId
                 }))
        {
            var ordered = group.OrderBy(r => r.StartTime).ThenBy(r => r.Id).ToList();
            var run = new List<Reservation>();

            void FlushRun()
            {
                if (run.Count < 2)
                {
                    run.Clear();
                    return;
                }

                sequences++;
                var pairCount = run.Count - 1;
                attempted += pairCount;
                var mutation = store.PromoteExistingScheduledUserChainSequence(
                    run.Select(x => x.Id).ToArray(),
                    featureEnabled: true);

                if (mutation.Applied)
                {
                    applied += mutation.AppliedPairs;
                    alreadyLinked += mutation.AlreadyLinkedPairs;
                }
                else
                {
                    rejected += pairCount;
                    if (samples.Count < 12)
                        samples.Add($"rule={group.Key.RuleId}:R{run[0].Id}->R{run[^1].Id}:{mutation.Reason}");
                }

                run.Clear();
            }

            foreach (var reservation in ordered)
            {
                if (run.Count == 0)
                {
                    run.Add(reservation);
                    continue;
                }

                var predecessor = run[^1];
                if (predecessor.EndTime == reservation.StartTime)
                {
                    run.Add(reservation);
                    continue;
                }

                FlushRun();
                run.Add(reservation);
            }

            FlushRun();
        }

        log.Add("KEYWORD_AUTO_CHAIN", "ManualKeywordOption",
            $"result=OK rules={enabledRuleIds.Count} candidates={reservations.Count} sequences={sequences} attempted={attempted} applied={applied} alreadyLinked={alreadyLinked} rejected={rejected} samples=[{string.Join(',', samples)}] source=KeywordRuleId+service_identity+exact_adjacency operation=atomic_existing_scheduled_sequence priorityMutation=none marginMutation=none conflictMutation=none cancellationMutation=none finalConflictOwner=common_allocation rule=automatic_keyword_chain_assist_contract");
    }

}
