using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TvAIr.Storage;

namespace TvAIrEpgRec;

// One physical-file lifecycle for the primary recording and every chain member.
// The receiver, descrambler and tuner remain alive when the file changes.
internal readonly record struct PhysicalOutputQuality(long Drops, long Errors, long Sync, long Scrambled, long TargetScrambled);

internal sealed class RelayRecordingOutput : IAsyncDisposable
{
    private FileStream? stream;
    private readonly DiskRelayPolicy? policy;
    private readonly JsonObject? declaration;
    private readonly string? controlDirectory;
    private readonly Func<string, long?> available;
    private readonly Func<string, Task> log;
    private string path;
    private string resultPath = "";
    private string runtimePath = "";
    private readonly string originalName;
    private TransportQualityState quality = new();
    private readonly Func<PhysicalOutputQuality>? readQuality;
    private PhysicalOutputQuality qualityStart;
    private long targetScrambled;
    private DateTimeOffset startedAt = DateTimeOffset.Now;
    private DateTime nextCheck = DateTime.Now.AddSeconds(2);
    private int cursor;
    private int part = 1;
    private bool disposed;
    private Exception? closeFailure;
    private bool noCandidateLogged;
    private readonly List<string> pendingMessages = new();
    public bool Managed => policy is not null;

    public RelayRecordingOutput(string outputPath, string? declarationJson, string? controlDirectory,
        DiskRelayPolicy? policy, Func<string, Task> log, Func<string, long?>? available = null, Func<PhysicalOutputQuality>? readQuality = null)
    {
        path = Path.GetFullPath(outputPath);
        originalName = Path.GetFileNameWithoutExtension(path);
        this.controlDirectory = controlDirectory;
        this.log = log;
        this.available = available ?? DiskRelayPolicy.AvailableBytes;
        this.readQuality = readQuality;
        qualityStart = readQuality?.Invoke() ?? default;
        if (policy is not null)
        {
            if (string.IsNullOrWhiteSpace(declarationJson) || string.IsNullOrWhiteSpace(controlDirectory))
                throw new InvalidDataException("Disk relay requires a durable recording declaration.");
            declaration = JsonNode.Parse(declarationJson)!.AsObject();
            resultPath = declaration["ResultPath"]!.GetValue<string>() + ".physical.json";
            runtimePath = declaration["RuntimeStatsPath"]!.GetValue<string>();
            this.policy = policy;
        }
        var free = policy is null ? null : this.available(Path.GetDirectoryName(path)!);
        var initial = free.HasValue && free.Value <= policy!.LowFreeSpaceBytes ? OpenAlternative(1) : null;
        if (initial is not null)
        {
            path = initial.Path; resultPath = initial.Proof; runtimePath = initial.Proof + ".jsonl";
            stream = initial.Stream;
            pendingMessages.Add($"result=INITIAL_SELECTED path={path} receiverRestart=False");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = Open(path, FileMode.Create);
        }
    }

