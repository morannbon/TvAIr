using System.Diagnostics;
using System.Text;
using TvAIr.Core;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// Host-owned external process execution authority for plugins.
/// The host owns process lifetime, plugin isolation, idempotency, bounded diagnostics, and cancellation.
/// Encoder policy and queues remain plugin-owned.
/// </summary>
internal sealed class PluginExternalJobHost : IDisposable
{
    private const int TailCharacterLimit = 4 * 1024;
    private const int MaxActiveJobsPerPlugin = 8192;
    // Keep every active job, but retain only a bounded recent terminal history per plugin.
    // ClientRequestId idempotency is retained for the same bounded job history.
    private const int MaxTerminalJobsPerPlugin = 512;

    private readonly object gate = new();
    private readonly Dictionary<string, JobEntry> jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> requestIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly LogRepository log;
    private readonly PluginTypedEventHub events;
    private bool disposed;

    public PluginExternalJobHost(LogRepository log, PluginTypedEventHub events)
    {
        this.log = log;
        this.events = events;
    }

    public TvAirExternalJobDto Enqueue(string pluginId, TvAirExternalJobRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owner = PluginIdentity.Normalize(pluginId);
        var validation = Validate(request);
        if (validation is not null)
        {
            return new TvAirExternalJobDto
            {
                Kind = request.Kind,
                StateCode = TvAirExternalJobState.Failed,
                State = TvAirExternalJobState.Failed.ToString(),
                ReservationId = NormalizeOptional(request.ReservationId),
                RecordingId = NormalizeOptional(request.RecordingId),
                ClientRequestId = NormalizeOptional(request.ClientRequestId),
                Accepted = false,
                FinishedAt = DateTimeOffset.Now,
                FailureCode = validation.Value.Code,
                Message = validation.Value.Message
            };
        }

        JobEntry entry;
        lock (gate)
        {
            ThrowIfDisposed();
            var clientRequestId = request.ClientRequestId.Trim();
            var requestKey = MakeRequestKey(owner, clientRequestId);
            if (requestIndex.TryGetValue(requestKey, out var existingId) && jobs.TryGetValue(existingId, out var existing))
                return existing.ToDto();

            PruneTerminalHistoryLocked(owner);
            if (jobs.Values.Count(x => SameOwner(x, owner) && !x.IsTerminal) >= MaxActiveJobsPerPlugin)
            {
                return new TvAirExternalJobDto
                {
                    Kind = request.Kind, StateCode = TvAirExternalJobState.Failed, State = TvAirExternalJobState.Failed.ToString(),
                    ReservationId = NormalizeOptional(request.ReservationId), RecordingId = NormalizeOptional(request.RecordingId),
                    ClientRequestId = clientRequestId, Accepted = false, FinishedAt = DateTimeOffset.Now,
                    FailureCode = "host_job_capacity_reached", Message = "External job active-session capacity was reached."
                };
            }

            entry = new JobEntry
            {
                PluginId = owner,
                JobId = Guid.NewGuid().ToString("N"),
                Kind = request.Kind,
                ReservationId = NormalizeOptional(request.ReservationId),
                RecordingId = NormalizeOptional(request.RecordingId),
                ClientRequestId = clientRequestId,
                ProcessSpec = request.Process!,
                State = TvAirExternalJobState.Queued,
                Accepted = true,
                QueuedAt = DateTimeOffset.Now
            };
            jobs[entry.JobId] = entry;
            requestIndex[requestKey] = entry.JobId;
        }

        Publish(entry);
        _ = Task.Run(() => RunAsync(entry));
        return entry.ToDto();
    }

    public TvAirExternalJobDto? Get(string pluginId, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        var owner = PluginIdentity.Normalize(pluginId);
        lock (gate)
        {
            if (jobs.TryGetValue(jobId.Trim(), out var entry) && SameOwner(entry, owner)) return entry.ToDto();
        }
        return null;
    }

    public IReadOnlyList<TvAirExternalJobDto> List(string pluginId, TvAirExternalJobQueryDto? query)
    {
        var owner = PluginIdentity.Normalize(pluginId);
        query ??= new TvAirExternalJobQueryDto();
        lock (gate)
        {
            IEnumerable<JobEntry> rows = jobs.Values.Where(x => SameOwner(x, owner));
            if (query.Kind.HasValue) rows = rows.Where(x => x.Kind == query.Kind.Value);
            if (query.StateCode.HasValue) rows = rows.Where(x => x.State == query.StateCode.Value);
            else if (!string.IsNullOrWhiteSpace(query.State) && Enum.TryParse<TvAirExternalJobState>(query.State, true, out var parsed)) rows = rows.Where(x => x.State == parsed);
            if (!string.IsNullOrWhiteSpace(query.RecordingId)) rows = rows.Where(x => string.Equals(x.RecordingId, query.RecordingId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.ClientRequestId)) rows = rows.Where(x => string.Equals(x.ClientRequestId, query.ClientRequestId.Trim(), StringComparison.Ordinal));
            return rows.OrderByDescending(x => x.QueuedAt).Select(x => x.ToDto()).ToList();
        }
    }

