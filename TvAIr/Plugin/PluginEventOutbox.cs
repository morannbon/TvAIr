using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TvAIrPlugin;

namespace TvAIr.Plugin;

// A single consumer owns delivery. Its synchronous callbacks cannot wait for
// that same consumer to free space. Only this reentrant overflow is spooled;
// ordinary publishers retain bounded-queue backpressure and FIFO admission.
internal sealed class PluginEventOutbox(int capacity) : IDisposable
{
    private static readonly JsonSerializerOptions SpillJson = new()
    {
        Converters = { new DetailsConverter() }
    };
    private readonly object gate = new();
    private readonly Queue<TvAirEventDto> memory = new();
    private readonly Queue<WaitingWriter> writers = new();
    private readonly SemaphoreSlim available = new(0, 1);
    private FileStream? spill;
    private long readOffset;
    private long writeOffset;
    private bool completed;

    public void Write(TvAirEventDto source, bool fromDispatcher)
    {
        lock (gate)
        {
            if (completed) throw new ChannelClosedException();
            if (spill is null && memory.Count < capacity && writers.Count == 0)
            {
                memory.Enqueue(source);
                Signal();
                return;
            }
            if (fromDispatcher)
            {
                try
                {
                    // Writers already waiting before the callback's publication
                    // must stay ahead of it, just as in the original FIFO channel.
                    while (writers.TryPeek(out var waiting))
                    {
                        AppendSpill(waiting.Source);
                        writers.Dequeue();
                        waiting.Accepted = true;
                    }
                    AppendSpill(source);
                }
                finally
                {
                    Monitor.PulseAll(gate);
                    Signal();
                }
                return;
            }

            var writer = new WaitingWriter(source);
            writers.Enqueue(writer);
            while (!writer.Accepted && !completed) Monitor.Wait(gate);
            if (!writer.Accepted) throw new ChannelClosedException();
        }
    }

    public async ValueTask<TvAirEventDto?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // Drain buffered events before observing cancellation, like Channel.ReadAllAsync.
            lock (gate)
            {
                if (memory.TryDequeue(out var source))
                {
                    PromoteWriters();
                    return source;
                }
                if (spill is not null && readOffset < writeOffset)
                {
                    var item = ReadSpill();
                    if (readOffset == writeOffset)
                    {
                        CloseSpill();
                        PromoteWriters();
                    }
                    return item;
                }
                if (completed) return null;
            }
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private TvAirEventDto ReadSpill()
    {
        var stream = spill!;
        stream.Position = readOffset;
        Span<byte> header = stackalloc byte[sizeof(int)];
        stream.ReadExactly(header);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > writeOffset - stream.Position)
            throw new InvalidDataException("Invalid typed event spool record.");
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        var item = JsonSerializer.Deserialize<TvAirEventDto>(bytes, SpillJson)
            ?? throw new InvalidDataException("Missing typed event spool record.");
        readOffset = stream.Position;
        return item;
    }

    public void Complete()
    {
        lock (gate)
        {
            if (completed) return;
            completed = true;
            // Unaccepted producers are released explicitly; accepted events are
            // still drained by the consumer before a normal shutdown completes.
            writers.Clear();
            Monitor.PulseAll(gate);
            Signal();
        }
    }

    private void PromoteWriters()
    {
        if (spill is not null) return;
        while (memory.Count < capacity && writers.TryDequeue(out var writer))
        {
            memory.Enqueue(writer.Source);
            writer.Accepted = true;
        }
        Monitor.PulseAll(gate);
    }

    private void AppendSpill(TvAirEventDto source)
    {
        // Serialize before touching the file. A failed append is rolled back and
        // surfaced to the publisher, never treated as a successful publication.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, SpillJson);
        spill ??= new FileStream(
            Path.Combine(Path.GetTempPath(), "TvAIr.TypedEvents." + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        try
        {
            spill.Position = writeOffset;
            Span<byte> header = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            spill.Write(header);
            spill.Write(bytes);
            spill.Flush();
            writeOffset = spill.Position;
        }
        catch
        {
            try { spill.SetLength(writeOffset); } catch { }
            if (writeOffset == readOffset) CloseSpill();
            throw;
        }
    }

    private void Signal()
    {
        if (available.CurrentCount == 0) available.Release();
    }

    private void CloseSpill()
    {
        var stream = spill;
        spill = null;
        readOffset = writeOffset = 0;
        stream?.Dispose();
    }

    public void Dispose()
    {
        Complete();
        lock (gate)
        {
            memory.Clear();
            CloseSpill();
            available.Dispose();
        }
    }

    private sealed class WaitingWriter(TvAirEventDto source)
    {
        public TvAirEventDto Source { get; } = source;
        public bool Accepted { get; set; }
    }

    // The Host emits both ordinal and ordinal-ignore-case Details maps. Preserve
    // their lookup semantics as well as values when an event passes through disk.
    private sealed class DetailsConverter : JsonConverter<IReadOnlyDictionary<string, string>>
    {
        public override IReadOnlyDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var result = new Dictionary<string, string>(root.GetProperty("ignoreCase").GetBoolean()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var property in root.GetProperty("entries").EnumerateObject())
                result.Add(property.Name, property.Value.GetString()!);
            return result;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
        {
            var ignoreCase = value is Dictionary<string, string> dictionary
                ? dictionary.Comparer.Equals("a", "A")
                : value.Keys.Any(key =>
                {
                    var alternate = key.ToUpperInvariant() == key ? key.ToLowerInvariant() : key.ToUpperInvariant();
                    return alternate != key && !value.Keys.Contains(alternate, StringComparer.Ordinal) && value.ContainsKey(alternate);
                });
            writer.WriteStartObject();
            writer.WriteBoolean("ignoreCase", ignoreCase);
            writer.WriteStartObject("entries");
            foreach (var pair in value) writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }
}
