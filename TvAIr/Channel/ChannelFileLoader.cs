using System.Text;
using Microsoft.Extensions.Options;
using TvAIr.Core;

namespace TvAIr.Channel;

/// <summary>
/// ch2 + ChSet.txt を読み込みChannelTargetを構築する。
/// GR  : ChSet.txt BonDriverCh → 物理CH番号 → /ch {physCh}
/// BS/CS: ChSet.txt TsId → (Space, BonCh) → /chspace {Space} /chi {BonCh}
///        同一TS内に複数サービスがある場合のみ /sid を追加してONE/TWO/NEXT等を分離する。
///        DirectRecorder/EPG用のチャンネル同定は /chspace /chi /sid を基本とし、/nid /tsid /reccurservice は通常経路へ混ぜない。
/// </summary>
public sealed class ChannelFileLoader
{
    static ChannelFileLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly ChannelMapSettings baseSettings;
    private readonly IniSettingsService ini;
    private ChannelLoadCache? cache;
    private readonly object cacheLock = new();

    public ChannelFileLoader(IOptions<ChannelMapSettings> settings, IniSettingsService ini)
    {
        baseSettings = settings.Value;
        this.ini = ini;
    }

    public void Invalidate()
    {
        lock (cacheLock)
        {
            cache = null;
        }
    }

    /// <summary>
    /// ch2 + ChSet.txt を読み込む。
    /// release_contract: 起動時IOptions固定ではなく、TvAIr.iniの現在値を正とする。
    /// ch2/ChSet 4パスと各ファイルの更新時刻をcache keyに含め、設定変更・ファイル差し替え時は再読込する。
    /// </summary>
    public ChannelLoadResult Load()
    {
        var snapshot = BuildSnapshot();
        lock (cacheLock)
        {
            if (cache is not null && cache.Snapshot.Equals(snapshot)) return cache.Result;
        }

        var result = new ChannelLoadResult();
        var grChSet   = LoadGrChSet(result, snapshot);
        var bscsChSet = LoadBscsChSet(result, snapshot);

        var grPath = ResolveExistingCh2Path(snapshot.GrChannelFilePath, result);
        var bscsPath = ResolveExistingCh2Path(snapshot.BscsChannelFilePath, result);

        if (!string.IsNullOrWhiteSpace(grPath))
            ParseFile(grPath, "GR", grChSet, bscsChSet, result);
        if (!string.IsNullOrWhiteSpace(bscsPath))
            ParseFile(bscsPath, "BSCS", grChSet, bscsChSet, result);

        // ch2 記載順を維持しながら重複除去
        var seen = new HashSet<(string, ushort, ushort, ushort)>();
        var deduped = new List<ChannelTarget>();
        foreach (var t in result.Targets)
        {
            var key = (t.Group, t.OriginalNetworkId, t.TransportStreamId, t.ServiceId);
            if (seen.Add(key)) deduped.Add(t);
        }
        result.Targets = deduped;

        var gr = result.Targets.Count(x => x.Group == "GR");
        var bs = result.Targets.Count(x => x.Group == "BS");
        var cs = result.Targets.Count(x => x.Group == "CS");
        result.Message = result.Targets.Count > 0
            ? $"Loaded {result.Targets.Count} channels. GR={gr} BS={bs} CS={cs} (ch2順)"
            : "No valid channels loaded.";
        lock (cacheLock) { cache = new ChannelLoadCache(snapshot, result); }
        return result;
    }

    private ChannelSettingsSnapshot BuildSnapshot()
    {
        var grCh2 = ini.IsFirstRun ? baseSettings.GrChannelFilePath : ini.GrChannelFilePath;
        var grChSet = ini.IsFirstRun ? baseSettings.GrChSetFilePath : ini.GrChSetFilePath;
        var bscsCh2 = ini.IsFirstRun ? baseSettings.BscsChannelFilePath : ini.BscsChannelFilePath;
        var bscsChSet = ini.IsFirstRun ? baseSettings.BscsChSetFilePath : ini.BscsChSetFilePath;
        return new ChannelSettingsSnapshot(
            NormalizeKeyPath(grCh2),
            NormalizeKeyPath(grChSet),
            NormalizeKeyPath(bscsCh2),
            NormalizeKeyPath(bscsChSet),
            FileStamp(grCh2),
            FileStamp(grChSet),
            FileStamp(bscsCh2),
            FileStamp(bscsChSet));
    }

