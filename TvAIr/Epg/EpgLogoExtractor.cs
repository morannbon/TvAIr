using TvAIr.Core;

namespace TvAIr.Epg;

/// <summary>
/// EPG取得済みTSファイルからGR地デジロゴを抽出してDBに保存する。
///
/// 取得方針 (v0.9.44):
///   - CDT (table_id=0xC8) は PID 0x0029 を本線とする (TVTest/EDCB実態寄せ)
///   - NIT (PID=0x0010) は到達確認・ONID補助のみ。CDT PID動的解決は行わない
///   - DSM-CC / DII / carousel 追跡は実装しない (スコープ外)
///   - GR限定 (IsTerrestrialOnid)。BS/CS CDT は捨てる
///   - PNG不正・data_size不整合は破棄のみ。リトライ・再巡回なし
///   - SDT logo_transmission_descriptor(0xCF) と (ONID, logo_id) でサービスに紐付ける
/// </summary>
public sealed class EpgLogoExtractor
{
    private readonly ServiceLogoStore store;
    private readonly LogRepository log;
    private readonly string logoDirectory;

    public EpgLogoExtractor(ServiceLogoStore store, LogRepository log, Database database)
    {
        this.store = store;
        this.log = log;
        logoDirectory = Path.Combine(database.DataDirectory, "service-logos");
    }

