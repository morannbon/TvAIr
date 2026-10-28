using Microsoft.Data.Sqlite;
using TvAIr.Core;
namespace TvAIr.Schedule;

internal sealed record ProgramReservationCleanupRequest(string Predicate,Action<SqliteCommand>? Bind,string Reason,
    Reservation? Replacement=null,ProgramRule? ExpectedRule=null,IReadOnlyList<int>? ExpiredRuleIds=null,int? EmptyRuleId=null,int? RequiredResolvedId=null);
internal sealed record ProgramReservationCleanupResult(bool Applied,string Reason,IReadOnlyList<int> RemovedIds,
    int AddedId,IReadOnlyList<int> RemovedRuleIds);
