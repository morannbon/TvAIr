using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TvAIr.Tuner;
using TvAIrPlugin;

var assembly = typeof(ViewerOperationResult).Assembly;
var capability = assembly.GetType("TvAIr.Plugin.PluginCapabilityContext", true)!;
var viewers = capability.GetNestedTypes(BindingFlags.NonPublic).Single(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Any(m => m.Name == "ToDto" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ViewerOperationResult)));
var toDto = viewers.GetMethod("ToDto", BindingFlags.NonPublic | BindingFlags.Static)!;
var runtime = assembly.GetType("TvAIr.Plugin.PluginRuntimeContext+RuntimeViewersApi", true)!;
var toRuntime = runtime.GetMethod("ToRuntimeOperation", BindingFlags.NonPublic | BindingFlags.Instance)!;
var runtimeInstance = RuntimeHelpers.GetUninitializedObject(runtime);
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
foreach (var state in new[] { "started", "retuned", "restarted", "activated", "stopped", "warning", "denied", "failed" })
{
    bool success = state is not ("denied" or "failed");
    var source = new ViewerOperationResult(success, state, state == "warning" ? "qualityWarning" : success ? "" : "serviceUnavailable", "operation-message", "profile", "session", 17, "lease", 123, 4, 10, 20, "slot", new DateTime(2026,10,16,12,0,0), 2, 3, "running", "source-diagnostics");
    var dto = (TvAirViewerOperationResultDto)toDto.Invoke(null, new object[] { source })!;
    Check(!dto.FocusPreserved && dto.FocusPreserveFailureReason == "not_measured", "unmeasured focus falsely successful: " + state);
    Check(dto.ForegroundBeforeHwnd is null && dto.ForegroundBeforePid is null && dto.ForegroundAfterRetuneHwnd is null && dto.ForegroundAfterRetunePid is null && dto.ForegroundFinalHwnd is null && dto.ForegroundFinalPid is null, "invented foreground sample");
    Check(!dto.ForegroundChanged && !dto.ChangedToTargetViewer && !dto.RestorationAttempted && !dto.RestorationSucceeded, "invented focus action");
    Check(dto.FocusPolicyRequested == "not_requested" && dto.FocusPolicyApplied == "not_requested", "policy altered");
    Check(dto.Success == success && dto.OperationCompleted == source.OperationCompleted && dto.HasWarning == source.HasWarning && dto.ContinuationRecommended == source.ContinuationRecommended, "operation outcome altered");
    Check(dto.ViewerSessionId == "session" && dto.Generation == 17 && dto.LeaseId == "lease" && dto.ProcessId == 123 && dto.NetworkId == 4 && dto.TransportStreamId == 10 && dto.ServiceId == 20 && dto.LogicalViewerSlotId == "slot" && dto.ChannelSpace == 2 && dto.ChannelIndex == 3 && dto.Diagnostics == "source-diagnostics" && dto.Message == "operation-message", "identity or diagnostics lost");
    var result = toRuntime.Invoke(runtimeInstance, new object[] { dto })!;
    var succeeded = (bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)!;
    var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
    var json = JsonSerializer.SerializeToElement(value, value.GetType());
    Check(succeeded == success, "runtime success changed");
    Check(!json.GetProperty("FocusPreserved").GetBoolean() && json.GetProperty("FocusPreserveFailureReason").GetString() == "not_measured", "runtime lost unknown marker");
    Check(json.GetProperty("OperationCompleted").GetBoolean() == source.OperationCompleted && json.GetProperty("ContinuationRecommended").GetBoolean() == source.ContinuationRecommended && json.GetProperty("HasWarning").GetBoolean() == source.HasWarning, "runtime continuation changed");
    Check(json.GetProperty("Session").GetProperty("Generation").GetInt64() == 17 && json.GetProperty("CurrentService").GetProperty("ServiceId").GetInt32() == 20, "runtime session lost");
    passed++;Console.WriteLine("PASS " + state + " capability-to-runtime focus diagnostics and authoritative outcome");
}
Console.WriteLine($"{passed}/8 passed; pure DTO projection; no native focus operation executed.");
