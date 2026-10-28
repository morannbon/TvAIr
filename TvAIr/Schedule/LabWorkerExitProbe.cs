using System.Diagnostics;
using TvAIr.Core;
namespace TvAIr.Schedule;
public enum LabWorkerPresence { Unknown,Alive,Exited }
// A missing registry entry or inaccessible metadata is not proof of exit.
public static class LabWorkerExitProbe
{
 public static LabWorkerPresence Observe(ManagedProcessIdentity registered)
 {
  if(!registered.IsAvailable||registered.ProcessId<=0||!registered.ProcessStartTimeUtc.HasValue)return LabWorkerPresence.Unknown;
  try{
   using var process=Process.GetProcessById(registered.ProcessId);
   if(process.HasExited)return LabWorkerPresence.Exited;
   return process.StartTime.ToUniversalTime()==registered.ProcessStartTimeUtc.Value?LabWorkerPresence.Alive:LabWorkerPresence.Exited;
  }
  catch(ArgumentException){return LabWorkerPresence.Exited;}
  catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException){return LabWorkerPresence.Unknown;}
 }
}