    public bool Cancel(string pluginId, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;
        var owner = PluginIdentity.Normalize(pluginId);
        JobEntry? entry;
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId.Trim(), out entry) || !SameOwner(entry, owner) || entry.IsTerminal) return false;
            entry.CancelRequested = true;
            if (entry.State == TvAirExternalJobState.Queued && entry.Process is null)
            {
                entry.State = TvAirExternalJobState.Cancelled;
                entry.FinishedAt = DateTimeOffset.Now;
                entry.Message = "Cancelled before process start.";
            }
        }

        TryKillTree(entry);
        if (entry.State == TvAirExternalJobState.Cancelled)
        {
            Publish(entry);
            PruneTerminalHistory(owner);
        }
        return true;
    }

    public void CancelPlugin(string pluginId)
    {
        var owner = PluginIdentity.Normalize(pluginId);
        List<JobEntry> active;
        lock (gate)
        {
            active = jobs.Values.Where(x => SameOwner(x, owner) && !x.IsTerminal).ToList();
            foreach (var entry in active)
            {
                entry.CancelRequested = true;
                if (entry.State == TvAirExternalJobState.Queued && entry.Process is null)
                {
                    entry.State = TvAirExternalJobState.Cancelled;
                    entry.FinishedAt = DateTimeOffset.Now;
                    entry.Message = "Cancelled because the owning plugin stopped.";
                }
            }
        }
        var terminalPublished = false;
        foreach (var entry in active)
        {
            TryKillTree(entry);
            if (entry.State == TvAirExternalJobState.Cancelled)
            {
                Publish(entry);
                terminalPublished = true;
            }
        }
        if (terminalPublished) PruneTerminalHistory(owner);
        WaitForStoppedJobs(active, owner);
    }

    private async Task RunAsync(JobEntry entry)
    {
        Process? process = null;
        Task? outputDrain = null;
        bool started = false;
        bool released = true;
        int? exitCode = null;
        Exception? executionError = null;
        string? releaseFailure = null;
        try
        {
            lock (gate)
            {
                if (entry.IsTerminal || entry.CancelRequested) return;
            }
            var spec = entry.ProcessSpec;
            var executable = Path.GetFullPath(spec.ExecutablePath.Trim());
            if (!File.Exists(executable)) throw new FileNotFoundException("Executable file was not found.", executable);
            var workingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                ? Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
                : Path.GetFullPath(spec.WorkingDirectory.Trim());
            if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException($"Working directory was not found: {workingDirectory}");
            var psi = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = workingDirectory,
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in spec.Arguments ?? Array.Empty<string>()) psi.ArgumentList.Add(argument ?? string.Empty);
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            lock (gate)
            {
                if (entry.IsTerminal || entry.CancelRequested) return;
                entry.Process = process;
            }
            if (!process.Start()) throw new InvalidOperationException("External process did not start.");
            started = true;
            outputDrain = Task.WhenAll(CaptureTailAsync(process.StandardOutput, entry.StdoutTail),
                CaptureTailAsync(process.StandardError, entry.StderrTail));
            lock (gate)
            {
                if (!entry.CancelRequested)
                {
                    entry.State = TvAirExternalJobState.Running;
                    entry.StartedAt = DateTimeOffset.Now;
                    entry.Message = null;
                }
            }
            if (entry.CancelRequested) TryKillTree(entry); else Publish(entry);
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            executionError = ex;
        }
        finally
        {
            // One terminal boundary for success, nonzero exit, cancellation and exceptions.
            // A failure notification must never precede the owned process/file-handle cleanup.
            if (process is not null && started)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        TryKillTree(entry, process);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    exitCode = process.ExitCode;
                }
                catch (Exception ex)
                {
                    released = false;
                    releaseFailure = "process_stop_not_confirmed";
                    log.Add("PLUGIN_EXTERNAL_JOB_STOP", entry.PluginId,
                        $"jobId={entry.JobId} result=STOP_FAILED error={Sanitize(ex.Message)} rule=plugin_external_job_host_contract");
                }
                if (outputDrain is not null)
                {
                    try { await outputDrain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        released = false;
                        releaseFailure ??= "output_drain_failed";
                        executionError ??= ex;
                        // Closing our pipe readers cannot terminate or reconnect a process.
                        try { process.StandardOutput.Dispose(); } catch { }
                        try { process.StandardError.Dispose(); } catch { }
                        _ = outputDrain.ContinueWith(task => { _ = task.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
            }
            try { process?.Dispose(); }
            catch (Exception ex) { released = false; releaseFailure ??= "process_handle_release_failed"; executionError ??= ex; }
            bool publishTerminal;
            lock (gate)
            {
                entry.Process = null;
                entry.ResourcesReleased = released;
                publishTerminal = !entry.IsTerminal;
                if (publishTerminal)
                {
                    entry.ExitCode = exitCode;
                    entry.FinishedAt = DateTimeOffset.Now;
                    if (!released)
                    {
                        entry.State = TvAirExternalJobState.Failed;
                        entry.FailureCode = releaseFailure;
                        entry.Message = "External process resource release was not confirmed.";
                    }
                    else if (entry.CancelRequested)
                    {
                        entry.State = TvAirExternalJobState.Cancelled;
                        entry.Message = "Cancelled.";
                    }
                    else if (executionError is not null)
                    {
                        entry.State = TvAirExternalJobState.Failed;
                        entry.FailureCode = FailureCode(executionError);
                        entry.Message = Sanitize(executionError.Message);
                    }
                    else if (exitCode == 0)
                    {
                        entry.State = TvAirExternalJobState.Succeeded;
                        entry.Message = null;
                    }
                    else
                    {
                        entry.State = TvAirExternalJobState.Failed;
                        entry.FailureCode = "nonzero_exit_code";
                        entry.Message = $"External process exited with code {exitCode}.";
                    }
                }
            }
            try
            {
                if (publishTerminal) Publish(entry);
                PruneTerminalHistory(entry.PluginId);
            }
            finally { entry.Completion.TrySetResult(); }
        }
    }

    private static async Task CaptureTailAsync(StreamReader reader, BoundedTail tail)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            if (read <= 0) return;
            tail.Append(buffer.AsSpan(0, read));
        }
    }

    private void Publish(JobEntry entry)
    {
        var dto = entry.ToDto();
        events.Publish(new TvAirEventDto
        {
            EventType = TvAirEventType.ExternalJobChanged,
            EntityId = $"external-job:{dto.JobId}",
            SourceOwnerId = entry.PluginId,
            ChangeKind = dto.StateCode.ToString(),
            ExternalJob = dto,
            Details = new Dictionary<string, string>
            {
                ["jobId"] = dto.JobId,
                ["state"] = dto.StateCode.ToString()
            }
        });
        log.Add("PLUGIN_EXTERNAL_JOB", entry.PluginId,
            $"jobId={dto.JobId} state={dto.StateCode} accepted={dto.Accepted} kind={dto.Kind} exitCode={(dto.ExitCode?.ToString() ?? "-")} failureCode={(dto.FailureCode ?? "-")} rule=plugin_external_job_host_contract");
    }

    private static (string Code, string Message)? Validate(TvAirExternalJobRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientRequestId)) return ("client_request_id_required", "ClientRequestId is required.");
        if (request.Process is null) return ("process_required", "Process specification is required.");
        if (string.IsNullOrWhiteSpace(request.Process.ExecutablePath)) return ("executable_path_required", "ExecutablePath is required.");
        if (!Path.IsPathFullyQualified(request.Process.ExecutablePath)) return ("executable_path_must_be_absolute", "ExecutablePath must be an absolute path.");
        if (!string.IsNullOrWhiteSpace(request.Process.WorkingDirectory) && !Path.IsPathFullyQualified(request.Process.WorkingDirectory)) return ("working_directory_must_be_absolute", "WorkingDirectory must be an absolute path when specified.");
        return null;
    }

    private void PruneTerminalHistory(string pluginId)
    {
        lock (gate) PruneTerminalHistoryLocked(pluginId);
    }

    private void PruneTerminalHistoryLocked(string pluginId)
    {
        var stale = jobs.Values
            .Where(x => SameOwner(x, pluginId) && x.IsTerminal)
            .OrderByDescending(x => x.FinishedAt ?? x.QueuedAt)
            .ThenByDescending(x => x.QueuedAt)
            .Skip(MaxTerminalJobsPerPlugin)
            .ToList();

        foreach (var entry in stale)
        {
            jobs.Remove(entry.JobId);
            var requestKey = MakeRequestKey(entry.PluginId, entry.ClientRequestId);
            if (requestIndex.TryGetValue(requestKey, out var indexedJobId)
                && string.Equals(indexedJobId, entry.JobId, StringComparison.OrdinalIgnoreCase))
            {
                requestIndex.Remove(requestKey);
            }
        }
    }

    private static bool SameOwner(JobEntry entry, string pluginId) => string.Equals(entry.PluginId, pluginId, StringComparison.OrdinalIgnoreCase);
    private static string MakeRequestKey(string pluginId, string clientRequestId) => pluginId + "\n" + clientRequestId;
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FailureCode(Exception ex) => ex switch
    {
        FileNotFoundException => "executable_not_found",
        DirectoryNotFoundException => "working_directory_not_found",
        UnauthorizedAccessException => "access_denied",
        _ => "process_start_or_execution_failed"
    };
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "External process failed.";
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 240 ? text : text[..240];
    }

    private void TryKillTree(JobEntry? entry, Process? ownedProcess = null)
    {
        if (entry is null) return;
        var process = ownedProcess ?? entry.Process;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // A queued cancellation can precede Process.Start. RunAsync rechecks cancellation
            // after Start and owns the final termination/Completion signal.
        }
        catch (Exception ex)
        {
            log.Add("PLUGIN_EXTERNAL_JOB_STOP", entry.PluginId,
                $"jobId={entry.JobId} result=STOP_FAILED error={Sanitize(ex.Message)} rule=plugin_external_job_host_contract");
        }
    }

    private void WaitForStoppedJobs(IReadOnlyCollection<JobEntry> active, string source)
    {
        if (active.Count == 0) return;
        var completion = Task.WhenAll(active.Select(x => x.Completion.Task));
        var completed = completion.Wait(TimeSpan.FromSeconds(5));
        log.Add("PLUGIN_EXTERNAL_JOB_STOP", source,
            $"result={(!completed ? "TIMEOUT" : active.All(x => x.ResourcesReleased) ? "COMPLETED" : "RELEASE_FAILED")} requested={active.Count} completed={active.Count(x => x.Completion.Task.IsCompleted && x.ResourcesReleased)} " +
            "timeoutMs=5000 boundary=process_exit_and_output_drain rule=plugin_external_job_host_contract");
    }

    public void Dispose()
    {
        List<JobEntry> active;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            // Include runners whose terminal state was published but whose process/streams
            // are still being disposed. Admission closes before cancellation is sent.
            active = jobs.Values.Where(x => !x.Completion.Task.IsCompleted).ToList();
            foreach (var entry in active)
                if (!entry.IsTerminal) entry.CancelRequested = true;
        }
        foreach (var entry in active)
        {
            if (!Cancel(entry.PluginId, entry.JobId)) TryKillTree(entry);
        }
        WaitForStoppedJobs(active, "HostShutdown");
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PluginExternalJobHost));
    }

    private sealed class JobEntry
    {
        public required string PluginId { get; init; }
        public required string JobId { get; init; }
        public TvAirExternalJobKind Kind { get; init; }
        public string? ReservationId { get; init; }
        public string? RecordingId { get; init; }
        public required string ClientRequestId { get; init; }
        public required TvAirExternalProcessSpecDto ProcessSpec { get; init; }
        public TvAirExternalJobState State { get; set; }
        public bool Accepted { get; init; }
        public DateTimeOffset QueuedAt { get; init; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public int? ExitCode { get; set; }
        public string? FailureCode { get; set; }
        public string? Message { get; set; }
        public bool CancelRequested { get; set; }
        public Process? Process { get; set; }
        public bool ResourcesReleased { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BoundedTail StdoutTail { get; } = new(TailCharacterLimit);
        public BoundedTail StderrTail { get; } = new(TailCharacterLimit);
        public bool IsTerminal => State is TvAirExternalJobState.Succeeded or TvAirExternalJobState.Failed or TvAirExternalJobState.Cancelled;

        public TvAirExternalJobDto ToDto() => new()
        {
            JobId = JobId,
            Kind = Kind,
            StateCode = State,
            State = State.ToString(),
            ReservationId = ReservationId,
            RecordingId = RecordingId,
            ClientRequestId = ClientRequestId,
            Accepted = Accepted,
            QueuedAt = QueuedAt,
            StartedAt = StartedAt,
            FinishedAt = FinishedAt,
            ExitCode = ExitCode,
            FailureCode = FailureCode,
            Message = Message,
            StandardOutputTail = StdoutTail.ToString(),
            StandardErrorTail = StderrTail.ToString()
        };
    }

    private sealed class BoundedTail
    {
        private readonly int limit;
        private readonly StringBuilder value = new();
        private readonly object tailGate = new();
        public BoundedTail(int limit) => this.limit = limit;
        public void Append(ReadOnlySpan<char> text)
        {
            lock (tailGate)
            {
                value.Append(text);
                if (value.Length > limit) value.Remove(0, value.Length - limit);
            }
        }
        public override string ToString()
        {
            lock (tailGate) return value.ToString();
        }
    }
}
