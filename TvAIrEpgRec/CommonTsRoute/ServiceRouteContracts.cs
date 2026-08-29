namespace TvAIrEpgRec.CommonTsRoute;

/// <summary>
/// release_contract shared route contract boundary.
/// TvAIrEpgRec is the sole execution owner for recording, EPG acquisition, and pre-record EPG-check.
/// All modes use this common tuner/service identity route before mode-specific processing.
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
    public const string Rule = "release_contract";

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
        "FreeLibrary"
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
