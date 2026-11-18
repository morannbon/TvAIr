namespace TvAIrEpgRec.CommonTsRoute;

/// <summary>
/// Tuner and service coordinates used by the common TS route's mode attachment.
/// The worker verifies physical service identity before consuming TS packets.
/// </summary>
internal sealed record ServiceTripletContract(
    int NetworkId,
    int TransportStreamId,
    int ServiceId,
    int ChannelSpace,
    int ChannelIndex,
    string BonDriver);
