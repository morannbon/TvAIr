namespace TvAIr.Core;

/// <summary>
/// User-facing reservation title display contract.
///
/// Raw Reservation.Title and raw EPG title are allowed to remain empty for blank-title
/// EPG events. Presentation surfaces must not expose empty brackets, a blank title lane,
/// or a bare dash as the user-visible program title.
/// </summary>
public static class ReservationTitleDisplayContract
{
    public const string UnavailableTitle = "未取得";
    public const string Rule = "release_contract";

    public static string NormalizeRaw(string? value)
        => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

    public static string ForUser(string? rawTitle)
    {
        var title = NormalizeRaw(rawTitle);
        return string.IsNullOrWhiteSpace(title) ? UnavailableTitle : title;
    }

    public static string ForLog(string? rawTitle, int maxLength = 120)
    {
        var title = ForUser(rawTitle);
        return title.Length <= maxLength ? title : title[..maxLength] + "…";
    }

    public static string RawBlankFlag(string? rawTitle)
        => string.IsNullOrWhiteSpace(NormalizeRaw(rawTitle)) ? "True" : "False";
}
