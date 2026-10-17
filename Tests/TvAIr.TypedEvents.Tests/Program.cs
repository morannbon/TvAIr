using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Plugin;
using TvAIr.Schedule;
using TvAIrPlugin;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "TvAIr.EventTests"));
Directory.CreateDirectory(root);
const int capacity = 4096;
var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Wait(ManualResetEventSlim signal) => Check(signal.Wait(TimeSpan.FromSeconds(10)), "signal timeout");
PluginTypedEventHub Hub()
{
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var ini = new IniSettingsService(dir, Path.Combine(dir, "data"), 18787);
    return new PluginTypedEventHub(new LogRepository(), new ReservationMutationJournal(),
        new ChannelFileLoader(Options.Create(new ChannelMapSettings()), ini));
}
TvAirEventDto Item(string id) => new() { EventInstanceId = id, EventType = TvAirEventType.ReservationUpdated };
int Pending(PluginTypedEventHub hub) => (int)typeof(PluginTypedEventHub).GetField("pendingEventCount", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hub)!;
Task DispatchTask(PluginTypedEventHub hub) => (Task)typeof(PluginTypedEventHub).GetField("dispatchTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hub)!;
void Completion(PluginTypedEventHub hub, Action<TvAirEventDto> action) => typeof(PluginTypedEventHub)
    .GetEvent("DispatchCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetAddMethod(true)!.Invoke(hub, new object[] { action });
void Full(PluginTypedEventHub hub) { for (var i = 0; i < capacity; i++) hub.Publish(Item("fill:" + i)); }
void AssertOrder(ConcurrentQueue<TvAirEventDto> received, params string[] tail)
{
    var rows = received.ToArray();
    Check(rows.Length == capacity + 1 + tail.Length, "event count/loss/duplication");
    Check(rows[0].EventInstanceId == "root", "root order");
    for (var i = 0; i < capacity; i++) Check(rows[i + 1].EventInstanceId == "fill:" + i, "fill FIFO " + i);
    Check(rows.Skip(capacity + 1).Select(e => e.EventInstanceId).SequenceEqual(tail), "tail FIFO");
    Check(rows.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, rows.Length).Select(i => (long)i)), "sequence not monotonic");
}
void Case(string name, Action body)
{
    var task = Task.Run(body);
    if (!task.Wait(TimeSpan.FromSeconds(25)))
    {
        Console.WriteLine("FAIL " + name + ": watchdog timeout");
        Environment.Exit(1);
    }
    task.GetAwaiter().GetResult();
    passed++;
    Console.WriteLine("PASS " + name);
}

Case("full queue callback publication returns and preserves FIFO", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var returned = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    var received = new ConcurrentQueue<TvAirEventDto>();
    using var subscription = hub.Subscribe(e =>
    {
        received.Enqueue(e);
        if (e.EventInstanceId == "root") { entered.Set(); Wait(resume); hub.Publish(Item("nested")); returned.Set(); }
        if (e.EventInstanceId == "nested") done.Set();
    });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub); resume.Set();
        Check(returned.Wait(TimeSpan.FromSeconds(2)), "callback blocked on its own full queue");
        Wait(done); AssertOrder(received, "nested");
    }
    finally { resume.Set(); }
});

Case("waiting ordinary producer stays ahead of reentrant publication", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    var received = new ConcurrentQueue<TvAirEventDto>();
    using var subscription = hub.Subscribe(e =>
    {
        received.Enqueue(e);
        if (e.EventInstanceId == "root") { entered.Set(); Wait(resume); hub.Publish(Item("nested")); }
        if (e.EventInstanceId == "nested") done.Set();
    });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub);
        var writers = new List<Task>();
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            writers.Add(Task.Run(() => hub.Publish(Item("waiting:" + index))));
            Check(SpinWait.SpinUntil(() => Pending(hub) == capacity + index + 1, 3000), "ordinary producer did not reach backpressure");
            Check(!writers[index].IsCompleted, "ordinary producer bypassed capacity");
        }
        resume.Set(); Wait(done); Check(Task.WaitAll(writers.ToArray(), 3000), "ordinary producers not released");
        AssertOrder(received, "waiting:0", "waiting:1", "waiting:2", "nested");
    }
    finally { resume.Set(); }
});

Case("large callback burst spools instead of growing memory and deletes its file", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    var received = new ConcurrentQueue<TvAirEventDto>(); string? path = null;
    const int burst = 5000;
    using var subscription = hub.Subscribe(e =>
    {
        received.Enqueue(e);
        if (e.EventInstanceId == "root")
        {
            entered.Set(); Wait(resume);
            for (var i = 0; i < burst; i++) hub.Publish(Item("nested:" + i));
            var outbox = typeof(PluginTypedEventHub).GetField("outbox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hub)!;
            var fields = outbox.GetType();
            var memory = fields.GetField("memory", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(outbox)!;
            Check((int)memory.GetType().GetProperty("Count")!.GetValue(memory)! <= capacity, "unbounded memory queue");
            var file = (FileStream)fields.GetField("spill", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(outbox)!;
            path = file.Name; Check(File.Exists(path), "overflow not spooled");
        }
        if (e.EventInstanceId == "nested:" + (burst - 1)) done.Set();
    });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub); resume.Set(); Wait(done);
        AssertOrder(received, Enumerable.Range(0, burst).Select(i => "nested:" + i).ToArray());
        Check(path is not null && !File.Exists(path), "drained spool file remains");
    }
    finally { resume.Set(); }
});

