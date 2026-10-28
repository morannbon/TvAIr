using TvAIr.Core;
namespace TvAIr.Schedule;
// Immutable evidence stored in the canonical operation payload, not a second registry.
public sealed record LabWithdrawalOwnerIdentity(int OwnerReservationId,int ProcessId,DateTime ProcessStartTimeUtc,string? ExecutablePath,string ControlPath)
{
 public bool IsValid=>OwnerReservationId>0&&ProcessId>0&&ProcessStartTimeUtc.Kind==DateTimeKind.Utc
  &&ProcessStartTimeUtc!=default&&!string.IsNullOrWhiteSpace(ControlPath);
 public bool SameOwner(LabWithdrawalOwnerIdentity? other)=>IsValid&&other is{IsValid:true}
  &&OwnerReservationId==other.OwnerReservationId&&ProcessId==other.ProcessId&&ProcessStartTimeUtc==other.ProcessStartTimeUtc
  &&string.Equals(ExecutablePath,other.ExecutablePath,StringComparison.OrdinalIgnoreCase)
  &&string.Equals(ControlPath,other.ControlPath,StringComparison.OrdinalIgnoreCase);
 public ManagedProcessIdentity ProcessIdentity=>new(ProcessId,ProcessStartTimeUtc,ExecutablePath,true);
}
