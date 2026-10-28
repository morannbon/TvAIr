using Microsoft.Data.Sqlite;
using TvAIr.Core;
namespace TvAIr.Schedule;
// Isolated durable lifecycle experiment. All writes are invoked by the canonical route.
public sealed class LabWithdrawalJournal(Database db)
{
 internal static void EnsureSchema(SqliteConnection con,SqliteTransaction tx)
 {
  if(!ReferenceEquals(tx.Connection,con))throw new ArgumentException("transaction connection mismatch");
  using var cmd=con.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="CREATE TABLE IF NOT EXISTS lab_withdrawal_operations(operation TEXT PRIMARY KEY,selected INTEGER NOT NULL,payload TEXT NOT NULL,targets TEXT NOT NULL,state TEXT NOT NULL CHECK(state IN ('pending','committed','aborted')));CREATE TABLE IF NOT EXISTS lab_withdrawal_claims(reservation INTEGER PRIMARY KEY,operation TEXT NOT NULL);";cmd.ExecuteNonQuery();
  using var columns=con.CreateCommand();columns.Transaction=tx;columns.CommandText="PRAGMA table_info(lab_withdrawal_operations)";
  bool hasOutcome=false,hasWorker=false;using(var reader=columns.ExecuteReader()){while(reader.Read()){if(reader.GetString(1)=="outcome")hasOutcome=true;if(reader.GetString(1)=="worker_instance")hasWorker=true;}}
  if(!hasOutcome){columns.CommandText="ALTER TABLE lab_withdrawal_operations ADD COLUMN outcome TEXT NULL";columns.ExecuteNonQuery();}
  if(!hasWorker){columns.CommandText="ALTER TABLE lab_withdrawal_operations ADD COLUMN worker_instance TEXT NULL";columns.ExecuteNonQuery();}
 }
 public bool Register(string operation,int selected,string payload,IEnumerable<int>? affected=null)
 {
  using var con=db.Open();using var tx=con.BeginTransaction();
  var accepted=RegisterCore(con,tx,operation,selected,payload,affected);
  if(accepted)tx.Commit();else tx.Rollback();return accepted;
 }
 internal static bool RegisterCore(SqliteConnection con,SqliteTransaction tx,string operation,int selected,string payload,IEnumerable<int>? affected=null)
 {
  var ids=(affected??new[]{selected}).Distinct().OrderBy(x=>x).ToArray();
  if(string.IsNullOrWhiteSpace(operation)||ids.Length==0||ids.Any(id=>id<=0)||!ids.Contains(selected))return false;
  var targets=System.Text.Json.JsonSerializer.Serialize(ids);
  if(!ReferenceEquals(tx.Connection,con))throw new ArgumentException("transaction connection mismatch");
  using var cmd=con.CreateCommand();cmd.Transaction=tx;
  EnsureSchema(con,tx);
  cmd.Parameters.AddWithValue("$op",operation);cmd.Parameters.AddWithValue("$id",selected);cmd.Parameters.AddWithValue("$payload",payload);cmd.Parameters.AddWithValue("$targets",targets);
  cmd.CommandText="SELECT COUNT(*) FROM lab_withdrawal_operations WHERE operation=$op";var exists=Convert.ToInt32(cmd.ExecuteScalar())!=0;
  if(exists){cmd.CommandText="SELECT COUNT(*) FROM lab_withdrawal_operations WHERE operation=$op AND selected=$id AND payload=$payload AND targets=$targets";var same=Convert.ToInt32(cmd.ExecuteScalar())==1;return same;}
  foreach(var id in ids){cmd.CommandText="SELECT COUNT(*) FROM lab_withdrawal_claims WHERE reservation=$held";cmd.Parameters.AddWithValue("$held",id);var busy=Convert.ToInt32(cmd.ExecuteScalar())!=0;cmd.Parameters.RemoveAt("$held");if(busy)return false;}
  cmd.CommandText="INSERT INTO lab_withdrawal_operations(operation,selected,payload,targets,state) VALUES($op,$id,$payload,$targets,'pending')";cmd.ExecuteNonQuery();
  foreach(var id in ids){cmd.CommandText="INSERT INTO lab_withdrawal_claims VALUES($held,$op)";cmd.Parameters.AddWithValue("$held",id);cmd.ExecuteNonQuery();cmd.Parameters.RemoveAt("$held");}
  return true;
 }
 public bool AcknowledgeWorker(string operation,string state)
 {
  if(state is not ("committed" or "aborted"))return false;
  using var con=db.Open();using var tx=con.BeginTransaction();using var cmd=con.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="SELECT COUNT(*) FROM lab_withdrawal_operations WHERE operation=$op AND state=$state";cmd.Parameters.AddWithValue("$op",operation);cmd.Parameters.AddWithValue("$state",state);
  if(Convert.ToInt32(cmd.ExecuteScalar())!=1)return false;
  cmd.CommandText="DELETE FROM lab_withdrawal_claims WHERE operation=$op";cmd.ExecuteNonQuery();tx.Commit();return true;
 }
 public bool ConfirmOwnerExited(string operation,string state,LabWithdrawalOwnerIdentity owner)
 {
  if(state is not ("committed" or "aborted")||!owner.IsValid)return false;
  using var con=db.Open();using var tx=con.BeginTransaction();using var cmd=con.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="SELECT payload FROM lab_withdrawal_operations WHERE operation=$op AND state=$state";cmd.Parameters.AddWithValue("$op",operation);cmd.Parameters.AddWithValue("$state",state);
  if(cmd.ExecuteScalar() is not string payload)return false;
  try{
   using var doc=System.Text.Json.JsonDocument.Parse(payload);var saved=System.Text.Json.JsonSerializer.Deserialize<LabWithdrawalOwnerIdentity>(doc.RootElement.GetProperty("ownerBinding").GetRawText());
   if(!owner.SameOwner(saved)||LabWorkerExitProbe.Observe(owner.ProcessIdentity)!=LabWorkerPresence.Exited)return false;
  }catch(Exception ex)when(ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException){return false;}
  cmd.CommandText="DELETE FROM lab_withdrawal_claims WHERE operation=$op";cmd.ExecuteNonQuery();tx.Commit();return true;
 }
 public string TryResolveForRecovery(string operation)
 {
  try{return ResolveForRecovery(operation);}
  catch(Exception ex) when(ex is SqliteException or IOException or UnauthorizedAccessException){return "unknown";}
 }
 public (string State,string Payload)? Read(string operation)
 {
  using var con=db.Open();using var cmd=con.CreateCommand();cmd.CommandText="SELECT state,payload FROM lab_withdrawal_operations WHERE operation=$op";cmd.Parameters.AddWithValue("$op",operation);using var reader=cmd.ExecuteReader();return reader.Read()?(reader.GetString(0),reader.GetString(1)):null;
 }
 public bool BindWorkerInstance(string operation,string payload,string instance)
 {
  if(!Guid.TryParseExact(instance,"N",out _))return false;
  using var con=db.Open();using var tx=con.BeginTransaction();using var cmd=con.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="SELECT worker_instance FROM lab_withdrawal_operations WHERE operation=$op AND payload=$payload";
  cmd.Parameters.AddWithValue("$instance",instance);cmd.Parameters.AddWithValue("$op",operation);cmd.Parameters.AddWithValue("$payload",payload);
  using(var reader=cmd.ExecuteReader()){if(!reader.Read())return false;if(!reader.IsDBNull(0))return reader.GetString(0)==instance;}
  cmd.CommandText="UPDATE lab_withdrawal_operations SET worker_instance=$instance WHERE operation=$op AND payload=$payload AND worker_instance IS NULL";
  if(cmd.ExecuteNonQuery()!=1)return false;tx.Commit();return true;
 }
 public string? ReadWorkerInstance(string operation)
 {
  using var con=db.Open();using var cmd=con.CreateCommand();cmd.CommandText="SELECT worker_instance FROM lab_withdrawal_operations WHERE operation=$op";cmd.Parameters.AddWithValue("$op",operation);return cmd.ExecuteScalar() as string;
 }
 public static void SaveOutcome(SqliteConnection con,SqliteTransaction tx,string operation,ReservationCancellationResult result)
 {
  if(!result.Applied||result.Cancelled.Count!=1||result.Cancelled[0].Id!=result.RequestedId)throw new InvalidOperationException("Invalid automatic cancellation result.");
  using var cmd=con.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE lab_withdrawal_operations SET outcome=$outcome WHERE operation=$op AND selected=$id AND state='pending' AND outcome IS NULL";
  cmd.Parameters.AddWithValue("$outcome",System.Text.Json.JsonSerializer.Serialize(result));cmd.Parameters.AddWithValue("$op",operation);cmd.Parameters.AddWithValue("$id",result.RequestedId);
  if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Durable outcome identity mismatch.");
 }
 public ReservationCancellationResult? ReadOutcome(string operation)
 {
  using var con=db.Open();using var cmd=con.CreateCommand();cmd.CommandText="SELECT selected,outcome FROM lab_withdrawal_operations WHERE operation=$op AND state='committed'";cmd.Parameters.AddWithValue("$op",operation);
  using var reader=cmd.ExecuteReader();if(!reader.Read()||reader.IsDBNull(1))return null;
  try{var result=System.Text.Json.JsonSerializer.Deserialize<ReservationCancellationResult>(reader.GetString(1));return result is {Applied:true}&&result.RequestedId==reader.GetInt32(0)&&result.Cancelled is {Count:1}&&result.Cancelled[0] is {Status:ReservationStatus.Cancelled} cancelled&&cancelled.Id==result.RequestedId&&result.PreRecordChildrenCancelled>=0?result:null;}
  catch(Exception ex)when(ex is System.Text.Json.JsonException or InvalidOperationException){return null;}
 }
 public LabWithdrawalRecoveryRow? FindOperation(string operation)
 {
  using var con=db.Open();using var cmd=con.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='lab_withdrawal_operations'";if(Convert.ToInt32(cmd.ExecuteScalar())==0)return null;
  cmd.CommandText="SELECT selected,payload,state FROM lab_withdrawal_operations WHERE operation=$op";cmd.Parameters.AddWithValue("$op",operation);using var reader=cmd.ExecuteReader();return reader.Read()?new(operation,reader.GetInt32(0),reader.GetString(1),reader.GetString(2)):null;
 }
 public bool IsSettled(string operation,string decision)
 {
  if(decision is not ("committed" or "aborted"))return false;
  using var con=db.Open();using var cmd=con.CreateCommand();
  cmd.CommandText="SELECT COUNT(*) FROM lab_withdrawal_operations o WHERE operation=$op AND state=$state AND NOT EXISTS(SELECT 1 FROM lab_withdrawal_claims c WHERE c.operation=o.operation)";
  cmd.Parameters.AddWithValue("$op",operation);cmd.Parameters.AddWithValue("$state",decision);
  return Convert.ToInt32(cmd.ExecuteScalar())==1;
 }
 public bool HasUnsettledForControl(string controlPath)
 {
  foreach(var row in ListUnsettled()){
   try{
    using var doc=System.Text.Json.JsonDocument.Parse(row.Payload);
    var owner=System.Text.Json.JsonSerializer.Deserialize<LabWithdrawalOwnerIdentity>(doc.RootElement.GetProperty("ownerBinding").GetRawText());
    // Unknown ownership is not evidence that the bundle can be discarded.
    if(owner is not{IsValid:true}||string.Equals(owner.ControlPath,controlPath,StringComparison.OrdinalIgnoreCase))return true;
   }catch(Exception ex)when(ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return true;}
  }
  return false;
 }
 public IReadOnlyList<LabWithdrawalRecoveryRow> ListUnsettled()
 {
  using var con=db.Open();using var cmd=con.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='lab_withdrawal_operations'";if(Convert.ToInt32(cmd.ExecuteScalar())==0)return Array.Empty<LabWithdrawalRecoveryRow>();
  cmd.CommandText="SELECT operation,selected,payload,state FROM lab_withdrawal_operations o WHERE state='pending' OR EXISTS(SELECT 1 FROM lab_withdrawal_claims c WHERE c.operation=o.operation) ORDER BY operation";
  var rows=new List<LabWithdrawalRecoveryRow>();using var reader=cmd.ExecuteReader();while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3)));return rows.AsReadOnly();
 }
 public static void Commit(SqliteConnection con,SqliteTransaction tx,string operation,string payload)
 {
  using var cmd=con.CreateCommand();cmd.Transaction=tx;cmd.Parameters.AddWithValue("$op",operation);cmd.Parameters.AddWithValue("$payload",payload);
  cmd.CommandText="SELECT targets FROM lab_withdrawal_operations WHERE operation=$op AND payload=$payload AND state='pending'";
  var text=cmd.ExecuteScalar() as string;
  var targets=text is null?null:System.Text.Json.JsonSerializer.Deserialize<int[]>(text);
  if(targets is null||targets.Length==0||targets.Any(id=>id<=0)||targets.Distinct().Count()!=targets.Length)throw new InvalidOperationException("Durable operation is not pending with valid targets.");
  cmd.CommandText="SELECT reservation FROM lab_withdrawal_claims WHERE operation=$op ORDER BY reservation";var owned=new List<int>();using(var reader=cmd.ExecuteReader()){while(reader.Read())owned.Add(reader.GetInt32(0));}
  if(!targets.OrderBy(id=>id).SequenceEqual(owned))throw new InvalidOperationException("Durable operation does not own exactly the affected targets.");
  cmd.CommandText="UPDATE lab_withdrawal_operations SET state='committed' WHERE operation=$op AND payload=$payload AND state='pending'";
  if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Durable operation is no longer pending with matching identity.");
 }
 public string ResolveForRecovery(string operation)
 {
  using var con=db.Open();using var tx=con.BeginTransaction();using var cmd=con.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE lab_withdrawal_operations SET state='aborted' WHERE operation=$op AND state='pending'";cmd.Parameters.AddWithValue("$op",operation);cmd.ExecuteNonQuery();cmd.CommandText="SELECT state FROM lab_withdrawal_operations WHERE operation=$op";var state=cmd.ExecuteScalar() as string??"unknown";tx.Commit();return state;
 }
}
public sealed class LabJournalCommand(Func<ReservationStore,bool> execute) : ReservationTopologyCommand
{
 public bool? Result {get;private set;}
 internal override bool RequiresProjectionMetadata=>false;
 internal override bool IsRejected=>Result==false;
 internal override bool RequiresAllocationAfterCommit=>false;
 internal override void Apply(ReservationStore store,bool enabled,ReservationProjectionMetadataStore? metadataStore=null){if(Result is null)Result=execute(store);}
}

public sealed record LabWithdrawalRecoveryRow(string Operation,int Selected,string Payload,string State);
