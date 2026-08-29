namespace TvAIr.Plugin;

/// <summary>
/// Owns legacy plugin files under one plugin-scoped directory. New plugins use
/// the generic Storage API; this owner keeps the legacy loader path safe and atomic.
/// </summary>
internal sealed class PluginOwnedFileStore
{
    private readonly string _rootDirectory;

    public PluginOwnedFileStore(string dataDirectory, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        _rootDirectory = Path.Combine(dataDirectory, "Plugins", PluginIdentity.Normalize(pluginId, "UnknownPlugin"));
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public string? Read(string relativePath)
    {
        var path = Resolve(relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Write(string relativePath, string? content)
    {
        var path = Resolve(relativePath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content ?? string.Empty);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temp, path, overwrite: true); }
                catch (IOException) { File.Move(temp, path, overwrite: true); }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("プラグインファイル名が空です。");
        var combined = Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));
        var root = Path.GetFullPath(_rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PluginDataDirectory外へのアクセスは禁止です。");
        return combined;
    }
}
