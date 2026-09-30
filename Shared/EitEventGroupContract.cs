namespace TvAIr.Epg.Shared;

internal readonly record struct EitEventReference(ushort ServiceId, ushort EventId);

internal sealed record EitEventGroupInfo(byte GroupType, IReadOnlyList<EitEventReference> References)
{
    // ARIB event_group_descriptor: group_type=0x01 denotes common/shared event relation.
    public bool IsCommon => GroupType == 0x01;
}

internal static class EitEventGroupContract
{
    public const byte DescriptorTag = 0xD6;
    public const byte CommonGroupType = 0x01;

    public static bool TryParse(ReadOnlySpan<byte> descriptorBody, out EitEventGroupInfo info)
    {
        info = new EitEventGroupInfo(0, Array.Empty<EitEventReference>());
        if (descriptorBody.Length < 1) return false;

        var groupType = (byte)((descriptorBody[0] >> 4) & 0x0F);
        var eventCount = descriptorBody[0] & 0x0F;
        var required = 1 + eventCount * 4;
        if (descriptorBody.Length < required) return false;

        var refs = new List<EitEventReference>(eventCount);
        var offset = 1;
        for (var i = 0; i < eventCount; i++, offset += 4)
        {
            var sid = (ushort)((descriptorBody[offset] << 8) | descriptorBody[offset + 1]);
            var eid = (ushort)((descriptorBody[offset + 2] << 8) | descriptorBody[offset + 3]);
            refs.Add(new EitEventReference(sid, eid));
        }

        info = new EitEventGroupInfo(groupType, refs);
        return true;
    }
}
