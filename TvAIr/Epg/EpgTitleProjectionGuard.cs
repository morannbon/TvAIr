using System.Text.RegularExpressions;
using TvAIr.Core;

namespace TvAIr.Epg;

public static class EpgTitleProjectionGuard
{
    private static readonly Regex InternalPurposeTitle = new(
        @"^(EPG確認|録画前EPG確認対象|SystemEpg|PreRecEpg)(\s|（|\(|:|：|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSafeForSpecialProjection(EpgEvent e, out string reason)
    {
        return IsSafeTitleForCandidate(EpgProjection.Title(e), out reason);
    }

    public static bool IsSafeForAutoReservation(EpgEvent e, out string reason)
    {
        return IsSafeTitleForCandidate(EpgProjection.Title(e), out reason);
    }

    public static bool IsSafeTitleForCandidate(string? title, out string reason)
    {
        var t = Normalize(title);
        if (string.IsNullOrWhiteSpace(t)) { reason = "blank"; return false; }
        if (InternalPurposeTitle.IsMatch(t)) { reason = "internal_purpose_title"; return false; }
        reason = "ok";
        return true;
    }

    public static string NormalizeInternalSystemTitle(string? title, string? serviceName = null)
    {
        var t = Normalize(title);
        if (!string.IsNullOrWhiteSpace(t) && !InternalPurposeTitle.IsMatch(t)) return t;
        var service = Normalize(serviceName);
        return service;
    }

    public static bool IsInternalPurposeTitle(string? title)
    {
        var t = Normalize(title);
        return !string.IsNullOrWhiteSpace(t) && InternalPurposeTitle.IsMatch(t);
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().Normalize(System.Text.NormalizationForm.FormKC);
}