Case("callback and completion listeners remain serial including completion reentry", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    var trace = new ConcurrentQueue<string>(); var received = new ConcurrentQueue<TvAirEventDto>();
    using var first = hub.Subscribe(e =>
    {
        received.Enqueue(e);
        if (e.EventInstanceId == "root") { trace.Enqueue("first-enter"); entered.Set(); Wait(resume); trace.Enqueue("first-return"); }
    });
    using var second = hub.Subscribe(e => { if (e.EventInstanceId == "root") trace.Enqueue("second-return"); });
    Completion(hub, e =>
    {
        if (e.EventInstanceId == "root") { trace.Enqueue("complete"); hub.Publish(Item("completion-child")); }
        if (e.EventInstanceId == "completion-child") done.Set();
    });
    try
    {
        hub.Publish(new TvAirEventDto { EventInstanceId = "root", EventType = TvAirEventType.RecordingResultFinalized, ReservationId = "R1" });
        Wait(entered); Full(hub); Check(!trace.Contains("complete"), "power handoff completion before callback returned");
        resume.Set(); Wait(done);
        Check(trace.SequenceEqual(new[] { "first-enter", "first-return", "second-return", "complete" }), "callback/completion order changed");
        AssertOrder(received, "completion-child");
    }
    finally { resume.Set(); }
});

Case("nested operation scopes and throwing subscribers preserve subsequent delivery", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    var received = new ConcurrentQueue<TvAirEventDto>();
    using var bad = hub.Subscribe(e => { if (e.EventInstanceId == "root") throw new InvalidOperationException("synthetic subscriber failure"); });
    using var good = hub.Subscribe(e =>
    {
        received.Enqueue(e);
        if (e.EventInstanceId == "root")
        {
            entered.Set(); Wait(resume);
            using var outer = hub.BeginOutboxScope(out var commit);
            hub.Publish(Item("scope:1"));
            using (hub.BeginOutboxScope(out var inner)) { hub.Publish(Item("scope:2")); inner(); }
            hub.Publish(Item("scope:3")); commit();
        }
        if (e.EventInstanceId == "scope:3") done.Set();
    });
    try { hub.Publish(Item("root")); Wait(entered); Full(hub); resume.Set(); Wait(done); AssertOrder(received, "scope:1", "scope:2", "scope:3"); }
    finally { resume.Set(); }
});

Case("callback disposal does not wait for its own dispatch task and drains accepted events", () =>
{
    var hub = Hub(); var dispatch = DispatchTask(hub);
    using var done = new ManualResetEventSlim(); var ids = new ConcurrentQueue<string>();
    using var sub = hub.Subscribe(e =>
    {
        ids.Enqueue(e.EventInstanceId);
        if (e.EventInstanceId == "root") { hub.Publish(Item("accepted")); hub.Dispose(); done.Set(); }
    });
    hub.Publish(Item("root")); Wait(done); Check(dispatch.Wait(3000), "self-disposal did not finish");
    Check(ids.SequenceEqual(new[] { "root", "accepted" }), "accepted event lost at self-disposal"); hub.Dispose();
});

Case("shutdown releases blocked publishers and drains accepted queue", () =>
{
    var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
    var count = 0;
    using var sub = hub.Subscribe(e => { Interlocked.Increment(ref count); if (e.EventInstanceId == "root") { entered.Set(); Wait(resume); } });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub);
        var writer = Task.Run(() => hub.Publish(Item("unaccepted")));
        Check(SpinWait.SpinUntil(() => Pending(hub) == capacity + 1, 3000), "producer not blocked");
        var closing = Task.Run(hub.Dispose);
        Check(writer.Wait(3000), "shutdown left producer blocked"); resume.Set();
        Check(closing.Wait(5000), "shutdown did not drain"); Check(count == capacity + 1, "accepted events lost or rejected event delivered");
        Check(Pending(hub) == 0, "pending count not cleared");
    }
    finally { resume.Set(); hub.Dispose(); }
});

