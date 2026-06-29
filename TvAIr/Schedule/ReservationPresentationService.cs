using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Tuner;

namespace TvAIr.Schedule;

public sealed class ReservationPresentationService
{
    private readonly ReservationStore _store;
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly TunerPool _tunerPool;
    private readonly LogRepository _log;
    private readonly ChannelFileLoader _channelLoader;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly EpgStore _epgStore;

    public ReservationPresentationService(
        ReservationStore store,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        TunerPool tunerPool,
        LogRepository log,
        ChannelFileLoader channelLoader,
        ReservationAllocationRouteService allocationRoute,
        EpgStore epgStore)
    {
        _store = store;
        _ini = ini;
        _tunerProfiles = tunerProfiles;
        _tunerPool = tunerPool;
        _log = log;
        _channelLoader = channelLoader;
        _allocationRoute = allocationRoute;
        _epgStore = epgStore;
    }

    public IReadOnlyList<ReservationPresentationItem> GetReservations()
    {
        try
        {
            var reservations = _store.GetAll();
            reservations = ProjectSystemEpgVisibleRows(reservations);
            return BuildPresentationItems(reservations);
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "GetReservations", $"予約一覧整形失敗: {ex}");
            return Array.Empty<ReservationPresentationItem>();
        }
    }

    public IReadOnlyList<KeywordRuleReservationPresentationGroup> GetKeywordRuleReservations()
    {
        try
        {
            var items = GetReservations()
                .Where(x => x.Source == "keyword")
                .Where(IsListVisibleReservation)
                .OrderBy(x => x.StartTime)
                .ThenBy(x => x.ServiceName)
                .ThenBy(x => x.Title)
                .ToList();

            if (items.Count == 0)
                return Array.Empty<KeywordRuleReservationPresentationGroup>();

            var ruleOrderMap = _store.GetKeywordRules()
                .Select((rule, index) => new { rule.Id, Order = rule.SortOrder > 0 ? rule.SortOrder : index + 1 })
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First().Order);

            var groups = items
                .GroupBy(x => new { x.SourceRuleId, RuleName = string.IsNullOrWhiteSpace(x.SourceRuleName) ? "（ルール不明）" : x.SourceRuleName })
                .OrderBy(g => g.Key.SourceRuleId.HasValue && ruleOrderMap.TryGetValue(g.Key.SourceRuleId.Value, out var order) ? order : int.MaxValue)
                .ThenBy(g => g.Key.RuleName, StringComparer.Ordinal)
                .Select(g => new KeywordRuleReservationPresentationGroup
                {
                    RuleId = g.Key.SourceRuleId,
                    RuleName = g.Key.RuleName,
                    Count = g.Count(),
                    Items = g.ToList()
                })
                .ToList();

            return groups;
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "GetKeywordRuleReservations", $"自動検索予約一覧整形失敗: {ex}");
            return Array.Empty<KeywordRuleReservationPresentationGroup>();
        }
    }


    private static bool IsListVisibleReservation(ReservationPresentationItem item)
    {
        var status = (item.Status ?? string.Empty).Trim().ToLowerInvariant();
        return status == "scheduled" || status == "recording";
    }

    private IReadOnlyList<Reservation> ProjectSystemEpgVisibleRows(IReadOnlyList<Reservation> reservations)
    {
        var userRows = reservations
            .Where(r => r.Source != ReservationSource.Epg)
            .ToList();

        var projection = BuildSystemEpgVisibleProjection(reservations);
        if (projection.Rows.Count == 0)
            return userRows;

        userRows.AddRange(projection.Rows
            .OrderBy(r => r.StartTime)
            .ThenBy(r => ResolveRecordingTunerOrder(projection.RecordingTuners, r.TunerName))
            .ThenBy(r => r.Id));
        return userRows;
    }

    private SystemEpgVisibleProjection BuildSystemEpgVisibleProjection(IReadOnlyList<Reservation> reservations)
    {
        var recordingTuners = GetRecordingTuners();
        if (recordingTuners.Count == 0)
            return new SystemEpgVisibleProjection(recordingTuners, Array.Empty<Reservation>());

        var now = DateTime.Now;
        var epgRowsByTuner = reservations
            .Where(r => IsActiveFutureSystemEpgRow(r, now))
            .Where(r => !string.IsNullOrWhiteSpace(r.TunerName))
            .GroupBy(r => r.TunerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var visibleRows = new List<Reservation>(recordingTuners.Count);
        var dailyWindows = _ini.EpgEnabled
            ? BuildDailyEpgWindows(recordingTuners, now)
            : new Dictionary<string, DailyEpgWindow>(StringComparer.OrdinalIgnoreCase);

        var virtualIndex = 0;
        foreach (var tuner in recordingTuners)
        {
            epgRowsByTuner.TryGetValue(tuner.Name, out var tunerRows);
            tunerRows ??= new List<Reservation>();

            var preRecord = SelectVisiblePreRecordEpg(tunerRows);
            if (preRecord is not null)
            {
                visibleRows.Add(preRecord);
                continue;
            }

            if (!_ini.EpgEnabled)
                continue;

            var daily = SelectVisibleDailyEpg(tunerRows)
                ?? BuildVirtualDailyEpgRow(tuner, dailyWindows, now, --virtualIndex);
            visibleRows.Add(daily);
        }

        return new SystemEpgVisibleProjection(recordingTuners, visibleRows);
    }

    private IReadOnlyList<TunerProfile> GetRecordingTuners()
        => _tunerProfiles
            .Where(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static bool IsActiveFutureSystemEpgRow(Reservation r, DateTime now)
        => r.Source == ReservationSource.Epg
        && (r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
        && r.EndTime > now;

    private static Reservation? SelectVisiblePreRecordEpg(IEnumerable<Reservation> rows)
        => rows
            .Where(IsPreRecordEpgRow)
            .OrderBy(r => r.Status == ReservationStatus.Recording ? 0 : 1)
            .ThenBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .FirstOrDefault();

    private static Reservation? SelectVisibleDailyEpg(IEnumerable<Reservation> rows)
        => rows
            .Where(IsDailyEpgRow)
            .OrderBy(r => r.Status == ReservationStatus.Recording ? 0 : 1)
            .ThenBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .FirstOrDefault();

    private static bool IsPreRecordEpgRow(Reservation r)
        => string.Equals(r.SourceRuleName, "PreRecEpg", StringComparison.OrdinalIgnoreCase)
        || r.Title.StartsWith("EPG確認", StringComparison.OrdinalIgnoreCase)
        || r.Title.StartsWith("録画前EPG確認", StringComparison.OrdinalIgnoreCase);

    private static bool IsDailyEpgRow(Reservation r)
        => !IsPreRecordEpgRow(r)
        && r.Title.StartsWith("EPG取得", StringComparison.OrdinalIgnoreCase);

    private static int ResolveRecordingTunerOrder(IReadOnlyList<TunerProfile> tuners, string? tunerName)
    {
        if (string.IsNullOrWhiteSpace(tunerName)) return int.MaxValue;
        for (var i = 0; i < tuners.Count; i++)
        {
            if (string.Equals(tuners[i].Name, tunerName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return int.MaxValue;
    }

    private Dictionary<string, DailyEpgWindow> BuildDailyEpgWindows(IReadOnlyList<TunerProfile> recordingTuners, DateTime now)
    {
        var start = CalcNextDailyEpgStart(now).AddMinutes(-1);
        var result = new Dictionary<string, DailyEpgWindow>(StringComparer.OrdinalIgnoreCase);
        if (recordingTuners.Count == 0)
            return result;

        var tunersByGroup = BuildDailyEpgTunerGroups(recordingTuners);
        var durationByGroup = EstimateDailyEpgDurationByGroup(tunersByGroup);

        foreach (var (group, tuners) in tunersByGroup)
        {
            if (tuners.Count == 0) continue;
            var duration = durationByGroup.TryGetValue(group, out var value) ? value : TimeSpan.FromSeconds(EpgDurationPolicy.CreateSchedulePlan(_ini.EpgDepth, 0).RecDurationSeconds);
            var end = CeilTo30Min(start.Add(duration));
            foreach (var tuner in tuners)
                result[tuner.Name] = new DailyEpgWindow(start, end);
        }

        return result;
    }

    private static Dictionary<string, List<TunerProfile>> BuildDailyEpgTunerGroups(IReadOnlyList<TunerProfile> recordingTuners)
    {
        var result = new Dictionary<string, List<TunerProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tuner in recordingTuners)
        {
            var group = string.Equals(tuner.Group, "HYBRID", StringComparison.OrdinalIgnoreCase)
                ? "BSCS"
                : (string.IsNullOrWhiteSpace(tuner.Group) ? "GR" : tuner.Group.ToUpperInvariant());
            if (!result.TryGetValue(group, out var list))
            {
                list = new List<TunerProfile>();
                result[group] = list;
            }
            list.Add(tuner);
        }
        return result;
    }

    private Dictionary<string, TimeSpan> EstimateDailyEpgDurationByGroup(Dictionary<string, List<TunerProfile>> tunersByGroup)
    {
        var durationByGroup = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var basePlan = EpgDurationPolicy.CreateSchedulePlan(_ini.EpgDepth, 0);
        var fallbackSecondsByGroup = tunersByGroup.ToDictionary(kv => kv.Key, kv => basePlan.RecDurationSeconds * Math.Max(1, kv.Value.Count), StringComparer.OrdinalIgnoreCase);

        try
        {
            var targets = _channelLoader.Load().Targets;
            var totalSeconds = targets
                .GroupBy(t => new
                {
                    Group = t.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase) ? "BSCS" : "GR",
                    t.OriginalNetworkId,
                    t.TransportStreamId
                })
                .GroupBy(g => g.Key.Group)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(ts => EpgDurationPolicy.CreateSchedulePlan(_ini.EpgDepth, 0, ts.Count()).RecDurationSeconds),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var (group, tuners) in tunersByGroup)
            {
                var total = totalSeconds.TryGetValue(group, out var seconds) ? seconds : fallbackSecondsByGroup[group];
                var parallel = Math.Max(1, tuners.Count);
                durationByGroup[group] = TimeSpan.FromSeconds((int)Math.Ceiling((double)total / parallel));
            }
        }
        catch
        {
            foreach (var (group, tuners) in tunersByGroup)
            {
                var parallel = Math.Max(1, tuners.Count);
                durationByGroup[group] = TimeSpan.FromSeconds((int)Math.Ceiling((double)fallbackSecondsByGroup[group] / parallel));
            }
        }

        return durationByGroup;
    }

    private Reservation BuildVirtualDailyEpgRow(TunerProfile tuner, IReadOnlyDictionary<string, DailyEpgWindow> dailyWindows, DateTime now, int virtualId)
    {
        var window = dailyWindows.TryGetValue(tuner.Name, out var value)
            ? value
            : new DailyEpgWindow(CalcNextDailyEpgStart(now).AddMinutes(-1), CalcNextDailyEpgStart(now).AddMinutes(29));

        return new Reservation
        {
            Id = -100000 + virtualId,
            Title = $"EPG取得（{tuner.Name}）",
            StartTime = window.Start,
            EndTime = window.End,
            Status = ReservationStatus.Scheduled,
            Source = ReservationSource.Epg,
            IsEnabled = true,
            IsConflicted = false,
            TunerName = tuner.Name,
            ServiceName = string.Empty,
            SourceRuleName = string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private DateTime CalcNextDailyEpgStart(DateTime now)
    {
        var hour = Math.Clamp(_ini.EpgHour, 0, 23);
        var minute = Math.Clamp(_ini.EpgMinute, 0, 59);
        var baseTime = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, now.Kind);
        if (baseTime <= now) baseTime = baseTime.AddDays(1);
        return baseTime;
    }

    private static DateTime CeilTo30Min(DateTime value)
    {
        var tick = TimeSpan.FromMinutes(30).Ticks;
        return new DateTime(((value.Ticks + tick - 1) / tick) * tick, value.Kind);
    }

    private sealed record SystemEpgVisibleProjection(
        IReadOnlyList<TunerProfile> RecordingTuners,
        IReadOnlyList<Reservation> Rows);

    private readonly record struct DailyEpgWindow(DateTime Start, DateTime End);

    private IReadOnlyList<ReservationPresentationItem> BuildPresentationItems(IReadOnlyList<Reservation> reservations)
    {
        var chainMap = BuildChainMap();
        var conflictPartnerMap = BuildConflictPartnerMap(reservations);
        var items = new List<ReservationPresentationItem>(reservations.Count);

        foreach (var r in reservations)
        {
            try
            {
                chainMap.TryGetValue(r.Id, out var chainInfo);
                conflictPartnerMap.TryGetValue(r.Id, out var partners);
                partners ??= Array.Empty<string>();

                var sourceLabel = GetSourceLabel(r);
                var displayStatusLabel = BuildDisplayStatusLabel(r, sourceLabel);
                var conflictPrefix = BuildConflictPrefix(r, partners);
                var title = ResolveReservationTitle(r);
                var resolvedServiceName = ResolveServiceName(r);
                var genreCodes = ResolveGenreCodes(r);
                var origin = ReservationOriginClassifier.Classify(r);

                items.Add(new ReservationPresentationItem
                {
                    Id = r.Id,
                    NetworkId = r.NetworkId,
                    TransportStreamId = r.TransportStreamId,
                    ServiceId = r.ServiceId,
                    EventId = r.EventId,
                    Title = title,
                    TitleDisplay = string.IsNullOrEmpty(conflictPrefix) ? title : $"{conflictPrefix}{title}",
                    ConflictPrefix = conflictPrefix,
                    ConflictTitles = partners,
                    StartTime = r.StartTime,
                    EndTime = r.EndTime,
                    Status = r.Status.ToString().ToLowerInvariant(),
                    Source = r.Source.ToString().ToLowerInvariant(),
                    SourceLabel = sourceLabel,
                    DisplayStatusLabel = displayStatusLabel,
                    ChannelArgument = r.ChannelArgument ?? string.Empty,
                    IsConflicted = r.IsConflicted,
                    IsEnabled = r.IsEnabled,
                    TunerName = ResolvePresentationTunerName(r),
                    ActualTunerName = r.ActualTunerName ?? string.Empty,
                    HasRecordingStarted = r.RecordingStartedAt.HasValue,
                    RecordingStartedAt = r.RecordingStartedAt,
                    RecordingFinishedAt = r.RecordingFinishedAt,
                    ServiceName = resolvedServiceName,
                    GenreCodes = genreCodes,
                    ScheduledStartTime = r.ScheduledStartTime,
                    SourceRuleId = r.SourceRuleId,
                    SourceRuleName = r.SourceRuleName ?? string.Empty,
                    ReservationOrigin = origin.Origin.ToString(),
                    ReservationIdentity = origin.Identity.ToString(),
                    IsProgramGuideMissing = origin.IsProgramGuideMissingProgramRule,
                    IsResolvedEventReservation = origin.IsResolvedEventReservation,
                    ChainRole = chainInfo?.Role ?? string.Empty,
                    ChainLabel = chainInfo?.Label ?? string.Empty,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt
                });
            }
            catch (Exception ex)
            {
                _log.Add("ReservationAPI", "BuildPresentationItem", $"予約ID={r.Id} 整形失敗: {ex.Message}");
            }
        }

        return items;
    }

    private static string ResolvePresentationTunerName(Reservation reservation)
    {
        if (reservation.Source == ReservationSource.Epg)
            return string.Empty;

        if (!reservation.IsEnabled || reservation.IsConflicted)
            return string.Empty;

        if (reservation.Status == ReservationStatus.Recording)
            return !string.IsNullOrWhiteSpace(reservation.ActualTunerName)
                ? reservation.ActualTunerName
                : reservation.TunerName ?? string.Empty;

        if (reservation.Status == ReservationStatus.Scheduled)
            return reservation.TunerName ?? string.Empty;

        return string.Empty;
    }

    private string ResolveGenreCodes(Reservation reservation)
    {
        try
        {
            // release_contract ARIB raw-field: 予約行のジャンルも、同一EIDで取得済みの EPG 値だけを使う。
            // 同一時刻帯の既存イベントを探して色を補う経路は、EPG取得結果の見え方を曖昧にするため撤去。
            var ev = _epgStore.GetOne(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventId);
            if (!string.IsNullOrWhiteSpace(ev?.GenreCodes))
                return ev.GenreCodes;

            var byRange = ResolveGenreCodesFromRangeFallback(reservation);
            return byRange ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "ResolveGenreCodes", $"予約ID={reservation.Id} genre解決失敗: {ex.Message}");
            return string.Empty;
        }
    }

    private string? ResolveGenreCodesFromRangeFallback(Reservation reservation)
    {
        // release_contract:
        // Some user-facing reservations, especially program/search-generated rows, can carry
        // incomplete triplet/EID metadata while still showing the correct service/title/time.
        // Do not let that leak into the UI as uncolored rows.  The fallback remains bounded
        // to the reservation time window and prefers exact service identity before service name.
        try
        {
            var candidates = _epgStore.GetByRange(reservation.StartTime.AddHours(-3), reservation.EndTime.AddHours(3))
                .Where(ev => ev is not null)
                .Where(ev => !string.IsNullOrWhiteSpace(ev.GenreCodes))
                .Where(ev => ev.End > reservation.StartTime && ev.Start < reservation.EndTime)
                .Select(ev => new
                {
                    Event = ev,
                    ServiceScore = ScoreReservationGenreServiceMatch(reservation, ev),
                    TitleScore = ScoreReservationGenreTitleMatch(reservation.Title, ev.Title),
                    OverlapSeconds = Math.Max(0, (Math.Min(ev.End.Ticks, reservation.EndTime.Ticks) - Math.Max(ev.Start.Ticks, reservation.StartTime.Ticks)) / TimeSpan.TicksPerSecond)
                })
                .Where(x => x.ServiceScore > 0 || x.TitleScore > 0)
                .OrderByDescending(x => x.ServiceScore)
                .ThenByDescending(x => x.TitleScore)
                .ThenByDescending(x => x.OverlapSeconds)
                .ThenBy(x => Math.Abs((x.Event.Start - reservation.StartTime).TotalSeconds))
                .ThenByDescending(x => x.Event.UpdatedAt)
                .Take(1)
                .ToList();

            return candidates.FirstOrDefault()?.Event.GenreCodes;
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "ResolveGenreCodesRangeFallback", $"予約ID={reservation.Id} genre範囲参照失敗: {ex.Message}");
            return null;
        }
    }

    private static int ScoreReservationGenreServiceMatch(Reservation reservation, EpgEvent ev)
    {
        var score = 0;
        if (reservation.ServiceId != 0 && ev.ServiceId == reservation.ServiceId) score += 100;
        if (reservation.NetworkId != 0 && ev.NetworkId == reservation.NetworkId) score += 20;
        if (reservation.TransportStreamId != 0 && ev.TransportStreamId == reservation.TransportStreamId) score += 20;

        var rService = NormalizeGenreMatchText(reservation.ServiceName);
        var eService = NormalizeGenreMatchText(ev.ServiceName);
        if (!string.IsNullOrEmpty(rService) && !string.IsNullOrEmpty(eService))
        {
            if (string.Equals(rService, eService, StringComparison.Ordinal)) score += 70;
            else if (rService.Contains(eService, StringComparison.Ordinal) || eService.Contains(rService, StringComparison.Ordinal)) score += 35;
        }

        return score;
    }

    private static int ScoreReservationGenreTitleMatch(string? reservationTitle, string? eventTitle)
    {
        var rTitle = NormalizeGenreMatchText(NormalizeTitle(reservationTitle));
        var eTitle = NormalizeGenreMatchText(eventTitle);
        if (string.IsNullOrEmpty(rTitle) || string.IsNullOrEmpty(eTitle)) return 0;
        if (string.Equals(rTitle, eTitle, StringComparison.Ordinal)) return 100;
        if (rTitle.Contains(eTitle, StringComparison.Ordinal) || eTitle.Contains(rTitle, StringComparison.Ordinal)) return 60;

        var prefixLen = Math.Min(Math.Min(rTitle.Length, eTitle.Length), 12);
        return prefixLen >= 6 && string.Equals(rTitle[..prefixLen], eTitle[..prefixLen], StringComparison.Ordinal) ? 30 : 0;
    }

    private static string NormalizeGenreMatchText(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;
        s = s.Normalize(System.Text.NormalizationForm.FormKC);
        var chars = s.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private Dictionary<int, ReservationChainInfo> BuildChainMap()
    {
        var predecessors = _store.GetChainPredecessors();
        var result = new Dictionary<int, ReservationChainInfo>();
        foreach (var pair in predecessors)
        {
            var successorId = pair.Key;
            var predecessorId = pair.Value;

            if (!result.TryGetValue(predecessorId, out var prevInfo))
            {
                result[predecessorId] = new ReservationChainInfo("head", "チェーン");
            }
            else if (prevInfo.Role == "tail")
            {
                result[predecessorId] = new ReservationChainInfo("mid", "チェーン");
            }

            if (!result.TryGetValue(successorId, out var nextInfo))
            {
                result[successorId] = new ReservationChainInfo("tail", "チェーン");
            }
            else if (nextInfo.Role == "head")
            {
                result[successorId] = new ReservationChainInfo("mid", "チェーン");
            }
        }
        return result;
    }

    private Dictionary<int, IReadOnlyList<string>> BuildConflictPartnerMap(IReadOnlyList<Reservation> reservations)
    {
        var result = new Dictionary<int, IReadOnlyList<string>>();
        var preMargin = TimeSpan.FromSeconds(_ini.PreStartMarginSeconds);
        var postMargin = TimeSpan.FromSeconds(_ini.PostEndMarginSeconds);
        var predecessors = _ini.PseudoContinuousRecording ? _store.GetChainPredecessors() : new Dictionary<int, int>();
        var chainRootById = new Dictionary<int, int>();
        if (_ini.PseudoContinuousRecording)
        {
            var byIdForChain = reservations.ToDictionary(r => r.Id);
            foreach (var r in reservations)
            {
                var root = r.UserChainRootId ?? r.UserChainPreviousId ?? (predecessors.ContainsValue(r.Id) ? r.Id : 0);
                if (root > 0)
                    chainRootById[r.Id] = root;
            }
        }

        bool IsSameChainGroup(Reservation a, Reservation b)
        {
            if (!_ini.PseudoContinuousRecording) return false;
            if (!chainRootById.TryGetValue(a.Id, out var ar)) return false;
            if (!chainRootById.TryGetValue(b.Id, out var br)) return false;
            return ar == br;
        }

        static string ResolveGroup(Reservation r)
        {
            if (!string.IsNullOrWhiteSpace(r.ChannelArgument))
                return r.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase) ? "BSCS" : "GR";
            return r.NetworkId == 4 ? "BSCS" : "GR";
        }

        static bool IsContinuousPair(Reservation a, Reservation b)
            => a.NetworkId == b.NetworkId
            && a.TransportStreamId == b.TransportStreamId
            && a.ServiceId == b.ServiceId
            && a.EndTime.ToString("yyyy-MM-ddTHH:mm:ss") == b.StartTime.ToString("yyyy-MM-ddTHH:mm:ss");

        DateTime OccupyStart(Reservation r) => r.StartTime - preMargin;
        DateTime OccupyEnd(Reservation r) => r.EndTime + postMargin;

        var active = reservations
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => r.IsEnabled)
            .Where(r => r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
            .ToList();

        foreach (var r in active.Where(x => x.IsConflicted))
        {
            var oStart = OccupyStart(r);
            var oEnd = OccupyEnd(r);
            var sameGroup = active
                .Where(x => x.Id != r.Id)
                .Where(x => string.Equals(ResolveGroup(x), ResolveGroup(r), StringComparison.OrdinalIgnoreCase))
                .Where(x => !IsSameChainGroup(x, r))
                .Where(x => !(predecessors.ContainsKey(x.Id)))
                .Where(x => !IsContinuousPair(x, r))
                .Where(x => OccupyStart(x) < oEnd && OccupyEnd(x) > oStart)
                .OrderBy(x => x.IsConflicted)
                .ThenBy(x => x.StartTime)
                .ThenBy(x => x.Id)
                .Select(x => string.IsNullOrWhiteSpace(x.Title) ? "（無題）" : x.Title)
                .Select(NormalizeTitle)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();

            result[r.Id] = sameGroup;
        }

        return result;
    }



    private string ResolveReservationTitle(Reservation r)
    {
        var title = NormalizeTitle(r.Title);
        if (!string.IsNullOrWhiteSpace(title)) return title;

        // release_contract: Existing reservations may have been persisted while the legacy title
        // field was empty.  The reservation list is a presentation surface, so recover the
        // visible title from the same DB raw-descriptor projection used by the program guide.
        if (r.EventId != 0)
        {
            try
            {
                var ev = _epgStore.GetOne(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);
                if (ev is not null)
                {
                    var projected = NormalizeTitle(EpgProjection.Title(ev));
                    if (!string.IsNullOrWhiteSpace(projected)) return projected;
                }
            }
            catch
            {
                // Presentation fallback below keeps raw Reservation.Title untouched.
            }
        }

        // release_contract ReservationTitleDisplayContract:
        // raw Reservation.Title may intentionally remain empty for blank-title EPG events.
        // Reservation list / keyword reservation list are user-facing surfaces, so display
        // the same unavailable label as ProgramGuide without writing it back to storage.
        return ReservationTitleDisplayContract.ForUser(r.Title);
    }

    private static string NormalizeTitle(string? title)
    {
        var value = (title ?? string.Empty).TrimStart();

        while (true)
        {
            var updated = value;
            updated = System.Text.RegularExpressions.Regex.Replace(
                updated,
                @"^(?:【競合(?::\s*[^】]+)?】|\[競合(?::\s*[^\]]+)?\]|［競合(?::\s*[^］]+)?］)\s*",
                string.Empty);

            if (updated == value)
                break;

            value = updated.TrimStart();
        }

        return value;
    }

    private string ResolveServiceName(Reservation reservation)
    {
        var current = reservation.ServiceName?.Trim() ?? string.Empty;

        try
        {
            var loaded = _channelLoader.Load();
            var targets = loaded.Targets;

            ChannelTarget? target = targets.FirstOrDefault(t =>
                t.OriginalNetworkId == reservation.NetworkId
                && t.TransportStreamId == reservation.TransportStreamId
                && t.ServiceId == reservation.ServiceId);

            target ??= targets.FirstOrDefault(t =>
                t.OriginalNetworkId == reservation.NetworkId
                && t.ServiceId == reservation.ServiceId);

            target ??= targets.FirstOrDefault(t => t.ServiceId == reservation.ServiceId);

            if (target is not null && !string.IsNullOrWhiteSpace(target.Name))
            {
                var canonical = target.Name.Trim();
                if (ShouldPreferCanonicalServiceName(current, canonical))
                    return canonical;
            }
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "ResolveServiceName", $"予約ID={reservation.Id} 局名解決失敗: {ex.Message}");
        }

        return current;
    }

    private static bool ShouldPreferCanonicalServiceName(string current, string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
            return false;
        if (string.IsNullOrWhiteSpace(current))
            return true;
        if (current.StartsWith("SID", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(current.Trim(), canonical.Trim(), StringComparison.Ordinal))
            return false;

        // release_contract: Reservation.ServiceName may be polluted by EPG text/title fragments
        // such as 「詳しくはご案内」. A triplet match from ch2 is the authoritative
        // channel label for reservation list display.
        return true;
    }

    private static string GetSourceLabel(Reservation reservation)
    {
        // release_contract: 予約もと列は「予約が発生した起点」という同一メタ属性だけを表示する。
        // source名をそのまま表示名へ丸めない。
        //
        //   番組表   : 番組表直接予約 / キーワード検索結果からのユーザー手動予約 / 今すぐ録画
        //   自動検索 : KeywordRule により自動生成された予約
        //   プログラム: ProgramRule / プラグイン等のプログラム由来予約
        //   システム : EPG取得 / 録画前EPG確認など内部生成
        //
        // 注意: ReservationSource.Keyword は「キーワード検索画面」ではなく、
        // KeywordMatcher が作る自動検索予約の内部sourceとして使われている。
        return reservation.Source switch
        {
            ReservationSource.Manual => "番組表",
            ReservationSource.KeywordSearch => "番組表",
            ReservationSource.Immediate => "番組表",
            ReservationSource.Keyword => "自動検索",
            ReservationSource.Program => "プログラム",
            ReservationSource.Epg => "システム",
            _ => "不明"
        };
    }

    private static string BuildDisplayStatusLabel(Reservation reservation, string sourceLabel)
    {
        return sourceLabel;
    }

    private static string BuildConflictPrefix(Reservation reservation, IReadOnlyList<string> partners)
    {
        return string.Empty;
    }
}

public sealed class ReservationPresentationItem
{
    public int Id { get; set; }
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string Title { get; set; } = "";
    public string TitleDisplay { get; set; } = "";
    public string ConflictPrefix { get; set; } = "";
    public IReadOnlyList<string> ConflictTitles { get; set; } = Array.Empty<string>();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Status { get; set; } = "scheduled";
    public string Source { get; set; } = "manual";
    public string SourceLabel { get; set; } = "";
    public string DisplayStatusLabel { get; set; } = "";
    public string ChannelArgument { get; set; } = "";
    public bool IsConflicted { get; set; }
    public bool IsEnabled { get; set; }
    public string TunerName { get; set; } = "";
    public string ActualTunerName { get; set; } = "";
    public bool HasRecordingStarted { get; set; }
    public DateTime? RecordingStartedAt { get; set; }
    public DateTime? RecordingFinishedAt { get; set; }
    public string ServiceName { get; set; } = "";
    public string GenreCodes { get; set; } = "";
    public DateTime? ScheduledStartTime { get; set; }
    public int? SourceRuleId { get; set; }
    public string SourceRuleName { get; set; } = "";
    public string ReservationOrigin { get; set; } = "Unknown";
    public string ReservationIdentity { get; set; } = "Unknown";
    public bool IsProgramGuideMissing { get; set; }
    public bool IsResolvedEventReservation { get; set; }
    public string ChainRole { get; set; } = "";
    public string ChainLabel { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class KeywordRuleReservationPresentationGroup
{
    public int? RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public int Count { get; set; }
    public IReadOnlyList<ReservationPresentationItem> Items { get; set; } = Array.Empty<ReservationPresentationItem>();
}

public sealed record ReservationChainInfo(string Role, string Label);
