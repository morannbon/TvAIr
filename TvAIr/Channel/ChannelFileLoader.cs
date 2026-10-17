using System.Text;
using Microsoft.Extensions.Options;
using TvAIr.Core;

namespace TvAIr.Channel;

/// <summary>
/// ch2の局情報と任意のChSet選局情報を読み込み、旧経路のChannelTargetとBonDriver固有の経路カタログを構築する。
/// GR  : ChSet.txt BonDriverCh → 物理CH番号 → /ch {physCh}
/// BS/CS: ChSet.txt TsId → (Space, BonCh) → /chspace {Space} /chi {BonCh}
///        同一TS内に複数サービスがある場合のみ /sid を追加してONE/TWO/NEXT等を分離する。
///        RecordingWorker/EPG用のチャンネル同定は /chspace /chi /sid を基本とし、/nid /tsid /reccurservice は通常経路へ混ぜない。
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

    public IReadOnlyList<ChannelTarget> GetDisplayTargets() => new ChannelServiceAccessPolicy(this).Capture().DisplayTargets;

    public void Invalidate()
    {
        lock (cacheLock)
        {
            cache = null;
        }
    }

    internal IReadOnlyList<TunerProfileDto> CaptureConfiguredTuners()
        => ini.GetEffectiveTuners().Select(t => new TunerProfileDto
        {
            Name = t.Name, BonDriverFileName = t.BonDriverFileName, ChannelCh2Path = t.ChannelCh2Path,
            Group = t.Group, Role = t.Role, Did = t.Did, DeviceNumber = t.DeviceNumber,
            LogicalViewerSlotId = t.LogicalViewerSlotId
        }).ToArray();

    /// <summary>
    /// ch2・任意のChSet.txt・設定BonDriverに結び付いたch2経路を読み込む。
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
        // The same snapshot that guards legacy channels also owns all explicitly
        // configured BonDriver channel tables. Never publish a cached route table
        // after a tuner setting or its .ch2 contents change.
        try
        {
            result.BonDriverRoutes = BonDriverChannelRouteCatalog.FromConfiguredTuners(ini.GetEffectiveTuners());
            result.Warnings.AddRange(result.BonDriverRoutes.SourceErrors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            // A failed direct source must never fall back to another driver's route.
            // Legacy ChSet channels remain independently available.
            result.Warnings.Add($"BonDriver channel source unavailable: {ex.GetType().Name}");
        }
        var grChSet   = LoadGrChSet(result, snapshot);
        var bscsChSet = LoadBscsChSet(result, snapshot);

        var grPath = ResolveExistingCh2Path(snapshot.GrChannelFilePath, result);
        var bscsPath = ResolveExistingCh2Path(snapshot.BscsChannelFilePath, result);

        if (!string.IsNullOrWhiteSpace(grPath))
            ParseFile(grPath, "GR", grChSet, bscsChSet, result);
        if (!string.IsNullOrWhiteSpace(bscsPath))
            ParseFile(bscsPath, "BSCS", grChSet, bscsChSet, result);

        // Explicit BonDriver tables are independent inputs. Keep their service
        // inventory separate from legacy ActiveTargets: direct tuning requires
        // a real tuner lease, so making these globally routable would allow
        // the wrong BonDriver to receive a legacy channel number.
        if (result.BonDriverRoutes is { } directCatalog)
        {
            result.DirectSourceServices.AddRange(directCatalog.SourceRoutesInFileOrder);
            result.Files.AddRange(directCatalog.SourceRoutesInFileOrder.Select(r => Path.GetFileName(r.SourceCh2Path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !result.Files.Contains(f)));
        }

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
        result.Message = result.Targets.Count > 0 || result.DirectSourceServices.Count > 0
            ? $"Loaded legacy={result.Targets.Count} direct={result.DirectSourceServices.Count} channels. GR={gr} BS={bs} CS={cs} (ch2順)"
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
            FileStamp(bscsChSet),
            BuildDirectRouteSnapshot(ini.GetEffectiveTuners()));
    }

    // Include tuner identity AND the corresponding channel-table revision in the
    // cache key. The order is stable and changes to the tuner set also invalidate.
    private static string BuildDirectRouteSnapshot(IEnumerable<TunerProfileDto> tuners)
    {
        return string.Join("\n", tuners
            .Select(t => (Driver: (t.BonDriverFileName ?? string.Empty).Trim(),
                          Path: NormalizeKeyPath(t.ChannelCh2Path)))
            .OrderBy(t => t.Driver, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"{t.Driver}\t{t.Path}\t{FileStamp(t.Path)}"));
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
        string BscsChSetFileStamp,
        string DirectRouteSnapshot);

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
            // ChSet is optional: automatic BonDriver/ch2 routes remain independent.
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
            var cols = ParseCh2Columns(line);
            if (cols.Length < 9) continue;
            if (!TryReadServiceState(cols, fileGroup, Path.GetFileName(path), entry.LineNumber, out var serviceState))
            {
                result.RawSkippedCount++;
                continue;
            }

            result.ServiceStates.Add(serviceState);
            if (TryParse(cols, fileGroup, Path.GetFileName(path), entry.LineNumber, grChSet, bscsChSet, serviceCountByTs, out var t))
            {
                result.Targets.Add(t);
                serviceState.IsRoutable = true;
            }
            else
            {
                result.RawSkippedCount++;
            }
        }
    }

    internal static bool TryReadServiceState(string[] cols, string fileGroup, string ch2FileName, int ch2LineNumber,
        out ChannelServiceState serviceState)
    {
        serviceState = new ChannelServiceState();
        var name = cols[0];
        if (string.IsNullOrWhiteSpace(name) || name is "－" or "-") return false;
        if (!int.TryParse(cols[4], out var svcType) || svcType is not (1 or 161 or 162 or 173)) return false;
        if (!ushort.TryParse(cols[5], out var sid) || sid == 0) return false;
        if (!ushort.TryParse(cols[6], out var onid) || onid == 0) return false;
        if (!ushort.TryParse(cols[7], out var tsid) || tsid == 0) return false;
        _ = int.TryParse(cols[8], out var state);

        serviceState = new ChannelServiceState
        {
            Group = ResolveGroup(fileGroup, onid),
            OriginalNetworkId = onid,
            TransportStreamId = tsid,
            ServiceId = sid,
            Name = name,
            IsEnabled = state != 0,
            IsRoutable = false,
            Ch2FileName = ch2FileName,
            Ch2LineNumber = ch2LineNumber
        };
        return true;
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
        // Keep the input coordinates separate from the ChSet-resolved route.
        // They can only be used with a BonDriver whose channel table matches
        // the TVTest .ch2 source; existing recordings keep the legacy route.
        if (!int.TryParse(cols[1], out var ch2Space) || ch2Space < 0) return false;
        if (!int.TryParse(cols[2], out var bonCh) || bonCh < 0) return false;
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
            Ch2TuningSpace = ch2Space,
            Ch2ChannelIndex = bonCh,
            ResolvedSpace = resolvedSpace,
            ResolvedChannelIndex = resolvedChannelIndex,
            SameTransportServiceCount = sameTsServiceCount,
            ChannelBuildSource = buildSource
        };
        return true;
    }

    private static string ResolveGroup(string fileGroup, ushort onid)
    {
        return BroadcastNetworkIdentity.ResolveLegacyChannelGroup(fileGroup, onid);
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

        // BS/CSも現在の共通チャンネル解決経路へ投影する。
        // ChSetのTSID→BonDriverチャンネルを /chi として渡し、TVTestの物理選局経路と一致させる。
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

    // TVTest .ch2 is CSV: a quoted channel name may contain a comma or escaped quote.
    // Use this single parser for both route resolution and active-service counting.
    internal static string[] ParseCh2Columns(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(value.ToString().Trim());
                value.Clear();
            }
            else
            {
                value.Append(c);
            }
        }
        if (quoted) return Array.Empty<string>();
        fields.Add(value.ToString().Trim());
        return fields.ToArray();
    }

    private static bool TryReadActiveServiceTriplet(string rawLine, out ushort onid, out ushort tsid, out ushort sid)
    {
        onid = tsid = sid = 0;
        var line = rawLine.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(line) || line[0] is ';' or '#' or '[') return false;
        var cols = ParseCh2Columns(line);
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

