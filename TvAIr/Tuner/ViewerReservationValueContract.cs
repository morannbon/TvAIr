namespace TvAIr.Tuner;

/// <summary>
/// Shared projection rules for viewer reservation store and due scheduler.
/// An EPG DateTime without Kind means local broadcast time, never UTC.
/// </summary>
internal static class ViewerReservationValueContract
{
    public static DateTimeOffset ToOffset(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local))
            : new DateTimeOffset(value);

    public static string SafeLogValue(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160];
    }
}
