using System.Text.Json;

namespace TvAIr.Core;

public sealed class AirhythmProfileService
{
    private readonly Database db;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object syncRoot = new();

    public AirhythmProfileService(Database db)
    {
        this.db = db;
    }

    public AirhythmProfileSettings Get()
    {
        lock (syncRoot)
        {
            var path = GetProfilePath();
            if (!File.Exists(path))
            {
                return new AirhythmProfileSettings
                {
                    UserNickname = string.Empty,
                    AssistantNickname = string.Empty,
                    IsEnabled = true,
                    UpdatedAt = DateTime.MinValue
                };
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AirhythmProfileSettings>(json, jsonOptions) ?? new AirhythmProfileSettings();
            }
            catch
            {
                return new AirhythmProfileSettings();
            }
        }
    }

    public AirhythmProfileSettings Save(AirhythmProfileSettings input)
    {
        lock (syncRoot)
        {
            var normalized = new AirhythmProfileSettings
            {
                UserNickname = NormalizeUserNickname(input.UserNickname),
                AssistantNickname = Normalize(input.AssistantNickname, "AI-rhythm"),
                IsEnabled = input.IsEnabled,
                UpdatedAt = DateTime.Now
            };

            var path = GetProfilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(normalized, jsonOptions));
            return normalized;
        }
    }

    public string GetProfilePath() => Path.Combine(db.DataDirectory, "airhythm-profile.json");

    private static string Normalize(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string NormalizeUserNickname(string? value)
    {
        var text = Normalize(value, "ユーザー");
        return text.EndsWith("さん", StringComparison.Ordinal)
            ? text[..^2].TrimEnd()
            : text;
    }

    public static string FormatDisplayUserNickname(string? value)
    {
        var text = Normalize(value, "ユーザー");
        return text.EndsWith("さん", StringComparison.Ordinal) ? text : text + "さん";
    }
}
