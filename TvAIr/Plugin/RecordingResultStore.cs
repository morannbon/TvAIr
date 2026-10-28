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
    public RecordingFileCatalog Files { get; }
    private readonly object gate = new();
    private readonly string filePath;
    private readonly LogRepository log;
    private Dictionary<string, StoredRecordingResult> rows;
    private bool loadFailed;

    public RecordingResultStore(Database database, LogRepository log)
    {
        this.log = log;
        Files = new RecordingFileCatalog(database, log, Get);
        Directory.CreateDirectory(database.DataDirectory);
        filePath = Path.Combine(database.DataDirectory, "recording-results.json");
        rows = Load();
    }

    public RecordingResultUpsertOutcome Upsert(TvAirRecordingResultDto result, RecordingTerminationEvidence? terminationEvidence = null)
    {
        lock (gate)
        {
            // An unreadable existing ledger is not an empty one. Retry its load before
            // accepting another projection, preserving both disk evidence and finalize-once.
            if (loadFailed)
            {
                rows = Load(allowMissing: false);
                if (loadFailed) throw new IOException("Recording result history is unreadable; projection was not saved.");
            }
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
                // ReservationId is the live-reservation key, but recording-results.json can outlive a
                // reservations DB replacement/restore in developer and recovery workflows. Therefore
                // ReservationId alone is not sufficient to prove that an existing finalized row is the
                // same recording occurrence. Treat a different scheduled event/file identity as a new
                // occurrence and replace the stale row. The finalize-once guard remains strict only for
                // the same recording occurrence.
                if (!IsSameRecordingOccurrence(existing.PublicResult, result))
                {
                    var previous = existing.PublicResult;
                    SaveUnsafe(result.ReservationId, new StoredRecordingResult
                    {
                        PublicResult = result,
                        TerminationEvidence = terminationEvidence
                    });
                    log.Add("RECORDING_RESULT_STORE_FINALIZE_ONCE", "RecordingResultStore",
                        $"result=APPLIED_NEW_OCCURRENCE reservation={result.ReservationId} " +
                        $"previousScheduled={previous.ScheduledStartTime:O} currentScheduled={result.ScheduledStartTime:O} " +
                        $"previousEvent={previous.NetworkId}:{previous.TransportStreamId}:{previous.ServiceId}:{previous.EventId} " +
                        $"currentEvent={result.NetworkId}:{result.TransportStreamId}:{result.ServiceId}:{result.EventId} " +
                        $"previousFile={SanitizePath(previous.FilePath)} currentFile={SanitizePath(result.FilePath)} " +
                        "publicResult=replaced_stale_occurrence rule=recording_result_finalize_once_contract");
                    return RecordingResultUpsertOutcome.AppliedNewFinalized;
                }

                if (existing.TerminationEvidence is null && terminationEvidence is not null)
                {
                    SaveUnsafe(result.ReservationId, new StoredRecordingResult
                    {
                        PublicResult = existing.PublicResult,
                        TerminationEvidence = terminationEvidence
                    });
                    log.Add("RECORDING_RESULT_STORE_FINALIZE_ONCE", "RecordingResultStore",
                        $"result=EVIDENCE_COMPLETED reservation={result.ReservationId} publicResult=preserved rule=recording_result_finalize_once_contract");
                    return RecordingResultUpsertOutcome.AppliedEvidenceOnly;
                }

                log.Add("RECORDING_RESULT_STORE_FINALIZE_ONCE", "RecordingResultStore",
                    $"result=SKIPPED_ALREADY_FINALIZED reservation={result.ReservationId} identity=same_recording_occurrence publicResult=preserved rule=recording_result_finalize_once_contract");
                return RecordingResultUpsertOutcome.SkippedAlreadyFinalized;
            }

            SaveUnsafe(result.ReservationId, new StoredRecordingResult
            {
                PublicResult = result,
                TerminationEvidence = terminationEvidence ?? existing?.TerminationEvidence
            });
            return RecordingResultUpsertOutcome.AppliedNewFinalized;
        }
    }

    internal bool HasFinalizedOccurrence(TvAirRecordingResultDto identity)
    {
        lock (gate) return rows.TryGetValue(identity.ReservationId, out var existing)
            && existing.PublicResult.ResultFinalized && IsSameRecordingOccurrence(existing.PublicResult, identity);
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

    private void SaveUnsafe(string reservationId, StoredRecordingResult incoming)
    {
        // Publish only after the file replacement succeeds. An IO failure must leave
        // both the previous public result and the finalize-once retry decision intact.
        var candidate = new Dictionary<string, StoredRecordingResult>(rows, rows.Comparer)
        {
            [reservationId] = incoming
        };
        var temp = filePath + ".tmp";
        var payload = candidate.Values
            .OrderBy(x => x.PublicResult.ScheduledStartTime)
            .ToList();
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions));
        File.Move(temp, filePath, true);
        rows = candidate;
    }

    private Dictionary<string, StoredRecordingResult> Load(bool allowMissing = true)
    {
        loadFailed = false;
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
                .ToDictionary(g => g.Key, g => NormalizeSucceeded(g.Last()), StringComparer.OrdinalIgnoreCase);

            log.Add("RECORDING_RESULT_STORE_LOAD", "RecordingResultStore",
                $"result=OK format={(isNewFormat ? "stored_result" : "legacy_public_result")} count={loaded.Count} migration=typed_succeeded_projection rule=recording_result_store_contract");
            return loaded;
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        catch (DirectoryNotFoundException) when (allowMissing)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            loadFailed = true;
            log.Add("RECORDING_RESULT_STORE_LOAD", "RecordingResultStore",
                $"result=FAILED filePreserved=True action=do_not_overwrite reason={Sanitize(ex.Message)} rule=recording_result_store_contract");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }


    // Legacy recording-results predate the typed Succeeded field. The store is the compatibility
    // boundary: infer the historical value once from the old finalized terminal result and expose
    // only the typed value to plugins. New writes always carry Succeeded directly.
    private static StoredRecordingResult NormalizeSucceeded(StoredRecordingResult row)
    {
        var source = row.PublicResult;
        if (source.Succeeded || !source.ResultFinalized || !string.Equals(source.Result, "Completed", StringComparison.OrdinalIgnoreCase))
            return row;
        row.PublicResult = new TvAirRecordingResultDto
        {
            ReservationId = source.ReservationId, RecordingId = source.RecordingId, ServiceName = source.ServiceName, EventTitle = source.EventTitle,
            NetworkId = source.NetworkId, TransportStreamId = source.TransportStreamId, ServiceId = source.ServiceId, EventId = source.EventId,
            ScheduledStartTime = source.ScheduledStartTime, Genre = source.Genre, GenreCodes = source.GenreCodes, ActualStartTime = source.ActualStartTime,
            ActualEndTime = source.ActualEndTime, Result = source.Result, EndReason = source.EndReason, FilePath = source.FilePath, FileCreated = source.FileCreated,
            Drop = source.Drop, Error = source.Error, Scramble = source.Scramble, QualityDataAvailable = source.QualityDataAvailable,
            QualityCompleteness = source.QualityCompleteness, QualitySource = source.QualitySource, ResourceReleaseState = source.ResourceReleaseState,
            ResultFinalized = source.ResultFinalized, Succeeded = true
        };
        return row;
    }


    private static bool IsSameRecordingOccurrence(TvAirRecordingResultDto existing, TvAirRecordingResultDto incoming)
    {
        if (!string.Equals(existing.ReservationId, incoming.ReservationId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (existing.ScheduledStartTime.ToUniversalTime() != incoming.ScheduledStartTime.ToUniversalTime())
            return false;
        if (existing.NetworkId != incoming.NetworkId
            || existing.TransportStreamId != incoming.TransportStreamId
            || existing.ServiceId != incoming.ServiceId
            || existing.EventId != incoming.EventId)
            return false;

        // EventId=0 reservations have no broadcast-event identity, so include the committed title as
        // the remaining occurrence discriminator. FilePath is deliberately not part of identity: the
        // same recording occurrence may only learn its final path late in the lifecycle, and a path
        // change must never weaken finalize-once protection.
        if (existing.EventId == 0
            && !string.Equals(existing.EventTitle, incoming.EventTitle, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static string SanitizePath(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : Sanitize(value);

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
            ["registryReleaseResult"] = RegistryReleaseResult,
            ["tunerStateAfterRelease"] = TunerStateAfterRelease,
            ["recoveryReservationResult"] = RecoveryReservationResult
        };
    }
}
