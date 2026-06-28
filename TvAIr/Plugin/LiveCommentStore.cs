using TvAIr.Core;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// NicoJkPluginなどから受け取ったライブコメントをUI配信用に短期間だけ保持する。
/// 永続化はプラグイン側のNicoJK互換ログへ任せ、本体DBへは保存しない。
/// </summary>
public sealed class LiveCommentStore
{
    private const int PerReservationLimit = 300;
    private const int GlobalLimit = 3000;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedList<LiveCommentEvent>> _byReservation = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LiveCommentEvent> _global = new();
    private readonly Dictionary<string, int> _receivedCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly LogRepository _log;

    public LiveCommentStore(LogRepository log)
    {
        _log = log;
    }

    public void Add(LiveCommentEvent comment)
    {
        if (comment is null) return;
        var normalized = Normalize(comment);
        if (string.IsNullOrWhiteSpace(normalized.Content)) return;

        int countForReservation;
        int queuedCount;
        lock (_sync)
        {
            var key = NormalizeReservationKey(normalized.ReservationId);
            if (!_byReservation.TryGetValue(key, out var list))
            {
                list = new LinkedList<LiveCommentEvent>();
                _byReservation[key] = list;
            }

            list.AddLast(normalized);
            while (list.Count > PerReservationLimit) list.RemoveFirst();

            _global.AddLast(normalized);
            while (_global.Count > GlobalLimit) _global.RemoveFirst();

            _receivedCounts.TryGetValue(key, out var currentCount);
            countForReservation = currentCount + 1;
            _receivedCounts[key] = countForReservation;
            queuedCount = list.Count;
        }

        if (countForReservation == 1 || countForReservation % 250 == 0)
        {
            _log.Add(
                "PLUGIN_LIVE_COMMENT_RECEIVED",
                string.IsNullOrWhiteSpace(normalized.ServiceName) ? "NicoJk" : normalized.ServiceName,
                $"reservationId={normalized.ReservationId} jkChannel={normalized.JkChannel} contentLength={normalized.Content.Length} receivedCount={countForReservation} log=throttled_250 rule=wake_log_polish_quality_wording_noise_reduce");
            _log.Add(
                "PLUGIN_LIVE_COMMENT_DISPATCH",
                "webui",
                $"target=webui reservationId={normalized.ReservationId} queuedCount={queuedCount}");
        }
    }

    public IReadOnlyList<LiveCommentEvent> GetRecent(string? reservationId, int count = 100)
    {
        var limit = Math.Clamp(count <= 0 ? 100 : count, 1, PerReservationLimit);
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(reservationId))
            {
                var key = NormalizeReservationKey(reservationId);
                return _byReservation.TryGetValue(key, out var list)
                    ? list.Reverse().Take(limit).Reverse().ToList()
                    : Array.Empty<LiveCommentEvent>();
            }
            return _global.Reverse().Take(limit).Reverse().ToList();
        }
    }

    public IReadOnlyList<object> GetGroups(int perReservationCount = 50)
    {
        var limit = Math.Clamp(perReservationCount <= 0 ? 50 : perReservationCount, 1, PerReservationLimit);
        lock (_sync)
        {
            return _byReservation
                .Select(pair =>
                {
                    var comments = pair.Value.Reverse().Take(limit).Reverse().ToList();
                    var last = comments.LastOrDefault();
                    return new
                    {
                        reservationId = pair.Key,
                        serviceName = last?.ServiceName ?? string.Empty,
                        programTitle = last?.ProgramTitle ?? string.Empty,
                        jkChannel = last?.JkChannel ?? 0,
                        count = pair.Value.Count,
                        latestReceivedAt = last?.ReceivedAt,
                        comments
                    };
                })
                .OrderBy(x => x.latestReceivedAt)
                .Cast<object>()
                .ToList();
        }
    }

    public void PruneOlderThan(TimeSpan age)
    {
        var threshold = DateTimeOffset.Now - age;
        lock (_sync)
        {
            while (_global.First is { Value.ReceivedAt: var at } && at < threshold) _global.RemoveFirst();
            foreach (var key in _byReservation.Keys.ToList())
            {
                var list = _byReservation[key];
                while (list.First is { Value.ReceivedAt: var at } && at < threshold) list.RemoveFirst();
                if (list.Count == 0) _byReservation.Remove(key);
            }
        }
    }

    private static LiveCommentEvent Normalize(LiveCommentEvent source)
    {
        var reservationId = NormalizeReservationKey(source.ReservationId);
        return new LiveCommentEvent
        {
            PluginId = TrimTo(source.PluginId, 80),
            ReservationId = reservationId,
            ServiceName = TrimTo(source.ServiceName, 120),
            ProgramTitle = TrimTo(source.ProgramTitle, 240),
            JkChannel = source.JkChannel,
            UnixTime = source.UnixTime,
            Vpos = source.Vpos,
            UserId = TrimTo(source.UserId, 120),
            Mail = TrimTo(source.Mail, 240),
            Content = TrimTo(source.Content, 1000),
            ReceivedAt = source.ReceivedAt == default ? DateTimeOffset.Now : source.ReceivedAt
        };
    }

    private static string NormalizeReservationKey(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return "active";
        if (raw.StartsWith("R", StringComparison.OrdinalIgnoreCase)) return "R" + raw[1..].Trim();
        return int.TryParse(raw, out var id) ? $"R{id}" : TrimTo(raw, 80);
    }

    private static string TrimTo(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
