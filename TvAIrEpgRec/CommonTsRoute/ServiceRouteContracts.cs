namespace TvAIrEpgRec.CommonTsRoute;

/// <summary>
/// v0.7.56 shared route contract boundary.
/// This is intentionally a contract-only type set. Production recording remains on DirectRecorderBridge
/// until the DirectRecorderBridge TS service-scope implementation is extracted/wrapped, attached, and parity checked.
/// </summary>
internal sealed record ServiceTripletContract(
    int NetworkId,
    int TransportStreamId,
    int ServiceId,
    int ChannelSpace,
    int ChannelIndex,
    string BonDriver);

internal sealed record ServiceRouteContract(
    string Group,
    string Tuner,
    string Did,
    string? BonDriverPath,
    string ServiceName,
    string ResolveSource,
    ServiceTripletContract ServiceTriplet);

internal static class CommonTsRouteBoundary
{
    public const string Rule = "v0.7.56_tvairepgrec_integrated_runtime_boundary";

    public static readonly string[] RequiredBeforeMode =
    [
        "BonDriver",
        "LoadLibrary",
        "CreateBonDriver",
        "OpenTuner",
        "SetChannel(chspace, chi)",
        "NID",
        "TSID",
        "SID",
        "PAT/PMT/SDT service confirmation",
        "service-scoped TS packets",
        "CloseTuner",
        "Release",
        "FreeLibrary",
        "stop-cooldown"
    ];

    public static readonly string[] ModeNames =
    [
        "record",
        "chain-record-execution-module-if-needed",
        "epg",
        "epg-check"
    ];

    public static readonly string[] ForbiddenInModeLayer =
    [
        "station-name partial matching",
        "NEXT string search",
        "EPG-only SID resolver as production logic",
        "separate same-TS recognition outside shared route"
    ];
}
