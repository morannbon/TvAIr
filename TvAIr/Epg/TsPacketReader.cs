namespace TvAIr.Epg;

internal readonly record struct TsPacketView(
    int Pid,
    bool PayloadUnitStart,
    int ContinuityCounter,
    bool HasPayload,
    bool Scrambled,
    int PayloadOffset,
    int PayloadLength);

internal static class TsPacketReader
{
    public const int PacketSize = 188;

    public static bool TryRead(ReadOnlySpan<byte> packet, out TsPacketView view)
    {
        view = default;
        if (packet.Length != PacketSize || packet[0] != 0x47) return false;

        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var adaptationControl = (packet[3] >> 4) & 0x03;
        var hasPayload = (adaptationControl & 0x01) != 0;
        if (!hasPayload)
        {
            view = new TsPacketView(pid, (packet[1] & 0x40) != 0, packet[3] & 0x0F, false, false, PacketSize, 0);
            return true;
        }

        var offset = 4;
        if ((adaptationControl & 0x02) != 0)
        {
            if (offset >= PacketSize) return false;
            offset += 1 + packet[offset];
        }

        if (offset > PacketSize) return false;
        var scrambled = ((packet[3] >> 6) & 0x03) != 0;
        view = new TsPacketView(
            pid,
            (packet[1] & 0x40) != 0,
            packet[3] & 0x0F,
            hasPayload,
            scrambled,
            offset,
            PacketSize - offset);
        return true;
    }
}
