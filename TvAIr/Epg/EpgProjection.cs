using TvAIr.Core;

namespace TvAIr.Epg;

/// <summary>
/// EPG raw/decoded storeから表示・検索向けの投影値を作る境界。
/// DBには取得データと一次展開値だけを置き、UI色・表示判断・予約状態はここより外側で決める。
/// </summary>
public static class EpgProjection
{
    public static string Title(EpgEvent e)
        => ProgramGuideCellTextDecoder.Decode(e).Title;

    public static string ShortText(EpgEvent e)
        => ProgramGuideCellTextDecoder.Decode(e).Outline;

    public static string ExtendedText(EpgEvent e)
    {
        var cell = ProgramGuideCellTextDecoder.Decode(e);
        return FirstNonEmpty(cell.Detail, cell.Items);
    }

    public static string GenreCodes(EpgEvent e)
        => FirstNonEmpty(e.GenreCodes);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            var t = (v ?? string.Empty).Trim();
            if (t.Length > 0) return t;
        }
        return string.Empty;
    }
}
