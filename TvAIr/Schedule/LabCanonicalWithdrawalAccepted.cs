using TvAIr.Core;
namespace TvAIr.Schedule;
public sealed record LabCanonicalWithdrawalAccepted(string Payload,IReadOnlyList<Reservation> Expected,string State="pending",bool Reused=false,LabWithdrawalOwnerIdentity? OwnerBinding=null);
