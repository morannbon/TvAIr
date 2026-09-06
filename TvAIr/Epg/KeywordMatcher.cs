using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Schedule;
using TvAIr.Epg.Projection;

namespace TvAIr.Epg;

public sealed class KeywordRulePreviewResult
{
    public int CandidateCount { get; set; }
    public int AlreadyReservedCount { get; set; }
    public List<KeywordRulePreviewItem> Items { get; set; } = new();
}

public sealed class KeywordRulePreviewItem
{
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string ServiceName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Genre { get; set; } = "";
    public string GenreCodes { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AlreadyReserved { get; set; }
}

public sealed class KeywordRuleReservationGroup
{
    public int? RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public List<Reservation> Items { get; set; } = new();
}


public sealed class KeywordRuleReconcileResult
{
    public int Preserved { get; set; }
    public int PreservedSourceMissing { get; set; }
    public int Removed { get; set; }
}

public sealed class KeywordMatcher
{
    private const int RegexCacheLimit = 256;
    private static readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> _regexCacheOrder = new();
    private static readonly object _regexCacheLock = new();
    private static readonly object _runMatchingGate = new();

    private readonly IProgramEventSource _programEvents;
    private readonly ReservationStore _rsvStore;
    private readonly ReservationProjectionMetadataStore _projectionMetadataStore;
    private readonly ChannelFileLoader _channelLoader;
    private readonly LogRepository _log;

    public KeywordMatcher(IProgramEventSource programEvents, ReservationStore rsvStore, ReservationProjectionMetadataStore projectionMetadataStore, ChannelFileLoader channelLoader, LogRepository log)
    {
        _programEvents = programEvents;
        _rsvStore = rsvStore;
        _projectionMetadataStore = projectionMetadataStore;
        _channelLoader = channelLoader;
        _log = log;
    }

    public KeywordRulePreviewResult PreviewRule(KeywordRule rule, int limit = 200)
    {
        var result = new KeywordRulePreviewResult();
        var now = DateTime.Now;
        var events = GetFutureSafeEvents(now)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.ServiceName)
            .ThenBy(e => e.Title);
        var existing = _rsvStore.GetAll()
            .Where(r => r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
            .Select(KeywordEventOccurrenceDedupeKey)
            .ToHashSet(StringComparer.Ordinal);

        var compiled = CompileRule(rule);
        if (compiled is null)
            return result;

        var targetCache = new Dictionary<(string EventKey, long StartTicks, long EndTicks, byte Mask), MatchText>();
        var channelTargets = _channelLoader.Load().Targets.ToList();
        var serviceNameMap = BuildServiceNameMap(channelTargets);

        foreach (var ev in events)
        {
            if (!IsMatch(ev, compiled, targetCache))
                continue;

            var evKey = KeywordEventOccurrenceDedupeKey(ev);
            var alreadyReserved = existing.Contains(evKey);
            result.CandidateCount++;
            if (alreadyReserved)
                result.AlreadyReservedCount++;

            if (result.Items.Count < limit)
            {
                result.Items.Add(new KeywordRulePreviewItem
                {
                    NetworkId = ev.NetworkId,
                    TransportStreamId = ev.TransportStreamId,
                    ServiceId = ev.ServiceId,
                    EventId = ev.EventId,
                    ServiceName = ResolveCanonicalServiceName(ev, serviceNameMap),
                    Title = ev.Title,
                    Description = ev.ShortText,
                    Genre = ev.Genre,
                    GenreCodes = ev.GenreCodes,
                    Start = ev.Start,
                    End = ev.End,
                    AlreadyReserved = alreadyReserved
                });
            }
        }

        return result;
    }

    public Dictionary<int, int> GetRuleHitCounts(IEnumerable<KeywordRule> rules)
    {
        var now = DateTime.Now;
        var events = GetFutureSafeEvents(now)
            .ToList();
        var compiled = rules
            .Select(r => (Rule: r, Compiled: CompileRule(r)))
            .Where(x => x.Compiled is not null)
            .Select(x => x.Compiled!)
            .ToList();

        var counts = rules.ToDictionary(r => r.Id, _ => 0);
        if (compiled.Count == 0 || events.Count == 0)
            return counts;

        var targetCache = new Dictionary<(string EventKey, long StartTicks, long EndTicks, byte Mask), MatchText>();

        foreach (var ev in events)
        {
            foreach (var rule in compiled)
            {
                if (IsMatch(ev, rule, targetCache))
                    counts[rule.Rule.Id]++;
            }
        }

        return counts;
    }


    public KeywordRuleReconcileResult ReconcileScheduledReservationsForRule(KeywordRule rule)
    {
        var result = new KeywordRuleReconcileResult();
        var existing = _rsvStore.GetAll()
            .Where(r => r.Source == ReservationSource.Keyword
                        && r.Status == ReservationStatus.Scheduled
                        && r.SourceRuleId == rule.Id)
            .ToList();

        if (existing.Count == 0)
            return result;

        var desiredKeys = new HashSet<string>(StringComparer.Ordinal);
        var availableKeys = new HashSet<string>(StringComparer.Ordinal);
        CompiledKeywordRule? compiledRule = null;
        var ruleExpired = !string.IsNullOrWhiteSpace(rule.ExpiresOn)
                          && DateOnly.TryParse(rule.ExpiresOn, out var expiresOn)
                          && expiresOn < DateOnly.FromDateTime(DateTime.Now);
        if (rule.Enabled && !ruleExpired)
        {
            compiledRule = CompileRule(rule);
            if (compiledRule is not null)
            {
                var now = DateTime.Now;
                var targetCache = new Dictionary<(string EventKey, long StartTicks, long EndTicks, byte Mask), MatchText>();
                foreach (var ev in GetFutureSafeEvents(now))
                {
                    var occurrenceKey = KeywordOccurrenceKey(ev);
                    availableKeys.Add(occurrenceKey);
                    if (IsMatch(ev, compiledRule, targetCache))
                        desiredKeys.Add(occurrenceKey);
                }
            }
        }

        var staleIds = new List<int>();
        foreach (var reservation in existing)
        {
            var occurrenceKey = KeywordOccurrenceKey(reservation);

            // A legacy SID-only station selection that is ambiguous/unavailable in current channel
            // metadata is not a positive mismatch. Preserve the user's already-scheduled row until
            // the legacy selection can be resolved or the rule is explicitly edited/disabled.
            // This prevents station-identity migration uncertainty from becoming a destructive
            // reservation mutation. New automatic additions remain suppressed because IsMatch
            // cannot match an unresolved legacy station selection.
            if (rule.Enabled && !ruleExpired && compiledRule is not null
                && (compiledRule.HasInvalidServiceToken
                    || compiledRule.UnresolvedLegacyServiceIds.Contains(reservation.ServiceId)))
            {
                result.Preserved++;
                result.PreservedSourceMissing++;
                continue;
            }

            if (desiredKeys.Contains(occurrenceKey))
            {
                result.Preserved++;
                continue;
            }

            // release_contract KeywordRuleReconcileSourceAvailability:
            // An enabled rule may be saved while External EPG is still loading, while a source
            // snapshot is being replaced, or while a projected occurrence is temporarily absent.
            // Absence from the current projection is not proof that the user's scheduled
            // reservation no longer matches.  Only remove an enabled-rule reservation when the
            // same occurrence is currently available and can be positively evaluated as no
            // longer matching.  Explicit disable/expiry still removes scheduled rows by intent.
            if (rule.Enabled && !ruleExpired && !availableKeys.Contains(occurrenceKey))
            {
                result.Preserved++;
                result.PreservedSourceMissing++;
                continue;
            }

            staleIds.Add(reservation.Id);
        }

        result.Removed = _rsvStore.DeleteScheduledKeywordReservationsByIds(rule.Id, staleIds);
        _rsvStore.UpdateScheduledKeywordReservationRuleName(
            rule.Id,
            string.IsNullOrWhiteSpace(rule.Name) ? TrimRuleLabel(rule.Pattern) : rule.Name);
        return result;
    }

    public int RunMatching()
    {
        return RunMatchingScoped(null);
    }

    public int RunMatching(IReadOnlyList<EpgEvent> committedEvents)
    {
        ArgumentNullException.ThrowIfNull(committedEvents);
        return RunMatchingScoped(committedEvents);
    }

    private int RunMatchingScoped(IReadOnlyList<EpgEvent>? committedEvents)
    {
        // release_contract KeywordMatcherSingleFlight:
        // EPG imports can complete concurrently (per transport stream and external source).
        // Each completion may request keyword matching.  The existing-reservation snapshot and
        // subsequent ReservationStore.Add operations must therefore be one serialized unit.
        // Without this gate, two matcher runs can both observe the same occurrence as absent and
        // create duplicate automatic reservations before either run refreshes its snapshot.
        // Keep the gate process-wide rather than instance-local so accidental duplicate DI
        // instances cannot reopen the race.  Waiting callers must run after the owner completes;
        // they re-read the authoritative reservation store and converge idempotently.
        lock (_runMatchingGate)
        {
            return RunMatchingSingleFlightCore(committedEvents);
        }
    }

