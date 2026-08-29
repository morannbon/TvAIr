using System.Text.Json;
using TvAIr.Core;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>Host-owned playback progress ledger keyed by stable recording identity.</summary>
public sealed class PlaybackProgressStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private readonly LogRepository log;
    private Dictionary<string, TvAirPlaybackProgressDto> rows;

    public PlaybackProgressStore(Database database, LogRepository log)
    {
        this.log = log;
        Directory.CreateDirectory(database.DataDirectory);
        filePath = Path.Combine(database.DataDirectory, "playback-progress.json");
        rows = Load();
    }

    public TvAirPlaybackProgressSnapshotDto GetSnapshot()
    {
        lock (gate)
        {
            var capturedAt = DateTimeOffset.Now;
            return new TvAirPlaybackProgressSnapshotDto
            {
                SnapshotId = $"playback:{capturedAt:O}:{rows.Count}",
                CapturedAt = capturedAt,
                Items = rows.Values.OrderByDescending(x => x.UpdatedAt).ToArray()
            };
        }
    }

    public TvAirPlaybackProgressDto? Get(string recordingId)
    {
        var key = NormalizeId(recordingId);
        lock (gate) return rows.TryGetValue(key, out var value) ? value : null;
    }

    public TvAirPlaybackProgressDto Update(TvAirPlaybackProgressUpdateDto update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var key = NormalizeId(update.RecordingId);
        var now = update.PlayedAt ?? DateTimeOffset.Now;
        lock (gate)
        {
            rows.TryGetValue(key, out var current);
            var duration = Math.Max(0, update.DurationSeconds > 0 ? update.DurationSeconds : current?.DurationSeconds ?? 0);
            var position = Math.Clamp(update.PositionSeconds, 0, duration > 0 ? duration : long.MaxValue);
            var ratio = duration > 0 ? Math.Clamp((double)position / duration, 0, 1) : 0;
            var completed = update.IsCompleted ?? (duration > 0 && ratio >= 0.95);
            var result = new TvAirPlaybackProgressDto
            {
                RecordingId = key,
                ReservationId = string.IsNullOrWhiteSpace(update.ReservationId) ? current?.ReservationId ?? string.Empty : update.ReservationId.Trim(),
                PositionSeconds = completed && duration > 0 ? duration : position,
                DurationSeconds = duration,
                CompletionRatio = completed ? 1 : ratio,
                IsCompleted = completed,
                PlayCount = Math.Max(0, current?.PlayCount ?? 0) + (update.IncrementPlayCount ? 1 : 0),
                FirstPlayedAt = current?.FirstPlayedAt ?? now,
                LastPlayedAt = now,
                UpdatedAt = DateTimeOffset.Now
            };
            rows[key] = result;
            SaveUnsafe();
            return result;
        }
    }

    public bool Remove(string recordingId)
    {
        var key = NormalizeId(recordingId);
        lock (gate)
        {
            if (!rows.Remove(key)) return false;
            SaveUnsafe();
            return true;
        }
    }

    private Dictionary<string, TvAirPlaybackProgressDto> Load()
    {
        if (!File.Exists(filePath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = JsonSerializer.Deserialize<List<TvAirPlaybackProgressDto>>(File.ReadAllText(filePath), JsonOptions) ?? new();
            var result = values.Where(x => !string.IsNullOrWhiteSpace(x.RecordingId))
                .GroupBy(x => x.RecordingId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.OrderBy(v => v.UpdatedAt).Last(), StringComparer.OrdinalIgnoreCase);
            log.Add("PLAYBACK_PROGRESS_STORE_LOAD", "PlaybackProgress", $"result=OK count={result.Count} rule=playback_progress_store_contract");
            return result;
        }
        catch (Exception ex)
        {
            log.Add("PLAYBACK_PROGRESS_STORE_LOAD", "PlaybackProgress", $"result=FAILED filePreserved=True reason={Sanitize(ex.Message)} rule=playback_progress_store_contract");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnsafe()
    {
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(rows.Values.OrderBy(x => x.RecordingId).ToArray(), JsonOptions));
        File.Move(temp, filePath, true);
    }

    private static string NormalizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("recordingId is required", nameof(value));
        var id = value.Trim();
        if (id.Length > 256) throw new ArgumentException("recordingId is too long", nameof(value));
        return id;
    }

    private static string Sanitize(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace('\r', ' ').Replace('\n', ' ').Trim()[..Math.Min(160, value.Replace('\r', ' ').Replace('\n', ' ').Trim().Length)];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