Case("spooled payload retains every envelope and nested payload field", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    var stamp = DateTimeOffset.Now;
    var rich = new TvAirEventDto
    {
        EventInstanceId = "rich", EventType = TvAirEventType.RecordingResultFinalized,
        OccurredAt = stamp, Timestamp = stamp, EntityId = "recording:R1", EntityVersion = 3, DataRevision = 3,
        OperationId = "op", SourceOwnerId = "owner", ChangeKind = "finalized", ReservationId = "R1", ServiceName = "局", ProgramTitle = "番組",
        Reservation = new() { ReservationId = "R1", NetworkId = 4, TransportStreamId = 200, ServiceId = 201, ServiceName = "局", EventTitle = "番組" },
        BeforeReservation = new() { ReservationId = "R1", ServiceName = "前" }, AfterReservation = new() { ReservationId = "R1", ServiceName = "後" },
        RecordingResult = new() { ReservationId = "R1", ServiceName = "局", FilePath = "C:/synthetic.ts", ResultFinalized = true },
        ChangedFields = new[] { "Status", "日本語" }, Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["key"] = "値" },
        ExternalJob = new(), RuntimeWindowLifecycle = new(), ViewerReservation = new()
    };
    TvAirEventDto? actual = null;
    using var sub = hub.Subscribe(e =>
    {
        if (e.EventInstanceId == "root") { entered.Set(); Wait(resume); hub.Publish(rich); }
        if (e.EventInstanceId == "rich") { actual = e; done.Set(); }
    });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub); resume.Set(); Wait(done);
        var expected = JsonSerializer.SerializeToNode(rich)!.AsObject(); var delivered = JsonSerializer.SerializeToNode(actual)!.AsObject();
        expected.Remove("Sequence"); delivered.Remove("Sequence");
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, delivered), "payload changed after spooling");
        Check(actual!.Details.ContainsKey("KEY"), "Details lookup comparer lost during spooling");
    }
    finally { resume.Set(); }
});

Case("spool creation failure is visible and does not damage accepted notifications", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    var received = new ConcurrentQueue<TvAirEventDto>(); var failed = false;
    var blockedPath = Path.Combine(root, "not-a-directory"); File.WriteAllText(blockedPath, "synthetic file");
    using var sub = hub.Subscribe(e =>
    {
        received.Enqueue(e);
        if (e.EventInstanceId == "root")
        {
            entered.Set(); Wait(resume);
            var oldTemp = Environment.GetEnvironmentVariable("TEMP"); var oldTmp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                Environment.SetEnvironmentVariable("TEMP", blockedPath); Environment.SetEnvironmentVariable("TMP", blockedPath);
                try { hub.Publish(Item("cannot-persist")); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
            }
            finally { Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }
            hub.Publish(Item("recovered"));
        }
        if (e.EventInstanceId == "recovered") done.Set();
    });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub); resume.Set(); Wait(done);
        Check(failed, "spool write failure was silently accepted"); AssertOrder(received, "recovered");
        Check(Pending(hub) == 0, "failed publication leaked pending count");
    }
    finally { resume.Set(); }
});

Case("ordinal case-distinct Details keys survive disk overflow", () =>
{
    using var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
    TvAirEventDto? result = null;
    using var sub = hub.Subscribe(e =>
    {
        if (e.EventInstanceId == "root")
        {
            entered.Set(); Wait(resume);
            hub.Publish(new TvAirEventDto { EventInstanceId = "ordinal", Details = new Dictionary<string, string> { ["key"] = "lower", ["KEY"] = "upper" } });
        }
        if (e.EventInstanceId == "ordinal") { result = e; done.Set(); }
    });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub); resume.Set(); Wait(done);
        Check(result!.Details.Count == 2 && result.Details["key"] == "lower" && result.Details["KEY"] == "upper" && !result.Details.ContainsKey("Key"), "case-sensitive map merged or changed comparer");
    }
    finally { resume.Set(); }
});

Case("shutdown timeout still drains accepted memory and disk notifications", () =>
{
    var hub = Hub();
    using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var spooled = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
    var received = new ConcurrentQueue<TvAirEventDto>();
    var cancellation = (CancellationTokenSource)typeof(PluginTypedEventHub).GetField("disposeCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hub)!;
    using var sub = hub.Subscribe(e =>
    {
        received.Enqueue(e);
        if (e.EventInstanceId == "root") { entered.Set(); Wait(resume); hub.Publish(Item("nested")); spooled.Set(); Wait(finish); }
    });
    try
    {
        hub.Publish(Item("root")); Wait(entered); Full(hub); resume.Set(); Wait(spooled);
        var closing = Task.Run(hub.Dispose);
        Check(SpinWait.SpinUntil(() => cancellation.IsCancellationRequested, 7000), "shutdown cancellation boundary not reached");
        finish.Set(); Check(closing.Wait(5000), "shutdown did not complete after callback returned");
        AssertOrder(received, "nested"); Check(Pending(hub) == 0, "timeout discarded accepted notifications");
    }
    finally { resume.Set(); finish.Set(); hub.Dispose(); }
});

Console.WriteLine($"{passed}/{passed} typed event cases passed; no RF or native power calls.");
