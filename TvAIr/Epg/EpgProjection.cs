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

    public static string GenreLabel(string? genre, string? genreCodes)
    {
        var explicitGenre = FirstNonEmpty(genre);
        if (explicitGenre.Length > 0) return explicitGenre;

        var first = string.IsNullOrWhiteSpace(genreCodes)
            ? string.Empty
            : genreCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToUpperInvariant() ?? string.Empty;
        if (first.StartsWith("0X", StringComparison.OrdinalIgnoreCase)) first = first[2..];
        if (first.Length == 0) return string.Empty;
        return first[0] switch
        {
            '0' => "ニュース/報道",
            '1' => "スポーツ",
            '2' => "情報/ワイドショー",
            '3' => "ドラマ",
            '4' => "音楽",
            '5' => "バラエティ",
            '6' => "映画",
            '7' => "アニメ/特撮",
            '8' => "ドキュメンタリー/教養",
            '9' => "劇場/公演",
            'A' => "趣味/教育",
            'B' => "福祉",
            _ => "その他"
        };
    }

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
