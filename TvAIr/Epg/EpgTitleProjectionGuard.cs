using TvAIr.Core;

namespace TvAIr.Epg;

/// <summary>
/// EPG番組候補の最低限の入力品質を確認する。
/// Reservation内部用途はReservation.Intentが正本であり、番組タイトルから用途を推測しない。
/// </summary>
public static class EpgTitleProjectionGuard
{
    public static bool IsSafeForSpecialProjection(EpgEvent e, out string reason)
        => IsSafeTitleForCandidate(EpgProjection.Title(e), out reason);

    public static bool IsSafeForAutoReservation(EpgEvent e, out string reason)
        => IsSafeTitleForCandidate(EpgProjection.Title(e), out reason);

    public static bool IsSafeTitleForCandidate(string? title, out string reason)
    {
        var t = Normalize(title);
        if (string.IsNullOrWhiteSpace(t))
        {
            reason = "blank";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().Normalize(System.Text.NormalizationForm.FormKC);
}
