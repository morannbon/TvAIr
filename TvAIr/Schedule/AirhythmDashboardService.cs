using System.Globalization;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Plugin;
using TvAIrPlugin;

namespace TvAIr.Schedule;

public sealed class AirhythmDashboardService
{
    private readonly ReservationStore reservationStore;
    private readonly EpgStore epgStore;
    private readonly AirhythmProfileService profileService;
    private readonly AirhythmBackupService backupService;
    private readonly PluginRegistry pluginRegistry;

    public AirhythmDashboardService(
        ReservationStore reservationStore,
        EpgStore epgStore,
        AirhythmProfileService profileService,
        AirhythmBackupService backupService,
        PluginRegistry pluginRegistry)
    {
        this.reservationStore = reservationStore;
        this.epgStore = epgStore;
        this.profileService = profileService;
        this.backupService = backupService;
        this.pluginRegistry = pluginRegistry;
    }

    public AirhythmDashboardResponse Build()
    {
        var profile = profileService.Get();
        var now = DateTime.Now;
        var reservations = reservationStore.GetAll();
        var activeReservations = reservations
            .Where(r => r.IsEnabled && r.Status != ReservationStatus.Cancelled && r.Status != ReservationStatus.Failed)
            .OrderBy(r => r.StartTime)
            .ToList();
        var futureReservations = activeReservations.Where(r => r.EndTime >= now).ToList();
        var recentReservations = reservations
            .Where(r => r.CreatedAt >= now.AddDays(-180) || r.StartTime >= now.AddDays(-180))
            .OrderByDescending(r => r.StartTime)
            .ToList();

        var epgWindow = epgStore.GetByRange(now.AddDays(-180), now.AddDays(14)).ToList();
        var epgByKey = epgWindow.ToDictionary(ToKey, x => x);
        var preference = BuildPreference(recentReservations, epgByKey);
        var chainItems = BuildChainItems(futureReservations);
        var recommendations = BuildRecommendations(now, epgWindow, futureReservations, preference, 8);
        var watchItems = BuildWatchItems(now, reservations, epgByKey, preference, 5);

        var pluginInfo = BuildPluginInfo(profile);
        var pluginAnalyses = profile.IsEnabled
            ? BuildPluginAnalyses(profile, reservations, epgWindow)
            : new List<AirhythmPluginAnalysisItem>();

        var summary = new AirhythmSummary
        {
            ReservedCount = futureReservations.Count,
            ChainCount = chainItems.Count(x => x.Status == "ok"),
            ConflictCount = futureReservations.Count(x => x.IsConflicted),
            RecommendationCount = recommendations.Count,
            FavoriteGenre = preference.TopGenreLabel,
            FavoriteHourBand = preference.TopHourLabel,
            Tags = BuildTags(preference, futureReservations)
        };

        return new AirhythmDashboardResponse
        {
            Profile = profile,
            Greeting = $"{AirhythmProfileService.FormatDisplayUserNickname(profile.UserNickname)}、{profile.AssistantNickname}が最近の録画傾向をまとめました。",
            Comment = BuildComment(profile, summary, recommendations, chainItems, watchItems),
            Summary = summary,
            ChainItems = chainItems,
            Recommendations = recommendations,
            WatchItems = watchItems,
            Backup = backupService.GetInfo(),
            Plugins = pluginInfo,
            PluginAnalyses = pluginAnalyses
        };
    }

