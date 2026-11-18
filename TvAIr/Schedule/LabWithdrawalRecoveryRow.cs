namespace TvAIr.Schedule;
// Durable journal projection used to restore unsettled withdrawal operations after restart.
public sealed record LabWithdrawalRecoveryRow(string Operation,int Selected,string Payload,string State);
