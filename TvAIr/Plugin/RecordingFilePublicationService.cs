using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using TvAIr.Core;
using TvAIr.Schedule;
using TvAIrPlugin;

namespace TvAIr.Plugin;

// One durable publication path for normal stops, closed chain segments and recovered files.
// The file catalog commits first; subscriber callbacks acquire their own admission before
// either this handoff lease or the reservation power guard can be released.
internal sealed class RecordingFileCatalogService(
    RecordingResultStore results, ReservationStore reservations, PluginTypedEventHub events,
    SystemSleepInhibitionService power, IHostApplicationLifetime lifetime, LogRepository log) : BackgroundService
{
    private readonly ConcurrentDictionary<string,IDisposable> handoffs = new();
    private readonly ConcurrentDictionary<string,byte> queued = new();
    private static readonly DateTimeOffset HostStarted = new(Process.GetCurrentProcess().StartTime);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        results.Files.StopToken=stoppingToken;
        events.DispatchSettled+=Delivered;
        try
        {
            // Recovered files must not be confirmed before Plugin startup can observe them.
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration=lifetime.ApplicationStarted.Register(()=>started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            while(!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Run(results.Files.ProcessPending,stoppingToken).ConfigureAwait(false);
                    StageReleasedFiles();
                    PublishReady();
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
                catch(Exception ex)
                {
                    log.Add("RECORDING_FILE_PUBLICATION","Retry",$"result=PENDING errorType={ex.GetType().Name} reason={ex.Message}");
                }
                await results.Files.WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
        finally
        {
            events.DispatchSettled-=Delivered;
            foreach(var key in handoffs.Keys)ReleaseHandoff(key);
        }
    }

    internal void StageReleasedFiles()
    {
        foreach(var (file,manifest) in results.Files.UnpublishedReleasedFiles())
        {
            var id=ParseId(file.ReservationId);
            var reservation=id>0?reservations.GetById(id):null;
            var legacy=results.Get(file.ReservationId);
            var matchingLegacy=legacy?.ResultFinalized==true
                && string.Equals(legacy.FilePath,file.FilePath,StringComparison.OrdinalIgnoreCase);
            // The one-time schema baseline excludes old completed recordings. Wall clock or
            // Host session age must never discard a file committed before a crash.
            // Freshly confirmed files and a reservation just finalized by this startup remain eligible.
            var newlyTerminal=reservation?.RecordingFinishedAt is DateTime finished
                && new DateTimeOffset(finished)>=HostStarted;
            if(results.Files.IsPublicationBaseline(file.Sequence) && !newlyTerminal)
            {
                var historical=BuildEvent(file,manifest,reservation,matchingLegacy?legacy:null);
                results.Files.StageFinalizedEvent(historical);
                results.Files.CompletePublication(RecordingFileCatalog.PublicationKey(file.ReservationId,Path.GetFullPath(file.FilePath)));
                continue;
            }
            if(manifest?.PhysicalResultJson is null && reservation is not null && reservation.Status is ReservationStatus.Recording or ReservationStatus.Starting or ReservationStatus.Stopping)
                continue;
            // A live Host still owns its normal completion projection. Missing results from
            // an exited Host are reconstructed from physical release evidence, never success guesses.
            if(!matchingLegacy && manifest is not null && manifest.PhysicalResultJson is null && IsHostAlive(manifest.HostPid,manifest.HostStartTicks))
                continue;
            results.Files.StageFinalizedEvent(BuildEvent(file,manifest,reservation,matchingLegacy?legacy:null));
        }
    }

    private static TvAirEventDto BuildEvent(TvAirFinalizedRecordingFileDto file,RecordingFileManifest? manifest,
        Reservation? reservation,TvAirRecordingResultDto? legacy)
    {
        var result=legacy ?? new TvAirRecordingResultDto
        {
            ReservationId=file.ReservationId,RecordingId=file.RecordingFileId,
            ServiceName=file.ServiceName,EventTitle=file.ProgramTitle,
            NetworkId=reservation?.NetworkId??0,TransportStreamId=reservation?.TransportStreamId??0,
            ServiceId=reservation?.ServiceId??0,EventId=reservation?.EventId??0,
            ScheduledStartTime=(manifest?.ScheduledStartTime ?? reservation?.ScheduledStartTime ?? reservation?.StartTime) is DateTime scheduled?new DateTimeOffset(scheduled):default,
            ActualStartTime=reservation?.RecordingStartedAt is DateTime start?new DateTimeOffset(start):null,
            Result="Failed",EndReason=file.EndReason,FilePath=file.FilePath,FileCreated=true,
            ResourceReleaseState="catalog_file_release_verified",QualityCompleteness="unavailable",
            QualitySource="Unavailable",ResultFinalized=true,Succeeded=false
        };
        if(manifest?.PhysicalResultJson is string physicalJson)
        {
            using var proof=JsonDocument.Parse(physicalJson);var body=proof.RootElement;
            var success=body.GetProperty("success").GetBoolean();
            var manualStop=file.EndReason=="manual_stop";
            result=new TvAirRecordingResultDto
            {
                ReservationId=file.ReservationId,RecordingId=file.RecordingFileId,
                ServiceName=file.ServiceName,EventTitle=file.ProgramTitle,
                NetworkId=reservation?.NetworkId??0,TransportStreamId=reservation?.TransportStreamId??0,
                ServiceId=reservation?.ServiceId??0,EventId=reservation?.EventId??0,
                ScheduledStartTime=manifest.ScheduledStartTime is DateTime scheduledStart ? new DateTimeOffset(scheduledStart) : default,
                ActualStartTime=body.GetProperty("startedAt").GetDateTimeOffset(),
                ActualEndTime=body.GetProperty("endedAt").GetDateTimeOffset(),
                Result=manualStop?"Stopped":success?"Completed":"Failed",EndReason=file.EndReason,
                FilePath=file.FilePath,FileCreated=file.SizeBytes>0,
                Drop=body.GetProperty("outputContinuityDrops").GetInt64(),
                Error=body.GetProperty("outputContinuityErrors").GetInt64(),
                Scramble=body.GetProperty("outputScrambledPackets").GetInt64(),
                QualityDataAvailable=true,QualityCompleteness="complete",QualitySource="WorkerPhysicalFile",
                ResourceReleaseState="catalog_file_release_verified",ResultFinalized=true,Succeeded=success&&!manualStop
            };
        }
        return new TvAirEventDto
        {
            EventType=TvAirEventType.RecordingResultFinalized,
            EntityId="recording-file:"+file.RecordingFileId,ReservationId=file.ReservationId,
            EntityVersion=reservation?.DataVersion??0,DataRevision=reservation?.DataVersion??0,
            ServiceName=file.ServiceName,ProgramTitle=file.ProgramTitle,
            Reservation=reservation is null?null:PluginTypedEventHub.ToSnapshot(reservation),
            PluginBindings=file.PluginBindings,RecordingResult=result
        };
    }

    internal void PublishReady()
    {
        if(lifetime.ApplicationStopping.IsCancellationRequested)return;
        var pending=results.Files.PendingPublications();
        // Acquire every pending handoff before dispatch can release the predecessor guard.
        foreach(var item in pending)
            handoffs.GetOrAdd(item.Key,key=>
            {
                if(!power.TryAcquire("RecordingFileCatalog",key,0,out var lease,out var error))
                    throw new IOException("Recording file power handoff unavailable: "+error);
                return lease!;
            });
        foreach(var item in pending)
        {
            if(queued.ContainsKey(item.Key))continue;
            var result=item.Event.RecordingResult!;
            var file=string.IsNullOrWhiteSpace(item.Path)?null:results.Files.FindReleased(item.Path);
            if(result.FileCreated==true && file is null)continue;
            var physicalManifest=file is null?null:results.Files.ReleasedManifest(file.RecordingFileId);
            if(file is not null && physicalManifest is {PhysicalResultJson:not null} manifest)
                result=BuildEvent(file,manifest,reservations.GetById(ParseId(file.ReservationId)),null).RecordingResult!;
            // Keep an existing reservation-level result immutable when another physical file
            // from the same reservation is announced. The file catalog is the multi-file authority.
            var existing=results.Get(result.ReservationId);
            if(physicalManifest?.PhysicalResultJson is null && (existing?.ResultFinalized!=true || string.Equals(existing.FilePath,result.FilePath,StringComparison.OrdinalIgnoreCase)))
                results.Upsert(result);
            if(!queued.TryAdd(item.Key,0))continue;
            try
            {
                var e=item.Event;
                events.Publish(new TvAirEventDto
                {
                    EventType=TvAirEventType.RecordingResultFinalized,
                    EventInstanceId=item.Key,EntityId=e.EntityId,EntityVersion=e.EntityVersion,
                    DataRevision=e.DataRevision,ReservationId=e.ReservationId,
                    ServiceName=e.ServiceName,ProgramTitle=e.ProgramTitle,Reservation=e.Reservation,
                    PluginBindings=e.PluginBindings,RecordingResult=result,
                    Details=new Dictionary<string,string>
                    {
                        ["recordingPublicationKey"]=item.Key,
                        ["recordingFileId"]=file?.RecordingFileId??"",
                        ["recordingFileSequence"]=(file?.Sequence??0).ToString(),
                        ["reservationSource"]=file?.ReservationSource??"",
                        ["endReason"]=file?.EndReason??result.EndReason
                    }
                });
                log.Add("RECORDING_FILE_PUBLICATION","Queued",
                    $"result=QUEUED service={e.ServiceName} title={e.ProgramTitle} scheduled={result.ScheduledStartTime:O} file={Path.GetFileName(item.Path)} sequence={file?.Sequence} fileId={file?.RecordingFileId} reservationSource={file?.ReservationSource} endReason={file?.EndReason} handoff=held_until_subscriber_return");
            }
            catch {queued.TryRemove(item.Key,out _);throw;}
        }
    }
    private void Delivered(TvAirEventDto item,int failed)
    {
        if(item.EventType!=TvAirEventType.RecordingResultFinalized
            || !item.Details.TryGetValue("recordingPublicationKey",out var key))return;
        if(failed>0)
        {
            queued.TryRemove(key,out _);
            log.Add("RECORDING_FILE_PUBLICATION","Retry",$"result=PENDING publication={key} failedSubscribers={failed} handoff=retained");
            return;
        }
        try
        {
            results.Files.CompletePublication(key);
            ReleaseHandoff(key);
            log.Add("RECORDING_FILE_PUBLICATION","Delivered",$"result=DELIVERED publication={key} reservation={item.ReservationId} handoff=released_after_subscriber_return");
        }
        catch(Exception ex){log.Add("RECORDING_FILE_PUBLICATION","Retry",$"result=PENDING publication={key} errorType={ex.GetType().Name}");}
        finally{queued.TryRemove(key,out _);}
    }
    private void ReleaseHandoff(string key){if(handoffs.TryRemove(key,out var lease))lease.Dispose();}
    private static int ParseId(string id)=>int.TryParse(id.TrimStart('R','r'),out var value)?value:0;
    private static bool IsHostAlive(int pid,long ticks)
    {
        try {using var process=Process.GetProcessById(pid);return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==ticks;}
        catch(ArgumentException){return false;}catch{return true;}
    }
}
