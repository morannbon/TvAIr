using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using TvAIr.Core;
namespace TvAIr.Schedule;
// Existing DELETE entry's asynchronous registered-future branch; null preserves the legacy path.
public static class LabWithdrawalHttpEndpoint
{
 public static async Task<IResult?> TryHandleAsync(HttpContext context,int id,Database db,IniSettingsService ini,
  ReservationStore store,ReservationAllocationRouteService route,ReservationScheduler scheduler)
 {
  var raw=context.Request.Headers["X-TvAIr-Reservation-Operation"].ToString();var hasKey=!string.IsNullOrWhiteSpace(raw);
  var intent=context.Request.Headers["X-TvAIr-Reservation-Action"].ToString();
  if(hasKey&&!Guid.TryParseExact(raw,"N",out _))return Results.BadRequest(new{message="操作情報を確認できません。画面を更新してから再度操作してください。",changed=false});
  if(intent.Length>0&&intent!="cancel")return Results.BadRequest(new{message="予約操作を確認できません。",changed=false});
  if(!hasKey&&intent.Length==0&&!ini.AutomaticChainRecordingEnabled)return null;
  context.RequestAborted.ThrowIfCancellationRequested();
  var operation=hasKey?Guid.ParseExact(raw,"N"):Guid.NewGuid();var journal=new LabWithdrawalJournal(db);
  try{
   var saved=hasKey?journal.FindOperation(operation.ToString("N")):null;
   var row=store.GetById(id);var target=id;
   if(saved is not null){
    if(saved.Selected!=id&&!(row is not null&&ReservationIntentContract.IsPreRecordEpg(row)&&row.SourceRuleId==saved.Selected))return Results.Conflict(new{message="別の番組の操作情報が含まれています。画面を更新してください。",changed=false});
    target=saved.Selected;row=store.GetById(target);
   }else if(row is not null&&row.SourceRuleId.HasValue&&ReservationIntentContract.IsPreRecordEpg(row)){
    var parent=store.GetById(row.SourceRuleId.Value);
    if(parent is not null&&parent.Status is not (ReservationStatus.Completed or ReservationStatus.Failed or ReservationStatus.Cancelled)){row=parent;target=parent.Id;}
   }
   var allowNew=ini.AutomaticChainRecordingEnabled&&row?.Status==ReservationStatus.Scheduled;
   if(hasKey||allowNew){
    var factory=new LabWithdrawalOperationFactory(scheduler,journal);var expected=row??new Reservation{Id=target};
    var admission=await route.AdmitLabWithdrawalAsync(operation,target,()=>factory.CreateOrRestore(expected,operation,allowNew));
    if(admission.Reason=="operation_target_mismatch")return Results.Conflict(new{message="別の番組の操作が進行中です。画面を更新してください。",changed=false});
    if(admission.Operation is not null){
     context.Response.Headers["X-TvAIr-Reservation-Operation"]=operation.ToString("N");
     return await LabWithdrawalHttpResponse.WaitAsync(admission.Operation.Completion,()=>admission.Operation.Result,TimeSpan.FromSeconds(5),context.RequestAborted);
    }
    if(saved is not null)return LabWithdrawalHttpResponse.Pending();
   }
   // A stale cancellation intent must never become a terminal history deletion.
   if((hasKey||intent=="cancel")&&row is not null&&row.Status is ReservationStatus.Completed or ReservationStatus.Failed or ReservationStatus.Cancelled)
    return Results.Conflict(new{message="予約の状態が変わっています。最新の状態を確認してください。",changed=false});
   return null;
  }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or SqliteException){return LabWithdrawalHttpResponse.Pending();}
 }
}