    private int RunMatchingSingleFlightCore(IReadOnlyList<EpgEvent>? committedEvents)
    {
        var rules = _rsvStore.GetKeywordRules().Where(r => r.Enabled).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToList();
        if (rules.Count == 0) return 0;
#if TVAIR_DEVELOPER_DIAGNOSTICS
        var matcherAllocatedAtEntry = GC.GetAllocatedBytesForCurrentThread();
#endif
        _log.Add("KEYWORD_MATCH_BEGIN", "KeywordMatcher", $"自動検索マッチング開始: enabledRules={rules.Count}");

        var now = DateTime.Now;
        _rsvStore.PurgeExpiredKeywordCancelOnce(now);
        // A full RunMatching execution observes one programme-source snapshot. Incremental EPG
        // matching instead projects only the rows from the transport stream that was just
        // committed. Both paths feed the same matching pipeline below; only the input scope differs.
        // The final EpgCompletePostImport pass remains the full authoritative convergence pass.
        var sourceEvents = committedEvents is null
            ? _programEvents.GetAll()
            : _programEvents.ProjectCommittedDbEvents(committedEvents);
        var futureEvents = sourceEvents
            .Where(e => e.End > now)
            .ToList();
        var matchScope = committedEvents is null
            ? "all_future_programmes_x_all_enabled_rules"
            : "committed_ts_future_programmes_x_all_enabled_rules";
        var events = GetFutureSafeEvents(futureEvents, now)
            .ToList();
        // ServiceId単独をキーにすると地上波とBS/CSでServiceIdが衝突した際に
        // ToDictionaryが重複キー例外を投げる(Key: 161等で発生確認済み)。
        // また ChannelArgument は (NetworkId, TSID, ServiceId) で一意に決まる設計上、
        // ServiceId だけで引くのは本来不正確。EpgEventのキー(NetworkId,TSID,ServiceId)と
        // 対応するタプルキーに変更し、同時に重複安全化する(同一3キーで複数あれば初出採用)。
        var channelTargets = _channelLoader.Load().Targets.ToList();
        var chArgMap = channelTargets
            .GroupBy(t => (t.OriginalNetworkId, t.TransportStreamId, t.ServiceId))
            .ToDictionary(g => g.Key, g => g.First().ChannelArgument);
        var serviceNameMap = BuildServiceNameMap(channelTargets);

        // release_contract KeywordMatcherExistingReservationAuthority:
        // Matcher の「既存予約」判定は、現在も予約・録画責務を持つ行と、同一発生回の
        // 完了/失敗証拠だけを正本とする。Cancelled は一律の既存予約証拠にしない。
        //
        // ユーザーが自動検索予約を取消した意思は keyword_cancel_once、録画を手動停止した
        // 意思は manual_stopped_occurrences が同一発生回単位で保持する。Cancelled 行まで
        // ここで無条件抑止すると、ルール再整合や内部処理で取消状態になった未来番組が、
        // 正規の再マッチでも永久に再生成されない。ユーザー意思の抑止責務を専用Storeへ
        // 一本化し、Cancelled 履歴そのものは再生成可否の正本にしない。
        var allReservations = _rsvStore.GetAll();
        var existingReservations = allReservations
            .Where(IsAuthoritativeExistingReservationForKeywordMatch)
            .ToList();
        var existing = existingReservations
            .Where(r => r.EventId != 0)
            .Select(KeywordEventOccurrenceDedupeKey)
            .ToHashSet(StringComparer.Ordinal);
        var existingSchedule = existingReservations
            .Select(KeywordScheduleDedupeKey)
            .ToHashSet(StringComparer.Ordinal);
        var existingByEventIdentity = existingReservations
            .Where(r => r.EventId != 0)
            .GroupBy(KeywordEventOccurrenceDedupeKey)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).ToArray(), StringComparer.Ordinal);
        var existingBySchedule = existingReservations
            .GroupBy(KeywordScheduleDedupeKey)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).ToArray(), StringComparer.Ordinal);

        var compiled = rules
            .Select(CompileRule)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        // keyword_match_rule_pipeline_accounting:
        // The interactive search and automatic reservation paths can both see the same projected
        // event while an enabled rule is silently skipped before EvaluateMatch (for example by an
        // expiry gate), or while a later rule is never reached because an earlier rule matched.
        // Audit every compiled rule independently against the same safe future-event snapshot used
        // by production matching.  This diagnostic pass never creates or suppresses reservations;
        // it only establishes a per-rule conservation trail from input to final condition match,
        // including DB-backed and external-only projection sources.
        var rulePipeline = compiled.ToDictionary(
            x => x.Rule.Id,
            x => new RulePipelineAccounting(
                RuleId: x.Rule.Id,
                Expired: IsRuleExpired(x.Rule, now),
                ExpiresOn: x.Rule.ExpiresOn ?? string.Empty));
        // release_contract KeywordMatcherEvaluationSingleSource:
        // A single matcher run used to evaluate the same full future-programme x enabled-rule
        // Cartesian product three times: rule-pipeline accounting, global conservation crosscheck,
        // and the production first-match pass.  Besides repeating regex work, each pass built its
        // own potentially large search-target strings for the same event/field-mask pairs.  Keep
        // all observable accounting and production semantics unchanged, but make the first full
        // pass authoritative for MatchEvaluation and reuse that immutable result below.
        var evaluations = new MatchEvaluation[events.Count][];
        // FieldMask is a 4-bit value (Title/Outline/Detail/Cast).  Match-target text is only
        // meaningful for the current event, so keep a fixed 16-slot event-local cache instead of
        // a run-wide Dictionary keyed by occurrence strings/timestamps.  Reuse the same small
        // array for every event; the initialized bitset makes old-event entries unreachable.
        var eventTargetCache = new MatchText[16];
        for (var eventIndex = 0; eventIndex < events.Count; eventIndex++)
        {
            var ev = events[eventIndex];
            var titleMatchText = MatchText.Create(ev.Title);
            ushort initializedTargetMask = 0;
            eventTargetCache[0x1] = titleMatchText;
            initializedTargetMask |= 1 << 0x1;
            var eventEvaluations = new MatchEvaluation[compiled.Count];
            evaluations[eventIndex] = eventEvaluations;

            for (var ruleIndex = 0; ruleIndex < compiled.Count; ruleIndex++)
            {
                var candidate = compiled[ruleIndex];
                var accounting = rulePipeline[candidate.Rule.Id];
                accounting.FutureEventsSeen++;
                IncrementSourceCount(accounting.SourceEventsSeen, RulePipelineSource(ev));

                if (accounting.Expired)
                {
                    accounting.ExpiredSkipped++;
                    continue;
                }

                accounting.Evaluated++;
                if (candidate.IncludeMatcher.IsMatch(titleMatchText))
                    accounting.TitleExpressionPositive++;

                var evaluation = EvaluateMatch(ev, candidate, eventTargetCache, ref initializedTargetMask);
                eventEvaluations[ruleIndex] = evaluation;
                if (evaluation.IsMatch)
                {
                    accounting.FinalMatched++;
                    IncrementSourceCount(accounting.SourceFinalMatched, RulePipelineSource(ev));
                }
                else
                {
                    accounting.Rejected[evaluation.Reason] = accounting.Rejected.TryGetValue(evaluation.Reason, out var rejected)
                        ? rejected + 1
                        : 1;
                }
            }
        }
        // Evaluation results are now authoritative for this run.  The fixed event-local target
        // cache contains references only for the last event and is not retained outside this run.
        Array.Clear(eventTargetCache);

        // keyword_match_projection_diagnostics:
        // The programme guide and auto-search share IProgramEventSource, but auto-search applies
        // ProgramEventProjectionGuard before rule evaluation.  A visible event can therefore
        // disappear before IsMatch and will not appear in rule-rejection diagnostics.  Preserve
        // the production guard, but account for title-positive future events rejected at this
        // boundary so every silent loss has an observable reason.
        var projectionRejectedCounts = new Dictionary<(int RuleId, string Reason), int>();
        var projectionRejectedSamples = new List<string>();
        foreach (var ev in futureEvents)
        {
            if (ProgramEventProjectionGuard.IsSafeForAutoReservation(ev, out var guardReason))
                continue;

            var titleMatchText = MatchText.Create(ev.Title);
            foreach (var candidate in compiled)
            {
                if (!candidate.IncludeMatcher.IsMatch(titleMatchText))
                    continue;

                var key = (candidate.Rule.Id, guardReason);
                projectionRejectedCounts[key] = projectionRejectedCounts.TryGetValue(key, out var current)
                    ? current + 1
                    : 1;

                if (projectionRejectedSamples.Count < 24)
                {
                    projectionRejectedSamples.Add(
                        $"rule={candidate.Rule.Id}:reason={SafeProjectedLogValue(guardReason)}:title={SafeProjectedLogValue(ev.Title)}:service={SafeProjectedLogValue(ev.ServiceName)}:nid={ev.NetworkId}:tsid={ev.TransportStreamId}:sid={ev.ServiceId}:eid={ev.EventId}:start={ev.Start:MM/dd HH:mm}:end={ev.End:MM/dd HH:mm}:projectionState={SafeProjectedLogValue(ev.ProjectionState)}:sourceKind={SafeProjectedLogValue(ev.SourceKind)}:sourcePluginId={SafeProjectedLogValue(ev.SourcePluginId)}:sourceEventKey={SafeProjectedLogValue(ev.SourceEventKey)}:dbEventExists={ev.DbEventExists}");
                }
            }
        }

        var totalAdded = 0;
        var ruleMatchedCount = 0;
        var suppressedByExistingEventCount = 0;
        var suppressedByExistingScheduleCount = 0;
        var suppressedByKeywordCancelOnceCount = 0;
        var suppressedByManualStopAutomaticRerecordCount = 0;
        var existingSuppressionSamples = new List<string>();
        var titlePositiveRejectedCounts = new Dictionary<(int RuleId, MatchRejectReason Reason), int>();
        var titlePositiveRejectedSamples = new List<string>();

        // keyword_match_global_crosscheck:
        // Evaluate the complete Cartesian product of the current safe future programme snapshot
        // and every enabled compiled rule. This is programme- and environment-agnostic.
        // Follow production first-match ordering and account every expected automatic reservation
        // as existing, explicitly suppressed, or unexplained.
        var crosscheckPairs = 0L;
        var crosscheckExpected = 0;
        var crosscheckExistingEvent = 0;
        var crosscheckExistingSchedule = 0;
        var crosscheckKeywordCancel = 0;
        var crosscheckManualStop = 0;
        var crosscheckUnaccounted = 0;
        var crosscheckMultipleRuleMatches = 0;
        var crosscheckSourceExpected = new Dictionary<string, int>(StringComparer.Ordinal);
        var crosscheckSourceUnaccounted = new Dictionary<string, int>(StringComparer.Ordinal);
        var crosscheckRuleExpected = new Dictionary<int, int>();
        var crosscheckRuleUnaccounted = new Dictionary<int, int>();
        var crosscheckTitleRejected = new Dictionary<(int RuleId, MatchRejectReason Reason, string Source), int>();
        var crosscheckUnaccountedSamples = new List<string>();
        var crosscheckPending = new List<(ProjectedProgramEvent Event, CompiledKeywordRule FirstRule, string Source)>();
        var crosscheckRejectedSamples = new Dictionary<(int RuleId, MatchRejectReason Reason, string Source), List<string>>();

        for (var eventIndex = 0; eventIndex < events.Count; eventIndex++)
        {
            var ev = events[eventIndex];
            var titleMatchText = MatchText.Create(ev.Title);
            var matchingRules = new List<CompiledKeywordRule>();
            var source = RulePipelineSource(ev);
            for (var ruleIndex = 0; ruleIndex < compiled.Count; ruleIndex++)
            {
                var candidate = compiled[ruleIndex];
                crosscheckPairs++;
                if (IsRuleExpired(candidate.Rule, now))
                    continue;

                var evaluation = evaluations[eventIndex][ruleIndex];
                if (evaluation.IsMatch)
                {
                    matchingRules.Add(candidate);
                    continue;
                }

                if (!candidate.IncludeMatcher.IsMatch(titleMatchText))
                    continue;

                var rejectionKey = (candidate.Rule.Id, evaluation.Reason, source);
                crosscheckTitleRejected[rejectionKey] = crosscheckTitleRejected.TryGetValue(rejectionKey, out var rejectedCount)
                    ? rejectedCount + 1
                    : 1;
                if (!crosscheckRejectedSamples.TryGetValue(rejectionKey, out var rejectedSamples))
                {
                    rejectedSamples = new List<string>();
                    crosscheckRejectedSamples[rejectionKey] = rejectedSamples;
                }
                if (rejectedSamples.Count < 3)
                    rejectedSamples.Add($"title={SafeProjectedLogValue(ev.Title)}:service={SafeProjectedLogValue(ev.ServiceName)}:nid={ev.NetworkId}:tsid={ev.TransportStreamId}:sid={ev.ServiceId}:eid={ev.EventId}:start={ev.Start:MM/dd HH:mm}:end={ev.End:MM/dd HH:mm}:genres={SafeProjectedLogValue(ev.GenreCodes)}");
            }

            if (matchingRules.Count == 0)
                continue;

            crosscheckExpected++;
            if (matchingRules.Count > 1)
                crosscheckMultipleRuleMatches++;

            var firstRule = matchingRules[0].Rule;
            IncrementSourceCount(crosscheckSourceExpected, source);
            crosscheckRuleExpected[firstRule.Id] = crosscheckRuleExpected.TryGetValue(firstRule.Id, out var expectedForRule) ? expectedForRule + 1 : 1;

            var crosscheckEventKey = KeywordEventOccurrenceDedupeKey(ev);
            var crosscheckScheduleKey = KeywordScheduleDedupeKey(ev);
            if (existing.Contains(crosscheckEventKey))
            {
                crosscheckExistingEvent++;
                continue;
            }
            if (existingSchedule.Contains(crosscheckScheduleKey))
            {
                crosscheckExistingSchedule++;
                continue;
            }
            if (_rsvStore.IsKeywordCancelOnceSuppressed(firstRule.Id, ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.Start, ev.End, RawTitleForSuppression(ev)))
            {
                crosscheckKeywordCancel++;
                continue;
            }
            if (_rsvStore.IsManualStoppedOccurrenceSuppressed(ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.EventId, ev.Start, ev.End, RawTitleForSuppression(ev)))
            {
                crosscheckManualStop++;
                continue;
            }

            crosscheckUnaccounted++;
            crosscheckPending.Add((ev, matchingRules[0], source));
            IncrementSourceCount(crosscheckSourceUnaccounted, source);
            crosscheckRuleUnaccounted[firstRule.Id] = crosscheckRuleUnaccounted.TryGetValue(firstRule.Id, out var missingForRule) ? missingForRule + 1 : 1;
            if (crosscheckUnaccountedSamples.Count < 64)
                crosscheckUnaccountedSamples.Add($"rule={firstRule.Id}:ruleName={SafeProjectedLogValue(string.IsNullOrWhiteSpace(firstRule.Name) ? TrimRuleLabel(firstRule.Pattern) : firstRule.Name)}:source={source}:title={SafeProjectedLogValue(ev.Title)}:service={SafeProjectedLogValue(ev.ServiceName)}:nid={ev.NetworkId}:tsid={ev.TransportStreamId}:sid={ev.ServiceId}:eid={ev.EventId}:start={ev.Start:MM/dd HH:mm}:end={ev.End:MM/dd HH:mm}:projectionState={SafeProjectedLogValue(ev.ProjectionState)}:sourcePluginId={SafeProjectedLogValue(ev.SourcePluginId)}:sourceEventKey={SafeProjectedLogValue(ev.SourceEventKey)}:matchedRuleIds={string.Join(",", matchingRules.Select(x => x.Rule.Id))}");
        }

        var crosscheckRuleExpectedSummary = crosscheckRuleExpected.Count == 0 ? "-" : string.Join(",", crosscheckRuleExpected.OrderBy(x => x.Key).Select(x => $"rule{x.Key}={x.Value}"));
        var crosscheckRuleUnaccountedSummary = crosscheckRuleUnaccounted.Count == 0 ? "-" : string.Join(",", crosscheckRuleUnaccounted.OrderBy(x => x.Key).Select(x => $"rule{x.Key}={x.Value}"));
        var crosscheckRejectedSummary = crosscheckTitleRejected.Count == 0
            ? "-"
            : string.Join(",", crosscheckTitleRejected.OrderBy(x => x.Key.RuleId).ThenBy(x => x.Key.Reason).ThenBy(x => x.Key.Source, StringComparer.Ordinal).Select(x => $"rule{x.Key.RuleId}.{x.Key.Reason}.{x.Key.Source}={x.Value}"));
        var crosscheckRejectedSampleSummary = crosscheckRejectedSamples.Count == 0
            ? "-"
            : string.Join("/", crosscheckRejectedSamples.OrderBy(x => x.Key.RuleId).ThenBy(x => x.Key.Reason).ThenBy(x => x.Key.Source, StringComparer.Ordinal).SelectMany(x => x.Value.Select(sample => $"rule={x.Key.RuleId}:reason={x.Key.Reason}:source={x.Key.Source}:{sample}")));

        // This is intentionally a pre-commit view.  A newly discovered occurrence is expected to
        // be absent before the production pass below adds it.  Reporting that normal state as
        // UNACCOUNTED made successful additions look like a conservation failure.  Keep the
        // bounded evidence, but reserve KEYWORD_MATCH_GLOBAL_CROSSCHECK for the post-commit
        // authoritative result emitted after Add/suppression processing completes.
        _log.Add("KEYWORD_MATCH_GLOBAL_PRECOMMIT", "KeywordMatcher",
            $"result={(crosscheckUnaccounted == 0 ? "SETTLED" : "PENDING_ADD")} scope={matchScope} events={events.Count} rules={compiled.Count} pairs={crosscheckPairs} expectedOccurrences={crosscheckExpected} existingEvent={crosscheckExistingEvent} existingSchedule={crosscheckExistingSchedule} keywordCancel={crosscheckKeywordCancel} manualStop={crosscheckManualStop} pendingAdd={crosscheckUnaccounted} sourceExpected=[{FormatSourceCounts(crosscheckSourceExpected)}] rule=keyword_match_global_precommit");
        if (crosscheckUnaccounted > 0)
        {
            _log.Add("KEYWORD_MATCH_GLOBAL_PENDING_ADD", "KeywordMatcher",
                $"result=PENDING_ADD sourcePending=[{FormatSourceCounts(crosscheckSourceUnaccounted)}] ruleExpected=[{crosscheckRuleExpectedSummary}] rulePending=[{crosscheckRuleUnaccountedSummary}] samples=[{string.Join("/", crosscheckUnaccountedSamples)}] rule=keyword_match_global_precommit");
        }


        for (var eventIndex = 0; eventIndex < events.Count; eventIndex++)
        {
            var ev = events[eventIndex];
            var titleMatchText = MatchText.Create(ev.Title);
            CompiledKeywordRule? matchedEntry = null;
            for (var ruleIndex = 0; ruleIndex < compiled.Count; ruleIndex++)
            {
                var candidate = compiled[ruleIndex];
                var candidateRule = candidate.Rule;
                if (!string.IsNullOrWhiteSpace(candidateRule.ExpiresOn)
                    && DateOnly.TryParse(candidateRule.ExpiresOn, out var candidateExp)
                    && candidateExp < DateOnly.FromDateTime(now))
                {
                    continue;
                }

                var evaluation = evaluations[eventIndex][ruleIndex];
                if (evaluation.IsMatch)
                {
                    matchedEntry = candidate;
                    break;
                }

                // keyword_match_diagnostics:
                // A programme whose title itself satisfies the rule expression must never disappear
                // silently behind a secondary condition.  Record the exact rejection axis for a
                // bounded sample, without introducing programme- or rule-specific branches.
                if (candidate.IncludeMatcher.IsMatch(titleMatchText))
                {
                    var countKey = (candidateRule.Id, evaluation.Reason);
                    titlePositiveRejectedCounts[countKey] = titlePositiveRejectedCounts.TryGetValue(countKey, out var current)
                        ? current + 1
                        : 1;

                    if (titlePositiveRejectedSamples.Count < 24)
                    {
                        titlePositiveRejectedSamples.Add(
                            $"rule={candidateRule.Id}:reason={evaluation.Reason}:title={SafeProjectedLogValue(ev.Title)}:service={SafeProjectedLogValue(ev.ServiceName)}:nid={ev.NetworkId}:tsid={ev.TransportStreamId}:sid={ev.ServiceId}:eid={ev.EventId}:start={ev.Start:MM/dd HH:mm}:end={ev.End:MM/dd HH:mm}:genres={SafeProjectedLogValue(ev.GenreCodes)}:days={SafeProjectedLogValue(candidateRule.TargetDays)}:services={SafeProjectedLogValue(candidateRule.TargetServices)}:ruleGenres={SafeProjectedLogValue(candidateRule.TargetGenres)}:time={(candidateRule.UseTimeRange ? $"{candidateRule.StartTime}-{candidateRule.EndTime}" : "all")}:fields={candidate.FieldMask}:exclude={SafeProjectedLogValue(candidateRule.ExcludePattern)}");
                    }
                }
            }

            var evKey = KeywordEventOccurrenceDedupeKey(ev);
            var scheduleKey = KeywordScheduleDedupeKey(ev);
            var eventIdentityMatches = existingByEventIdentity.TryGetValue(evKey, out var identityMatches)
                ? identityMatches
                : Array.Empty<Reservation>();
            var scheduleIdentityMatches = existingBySchedule.TryGetValue(scheduleKey, out var scheduleMatches)
                ? scheduleMatches
                : Array.Empty<Reservation>();

            if (existing.Contains(evKey) || existingSchedule.Contains(scheduleKey))
            {
                RepairMissingProjectionMetadata(
                    ev,
                    eventIdentityMatches.Length > 0 ? eventIdentityMatches : scheduleIdentityMatches);

                if (matchedEntry is not null)
                {
                    ruleMatchedCount++;
                    var eventSuppressed = existing.Contains(evKey);
                    if (eventSuppressed)
                        suppressedByExistingEventCount++;
                    else
                        suppressedByExistingScheduleCount++;

                    if (existingSuppressionSamples.Count < 8)
                    {
                        var matches = eventSuppressed ? eventIdentityMatches : scheduleIdentityMatches;
                        var reservationEvidence = matches.Length == 0
                            ? "none"
                            : string.Join(",", matches.Take(4).Select(r => $"R{r.Id}:{r.Status}:{r.Source}:rule={r.SourceRuleId?.ToString() ?? "-"}"));
                        existingSuppressionSamples.Add(
                            $"rule={matchedEntry.Rule.Id}:title={SafeProjectedLogValue(ev.Title)}:start={ev.Start:MM/dd HH:mm}:eid={ev.EventId}:axis={(eventSuppressed ? "event_occurrence" : "schedule")}:reservations={reservationEvidence}");
                    }
                }
                continue;
            }

            if (matchedEntry is null)
                continue;

            ruleMatchedCount++;
            var rule = matchedEntry.Rule;

            if (_rsvStore.IsKeywordCancelOnceSuppressed(rule.Id, ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.Start, ev.End, RawTitleForSuppression(ev)))
            {
                // release_contract: user-suppressed keyword hits are expected persisted state.
                // Keep the per-program details out of the regular log and emit a summary after matching.
                suppressedByKeywordCancelOnceCount++;
                continue;
            }

            // 手動停止後は自動検索による同一発生回の勝手な再生成だけを抑止する。
            // 番組表・今すぐ録画・検索結果からのユーザー明示予約は抑止キーを残したままSource判定で通す。
            if (_rsvStore.IsManualStoppedOccurrenceSuppressed(ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.EventId, ev.Start, ev.End, RawTitleForSuppression(ev)))
            {
                suppressedByManualStopAutomaticRerecordCount++;
                continue;
            }

                chArgMap.TryGetValue((ev.NetworkId, ev.TransportStreamId, ev.ServiceId), out var chArg);
                var canonicalServiceName = ResolveCanonicalServiceName(ev, serviceNameMap);
                var safeTitle = SafeKeywordEventTitle(ev);
                var rsv = new Reservation
                {
                    NetworkId = ev.NetworkId,
                    TransportStreamId = ev.TransportStreamId,
                    ServiceId = ev.ServiceId,
                    EventId = ev.EventId,
                    Title = safeTitle,
                    StartTime = ev.Start,
                    EndTime = ev.End,
                    Status = ReservationStatus.Scheduled,
                    Source = ReservationSource.Keyword,
                    Intent = ReservationIntent.AutomaticSearch,
                    CreatedThrough = "Host",
                    CreatedByPluginId = string.Empty,
                    ChannelArgument = chArg ?? "",
                    ServiceName = canonicalServiceName,
                    SourceRuleId = rule.Id,
                    SourceRuleName = string.IsNullOrWhiteSpace(rule.Name) ? TrimRuleLabel(rule.Pattern) : rule.Name
                };

                try
                {
                    // BROADCAST_SLOT_EVENT_REBIND_INVARIANT:
                    // 自動検索も番組表/Pluginと同じatomic parent入口を通す。EventId差替えで同一放送枠の
                    // 旧予約が残っていても別ReservationIdを生成せず、既存予約へ収束させる。
                    var addResult = _rsvStore.AddOrGetActiveParent(rsv);
                    var reservationId = addResult.ReservationId;
                    _projectionMetadataStore.UpsertFromProjectedEvent(reservationId, ev);
                    if (string.Equals(ev.ProjectionState, ProjectedEventStates.OverlayOnly, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ev.SourceKind, ProjectedEventSourceKinds.ExternalEpg, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Add("PROJECTED_RESERVATION_METADATA", "Keyword",
                            $"result=SAVED reservation=R{reservationId} projectionState={ev.ProjectionState} sourceKind={ev.SourceKind} sourcePluginId={SafeProjectedLogValue(ev.SourcePluginId)} sourceEventKey={SafeProjectedLogValue(ev.SourceEventKey)} projectedEventId={SafeProjectedLogValue(ev.Key.Value)} dbEventExists={ev.DbEventExists} nid={ev.NetworkId} tsid={ev.TransportStreamId} sid={ev.ServiceId} eid={ev.EventId} rule=projected_reservation_contract");
                    }
                    existing.Add(evKey);
                    existingSchedule.Add(scheduleKey);
                    if (addResult.Added)
                    {
                        totalAdded++;
                        _log.Add("RESERVE_ENTRY", "Keyword", $"共通入口要求/確定 source=Keyword id=R{reservationId} title=[{safeTitle}] service=[{canonicalServiceName}] ruleId={rule.Id} rule=[{TrimRuleLabel(rule.Pattern)}] start={ev.Start:MM/dd HH:mm} end={ev.End:MM/dd HH:mm}");
                        _log.Add("KEYWORD_MATCH", safeTitle, $"自動検索予約: id=R{reservationId} 「{safeTitle}」 ({ev.Start:MM/dd HH:mm}) ルール:「{TrimRuleLabel(rule.Pattern)}」");
                    }
                    else if (addResult.RefreshedBroadcastSlot)
                    {
                        _log.Add("RESERVE_ENTRY", "Keyword", $"共通入口要求/更新 source=Keyword id=R{reservationId} title=[{safeTitle}] service=[{canonicalServiceName}] ruleId={rule.Id} rule=[{TrimRuleLabel(rule.Pattern)}] start={ev.Start:MM/dd HH:mm} end={ev.End:MM/dd HH:mm} newReservationId=False broadcastSlotRefreshed=True rule=release_contract");
                    }
                }
                catch (Exception ex)
                {
                    _log.Add("KEYWORD_MATCH_ERROR", SafeKeywordEventTitle(ev), $"自動予約追加失敗: {ex.Message}");
                }

        }

        // release_contract KeywordMatcherPostCommitConservation:
        // Reclassify only the pre-commit pending occurrences against the authoritative state after
        // this run has completed all Add/suppression decisions.  This distinguishes a normal
        // newly-added hit from a true insertion/suppression accounting failure.
        var postExistingEvent = crosscheckExistingEvent;
        var postExistingSchedule = crosscheckExistingSchedule;
        var postKeywordCancel = crosscheckKeywordCancel;
        var postManualStop = crosscheckManualStop;
        var postUnaccounted = 0;
        var postSourceUnaccounted = new Dictionary<string, int>(StringComparer.Ordinal);
        var postRuleUnaccounted = new Dictionary<int, int>();
        var postUnaccountedSamples = new List<string>();
        foreach (var pending in crosscheckPending)
        {
            var ev = pending.Event;
            var rule = pending.FirstRule.Rule;
            var eventKey = KeywordEventOccurrenceDedupeKey(ev);
            var scheduleKey = KeywordScheduleDedupeKey(ev);
            if (existing.Contains(eventKey))
            {
                postExistingEvent++;
                continue;
            }
            if (existingSchedule.Contains(scheduleKey))
            {
                postExistingSchedule++;
                continue;
            }
            if (_rsvStore.IsKeywordCancelOnceSuppressed(rule.Id, ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.Start, ev.End, RawTitleForSuppression(ev)))
            {
                postKeywordCancel++;
                continue;
            }
            if (_rsvStore.IsManualStoppedOccurrenceSuppressed(ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.EventId, ev.Start, ev.End, RawTitleForSuppression(ev)))
            {
                postManualStop++;
                continue;
            }

            postUnaccounted++;
            IncrementSourceCount(postSourceUnaccounted, pending.Source);
            postRuleUnaccounted[rule.Id] = postRuleUnaccounted.TryGetValue(rule.Id, out var count) ? count + 1 : 1;
            if (postUnaccountedSamples.Count < 64)
                postUnaccountedSamples.Add($"rule={rule.Id}:title={SafeProjectedLogValue(ev.Title)}:service={SafeProjectedLogValue(ev.ServiceName)}:nid={ev.NetworkId}:tsid={ev.TransportStreamId}:sid={ev.ServiceId}:eid={ev.EventId}:start={ev.Start:MM/dd HH:mm}:end={ev.End:MM/dd HH:mm}:source={pending.Source}");
        }

        _log.Add("KEYWORD_MATCH_GLOBAL_CROSSCHECK", "KeywordMatcher",
            $"result={(postUnaccounted == 0 ? "OK" : "UNACCOUNTED")} phase=post_commit scope={matchScope} events={events.Count} rules={compiled.Count} pairs={crosscheckPairs} expectedOccurrences={crosscheckExpected} existingEvent={postExistingEvent} existingSchedule={postExistingSchedule} keywordCancel={postKeywordCancel} manualStop={postManualStop} unaccounted={postUnaccounted} conservation={crosscheckExpected}={postExistingEvent}+{postExistingSchedule}+{postKeywordCancel}+{postManualStop}+{postUnaccounted} sourceExpected=[{FormatSourceCounts(crosscheckSourceExpected)}] addedThisRun={totalAdded} rule=keyword_match_global_crosscheck");
        if (postUnaccounted > 0)
        {
            var postRuleSummary = string.Join(",", postRuleUnaccounted.OrderBy(x => x.Key).Select(x => $"rule{x.Key}={x.Value}"));
            _log.Add("KEYWORD_MATCH_GLOBAL_UNACCOUNTED", "KeywordMatcher",
                $"result=UNACCOUNTED phase=post_commit sourceUnaccounted=[{FormatSourceCounts(postSourceUnaccounted)}] ruleUnaccounted=[{postRuleSummary}] samples=[{string.Join("/", postUnaccountedSamples)}] rule=keyword_match_global_crosscheck");
        }

        if (suppressedByKeywordCancelOnceCount > 0 || suppressedByManualStopAutomaticRerecordCount > 0)
            _log.Add("KEYWORD_MATCH", "SuppressedSummary",
                $"result=OK keywordCancelOnce={suppressedByKeywordCancelOnceCount} manualStopAutomaticRerecord={suppressedByManualStopAutomaticRerecordCount} rule=release_contract");

        var titlePositiveRejectedSummary = titlePositiveRejectedCounts.Count == 0
            ? "-"
            : string.Join(",", titlePositiveRejectedCounts
                .OrderBy(x => x.Key.RuleId)
                .ThenBy(x => x.Key.Reason)
                .Select(x => $"rule{x.Key.RuleId}.{x.Key.Reason}={x.Value}"));

        var actionableDiagnostic = postUnaccounted > 0 || projectionRejectedCounts.Values.Sum() > 0;
        if (actionableDiagnostic)
        {
            _log.Add("KEYWORD_MATCH_REJECTION_DIAGNOSTICS", "KeywordMatcher",
                $"result=DIAGNOSTIC scope=title_expression_positive_but_final_rejected counts=[{titlePositiveRejectedSummary}] samples=[{(titlePositiveRejectedSamples.Count == 0 ? "-" : string.Join("/", titlePositiveRejectedSamples))}] rule=keyword_match_rejection_diagnostics");

            var projectionRejectedSummary = projectionRejectedCounts.Count == 0
                ? "-"
                : string.Join(",", projectionRejectedCounts
                    .OrderBy(x => x.Key.RuleId)
                    .ThenBy(x => x.Key.Reason, StringComparer.Ordinal)
                    .Select(x => $"rule{x.Key.RuleId}.{x.Key.Reason}={x.Value}"));
            _log.Add("KEYWORD_MATCH_PROJECTION_REJECTION_DIAGNOSTICS", "KeywordMatcher",
                $"result=DIAGNOSTIC scope=title_expression_positive_but_projection_guard_rejected counts=[{projectionRejectedSummary}] samples=[{(projectionRejectedSamples.Count == 0 ? "-" : string.Join("/", projectionRejectedSamples))}] rule=keyword_match_projection_diagnostics");

            _log.Add("KEYWORD_MATCH_DIAGNOSTICS", "KeywordMatcher",
                $"result=DIAGNOSTIC eventsRead={events.Count} compiledRules={compiled.Count} ruleMatched={ruleMatchedCount} existingEventSuppressed={suppressedByExistingEventCount} existingScheduleSuppressed={suppressedByExistingScheduleCount} keywordCancelSuppressed={suppressedByKeywordCancelOnceCount} manualStopSuppressed={suppressedByManualStopAutomaticRerecordCount} added={totalAdded} titlePositiveRejected={titlePositiveRejectedCounts.Values.Sum()} existingSample=[{(existingSuppressionSamples.Count == 0 ? "-" : string.Join("/", existingSuppressionSamples))}] rule=keyword_match_diagnostics");

            var rulePipelineSummary = string.Join("/", rulePipeline.Values
                .OrderBy(x => x.RuleId)
                .Select(x =>
                {
                    var rejected = x.Rejected.Count == 0
                        ? "-"
                        : string.Join(",", x.Rejected.OrderBy(y => y.Key).Select(y => $"{y.Key}:{y.Value}"));
                    return $"rule={x.RuleId}:expired={x.Expired}:expiresOn={SafeProjectedLogValue(x.ExpiresOn)}:future={x.FutureEventsSeen}:expiredSkipped={x.ExpiredSkipped}:evaluated={x.Evaluated}:titlePositive={x.TitleExpressionPositive}:finalMatched={x.FinalMatched}:sourceSeen={FormatSourceCounts(x.SourceEventsSeen)}:sourceMatched={FormatSourceCounts(x.SourceFinalMatched)}:rejected={rejected}";
                }));
            _log.Add("KEYWORD_MATCH_RULE_PIPELINE", "KeywordMatcher",
                $"result=DIAGNOSTIC scope=all_compiled_rules_independent_of_first_match rules=[{rulePipelineSummary}] rule=keyword_match_rule_pipeline_accounting");
        }

        _log.Add("KEYWORD_MATCH_DONE", "KeywordMatcher",
            $"自動検索予約完了: {totalAdded}件追加 suppressedExistingEvent={suppressedByExistingEventCount} suppressedExistingSchedule={suppressedByExistingScheduleCount} suppressedKeywordCancelOnce={suppressedByKeywordCancelOnceCount} suppressedManualStop={suppressedByManualStopAutomaticRerecordCount} rule=release_contract");
#if TVAIR_DEVELOPER_DIAGNOSTICS
        var matcherAllocatedAtExit = GC.GetAllocatedBytesForCurrentThread();
        _log.Add("KEYWORD_MATCH_ALLOCATION", "KeywordMatcher",
            $"result=OK allocatedBytes={Math.Max(0, matcherAllocatedAtExit - matcherAllocatedAtEntry)} events={events.Count} rules={compiled.Count} pairs={crosscheckPairs} evaluationSource=single_pass_matrix_event_local_target_cache allocationScope=current_thread_exact rule=keyword_match_allocation_contract");
#endif
        return totalAdded;
    }


    private static bool IsRuleExpired(KeywordRule rule, DateTime now)
        => !string.IsNullOrWhiteSpace(rule.ExpiresOn)
           && DateOnly.TryParse(rule.ExpiresOn, out var expiresOn)
           && expiresOn < DateOnly.FromDateTime(now);

    private static string RulePipelineSource(ProjectedProgramEvent ev)
    {
        if (string.Equals(ev.ProjectionState, ProjectedEventStates.OverlayOnly, StringComparison.OrdinalIgnoreCase))
            return "ExternalOnly";
        if (ev.DbEventExists && string.Equals(ev.SourceKind, ProjectedEventSourceKinds.ExternalEpg, StringComparison.OrdinalIgnoreCase))
            return "DbWithOverlay";
        if (ev.DbEventExists)
            return "Db";
        return string.IsNullOrWhiteSpace(ev.SourceKind) ? "Unknown" : ev.SourceKind;
    }

    private static void IncrementSourceCount(Dictionary<string, int> counts, string source)
        => counts[source] = counts.TryGetValue(source, out var current) ? current + 1 : 1;

    private static string FormatSourceCounts(Dictionary<string, int> counts)
        => counts.Count == 0
            ? "-"
            : string.Join(",", counts.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}:{x.Value}"));

    private sealed class RulePipelineAccounting
    {
        public RulePipelineAccounting(int RuleId, bool Expired, string ExpiresOn)
        {
            this.RuleId = RuleId;
            this.Expired = Expired;
            this.ExpiresOn = ExpiresOn;
        }

        public int RuleId { get; }
        public bool Expired { get; }
        public string ExpiresOn { get; }
        public int FutureEventsSeen { get; set; }
        public int ExpiredSkipped { get; set; }
        public int Evaluated { get; set; }
        public int TitleExpressionPositive { get; set; }
        public int FinalMatched { get; set; }
        public Dictionary<string, int> SourceEventsSeen { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> SourceFinalMatched { get; } = new(StringComparer.Ordinal);
        public Dictionary<MatchRejectReason, int> Rejected { get; } = new();
    }


    private void RepairMissingProjectionMetadata(ProjectedProgramEvent ev, IReadOnlyList<Reservation> matches)
    {
        if (!(string.Equals(ev.ProjectionState, ProjectedEventStates.OverlayOnly, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ev.SourceKind, ProjectedEventSourceKinds.ExternalEpg, StringComparison.OrdinalIgnoreCase)))
            return;

        var scheduled = matches
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .ToArray();
        if (scheduled.Length != 1) return;

        var reservation = scheduled[0];
        if (_projectionMetadataStore.Get(reservation.Id) is not null) return;

        try
        {
            _projectionMetadataStore.UpsertFromProjectedEvent(reservation.Id, ev);
            _log.Add("PROJECTED_RESERVATION_METADATA", "Keyword",
                $"result=REPAIRED reservation=R{reservation.Id} projectionState={ev.ProjectionState} sourceKind={ev.SourceKind} sourcePluginId={SafeProjectedLogValue(ev.SourcePluginId)} sourceEventKey={SafeProjectedLogValue(ev.SourceEventKey)} projectedEventId={SafeProjectedLogValue(ev.Key.Value)} dbEventExists={ev.DbEventExists} nid={ev.NetworkId} tsid={ev.TransportStreamId} sid={ev.ServiceId} eid={ev.EventId} reason=existing_reservation_missing_metadata rule=projected_reservation_contract");
        }
        catch (Exception ex)
        {
            _log.Add("PROJECTED_RESERVATION_METADATA", "Keyword",
                $"result=REPAIR_FAILED reservation=R{reservation.Id} nid={ev.NetworkId} tsid={ev.TransportStreamId} sid={ev.ServiceId} eid={ev.EventId} error={SafeProjectedLogValue(ex.Message)} rule=projected_reservation_contract");
        }
    }


    private IReadOnlyList<ProjectedProgramEvent> GetFutureSafeEvents(DateTime now)
        => GetFutureSafeEvents(_programEvents.GetAll(), now);

    private static IReadOnlyList<ProjectedProgramEvent> GetFutureSafeEvents(
        IEnumerable<ProjectedProgramEvent> source,
        DateTime now)
    {
        // release_contract KeywordMatcherAvailableFutureTimeline:
        // Auto-search must evaluate every future event exposed by the common programme-event
        // source.  A fixed now+14-day horizon is not equivalent to the programme guide: an
        // external source can expose a visible event beyond that boundary, leaving a matching
        // cell silently unreserved.  Filter the common source by actual occurrence lifetime
        // instead of an arbitrary horizon, and keep the same projection safety gate.
        return source
            .Where(e => e.End > now)
            .Where(e => ProgramEventProjectionGuard.IsSafeForAutoReservation(e, out _))
            .OrderBy(e => e.Start)
            .ThenBy(e => e.End)
            .ThenBy(e => e.NetworkId)
            .ThenBy(e => e.TransportStreamId)
            .ThenBy(e => e.ServiceId)
            .ThenBy(e => e.EventId)
            .ToList();
    }

    private static bool IsAuthoritativeExistingReservationForKeywordMatch(Reservation reservation)
        => reservation.Status is ReservationStatus.Scheduled
            or ReservationStatus.Starting
            or ReservationStatus.Recording
            or ReservationStatus.Stopping
            or ReservationStatus.Completed
            or ReservationStatus.Failed;

    private static string KeywordOccurrenceKey(ProjectedProgramEvent ev)
        => ev.EventId != 0
            ? $"event:{KeywordEventOccurrenceDedupeKey(ev)}"
            : $"schedule:{KeywordScheduleDedupeKey(ev)}";

    private static string KeywordOccurrenceKey(Reservation reservation)
        => reservation.EventId != 0
            ? $"event:{KeywordEventOccurrenceDedupeKey(reservation)}"
            : $"schedule:{KeywordScheduleDedupeKey(reservation)}";

    // EventIdはサービス内で再利用されるため、EventId単独では放送発生回を一意に識別できない。
    // 過去の取消・完了履歴で抑止するのは同じ開始・終了時刻の発生回だけとし、
    // 将来の別番組が同じEventIdを再利用した場合まで誤って既存扱いしてはならない。
    private static string KeywordEventOccurrenceDedupeKey(ProjectedProgramEvent ev)
        => $"{ev.NetworkId}:{ev.TransportStreamId}:{ev.ServiceId}:{ev.EventId}:{ev.Start:O}:{ev.End:O}";

    private static string KeywordEventOccurrenceDedupeKey(Reservation reservation)
        => $"{reservation.NetworkId}:{reservation.TransportStreamId}:{reservation.ServiceId}:{reservation.EventId}:{reservation.StartTime:O}:{reservation.EndTime:O}";

    private static string KeywordScheduleDedupeKey(ProjectedProgramEvent ev)
        => $"{ev.NetworkId}:{ev.TransportStreamId}:{ev.ServiceId}:{ev.Start:O}:{ev.End:O}:{NormalizeKeywordDedupeText(ev.Title)}";

    private static string KeywordScheduleDedupeKey(Reservation r)
        => $"{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.StartTime:O}:{r.EndTime:O}:{NormalizeKeywordDedupeText(r.Title)}";

    private static string NormalizeKeywordDedupeText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Trim().Normalize(NormalizationForm.FormKC);

    private static Dictionary<(ushort Nid, ushort Tsid, ushort Sid), string> BuildServiceNameMap(IEnumerable<ChannelTarget> targets)
    {
        return targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => (t.OriginalNetworkId, t.TransportStreamId, t.ServiceId))
            .ToDictionary(g => g.Key, g => g.First().Name.Trim());
    }

    private static string ResolveCanonicalServiceName(ProjectedProgramEvent ev, IReadOnlyDictionary<(ushort Nid, ushort Tsid, ushort Sid), string> serviceNameMap)
    {
        if (serviceNameMap.TryGetValue((ev.NetworkId, ev.TransportStreamId, ev.ServiceId), out var canonical)
            && !string.IsNullOrWhiteSpace(canonical))
        {
            return canonical.Trim();
        }

        var current = ev.ServiceName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(current) ? $"SID{ev.ServiceId}" : current;
    }

    private static string SafeKeywordEventTitle(ProjectedProgramEvent ev)
    {
        return ev.Title;
    }

    private static string RawTitleForSuppression(ProjectedProgramEvent ev)
        => ev.DbEvent?.Title ?? ev.Title;


    public IReadOnlyList<KeywordRuleReservationGroup> GetRuleReservationGroups()
    {
        var reservations = _rsvStore.GetAll()
            .Where(r => r.Source == ReservationSource.Keyword)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.ServiceName)
            .ThenBy(r => r.Title)
            .ToList();
        if (reservations.Count == 0)
            return Array.Empty<KeywordRuleReservationGroup>();

        var rules = _rsvStore.GetKeywordRules()
            .Where(r => r.Enabled)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToList();
        var compiled = rules
            .Select(CompileRule)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
        var targetCache = new Dictionary<(string EventKey, long StartTicks, long EndTicks, byte Mask), MatchText>();
        var groups = new List<KeywordRuleReservationGroup>();
        var groupMap = new Dictionary<string, KeywordRuleReservationGroup>(StringComparer.Ordinal);

        foreach (var reservation in reservations)
        {
            int? ruleId = reservation.SourceRuleId;
            string ruleName = reservation.SourceRuleName ?? "";

            if ((!ruleId.HasValue || string.IsNullOrWhiteSpace(ruleName)) && compiled.Count > 0)
            {
                var ev = _programEvents.GetByEventKey(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventId);
                if (ev is not null)
                {
                    foreach (var entry in compiled)
                    {
                        if (IsMatch(ev, entry, targetCache))
                        {
                            ruleId = entry.Rule.Id;
                            ruleName = string.IsNullOrWhiteSpace(entry.Rule.Name) ? TrimRuleLabel(entry.Rule.Pattern) : entry.Rule.Name;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(ruleName))
                ruleName = "（ルール不明）";

            var key = $"{ruleId?.ToString() ?? "null"}:{ruleName}";
            if (!groupMap.TryGetValue(key, out var group))
            {
                group = new KeywordRuleReservationGroup
                {
                    RuleId = ruleId,
                    RuleName = ruleName
                };
                groupMap[key] = group;
                groups.Add(group);
            }

            group.Items.Add(reservation);
        }

        return groups
            .OrderBy(g => g.RuleName == "（ルール不明）" ? 1 : 0)
            .ThenBy(g => rules.FindIndex(r => r.Id == g.RuleId))
            .ThenBy(g => g.RuleName, StringComparer.CurrentCulture)
            .ToList();
    }

    public static string? ValidateExpression(string? raw, bool useRegex)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (useRegex)
            return ValidateRegexList(raw);

        try
        {
            _ = LogicalKeywordMatcher.Parse(raw);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static ITextMatcher? BuildMatcher(string? raw, bool useRegex)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (useRegex)
        {
            var list = CompileRegexList(raw);
            return list.Count == 0 ? null : new RegexListMatcher(list);
        }

        var expression = LogicalKeywordMatcher.Parse(raw);
        return expression.IsEmpty ? null : expression;
    }

    private static List<Regex> CompileRegexList(string? raw)
    {
        var list = new List<Regex>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            TryAddRegex(list, part);
            var normalized = NormalizePatternForMatch(part);
            if (!string.Equals(part, normalized, StringComparison.Ordinal))
                TryAddRegex(list, normalized);
        }
        return list;
    }

    private static string? ValidateRegexList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            try
            {
                _ = new Regex(part, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
        return null;
    }

    private static void TryAddRegex(List<Regex> list, string pattern)
    {
        try
        {
            list.Add(GetOrAddRegex(pattern));
        }
        catch
        {
        }
    }

    private static Regex GetOrAddRegex(string pattern)
    {
        lock (_regexCacheLock)
        {
            if (_regexCache.TryGetValue(pattern, out var cached))
                return cached;

            var created = new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                TimeSpan.FromSeconds(1));

            while (_regexCache.Count >= RegexCacheLimit && _regexCacheOrder.Count > 0)
            {
                var oldest = _regexCacheOrder.Dequeue();
                _regexCache.Remove(oldest);
            }

            _regexCache[pattern] = created;
            _regexCacheOrder.Enqueue(pattern);
            return created;
        }
    }

    private static string NormalizePatternForMatch(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;
        return pattern.Normalize(NormalizationForm.FormKC);
    }

    private static string NormalizeForMatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Normalize(NormalizationForm.FormKC);
    }

    private static string CompactForMatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return CompactNormalizedForMatch(NormalizeForMatch(text));
    }

    private static string CompactNormalizedForMatch(string normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return string.Empty;
        var chars = normalized.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private KeywordServiceFilter ParseServiceFilter(string? raw)
    {
        var exact = new HashSet<ServiceIdentityContract.Key>();
        var legacyUnique = new HashSet<ServiceIdentityContract.Key>();
        var unresolvedLegacySids = new HashSet<ushort>();
        var hasInvalidToken = false;
        if (string.IsNullOrWhiteSpace(raw))
            return new KeywordServiceFilter(exact, legacyUnique, unresolvedLegacySids, hasInvalidToken);

        IReadOnlyList<ChannelTarget> targets;
        try
        {
            targets = _channelLoader.Load().Targets.ToList();
        }
        catch
        {
            targets = Array.Empty<ChannelTarget>();
        }

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ServiceIdentityContract.TryParseKey(token, out var key))
            {
                exact.Add(key);
                continue;
            }

            // Legacy keyword rules stored SID-only values. Keep them readable, but only resolve
            // a legacy SID when it maps to exactly one current service identity. Ambiguous or
            // temporarily unresolvable legacy selections must not select another station and must
            // also not become authority to delete already-scheduled reservations for that rule.
            if (!ushort.TryParse(token, out var legacySid))
            {
                hasInvalidToken = true;
                continue;
            }

            var matches = targets
                .Where(t => t.ServiceId == legacySid)
                .Select(ServiceIdentityContract.From)
                .Distinct()
                .Take(2)
                .ToList();
            if (matches.Count == 1)
                legacyUnique.Add(matches[0]);
            else
                unresolvedLegacySids.Add(legacySid);
        }

        return new KeywordServiceFilter(exact, legacyUnique, unresolvedLegacySids, hasInvalidToken);
    }

    private static HashSet<char> ParseGenres(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Select(s => char.ToUpperInvariant(s[0]))
            .ToHashSet();
    }

    private static HashSet<int> ParseDays(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : -1)
            .Where(v => v >= 0 && v <= 6)
            .ToHashSet();
    }

    private CompiledKeywordRule? CompileRule(KeywordRule rule)
    {
        var includeMatcher = BuildMatcher(rule.Pattern, rule.UseRegex);
        if (includeMatcher is null)
            return null;

        var serviceFilter = ParseServiceFilter(rule.TargetServices);
        return new CompiledKeywordRule(
            rule,
            includeMatcher,
            BuildMatcher(rule.ExcludePattern, rule.UseRegex),
            serviceFilter.ExactServiceKeys,
            serviceFilter.LegacyUniqueServiceKeys,
            serviceFilter.UnresolvedLegacyServiceIds,
            serviceFilter.HasInvalidToken,
            ParseGenres(rule.TargetGenres),
            ParseDays(rule.TargetDays),
            BuildFieldMask(rule));
    }

    private static bool IsMatch(ProjectedProgramEvent ev, CompiledKeywordRule entry, Dictionary<(string EventKey, long StartTicks, long EndTicks, byte Mask), MatchText> targetCache)
        => EvaluateMatch(ev, entry, targetCache).IsMatch;

    // Interactive/query paths are not a full Cartesian matcher run, so keep their existing
    // request-local occurrence cache.  The automatic full-run path below uses the bounded
    // 16-slot event-local cache.
    private static MatchEvaluation EvaluateMatch(ProjectedProgramEvent ev, CompiledKeywordRule entry, Dictionary<(string EventKey, long StartTicks, long EndTicks, byte Mask), MatchText> targetCache)
    {
        var rule = entry.Rule;

        if (!rule.UseAllChannels)
        {
            var serviceKey = new ServiceIdentityContract.Key(ev.NetworkId, ev.TransportStreamId, ev.ServiceId);
            if ((entry.ServiceKeys.Count == 0 && entry.LegacyUniqueServiceKeys.Count == 0)
                || (!entry.ServiceKeys.Contains(serviceKey) && !entry.LegacyUniqueServiceKeys.Contains(serviceKey)))
            {
                return new MatchEvaluation(false, MatchRejectReason.Service);
            }
        }

        if (entry.Genres.Count > 0 && !MatchGenre(ev.GenreCodes, entry.Genres))
            return new MatchEvaluation(false, MatchRejectReason.Genre);

        if (entry.Days.Count > 0 && !entry.Days.Contains((int)ev.Start.DayOfWeek))
            return new MatchEvaluation(false, MatchRejectReason.Day);

        if (rule.UseTimeRange && !MatchTimeRange(ev.Start, rule.StartTime, rule.EndTime))
            return new MatchEvaluation(false, MatchRejectReason.Time);

        var target = GetOrBuildTarget(ev, entry.FieldMask, targetCache);
        if (string.IsNullOrWhiteSpace(target.Raw))
            return new MatchEvaluation(false, MatchRejectReason.EmptyTarget);

        if (!entry.IncludeMatcher.IsMatch(target))
            return new MatchEvaluation(false, MatchRejectReason.Include);

        if (entry.ExcludeMatcher?.IsMatch(target) == true)
            return new MatchEvaluation(false, MatchRejectReason.Exclude);

        return new MatchEvaluation(true, MatchRejectReason.None);
    }

    private static MatchEvaluation EvaluateMatch(ProjectedProgramEvent ev, CompiledKeywordRule entry, MatchText[] targetCache, ref ushort initializedTargetMask)
    {
        var rule = entry.Rule;

        if (!rule.UseAllChannels)
        {
            var serviceKey = new ServiceIdentityContract.Key(ev.NetworkId, ev.TransportStreamId, ev.ServiceId);
            if ((entry.ServiceKeys.Count == 0 && entry.LegacyUniqueServiceKeys.Count == 0)
                || (!entry.ServiceKeys.Contains(serviceKey) && !entry.LegacyUniqueServiceKeys.Contains(serviceKey)))
            {
                return new MatchEvaluation(false, MatchRejectReason.Service);
            }
        }

        if (entry.Genres.Count > 0 && !MatchGenre(ev.GenreCodes, entry.Genres))
            return new MatchEvaluation(false, MatchRejectReason.Genre);

        if (entry.Days.Count > 0 && !entry.Days.Contains((int)ev.Start.DayOfWeek))
            return new MatchEvaluation(false, MatchRejectReason.Day);

        if (rule.UseTimeRange && !MatchTimeRange(ev.Start, rule.StartTime, rule.EndTime))
            return new MatchEvaluation(false, MatchRejectReason.Time);

        var target = GetOrBuildTarget(ev, entry.FieldMask, targetCache, ref initializedTargetMask);
        if (string.IsNullOrWhiteSpace(target.Raw))
            return new MatchEvaluation(false, MatchRejectReason.EmptyTarget);

        if (!entry.IncludeMatcher.IsMatch(target))
            return new MatchEvaluation(false, MatchRejectReason.Include);

        if (entry.ExcludeMatcher?.IsMatch(target) == true)
            return new MatchEvaluation(false, MatchRejectReason.Exclude);

        return new MatchEvaluation(true, MatchRejectReason.None);
    }

    private static bool MatchGenre(string genreCodes, HashSet<char> targetGenres)
    {
        if (string.IsNullOrWhiteSpace(genreCodes)) return false;

        // Preserve the existing comma-separated/trim/first-character contract without allocating
        // a string[] and one trimmed string for every event x rule evaluation.
        var remaining = genreCodes.AsSpan();
        while (true)
        {
            var comma = remaining.IndexOf(',');
            var segment = (comma >= 0 ? remaining[..comma] : remaining).Trim();
            if (!segment.IsEmpty && targetGenres.Contains(char.ToUpperInvariant(segment[0])))
                return true;
            if (comma < 0)
                return false;
            remaining = remaining[(comma + 1)..];
        }
    }

    private static bool MatchTimeRange(DateTime start, string? from, string? to)
    {
        if (!TimeOnly.TryParse(from, out var startTime)) return true;
        if (!TimeOnly.TryParse(to, out var endTime)) return true;
        var t = TimeOnly.FromDateTime(start);
        if (startTime <= endTime)
            return t >= startTime && t <= endTime;
        return t >= startTime || t <= endTime;
    }

    private static byte BuildFieldMask(KeywordRule rule)
    {
        byte mask = 0;
        if (rule.SearchTitle) mask |= 0x1;
        if (rule.SearchOutline) mask |= 0x2;
        if (rule.SearchDetail) mask |= 0x4;
        if (rule.SearchCast) mask |= 0x8;
        return mask;
    }

    private static MatchText GetOrBuildTarget(ProjectedProgramEvent ev, byte fieldMask, Dictionary<(string EventKey, long StartTicks, long EndTicks, byte Mask), MatchText> targetCache)
    {
        var key = (ev.Key.Value, ev.Start.Ticks, ev.End.Ticks, fieldMask);
        if (targetCache.TryGetValue(key, out var cached))
            return cached;

        var parts = new List<string>(4);
        var title = ev.Title;
        var shortText = ev.ShortText;
        var extendedText = ev.ExtendedText;
        if ((fieldMask & 0x1) != 0 && !string.IsNullOrWhiteSpace(title)) parts.Add(title);
        if ((fieldMask & 0x2) != 0 && !string.IsNullOrWhiteSpace(shortText)) parts.Add(shortText);
        if ((fieldMask & 0x4) != 0 && !string.IsNullOrWhiteSpace(extendedText)) parts.Add(extendedText);
        if ((fieldMask & 0x8) != 0)
        {
            var cast = ExtractCast(extendedText);
            if (!string.IsNullOrWhiteSpace(cast)) parts.Add(cast);
        }

        var built = MatchText.Create(string.Join("\n", parts));
        targetCache[key] = built;
        return built;
    }

    private static MatchText GetOrBuildTarget(ProjectedProgramEvent ev, byte fieldMask, MatchText[] targetCache, ref ushort initializedTargetMask)
    {
        // FieldMask is 0..15.  The cache lifetime is one event, so occurrence identity is implicit
        // and cannot cross-contaminate another occurrence.  This removes run-wide tuple hashing
        // while keeping the exact same target-text construction semantics.
        var slot = fieldMask & 0x0F;
        var slotBit = (ushort)(1 << slot);
        if ((initializedTargetMask & slotBit) != 0)
            return targetCache[slot];

        var parts = new List<string>(4);
        var title = ev.Title;
        var shortText = ev.ShortText;
        var extendedText = ev.ExtendedText;
        if ((fieldMask & 0x1) != 0 && !string.IsNullOrWhiteSpace(title)) parts.Add(title);
        if ((fieldMask & 0x2) != 0 && !string.IsNullOrWhiteSpace(shortText)) parts.Add(shortText);
        if ((fieldMask & 0x4) != 0 && !string.IsNullOrWhiteSpace(extendedText)) parts.Add(extendedText);
        if ((fieldMask & 0x8) != 0)
        {
            var cast = ExtractCast(extendedText);
            if (!string.IsNullOrWhiteSpace(cast)) parts.Add(cast);
        }

        var built = MatchText.Create(string.Join("\n", parts));
        targetCache[slot] = built;
        initializedTargetMask |= slotBit;
        return built;
    }

    private static string ExtractCast(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.Where(l => l.Contains("出演") || l.Contains("キャスト") || l.Contains("声:")));
    }

    private static string TrimRuleLabel(string raw)
    {
        raw ??= "";
        return raw.Length <= 60 ? raw : raw[..60] + "…";
    }

    private static string TrimForLog(string? value, int max)
    {
        var text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string SafeProjectedLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var v = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Replace("|", "/").Replace("\"", "'").Trim();
        return v.Length <= 180 ? v : v[..180] + "…";
    }

    private enum MatchRejectReason
    {
        None,
        Service,
        Genre,
        Day,
        Time,
        EmptyTarget,
        Include,
        Exclude
    }

    private readonly record struct MatchEvaluation(bool IsMatch, MatchRejectReason Reason);

    private sealed record KeywordServiceFilter(
        HashSet<ServiceIdentityContract.Key> ExactServiceKeys,
        HashSet<ServiceIdentityContract.Key> LegacyUniqueServiceKeys,
        HashSet<ushort> UnresolvedLegacyServiceIds,
        bool HasInvalidToken);

    private sealed record CompiledKeywordRule(
        KeywordRule Rule,
        ITextMatcher IncludeMatcher,
        ITextMatcher? ExcludeMatcher,
        HashSet<ServiceIdentityContract.Key> ServiceKeys,
        HashSet<ServiceIdentityContract.Key> LegacyUniqueServiceKeys,
        HashSet<ushort> UnresolvedLegacyServiceIds,
        bool HasInvalidServiceToken,
        HashSet<char> Genres,
        HashSet<int> Days,
        byte FieldMask);

    // release_contract KeywordMatcherTargetTextSingleSource:
    // Normalize and whitespace-compact each event/field-mask target once per matcher run.
    // Matchers consume the immutable forms without rebuilding equivalent strings per rule/term.
    private readonly record struct MatchText(string Raw, string Normalized, string Compact)
    {
        public static MatchText Create(string? raw)
        {
            var source = raw ?? string.Empty;
            var normalized = NormalizeForMatch(source);
            return new MatchText(source, normalized, CompactNormalizedForMatch(normalized));
        }
    }

    private interface ITextMatcher
    {
        bool IsMatch(MatchText target);
    }

    private sealed class RegexListMatcher : ITextMatcher
    {
        private readonly IReadOnlyList<Regex> _regexes;

        public RegexListMatcher(IReadOnlyList<Regex> regexes)
        {
            _regexes = regexes;
        }

        public bool IsMatch(MatchText target)
        {
            foreach (var rx in _regexes)
            {
                if (rx.IsMatch(target.Raw) || rx.IsMatch(target.Normalized))
                    return true;
            }
            return false;
        }
    }

    private sealed class LogicalKeywordMatcher : ITextMatcher
    {
        private readonly LogicalNode? _root;
        public bool IsEmpty => _root is null;

        private LogicalKeywordMatcher(LogicalNode? root)
        {
            _root = root;
        }

        public static LogicalKeywordMatcher Parse(string raw)
        {
            var parser = new LogicalExpressionParser(raw);
            return new LogicalKeywordMatcher(parser.Parse());
        }

        public bool IsMatch(MatchText target)
        {
            if (_root is null) return false;
            return _root.IsMatch(target.Raw, target.Normalized, target.Compact);
        }
    }

    private sealed class LogicalExpressionParser
    {
        private readonly List<LogicalToken> _tokens;
        private int _index;

        public LogicalExpressionParser(string raw)
        {
            _tokens = Tokenize(raw);
        }

        public LogicalNode? Parse()
        {
            if (_tokens.Count == 0)
                return null;

            var node = ParseOr();
            if (!IsAtEnd)
                throw new InvalidOperationException($"キーワード式の末尾付近が不正です: {Current.Display}");
            return node;
        }

        private LogicalNode ParseOr()
        {
            var nodes = new List<LogicalNode> { ParseAnd() };
            while (Match(LogicalTokenType.Or))
            {
                nodes.Add(ParseAnd());
            }
            return nodes.Count == 1 ? nodes[0] : new OrNode(nodes);
        }

        private LogicalNode ParseAnd()
        {
            var nodes = new List<LogicalNode> { ParsePrimary() };
            while (Match(LogicalTokenType.And))
            {
                nodes.Add(ParsePrimary());
            }
            return nodes.Count == 1 ? nodes[0] : new AndNode(nodes);
        }

        private LogicalNode ParsePrimary()
        {
            if (Match(LogicalTokenType.LParen))
            {
                var node = ParseOr();
                if (!Match(LogicalTokenType.RParen))
                    throw new InvalidOperationException("キーワード式の閉じカッコ ')' が不足しています。");
                return node;
            }

            if (Match(LogicalTokenType.Term, out var token))
                return new TermNode(token!.Text);

            if (Peek(LogicalTokenType.RParen))
                throw new InvalidOperationException("キーワード式に空の () は使用できません。");

            throw new InvalidOperationException("キーワード式の書式が不正です。");
        }

        private bool IsAtEnd => _index >= _tokens.Count;
        private LogicalToken Current => _tokens[_index];

        private bool Peek(LogicalTokenType type) => !IsAtEnd && Current.Type == type;

        private bool Match(LogicalTokenType type)
        {
            if (Peek(type))
            {
                _index++;
                return true;
            }
            return false;
        }

        private bool Match(LogicalTokenType type, out LogicalToken? token)
        {
            if (Peek(type))
            {
                token = Current;
                _index++;
                return true;
            }
            token = null;
            return false;
        }

        private static List<LogicalToken> Tokenize(string raw)
        {
            var s = NormalizeLogicalInput(raw);
            var tokens = new List<LogicalToken>();
            var sb = new StringBuilder();

            void Flush()
            {
                var term = sb.ToString().Trim();
                sb.Clear();
                if (!string.IsNullOrWhiteSpace(term))
                    tokens.Add(new LogicalToken(LogicalTokenType.Term, term));
            }

            foreach (var ch in s)
            {
                switch (ch)
                {
                    case ';':
                        Flush();
                        tokens.Add(new LogicalToken(LogicalTokenType.And, ";"));
                        break;
                    case '|':
                        Flush();
                        tokens.Add(new LogicalToken(LogicalTokenType.Or, "|"));
                        break;
                    case '(':
                        Flush();
                        tokens.Add(new LogicalToken(LogicalTokenType.LParen, "("));
                        break;
                    case ')':
                        Flush();
                        tokens.Add(new LogicalToken(LogicalTokenType.RParen, ")"));
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }
            Flush();

            if (tokens.Count == 0)
                return tokens;

            LogicalToken? prev = null;
            foreach (var token in tokens)
            {
                if (prev is not null)
                {
                    var invalidPair = (prev.Type is LogicalTokenType.And or LogicalTokenType.Or or LogicalTokenType.LParen) &&
                                      (token.Type is LogicalTokenType.And or LogicalTokenType.Or or LogicalTokenType.RParen);
                    if (invalidPair)
                        throw new InvalidOperationException($"キーワード式の並びが不正です: {prev.Display}{token.Display}");

                    if ((prev.Type is LogicalTokenType.Term or LogicalTokenType.RParen) &&
                        (token.Type is LogicalTokenType.Term or LogicalTokenType.LParen))
                        throw new InvalidOperationException("キーワード式では語と語の間に ';' または '|' を入れてください。");
                }
                prev = token;
            }

            if (tokens[0].Type is LogicalTokenType.And or LogicalTokenType.Or)
                throw new InvalidOperationException("キーワード式を ';' や '|' から始めることはできません。");
            if (tokens[^1].Type is LogicalTokenType.And or LogicalTokenType.Or or LogicalTokenType.LParen)
                throw new InvalidOperationException("キーワード式の末尾が不正です。");

            return tokens;
        }

        private static string NormalizeLogicalInput(string raw)
        {
            return (raw ?? string.Empty)
                .Normalize(NormalizationForm.FormKC)
                .Replace('（', '(')
                .Replace('）', ')')
                .Replace('｜', '|');
        }
    }

    private sealed record LogicalToken(LogicalTokenType Type, string Text)
    {
        public string Display => Text;
    }

    private enum LogicalTokenType
    {
        Term,
        And,
        Or,
        LParen,
        RParen
    }

    private abstract class LogicalNode
    {
        public abstract bool IsMatch(string rawTarget, string normalizedTarget, string compactTarget);
    }

    private sealed class TermNode : LogicalNode
    {
        private readonly string _rawTerm;
        private readonly string _normalizedTerm;
        private readonly string _compactTerm;

        public TermNode(string term)
        {
            _rawTerm = term;
            _normalizedTerm = NormalizeForMatch(term);
            _compactTerm = CompactForMatch(term);
        }

        public override bool IsMatch(string rawTarget, string normalizedTarget, string compactTarget)
        {
            if (rawTarget.Contains(_rawTerm, StringComparison.OrdinalIgnoreCase)
                || normalizedTarget.Contains(_normalizedTerm, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrWhiteSpace(_compactTerm)) return false;
            return compactTarget.Contains(_compactTerm, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class AndNode : LogicalNode
    {
        private readonly IReadOnlyList<LogicalNode> _nodes;

        public AndNode(IReadOnlyList<LogicalNode> nodes)
        {
            _nodes = nodes;
        }

        public override bool IsMatch(string rawTarget, string normalizedTarget, string compactTarget)
            => _nodes.All(n => n.IsMatch(rawTarget, normalizedTarget, compactTarget));
    }

    private sealed class OrNode : LogicalNode
    {
        private readonly IReadOnlyList<LogicalNode> _nodes;

        public OrNode(IReadOnlyList<LogicalNode> nodes)
        {
            _nodes = nodes;
        }

        public override bool IsMatch(string rawTarget, string normalizedTarget, string compactTarget)
            => _nodes.Any(n => n.IsMatch(rawTarget, normalizedTarget, compactTarget));
    }
}
