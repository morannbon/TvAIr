namespace TvAIr.Epg;

internal sealed class PsiSectionAssembler
{
    private sealed class PidState
    {
        public readonly List<byte> Buffer = new(4096);
        public int? LastContinuityCounter;
    }

    private readonly Dictionary<int, PidState> states = new();

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> packet)
    {
        if (!TsPacketReader.TryRead(packet, out var ts) || !ts.HasPayload || ts.Scrambled || ts.Pid == 0x1FFF)
            return Array.Empty<byte[]>();

        var payload = packet.Slice(ts.PayloadOffset, ts.PayloadLength);
        if (!states.TryGetValue(ts.Pid, out var state))
        {
            state = new PidState();
            states[ts.Pid] = state;
        }

        var sections = new List<byte[]>();
        var duplicate = state.LastContinuityCounter == ts.ContinuityCounter;
        var discontinuity = false;
        if (state.LastContinuityCounter.HasValue)
        {
            var expected = (state.LastContinuityCounter.Value + 1) & 0x0F;
            discontinuity = ts.ContinuityCounter != expected && !duplicate;
        }
        state.LastContinuityCounter = ts.ContinuityCounter;
        if (duplicate) return sections;

        if (discontinuity)
        {
            state.Buffer.Clear();
            if (!ts.PayloadUnitStart) return sections;
        }

        if (ts.PayloadUnitStart)
        {
            if (payload.Length == 0) return sections;
            var pointer = payload[0];
            var index = 1;

            if (pointer > 0)
            {
                if (index + pointer > payload.Length)
                {
                    state.Buffer.Clear();
                    return sections;
                }

                if (state.Buffer.Count > 0)
                {
                    Append(state.Buffer, payload.Slice(index, pointer));
                    Drain(state.Buffer, sections);
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
                Drain(state.Buffer, sections);
            }
        }
        else
        {
            Append(state.Buffer, payload);
            Drain(state.Buffer, sections);
        }

        return sections;
    }

    private static void Append(List<byte> buffer, ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i < payload.Length; i++) buffer.Add(payload[i]);
    }

    private static void Drain(List<byte> buffer, List<byte[]> sections)
    {
        while (true)
        {
            while (buffer.Count > 0 && buffer[0] == 0xFF) buffer.RemoveAt(0);
            if (buffer.Count < 3) return;

            var sectionLength = ((buffer[1] & 0x0F) << 8) | buffer[2];
            var totalLength = 3 + sectionLength;
            if (sectionLength <= 0 || totalLength > 4096)
            {
                buffer.Clear();
                return;
            }

            if (buffer.Count < totalLength) return;

            var section = buffer.GetRange(0, totalLength).ToArray();
            sections.Add(section);
            buffer.RemoveRange(0, totalLength);
        }
    }
}