    private AirhythmPluginInfo BuildPluginInfo(AirhythmProfileSettings profile)
    {
        var analysis = pluginRegistry.GetAnalysisPlugins();
        var viewers = pluginRegistry.GetViewerPlugins();
        return new AirhythmPluginInfo
        {
            IsEnabled = profile.IsEnabled,
            AnalysisPluginCount = analysis.Count,
            ViewerPluginCount = viewers.Count,
            LoadedPlugins = pluginRegistry.GetAll()
                .Select(p => $"{p.Name} v{p.Version}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private List<AirhythmPluginAnalysisItem> BuildPluginAnalyses(
        AirhythmProfileSettings profile,
        IReadOnlyList<Reservation> reservations,
        IReadOnlyList<EpgEvent> programs)
    {
        var context = new AnalysisContext
        {
            IsClosedNetwork = true,
            UserNickname = profile.UserNickname,
            AssistantNickname = profile.AssistantNickname,
            Reservations = reservations
                .Where(r => r.Source != ReservationSource.Epg)
                .OrderByDescending(r => r.StartTime)
                .Take(200)
                .Select(r => new AnalysisReservationInfo
                {
                    Id = r.Id,
                    Title = r.Title,
                    ServiceName = r.ServiceName,
                    Source = r.Source.ToString(),
                    IsEnabled = r.IsEnabled,
                    IsConflicted = r.IsConflicted,
                    StartTime = r.StartTime,
                    EndTime = r.EndTime
                })
                .ToList(),
            Programs = programs
                .Where(p => p.Start >= DateTime.Now.AddHours(-12))
                .OrderBy(p => p.Start)
                .Take(400)
                .Select(p => new AnalysisProgramInfo
                {
                    Title = p.Title,
                    ServiceName = p.ServiceName,
                    Genre = p.Genre,
                    Description = p.Description,
                    StartTime = p.Start,
                    EndTime = p.End
                })
                .ToList()
        };

        var results = new List<AirhythmPluginAnalysisItem>();
        foreach (var plugin in pluginRegistry.GetAnalysisPlugins())
        {
            try
            {
                var result = plugin.Analyze(context) ?? new AnalysisResult();
                results.Add(new AirhythmPluginAnalysisItem
                {
                    PluginName = string.IsNullOrWhiteSpace(result.PluginName) ? plugin.Name : result.PluginName,
                    PluginVersion = string.IsNullOrWhiteSpace(result.PluginVersion) ? plugin.Version : result.PluginVersion,
                    Score = Math.Clamp(result.Score, 0, 100),
                    Summary = result.Summary,
                    Reasons = result.Reasons?.ToList() ?? new List<string>(),
                    Metrics = result.Metrics?.Select(m => new AirhythmPluginMetricItem
                    {
                        Label = m.Label,
                        Value = m.Value,
                        Unit = m.Unit
                    }).ToList() ?? new List<AirhythmPluginMetricItem>()
                });
            }
            catch
            {
                results.Add(new AirhythmPluginAnalysisItem
                {
                    PluginName = plugin.Name,
                    PluginVersion = plugin.Version,
                    Score = 0,
                    Summary = "分析プラグインの実行に失敗しました。",
                    Reasons = new List<string> { "本体処理は継続しています。プラグイン側の更新または削除で復旧できます。" }
                });
            }
        }

        return results;
    }

    private static string ToKey(EpgEvent ev) => $"{ev.NetworkId}:{ev.TransportStreamId}:{ev.ServiceId}:{ev.EventId}";
    private static string ToKey(Reservation r) => $"{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}";

    private static AirhythmPreference BuildPreference(IReadOnlyList<Reservation> reservations, IReadOnlyDictionary<string, EpgEvent> epgByKey)
    {
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hourCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var serviceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var reservation in reservations.Where(r => r.Source != ReservationSource.Epg))
        {
            if (epgByKey.TryGetValue(ToKey(reservation), out var ev))
            {
                var genre = NormalizeGenre(ev.Genre);
                AddCount(genreCounts, genre, 1);
                foreach (var token in ExtractTokens(EpgProjection.Title(ev) + " " + EpgProjection.ShortText(ev) + " " + EpgProjection.ExtendedText(ev)))
                    AddCount(keywordCounts, token, 1);
            }
            else
            {
                foreach (var token in ExtractTokens(reservation.Title))
                    AddCount(keywordCounts, token, 1);
            }

            AddCount(serviceCounts, string.IsNullOrWhiteSpace(reservation.ServiceName) ? "局未設定" : reservation.ServiceName, 1);
            AddCount(hourCounts, GetHourBand(reservation.StartTime), 1);
        }

        return new AirhythmPreference
        {
            GenreCounts = genreCounts,
            HourCounts = hourCounts,
            ServiceCounts = serviceCounts,
            KeywordCounts = keywordCounts,
            TopGenreLabel = TopLabelOrFallback(genreCounts, "集計中"),
            TopHourLabel = TopLabelOrFallback(hourCounts, "集計中")
        };
    }

    private static List<AirhythmChainItem> BuildChainItems(IReadOnlyList<Reservation> reservations)
    {
        var items = new List<AirhythmChainItem>();
        var ordered = reservations.OrderBy(r => r.StartTime).ToList();
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];
            if (a.ServiceId != b.ServiceId || !string.Equals(a.ServiceName, b.ServiceName, StringComparison.OrdinalIgnoreCase))
                continue;

            var gapMinutes = (b.StartTime - a.EndTime).TotalMinutes;
            if (gapMinutes < -120 || gapMinutes > 20)
                continue;

            var score = 60;
            var status = "watch";
            var reason = "同一局の連続予約として観察対象です。";
            if (gapMinutes <= 5)
            {
                score = 90;
                status = a.IsConflicted || b.IsConflicted ? "warn" : "ok";
                reason = a.IsConflicted || b.IsConflicted
                    ? "同一局連続ですが競合を含むため、共通割り当て結果の確認が必要です。"
                    : "同一局でほぼ連続しているため、チェーン予約候補として良好です。";
            }
            else if (gapMinutes <= 10)
            {
                score = 76;
                status = "watch";
                reason = "間隔がやや空くため、録画前後マージンを含めて要観察です。";
            }
            else
            {
                score = 58;
                status = "review";
                reason = "局は同じですが間隔が長めなので、チェーンというより個別予約寄りです。";
            }

            items.Add(new AirhythmChainItem
            {
                Status = status,
                Score = score,
                Title = $"{a.Title} → {b.Title}",
                Meta = $"{a.ServiceName} / {a.StartTime:MM/dd HH:mm} → {b.EndTime:HH:mm} / 間隔 {gapMinutes:0}分",
                Reason = reason
            });
        }

