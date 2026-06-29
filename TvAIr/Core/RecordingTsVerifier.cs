using TvAIr.Epg;

namespace TvAIr.Core;

/// <summary>
/// release_contract: 録画TSの実体確認ログ。
/// TVTestを起動できた/ファイルが増えた、だけではサービス違い・EPGズレ・映像PIDなし・スクランブル残りを見落とすため、
/// 録画開始後と録画停止後にTS先頭からPSI/SIの最低限の情報を読み、予約情報と照合する。
/// </summary>
public static class RecordingTsVerifier
{
    public sealed record VerificationResult(
        string Stage,
        string Result,
        string Verdict,
        string ReadableJudgement,
        bool ClearEnoughForCompleted,
        string Reason);
    private const int PacketSize = 188;
    private const int DefaultMaxPackets = 180_000;

    public static async Task<VerificationResult> VerifyAsync(
        Reservation reservation,
        string recordingFilePath,
        string stage,
        LogRepository log,
        int maxPackets = DefaultMaxPackets)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(recordingFilePath) || !File.Exists(recordingFilePath))
            {
                log.Add("REC_TS_VERIFY", $"R{reservation.Id}",
                    $"stage={stage} result=FAIL reason=file_missing expectedService={Safe(reservation.ServiceName)}/{reservation.ServiceId} path={Safe(recordingFilePath)}");
                return new VerificationResult(stage, "FAIL", "strict_check", "file_missing", false, "file_missing");
            }

            var fileInfo = new FileInfo(recordingFilePath);
            var psi = AnalyzePsi(recordingFilePath, maxPackets);
            var eit = await AnalyzeEitAsync(recordingFilePath, reservation, maxPackets);

            var serviceInPat = psi.ProgramMapPids.ContainsKey(reservation.ServiceId);
            var pmtFound = psi.Services.TryGetValue(reservation.ServiceId, out var expectedServicePsi);
            var videoPidCount = expectedServicePsi?.VideoPids.Count ?? 0;
            var expectedVideoScrambled = expectedServicePsi?.ScrambledPackets ?? 0;
            var expectedPackets = expectedServicePsi?.PacketCount ?? 0;
            var serviceInEit = eit.ServiceSeen;
            var serviceOk = serviceInPat || serviceInEit || pmtFound;
            var eventTitle = Safe(eit.CurrentOrNearestTitle);
            var eventMismatch = !string.IsNullOrWhiteSpace(eit.CurrentOrNearestTitle)
                && !LooksLikeSameEvent(reservation.Title, eit.CurrentOrNearestTitle);
            var videoOk = videoPidCount > 0;
            // release_contract: totalScrambledPackets includes packets from other services in the same TS.
            // It must not turn a PAT/PMT-confirmed target service into SERVICE_MISMATCH_OR_UNKNOWN.
            // Use only the expected service video PID scramble count for the result verdict; keep totalScrambledPackets as diagnostic detail.
            var expectedServiceScrambled = expectedVideoScrambled > 0;

            // release_contract: EITの番組名は停止直後・途中開始・番組境界付近で次番組/近傍番組を拾うことがある。
            // サービス一致 + PMT映像あり + 対象サービスのスクランブル0 が確認できている場合、
            // 番組名不一致だけで録画実体の失敗に見せない。警告情報として残し、判定名はWARNへ降格する。
            var titleMismatchDowngraded = serviceOk && videoOk && !expectedServiceScrambled && serviceInEit && eventMismatch;
            var titleMismatchKind = ClassifyTitleMismatch(reservation, stage, eventMismatch);
            var result = !serviceOk
                ? "SERVICE_MISMATCH_OR_UNKNOWN"
                : !videoOk
                    ? "SERVICE_OK_VIDEO_UNKNOWN"
                    : expectedServiceScrambled
                        ? "SERVICE_OK_VIDEO_SCRAMBLED"
                        : !serviceInEit
                            ? "SERVICE_PAT_PMT_OK_EIT_UNKNOWN_VIDEO_OK"
                            : titleMismatchDowngraded
                                ? titleMismatchKind.Result
                                : "OK";
            var verdict = titleMismatchDowngraded
                ? titleMismatchKind.Verdict
                : result == "OK"
                    ? "clear_ts_ok"
                    : "strict_check";

            var readableJudgement = BuildReadableJudgement(result, verdict, titleMismatchKind.Reason);
            var clearEnoughForCompleted = string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase)
                || verdict.StartsWith("clear_ts_ok", StringComparison.OrdinalIgnoreCase)
                || readableJudgement.StartsWith("clear_ts_ok", StringComparison.OrdinalIgnoreCase);
            log.Add("REC_TS_VERIFY", $"R{reservation.Id}",
                $"stage={stage} result={result} verdict={verdict} readableJudgement={readableJudgement} path={recordingFilePath} size={fileInfo.Length} " +
                $"expectedService={Safe(reservation.ServiceName)}/{reservation.ServiceId} expectedTitle={Safe(reservation.Title)} " +
                $"programs=[{string.Join(',', psi.ProgramMapPids.Keys.OrderBy(x => x))}] serviceInPat={serviceInPat} pmtFound={pmtFound} serviceInEit={serviceInEit} " +
                $"actualService={Safe(eit.ServiceName)}/{(eit.ServiceId.HasValue ? eit.ServiceId.Value.ToString() : "-")} actualEvent={eventTitle} eventMismatch={eventMismatch} eventMismatchDowngraded={titleMismatchDowngraded} titleMismatchReason={titleMismatchKind.Reason} " +
                $"videoPidCount={videoPidCount} videoPids=[{(expectedServicePsi != null ? string.Join(',', expectedServicePsi.VideoPids.OrderBy(x => x)) : "")}] " +
                $"expectedPackets={expectedPackets} expectedScrambledPackets={expectedVideoScrambled} totalScrambledPackets={psi.TotalScrambledPackets} " +
                $"packetsScanned={psi.PacketsScanned} syncErrors={psi.SyncErrors} clearEnoughForCompleted={clearEnoughForCompleted} rule=release_contract eitStats={Safe(eit.StatsLine)}");
            return new VerificationResult(stage, result, verdict, readableJudgement, clearEnoughForCompleted, titleMismatchKind.Reason);
        }
        catch (Exception ex)
        {
            log.Add("REC_TS_VERIFY", $"R{reservation.Id}",
                $"stage={stage} result=FAIL reason=exception message={TrimForLog(ex.Message, 220)} path={Safe(recordingFilePath)}");
            return new VerificationResult(stage, "FAIL", "strict_check", "exception", false, ex.GetType().Name);
        }
    }


    private static string BuildReadableJudgement(string result, string verdict, string titleMismatchReason)
    {
        if (string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase))
            return "clear_ts_ok";
        if (string.Equals(result, "OK_EVENT_TITLE_BOUNDARY_WARN", StringComparison.OrdinalIgnoreCase))
            return "clear_ts_ok_stop_boundary_eit_title_warning_not_recording_failure";
        if (string.Equals(result, "OK_EVENT_TITLE_MISMATCH_WARN", StringComparison.OrdinalIgnoreCase))
            return "clear_ts_ok_eit_title_warning_not_transport_failure";
        if (string.Equals(result, "SERVICE_OK_VIDEO_SCRAMBLED", StringComparison.OrdinalIgnoreCase))
            return "service_found_but_target_video_scrambled_investigate_descramble";
        if (string.Equals(result, "SERVICE_OK_VIDEO_UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return "service_found_but_video_pid_unknown_investigate_pmt_or_capture";
        if (string.Equals(result, "SERVICE_MISMATCH_OR_UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return "target_service_not_confirmed_in_recorded_ts";
        if (string.Equals(result, "SERVICE_PAT_PMT_OK_EIT_UNKNOWN_VIDEO_OK", StringComparison.OrdinalIgnoreCase))
            return "pat_pmt_video_ok_eit_unknown";

        return $"{Safe(result)}_{Safe(verdict)}_{Safe(titleMismatchReason)}";
    }

    private static TitleMismatchClassification ClassifyTitleMismatch(Reservation reservation, string stage, bool eventMismatch)
    {
        if (!eventMismatch)
        {
            return new TitleMismatchClassification("OK", "clear_ts_ok", "none");
        }

        var duration = reservation.EndTime - reservation.StartTime;
        var actualStart = reservation.RecordingStartedAt;
        var now = DateTime.Now;
        var stopNearEnd = Math.Abs((now - reservation.EndTime).TotalSeconds) <= 180;
        var shortImmediate = reservation.Source == ReservationSource.Immediate && duration.TotalMinutes <= 10;
        var startedLate = actualStart.HasValue && actualStart.Value > reservation.StartTime.AddSeconds(60);
        var afterStopVerify = stage.Contains("after_stop", StringComparison.OrdinalIgnoreCase);

        // release_contract: 「今すぐ録画」「短時間録画」「停止直後」は、TS実体が正常でも
        // EITの現在/近傍番組が次番組側に寄ることがある。録画失敗に見えない名称へ分離する。
        if (shortImmediate || startedLate || (afterStopVerify && stopNearEnd))
        {
            var reason = shortImmediate
                ? "short_immediate_or_boundary_eit"
                : startedLate
                    ? "late_start_nearby_eit"
                    : "after_stop_boundary_eit";
            return new TitleMismatchClassification(
                "OK_EVENT_TITLE_BOUNDARY_WARN",
                "clear_ts_ok_stop_boundary_eit_title_warning_not_recording_failure",
                reason);
        }

        return new TitleMismatchClassification(
            "OK_EVENT_TITLE_MISMATCH_WARN",
            "clear_ts_ok_event_title_warning_not_recording_failure",
            "eit_title_mismatch_but_clear_ts_ok");
    }

    private static async Task<EitSummary> AnalyzeEitAsync(string path, Reservation reservation, int maxPackets)
    {
        try
        {
            var result = await new EpgAnalyzer(maxPackets).AnalyzeAsync(path);
            var serviceEvents = result.Events
                .Where(e => e.ServiceId == reservation.ServiceId)
                .OrderBy(e => e.Start)
                .ToList();

            if (serviceEvents.Count == 0)
            {
                return new EitSummary(false, null, string.Empty, string.Empty, result.StatsLine);
            }

            var probeTime = DateTime.Now;
            var current = serviceEvents.FirstOrDefault(e => e.Start <= probeTime && probeTime < e.End)
                ?? serviceEvents.FirstOrDefault(e => e.Start <= reservation.StartTime && reservation.StartTime < e.End)
                ?? serviceEvents.OrderBy(e => Math.Abs((e.Start - reservation.StartTime).TotalSeconds)).First();

            return new EitSummary(true, current.ServiceId, current.ServiceName, current.Title, result.StatsLine);
        }
        catch (Exception ex)
        {
            return new EitSummary(false, null, string.Empty, string.Empty, $"eit_parse_error:{TrimForLog(ex.Message, 120)}");
        }
    }

    private static PsiSummary AnalyzePsi(string path, int maxPackets)
    {
        var summary = new PsiSummary();
        var packet = new byte[PacketSize];
        var pmtPids = new Dictionary<int, ushort>(); // pmtPid -> serviceId

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        while (summary.PacketsScanned < maxPackets)
        {
            var read = fs.Read(packet, 0, PacketSize);
            if (read == 0) break;
            if (read != PacketSize) break;

            summary.PacketsScanned++;
            if (packet[0] != 0x47)
            {
                summary.SyncErrors++;
                continue;
            }

            var payloadUnitStart = (packet[1] & 0x40) != 0;
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            var scramble = (packet[3] & 0xC0) >> 6;
            var adaptationControl = (packet[3] & 0x30) >> 4;
            if (scramble != 0) summary.TotalScrambledPackets++;

            var payloadOffset = 4;
            if (adaptationControl == 0 || adaptationControl == 2) continue;
            if (adaptationControl == 3)
            {
                if (payloadOffset >= PacketSize) continue;
                var adaptationLength = packet[payloadOffset];
                payloadOffset += 1 + adaptationLength;
                if (payloadOffset >= PacketSize) continue;
            }

            if (pid == 0)
            {
                ParsePat(packet, payloadOffset, payloadUnitStart, summary, pmtPids);
                continue;
            }

            if (pmtPids.TryGetValue(pid, out var serviceId))
            {
                ParsePmt(packet, payloadOffset, payloadUnitStart, serviceId, summary);
                continue;
            }

            foreach (var service in summary.Services.Values)
            {
                if (service.VideoPids.Contains(pid))
                {
                    service.PacketCount++;
                    if (scramble != 0) service.ScrambledPackets++;
                }
            }
        }

        return summary;
    }

    private static void ParsePat(byte[] packet, int payloadOffset, bool payloadUnitStart, PsiSummary summary, Dictionary<int, ushort> pmtPids)
    {
        var sectionOffset = GetSectionOffset(packet, payloadOffset, payloadUnitStart);
        if (sectionOffset < 0 || sectionOffset + 8 >= PacketSize) return;
        if (packet[sectionOffset] != 0x00) return;
        var sectionLength = ((packet[sectionOffset + 1] & 0x0F) << 8) | packet[sectionOffset + 2];
        var sectionEnd = Math.Min(sectionOffset + 3 + sectionLength - 4, PacketSize);
        for (var pos = sectionOffset + 8; pos + 4 <= sectionEnd; pos += 4)
        {
            var programNumber = (ushort)((packet[pos] << 8) | packet[pos + 1]);
            var pmtPid = ((packet[pos + 2] & 0x1F) << 8) | packet[pos + 3];
            if (programNumber == 0) continue;
            summary.ProgramMapPids[programNumber] = pmtPid;
            pmtPids[pmtPid] = programNumber;
        }
    }

    private static void ParsePmt(byte[] packet, int payloadOffset, bool payloadUnitStart, ushort serviceId, PsiSummary summary)
    {
        var sectionOffset = GetSectionOffset(packet, payloadOffset, payloadUnitStart);
        if (sectionOffset < 0 || sectionOffset + 12 >= PacketSize) return;
        if (packet[sectionOffset] != 0x02) return;

        var sectionLength = ((packet[sectionOffset + 1] & 0x0F) << 8) | packet[sectionOffset + 2];
        var programInfoLengthOffset = sectionOffset + 10;
        if (programInfoLengthOffset + 1 >= PacketSize) return;
        var programInfoLength = ((packet[programInfoLengthOffset] & 0x0F) << 8) | packet[programInfoLengthOffset + 1];
        var pos = sectionOffset + 12 + programInfoLength;
        var sectionEnd = Math.Min(sectionOffset + 3 + sectionLength - 4, PacketSize);

        if (!summary.Services.TryGetValue(serviceId, out var service))
        {
            service = new ServicePsiSummary(serviceId);
            summary.Services[serviceId] = service;
        }

        while (pos + 5 <= sectionEnd)
        {
            var streamType = packet[pos];
            var elementaryPid = ((packet[pos + 1] & 0x1F) << 8) | packet[pos + 2];
            var esInfoLength = ((packet[pos + 3] & 0x0F) << 8) | packet[pos + 4];
            if (IsVideoStreamType(streamType)) service.VideoPids.Add(elementaryPid);
            pos += 5 + esInfoLength;
        }
    }

    private static int GetSectionOffset(byte[] packet, int payloadOffset, bool payloadUnitStart)
    {
        if (payloadOffset < 0 || payloadOffset >= PacketSize) return -1;
        if (!payloadUnitStart) return payloadOffset;
        var pointer = packet[payloadOffset];
        var sectionOffset = payloadOffset + 1 + pointer;
        return sectionOffset < PacketSize ? sectionOffset : -1;
    }

    private static bool IsVideoStreamType(byte streamType)
        => streamType is 0x01 or 0x02 or 0x10 or 0x1B or 0x24 or 0xEA;

    private static bool LooksLikeSameEvent(string expected, string actual)
    {
        var e = NormalizeTitle(expected);
        var a = NormalizeTitle(actual);
        if (string.IsNullOrWhiteSpace(e) || string.IsNullOrWhiteSpace(a)) return true;
        if (e.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(e, StringComparison.OrdinalIgnoreCase)) return true;
        var shortE = e.Length > 18 ? e[..18] : e;
        var shortA = a.Length > 18 ? a[..18] : a;
        return shortE.Equals(shortA, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var chars = title.Where(c => !char.IsWhiteSpace(c) && c != '　' && c != '[' && c != ']' && c != '【' && c != '】').ToArray();
        return new string(chars).Trim();
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string TrimForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private sealed class PsiSummary
    {
        public int PacketsScanned { get; set; }
        public int SyncErrors { get; set; }
        public int TotalScrambledPackets { get; set; }
        public Dictionary<ushort, int> ProgramMapPids { get; } = new();
        public Dictionary<ushort, ServicePsiSummary> Services { get; } = new();
    }

    private sealed class ServicePsiSummary
    {
        public ushort ServiceId { get; }
        public HashSet<int> VideoPids { get; } = new();
        public int PacketCount { get; set; }
        public int ScrambledPackets { get; set; }

        public ServicePsiSummary(ushort serviceId) => ServiceId = serviceId;
    }

    private sealed record TitleMismatchClassification(string Result, string Verdict, string Reason);
    private sealed record EitSummary(bool ServiceSeen, ushort? ServiceId, string ServiceName, string CurrentOrNearestTitle, string StatsLine);
}
