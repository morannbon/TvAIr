namespace TvAIr.Epg;

internal sealed class EpgAnalyzer
{
    private readonly int maxPackets;

    public EpgAnalyzer(int maxPackets = 0)
    {
        this.maxPackets = maxPackets > 0 ? maxPackets : int.MaxValue;
    }

    public async Task<EpgAnalyzeResult> AnalyzeAsync(string tsPath, CancellationToken ct = default)
    {
        var assembler = new PsiSectionAssembler();
        var eit = new EitSectionReader();
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

        var sectionStatuses = eit.BuildSectionStatuses();
        var eventObservations = eit.BuildEventObservations();
        var accumulatorAudits = eit.BuildAccumulatorAudits();
        var events = eit.BuildEvents();

        return new EpgAnalyzeResult(
            packetCount,
            syncErrors,
            sectionCount,
            eit.EitSectionCount,
            eit.ShortEventDescriptorCount,
            eit.DecodeAttemptCount,
            eit.ExtendedWithoutShortCount,
            eit.DescriptorRecoveryCount,
            eit.RawSectionShortResolverCandidates,
            eit.RawSectionShortResolverMerged,
            eit.RawSectionShortResolverUnresolved,
            eit.TitleDecodes.ToArray(),
            sectionStatuses,
            eventObservations,
            accumulatorAudits,
            events);
    }
}
