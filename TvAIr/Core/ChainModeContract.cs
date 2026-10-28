namespace TvAIr.Core;

public static class ChainModeContract
{
    public const string Disabled="disabled", Manual="manual", Automatic="automatic";
    public static string Normalize(string? value,bool legacyEnabled)
        => string.IsNullOrWhiteSpace(value) ? (legacyEnabled?Manual:Disabled)
        : value.Trim().ToLowerInvariant() switch
        { Disabled=>Disabled,Manual=>Manual,Automatic=>Automatic,
          _=>throw new ArgumentException("チェーン予約の方式が不正です。") };
    // A legacy client may edit the old boolean on a current DTO. An explicit changed
    // mode wins; an unchanged mode plus changed legacy flag is a legacy edit.
    public static string Requested(string? value,bool legacyEnabled,string current)
    {
        if(string.IsNullOrWhiteSpace(value) && legacyEnabled==(current!=Disabled)) return current;
        var mode=Normalize(value,legacyEnabled);
        return mode==current && legacyEnabled!=(current!=Disabled)
            ? Normalize(null,legacyEnabled) : mode;
    }
}
