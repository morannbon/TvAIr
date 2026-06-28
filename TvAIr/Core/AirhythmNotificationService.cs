using System.Text.Json;
using TvAIr.Schedule;

namespace TvAIr.Core;

public sealed class AirhythmNotificationService
{
    private readonly Database db;
    private readonly AirhythmDashboardService dashboardService;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object syncRoot = new();

    public AirhythmNotificationService(Database db, AirhythmDashboardService dashboardService)
    {
        this.db = db;
        this.dashboardService = dashboardService;
    }

    public AirhythmNotificationInfo GetStatus()
    {
        var dashboard = dashboardService.Build();
        var candidate = BuildCandidate(dashboard);
        if (candidate.BadgeCount <= 0)
            return candidate;

        var state = GetState();
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
        if (string.Equals(state.LastOpenedDateKey, todayKey, StringComparison.Ordinal))
        {
            candidate.BadgeCount = 0;
        }

        return candidate;
    }

    public AirhythmNotificationInfo MarkOpened()
    {
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
        lock (syncRoot)
        {
            var state = GetStateUnsafe();
            state.LastOpenedAt = DateTime.Now;
            state.LastOpenedDateKey = todayKey;
            SaveStateUnsafe(state);
        }

        var info = GetStatus();
        info.BadgeCount = 0;
        return info;
    }

    private AirhythmNotificationInfo BuildCandidate(AirhythmDashboardResponse dashboard)
    {
        if (dashboard.Summary.ConflictCount > 0)
        {
            return new AirhythmNotificationInfo
            {
                BadgeCount = 1,
                Key = "conflict",
                Title = "競合があります",
                Summary = $"競合 {dashboard.Summary.ConflictCount} 件を確認できます。",
                EvaluatedAt = DateTime.Now
            };
        }

        var topWatch = dashboard.WatchItems.OrderByDescending(x => x.Score).FirstOrDefault();
        if (topWatch is not null && topWatch.Score >= 75)
        {
            return new AirhythmNotificationInfo
            {
                BadgeCount = 1,
                Key = $"watch:{topWatch.Title}",
                Title = "見送り候補を再確認",
                Summary = topWatch.Title,
                EvaluatedAt = DateTime.Now
            };
        }

        var topRecommendation = dashboard.Recommendations.OrderByDescending(x => x.Score).FirstOrDefault();
        if (topRecommendation is not null && topRecommendation.Score >= 85)
        {
            return new AirhythmNotificationInfo
            {
                BadgeCount = 1,
                Key = $"recommend:{topRecommendation.Title}",
                Title = "おすすめがあります",
                Summary = topRecommendation.Title,
                EvaluatedAt = DateTime.Now
            };
        }

        return new AirhythmNotificationInfo
        {
            BadgeCount = 0,
            Key = "none",
            Title = "通知なし",
            Summary = "今日は厳選通知はありません。",
            EvaluatedAt = DateTime.Now
        };
    }

    private AirhythmNotificationState GetState()
    {
        lock (syncRoot)
        {
            return GetStateUnsafe();
        }
    }

    private AirhythmNotificationState GetStateUnsafe()
    {
        var path = GetStatePath();
        if (!File.Exists(path))
        {
            var initial = new AirhythmNotificationState();
            SaveStateUnsafe(initial);
            return initial;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AirhythmNotificationState>(json, jsonOptions) ?? new AirhythmNotificationState();
        }
        catch
        {
            return new AirhythmNotificationState();
        }
    }

    private void SaveStateUnsafe(AirhythmNotificationState state)
    {
        var path = GetStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, jsonOptions));
    }

    private string GetStatePath() => Path.Combine(db.DataDirectory, "airhythm-notification-state.json");
}