    private static string NormalizeKeyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim()); }
        catch { return path.Trim(); }
    }

    private static string FileStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var full = NormalizeKeyPath(path);
            return File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks.ToString() : "missing";
        }
        catch
        {
            return "unavailable";
        }
    }

    private sealed record ChannelLoadCache(ChannelSettingsSnapshot Snapshot, ChannelLoadResult Result);

    private sealed record ChannelSettingsSnapshot(
        string GrChannelFilePath,
        string GrChSetFilePath,
        string BscsChannelFilePath,
        string BscsChSetFilePath,
        string GrChannelFileStamp,
        string GrChSetFileStamp,
        string BscsChannelFileStamp,
        string BscsChSetFileStamp);

    private static string ResolveExistingCh2Path(string path, ChannelLoadResult result)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        // release_contract: ConfiguredCh2Path を正とし、特定環境名への自動rewriteは行わない。
        return File.Exists(path) ? TunerIsolationPolicy.ResolveConfiguredCh2Path(path) : path;
    }

    private Dictionary<int, ChSetChannelEntry> LoadGrChSet(ChannelLoadResult result, ChannelSettingsSnapshot settings)
    {
        var map = new Dictionary<int, ChSetChannelEntry>();
        var chSetPath = ResolveExplicitChSetPath(settings.GrChSetFilePath, "GR", result);
        if (string.IsNullOrWhiteSpace(chSetPath)) return map;

        using var r = OpenReader(chSetPath);
        while (r.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line[0] is ';' or '$') continue;
            var cols = line.Split('	', StringSplitOptions.TrimEntries);
            if (cols.Length < 3) continue;
            if (!int.TryParse(cols[1], out var space)) continue;
            if (!int.TryParse(cols[2], out var bonDriverChannel)) continue;
            var physicalChannel = TryReadFirstInteger(cols[0]);
            var ptxChannel = cols.Length >= 4 && int.TryParse(cols[3], out var ptx) ? ptx : (int?)null;

            // GRの .ch2 channel 列は TVTest/BonDriver のチャンネル番号として扱う。
            // 複数空間で同じ BonDriverChannel が重複する場合は、UHF(space=0)を優先し、既存値を上書きしない。
            if (!map.ContainsKey(bonDriverChannel) || space == 0)
            {
                map[bonDriverChannel] = new ChSetChannelEntry(
                    Space: space,
                    BonDriverChannel: bonDriverChannel,
                    PhysicalChannel: physicalChannel,
                    PtxChannel: ptxChannel,
                    TsId: null,
                    Line: line,
                    Path: chSetPath);
            }
        }
        result.Files.Add(Path.GetFileName(chSetPath));
        return map;
    }

    private Dictionary<ushort, ChSetChannelEntry> LoadBscsChSet(ChannelLoadResult result, ChannelSettingsSnapshot settings)
    {
        var map  = new Dictionary<ushort, ChSetChannelEntry>();
        var chSetPath = ResolveExplicitChSetPath(settings.BscsChSetFilePath, "BS/CS", result);
        if (string.IsNullOrWhiteSpace(chSetPath)) return map;

        using var r = OpenReader(chSetPath);
        while (r.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line[0] is ';' or '$') continue;
            var cols = line.Split('	', StringSplitOptions.TrimEntries);
            if (cols.Length < 5) continue;
            if (!int.TryParse(cols[1], out var space)) continue;
            if (!int.TryParse(cols[2], out var bonDriverChannel)) continue;
            if (!ushort.TryParse(cols[4], out var tsId) || tsId == 0) continue;
            if (!map.ContainsKey(tsId))
            {
                var physicalChannel = TryReadFirstInteger(cols[0]);
                var ptxChannel = cols.Length >= 4 && int.TryParse(cols[3], out var ptx) ? ptx : (int?)null;
                map[tsId] = new ChSetChannelEntry(
                    Space: space,
                    BonDriverChannel: bonDriverChannel,
                    PhysicalChannel: physicalChannel,
                    PtxChannel: ptxChannel,
                    TsId: tsId,
                    Line: line,
                    Path: chSetPath);
            }
        }
        result.Files.Add(Path.GetFileName(chSetPath));
        return map;
    }

    private static string? ResolveExplicitChSetPath(string configuredPath, string groupLabel, ChannelLoadResult result)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            result.Warnings.Add($"{groupLabel} ChSet not configured: source=explicit_settings");
            return null;
        }

        var path = configuredPath.Trim();
        if (!File.Exists(path))
        {
            result.Warnings.Add($"{groupLabel} ChSet not found: path={path} source=explicit_settings");
            return null;
        }

        if (!LooksLikeChSetFile(path))
        {
            result.Warnings.Add($"{groupLabel} ChSet invalid: path={path} source=explicit_settings");
            return null;
        }

        result.Warnings.Add($"{groupLabel} ChSet loaded: {Path.GetFileName(path)} source=explicit_settings");
        return path;
    }

    private static string? ResolveExistingPathCaseInsensitive(string path)
    {
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeChSetFile(string path)
    {
        try
        {
            using var r = OpenReader(path);
            var channelRows = 0;
            var spaceRows = 0;
            var read = 0;
            while (read++ < 300 && r.ReadLine() is { } raw)
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line[0] is ';') continue;
                if (line.StartsWith('$'))
                {
                    spaceRows++;
                    continue;
                }
                var cols = line.Split('	', StringSplitOptions.TrimEntries);
                if (cols.Length >= 3 && int.TryParse(cols[1], out _) && int.TryParse(cols[2], out _))
                    channelRows++;
                if (spaceRows > 0 && channelRows > 0) return true;
                if (channelRows >= 3) return true;
            }
            return channelRows > 0;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryReadFirstInteger(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }

    private static void ParseFile(string path, string fileGroup,
        Dictionary<int, ChSetChannelEntry> grChSet,
        Dictionary<ushort, ChSetChannelEntry> bscsChSet,
        ChannelLoadResult result)
    {
        if (!File.Exists(path)) { result.Warnings.Add($"ch2 not found: {path}"); return; }
        result.Files.Add(Path.GetFileName(path));
        using var r = OpenReader(path);
        var rawLines = new List<(int LineNumber, string Raw)>();
        var serviceCountByTs = new Dictionary<(ushort Onid, ushort Tsid), int>();
        var lineNumber = 0;

        while (r.ReadLine() is { } rawLine)
        {
            lineNumber++;
            rawLines.Add((lineNumber, rawLine));
            if (TryReadActiveServiceTriplet(rawLine, out var onid, out var tsid, out _))
            {
                var key = (onid, tsid);
                serviceCountByTs[key] = serviceCountByTs.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }

        foreach (var entry in rawLines)
        {
            var rawLine = entry.Raw;
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line) || line[0] is ';' or '#' or '[') continue;
            var cols = line.Split(',', StringSplitOptions.TrimEntries);
            if (cols.Length < 9) continue;
            if (TryParse(cols, fileGroup, Path.GetFileName(path), entry.LineNumber, grChSet, bscsChSet, serviceCountByTs, out var t))
                result.Targets.Add(t);
            else
                result.RawSkippedCount++;
        }
    }

    private static bool TryParse(string[] cols, string fileGroup, string ch2FileName, int ch2LineNumber,
        Dictionary<int, ChSetChannelEntry> grChSet,
        Dictionary<ushort, ChSetChannelEntry> bscsChSet,
        Dictionary<(ushort Onid, ushort Tsid), int> serviceCountByTs,
        out ChannelTarget target)
    {
        target = new ChannelTarget();
        var name = cols[0];
        if (string.IsNullOrWhiteSpace(name) || name is "－" or "-") return false;
        if (!int.TryParse(cols[4], out var svcType) || svcType is not (1 or 161 or 162 or 173)) return false;
        if (!ushort.TryParse(cols[5], out var sid)  || sid  == 0) return false;
        if (!ushort.TryParse(cols[6], out var onid) || onid == 0) return false;
        if (!ushort.TryParse(cols[7], out var tsId) || tsId == 0) return false;
        _ = int.TryParse(cols[8], out var state);
        if (state == 0) return false;
        _ = int.TryParse(cols[2], out var bonCh);
        var sameTsServiceCount = serviceCountByTs.TryGetValue((onid, tsId), out var count) ? count : 1;

        var group = ResolveGroup(fileGroup, onid);
        var arg   = BuildArg(group, bonCh, onid, tsId, sid, sameTsServiceCount, grChSet, bscsChSet,
            out var resolvedSpace, out var resolvedChannelIndex, out var buildSource);
        if (string.IsNullOrWhiteSpace(arg)) return false;

        target = new ChannelTarget
        {
            Group = group,
            ServiceId = sid,
            OriginalNetworkId = onid,
            TransportStreamId = tsId,
            Name = name,
            ChannelArgument = arg,
            Ch2FileName = ch2FileName,
            Ch2LineNumber = ch2LineNumber,
            BonDriverChannel = bonCh,
            ResolvedSpace = resolvedSpace,
            ResolvedChannelIndex = resolvedChannelIndex,
            SameTransportServiceCount = sameTsServiceCount,
            ChannelBuildSource = buildSource
        };
        return true;
    }

    private static string ResolveGroup(string fileGroup, ushort onid)
    {
        if (onid == 4) return "BS";
        if (onid is 6 or 7 or 10) return "CS";
        if (onid >= 0x7880) return "GR";
        return fileGroup;
    }

    private static string BuildArg(string group, int bonCh, ushort onid, ushort tsId, ushort sid, int sameTsServiceCount,
        Dictionary<int, ChSetChannelEntry> grChSet,
        Dictionary<ushort, ChSetChannelEntry> bscsChSet,
        out int resolvedSpace,
        out int resolvedChannelIndex,
        out string buildSource)
    {
        resolvedSpace = group == "GR" ? 0 : (group == "BS" ? 0 : 1);
        resolvedChannelIndex = bonCh;
        buildSource = "unset_before_explicit_chset";

        if (group == "GR")
        {
            resolvedSpace = 0;
            if (!grChSet.TryGetValue(bonCh, out var grEntry))
            {
                resolvedChannelIndex = -1;
                buildSource = "gr_chset_missing";
                return string.Empty;
            }
            resolvedChannelIndex = grEntry.BonDriverChannel;
            buildSource = "gr_chset_bondriver_channel";
            return $"/ch {resolvedChannelIndex}";
        }

        // BS/CSは release_contract 以前のTVTest通常録画ルートへ戻す。
        // ChSetのTSID→BonDriverチャンネルを /chi として渡すことで、TVTest側の録画名・復号ルートを維持する。
        // ただしフジテレビONE/TWO/NEXT等の同一TS内複数サービスは /chi だけでは既定サービスに潰れるため、
        // /sid だけを最小追加する。/nid /tsid /reccurservice は復号事故の原因になるため使わない。
        if (!bscsChSet.TryGetValue(tsId, out var bscsEntry))
        {
            resolvedSpace = -1;
            resolvedChannelIndex = -1;
            buildSource = "bscs_chset_missing";
            return string.Empty;
        }
        resolvedSpace = bscsEntry.Space;
        resolvedChannelIndex = bscsEntry.BonDriverChannel;
        buildSource = "bscs_chset_tsid";

        var arg = $"/chspace {resolvedSpace} /chi {resolvedChannelIndex}";
        return sameTsServiceCount > 1 ? $"{arg} /sid {sid}" : arg;
    }

    private static bool TryReadActiveServiceTriplet(string rawLine, out ushort onid, out ushort tsid, out ushort sid)
    {
        onid = tsid = sid = 0;
        var line = rawLine.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(line) || line[0] is ';' or '#' or '[') return false;
        var cols = line.Split(',', StringSplitOptions.TrimEntries);
        if (cols.Length < 9) return false;
        var name = cols[0];
        if (string.IsNullOrWhiteSpace(name) || name is "－" or "-") return false;
        if (!int.TryParse(cols[4], out var svcType) || svcType is not (1 or 161 or 162 or 173)) return false;
        if (!ushort.TryParse(cols[5], out sid) || sid == 0) return false;
        if (!ushort.TryParse(cols[6], out onid) || onid == 0) return false;
        if (!ushort.TryParse(cols[7], out tsid) || tsid == 0) return false;
        _ = int.TryParse(cols[8], out var state);
        return state != 0;
    }

    private static StreamReader OpenReader(string path)
    {
        var fs = File.OpenRead(path);
        return new StreamReader(fs, Encoding.GetEncoding(932), detectEncodingFromByteOrderMarks: true);
    }
}

public sealed record ChSetChannelEntry(
    int Space,
    int BonDriverChannel,
    int? PhysicalChannel,
    int? PtxChannel,
    ushort? TsId,
    string Line,
    string Path);

public sealed class ChannelLoadResult
{
    public List<ChannelTarget> Targets { get; set; } = new();
    public List<string> Files    { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public int RawSkippedCount   { get; set; }
    public string Message        { get; set; } = "";
}
