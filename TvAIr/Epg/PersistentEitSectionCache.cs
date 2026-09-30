using System.Collections.Concurrent;

namespace TvAIr.Epg;

internal sealed class PersistentEitSectionCache
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string rootDirectory;

    public PersistentEitSectionCache(string? rootDirectory = null)
    {
        this.rootDirectory = rootDirectory ?? Path.Combine(AppContext.BaseDirectory, "runtime", "epg-eit-section-cache");
    }

    public IReadOnlyList<byte[]> LoadMatchingSections(IReadOnlyList<EitSectionReader.EitSubtableVersion> currentVersions)
    {
        if (currentVersions.Count == 0) return Array.Empty<byte[]>();

        var loaded = new List<byte[]>();
        foreach (var version in currentVersions)
        {
            var path = GetPath(version.NetworkId, version.TransportStreamId, version.ServiceId, version.TableId);
            lock (FileLocks.GetOrAdd(path, static _ => new object()))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new BinaryReader(stream);
                    if (reader.ReadUInt32() != 0x43544945) continue; // EITC
                    if (reader.ReadByte() != 1) continue;
                    var storedVersion = reader.ReadByte();
                    if (storedVersion != version.VersionNumber) continue;
                    var count = reader.ReadUInt16();
                    for (var i = 0; i < count; i++)
                    {
                        var sectionNumber = reader.ReadByte();
                        var length = reader.ReadUInt16();
                        if (length is < 18 or > 4096 || stream.Position + length > stream.Length) break;
                        var section = reader.ReadBytes(length);
                        if (section.Length != length) break;
                        // section_number is duplicated in the payload; keep the stored index as a cheap corruption guard.
                        if (section.Length > 6 && section[6] == sectionNumber) loaded.Add(section);
                    }
                }
                catch
                {
                    // Cache is opportunistic. Corruption or I/O failure must never fail an EPG run.
                }
            }
        }
        return loaded;
    }


    internal IReadOnlyList<PreviousVersionSection> LoadPreviousVersionSections(
        IReadOnlyList<EitSectionReader.EitSubtableVersion> currentVersions)
    {
        if (currentVersions.Count == 0) return Array.Empty<PreviousVersionSection>();

        var loaded = new List<PreviousVersionSection>();
        foreach (var version in currentVersions)
        {
            if (!EitTableContract.IsActualBasicSchedule(version.TableId)) continue;

            var path = GetPath(version.NetworkId, version.TransportStreamId, version.ServiceId, version.TableId);
            lock (FileLocks.GetOrAdd(path, static _ => new object()))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new BinaryReader(stream);
                    if (reader.ReadUInt32() != 0x43544945) continue; // EITC
                    if (reader.ReadByte() != 1) continue;
                    var storedVersion = reader.ReadByte();
                    if (storedVersion == version.VersionNumber) continue;
                    var count = reader.ReadUInt16();
                    for (var i = 0; i < count; i++)
                    {
                        var sectionNumber = reader.ReadByte();
                        var length = reader.ReadUInt16();
                        if (length is < 18 or > 4096 || stream.Position + length > stream.Length) break;
                        var section = reader.ReadBytes(length);
                        if (section.Length != length) break;
                        if (section.Length <= 6 || section[6] != sectionNumber) continue;
                        loaded.Add(new PreviousVersionSection(
                            version.NetworkId, version.TransportStreamId, version.ServiceId, version.TableId,
                            version.VersionNumber, storedVersion, sectionNumber, section));
                    }
                }
                catch
                {
                    // Previous-version donor lookup is optional quality support. Cache I/O must never fail an EPG run.
                }
            }
        }

        return loaded;
    }

    internal sealed record PreviousVersionSection(
        ushort NetworkId, ushort TransportStreamId, ushort ServiceId, byte TableId,
        byte CurrentVersionNumber, byte PreviousVersionNumber, byte SectionNumber, byte[] Section);

    internal StoredSubtableInfo ProbeStoredSubtable(ushort nid, ushort tsid, ushort sid, byte tableId)
    {
        var path = GetPath(nid, tsid, sid, tableId);
        lock (FileLocks.GetOrAdd(path, static _ => new object()))
        {
            try
            {
                if (!File.Exists(path)) return new StoredSubtableInfo(false, false, 0, 0);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt32() != 0x43544945) return new StoredSubtableInfo(true, false, 0, 0);
                if (reader.ReadByte() != 1) return new StoredSubtableInfo(true, false, 0, 0);
                var version = reader.ReadByte();
                var count = reader.ReadUInt16();
                return new StoredSubtableInfo(true, true, version, count);
            }
            catch
            {
                return new StoredSubtableInfo(true, false, 0, 0);
            }
        }
    }

    internal sealed record StoredSubtableInfo(bool Exists, bool Valid, byte VersionNumber, int SectionCount);

    public void MergeAndSave(IReadOnlyList<EitSectionReader.EitCachedSection> acceptedSections)
    {
        foreach (var group in acceptedSections
            .GroupBy(static section => (section.NetworkId, section.TransportStreamId, section.ServiceId, section.TableId, section.VersionNumber)))
        {
            var key = group.Key;
            var path = GetPath(key.NetworkId, key.TransportStreamId, key.ServiceId, key.TableId);
            lock (FileLocks.GetOrAdd(path, static _ => new object()))
            {
                try
                {
                    Directory.CreateDirectory(rootDirectory);
                    var sections = new SortedDictionary<byte, byte[]>();
                    if (File.Exists(path))
                    {
                        using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var reader = new BinaryReader(existing);
                        if (reader.ReadUInt32() == 0x43544945 && reader.ReadByte() == 1 && reader.ReadByte() == key.VersionNumber)
                        {
                            var count = reader.ReadUInt16();
                            for (var i = 0; i < count; i++)
                            {
                                var sectionNumber = reader.ReadByte();
                                var length = reader.ReadUInt16();
                                if (length is < 18 or > 4096 || existing.Position + length > existing.Length) break;
                                var section = reader.ReadBytes(length);
                                if (section.Length != length) break;
                                if (section.Length > 6 && section[6] == sectionNumber) sections[sectionNumber] = section;
                            }
                        }
                    }

                    foreach (var section in group) sections[section.SectionNumber] = section.Section;
                    if (sections.Count == 0) continue;

                    var temp = path + ".tmp." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId;
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new BinaryWriter(output))
                    {
                        writer.Write(0x43544945u); // EITC
                        writer.Write((byte)1);
                        writer.Write(key.VersionNumber);
                        writer.Write((ushort)Math.Min(sections.Count, ushort.MaxValue));
                        foreach (var pair in sections.Take(ushort.MaxValue))
                        {
                            writer.Write(pair.Key);
                            writer.Write((ushort)pair.Value.Length);
                            writer.Write(pair.Value);
                        }
                    }
                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    // Persistence is additive quality support only; never turn a cache failure into capture failure.
                }
            }
        }
    }

    private string GetPath(ushort nid, ushort tsid, ushort sid, byte tableId)
        => Path.Combine(rootDirectory, $"{nid:X4}_{tsid:X4}_{sid:X4}_{tableId:X2}.eitc");
}