public enum ChannelServiceEligibility
{
    Unknown,
    Disabled,
    ActiveRouteUnavailable,
    Active
}

/// <summary>
/// TVTest .ch2 上のサービス有効状態という生の事実。EPG収集範囲とは独立する。
/// 操作可否の判定は ChannelServiceAccessPolicy だけが公開責務として解釈する。
/// </summary>
public sealed class ChannelServiceState
{
    public string Group { get; set; } = "";
    public ushort OriginalNetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsRoutable { get; set; }
    public string Ch2FileName { get; set; } = "";
    public int Ch2LineNumber { get; set; }
}

public sealed class ChannelLoadResult
{
    public List<ChannelTarget> Targets { get; set; } = new();
    // Direct routes are bound to their explicit BonDriver/.ch2 source. They do
    // not make a legacy ChannelTarget globally routable until a physical tuner
    // is selected and the route is resolved for that tuner.
    public BonDriverChannelRouteCatalog? BonDriverRoutes { get; internal set; }
    // ChSet-independent service inventory. Never merge into Targets/ServiceStates
    // until an operation has selected a compatible physical tuner.
    public List<BonDriverChannelRoute> DirectSourceServices { get; } = new();
    public List<ChannelServiceState> ServiceStates { get; } = new();
    public List<string> Files    { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public int RawSkippedCount   { get; set; }
    public string Message        { get; set; } = "";

    /// <summary>
    /// ChannelServiceAccessPolicy 内部用。TVTest .ch2 state と旧ChSet経路の状態を分類する。直接経路の可否は上位ポリシーが別に確認する。
    /// EPGに存在するだけのサービスは Unknown。操作側から直接呼ばない。
    /// </summary>
    internal ChannelServiceEligibility GetServiceEligibility(ushort networkId, ushort transportStreamId, ushort serviceId)
    {
        var states = ServiceStates
            .Where(x => x.OriginalNetworkId == networkId
                        && x.TransportStreamId == transportStreamId
                        && x.ServiceId == serviceId)
            .ToList();
        if (states.Count == 0) return ChannelServiceEligibility.Unknown;
        if (states.All(x => !x.IsEnabled)) return ChannelServiceEligibility.Disabled;
        return states.Any(x => x.IsEnabled && x.IsRoutable)
            ? ChannelServiceEligibility.Active
            : ChannelServiceEligibility.ActiveRouteUnavailable;
    }

    /// <summary>
    /// TVTest .ch2 でユーザーが有効化しているサービスか。ChSet経路の一時的な解決可否とは分離する。
    /// .ch2 に存在しないサービスと state=0 のサービスはいずれも false。
    /// </summary>
    internal bool IsTvTestServiceEnabled(ushort networkId, ushort transportStreamId, ushort serviceId)
        => ServiceStates.Any(x => x.OriginalNetworkId == networkId
                                  && x.TransportStreamId == transportStreamId
                                  && x.ServiceId == serviceId
                                  && x.IsEnabled);


    internal ChannelTarget? ResolveActiveTarget(ushort networkId, ushort transportStreamId, ushort serviceId)
        => Targets.FirstOrDefault(x => x.OriginalNetworkId == networkId
                                       && x.TransportStreamId == transportStreamId
                                       && x.ServiceId == serviceId);
}
