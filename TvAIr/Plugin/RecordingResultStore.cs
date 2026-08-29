using System.Text.Json;
using TvAIrPlugin;
using TvAIr.Core;

namespace TvAIr.Plugin;

/// <summary>録画完了後も品質値と終了原因証跡を保持する本体側汎用録画結果台帳。</summary>
public enum RecordingResultUpsertOutcome
{
    AppliedNewFinalized,
    AppliedEvidenceOnly,
    SkippedAlreadyFinalized
}

public sealed class RecordingResultStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private readonly LogRepository log;
    private Dictionary<string, StoredRecordingResult> rows;

    public RecordingResultStore(Database database, LogRepository log)
    {
        this.log = log;
        Directory.CreateDirectory(database.DataDirectory);
        filePath = Path.Combine(database.DataDirectory, "recording-results.json");
        rows = Load();
    }

    public RecordingResultUpsertOutcome Upsert(TvAirRecordingResultDto result, RecordingTerminationEvidence? terminationEvidence = null)
    {
        lock (gate)
        {
            rows.TryGetValue(result.ReservationId, out var existing);

            // RECORDING_RESULT_FINALIZE_ONCE_INVARIANT:
            // A finalized public recording result is immutable. Stop-session normal completion and
            // exception fallback are separate physical exit routes, but neither may overwrite a
            // result that has already been finalized for the same ReservationId. This store-level
            // guard is the canonical last line of defense even if an upstream lifecycle race or
            // future caller accidentally attempts a late second projection. Missing termination
            // evidence may be completed without changing the finalized public result itself.
            if (existing?.PublicResult?.ResultFinalized == true)
            {
                if (existing.TerminationEvidence is null && terminationEvidence is not null)
                {
                    existing.TerminationEvidence = terminationEvidence;
                    SaveUnsafe();
                    log.Add("RECORDING_RESULT_STORE_FINALIZE_ONCE", "RecordingResultStore",
                        $"result=EVIDENCE_COMPLETED reservation={result.ReservationId} publicResult=preserved rule=recording_result_finalize_once_contract");
                    return RecordingResultUpsertOutcome.AppliedEvidenceOnly;
                }

                log.Add("RECORDING_RESULT_STORE_FINALIZE_ONCE", "RecordingResultStore",
                    $"result=SKIPPED_ALREADY_FINALIZED reservation={result.ReservationId} publicResult=preserved rule=recording_result_finalize_once_contract");
                return RecordingResultUpsertOutcome.SkippedAlreadyFinalized;
            }

            rows[result.ReservationId] = new StoredRecordingResult
            {
                PublicResult = result,
                TerminationEvidence = terminationEvidence ?? existing?.TerminationEvidence
            };
            SaveUnsafe();
            return RecordingResultUpsertOutcome.AppliedNewFinalized;
        }
    }

    public TvAirRecordingResultDto? Get(string reservationId)
    {
        lock (gate) return rows.TryGetValue(reservationId, out var value) ? value.PublicResult : null;
    }

    public RecordingTerminationEvidence? GetTerminationEvidence(string reservationId)
    {
        lock (gate) return rows.TryGetValue(reservationId, out var value) ? value.TerminationEvidence : null;
    }

    // RECORDING_RESULT_IMMUTABLE_AFTER_FINALIZE:
    // Upsert is the only public-result mutation route. Plugin/history reads may supplement missing
    // legacy program metadata in their own projection, but must not rewrite finalized store rows.

    public IReadOnlyList<TvAirRecordingResultDto> List()
    {
        lock (gate) return rows.Values.Select(x => x.PublicResult).ToList();
    }

    private void SaveUnsafe()
    {
        var temp = filePath + ".tmp";
        var payload = rows.Values
            .OrderBy(x => x.PublicResult.ScheduledStartTime)
            .ToList();
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions));
        File.Move(temp, filePath, true);
    }

    private Dictionary<string, StoredRecordingResult> Load()
    {
        if (!File.Exists(filePath)) return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(filePath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("recording-results root is not an array");

            var first = document.RootElement.EnumerateArray().FirstOrDefault();
            var isNewFormat = first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty(nameof(StoredRecordingResult.PublicResult), out _);

            List<StoredRecordingResult> list;
            if (isNewFormat)
            {
                list = JsonSerializer.Deserialize<List<StoredRecordingResult>>(json, JsonOptions) ?? new();
            }
            else
            {
                var legacy = JsonSerializer.Deserialize<List<TvAirRecordingResultDto>>(json, JsonOptions) ?? new();
                list = legacy.Select(x => new StoredRecordingResult { PublicResult = x }).ToList();
            }

            var loaded = list
                .Where(x => x.PublicResult is not null && !string.IsNullOrWhiteSpace(x.PublicResult.ReservationId))
                .GroupBy(x => x.PublicResult.ReservationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            log.Add("RECORDING_RESULT_STORE_LOAD", "RecordingResultStore",
                $"result=OK format={(isNewFormat ? "stored_result" : "legacy_public_result")} count={loaded.Count} migration={(isNewFormat ? "none" : "on_next_save")} rule=recording_result_store_contract");
            return loaded;
        }
        catch (Exception ex)
        {
            log.Add("RECORDING_RESULT_STORE_LOAD", "RecordingResultStore",
                $"result=FAILED filePreserved=True action=do_not_overwrite reason={Sanitize(ex.Message)} rule=recording_result_store_contract");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

public sealed class StoredRecordingResult
{
    public TvAirRecordingResultDto PublicResult { get; set; } = new();
    public RecordingTerminationEvidence? TerminationEvidence { get; set; }
}

public sealed class RecordingTerminationEvidence
{
    public string RecordingRecoveryChainId { get; set; } = string.Empty;
    public string PowerResumeCycleId { get; set; } = string.Empty;
    public string TerminationReason { get; set; } = string.Empty;
    public int WorkerProcessId { get; set; }
    public DateTime? WorkerProcessStartedAt { get; set; }
    public DateTime? WorkerProcessObservedEndedAt { get; set; }
    public int? WorkerExitCode { get; set; }
    public string WorkerIdentityResult { get; set; } = string.Empty;
    public DateTime? LastFileGrowthAt { get; set; }
    public long LastObservedFileSize { get; set; } = -1;
    public DateTime? StopRequestedAt { get; set; }
    public string StopRequestResult { get; set; } = string.Empty;
    public string ResultFileState { get; set; } = string.Empty;
    public string TransportStreamValidation { get; set; } = string.Empty;
    public string LeaseReleaseResult { get; set; } = string.Empty;
    public string ActivityReleaseResult { get; set; } = string.Empty;
    public string RegistryReleaseResult { get; set; } = string.Empty;
    public string TunerStateAfterRelease { get; set; } = string.Empty;
    public string RecoveryReservationResult { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["recordingRecoveryChainId"] = RecordingRecoveryChainId,
            ["powerResumeCycleId"] = PowerResumeCycleId,
            ["interruptionReason"] = TerminationReason,
            ["workerIdentityResult"] = WorkerIdentityResult,
            ["workerProcessId"] = WorkerProcessId > 0 ? WorkerProcessId.ToString() : string.Empty,
            ["workerProcessStartedAt"] = WorkerProcessStartedAt?.ToString("O") ?? string.Empty,
            ["workerProcessObservedEndedAt"] = WorkerProcessObservedEndedAt?.ToString("O") ?? string.Empty,
            ["workerExitCode"] = WorkerExitCode?.ToString() ?? string.Empty,
            ["lastFileGrowthAt"] = LastFileGrowthAt?.ToString("O") ?? string.Empty,
            ["lastObservedFileSize"] = LastObservedFileSize >= 0 ? LastObservedFileSize.ToString() : string.Empty,
            ["stopRequestedAt"] = StopRequestedAt?.ToString("O") ?? string.Empty,
            ["stopRequestResult"] = StopRequestResult,
            ["resultFileState"] = ResultFileState,
            ["transportStreamValidation"] = TransportStreamValidation,
            ["leaseReleaseResult"] = LeaseReleaseResult,
            ["activityReleaseResult"] = ActivityReleaseResult,
            ["registryReleaseResult"] = RegistryReleaseResult,
            ["tunerStateAfterRelease"] = TunerStateAfterRelease,
            ["recoveryReservationResult"] = RecoveryReservationResult
        };
    }
}
