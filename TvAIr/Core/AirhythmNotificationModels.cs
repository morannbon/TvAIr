namespace TvAIr.Core;

public sealed class AirhythmNotificationInfo
{
    public int BadgeCount { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Key { get; set; } = "";
    public DateTime EvaluatedAt { get; set; } = DateTime.Now;
}

public sealed class AirhythmNotificationState
{
    public DateTime? LastOpenedAt { get; set; }
    public string LastOpenedDateKey { get; set; } = "";
}
