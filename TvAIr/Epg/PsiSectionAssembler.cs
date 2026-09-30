namespace TvAIr.Epg;

internal sealed class PsiSectionAssembler
{
    private sealed class PidState
    {
        public readonly List<byte> Buffer = new(4096);
        public int? LastContinuityCounter;
        public bool WaitingForPayloadUnitStart;
    }

    private readonly Dictionary<int, PidState> states = new();

    public int TransportErrorPacketCount { get; private set; }
    public int ContinuityDiscontinuityCount { get; private set; }
    public int DiscontinuityIndicatorResetCount { get; private set; }
    public int DuplicatePayloadPacketCount { get; private set; }
    public int ResyncWaitDropPacketCount { get; private set; }
    public int InvalidSectionLengthResetCount { get; private set; }
    public int InvalidPointerResetCount { get; private set; }

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> packet)
    {
        if (!TsPacketReader.TryRead(packet, out var ts) || !ts.HasPayload || ts.Scrambled || ts.Pid == 0x1FFF)
            return Array.Empty<byte[]>();

        if (!states.TryGetValue(ts.Pid, out var state))
        {
            state = new PidState();
            states[ts.Pid] = state;
        }

        var sections = new List<byte[]>();

        // TEI marks an uncorrectable transport packet. Never feed its bytes into a PSI
        // section. Lose section synchronization until the next PUSI instead of treating
        // the following continuation packet as a fresh section header.
        if (ts.TransportError)
        {
            TransportErrorPacketCount++;
            EnterResync(state, ts.ContinuityCounter);
            return sections;
        }

        // adaptation_field.discontinuity_indicator is an explicit stream-boundary signal.
        // It may occur without an unexpected continuity counter, so reset independently.
        if (ts.DiscontinuityIndicator)
        {
            DiscontinuityIndicatorResetCount++;
            state.Buffer.Clear();
            state.WaitingForPayloadUnitStart = !ts.PayloadUnitStart;
            state.LastContinuityCounter = null;
        }

        if (state.LastContinuityCounter.HasValue)
        {
            if (state.LastContinuityCounter.Value == ts.ContinuityCounter)
            {
                // Preserve the established duplicate-packet rule. A repeated payload CC is
                // ignored rather than merged twice into the PSI buffer.
                DuplicatePayloadPacketCount++;
                return sections;
            }

            var expected = (state.LastContinuityCounter.Value + 1) & 0x0F;
            if (ts.ContinuityCounter != expected)
            {
                ContinuityDiscontinuityCount++;
                state.Buffer.Clear();
                state.WaitingForPayloadUnitStart = !ts.PayloadUnitStart;
            }
        }

        state.LastContinuityCounter = ts.ContinuityCounter;

        // Once a packet was lost/corrupt, non-PUSI payload is only a continuation of an
        // unknown section. Do not reinterpret it as a section header. This is the critical
        // resynchronization invariant that prevents one transport discontinuity from
        // cascading into CRC/syntax failures across later EIT sections.
        if (state.WaitingForPayloadUnitStart && !ts.PayloadUnitStart)
        {
            ResyncWaitDropPacketCount++;
            return sections;
        }
        if (ts.PayloadUnitStart)
            state.WaitingForPayloadUnitStart = false;

        var payload = packet.Slice(ts.PayloadOffset, ts.PayloadLength);
        if (ts.PayloadUnitStart)
        {
            if (payload.Length == 0) return sections;
            var pointer = payload[0];
            var index = 1;

            if (pointer > 0)
            {
                if (index + pointer > payload.Length)
                {
                    InvalidPointerResetCount++;
                    state.Buffer.Clear();
                    state.WaitingForPayloadUnitStart = true;
                    return sections;
                }

                if (state.Buffer.Count > 0)
                {
                    Append(state.Buffer, payload.Slice(index, pointer));
                    if (!Drain(state.Buffer, sections))
                    {
                        InvalidSectionLengthResetCount++;
                        state.WaitingForPayloadUnitStart = true;
                        return sections;
                    }
                }
                index += pointer;
            }
            else
            {
                state.Buffer.Clear();
            }

            if (index < payload.Length)
            {
                state.Buffer.Clear();
                Append(state.Buffer, payload[index..]);
                if (!Drain(state.Buffer, sections))
                {
                    InvalidSectionLengthResetCount++;
                    state.WaitingForPayloadUnitStart = true;
                }
            }
        }
        else
        {
            Append(state.Buffer, payload);
            if (!Drain(state.Buffer, sections))
            {
                InvalidSectionLengthResetCount++;
                state.WaitingForPayloadUnitStart = true;
            }
        }

        return sections;
    }

    private static void EnterResync(PidState state, int continuityCounter)
    {
        state.Buffer.Clear();
        state.WaitingForPayloadUnitStart = true;
        state.LastContinuityCounter = continuityCounter;
    }

    private static void Append(List<byte> buffer, ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i < payload.Length; i++) buffer.Add(payload[i]);
    }

    // true: buffer remains section-aligned; false: section header itself is implausible,
    // so caller must wait for the next PUSI before consuming further continuation bytes.
    private static bool Drain(List<byte> buffer, List<byte[]> sections)
    {
        while (true)
        {
            while (buffer.Count > 0 && buffer[0] == 0xFF) buffer.RemoveAt(0);
            if (buffer.Count < 3) return true;

            var sectionLength = ((buffer[1] & 0x0F) << 8) | buffer[2];
            var totalLength = 3 + sectionLength;
            if (sectionLength <= 0 || totalLength > 4096)
            {
                buffer.Clear();
                return false;
            }

            if (buffer.Count < totalLength) return true;

            var section = buffer.GetRange(0, totalLength).ToArray();
            sections.Add(section);
            buffer.RemoveRange(0, totalLength);
        }
    }
}
