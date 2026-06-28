using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace TvAIr.Tuner;

/// <summary>
/// v32.83(項目3): プロセス一覧から LIVE視聴中の TVTest.exe を検出し、
/// 使用中の (BonDriverファイル名, DID) ペアを返す。
///
/// 判定条件:
///   - プロセス名 = TVTest.exe
///   - コマンドラインに /rec を含まない（録画モードでない）
///   - コマンドラインから BonDriver と LogicalTunerIdentity を抽出
///
/// プロセスのコマンドライン取得には WMI(Win32_Process) を使う。
/// .NET 標準の Process クラスではコマンドラインを取得できないため。
/// </summary>
public static class LiveTvTestDetector
{
    // /d の値から BonDriver DLL ファイル名を抽出
    private static readonly Regex BonDriverRegex = new(
        @"/d\s+""?([^""\s]+\.dll)""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // /DID の値から LogicalTunerIdentity を抽出
    private static readonly Regex DidRegex = new(
        @"/DID\s+(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // /rec の有無（録画モード判定）
    private static readonly Regex RecRegex = new(
        @"\s/rec(\s|$|file|duration|delay|exit)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// LIVE視聴中のTVTestが使用している (BonDriverファイル名, DID) ペアの集合を返す。
    /// 取得失敗・WMI例外時は空集合を返す（フェイルセーフ）。
    /// </summary>
    public static HashSet<(string BonDriverFileName, string Did)> Detect()
    {
        var result = new HashSet<(string, string)>(LiveTuneKeyComparer.Instance);

        if (!OperatingSystem.IsWindows()) return result;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='TVTest.exe'");
            using var results = searcher.Get();

            foreach (var mo in results)
            {
                using (mo)
                {
                    var cmdLine = mo["CommandLine"] as string;
                    if (string.IsNullOrWhiteSpace(cmdLine)) continue;

                    // /rec を含む = 録画モード（EPG取得・予約録画）→ 除外しない
                    if (RecRegex.IsMatch(cmdLine)) continue;

                    var bonMatch = BonDriverRegex.Match(cmdLine);
                    if (!bonMatch.Success) continue;
                    var bonName = System.IO.Path.GetFileName(bonMatch.Groups[1].Value);

                    var didMatch = DidRegex.Match(cmdLine);
                    var did = didMatch.Success ? didMatch.Groups[1].Value : "";

                    result.Add((bonName, did));
                }
            }
        }
        catch
        {
            // WMI例外は無視（LIVE視聴中検出失敗してもEPG取得は継続できる方が良い）
        }

        return result;
    }

    /// <summary>大文字小文字を無視した (BonDriver, DID) 比較</summary>
    private sealed class LiveTuneKeyComparer : IEqualityComparer<(string BonDriverFileName, string Did)>
    {
        public static readonly LiveTuneKeyComparer Instance = new();

        public bool Equals((string BonDriverFileName, string Did) x, (string BonDriverFileName, string Did) y)
            => string.Equals(x.BonDriverFileName, y.BonDriverFileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Did,               y.Did,               StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string BonDriverFileName, string Did) obj)
            => HashCode.Combine(
                obj.BonDriverFileName?.ToUpperInvariant() ?? "",
                obj.Did?.ToUpperInvariant() ?? "");
    }
}
