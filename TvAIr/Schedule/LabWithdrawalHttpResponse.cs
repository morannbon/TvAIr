using Microsoft.AspNetCore.Http;
namespace TvAIr.Schedule;

// Isolated response boundary only. Does not own, cancel, enqueue, or retry an operation.
public static class LabWithdrawalHttpResponse
{
 public static async Task<IResult> WaitAsync(Task<string> settlement,
  Func<ReservationCancellationResult?> readOutcome,TimeSpan responseBudget,
  CancellationToken requestAborted)
 {
  requestAborted.ThrowIfCancellationRequested();
  string state;
  try { state=await settlement.WaitAsync(responseBudget,requestAborted); }
  catch(TimeoutException) { return Pending(); }
  requestAborted.ThrowIfCancellationRequested();
  var outcome=readOutcome();
  if(state=="committed" && outcome is { Applied:true } && outcome.Cancelled.Count>0)
  {
   var ids=outcome.Cancelled.Select(r=>r.Id).Distinct().ToArray();
   return Results.Json(new {message="キャンセルしました。",changed=(bool?)true,mutation="cancel",affectedReservationIds=ids,cancelledIds=ids,pending=false},statusCode:200);
  }
  if(state=="aborted" && outcome is { Applied:false })
   return Results.Json(new {message="予約状態が更新されたためキャンセルしていません。最新状態を確認してください。",changed=(bool?)false,cancelledIds=Array.Empty<int>(),pending=false},statusCode:409);
  return Pending();
 }
 internal static IResult Pending()=>Results.Json(new {message="取消処理の結果を確認中です。予約一覧を更新して状態を確認してください。",changed=(bool?)null,pending=true},statusCode:503);
}