        return items
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title, StringComparer.CurrentCulture)
            .Take(6)
            .ToList();
    }

    private static List<AirhythmRecommendationItem> BuildRecommendations(
        DateTime now,
        IReadOnlyList<EpgEvent> epgEvents,
        IReadOnlyList<Reservation> futureReservations,
        AirhythmPreference preference,
        int maxCount)
    {
        var reservedKeys = futureReservations.Select(ToKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<AirhythmRecommendationItem>();

        foreach (var ev in epgEvents.Where(e => e.Start >= now && e.Start <= now.AddDays(10)).OrderBy(e => e.Start))
        {
            if (reservedKeys.Contains(ToKey(ev)))
                continue;

            var score = ScoreEvent(ev, preference, out var tags, out var reasonLines);
            if (score < 35)
                continue;

            items.Add(new AirhythmRecommendationItem
            {
                NetworkId = ev.NetworkId,
                TransportStreamId = ev.TransportStreamId,
                ServiceId = ev.ServiceId,
                EventId = ev.EventId,
                Type = "recommend",
                Score = score,
                Title = EpgProjection.Title(ev),
                SubTitle = $"{ev.Start:MM/dd HH:mm}〜{ev.End:HH:mm} / {ev.ServiceName}",
                Reason = string.Join(" / ", reasonLines),
                Tags = tags
            });
        }

        return items
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.SubTitle, StringComparer.CurrentCulture)
            .Take(maxCount)
            .ToList();
    }

    private static List<AirhythmRecommendationItem> BuildWatchItems(
        DateTime now,
        IReadOnlyList<Reservation> allReservations,
        IReadOnlyDictionary<string, EpgEvent> epgByKey,
        AirhythmPreference preference,
        int maxCount)
    {
        var items = new List<AirhythmRecommendationItem>();
        foreach (var reservation in allReservations
            .Where(r => !r.IsEnabled && r.EndTime >= now.AddDays(-1) && r.StartTime <= now.AddDays(14))
            .OrderBy(r => r.StartTime))
        {
            var score = 25;
            var tags = new List<string> { "無効化済み" };
            var reason = new List<string> { "一度見送られた候補です。" };
            if (epgByKey.TryGetValue(ToKey(reservation), out var ev))
            {
                score = ScoreEvent(ev, preference, out var eventTags, out var eventReasons);
                tags.AddRange(eventTags.Where(x => !tags.Contains(x)));
                reason.AddRange(eventReasons);
            }

            if (score < 45)
                continue;

            items.Add(new AirhythmRecommendationItem
            {
                ReservationId = reservation.Id,
                Type = "watch",
                Score = score,
                Title = reservation.Title,
                SubTitle = $"{reservation.StartTime:MM/dd HH:mm}〜{reservation.EndTime:HH:mm} / {reservation.ServiceName}",
                Reason = string.Join(" / ", reason.Distinct(StringComparer.OrdinalIgnoreCase)),
                Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList()
            });
        }

        return items.OrderByDescending(x => x.Score).Take(maxCount).ToList();
    }

    private static int ScoreEvent(EpgEvent ev, AirhythmPreference preference, out List<string> tags, out List<string> reasons)
    {
        var score = 0;
        tags = new List<string>();
        reasons = new List<string>();
        var genre = NormalizeGenre(ev.Genre);
        var hourBand = GetHourBand(ev.Start);

        if (preference.GenreCounts.TryGetValue(genre, out var genreHits) && genreHits > 0)
        {
            score += Math.Min(genreHits * 3, 24);
            tags.Add(genre);
            reasons.Add($"よく録るジャンル ({genre}) に一致");
        }

        if (preference.HourCounts.TryGetValue(hourBand, out var hourHits) && hourHits > 0)
        {
            score += Math.Min(hourHits * 2, 18);
            tags.Add(hourBand);
            reasons.Add($"よく使う時間帯 ({hourBand}) に一致");
        }

        if (!string.IsNullOrWhiteSpace(ev.ServiceName) && preference.ServiceCounts.TryGetValue(ev.ServiceName, out var svcHits) && svcHits > 0)
        {
            score += Math.Min(svcHits * 2, 14);
            tags.Add("同局傾向");
            reasons.Add($"{ev.ServiceName} の予約傾向あり");
        }

        var titleText = $"{EpgProjection.Title(ev)} {EpgProjection.ShortText(ev)} {EpgProjection.ExtendedText(ev)}";
        var matchedKeywords = ExtractTokens(titleText)
            .Where(t => preference.KeywordCounts.TryGetValue(t, out var hits) && hits >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (matchedKeywords.Count > 0)
        {
            score += matchedKeywords.Count * 5;
            tags.AddRange(matchedKeywords.Select(x => $"語:{x}"));
            reasons.Add($"過去予約と近い語句 ({string.Join(", ", matchedKeywords)}) を検出");
        }

        if (ContainsAny(titleText, "新番組", "第1話", "新", "スタート"))
        {
            score += 18;
            tags.Add("新番組");
            reasons.Add("新番組候補として加点");
        }

        if (ContainsAny(titleText, "初放送", "初", "映画"))
        {
            score += 10;
            tags.Add("初放送候補");
            reasons.Add("単発で見逃しやすい番組として加点");
        }

        if (ev.Start <= DateTime.Now.AddHours(6))
        {
            score += 6;
            tags.Add("今夜");
            reasons.Add("近い時間帯なので先頭表示寄り");
        }

        return Math.Min(score, 99);
    }

    private static List<string> BuildTags(AirhythmPreference preference, IReadOnlyList<Reservation> futureReservations)
    {
        var tags = new List<string>();
        if (preference.TopGenreLabel != "集計中") tags.Add(preference.TopGenreLabel);
        if (preference.TopHourLabel != "集計中") tags.Add(preference.TopHourLabel);
        if (futureReservations.Any(r => r.IsConflicted)) tags.Add("競合あり");
        if (futureReservations.Any(r => !string.IsNullOrWhiteSpace(r.TunerName))) tags.Add("割り当て済み");
        return tags.Take(4).ToList();
    }

    private static string BuildComment(
        AirhythmProfileSettings profile,
        AirhythmSummary summary,
        IReadOnlyList<AirhythmRecommendationItem> recommendations,
        IReadOnlyList<AirhythmChainItem> chains,
        IReadOnlyList<AirhythmRecommendationItem> watchItems)
    {
        if (recommendations.Count > 0)
            return $"{profile.AssistantNickname}は今、{recommendations[0].Title} を有力候補として見ています。";
        if (chains.Any(x => x.Status == "warn" || x.Status == "review"))
            return $"{profile.AssistantNickname}はチェーン候補の中に再確認したいケースを見つけています。";
        if (watchItems.Count > 0)
            return $"見送った候補の中にも、もう一度見た方がよさそうな番組があります。";
        if (summary.ConflictCount > 0)
            return "競合を含む予約があります。AI-rhythmはまずそこを監視します。";
        return "おすすめ精度を育てるため、予約履歴を少しずつ学習しています。";
    }

    private static string NormalizeGenre(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return "ジャンル未設定";
        var text = genre.Split('・', '/', '／', ',', '，').FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "ジャンル未設定" : text;
    }

    private static string GetHourBand(DateTime time)
    {
        var hour = time.Hour;
        return hour switch
        {
            >= 5 and < 9 => "朝帯",
            >= 9 and < 18 => "日中帯",
            >= 18 and < 23 => "夜帯",
            _ => "深夜帯"
        };
    }

    private static IEnumerable<string> ExtractTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var token in text.Split(new[] { ' ', '　', '・', '/', '／', '「', '」', '【', '】', '(', ')', '（', '）', '！', '!', '?', '？', '：', ':', '、', ',', '，', '。', '―', '-', '〜', '~' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (t.Length < 2 || t.Length > 12) continue;
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) continue;
            yield return t;
        }
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static void AddCount(IDictionary<string, int> dict, string key, int delta)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        dict[key] = dict.TryGetValue(key, out var current) ? current + delta : delta;
    }

    private static string TopLabelOrFallback(Dictionary<string, int> counts, string fallback)
        => counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.CurrentCulture).FirstOrDefault().Key ?? fallback;

    private sealed class AirhythmPreference
    {
        public Dictionary<string, int> GenreCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> HourCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ServiceCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> KeywordCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string TopGenreLabel { get; init; } = "集計中";
        public string TopHourLabel { get; init; } = "集計中";
    }
}
