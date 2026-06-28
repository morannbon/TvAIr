namespace TvAIr.Core;

public sealed class AirhythmProfileSettings
{
    public string UserNickname { get; set; } = "ユーザー";
    public string AssistantNickname { get; set; } = "AI-rhythm";
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class AirhythmDashboardResponse
{
    public AirhythmProfileSettings Profile { get; set; } = new();
    public string Greeting { get; set; } = "AI-rhythmからのお知らせです。";
    public string Comment { get; set; } = "録画傾向の集計を始めています。";
    public AirhythmSummary Summary { get; set; } = new();
    public List<AirhythmChainItem> ChainItems { get; set; } = new();
    public List<AirhythmRecommendationItem> Recommendations { get; set; } = new();
    public List<AirhythmRecommendationItem> WatchItems { get; set; } = new();
    public AirhythmBackupInfo Backup { get; set; } = new();
    public AirhythmPluginInfo Plugins { get; set; } = new();
    public List<AirhythmPluginAnalysisItem> PluginAnalyses { get; set; } = new();
}

public sealed class AirhythmSummary
{
    public int ReservedCount { get; set; }
    public int ChainCount { get; set; }
    public int ConflictCount { get; set; }
    public int RecommendationCount { get; set; }
    public string FavoriteGenre { get; set; } = "集計中";
    public string FavoriteHourBand { get; set; } = "集計中";
    public List<string> Tags { get; set; } = new();
}

public sealed class AirhythmChainItem
{
    public string Status { get; set; } = "watch";
    public int Score { get; set; }
    public string Title { get; set; } = "";
    public string Meta { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class AirhythmRecommendationItem
{
    public int? ReservationId { get; set; }
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
    public ushort? EventId { get; set; }
    public string Type { get; set; } = "recommend";
    public int Score { get; set; }
    public string Title { get; set; } = "";
    public string SubTitle { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

public sealed class AirhythmBackupInfo
{
    public string DataDirectory { get; set; } = "";
    public string SnapshotDirectory { get; set; } = "";
    public string LatestBackupPath { get; set; } = "";
    public DateTime? LatestBackupAt { get; set; }
}

public sealed class AirhythmPluginInfo
{
    public bool IsEnabled { get; set; } = true;
    public int AnalysisPluginCount { get; set; }
    public int ViewerPluginCount { get; set; }
    public List<string> LoadedPlugins { get; set; } = new();
}

public sealed class AirhythmPluginAnalysisItem
{
    public string PluginName { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = new();
    public List<AirhythmPluginMetricItem> Metrics { get; set; } = new();
}

public sealed class AirhythmPluginMetricItem
{
    public string Label { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
}
