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
    private readonly SemaphoreSlim workAvailable = new(0, 1);
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
            CREATE TABLE IF NOT EXISTS file_stop_intents(job_path TEXT NOT NULL, file_path TEXT NOT NULL, requested_at TEXT NOT NULL,
                PRIMARY KEY(job_path,file_path));
            CREATE TABLE IF NOT EXISTS file_publications(publication_key TEXT PRIMARY KEY,
                file_path TEXT NOT NULL, reservation_id TEXT NOT NULL, payload TEXT NOT NULL,
                delivered INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS released_declarations(file_id TEXT PRIMARY KEY, declaration TEXT NOT NULL);
            INSERT OR IGNORE INTO catalog_meta(key,value)
                SELECT 'publication_baseline_sequence',CAST(COALESCE(MAX(sequence),0) AS TEXT) FROM released_files;
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
            process.Id,process.StartTime.ToUniversalTime().Ticks,bindings.ToArray(),
            reservation.ScheduledStartTime ?? reservation.StartTime,reservation.EndTime);
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
                // Relay declarations share the original control's lifetime and catalog authority.
                var relayDirectory=Path.Combine(store,"relay");
                if(Directory.Exists(relayDirectory))
                foreach(var intent in Directory.EnumerateFiles(relayDirectory,"*.json"))
                {
                    var relayJson=File.ReadAllText(intent);
                    var relay=JsonSerializer.Deserialize<RecordingFileManifest>(relayJson,Json)!;
                    if(!relay.DiskRelayPart || !Path.IsPathFullyQualified(relay.OutputPath))
                        throw new InvalidDataException("Invalid relay declaration.");
                    using var add=c.CreateCommand();
                    add.CommandText="INSERT OR IGNORE INTO control_inputs(store_path,file_id,declaration,state) VALUES($store,$id,$data,CASE WHEN EXISTS(SELECT 1 FROM released_files WHERE file_id=$id) THEN 1 ELSE 0 END)";
                    add.Parameters.AddWithValue("$store",store);add.Parameters.AddWithValue("$id",relay.FileId);add.Parameters.AddWithValue("$data",relayJson);add.ExecuteNonQuery();
                }
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
                        using(var keep=c.CreateCommand())
                        {
                            keep.Transaction=tx;keep.CommandText="INSERT OR IGNORE INTO released_declarations(file_id,declaration) SELECT $id,$data WHERE EXISTS(SELECT 1 FROM released_files WHERE file_id=$id)";
                            keep.Parameters.AddWithValue("$id",m.FileId);keep.Parameters.AddWithValue("$data",json);keep.ExecuteNonQuery();
                        }
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
                save.ExecuteNonQuery();
                var retainedManifest=m;
                if(File.Exists(CloseProofPath(m)))
                {
                    using var proof=JsonDocument.Parse(File.ReadAllText(CloseProofPath(m)));
                    if(proof.RootElement.TryGetProperty("result",out var body) && body.TryGetProperty("message",out var kind)
                        && kind.GetString()=="disk_relay_file_result")
                        retainedManifest=m with {PhysicalResultJson=body.GetRawText()};
                }
                using var declaration=connection.CreateCommand();declaration.Transaction=tx;
                declaration.CommandText="INSERT OR IGNORE INTO released_declarations(file_id,declaration) VALUES($id,$json)";
                declaration.Parameters.AddWithValue("$id",m.FileId);declaration.Parameters.AddWithValue("$json",JsonSerializer.Serialize(retainedManifest,Json));
                declaration.ExecuteNonQuery();tx.Commit();
                log.Add("RECORDING_FILE_CATALOG", "Released",
                    $"result=COMMITTED fileId={m.FileId} reservation={m.ReservationId} service={m.ServiceName} title={m.Title} scheduled={m.ScheduledStartTime:O} end={m.ScheduledEndTime:O} file={Path.GetFileName(m.OutputPath)} size={result!.SizeBytes} endReason={result.EndReason} rule=physical_recording_file_release_contract");
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

    internal IReadOnlyList<string> KnownOutputPaths(string reservationId)
    {
        var declarations=new Dictionary<string,RecordingFileManifest>(StringComparer.Ordinal);
        void Keep(string json)
        {
            var manifest=JsonSerializer.Deserialize<RecordingFileManifest>(json,Json)!;
            if(manifest.ReservationId==reservationId)declarations[manifest.FileId]=manifest;
        }
        using(var c=Open())using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="SELECT declaration FROM control_inputs WHERE json_extract(declaration,'$.ReservationId')=$id UNION ALL SELECT declaration FROM released_declarations WHERE json_extract(declaration,'$.ReservationId')=$id";
            cmd.Parameters.AddWithValue("$id",reservationId);
            using var rows=cmd.ExecuteReader();while(rows.Read())Keep(rows.GetString(0));
        }
        // An initial redirect can precede the background catalog importer. Read those same
        // durable declarations directly; never search unrelated disks for a similar name.
        if(Directory.Exists(PendingControlDirectory))
        foreach(var store in Directory.EnumerateDirectories(PendingControlDirectory))
        {
            var relay=Path.Combine(store,"relay");if(!Directory.Exists(relay))continue;
            foreach(var path in Directory.EnumerateFiles(relay,"*.json"))Keep(File.ReadAllText(path));
        }
        var current=declarations.Values.GroupBy(x=>x.JobPath,StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group=>File.GetLastWriteTimeUtc(group.Key)).FirstOrDefault();
        if(current is null)return Array.Empty<string>();
        var redirects=current.Where(x=>x.DiskRelayPartIndex==1).Select(x=>x.DiskRelaySourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return current.Where(x=>!redirects.Contains(x.OutputPath)).OrderBy(x=>x.DiskRelayPartIndex)
            .Select(x=>x.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal RecordingFileManifest? ReleasedManifest(string fileId)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT declaration FROM released_declarations WHERE file_id=$id";
        cmd.Parameters.AddWithValue("$id",fileId);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<RecordingFileManifest>(json,Json) : null;
    }

    internal bool IsPublicationBaseline(long sequence)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT CAST(value AS INTEGER) FROM catalog_meta WHERE key='publication_baseline_sequence'";
        return sequence<=Convert.ToInt64(cmd.ExecuteScalar());
    }
    internal void MarkManualStop(string jobPath,string outputPath)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT OR IGNORE INTO file_stop_intents(job_path,file_path,requested_at) VALUES($job,$path,$at)";
        cmd.Parameters.AddWithValue("$job",Path.GetFullPath(jobPath).ToUpperInvariant());
        cmd.Parameters.AddWithValue("$path",Path.GetFullPath(outputPath).ToUpperInvariant());
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.Now.ToString("O"));cmd.ExecuteNonQuery();
    }
    internal void StageFinalizedEvent(TvAirEventDto item)
    {
        var result=item.RecordingResult ?? throw new ArgumentException("Finalized result required.",nameof(item));
        var path=string.IsNullOrWhiteSpace(result.FilePath)?"":Path.GetFullPath(result.FilePath);
        var key=PublicationKey(result.ReservationId,path);
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT OR IGNORE INTO file_publications(publication_key,file_path,reservation_id,payload) VALUES($key,$path,$reservation,$payload)";
        cmd.Parameters.AddWithValue("$key",key);cmd.Parameters.AddWithValue("$path",path);
        cmd.Parameters.AddWithValue("$reservation",result.ReservationId);
        cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(item,Json));cmd.ExecuteNonQuery();
        try { workAvailable.Release(); } catch(SemaphoreFullException) { }
    }
    internal static string PublicationKey(string reservationId,string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reservationId+"\n"+path.ToUpperInvariant())));
    internal IReadOnlyList<(string Key,string Path,TvAirEventDto Event)> PendingPublications()
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT publication_key,file_path,payload FROM file_publications WHERE delivered=0 ORDER BY rowid LIMIT 64";
        using var read=cmd.ExecuteReader();var rows=new List<(string,string,TvAirEventDto)>();
        while(read.Read())rows.Add((read.GetString(0),read.GetString(1),JsonSerializer.Deserialize<TvAirEventDto>(read.GetString(2),Json)!));
        return rows;
    }
    internal bool HasPublication(string reservationId,string path)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT EXISTS(SELECT 1 FROM file_publications WHERE publication_key=$key)";
        cmd.Parameters.AddWithValue("$key",PublicationKey(reservationId,Path.GetFullPath(path)));
        return Convert.ToInt64(cmd.ExecuteScalar())!=0;
    }
    internal void CompletePublication(string key)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE file_publications SET delivered=1 WHERE publication_key=$key";
        cmd.Parameters.AddWithValue("$key",key);cmd.ExecuteNonQuery();
    }
    internal TvAirFinalizedRecordingFileDto? FindReleased(string path)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT sequence,payload FROM released_files WHERE json_extract(payload,'$.FilePath')=$path COLLATE NOCASE ORDER BY sequence DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$path",Path.GetFullPath(path));using var read=cmd.ExecuteReader();
        return read.Read()?JsonSerializer.Deserialize<TvAirFinalizedRecordingFileDto>(read.GetString(1),Json)! with {Sequence=read.GetInt64(0)}:null;
    }
    internal IReadOnlyList<(TvAirFinalizedRecordingFileDto File,RecordingFileManifest? Manifest)> UnpublishedReleasedFiles()
    {
        using var c=Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT r.sequence,r.payload,d.declaration FROM released_files r LEFT JOIN released_declarations d ON d.file_id=r.file_id WHERE NOT EXISTS(SELECT 1 FROM file_publications p WHERE p.file_path=json_extract(r.payload,'$.FilePath') COLLATE NOCASE AND p.reservation_id=json_extract(r.payload,'$.ReservationId')) ORDER BY r.sequence LIMIT 64";
        using var read=cmd.ExecuteReader();var rows=new List<(TvAirFinalizedRecordingFileDto,RecordingFileManifest?)>();
        while(read.Read())rows.Add((JsonSerializer.Deserialize<TvAirFinalizedRecordingFileDto>(read.GetString(1),Json)! with {Sequence=read.GetInt64(0)},read.IsDBNull(2)?null:JsonSerializer.Deserialize<RecordingFileManifest>(read.GetString(2),Json)!));
        return rows;
    }
    internal Task WaitForWorkAsync(CancellationToken ct) => workAvailable.WaitAsync(TimeSpan.FromSeconds(5),ct);

    private static string CloseProofPath(RecordingFileManifest m)
        => File.Exists(m.ResultPath+".physical.json") ? m.ResultPath+".physical.json" : m.ResultPath;

    private bool TryRelease(RecordingFileManifest m,Func<string,bool?> workerAlive,
        out TvAirFinalizedRecordingFileDto? file)
    {
        file=null;
        if(!File.Exists(m.OutputPath))return false;
        DateTimeOffset? closedAt=null;
        long? closedSize=null;
        var released=false;var reason="";var result="Released";
        try
        {
        if(File.Exists(CloseProofPath(m)))
        {
            // The owned Worker emits this artifact only after the individual sink is flushed/disposed.
            // Incomplete/mismatched proof is never replaced with a file-exists heuristic.
            using var doc=JsonDocument.Parse(File.ReadAllText(CloseProofPath(m)));var root=doc.RootElement;
            if(!root.TryGetProperty("recorderPid",out var pid)||pid.GetInt32()<=0
                || !root.TryGetProperty("result",out var body)
                || !body.TryGetProperty("outputPath",out var output)
                || !string.Equals(Path.GetFullPath(output.GetString()!),m.OutputPath,StringComparison.OrdinalIgnoreCase)
                || !body.TryGetProperty("runtimeStatsPath",out var stats)
                || !string.Equals(Path.GetFullPath(stats.GetString()!),m.RuntimeStatsPath,StringComparison.OrdinalIgnoreCase)
                || !body.TryGetProperty("message",out var message)||message.GetString() is not ("continuous_chain_segment_result" or "continuous_chain_primary_segment_result" or "disk_relay_file_result")
                || !body.TryGetProperty("bytesWritten",out var bytes)||!bytes.TryGetInt64(out var bytesWritten)||bytesWritten<0
                || !body.TryGetProperty("endedAt",out var end)||!DateTimeOffset.TryParse(end.GetString(),out _))
                throw new InvalidDataException("Segment close proof does not match declaration.");
            reason=root.TryGetProperty("message",out var wrapperMessage)?wrapperMessage.GetString()??"sink_released":"sink_released";
            try { closedSize=new FileInfo(m.OutputPath).Length; }
            catch { closedSize=null; }
            DateTimeOffset.TryParse(end.GetString(),out var sinkEndedAt);closedAt=sinkEndedAt;
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
        using(var c=Open())
        using(var intent=c.CreateCommand())
        {
            intent.CommandText="SELECT requested_at FROM file_stop_intents WHERE job_path=$job AND (file_path=$path OR file_path=$source)";
            intent.Parameters.AddWithValue("$job",Path.GetFullPath(m.JobPath).ToUpperInvariant());
            intent.Parameters.AddWithValue("$path",Path.GetFullPath(m.OutputPath).ToUpperInvariant());
            intent.Parameters.AddWithValue("$source",Path.GetFullPath(m.DiskRelaySourcePath ?? m.OutputPath).ToUpperInvariant());
            if(intent.ExecuteScalar() is string requested && DateTimeOffset.TryParse(requested,out var requestedAt)
                && (!closedAt.HasValue || closedAt.Value>=requestedAt))reason="manual_stop";
        }
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
    string RuntimeStatsPath,string JobPath,int HostPid,long HostStartTicks,IReadOnlyList<TvAirPluginSelectionBindingDto> Bindings,
    DateTime? ScheduledStartTime = null, DateTime? ScheduledEndTime = null,
    bool DiskRelayPart = false, string? PhysicalResultJson = null, string? DiskRelaySourcePath = null, int DiskRelayPartIndex = 0);

