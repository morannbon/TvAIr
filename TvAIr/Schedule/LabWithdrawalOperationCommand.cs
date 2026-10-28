using TvAIr.Core;
using Microsoft.Data.Sqlite;
namespace TvAIr.Schedule;
// One operation in the existing canonical queue; no task, queue or polling owner of its own.
public sealed class LabWithdrawalOperationCommand(
 int selected,LabWithdrawalJournal journal,string operation,
 Func<ReservationStore,LabCanonicalWithdrawalAccepted?> accept,
 Action<string,LabCanonicalWithdrawalAccepted> publish,
 Func<LabCanonicalWithdrawalAccepted,WithdrawalProtectionLabPermit?> prepared,
 Func<string,bool> workerApplied,Func<string?>? prepareDisposition=null,TimeProvider? clock=null,TimeSpan? prepareBudget=null) : ReservationTopologyCommand
{
 private readonly object orderingKey=new();
 private readonly TaskCompletionSource<string> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
 private LabCanonicalWithdrawalAccepted? accepted;
 private ReservationCancellationCommand? cancellation;
 private LabWithdrawalSettlementCommand? settlement;
 private ReservationScheduler? scheduler;
 private bool automatic;
 private int abortRequested;
 private readonly TimeProvider prepareClock=clock??TimeProvider.System;
 private long prepareStarted;
 private bool preparePublished;
 private ReservationCancellationResult? rejectedOutcome;
 private ReservationCancellationResult? recoveredOutcome;
 private bool recoveredRuntimeReconciled;
 private bool runtimeReconcilePending;
 public string LastWait{get;private set;}="none";
 public string? FailureType{get;private set;}
 private bool PrepareExpired=>prepareClock.GetElapsedTime(prepareStarted,prepareClock.GetTimestamp())>=(prepareBudget??TimeSpan.FromSeconds(30));
 public Task<string> Completion=>completion.Task;
 public int RequestedId=>selected;
 public string OperationId=>operation;
 public ReservationCancellationResult? Result=>cancellation?.Result??recoveredOutcome??rejectedOutcome;
 public string Phase{get;private set;}="accept";
 public void RequestAbort()=>Interlocked.Exchange(ref abortRequested,1);
 internal void PrepareMode(bool value)=>automatic=value;
 internal override bool RequiresProjectionMetadata=>false;
 internal override bool RequiresAllocationAfterCommit=>Result?.Applied==true;
 internal override bool IsRejected=>completion.Task.IsCompletedSuccessfully&&completion.Task.Result!="committed";
 internal override object? NonBlockingDeferralKey=>orderingKey;
 internal override bool RetainIfUnexecuted=>!completion.Task.IsCompleted;
 internal override string? DeferredReason=>!completion.Task.IsCompleted?"withdrawal_operation_pending":null;
 internal override bool RequiresRuntimeReconciliation=>true;
 internal override void PrepareSchedulerContext(ReservationScheduler value)=>scheduler=value;
 internal override void ReconcileRuntime(ReservationScheduler value){
  runtimeReconcilePending=cancellation is not null;
  if(cancellation is not null)runtimeReconcilePending=!cancellation.TryReconcileRuntime(value,true);
  if(accepted is{Reused:true,State:"committed"}&&!recoveredRuntimeReconciled){
   runtimeReconcilePending=true;
   recoveredRuntimeReconciled=value.TryReconcileActiveContinuousChainTopology("WithdrawalRecovery",true);
   runtimeReconcilePending=!recoveredRuntimeReconciled;
  }
 }
 private void BeginSettlement(){settlement=new LabWithdrawalSettlementCommand(journal,operation,decision=>publish(decision=="committed"?"commit":"abort",accepted!),workerApplied,accepted!.OwnerBinding,allowSettledReplay:accepted.Reused&&(accepted.State is "committed" or "aborted"));Phase="settle";}
 internal override void Apply(ReservationStore store,bool enabled,ReservationProjectionMetadataStore? metadataStore=null)
 {
  if(completion.Task.IsCompleted)return;
  if(Phase=="accept")
  {
   try{accepted=accept(store);}
   catch(Exception ex)when(ex is SqliteException or IOException or UnauthorizedAccessException){FailureType=ex.GetType().Name;LastWait="accept_retry";return;}
   if(accepted is null){Phase="not_accepted";completion.TrySetResult("not_accepted");return;}
   // Recovery/replay is not re-executed as a fresh cancellation.
   if(accepted.Reused){
    if(accepted.State is "pending" or "aborted"){BeginSettlement();LastWait="recovered_abort";return;}
    if(accepted.State=="committed"){recoveredOutcome=journal.ReadOutcome(operation);BeginSettlement();LastWait=recoveredOutcome is null?"durable_result_missing":"recovered_commit";return;}
    Phase="recovery_required";completion.TrySetResult("recovery_required");return;
   }
   prepareStarted=prepareClock.GetTimestamp();Phase="prepare";
  }
  if(Phase=="prepare")
  {
   void Abort(string reason){rejectedOutcome=new(false,reason,selected,Array.Empty<Reservation>(),false,0);settlement=new LabWithdrawalSettlementCommand(journal,operation,decision=>publish(decision=="committed"?"commit":"abort",accepted!),workerApplied,accepted!.OwnerBinding);Phase="settle";}
   if(accepted!.OwnerBinding is not null&&LabWorkerExitProbe.Observe(accepted.OwnerBinding.ProcessIdentity)==LabWorkerPresence.Exited){Abort("withdrawal_owner_exited");return;}
   if(PrepareExpired||!automatic||Volatile.Read(ref abortRequested)!=0){Abort("withdrawal_prepare_aborted");return;}
   if(!preparePublished){try{publish("prepare",accepted!);preparePublished=true;LastWait="prepare_response";}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){LastWait="prepare_publish_retry";}return;}
   WithdrawalProtectionLabPermit? permit;
   try{
    var disposition=prepareDisposition?.Invoke();
    if(disposition is "already_started" or "busy"){Abort("withdrawal_prepare_"+disposition);return;}
    permit=prepared(accepted!);
   }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or SqliteException){LastWait="prepare_response_unavailable";return;}
   if(permit is null){LastWait="prepare_response";return;}

   permit.RequireDurableOutcome(operation);
   cancellation=new ReservationCancellationCommand(selected,accepted!.Expected.Where(r=>r.Id==selected).ToArray())
    {LabPermit=permit,LabPreparationReady=()=>true};
   cancellation.PrepareSchedulerContext(scheduler??throw new InvalidOperationException("Missing canonical scheduler."));
   cancellation.PrepareCancellationPolicy(automatic);
   if(!automatic||Volatile.Read(ref abortRequested)!=0)cancellation.RequestLabAbort();
   try{cancellation.Apply(store,enabled,metadataStore);}
   catch(Exception ex)when(cancellation.Result is not null||ex is SqliteException or IOException or UnauthorizedAccessException){FailureType=ex.GetType().Name;LastWait="cancel_outcome_recovery";}
   BeginSettlement();return; // Runtime reconciliation executes in the existing route finally before terminal publication.
  }
  if(Phase=="settle")
  {
   if(runtimeReconcilePending){LastWait="runtime_reconcile_retry";return;}
   settlement!.Apply(store,enabled,metadataStore);
   if(settlement.Completion.IsCompletedSuccessfully){
    if(settlement.Completion.Result=="aborted"&&Result is null)rejectedOutcome=new(false,"withdrawal_aborted_after_failure",selected,Array.Empty<Reservation>(),false,0);
    Phase="completed";completion.TrySetResult(settlement.Completion.Result);
   }
  }
 }
}
