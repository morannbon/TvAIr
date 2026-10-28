using System.Text.Json;
using TvAIr.Core;
namespace TvAIr.Schedule;
// Host-owned assembly of the existing operation, publisher and evidence reader.
public sealed class LabWithdrawalOperationFactory(ReservationScheduler scheduler,LabWithdrawalJournal journal)
{
 public LabWithdrawalOperationCommand? CreateOrRestore(Reservation expected,Guid operation,bool allowNew=true)
 {
  var saved=journal.FindOperation(operation.ToString("N"));
  return saved is null?(allowNew?CreateNew(expected,operation):null):saved.Selected==expected.Id?Restore(saved):null;
 }
 public LabWithdrawalOperationCommand? CreateNew(Reservation expected,Guid operation)
 {
  var owner=scheduler.LabResolveWithdrawalOwner(expected.Id);if(owner is null)return null;
  return Create(expected.SnapshotForChainRequest(),operation.ToString("N"),owner,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
 }
 public LabWithdrawalOperationCommand? Restore(LabWithdrawalRecoveryRow row)
 {
  if(!Guid.TryParseExact(row.Operation,"N",out _))return null;
  try{
   using var doc=JsonDocument.Parse(row.Payload);var p=doc.RootElement;
   if(p.GetProperty("operation").GetString()!=row.Operation||p.GetProperty("selected").GetInt32()!=row.Selected)return null;
   var owner=JsonSerializer.Deserialize<LabWithdrawalOwnerIdentity>(p.GetProperty("ownerBinding").GetRawText());
   var session=p.GetProperty("session").GetString();var token=p.GetProperty("token").GetString();
   if(owner is not{IsValid:true}||p.GetProperty("owner").GetInt32()!=owner.OwnerReservationId||string.IsNullOrWhiteSpace(session)||string.IsNullOrWhiteSpace(token))return null;
   return Create(new Reservation{Id=row.Selected},row.Operation,owner,session,token);
  }catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return null;}
 }
 private LabWithdrawalOperationCommand Create(Reservation expected,string operation,LabWithdrawalOwnerIdentity owner,string session,string token)
 {
  LabCanonicalWithdrawalAccepted? accepted=null;
  var receipt=Path.Combine(owner.ControlPath,"withdrawals",operation+".json");
  bool Validate(JsonElement ack,LabCanonicalWithdrawalAccepted saved,out string instance,out string state){
   instance="";state="";
   try{
    if(!scheduler.LabValidateBoundWithdrawalWorkerReceipt(owner,ack)
     ||ack.GetProperty("operationId").GetString()!=operation||ack.GetProperty("ownerSession").GetString()!=session||ack.GetProperty("prepareToken").GetString()!=token)return false;
    instance=ack.GetProperty("workerInstance").GetString()??"";state=ack.GetProperty("state").GetString()??"";
    if(state is not ("prepared" or "committed" or "aborted" or "already_started" or "busy" or "recovery_required"))return false;
    var ids=ack.GetProperty("reservationIds").EnumerateArray().Select(v=>v.GetInt32()).OrderBy(id=>id).ToArray();
    return (!ack.TryGetProperty("cancellationTargetsExplicit",out var explicitTargets)||!explicitTargets.GetBoolean()
     ||(ack.TryGetProperty("cancelledReservationIds",out var cancelledIds)&&cancelledIds.EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual(new[]{expected.Id})))
     &&Guid.TryParseExact(instance,"N",out _)&&ids.SequenceEqual(saved.Expected.Select(r=>r.Id).OrderBy(id=>id));
   }catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return false;}
  }
  (string Instance,string State)? Read(LabCanonicalWithdrawalAccepted saved){
   try{using var doc=JsonDocument.Parse(File.ReadAllText(receipt));if(!Validate(doc.RootElement,saved,out var instance,out var state))return null;var bound=journal.ReadWorkerInstance(operation);if(bound is null?!journal.BindWorkerInstance(operation,saved.Payload,instance):bound!=instance)return null;return(instance,state);}
   catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){return null;}
  }
  return new LabWithdrawalOperationCommand(expected.Id,journal,operation,
   store=>accepted=store.LabAcceptOrReadCanonicalWithdrawal(expected,operation,owner.OwnerReservationId,session,token,owner),
   (phase,saved)=>{
    var decision=journal.Read(operation)?.State;
    if(phase=="commit"&&decision!="committed"||phase=="abort"&&decision!="aborted")throw new IOException("Unconfirmed withdrawal decision.");
    scheduler.LabPublishWithdrawal(owner,new(operation,session,token,phase,saved.Expected.Select(r=>r.Id).OrderBy(id=>id).ToArray(),receipt){HostAbortedDecision=phase=="abort",CancelledReservationIds=new[]{expected.Id}});
   },
   saved=>{
    var ack=Read(saved);if(ack?.State!="prepared")return null;
    return new WithdrawalProtectionLabPermit(receipt,operation,session,token,ack.Value.Instance,owner.OwnerReservationId,saved.Expected,scheduler.CaptureScheduledChainJoinRuntimeProtection(DateTime.Now)){
     CurrentWorker=()=>journal.ReadWorkerInstance(operation)??"",
     ValidateWorkerReceipt=element=>Validate(element,saved,out var instance,out _)&&instance==journal.ReadWorkerInstance(operation),
     PersistDecision=(con,tx)=>LabWithdrawalJournal.Commit(con,tx,operation,saved.Payload)};
   },
   state=>accepted is not null&&Read(accepted)?.State==state,
   prepareDisposition:()=>accepted is null?null:Read(accepted)?.State);
 }
}