    public int ExtractAndStore(
        string tsFile,
        ushort defaultNetworkId,
        ushort defaultTransportStreamId,
        IReadOnlyCollection<ushort> targetSids,
        IReadOnlyDictionary<ushort, string> ch2Names)
    {
        if (string.IsNullOrWhiteSpace(tsFile) || !File.Exists(tsFile)) return 0;
        try
        {
            Directory.CreateDirectory(logoDirectory);
            var parser = new LogoTsParser();
            var result = parser.Parse(tsFile, defaultNetworkId, defaultTransportStreamId);

            // PNG保存: (ONID, logo_id, logo_type) ごとに最新 version のみ保存。
            // v0.9.66: 表示へ直結せず、まず取得できた type を在庫として保持する。
            var saved = new Dictionary<(ushort NetworkId, int LogoId, int LogoType), string>();
            var savedLogoData = new Dictionary<(ushort NetworkId, int LogoId, int LogoType), CdtLogoData>();
            foreach (var logo in result.Logos
                .GroupBy(x => (x.NetworkId, x.LogoId, x.LogoType))
                .Select(g => g.OrderByDescending(x => x.LogoVersion).First()))
            {
                if (!IsValidPng(logo.Data)) continue;
                var typeStr = logo.LogoType >= 0 ? $"_type{logo.LogoType}" : string.Empty;
                var fileName = $"nid{logo.NetworkId}_dl{logo.DownloadDataId}_logo{logo.LogoId}_v{logo.LogoVersion}{typeStr}.png";
                var path = Path.Combine(logoDirectory, fileName);
                File.WriteAllBytes(path, logo.Data);
                var key = (logo.NetworkId, logo.LogoId, logo.LogoType);
                saved[key] = path;
                savedLogoData[key] = logo;
            }

            // SDT マップと保存済み PNG を (ONID, logo_id) で結合。
            // service_logos は従来互換の「代表ロゴ」だけを保持し、service_logo_inventory に全typeを蓄積する。
            var records = new List<ServiceLogoRecord>();
            var inventoryRecords = new List<ServiceLogoInventoryRecord>();
            foreach (var map in result.ServiceMaps)
            {
                if (targetSids.Count > 0 && !targetSids.Contains(map.ServiceId)) continue;

                var serviceName = ch2Names.TryGetValue(map.ServiceId, out var ch2Name) ? ch2Name : map.ServiceName;
                var available = saved
                    .Where(x => x.Key.NetworkId == map.NetworkId && x.Key.LogoId == map.LogoId)
                    .OrderBy(x => x.Key.LogoType)
                    .ToList();

                foreach (var item in available)
                {
                    var logo = savedLogoData[item.Key];
                    inventoryRecords.Add(new ServiceLogoInventoryRecord(
                        map.NetworkId,
                        map.TransportStreamId,
                        map.ServiceId,
                        serviceName,
                        map.LogoId,
                        item.Key.LogoType,
                        logo.LogoVersion,
                        logo.DownloadDataId,
                        item.Value,
                        "ts_cdt_sdt_inventory",
                        DateTimeOffset.Now));
                }

                // 代表ロゴの優先順は表示用途に近い順にするが、ここでは表示は行わない。
                // 中央表示候補: type5(64x36) → type2(48x27) → 大きめの補助 → 小型。
                string? path = null;
                foreach (var preferredType in new[] { 5, 2, 3, 4, 0, 1 })
                {
                    if (saved.TryGetValue((map.NetworkId, map.LogoId, preferredType), out path)) break;
                }
                if (path is null)
                {
                    path = available
                        .OrderByDescending(x => x.Key.LogoType)
                        .Select(x => (string?)x.Value)
                        .FirstOrDefault();
                }
                if (path is null) continue;

                records.Add(new ServiceLogoRecord(
                    map.NetworkId,
                    map.TransportStreamId,
                    map.ServiceId,
                    serviceName,
                    map.LogoId,
                    map.LogoVersion,
                    map.DownloadDataId,
                    path,
                    "ts_cdt_sdt",
                    DateTimeOffset.Now));
            }

            var inventoryUpserted = store.UpsertInventory(inventoryRecords);
            var upserted = store.UpsertMany(records);
            var d = result.Stats;
            var savedTypes = string.Join(",", saved.Keys.Select(x => x.LogoType).Where(x => x >= 0).Distinct().OrderBy(x => x));
            var inventoryTypeCounts = string.Join(",", inventoryRecords
                .GroupBy(x => x.LogoType)
                .OrderBy(x => x.Key)
                .Select(g => $"type{g.Key}={g.Count()}"));
            var centerReady = inventoryRecords
                .GroupBy(x => (x.NetworkId, x.TransportStreamId, x.ServiceId))
                .Count(g => g.Any(x => x.LogoType == 5));
            var centerFallback = inventoryRecords
                .GroupBy(x => (x.NetworkId, x.TransportStreamId, x.ServiceId))
                .Count(g => !g.Any(x => x.LogoType == 5) && g.Any(x => x.LogoType == 2));
            log.Add("SERVICE_LOGO_CAPTURE", $"TS{defaultTransportStreamId}",
                $"result=OK " +
                $"nitArrived={d.NitArrived} " +
                $"cdtSections={d.CdtSections} cdtDataModuleParsed={d.CdtDataModuleParsed} cdtPngValid={d.CdtPngValid} " +
                $"sdtSections={d.SdtSections} logoDescType1={d.LogoDescriptorType1} logoDescType2={d.LogoDescriptorType2} " +
                $"logosFound={result.Logos.Count} sdtMaps={result.ServiceMaps.Count} " +
                $"savedPng={saved.Count} savedTypes=[{Safe(savedTypes)}] upserted={upserted} inventoryUpserted={inventoryUpserted} " +
                $"inventoryTypes=[{Safe(inventoryTypeCounts)}] centerType5Services={centerReady} centerType2FallbackServices={centerFallback} " +
                $"displayPhase=deferred rule=v0.9.66_logo_inventory_acquisition_only " +
                $"file={Safe(tsFile)}");
            return upserted;
        }
        catch (Exception ex)
        {
            log.Add("SERVICE_LOGO_CAPTURE", $"TS{defaultTransportStreamId}",
                $"result=ERROR error={ex.GetType().Name}:{Safe(ex.Message)} " +
                $"file={Safe(tsFile)} rule=v0.9.44_gr_pid0029_nit_aux");
            return 0;
        }
    }

