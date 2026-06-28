using System.Runtime.InteropServices;

namespace TvAIr.Epg;

internal static class AribPsiSiDecoder
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DecodeAribWDelegate(byte[] data, int length);

    private static readonly object Gate = new();
    private static bool initialized;
    private static DecodeAribWDelegate? decode;

    public static AribDecodeResult Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return new AribDecodeResult("none", "empty_input", string.Empty);

        EnsureLoaded();
        if (decode != null)
        {
            try
            {
                var data = bytes.ToArray();
                var ptr = decode(data, data.Length);
                if (ptr != IntPtr.Zero)
                {
                    var text = Marshal.PtrToStringUni(ptr) ?? string.Empty;
                    return new AribDecodeResult("native", text.Length == 0 ? "native_empty" : "native_ok", text);
                }
            }
            catch
            {
            }
        }

        return new AribDecodeResult("failed", "native_unavailable", string.Empty);
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (initialized) return;
            initialized = true;
            try
            {
                var arch = Environment.Is64BitProcess ? "x64" : "x86";
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "native", arch, "AribDecodeBridge.dll"),
                    Path.Combine(AppContext.BaseDirectory, "AribDecodeBridge.dll")
                };

                foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path)) continue;
                    var module = LoadLibrary(path);
                    if (module == IntPtr.Zero) continue;
                    var proc = GetProcAddress(module, "DecodeAribW");
                    if (proc == IntPtr.Zero) continue;
                    decode = Marshal.GetDelegateForFunctionPointer<DecodeAribWDelegate>(proc);
                    return;
                }
            }
            catch
            {
                decode = null;
            }
        }
    }
}

internal readonly record struct AribDecodeResult(string Route, string Status, string Text);
