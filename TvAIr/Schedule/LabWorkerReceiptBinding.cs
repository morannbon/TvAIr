using System.Text.Json;
using TvAIr.Core;
namespace TvAIr.Schedule;
// Strict cancellation evidence; never changes general recording liveness policy.
public static class LabWorkerReceiptBinding
{
 public static bool Matches(JsonElement receipt,ManagedProcessIdentity registered,ManagedProcessIdentity observed)
 {
  if(!registered.IsAvailable||!observed.IsAvailable||registered.ProcessId<=0
   ||!registered.ProcessStartTimeUtc.HasValue||!observed.ProcessStartTimeUtc.HasValue
   ||registered.ProcessId!=observed.ProcessId||registered.ProcessStartTimeUtc!=observed.ProcessStartTimeUtc)return false;
  if(!TvAirManagedProcessRegistry.IdentityMatches(registered,observed))return false;
  try { return receipt.GetProperty("workerProcessId").GetInt32()==registered.ProcessId
   &&receipt.GetProperty("workerProcessStartTimeUtc").GetDateTime().ToUniversalTime()==registered.ProcessStartTimeUtc.Value; }
  catch(Exception ex) when(ex is KeyNotFoundException or InvalidOperationException or FormatException){return false;}
 }
}