    // PNG シグネチャ検査。不正なら破棄のみ、リトライなし
    private static bool IsValidPng(byte[] data) =>
        data.Length >= 8
        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
        && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    // GR (地デジ) ONID 判定。条件変更は必ずここだけを変える
    // ARIB STD-B10: 地デジ ONID は 0x7880〜0x7FE8 帯。0x7800 以上を GR 扱いとする
    internal static bool IsTerrestrialOnid(ushort networkId) => networkId >= 0x7800;

    private static string Safe(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "-" : v.Replace('\r', ' ').Replace('\n', ' ').Trim();

    // =========================================================================
    // TSパーサー
    // =========================================================================
    private sealed class LogoTsParser
    {
        private readonly Dictionary<int, PsiAssembler> assemblers = new();
        private readonly List<ServiceLogoMap> maps = new();
        private readonly List<CdtLogoData> logos = new();
        private readonly LogoParseStats stats = new();

        public LogoParseResult Parse(string tsFile, ushort defaultNetworkId, ushort defaultTransportStreamId)
        {
            using var fs = File.OpenRead(tsFile);
            var packet = new byte[188];
            while (fs.Read(packet, 0, packet.Length) == packet.Length)
            {
                if (packet[0] != 0x47) continue;
                var pid = ((packet[1] & 0x1F) << 8) | packet[2];

                // 監視対象 PID のみ処理
                // NIT: 0x0010 (ONID補助)
                // SDT: 0x0011 (サービスマップ)
                // CDT: 0x0029 (地デジロゴ本線)
                if (pid != 0x0010 && pid != 0x0011 && pid != 0x0029) continue;

                var payloadStart = (packet[1] & 0x40) != 0;
                var afc = (packet[3] >> 4) & 0x03;
                if (afc == 0 || afc == 2) continue;
                var off = 4;
                if (afc == 3)
                {
                    if (off >= 188) continue;
                    off += 1 + packet[off];
                }
                if (off >= 188) continue;

                if (!assemblers.TryGetValue(pid, out var asm))
                    assemblers[pid] = asm = new PsiAssembler();

                foreach (var section in asm.Push(packet, off, 188 - off, payloadStart))
                {
                    switch (pid)
                    {
                        case 0x0010: HandleNit(section, defaultNetworkId); break;
                        case 0x0011: HandleSdt(section, defaultNetworkId); break;
                        case 0x0029: HandleCdt(section, defaultNetworkId); break;
                    }
                }
            }
            return new LogoParseResult(maps, logos, stats);
        }

        // ─── NIT: 到達確認・ONID補助のみ ─────────────────────────────────────
        // CDT PID 動的解決・DSM-CC/DII 追跡は行わない
        private void HandleNit(byte[] s, ushort defaultNetworkId)
        {
            if (s.Length < 3) return;
            var tableId = s[0];
            if (tableId != 0x40 && tableId != 0x41) return; // NIT actual / other
            stats.NitArrived = true;
        }

        // ─── SDT: logo_transmission_descriptor(0xCF) からサービスマップ取得 ───
        private void HandleSdt(byte[] s, ushort defaultNetworkId)
        {
            if (s.Length < 15) return;
            var tableId = s[0];
            if (tableId != 0x42 && tableId != 0x46) return;
            stats.SdtSections++;

            var sectionLength = ((s[1] & 0x0F) << 8) | s[2];
            var end = Math.Min(s.Length, 3 + sectionLength - 4);
            var tsid = U16(s, 3);
            var onid = end >= 10 ? U16(s, 8) : defaultNetworkId;
            var pos = 11;
            while (pos + 5 <= end)
            {
                var sid = U16(s, pos); pos += 2;
                pos += 1; // EIT present/following flags
                if (pos + 2 > end) break;
                var descLoopLen = ((s[pos] & 0x0F) << 8) | s[pos + 1]; pos += 2;
                var descEnd = Math.Min(end, pos + descLoopLen);

                string? serviceName = null;
                int logoId = -1, logoVersion = -1, downloadDataId = -1;
                while (pos + 2 <= descEnd)
                {
                    var tag = s[pos++];
                    var len = s[pos++];
                    if (pos + len > descEnd) break;
                    if (tag == 0x48 && len >= 3)
                    {
                        // service_descriptor: provider_name + service_name
                        var p = pos + 1;
                        if (p < pos + len)
                        {
                            var providerLen = s[p++];
                            p += providerLen;
                            if (p < pos + len)
                            {
                                var nameLen = s[p++];
                                if (p + nameLen <= pos + len)
                                    serviceName = TryDecodeString(s, p, nameLen);
                            }
                        }
                    }
                    else if (tag == 0xCF && len >= 1)
                    {
                        // logo_transmission_descriptor
                        var logoTransType = s[pos];
                        if (logoTransType == 0x01 && len >= 7)
                        {
                            // type=0x01: logo_id + logo_version_no + download_data_id
                            stats.LogoDescriptorType1++;
                            logoId       = ((s[pos + 1] & 0x01) << 8) | s[pos + 2];
                            logoVersion  = ((s[pos + 3] & 0x0F) << 8) | s[pos + 4];
                            downloadDataId = U16(s, pos + 5);
                        }
                        else if (logoTransType == 0x02 && len >= 3)
                        {
                            // type=0x02: logo_id のみ
                            stats.LogoDescriptorType2++;
                            logoId = ((s[pos + 1] & 0x01) << 8) | s[pos + 2];
                        }
                    }
                    pos += len;
                }
                if (logoId >= 0 && downloadDataId >= 0)
                    maps.Add(new ServiceLogoMap(onid, tsid, sid, serviceName, logoId, logoVersion, downloadDataId));
                pos = descEnd;
            }
        }

        // ─── CDT: ARIB STD-B21 準拠 data_module_byte() パース ────────────────
        //
        // セクション構造:
        //   [0]      table_id = 0xC8
        //   [1-2]    section_syntax_indicator=1, section_length(12bit)
        //   [3-4]    download_data_id
        //   [5]      version_number(5bit), current_next_indicator(1bit)
        //   [6]      section_number
        //   [7]      last_section_number
        //   [8-9]    original_network_id
        //   [10]     data_type
        //   [11-12]  descriptor_loop_length(12bit)
        //   [13 .. 13+descLoopLen-1]  descriptor()
        //   [13+descLoopLen .. sectionEnd]  data_module_byte()
        //
        // data_module_byte() 内部 (data_type=0x01 ロゴデータ):
        //   [0]     logo_type
        //   [1]     reserved(7bit) | logo_id[8]   ← logo_id の上位 1bit
        //   [2]     logo_id[7:0]
        //   [3]     reserved(4bit) | logo_version_no[11:8]
        //   [4]     logo_version_no[7:0]
        //   [5-6]   data_size (16bit big-endian)
        //   [7 .. 7+data_size-1]  data_byte[] = PNG
        //
        // GR 限定。BSCS ONID の CDT は捨てる。
        // data_size 不整合・PNG署名なし は破棄のみ。リトライなし。
        private void HandleCdt(byte[] s, ushort defaultNetworkId)
        {
            if (s.Length < 13) return;
            if (s[0] != 0xC8) return;

            var sectionLength = ((s[1] & 0x0F) << 8) | s[2];
            var sectionEnd = Math.Min(s.Length, 3 + sectionLength - 4);
            if (sectionEnd < 13) return;

            var downloadDataId = U16(s, 3);
            var onid           = U16(s, 8);
            if (onid == 0) onid = defaultNetworkId;
            var dataType       = s[10];

            // GR 以外は捨てる
            if (!IsTerrestrialOnid(onid)) return;
            // ロゴデータ以外は対象外
            if (dataType != 0x01) return;

            stats.CdtSections++;

            // descriptor_loop をスキップして data_module_byte() 先頭へ
            var descLoopLen = ((s[11] & 0x0F) << 8) | s[12];
            var dataStart   = 13 + descLoopLen;
            if (dataStart >= sectionEnd) return;

            // data_module_byte() パース
            var dm = s.AsSpan(dataStart, sectionEnd - dataStart);
            if (dm.Length < 7) return;

            var logoType    = dm[0];
            var logoId      = ((dm[1] & 0x01) << 8) | dm[2];
            var logoVersion = ((dm[3] & 0x0F) << 8) | dm[4];
            var dataSize    = (dm[5] << 8) | dm[6];

            if (dataSize <= 0 || 7 + dataSize > dm.Length) return;

            var logoBytes = dm.Slice(7, dataSize).ToArray();
            stats.CdtDataModuleParsed++;

            if (!IsValidPng(logoBytes)) return;
            stats.CdtPngValid++;

            logos.Add(new CdtLogoData(onid, downloadDataId, logoId, logoVersion, logoType, logoBytes));
        }

        private static ushort U16(byte[] b, int off) =>
            off + 1 < b.Length ? (ushort)((b[off] << 8) | b[off + 1]) : (ushort)0;

        private static string TryDecodeString(byte[] b, int off, int len)
        {
            try { return System.Text.Encoding.GetEncoding(932).GetString(b, off, len).Trim('\0', ' '); }
            catch { return string.Empty; }
        }
    }

    // =========================================================================
    // PSI アセンブラ
    // =========================================================================
    private sealed class PsiAssembler
    {
        private readonly MemoryStream buffer = new();
        private int expectedLength = -1;

        public IEnumerable<byte[]> Push(byte[] packet, int offset, int count, bool payloadStart)
        {
            var pos = offset;
            var end = offset + count;
            if (payloadStart)
            {
                if (pos >= end) yield break;
                var pointer = packet[pos++];
                var pointerEnd = Math.Min(end, pos + pointer);
                // pointer field が示す前セクション末尾バイトを先に消化してから新セクション開始
                while (pos < pointerEnd)
                {
                    foreach (var completed in AppendByte(packet[pos++]))
                        yield return completed;
                }
                buffer.SetLength(0);
                expectedLength = -1;
            }
            while (pos < end)
            {
                if (packet[pos] == 0xFF && buffer.Length == 0) yield break;
                foreach (var completed in AppendByte(packet[pos++]))
                    yield return completed;
            }
        }

        private IEnumerable<byte[]> AppendByte(byte value)
        {
            buffer.WriteByte(value);
            if (expectedLength < 0 && buffer.Length >= 3)
            {
                var arr = buffer.ToArray();
                expectedLength = 3 + (((arr[1] & 0x0F) << 8) | arr[2]);
                if (expectedLength <= 3 || expectedLength > 4096)
                {
                    buffer.SetLength(0);
                    expectedLength = -1;
                    yield break;
                }
            }
            if (expectedLength > 0 && buffer.Length >= expectedLength)
            {
                var arr     = buffer.ToArray();
                var sec     = arr[..expectedLength];
                var remain  = arr.Skip(expectedLength).ToArray();
                buffer.SetLength(0);
                if (remain.Length > 0) buffer.Write(remain, 0, remain.Length);
                expectedLength = -1;
                yield return sec;
            }
        }
    }

    // =========================================================================
    // データ型
    // =========================================================================
    private sealed record LogoParseResult(
        List<ServiceLogoMap>   ServiceMaps,
        List<CdtLogoData>      Logos,
        LogoParseStats   Stats);

    private sealed record ServiceLogoMap(
        ushort NetworkId, ushort TransportStreamId, ushort ServiceId,
        string? ServiceName, int LogoId, int LogoVersion, int DownloadDataId);

    private sealed record CdtLogoData(
        ushort NetworkId, int DownloadDataId, int LogoId, int LogoVersion,
        int LogoType, byte[] Data);

    private sealed class LogoParseStats
    {
        public bool  NitArrived          { get; set; }
        public long  CdtSections         { get; set; }
        public long  CdtDataModuleParsed { get; set; }
        public long  CdtPngValid         { get; set; }
        public long  SdtSections         { get; set; }
        public long  LogoDescriptorType1 { get; set; }
        public long  LogoDescriptorType2 { get; set; }
    }
}
