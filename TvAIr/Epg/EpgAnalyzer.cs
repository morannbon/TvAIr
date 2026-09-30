namespace TvAIr.Epg;

internal sealed class EpgAnalyzer
{
    private readonly int maxPackets;
    private readonly EitTransportStreamScope transportStreamScope;
    private readonly bool persistentSectionCache;

    public EpgAnalyzer(
        int maxPackets = 0,
        EitTransportStreamScope transportStreamScope = EitTransportStreamScope.ActualOnly,
        bool persistentSectionCache = false)
    {
        this.maxPackets = maxPackets > 0 ? maxPackets : int.MaxValue;
        this.transportStreamScope = transportStreamScope;
        this.persistentSectionCache = persistentSectionCache;
    }

    public async Task<EpgAnalyzeResult> AnalyzeAsync(string tsPath, CancellationToken ct = default)
    {
        var assembler = new PsiSectionAssembler();
        var eit = new EitSectionReader(transportStreamScope);
        var packet = new byte[TsPacketReader.PacketSize];
        var packetCount = 0;
        var syncErrors = 0;
        var sectionCount = 0;

        await using var stream = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        while (packetCount < maxPackets)
        {
            ct.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(packet.AsMemory(0, packet.Length), ct);
            if (read == 0) break;
            if (read != packet.Length) break;
            packetCount++;
            if (packet[0] != 0x47)
            {
                syncErrors++;
                continue;
            }

            if (!TsPacketReader.TryRead(packet, out var packetView) || packetView.Pid is not (0x12 or 0x26 or 0x27))
                continue;

            foreach (var section in assembler.Feed(packet))
            {
                sectionCount++;
                eit.TryRead(section);
            }
        }

        var cachedSectionCount = 0;
        var cacheReplayAccepted = 0;
        var currentSubtableCount = 0;
        IReadOnlyList<EpgCaptureSubtableVersion> currentCaptureSubtables = Array.Empty<EpgCaptureSubtableVersion>();
        IReadOnlyList<EpgPersistentCacheSubtable> persistentCacheSubtables = Array.Empty<EpgPersistentCacheSubtable>();
        IReadOnlyList<EpgSectionEventInventory> currentCaptureSectionInventories = Array.Empty<EpgSectionEventInventory>();
        IReadOnlyList<EpgSectionEventInventory> persistentCacheSectionInventories = Array.Empty<EpgSectionEventInventory>();
        IReadOnlyList<EpgEventObservation> currentCaptureEventObservations = Array.Empty<EpgEventObservation>();
        IReadOnlyList<EpgEventObservation> persistentCacheEventObservations = Array.Empty<EpgEventObservation>();
        IReadOnlyList<EpgPreviousVersionCacheSubtable> previousVersionCacheSubtables = Array.Empty<EpgPreviousVersionCacheSubtable>();
        IReadOnlyList<EpgEventObservation> previousVersionCacheEventObservations = Array.Empty<EpgEventObservation>();
        if (persistentSectionCache)
        {
            var persistentCache = new PersistentEitSectionCache();
            var currentVersions = eit.GetCaptureSnapshotVersions();
            currentSubtableCount = currentVersions.Count;

            // Keep only the immediately previous cached Basic EIT version as a narrow
            // descriptor donor source.  It is never replayed into the current reader;
            // consumers may use an event only when broadcast identity, start, duration,
            // expected Basic table and a single raw short descriptor all match exactly.
            var previousVersionSections = persistentCache.LoadPreviousVersionSections(currentVersions);
            if (previousVersionSections.Count > 0)
            {
                var previousVersionProbe = new EitSectionReader(transportStreamScope);
                foreach (var cachedSection in previousVersionSections) previousVersionProbe.TryRead(cachedSection.Section);
                previousVersionCacheEventObservations = previousVersionProbe.BuildEventObservations();
            }

#if TVAIR_DEVELOPER_DIAGNOSTICS
            currentCaptureSubtables = currentVersions
                .Select(static v => new EpgCaptureSubtableVersion(v.NetworkId, v.TransportStreamId, v.ServiceId, v.TableId, v.VersionNumber))
                .ToArray();
            currentCaptureSectionInventories = eit.GetAcceptedScheduleSections()
                .Select(static section => BuildSectionInventory(section.Section))
                .Where(static inventory => inventory is not null)
                .Select(static inventory => inventory!)
                .ToArray();
            currentCaptureEventObservations = eit.BuildEventObservations();
            previousVersionCacheSubtables = previousVersionSections
                .GroupBy(static section => (
                    section.NetworkId, section.TransportStreamId, section.ServiceId, section.TableId,
                    section.CurrentVersionNumber, section.PreviousVersionNumber))
                .Select(static g => new EpgPreviousVersionCacheSubtable(
                    g.Key.NetworkId, g.Key.TransportStreamId, g.Key.ServiceId, g.Key.TableId,
                    g.Key.CurrentVersionNumber, g.Key.PreviousVersionNumber, g.Count()))
                .ToArray();
#endif
            var cachedSections = persistentCache.LoadMatchingSections(currentVersions);
            cachedSectionCount = cachedSections.Count;
#if TVAIR_DEVELOPER_DIAGNOSTICS
            persistentCacheSubtables = cachedSections
                .Where(static section => section.Length > 11)
                .GroupBy(static section => (
                    NetworkId: U16(section, 10),
                    TransportStreamId: U16(section, 8),
                    ServiceId: U16(section, 3),
                    TableId: section[0],
                    VersionNumber: (byte)((section[5] >> 1) & 0x1F)))
                .Select(static g => new EpgPersistentCacheSubtable(g.Key.NetworkId, g.Key.TransportStreamId, g.Key.ServiceId, g.Key.TableId, g.Key.VersionNumber, g.Count()))
                .ToArray();
            persistentCacheSectionInventories = cachedSections
                .Select(static section => BuildSectionInventory(section))
                .Where(static inventory => inventory is not null)
                .Select(static inventory => inventory!)
                .ToArray();
            if (cachedSections.Count > 0)
            {
                var cacheProbe = new EitSectionReader(transportStreamScope);
                foreach (var cachedSection in cachedSections) cacheProbe.TryRead(cachedSection);
                persistentCacheEventObservations = cacheProbe.BuildEventObservations();
            }
#endif
            foreach (var cachedSection in cachedSections)
            {
                var cachedIdentity = new EitSectionReader.EitCachedSection(
                    U16(cachedSection, 10), U16(cachedSection, 8), U16(cachedSection, 3), cachedSection[0],
                    (byte)((cachedSection[5] >> 1) & 0x1F), cachedSection[6], cachedSection);
                if (eit.HasAcceptedSection(cachedIdentity)) continue;

                var before = eit.EitSectionCount;
                eit.TryRead(cachedSection);
                if (eit.EitSectionCount > before) cacheReplayAccepted++;
            }
            persistentCache.MergeAndSave(eit.GetAcceptedScheduleSections());
        }

        var sectionStatuses = eit.BuildSectionStatuses();
        var eventObservations = eit.BuildEventObservations();
        var accumulatorAudits = eit.BuildAccumulatorAudits();
        var events = eit.BuildEvents();

        return new EpgAnalyzeResult(
            packetCount,
            syncErrors,
            assembler.TransportErrorPacketCount,
            assembler.ContinuityDiscontinuityCount,
            assembler.DiscontinuityIndicatorResetCount,
            assembler.DuplicatePayloadPacketCount,
            assembler.ResyncWaitDropPacketCount,
            assembler.InvalidSectionLengthResetCount,
            assembler.InvalidPointerResetCount,
            sectionCount,
            eit.EitSectionCount,
            eit.ShortEventDescriptorCount,
            eit.DecodeAttemptCount,
            eit.ExtendedWithoutShortCount,
            eit.DescriptorRecoveryCount,
            eit.RawSectionShortResolverCandidates,
            eit.RawSectionShortResolverMerged,
            eit.RawSectionShortResolverUnresolved,
            eit.CommonEventCount,
            eit.CommonResolvedCount,
            eit.CommonUnresolvedCount,
            eit.RejectedEventHeaderCount,
            eit.RejectedBasicScheduleEventHeaderCount,
            eit.IgnoredOtherTransportStreamEitSectionCount,
            eit.InvalidEitSectionCount,
            eit.IgnoredNonCurrentEitSectionCount,
            eit.IgnoredDuplicateEitSectionCount,
            eit.IgnoredVersionSwitchEitSectionCount,
            eit.IgnoredBasicScheduleVersionSwitchEitSectionCount,
            eit.InvalidSyntaxOrLengthEitSectionCount,
            eit.InvalidCrcEitSectionCount,
            eit.InvalidHeaderConsistencyEitSectionCount,
            eit.ToleratedSameVersionScheduleMetadataDriftCount,
            cachedSectionCount,
            cacheReplayAccepted,
            currentSubtableCount,
            currentCaptureSubtables,
            persistentCacheSubtables,
            currentCaptureSectionInventories,
            persistentCacheSectionInventories,
            currentCaptureEventObservations,
            persistentCacheEventObservations,
            previousVersionCacheSubtables,
            previousVersionCacheEventObservations,
            eit.RejectedEventHeaders.ToArray(),
            eit.TitleDecodes.ToArray(),
            sectionStatuses,
            eventObservations,
            accumulatorAudits,
            events);
    }

    private static EpgSectionEventInventory? BuildSectionInventory(byte[] section)
    {
        if (section.Length < 18) return null;
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var sectionEnd = 3 + sectionLength;
        if (sectionEnd > section.Length || sectionEnd < 18) return null;
        var dataEnd = sectionEnd - 4;
        var eventIds = new List<ushort>();
        var pos = 14;
        while (pos + 12 <= dataEnd)
        {
            eventIds.Add(U16(section, pos));
            var descriptorLoopLength = ((section[pos + 10] & 0x0F) << 8) | section[pos + 11];
            var next = pos + 12 + descriptorLoopLength;
            if (next <= pos || next > dataEnd) break;
            pos = next;
        }

        return new EpgSectionEventInventory(
            U16(section, 10), U16(section, 8), U16(section, 3), section[0],
            (byte)((section[5] >> 1) & 0x1F), section[6], eventIds);
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
