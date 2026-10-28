using System.Text.Json;
using Microsoft.Data.Sqlite;
using TvAIr.Core;
namespace TvAIr.Schedule;
// Isolated admission experiment. Not a released capability or SDK contract.
public sealed class WithdrawalProtectionLabPermit
{
 private readonly string path, operation, session, token, worker;
 internal Func<string>? CurrentWorker { get; init; }
 internal Func<JsonElement,bool>? ValidateWorkerReceipt { get; init; }
 internal Action<SqliteConnection,SqliteTransaction>? PersistDecision { get; init; }
 private bool saveOutcome;
 internal string? DurableOperation=>saveOutcome?operation:null;
 internal void RequireDurableOutcome(string id){if(id!=operation)throw new InvalidOperationException("Operation identity mismatch.");saveOutcome=true;}
 private readonly int owner;
 private readonly Reservation[] expected;
 private readonly ScheduledChainRuntimeProtectionEntry[] evidence;
 private readonly HashSet<int> observedProtected;
 private readonly bool observationReadSucceeded;
 internal WithdrawalProtectionLabPermit(string path,string operation,string session,string token,string worker,int owner,
     IEnumerable<Reservation> expected,ScheduledChainRuntimeProtectionSnapshot observed)
 {
  this.path=path;this.operation=operation;this.session=session;this.token=token;this.worker=worker;this.owner=owner;
  this.expected=expected.Select(x=>x.SnapshotForChainRequest()).ToArray();
  evidence=observed.Entries.ToArray();
  observedProtected=observed.ProtectedReservationIds.ToHashSet();
  observationReadSucceeded=observed.ReadSucceeded;
 }
 internal IReadOnlySet<int> ExpectedAffectedIds => expected.Select(r=>r.Id).ToHashSet();
 internal bool TryDerive(ReservationStore store,ScheduledChainRuntimeProtectionSnapshot current,out HashSet<int> remaining)
 {
  remaining=current.ProtectedReservationIds.ToHashSet();
  try
  {
   if(CurrentWorker is null || ValidateWorkerReceipt is null || PersistDecision is null)return false;
   if(CurrentWorker()!=worker)return false;
   if(!observationReadSucceeded||!current.ReadSucceeded||expected.Length==0||expected.Any(x=>x.Id==owner))return false;
   using var doc=JsonDocument.Parse(File.ReadAllText(path));var ack=doc.RootElement;
   if(!ValidateWorkerReceipt(ack))return false;
   if(ack.GetProperty("operationId").GetString()!=operation||ack.GetProperty("ownerSession").GetString()!=session
    ||ack.GetProperty("prepareToken").GetString()!=token||ack.GetProperty("workerInstance").GetString()!=worker
    ||ack.GetProperty("state").GetString()!="prepared")return false;
   var ids=expected.Select(x=>x.Id).OrderBy(x=>x).ToArray();
   if(ids.Distinct().Count()!=ids.Length||!ack.GetProperty("reservationIds").EnumerateArray().Select(x=>x.GetInt32()).OrderBy(x=>x).SequenceEqual(ids))return false;
   foreach(var prior in expected)
   {
    var row=store.GetById(prior.Id);
    if(row is null||row.Status!=ReservationStatus.Scheduled||row.DataVersion!=prior.DataVersion
     ||row.StartTime!=prior.StartTime||row.EndTime!=prior.EndTime||row.UserChainPreviousId!=prior.UserChainPreviousId||row.UserChainRootId!=prior.UserChainRootId)return false;
    var entries=current.Entries.Where(x=>x.ReservationId==prior.Id).ToArray();
    var old=evidence.Where(x=>x.ReservationId==prior.Id).ToArray();
    // Canonical DB repair can include a later reservation not yet registered with this Worker.
    // Such a row needs no runtime protection released. It must be absent from both complete
    // observations and from both protected-ID sets; any partial/changed ownership still rejects.
    if(entries.Length==0&&old.Length==0&&!observedProtected.Contains(prior.Id)
       &&!current.ProtectedReservationIds.Contains(prior.Id))continue;
    if(entries.Length!=1||old.Length!=1)return false;
    var e=entries[0];var o=old[0];
    if(e.Reason!="control_segment"||e.OwnerReservationId!=owner||e.OwnerCommitState!="RecordingCommitted"
      ||e.ControlRootReservationId!=o.ControlRootReservationId||e.OwnerReservationId!=o.OwnerReservationId||e.OpenAt!=o.OpenAt)return false;
   }
   remaining.ExceptWith(ids);return true;
  }
  catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return false;}
 }
}
