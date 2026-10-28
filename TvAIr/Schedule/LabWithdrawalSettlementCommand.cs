using Microsoft.Data.Sqlite;
using TvAIr.Core;
namespace TvAIr.Schedule;
// Isolated post-cancellation settlement. Must be submitted only after cancellation outcome is known.
public sealed class LabWithdrawalSettlementCommand(LabWithdrawalJournal journal,string operation,Action<string> publish,Func<string,bool> workerApplied,LabWithdrawalOwnerIdentity? owner=null,bool allowSettledReplay=false) : ReservationTopologyCommand
{
 private readonly object orderingKey=new();
 private readonly TaskCompletionSource<string> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public Task<string> Completion=>completion.Task;
 public int Attempts {get;private set;}
 public string LastWait {get;private set;}="not_started";
 internal override bool RequiresProjectionMetadata=>false;
 internal override bool IsRejected=>false;
 internal override bool RequiresAllocationAfterCommit=>false;
 internal override object? NonBlockingDeferralKey=>orderingKey;
 internal override bool RetainIfUnexecuted=>!Completion.IsCompleted;
 internal override string? DeferredReason=>!Completion.IsCompleted?"withdrawal_settlement_pending":null;
 internal override void Apply(ReservationStore store,bool enabled,ReservationProjectionMetadataStore? metadataStore=null)
 {
  if(Completion.IsCompleted)return;
  Attempts++;
  try
  {
   var decision=journal.TryResolveForRecovery(operation);
   if(decision is not ("committed" or "aborted")){LastWait="durable_decision_unknown";return;}
   // Only a restored terminal operation may reuse durable settlement. Fresh or
   // previously pending operations must still publish their terminal decision.
   if(allowSettledReplay&&journal.IsSettled(operation,decision)){
    LastWait="durable_settled_replay";completion.TrySetResult(decision);return;
   }
   publish(decision);
   if(owner is not null && LabWorkerExitProbe.Observe(owner.ProcessIdentity)==LabWorkerPresence.Exited){
    if(!journal.ConfirmOwnerExited(operation,decision,owner)){LastWait="owner_exit_confirmation_pending";return;}
    LastWait="owner_exit_confirmed";completion.TrySetResult(decision);return;
   }
   if(!workerApplied(decision)){LastWait="worker_applied_response_pending";return;}
   if(!journal.AcknowledgeWorker(operation,decision)){LastWait="durable_confirmation_pending";return;}
   LastWait="completed";completion.TrySetResult(decision);
  }
  catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException)
  {LastWait="retry_"+ex.GetType().Name;}
 }
}
