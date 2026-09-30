namespace TvAIr.Epg;

/// <summary>
/// ARIB EIT table-id family contract used by capture, section tracking, stale authority and
/// other-TS supplementation. Keep table-family semantics here so parser/import paths cannot
/// silently diverge when EPG handling is changed.
/// </summary>
internal static class EitTableContract
{
    public const byte PresentFollowingActual = 0x4E;
    public const byte PresentFollowingOther = 0x4F;
    public const byte ActualBasicFirst = 0x50;
    public const byte ActualBasicLast = 0x57;
    public const byte ActualExtendedFirst = 0x58;
    public const byte ActualExtendedLast = 0x5F;
    public const byte OtherBasicFirst = 0x60;
    public const byte OtherBasicLast = 0x67;
    public const byte OtherExtendedFirst = 0x68;
    public const byte OtherExtendedLast = 0x6F;

    public static bool IsEit(byte tableId)
        => tableId is >= PresentFollowingActual and <= OtherExtendedLast;

    public static bool IsPresentFollowing(byte tableId)
        => tableId is PresentFollowingActual or PresentFollowingOther;

    public static bool IsActualSchedule(byte tableId)
        => tableId is >= ActualBasicFirst and <= ActualExtendedLast;

    public static bool IsOtherSchedule(byte tableId)
        => tableId is >= OtherBasicFirst and <= OtherExtendedLast;

    public static bool IsSchedule(byte tableId)
        => IsActualSchedule(tableId) || IsOtherSchedule(tableId);

    public static bool IsActualBasicSchedule(byte tableId)
        => tableId is >= ActualBasicFirst and <= ActualBasicLast;

    public static bool IsActualExtendedSchedule(byte tableId)
        => tableId is >= ActualExtendedFirst and <= ActualExtendedLast;

    public static bool IsOtherBasicSchedule(byte tableId)
        => tableId is >= OtherBasicFirst and <= OtherBasicLast;

    public static bool IsOtherExtendedSchedule(byte tableId)
        => tableId is >= OtherExtendedFirst and <= OtherExtendedLast;

    public static bool IsBasicSchedule(byte tableId)
        => IsActualBasicSchedule(tableId) || IsOtherBasicSchedule(tableId);

    public static bool IsExtendedSchedule(byte tableId)
        => IsActualExtendedSchedule(tableId) || IsOtherExtendedSchedule(tableId);

    public static bool IsOtherTransportStream(byte tableId)
        => tableId == PresentFollowingOther || IsOtherSchedule(tableId);

    public static bool IsSameScheduleFamily(byte tableId, byte lastTableId)
        => (IsActualSchedule(tableId) && IsActualSchedule(lastTableId))
            || (IsOtherSchedule(tableId) && IsOtherSchedule(lastTableId));
}
