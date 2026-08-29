using TvAIr.Core;
using TvAIrPlugin;

namespace TvAIr.Plugin;

public sealed class TimedTextStreamGroup
{
    public string StreamId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTimeOffset? LatestOccurredAt { get; init; }
    public IReadOnlyList<TvAirTimedTextItemDto> Items { get; init; } = Array.Empty<TvAirTimedTextItemDto>();
}

public sealed class TimedTextStreamStore
{
    private const int PerGroupLimit = 300;
    private const int GlobalLimit = 3000;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedList<TvAirTimedTextItemDto>> _byGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<TvAirTimedTextItemDto> _global = new();
    private readonly Dictionary<string, int> _receivedCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly LogRepository _log;

    public TimedTextStreamStore(LogRepository log) => _log = log;

    public void Add(string defaultOwnerId, TvAirTimedTextPublishDto item)
    {
        if (item is null) return;
        var normalized = Normalize(defaultOwnerId, item);
        if (string.IsNullOrWhiteSpace(normalized.Text)) return;
        int total; int queued;
        lock (_sync)
        {
            var key = GroupKey(normalized.StreamId, normalized.GroupId);
            if (!_byGroup.TryGetValue(key, out var list)) _byGroup[key] = list = new();
            list.AddLast(normalized);
            while (list.Count > PerGroupLimit) list.RemoveFirst();
            _global.AddLast(normalized);
            while (_global.Count > GlobalLimit) _global.RemoveFirst();
            _receivedCounts.TryGetValue(key, out var current);
            _receivedCounts[key] = total = current + 1;
            queued = list.Count;
        }
        if (total == 1 || total % 250 == 0)
            _log.Add("PLUGIN_TIMED_TEXT_RECEIVED", normalized.SourceKind, $"streamId={normalized.StreamId} groupId={normalized.GroupId} textLength={normalized.Text.Length} receivedCount={total} queuedCount={queued}");
    }

    public IReadOnlyList<TvAirTimedTextItemDto> GetRecent(string? streamId, string? groupId, int count)
    {
        var limit=Math.Clamp(count<=0?100:count,1,PerGroupLimit);
        lock(_sync)
        {
            if(!string.IsNullOrWhiteSpace(groupId))
            {
                var key=GroupKey(NormalizeId(streamId,"default",80),NormalizeId(groupId,"active",80));
                return _byGroup.TryGetValue(key,out var list)?list.Reverse().Take(limit).Reverse().ToList():Array.Empty<TvAirTimedTextItemDto>();
            }
            IEnumerable<TvAirTimedTextItemDto> source=_global;
            if(!string.IsNullOrWhiteSpace(streamId)) source=source.Where(x=>string.Equals(x.StreamId,streamId.Trim(),StringComparison.OrdinalIgnoreCase));
            return source.Reverse().Take(limit).Reverse().ToList();
        }
    }

    public IReadOnlyList<TimedTextStreamGroup> GetGroups(string? streamId,int count)
    {
        var limit=Math.Clamp(count<=0?50:count,1,PerGroupLimit);
        lock(_sync)
            return _byGroup.Values.Select(list=>list.Reverse().Take(limit).Reverse().ToList()).Where(items=>items.Count>0)
                .Where(items=>string.IsNullOrWhiteSpace(streamId)||string.Equals(items[^1].StreamId,streamId.Trim(),StringComparison.OrdinalIgnoreCase))
                .Select(items=>new TimedTextStreamGroup{StreamId=items[^1].StreamId,GroupId=items[^1].GroupId,Title=items[^1].Title,Subtitle=items[^1].Subtitle,Count=items.Count,LatestOccurredAt=items[^1].OccurredAt,Items=items})
                .OrderBy(x=>x.LatestOccurredAt).ToList();
    }

    public void PruneOlderThan(TimeSpan age)
    {
        var threshold=DateTimeOffset.Now-age;
        lock(_sync)
        {
            while(_global.First is { Value.OccurredAt: var at } && at<threshold) _global.RemoveFirst();
            foreach(var key in _byGroup.Keys.ToList())
            {
                var list=_byGroup[key];
                while(list.First is { Value.OccurredAt: var at } && at<threshold) list.RemoveFirst();
                if(list.Count==0)
                {
                    _byGroup.Remove(key);
                    _receivedCounts.Remove(key);
                }
            }
        }
    }

    private static TvAirTimedTextItemDto Normalize(string owner,TvAirTimedTextPublishDto x)=>new()
    {
        StreamId=NormalizeId(x.StreamId,"default",80), GroupId=NormalizeId(x.GroupId,"active",80),
        SourceOwnerId=NormalizeId(string.IsNullOrWhiteSpace(x.SourceOwnerId)?owner:x.SourceOwnerId,"unknown",120), SourceKind=Trim(x.SourceKind,80),
        Title=Trim(x.Title,240), Subtitle=Trim(x.Subtitle,240), PositionMilliseconds=x.PositionMilliseconds,
        AuthorId=Trim(x.AuthorId,120), Style=Trim(x.Style,240), Text=Trim(x.Text,1000), OccurredAt=x.OccurredAt??DateTimeOffset.Now,
        Attributes=(x.Attributes??new Dictionary<string,string>()).Where(kv=>!string.IsNullOrWhiteSpace(kv.Key)).Take(32).ToDictionary(kv=>Trim(kv.Key,80),kv=>Trim(kv.Value,240),StringComparer.OrdinalIgnoreCase)
    };
    private static string GroupKey(string stream,string group)=>stream+"\n"+group;
    private static string NormalizeId(string? value,string fallback,int max){var s=(value??"").Trim();return s.Length==0?fallback:Trim(s,max);}
    // TIMED_TEXT_UTF16_BOUNDARY_INVARIANT:
    // Keep the existing UTF-16 code-unit limit, but never create an isolated surrogate
    // by cutting between a valid high/low surrogate pair. This is boundary safety only;
    // do not add normalization, emoji replacement, font probing, or plugin-specific repair here.
    private static string Trim(string? value, int max)
    {
        var s = value ?? string.Empty;
        if (s.Length <= max) return s;

        var cut = Math.Max(0, max);
        if (cut > 0
            && cut < s.Length
            && char.IsHighSurrogate(s[cut - 1])
            && char.IsLowSurrogate(s[cut]))
        {
            cut--;
        }

        return s[..cut];
    }
}
