using System.Globalization;
using System.Runtime.InteropServices;

namespace TvAIr.Storage;

// DiskRelay.ini is owned by TVTest. Read only; do not persist a second copy of its settings.
public sealed record DiskRelayPolicy(long LowFreeSpaceBytes, string[] Folders)
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available,
        out ulong total, out ulong free);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    public static DiskRelayPolicy? Load(string pluginsDirectory, out string status)
    {
        if (string.IsNullOrWhiteSpace(pluginsDirectory))
        { status = "not_configured"; return null; }
        try
        {
            var path = Path.Combine(Path.GetFullPath(pluginsDirectory), "DiskRelay.ini");
            if (!File.Exists(path)) { status = "ini_missing"; return null; }
            // Win32 profile APIs use the system ANSI code page unless the INI has a Unicode BOM.
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var section = "";
            foreach (var source in File.ReadAllLines(path, ReadEncoding(path)))
            {
                var line = source.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                var separator = line.IndexOf('=');
                if (!section.Equals("Settings", StringComparison.OrdinalIgnoreCase) || separator <= 0) continue;
                var key = line[..separator].Trim();
                // GetPrivateProfileString uses the first occurrence of a key.
                values.TryAdd(key, line[(separator + 1)..].Trim().Trim('"'));
            }
            uint megabytes = 64;
            if (values.TryGetValue("LowFreeSpace", out var raw)
                && !uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out megabytes))
                throw new InvalidDataException("invalid_low_free_space");
            var folders = Enumerable.Range(0, 3)
                .Select(i => values.GetValueOrDefault("Folder" + i, "")).ToArray();
            // A relative folder depends on TVTest's process working directory. Do not silently
            // reinterpret it against TvAIr's different working directory.
            if (folders.Any(x => x.Length != 0 && !Path.IsPathFullyQualified(x)))
                throw new InvalidDataException("relative_relay_folder");
            status = "loaded";
            return new DiskRelayPolicy((long)megabytes * 0x100000, folders);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { status = "read_failed:" + ex.Message; return null; }
    }

    private static System.Text.Encoding ReadEncoding(string path)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // StreamReader detects UTF BOMs; this fallback matches the Windows ANSI code page.
        return System.Text.Encoding.GetEncoding((int)GetACP());
    }

    public static long? AvailableBytes(string directory)
    {
        if (!GetDiskFreeSpaceEx(directory, out var available, out _, out _)) return null;
        return available > long.MaxValue ? long.MaxValue : (long)available;
    }

    // The cursor belongs to an individual output, never the global policy or another recording.
    public string? SelectNext(ref int cursor,
        Func<string, bool>? exists = null, Func<string, long?>? available = null)
    {
        exists ??= Directory.Exists;
        available ??= AvailableBytes;
        for (; cursor < Folders.Length; cursor++)
        {
            var folder = Folders[cursor];
            if (folder.Length == 0 || !exists(folder)) continue;
            var bytes = available(folder);
            if (bytes.HasValue && bytes.Value > LowFreeSpaceBytes)
                return folder;
        }
        return null;
    }
}

