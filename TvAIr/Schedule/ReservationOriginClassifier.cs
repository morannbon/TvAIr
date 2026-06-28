using TvAIr.Core;

namespace TvAIr.Schedule;

public enum ReservationOriginKind
{
    ManualProgramGuide,
    ImmediateProgramGuide,
    KeywordSearchProgramGuide,
    AutoSearch,
    ExplicitProgramRule,
    ProgramGuideMissingProgramRule,
    SystemEpg,
    Unknown
}

public enum ReservationIdentityKind
{
    EventIdentity,
    TimeIdentity,
    RuleIdentity,
    Unknown
}

public sealed record ReservationOriginClassification(
    ReservationOriginKind Origin,
    ReservationIdentityKind Identity,
    bool IsProgramGuideMissingProgramRule,
    bool IsResolvedEventReservation,
    string Reason);

/// <summary>
/// v0.11.370: 予約元と同定軸の分類を一箇所へ集約する。
/// ここはチューナー割当を行わず、ALLOC_ROUTE/TUNER_ALLOCへ渡す前の意味付けだけを扱う。
/// </summary>
public static class ReservationOriginClassifier
{
    public const string ProgramGuideMissingRuleMarker = "ProgramGuideMissing";
    public const string ProgramGuideMissingTitle = "ProgramGuideBlank";

    public static ReservationOriginClassification Classify(Reservation? r)
    {
        if (r is null)
            return new(ReservationOriginKind.Unknown, ReservationIdentityKind.Unknown, false, false, "null_reservation");

        var identity = ClassifyIdentity(r);
        var title = Normalize(r.Title);
        var ruleName = Normalize(r.SourceRuleName);
        var isMissingProgramRule = IsProgramGuideMissingProgramRule(r);
        var isResolved = IsResolvedEventReservation(r);

        var origin = r.Source switch
        {
            ReservationSource.Manual => ReservationOriginKind.ManualProgramGuide,
            ReservationSource.Immediate => ReservationOriginKind.ImmediateProgramGuide,
            ReservationSource.KeywordSearch => ReservationOriginKind.KeywordSearchProgramGuide,
            ReservationSource.Keyword => ReservationOriginKind.AutoSearch,
            ReservationSource.Epg => ReservationOriginKind.SystemEpg,
            ReservationSource.Program when isMissingProgramRule => ReservationOriginKind.ProgramGuideMissingProgramRule,
            ReservationSource.Program => ReservationOriginKind.ExplicitProgramRule,
            _ => ReservationOriginKind.Unknown
        };

        var reason = origin switch
        {
            ReservationOriginKind.ProgramGuideMissingProgramRule => $"source_program_rule_marker title=[{title}] rule=[{ruleName}]",
            ReservationOriginKind.ExplicitProgramRule => "source_program_explicit_or_legacy",
            ReservationOriginKind.AutoSearch => "source_keyword",
            ReservationOriginKind.ManualProgramGuide => "source_manual_programguide",
            ReservationOriginKind.ImmediateProgramGuide => "source_immediate_programguide",
            ReservationOriginKind.KeywordSearchProgramGuide => "source_keywordsearch_programguide",
            ReservationOriginKind.SystemEpg => "source_epg_internal",
            _ => "source_unknown"
        };

        return new(origin, identity, isMissingProgramRule, isResolved, reason);
    }

    public static ReservationIdentityKind ClassifyIdentity(Reservation? r)
    {
        if (r is null) return ReservationIdentityKind.Unknown;
        if (r.NetworkId > 0 && r.TransportStreamId > 0 && r.ServiceId > 0 && r.EventId > 0)
            return ReservationIdentityKind.EventIdentity;
        if (r.NetworkId > 0 && r.TransportStreamId > 0 && r.ServiceId > 0 && r.StartTime < r.EndTime)
            return ReservationIdentityKind.TimeIdentity;
        if (r.SourceRuleId.HasValue)
            return ReservationIdentityKind.RuleIdentity;
        return ReservationIdentityKind.Unknown;
    }

    public static bool IsProgramGuideMissingProgramRule(Reservation? r)
    {
        if (r is null || r.Source != ReservationSource.Program || !r.SourceRuleId.HasValue) return false;
        return IsProgramGuideMissingRuleName(r.SourceRuleName)
            || string.Equals(Normalize(r.Title), ProgramGuideMissingTitle, StringComparison.Ordinal);
    }

    public static bool IsProgramGuideMissingProgramRule(ProgramRule? rule)
    {
        if (rule is null) return false;
        return IsProgramGuideMissingRuleName(rule.Name);
    }

    public static bool IsProgramGuideMissingRuleName(string? name)
    {
        var n = Normalize(name);
        return string.Equals(n, ProgramGuideMissingRuleMarker, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, ProgramGuideMissingTitle, StringComparison.Ordinal);
    }

    public static bool IsResolvedEventReservation(Reservation? r)
    {
        if (r is null || r.Source == ReservationSource.Epg) return false;
        if (IsProgramGuideMissingProgramRule(r)) return false;
        if (ClassifyIdentity(r) != ReservationIdentityKind.EventIdentity) return false;
        var title = Normalize(r.Title);
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (title.StartsWith("放送休止", StringComparison.Ordinal) || title.StartsWith("休止", StringComparison.Ordinal)) return false;
        return true;
    }

    public static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
}
