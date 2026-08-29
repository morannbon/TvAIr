using System.Text.Json;
using System.Text.Json.Serialization;

namespace TvAIrPlugin.Notifications;

[JsonConverter(typeof(PluginNotificationSeverityJsonConverter))]
public enum PluginNotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
    Progress
}

public sealed class PluginNotificationSeverityJsonConverter : JsonConverter<PluginNotificationSeverity>
{
    public override PluginNotificationSeverity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Notification severity must be a string.");

        var value = reader.GetString();
        return value?.Trim().ToLowerInvariant() switch
        {
            "info" => PluginNotificationSeverity.Info,
            "success" => PluginNotificationSeverity.Success,
            "warning" => PluginNotificationSeverity.Warning,
            "error" => PluginNotificationSeverity.Error,
            "progress" => PluginNotificationSeverity.Progress,
            _ => throw new JsonException($"Unknown notification severity: {value ?? "<null>"}.")
        };
    }

    public override void Write(Utf8JsonWriter writer, PluginNotificationSeverity value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            PluginNotificationSeverity.Info => "info",
            PluginNotificationSeverity.Success => "success",
            PluginNotificationSeverity.Warning => "warning",
            PluginNotificationSeverity.Error => "error",
            PluginNotificationSeverity.Progress => "progress",
            _ => throw new JsonException($"Unknown notification severity value: {(int)value}.")
        });
    }
}

public sealed record CreatePluginNotificationRequest(string NotificationDefinitionId, string Title, string Message, PluginNotificationSeverity Severity = PluginNotificationSeverity.Info, string InstanceKey = "default");
public sealed record UpdatePluginNotificationRequest(string NotificationInstanceId, string Title, string Message, PluginNotificationSeverity Severity = PluginNotificationSeverity.Info, long? ExpectedRevision = null);
public sealed record PluginNotificationState(string NotificationInstanceId, string NotificationDefinitionId, string InstanceKey, string Title, string Message, PluginNotificationSeverity Severity, bool IsRead, bool IsClosed, long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public interface ITvAirPluginNotificationsApi
{
    TvAIrPlugin.Runtime.TvAirOperationResult<PluginNotificationState> Create(CreatePluginNotificationRequest request);
    TvAIrPlugin.Runtime.TvAirOperationResult<PluginNotificationState> Update(UpdatePluginNotificationRequest request);
    TvAIrPlugin.Runtime.TvAirOperationResult Close(string notificationInstanceId, long? expectedRevision = null);
    IReadOnlyList<PluginNotificationState> List(bool includeClosed = false);
}
