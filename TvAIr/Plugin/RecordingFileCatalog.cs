using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TvAIr.Core;
using TvAIr.Schedule;
using TvAIrPlugin;

namespace TvAIr.Plugin;

// Durable control declarations are preparation evidence, not proof that a TS exists or is closed.
// Only this catalog assigns the persistent release sequence. It never owns a tuner.
public sealed class RecordingFileCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string connectionString;
    private readonly LogRepository log;
    private readonly Database database;
    private readonly LabWithdrawalJournal withdrawalJournal;
    private readonly Func<string,TvAirRecordingResultDto?>? legacyResult;
    private readonly object gate = new();
    private bool schemaReady;
    public string PendingControlDirectory { get; }
    private int controlOffset;
    internal CancellationToken StopToken { get; set; }
    public RecordingFileCatalog(Database database, LogRepository log, Func<string,TvAirRecordingResultDto?>? legacyResult = null)
    {
        this.log = log;
        this.database = database;
        withdrawalJournal = new LabWithdrawalJournal(database);
        this.legacyResult = legacyResult;
        PendingControlDirectory = Path.Combine(database.DataDirectory,"recording-file-controls");
        connectionString = new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(database.DataDirectory,"recording-files.db"), DefaultTimeout = 1
        }.ToString();
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        try
        {
        c.Open();
        lock(gate)
        {
        if(schemaReady)return c;
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS catalog_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            INSERT OR IGNORE INTO catalog_meta(key,value) VALUES('journal_id',lower(hex(randomblob(16))));
            CREATE TABLE IF NOT EXISTS released_files(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id TEXT NOT NULL UNIQUE,
                job_path TEXT NOT NULL,
                payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS released_job ON released_files(job_path);
            CREATE TABLE IF NOT EXISTS control_scan(store_path TEXT PRIMARY KEY, generation INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS control_inputs(store_path TEXT NOT NULL,file_id TEXT NOT NULL,declaration TEXT NOT NULL,
                state INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(store_path,file_id));
            CREATE INDEX IF NOT EXISTS control_pending ON control_inputs(state,file_id);
            CREATE INDEX IF NOT EXISTS control_file ON control_inputs(file_id);
            """;
        cmd.ExecuteNonQuery();
        schemaReady=true;
        }
        return c;
        }
        catch { c.Dispose(); throw; }
    }
    public static string CreateDeclaration(Reservation reservation, string outputPath, string resultPath,
        string runtimeStatsPath, string jobPath, IReadOnlyList<TvAirPluginSelectionBindingDto> bindings)
    {
        var artifact=Path.GetFullPath(resultPath);
        var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifact.ToUpperInvariant())));
        using var process=Process.GetCurrentProcess();
        var manifest=new RecordingFileManifest(id,"R"+reservation.Id,
            reservation.RecoveryParentReservationId is int parent ? "R"+parent : null,
            reservation.RecordingRecoveryChainId ?? "",reservation.Source.ToString(),
            reservation.Title ?? "",reservation.ServiceName ?? "",Path.GetFullPath(outputPath),
            artifact,Path.GetFullPath(runtimeStatsPath),Path.GetFullPath(jobPath),
            process.Id,process.StartTime.ToUniversalTime().Ticks,bindings.ToArray());
        return JsonSerializer.Serialize(manifest,Json);
    }
    public void Register(Reservation reservation, string outputPath, string resultPath,
        string runtimeStatsPath, string jobPath, IReadOnlyList<TvAirPluginSelectionBindingDto> bindings)
        => QueueDeclaration(CreateDeclaration(reservation,outputPath,resultPath,runtimeStatsPath,jobPath,bindings));
    private void QueueDeclaration(string json)
    {
        var manifest=JsonSerializer.Deserialize<RecordingFileManifest>(json,Json)
            ?? throw new InvalidDataException("Missing recording-file declaration.");
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT OR IGNORE INTO control_inputs(store_path,file_id,declaration,state) VALUES('', $id,$data,CASE WHEN EXISTS(SELECT 1 FROM released_files WHERE file_id=$id) THEN 1 ELSE 0 END)";
        cmd.Parameters.AddWithValue("$id",manifest.FileId);cmd.Parameters.AddWithValue("$data",json);cmd.ExecuteNonQuery();
    }
    private bool JobHasRun(string job)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT EXISTS(SELECT 1 FROM released_files WHERE job_path=$job)";
        cmd.Parameters.AddWithValue("$job",job);return Convert.ToInt64(cmd.ExecuteScalar())!=0;
    }
    // Durable declarations travel in the existing immutable control publication. This avoids
    // new file/SQLite IO in the chain boundary and survives a crash before catalog import.
    private void ImportControls(Func<string,bool?> workerAlive)
    {
        if(!Directory.Exists(PendingControlDirectory))return;
        var stores=Directory.EnumerateDirectories(PendingControlDirectory).Skip(controlOffset).Take(8).ToArray();
        if(stores.Length==0)
        {
            controlOffset=0;
            stores=Directory.EnumerateDirectories(PendingControlDirectory).Take(8).ToArray();
            if(stores.Length==0)return;
        }
        var retained=0;
        foreach(var store in stores)
        {
            StopToken.ThrowIfCancellationRequested();
            try
            {
                using var c=Open();
                long generation;
                using(var read=c.CreateCommand())
                { read.CommandText="SELECT generation FROM control_scan WHERE store_path=$store";read.Parameters.AddWithValue("$store",store);generation=Convert.ToInt64(read.ExecuteScalar()??0L); }
                // Control generations are contiguous D20 filenames. Persist the cursor; never
                // enumerate/decode historical generations again, including across Host restarts.
                for(var count=0;count<64;count++)
                {
                    StopToken.ThrowIfCancellationRequested();
                    var path=Path.Combine(store,$"{generation+1:D20}.json");if(!File.Exists(path))break;
                    using var doc=JsonDocument.Parse(File.ReadAllText(path));
                    if(!doc.RootElement.TryGetProperty("segments",out var segments))throw new InvalidDataException("Missing control segments.");
                    using var tx=c.BeginTransaction();
                    foreach(var segment in segments.EnumerateArray())
                    {
                        if(!segment.TryGetProperty("recordingFileDeclaration",out var raw)||string.IsNullOrWhiteSpace(raw.GetString()))continue;
                        var json=raw.GetString()!;var m=JsonSerializer.Deserialize<RecordingFileManifest>(json,Json)!;
                        using var add=c.CreateCommand();add.Transaction=tx;
                        add.CommandText="INSERT OR IGNORE INTO control_inputs(store_path,file_id,declaration,state) VALUES($store,$id,$data,CASE WHEN EXISTS(SELECT 1 FROM released_files WHERE file_id=$id) THEN 1 ELSE 0 END)";
                        add.Parameters.AddWithValue("$store",store);add.Parameters.AddWithValue("$id",m.FileId);add.Parameters.AddWithValue("$data",json);add.ExecuteNonQuery();
                    }
                    using(var advance=c.CreateCommand())
                    { advance.Transaction=tx;advance.CommandText="INSERT INTO control_scan(store_path,generation) VALUES($store,$gen) ON CONFLICT(store_path) DO UPDATE SET generation=excluded.generation";
                      advance.Parameters.AddWithValue("$store",store);advance.Parameters.AddWithValue("$gen",generation+1);advance.ExecuteNonQuery(); }
                    tx.Commit();generation++;
                }
                long pending;
                using(var read=c.CreateCommand())
                {
                    read.CommandText="SELECT EXISTS(SELECT 1 FROM control_inputs WHERE store_path=$store AND state=0)";
                    read.Parameters.AddWithValue("$store",store);pending=Convert.ToInt64(read.ExecuteScalar());
                }
                if(pending!=0){retained++;continue;}
                if(File.Exists(Path.Combine(store,$"{generation+1:D20}.json"))){retained++;continue;}
                string? first=null;
                using(var read=c.CreateCommand())
                { read.CommandText="SELECT declaration FROM control_inputs WHERE store_path=$store LIMIT 1";read.Parameters.AddWithValue("$store",store);first=read.ExecuteScalar() as string; }
                if(first is null || workerAlive(JsonSerializer.Deserialize<RecordingFileManifest>(first,Json)!.JobPath)!=false){retained++;continue;}
                if(withdrawalJournal.HasUnsettledForControl(store)){retained++;continue;}
                var cleanupReady=true;
                using(var read=c.CreateCommand())
                {
                    read.CommandText="SELECT declaration,state FROM control_inputs WHERE store_path=$store";read.Parameters.AddWithValue("$store",store);
                    using var rows=read.ExecuteReader();
                    while(rows.Read())
                    {
                        StopToken.ThrowIfCancellationRequested();
                        var m=JsonSerializer.Deserialize<RecordingFileManifest>(rows.GetString(0),Json)!;
                        var legacyReady = legacyResult?.Invoke(m.ReservationId) is {ResultFinalized:true} legacy
                            && string.Equals(legacy.FilePath,m.OutputPath,StringComparison.OrdinalIgnoreCase);
                        var orphanSettled = m.ReservationId.StartsWith("R",StringComparison.Ordinal)
                            && int.TryParse(m.ReservationId.AsSpan(1),out var reservationId)
                            && ReservationStore.IsControlRecoverySettled(database,reservationId,HostAlive(m.HostPid,m.HostStartTicks));
                        if(rows.GetInt32(1)==1 && !legacyReady && !orphanSettled)
                        {cleanupReady=false;break;}
                    }
                }
                if(!cleanupReady){retained++;continue;}
                var target=Path.GetFullPath(store);var parent=Path.GetFullPath(PendingControlDirectory)+Path.DirectorySeparatorChar;
                if(!target.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Control cleanup outside catalog root.");
                using(var read=c.CreateCommand())
                {
                    read.CommandText="SELECT declaration FROM control_inputs WHERE store_path=$store";read.Parameters.AddWithValue("$store",store);
                    using var rows=read.ExecuteReader();
                    while(rows.Read())
                    {
                        StopToken.ThrowIfCancellationRequested();
                        var m=JsonSerializer.Deserialize<RecordingFileManifest>(rows.GetString(0),Json)!;
                        foreach(var artifact in new[]{m.ResultPath,m.RuntimeStatsPath})
                        {
                            var full=Path.GetFullPath(artifact);var owner=Path.GetDirectoryName(Path.GetFullPath(m.JobPath))!+Path.DirectorySeparatorChar;
                            if(full.StartsWith(owner,StringComparison.OrdinalIgnoreCase)
                                && Path.GetFileName(full).StartsWith("record_chain_segment_",StringComparison.OrdinalIgnoreCase))File.Delete(full);
                        }
                    }
                }
                Directory.Delete(target,recursive:true);
                using(var clean=c.CreateCommand())
                { clean.CommandText="DELETE FROM control_inputs WHERE store_path=$store; DELETE FROM control_scan WHERE store_path=$store";clean.Parameters.AddWithValue("$store",store);clean.ExecuteNonQuery(); }
            }
            catch(Exception ex){retained++;log.Add("RECORDING_FILE_CATALOG","ControlRetry",$"result=PENDING errorType={ex.GetType().Name}");}
        }
        controlOffset+=retained;
    }
    public TvAirFinalizedRecordingFilePageDto ListFinalized(TvAirFinalizedRecordingFileQueryDto? query=null)
    {
        query ??=new();
        if(query.AfterSequence<0 || query.Limit<1 || query.Limit>500)
            throw new ArgumentOutOfRangeException(nameof(query),"Sequence >= 0; limit 1..500.");
        using var c=Open();
        string journal;
        using(var meta=c.CreateCommand()){meta.CommandText="SELECT value FROM catalog_meta WHERE key='journal_id'";journal=(string)meta.ExecuteScalar()!;}
        if(query.JournalId is not null && query.JournalId!=journal)
            throw new InvalidOperationException("Recording-file journal changed; explicit cursor reconciliation is required.");
        if(query.AfterSequence>0)
        {
            if(query.JournalId is null || string.IsNullOrWhiteSpace(query.AfterRecordingFileId))
                throw new ArgumentException("A nonzero cursor requires JournalId and AfterRecordingFileId.",nameof(query));
            using var anchor=c.CreateCommand();anchor.CommandText="SELECT file_id FROM released_files WHERE sequence=$seq";
            anchor.Parameters.AddWithValue("$seq",query.AfterSequence);
            if(!string.Equals(anchor.ExecuteScalar() as string,query.AfterRecordingFileId,StringComparison.Ordinal))
                throw new InvalidOperationException("Recording-file cursor anchor changed; explicit reconciliation is required.");
        }
        using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT sequence,payload FROM released_files WHERE sequence>$after ORDER BY sequence LIMIT $limit";
        cmd.Parameters.AddWithValue("$after",query.AfterSequence);cmd.Parameters.AddWithValue("$limit",query.Limit+1);
        using var reader=cmd.ExecuteReader();var items=new List<TvAirFinalizedRecordingFileDto>();
        while(reader.Read())
            items.Add(JsonSerializer.Deserialize<TvAirFinalizedRecordingFileDto>(reader.GetString(1),Json)! with { Sequence=reader.GetInt64(0) });
        var more=items.Count>query.Limit;if(more)items.RemoveAt(items.Count-1);
        return new(){JournalId=journal,Items=items,
            NextSequence=items.Count==0?query.AfterSequence:items[^1].Sequence,
            NextRecordingFileId=items.Count==0?query.AfterRecordingFileId:items[^1].RecordingFileId,HasMore=more};
    }
    public bool IsPendingArtifact(string path) => Path.GetFileName(path).StartsWith("record_chain_segment_",StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.GetFullPath(PendingControlDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    public void ProcessPending() => Reconcile(HostAlive,WorkerAlive);
    internal void Reconcile(Func<int,long,bool> hostAlive, Func<string,bool?> workerAlive)
    {
        StopToken.ThrowIfCancellationRequested();
        var writers=new Dictionary<string,bool?>(StringComparer.OrdinalIgnoreCase);
        bool? Writer(string job)
        { if(!writers.TryGetValue(job,out var alive)){alive=workerAlive(job);writers.Add(job,alive);}return alive; }
        ImportControls(Writer);
        var pending=new List<string>();
        using(var c=Open())
        {
            string after;
            using(var read=c.CreateCommand()){read.CommandText="SELECT value FROM catalog_meta WHERE key='pending_after'";after=read.ExecuteScalar() as string??"";}
            var previousAfter=after;
            string last=after;
            for(var pass=0;pass<2 && pending.Count==0;pass++)
            {
                using var read=c.CreateCommand();read.CommandText="SELECT file_id,declaration FROM control_inputs WHERE state=0 AND file_id>$after ORDER BY file_id LIMIT 64";
                read.Parameters.AddWithValue("$after",after);using var rows=read.ExecuteReader();
                while(rows.Read()){last=rows.GetString(0);pending.Add(rows.GetString(1));}
                after="";
            }
            var cursor=pending.Count==0?"":last;
            if(cursor!=previousAfter)
            {
                using var advance=c.CreateCommand();advance.CommandText="INSERT INTO catalog_meta(key,value) VALUES('pending_after',$cursor) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                advance.Parameters.AddWithValue("$cursor",cursor);advance.ExecuteNonQuery();
            }
        }
        foreach(var json in pending)
        {
            StopToken.ThrowIfCancellationRequested();
            try
            {
                var m=JsonSerializer.Deserialize<RecordingFileManifest>(json,Json)!;
                if(!TryRelease(m,Writer,out var result))
                {
                    if(IsDefinitelyMissing(m.OutputPath) && (!hostAlive(m.HostPid,m.HostStartTicks) || JobHasRun(m.JobPath))
                        && Writer(m.JobPath)==false)
                    {
                        using var c=Open();using var abandon=c.CreateCommand();
                        abandon.CommandText="UPDATE control_inputs SET state=2 WHERE file_id=$id";
                        abandon.Parameters.AddWithValue("$id",m.FileId);abandon.ExecuteNonQuery();
                    }
                    continue;
                }
                using var connection=Open();using var tx=connection.BeginTransaction();using var save=connection.CreateCommand();save.Transaction=tx;
                save.CommandText="INSERT INTO released_files(file_id,job_path,payload) VALUES($id,$job,$payload) ON CONFLICT(file_id) DO NOTHING; UPDATE control_inputs SET state=1 WHERE file_id=$id";
                save.Parameters.AddWithValue("$id",m.FileId);save.Parameters.AddWithValue("$job",m.JobPath);save.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(result,Json));
                save.ExecuteNonQuery();tx.Commit();
            }
            catch(Exception ex){log.Add("RECORDING_FILE_CATALOG","Retry",$"result=PENDING errorType={ex.GetType().Name} reason={ex.Message} recordingPriority=unchanged");}
        }
    }
    private static bool IsDefinitelyMissing(string path)
    {
        try { _=File.GetAttributes(path);return false; }
        catch(FileNotFoundException)
        {
            try { return (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.Directory)!=0; }
            catch { return false; }
        }
        catch { return false; } // An unavailable directory/share is not proof of an uncreated file.
    }
    private static bool TryRelease(RecordingFileManifest m,Func<string,bool?> workerAlive,
        out TvAirFinalizedRecordingFileDto? file)
    {
        file=null;
        long? closedSize=null;
        var released=false;var reason="";var result="Released";
        try
        {
        if(File.Exists(m.ResultPath))
        {
            // The owned Worker emits this artifact only after the individual sink is flushed/disposed.
            // Incomplete/mismatched proof is never replaced with a file-exists heuristic.
            using var doc=JsonDocument.Parse(File.ReadAllText(m.ResultPath));var root=doc.RootElement;
            if(!root.TryGetProperty("recorderPid",out var pid)||pid.GetInt32()<=0
                || !root.TryGetProperty("result",out var body)
                || !body.TryGetProperty("outputPath",out var output)
                || !string.Equals(Path.GetFullPath(output.GetString()!),m.OutputPath,StringComparison.OrdinalIgnoreCase)
                || !body.TryGetProperty("runtimeStatsPath",out var stats)
                || !string.Equals(Path.GetFullPath(stats.GetString()!),m.RuntimeStatsPath,StringComparison.OrdinalIgnoreCase)
                || !body.TryGetProperty("message",out var message)||message.GetString() is not ("continuous_chain_segment_result" or "continuous_chain_primary_segment_result")
                || !body.TryGetProperty("bytesWritten",out var bytes)||!bytes.TryGetInt64(out var bytesWritten)||bytesWritten<0
                || !body.TryGetProperty("endedAt",out var end)||!DateTimeOffset.TryParse(end.GetString(),out _))
                throw new InvalidDataException("Segment close proof does not match declaration.");
            reason=root.TryGetProperty("message",out var wrapperMessage)?wrapperMessage.GetString()??"sink_released":"sink_released";
            try { closedSize=new FileInfo(m.OutputPath).Length; }
            catch { closedSize=null; }
            released=true;
        }
        }
        catch(JsonException) { }
        catch(InvalidDataException) { }
        if(!released && File.Exists(m.OutputPath) && workerAlive(m.JobPath)==false)
        {
            // Exact job writer has exited. Never wait for or stop an unrelated shared Worker.
            using var exclusive=new FileStream(m.OutputPath,FileMode.Open,FileAccess.Read,FileShare.None);
            closedSize=exclusive.Length;
            result="WriterExited";reason="writer_exited_without_segment_close_proof";released=true;
        }
        if(!released)return false;
        file=new(){RecordingFileId=m.FileId,ReservationId=m.ReservationId,
            RecoveryParentReservationId=m.RecoveryParentReservationId,RecoveryChainId=m.RecoveryChainId,
            ReservationSource=m.Source,ProgramTitle=m.Title,ServiceName=m.ServiceName,
            FilePath=m.OutputPath,SizeBytes=closedSize,FileReleased=true,
            Result=result,EndReason=reason,ReleasedAt=DateTimeOffset.Now,PluginBindings=m.Bindings};
        return true;
    }
    private static bool HostAlive(int pid,long ticks)
    {
        try{using var p=Process.GetProcessById(pid);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==ticks;}
        catch(ArgumentException){return false;}catch{return true;}
    }
    private static bool? WorkerAlive(string jobPath)
    {
        try
        {
            using var query=new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='TvAIrEpgRec.exe'");
            query.Options.Timeout=TimeSpan.FromSeconds(2);
            using var rows=query.Get();var unknown=false;
            foreach(ManagementObject row in rows)
            { using(row){var command=row["CommandLine"] as string;if(command is null)unknown=true;
                else if(command.Contains(jobPath,StringComparison.OrdinalIgnoreCase))return true;} }
            return unknown?null:false;
        }
        catch{return null;}
    }
}
internal sealed record RecordingFileManifest(string FileId,string ReservationId,string? RecoveryParentReservationId,
    string RecoveryChainId,string Source,string Title,string ServiceName,string OutputPath,string ResultPath,
    string RuntimeStatsPath,string JobPath,int HostPid,long HostStartTicks,IReadOnlyList<TvAirPluginSelectionBindingDto> Bindings);

internal sealed class RecordingFileCatalogService(RecordingResultStore results,LogRepository log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        results.Files.StopToken=stoppingToken;
        while(!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Run(results.Files.ProcessPending,stoppingToken).ConfigureAwait(false); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){log.Add("RECORDING_FILE_CATALOG","Retry",$"result=PENDING errorType={ex.GetType().Name}");}
            try{await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken).ConfigureAwait(false);}
            catch(OperationCanceledException){break;}
        }
    }
}