    private static FileStream Open(string path, FileMode mode) => new(path, mode, FileAccess.Write,
        FileShare.ReadWrite, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, IReadOnlyCollection<int> targetPids)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.Length == 0) return;
        await ReportPendingAsync().ConfigureAwait(false);
        if (policy is not null && DateTime.Now >= nextCheck)
        {
            nextCheck = DateTime.Now.AddSeconds(2);
            var bytes = available(Path.GetDirectoryName(path)!);
            if (bytes.HasValue && bytes.Value <= policy.LowFreeSpaceBytes)
                await RelayAsync().ConfigureAwait(false);
        }
        await stream!.WriteAsync(buffer).ConfigureAwait(false);
        if (policy is not null && readQuality is null) Observe(buffer, targetPids);
    }

    private void Observe(ReadOnlyMemory<byte> buffer, IReadOnlyCollection<int> targetPids)
    {
        var span = buffer.Span;
        for (var i = 0; i + 188 <= span.Length; i += 188)
        {
            var packet = span.Slice(i, 188);
            quality.ObservePacket(packet);
            var pid = ((packet[1] & 0x1f) << 8) | packet[2];
            if ((packet[3] & 0xc0) != 0 && targetPids.Contains(pid)) targetScrambled++;
        }
    }

    private sealed record Destination(FileStream Stream, string Path, string Proof);

    private Destination? OpenAlternative(int index)
    {
        while (cursor < policy!.Folders.Length)
        {
            var folder = policy.SelectNext(ref cursor, available: available);
            if (folder is null) break;
            cursor++;
            var nextPath = Path.Combine(folder, originalName + $".part{index}_{Guid.NewGuid():N}.ts");
            var proof = Path.Combine(Path.GetDirectoryName(resultPath)!, "record_chain_segment_relay_" + Guid.NewGuid().ToString("N") + ".json");
            var next = declaration!.DeepClone().AsObject();
            next["FileId"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof.ToUpperInvariant())));
            next["OutputPath"] = nextPath; next["ResultPath"] = proof; next["RuntimeStatsPath"] = proof + ".jsonl";
            next["DiskRelayPart"] = true; next["DiskRelayPartIndex"] = index;
            next["DiskRelaySourcePath"] = declaration["OutputPath"]!.GetValue<string>();
            FileStream? destination = null;
            try
            {
                destination = Open(nextPath, FileMode.CreateNew);
                var directory = Path.Combine(controlDirectory!, "relay");
                Directory.CreateDirectory(directory);
                // An immutable declaration precedes the first TS byte, also for an initial redirect.
                AtomicWrite(Path.Combine(directory, next["FileId"]!.GetValue<string>() + ".json"), next.ToJsonString());
                return new Destination(destination, nextPath, proof);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                destination?.Dispose();
                pendingMessages.Add($"result=CANDIDATE_REJECTED path={nextPath} error={ex.GetType().Name}");
            }
        }
        return null;
    }

    private async Task ReportPendingAsync()
    {
        foreach (var message in pendingMessages) await log(message).ConfigureAwait(false);
        pendingMessages.Clear();
    }

    private async Task RelayAsync()
    {
        var next = OpenAlternative(part + 1);
        var adopted = false;
        try
        {
            await ReportPendingAsync().ConfigureAwait(false);
            if (next is not null)
            {
                await ClosePhysicalAsync("disk_relay").ConfigureAwait(false);
                path = next.Path; resultPath = next.Proof; runtimePath = next.Proof + ".jsonl";
                stream = next.Stream;
                adopted = true;
                quality = new(); targetScrambled = 0;
                qualityStart = readQuality?.Invoke() ?? default;
                startedAt = DateTimeOffset.Now; part++; noCandidateLogged = false;
                await log($"result=SWITCHED part={part} path={path} receiverRestart=False").ConfigureAwait(false);
                return;
            }
            if (!noCandidateLogged)
            { noCandidateLogged = true; await log($"result=NO_ELIGIBLE_FOLDER path={path} action=continue_current_output").ConfigureAwait(false); }
        }
        finally
        {
            if (next is not null && !adopted)
                await next.Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static void AtomicWrite(string path, string contents)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents, Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    public Task FlushAsync() => stream?.FlushAsync() ?? Task.CompletedTask;

    private async Task ClosePhysicalAsync(string reason)
    {
        if (closeFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
        try { await ClosePhysicalCoreAsync(reason).ConfigureAwait(false); }
        catch (Exception ex) { closeFailure = ex; throw; }
    }

    private async Task ClosePhysicalCoreAsync(string reason)
    {
        var closing = stream;
        if (closing is null) return;
        stream = null;
        long length;
        try
        {
            length = closing.Position;
            await closing.FlushAsync().ConfigureAwait(false);
        }
        finally { await closing.DisposeAsync().ConfigureAwait(false); }
        if (policy is null) return;
        var endedAt = DateTimeOffset.Now;
        var observed = readQuality?.Invoke() ?? new PhysicalOutputQuality(quality.ContinuityDrops, quality.ContinuityErrors, quality.SyncErrors, quality.ScrambledPackets, targetScrambled);
        var delta = new PhysicalOutputQuality(observed.Drops - qualityStart.Drops, observed.Errors - qualityStart.Errors, observed.Sync - qualityStart.Sync, observed.Scrambled - qualityStart.Scrambled, observed.TargetScrambled - qualityStart.TargetScrambled);
        var success = length > 0 && delta.Sync == 0 && delta.TargetScrambled == 0;
        var result = new
        {
            success, outputPath = path, startedAt, endedAt, bytesWritten = length,
            packetsWritten = length / 188, runtimeStatsPath = runtimePath,
            outputContinuityDrops = delta.Drops, outputContinuityErrors = delta.Errors,
            outputSyncErrors = delta.Sync, outputScrambledPackets = delta.Scrambled,
            outputTargetMediaScrambledPackets = delta.TargetScrambled,
            qualityVerdict = success ? "LIVE_CLEAR_TARGET_SERVICE_TS_OK" : "DISK_RELAY_OUTPUT_WARN",
            message = "disk_relay_file_result"
        };
        AtomicWrite(runtimePath, JsonSerializer.Serialize(new { final = true, result }));
        AtomicWrite(resultPath, JsonSerializer.Serialize(new
        { success, exitCode = 0, recorderPid = Environment.ProcessId, message = reason, result }));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed && closeFailure is null) return;
        disposed = true;
        await ClosePhysicalAsync("recording_sink_disposed").ConfigureAwait(false);
    }
}
