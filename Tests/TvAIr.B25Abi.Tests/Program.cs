using System.Reflection;
using System.Runtime.InteropServices;

// Calls the actual product wrapper with an isolated mock DLL, not a real card.
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
var summaryType = assembly.GetType("TvAIrEpgRec.TsReadProbeSummary") ?? assembly.GetType("TsReadProbeSummary")!;
if (summaryType is null) throw new Exception("Summary type missing");
var summary = Activator.CreateInstance(summaryType, true)!;
var wrapper = assembly.GetTypes().Single(t => t.Name == "ExternalB25DecoderRuntime");
var trace = new List<string>();
Func<string, string, Task> progress = (stage, message) => { trace.Add(stage + " " + message); return Task.CompletedTask; };
var decoder = wrapper.GetMethod("TryCreate", All)!.Invoke(null, new object[] { summary, progress });
if (decoder is null) throw new Exception(string.Join("\n", trace));
var payload = new byte[188]; payload[0] = 0x47;
var result = wrapper.GetMethod("Decode", All)!.Invoke(decoder, new object[] { payload, payload.Length })!;
if (!(bool)result.GetType().GetProperty("Ok")!.GetValue(result)!) throw new Exception("Decode failed");
var data = (byte[])result.GetType().GetProperty("Data")!.GetValue(result)!;
if (!data.SequenceEqual(payload)) throw new Exception("Output mismatch");
var flushed = wrapper.GetMethod("Flush", All)!.Invoke(decoder, null)!;
if (!(bool)flushed.GetType().GetProperty("Ok")!.GetValue(flushed)!) throw new Exception("Flush failed");
var mockPath = Path.Combine(AppContext.BaseDirectory, "B25Decoder.dll");
var module = NativeLibrary.Load(mockPath);
try {
    ((IDisposable)decoder).Dispose();
    var getFlags = Marshal.GetDelegateForFunctionPointer<GetFlags>(NativeLibrary.GetExport(module, "GetTestFlags"));
    if (getFlags() != 15) throw new Exception("Not all native members called");
} finally { NativeLibrary.Free(module); }
Console.WriteLine($"PASS product B25 wrapper {RuntimeInformation.ProcessArchitecture}: Initialize/Decode/Flush/Release; output verified; hardware not used");
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate uint GetFlags();
