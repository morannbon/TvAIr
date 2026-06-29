using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Schedule;

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

public sealed class KeywordMatcher
{
    private static readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    private static readonly object _regexCacheLock = new();

    private readonly EpgStore _epgStore;
    private readonly ReservationStore _rsvStore;
    private readonly ChannelFileLoader _channelLoader;
    private readonly LogRepository _log;

    public KeywordMatcher(EpgStore epgStore, ReservationStore rsvStore, ChannelFileLoader channelLoader, LogRepository log)
    {
        _epgStore = epgStore;
        _rsvStore = rsvStore;
        _channelLoader = channelLoader;
        _log = log;
    }

    public KeywordRulePreviewResult PreviewRule(KeywordRule rule, int limit = 200)
    {
        var result = new KeywordRulePreviewResult();
        var now = DateTime.Now;
        var events = _epgStore.GetByRange(now, now.AddDays(14))
            .Where(e => EpgTitleProjectionGuard.IsSafeForAutoReservation(e, out _))
            .OrderBy(e => e.Start)
            .ThenBy(e => e.ServiceName)
            .ThenBy(e => EpgProjection.Title(e));
        var existing = _rsvStore.GetAll()
            .Where(r => r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
            .Select(r => $"{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}")
            .ToHashSet();

        var compiled = CompileRule(rule);
        if (compiled is null)
            return result;

        var targetCache = new Dictionary<(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, byte Mask), string>();
        var channelTargets = _channelLoader.Load().Targets.ToList();
        var serviceNameMap = BuildServiceNameMap(channelTargets);

        foreach (var ev in events)
        {
            if (!IsMatch(ev, compiled, targetCache))
                continue;

            var evKey = $"{ev.NetworkId}:{ev.TransportStreamId}:{ev.ServiceId}:{ev.EventId}";
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
                    Title = EpgProjection.Title(ev),
                    Description = EpgProjection.ShortText(ev),
                    Genre = ev.Genre,
                    GenreCodes = EpgProjection.GenreCodes(ev),
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
        var events = _epgStore.GetByRange(now, now.AddDays(14))
            .Where(e => EpgTitleProjectionGuard.IsSafeForAutoReservation(e, out _))
            .ToList();
        var compiled = rules
            .Select(r => (Rule: r, Compiled: CompileRule(r)))
            .Where(x => x.Compiled is not null)
            .Select(x => x.Compiled!)
            .ToList();

        var counts = rules.ToDictionary(r => r.Id, _ => 0);
        if (compiled.Count == 0 || events.Count == 0)
            return counts;

        var targetCache = new Dictionary<(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, byte Mask), string>();

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

    public void RunMatching()
    {
        var rules = _rsvStore.GetKeywordRules().Where(r => r.Enabled).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToList();
        if (rules.Count == 0) return;
        _log.Add("KEYWORD_MATCH_BEGIN", "KeywordMatcher", $"自動検索マッチング開始: enabledRules={rules.Count}");

        var now = DateTime.Now;
        _rsvStore.PurgeExpiredKeywordCancelOnce(now);
        var events = _epgStore.GetByRange(now, now.AddDays(14))
            .Where(e => EpgTitleProjectionGuard.IsSafeForAutoReservation(e, out _))
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

        // existing セットに cancelled / completed / failed も含める。
        // これにより以下の問題を防ぐ:
        //   ・録画停止ボタンを押した番組が数秒後に再マッチして再録画開始(無限ループ)
        //   ・ユーザーが解除した番組が意思に反して再び予約リストに現れる
        //   ・既に録画完了/失敗した番組が再度登録される
        // ユーザーの能動的意思(停止・解除・録画完了の履歴)は全て尊重する。
        // ルール自体が削除/無効化された場合は DeleteScheduledByRuleId で別途処理済み。
        var existing = _rsvStore.GetAll()
            .Select(r => $"{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}")
            .ToHashSet();
        var existingSchedule = _rsvStore.GetAll()
            .Select(KeywordScheduleDedupeKey)
            .ToHashSet(StringComparer.Ordinal);

        var compiled = rules
            .Select(CompileRule)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        var totalAdded = 0;
        var suppressedByUserCount = 0;

        var targetCache = new Dictionary<(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, byte Mask), string>();

        foreach (var ev in events)
        {
            var evKey = $"{ev.NetworkId}:{ev.TransportStreamId}:{ev.ServiceId}:{ev.EventId}";
            var scheduleKey = KeywordScheduleDedupeKey(ev);
            if (existing.Contains(evKey) || existingSchedule.Contains(scheduleKey)) continue;

            foreach (var entry in compiled)
            {
                var rule = entry.Rule;

                if (!string.IsNullOrWhiteSpace(rule.ExpiresOn) && DateOnly.TryParse(rule.ExpiresOn, out var exp) && exp < DateOnly.FromDateTime(now))
                    continue;

                if (!IsMatch(ev, entry, targetCache))
                    continue;

                if (_rsvStore.IsKeywordCancelOnceSuppressed(rule.Id, ev))
                {
                    // release_contract: user-suppressed keyword hits are expected persisted state.
                    // Keep the per-program details out of the regular log and emit a summary after matching.
                    suppressedByUserCount++;
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
                    ChannelArgument = chArg ?? "",
                    ServiceName = canonicalServiceName,
                    SourceRuleId = rule.Id,
                    SourceRuleName = string.IsNullOrWhiteSpace(rule.Name) ? TrimRuleLabel(rule.Pattern) : rule.Name
                };

                try
                {
                    var reservationId = _rsvStore.Add(rsv);
                    existing.Add(evKey);
                    existingSchedule.Add(scheduleKey);
                    totalAdded++;
                    _log.Add("RESERVE_ENTRY", "Keyword", $"共通入口要求/確定 source=Keyword id=R{reservationId} title=[{safeTitle}] service=[{ev.ServiceName}] ruleId={rule.Id} rule=[{TrimRuleLabel(rule.Pattern)}] start={ev.Start:MM/dd HH:mm} end={ev.End:MM/dd HH:mm}");
                    _log.Add("KEYWORD_MATCH", safeTitle, $"自動検索予約: id=R{reservationId} 「{safeTitle}」 ({ev.Start:MM/dd HH:mm}) ルール:「{TrimRuleLabel(rule.Pattern)}」");
                }
                catch (Exception ex)
                {
                    _log.Add("KEYWORD_MATCH_ERROR", EpgProjection.Title(ev), $"自動予約追加失敗: {ex.Message}");
                }

                break;
            }
        }

        if (suppressedByUserCount > 0)
            _log.Add("KEYWORD_MATCH", "SuppressedSummary", $"result=OK suppressedByUser={suppressedByUserCount} rule=release_contract");

        if (totalAdded > 0)
            _log.Add("KEYWORD_MATCH_DONE", "KeywordMatcher", $"自動検索予約完了: {totalAdded}件追加");
    }

    private static string KeywordScheduleDedupeKey(EpgEvent ev)
        => $"{ev.NetworkId}:{ev.TransportStreamId}:{ev.ServiceId}:{ev.Start:O}:{ev.End:O}:{NormalizeKeywordDedupeText(EpgProjection.Title(ev))}";

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

    private static string ResolveCanonicalServiceName(EpgEvent ev, IReadOnlyDictionary<(ushort Nid, ushort Tsid, ushort Sid), string> serviceNameMap)
    {
        if (serviceNameMap.TryGetValue((ev.NetworkId, ev.TransportStreamId, ev.ServiceId), out var canonical)
            && !string.IsNullOrWhiteSpace(canonical))
        {
            return canonical.Trim();
        }

        var current = ev.ServiceName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(current) ? $"SID{ev.ServiceId}" : current;
    }

    private static string SafeKeywordEventTitle(EpgEvent ev)
    {
        return EpgProjection.Title(ev);
    }


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
        var targetCache = new Dictionary<(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, byte Mask), string>();
        var groups = new List<KeywordRuleReservationGroup>();
        var groupMap = new Dictionary<string, KeywordRuleReservationGroup>(StringComparer.Ordinal);

        foreach (var reservation in reservations)
        {
            int? ruleId = reservation.SourceRuleId;
            string ruleName = reservation.SourceRuleName ?? "";

            if ((!ruleId.HasValue || string.IsNullOrWhiteSpace(ruleName)) && compiled.Count > 0)
            {
                var ev = _epgStore.GetOne(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventId);
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
            _regexCache[pattern] = created;
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
        var normalized = NormalizeForMatch(text);
        var chars = normalized.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private static HashSet<ushort> ParseSids(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => ushort.TryParse(s, out var v) ? (ushort?)v : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToHashSet();
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

    private static CompiledKeywordRule? CompileRule(KeywordRule rule)
    {
        var includeMatcher = BuildMatcher(rule.Pattern, rule.UseRegex);
        if (includeMatcher is null)
            return null;

        return new CompiledKeywordRule(
            rule,
            includeMatcher,
            BuildMatcher(rule.ExcludePattern, rule.UseRegex),
            ParseSids(rule.TargetServices),
            ParseGenres(rule.TargetGenres),
            ParseDays(rule.TargetDays),
            BuildFieldMask(rule));
    }

    private static bool IsMatch(EpgEvent ev, CompiledKeywordRule entry, Dictionary<(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, byte Mask), string> targetCache)
    {
        var rule = entry.Rule;

        if (!rule.UseAllChannels)
        {
            if (entry.ServiceIds.Count == 0 || !entry.ServiceIds.Contains(ev.ServiceId))
                return false;
        }

        if (entry.Genres.Count > 0 && !MatchGenre(EpgProjection.GenreCodes(ev), entry.Genres))
            return false;

        if (entry.Days.Count > 0 && !entry.Days.Contains((int)ev.Start.DayOfWeek))
            return false;

        if (rule.UseTimeRange && !MatchTimeRange(ev.Start, rule.StartTime, rule.EndTime))
            return false;

        var target = GetOrBuildTarget(ev, entry.FieldMask, targetCache);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (!entry.IncludeMatcher.IsMatch(target))
            return false;

        if (entry.ExcludeMatcher?.IsMatch(target) == true)
            return false;

        return true;
    }

    private static bool MatchGenre(string genreCodes, HashSet<char> targetGenres)
    {
        if (string.IsNullOrWhiteSpace(genreCodes)) return false;
        foreach (var code in genreCodes.Split(','))
        {
            var c = code.Trim();
            if (c.Length > 0 && targetGenres.Contains(char.ToUpperInvariant(c[0])))
                return true;
        }
        return false;
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

    private static string GetOrBuildTarget(EpgEvent ev, byte fieldMask, Dictionary<(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, byte Mask), string> targetCache)
    {
        var key = (ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.EventId, fieldMask);
        if (targetCache.TryGetValue(key, out var cached))
            return cached;

        var parts = new List<string>(4);
        var title = EpgProjection.Title(ev);
        var shortText = EpgProjection.ShortText(ev);
        var extendedText = EpgProjection.ExtendedText(ev);
        if ((fieldMask & 0x1) != 0 && !string.IsNullOrWhiteSpace(title)) parts.Add(title);
        if ((fieldMask & 0x2) != 0 && !string.IsNullOrWhiteSpace(shortText)) parts.Add(shortText);
        if ((fieldMask & 0x4) != 0 && !string.IsNullOrWhiteSpace(extendedText)) parts.Add(extendedText);
        if ((fieldMask & 0x8) != 0)
        {
            var cast = ExtractCast(extendedText);
            if (!string.IsNullOrWhiteSpace(cast)) parts.Add(cast);
        }

        var built = string.Join("\n", parts);
        targetCache[key] = built;
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

    private sealed record CompiledKeywordRule(
        KeywordRule Rule,
        ITextMatcher IncludeMatcher,
        ITextMatcher? ExcludeMatcher,
        HashSet<ushort> ServiceIds,
        HashSet<char> Genres,
        HashSet<int> Days,
        byte FieldMask);

    private interface ITextMatcher
    {
        bool IsMatch(string target);
    }

    private sealed class RegexListMatcher : ITextMatcher
    {
        private readonly IReadOnlyList<Regex> _regexes;

        public RegexListMatcher(IReadOnlyList<Regex> regexes)
        {
            _regexes = regexes;
        }

        public bool IsMatch(string target)
        {
            var normalizedTarget = NormalizeForMatch(target);
            foreach (var rx in _regexes)
            {
                if (rx.IsMatch(target) || rx.IsMatch(normalizedTarget))
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

        public bool IsMatch(string target)
        {
            if (_root is null) return false;
            var normalizedTarget = NormalizeForMatch(target);
            return _root.IsMatch(target, normalizedTarget);
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
        public abstract bool IsMatch(string rawTarget, string normalizedTarget);
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

        public override bool IsMatch(string rawTarget, string normalizedTarget)
        {
            if (rawTarget.Contains(_rawTerm, StringComparison.OrdinalIgnoreCase)
                || normalizedTarget.Contains(_normalizedTerm, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrWhiteSpace(_compactTerm)) return false;
            return CompactForMatch(rawTarget).Contains(_compactTerm, StringComparison.OrdinalIgnoreCase)
                || CompactForMatch(normalizedTarget).Contains(_compactTerm, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class AndNode : LogicalNode
    {
        private readonly IReadOnlyList<LogicalNode> _nodes;

        public AndNode(IReadOnlyList<LogicalNode> nodes)
        {
            _nodes = nodes;
        }

        public override bool IsMatch(string rawTarget, string normalizedTarget)
            => _nodes.All(n => n.IsMatch(rawTarget, normalizedTarget));
    }

    private sealed class OrNode : LogicalNode
    {
        private readonly IReadOnlyList<LogicalNode> _nodes;

        public OrNode(IReadOnlyList<LogicalNode> nodes)
        {
            _nodes = nodes;
        }

        public override bool IsMatch(string rawTarget, string normalizedTarget)
            => _nodes.Any(n => n.IsMatch(rawTarget, normalizedTarget));
    }
}
