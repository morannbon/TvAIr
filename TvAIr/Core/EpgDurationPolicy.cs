namespace TvAIr.Core;

/// <summary>
/// EPG深度・取得秒数の共通ポリシー。
///
/// release_contract:
///   設定表示、定時EPG枠見積り、通常EPG実取得、録画前EPG確認、ログ出力が
///   個別に depth -> seconds を変換していたため、deeper 表示なのに実取得が120秒へ戻る
///   経路が発生した。以後、EPGの秒数変換はこのクラスを唯一の入口にする。
/// </summary>
public static class EpgDurationPolicy
{
    public const string Rule = "epg_duration_policy_common";

    public static string NormalizeDepth(string? value)
        => value is "shallow" or "medium" or "deep" or "deeper" ? value : "medium";

    public static int BaseSecondsForDepth(string? value) => NormalizeDepth(value) switch
    {
        "shallow" => 120,
        "deep"    => 240,
        "deeper"  => 300,
        _          => 180
    };

    public static EpgDurationPlan Create(
        string? depth,
        bool isPreRecordCheck,
        int? maxCaptureSeconds,
        int serviceCount,
        int pass,
        int configuredExtraPerServiceSeconds)
    {
        var normalizedDepth = NormalizeDepth(depth);
        var configuredBase = BaseSecondsForDepth(normalizedDepth);

        // release_contract以降の方針を共通化:
        // BS/CSや多サービスTSを秒数の隠れ延長で補わず、チャンネル/TS/SID束ねの監査で追う。
        var effectiveBase = configuredBase;
        var configuredExtra = Math.Max(0, configuredExtraPerServiceSeconds);
        var effectiveExtra = 0;
        var safeServiceCount = Math.Max(1, serviceCount);
        var normalSeconds = effectiveBase + Math.Max(0, safeServiceCount - 1) * effectiveExtra;

        // 再巡回でも勝手に秒数を伸ばさない。
        var retryExtra = 0;

        var recDuration = normalSeconds;
        var reason = "channel_scope_first_no_hidden_bscs_extension";
        if (isPreRecordCheck && maxCaptureSeconds.HasValue)
        {
            // 録画前EPG確認は時間追従プローブ。通常EPGの深度を上限に、安全上限へ丸める。
            recDuration = Math.Max(8, Math.Min(normalSeconds, maxCaptureSeconds.Value));
            reason = "pre_record_time_follow_safety_ceiling";
        }

        return new EpgDurationPlan(
            Depth: normalizedDepth,
            ConfiguredBaseSeconds: configuredBase,
            EffectiveBaseSeconds: effectiveBase,
            ConfiguredExtraPerServiceSeconds: configuredExtra,
            EffectiveExtraPerServiceSeconds: effectiveExtra,
            ServiceCount: safeServiceCount,
            NormalDurationSeconds: normalSeconds,
            RetryExtraSeconds: retryExtra,
            RecDurationSeconds: recDuration,
            Pass: Math.Max(1, pass),
            Reason: reason,
            Rule: Rule);
    }

    public static EpgDurationPlan CreateSchedulePlan(
        string? depth,
        int configuredExtraPerServiceSeconds,
        int serviceCount = 1)
        => Create(
            depth,
            isPreRecordCheck: false,
            maxCaptureSeconds: null,
            serviceCount: serviceCount,
            pass: 1,
            configuredExtraPerServiceSeconds: configuredExtraPerServiceSeconds);
}

public sealed record EpgDurationPlan(
    string Depth,
    int ConfiguredBaseSeconds,
    int EffectiveBaseSeconds,
    int ConfiguredExtraPerServiceSeconds,
    int EffectiveExtraPerServiceSeconds,
    int ServiceCount,
    int NormalDurationSeconds,
    int RetryExtraSeconds,
    int RecDurationSeconds,
    int Pass,
    string Reason,
    string Rule);
