namespace TvAIr.Epg;

internal sealed record EpgAnalyzeResult(
    int PacketCount,
    int SyncErrors,
    int SectionCount,
    int EitSectionCount,
    int ShortEventDescriptorCount,
    int DecodeAttemptCount,
    int ExtendedWithoutShortCount,
    int DescriptorRecoveryCount,
    int RawSectionShortResolverCandidates,
    int RawSectionShortResolverMerged,
    int RawSectionShortResolverUnresolved,
    IReadOnlyList<EpgTitleDecode> TitleDecodes,
    IReadOnlyList<EpgSectionStatus> SectionStatuses,
    IReadOnlyList<EpgEventObservation> EventObservations,
    IReadOnlyList<EpgEventAccumulatorAudit> EventAccumulatorAudits,
    IReadOnlyList<ParsedEpgEvent> Events)
{
    public string StatsLine =>
        $"packets={PacketCount} syncErrors={SyncErrors} sections={SectionCount} eitSections={EitSectionCount} shortEventDescriptors={ShortEventDescriptorCount} decodeAttempts={DecodeAttemptCount} extendedWithoutShort={ExtendedWithoutShortCount} descriptorRecovery={DescriptorRecoveryCount} rawSectionShortResolverCandidates={RawSectionShortResolverCandidates} rawSectionShortResolverMerged={RawSectionShortResolverMerged} rawSectionShortResolverUnresolved={RawSectionShortResolverUnresolved} titleDecodes={TitleDecodes.Count} sectionStatus={SectionStatuses.Count} observations={EventObservations.Count} accumulators={EventAccumulatorAudits.Count} events={Events.Count}";
}

internal sealed record EpgTitleDecode(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    byte TableId,
    byte SectionNumber,
    byte LastSectionNumber,
    int DescriptorLoopLength,
    int DescriptorOffset,
    int DescriptorLength,
    string BoundaryStatus,
    string Iso639LanguageCode,
    int EventNameLength,
    int EventNameBytesLength,
    string EventNameBytesHex,
    string DecodeRoute,
    string DecodeStatus,
    string DecodedTitle,
    int DecodedTitleLength,
    int TextLength,
    int TextBytesLength,
    string TextBytesHexHead,
    string DecodedTextHead,
    string EmptyReason);

internal sealed record EpgSectionStatus(
    ushort ServiceId,
    byte TableId,
    byte LastSectionNumber,
    int SeenSectionCount,
    int ExpectedSectionCount,
    int SegmentSeenTotal,
    int SegmentExpectedTotal,
    IReadOnlyList<byte> MissingSegments);



internal sealed record EpgEventObservation(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    byte TableId,
    byte SectionNumber,
    byte LastSectionNumber,
    byte VersionNumber,
    int SectionLength,
    int DataEndOffset,
    int EventOffset,
    int DescriptorLoopLength,
    int DescriptorStartOffset,
    int DescriptorEndOffset,
    int NextEventOffset,
    DateTime Start,
    int DurationSeconds,
    string RawDescriptorLoopHex,
    string RawShortEventDescriptorHex,
    string RawExtendedEventDescriptorHex,
    string RawContentDescriptorHex,
    string EventHeaderHex,
    string PreviousEventTailHex,
    string DescriptorLoopHeadHex,
    string DescriptorLoopTailHex,
    string NextEventHeaderHex,
    string EventRawWindowHex,
    int ObservationIndex)
{
    public DateTime End => Start == DateTime.MinValue || DurationSeconds <= 0
        ? DateTime.MinValue
        : Start.AddSeconds(DurationSeconds);
}

internal sealed record EpgEventAccumulatorAudit(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    DateTime Start,
    int DurationSeconds,
    byte BestTableId,
    byte SectionNumber,
    int ObservationCount,
    int ShortDescriptorObservationCount,
    int ExtendedDescriptorObservationCount,
    string SourceTables,
    string ShortSourceTables,
    string ExtendedSourceTables,
    bool HasRawShort,
    bool HasRawExtended,
    bool HasScheduleTitleCarrier,
    bool HasScheduleBodyCarrier,
    bool HasExpectedScheduleTitleBodyPair,
    int RawShortBytes,
    int RawExtendedBytes);

internal sealed record ParsedEpgEvent(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    byte BestTableId,
    DateTime Start,
    int DurationSeconds,
    string Title,
    string Description,
    string GenreCodes,
    string TitleDecodeRoute,
    string TitleDecodeStatus,
    string BoundaryStatus,
    byte SectionNumber,
    byte VersionNumber,
    string RawDescriptorLoopHex,
    string RawShortEventDescriptorHex,
    string RawExtendedEventDescriptorHex,
    string RawContentDescriptorHex)
{
    public string ServiceName => string.Empty;

    public DateTime End => Start == DateTime.MinValue || DurationSeconds <= 0
        ? DateTime.MinValue
        : Start.AddSeconds(DurationSeconds);
}
