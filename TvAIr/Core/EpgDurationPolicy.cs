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
        => SettingsDefaults.NormalizeEpgDepth(value);

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
            // 録画前EPG確認の maxCaptureSeconds は、Scheduler が設定の5/10/15/20分と
            // 本録画due・他録画占有から逆算した hard deadline。通常EPGの depth 秒数で
            // 90/300秒へ再度切り詰めると設定窓の意味を失うため、ここではその安全予算を尊重する。
            // workerは目的EventIdentityを観測した時点で即終了するため、予算全量を常用しない。
            recDuration = Math.Max(8, maxCaptureSeconds.Value);
            reason = "pre_record_setting_horizon_hard_deadline";
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
