using System.Threading.Channels;
using TvAIr.Core;
using TvAIr.Channel;
using TvAIr.Schedule;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>開発者ログから独立した、TvAIr確定事実の型付きイベント正本。</summary>
public sealed class PluginTypedEventHub : IDisposable
{
    private readonly object gate = new();
    private readonly List<Subscriber> subscribers = new();
    private readonly LogRepository log;
    private readonly ReservationMutationJournal reservationMutations;
    private readonly ChannelFileLoader channelLoader;
    private const int OutboxCapacity = 4096;
    private readonly Channel<TvAirEventDto> outbox = global::System.Threading.Channels.Channel.CreateBounded<TvAirEventDto>(new BoundedChannelOptions(OutboxCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource disposeCts = new();
    private readonly Task dispatchTask;
    private readonly AsyncLocal<OutboxScope?> ambientScope = new();
    [ThreadStatic]
    private static TvAirEventDto? currentDispatchEvent;
    private long sequence;
    private int pendingEventCount;
    private int maxPendingEventCount;
    private int disposed;

    public PluginTypedEventHub(LogRepository log, ReservationMutationJournal reservationMutations, ChannelFileLoader channelLoader)
    {
        this.log = log;
        this.reservationMutations = reservationMutations;
        this.channelLoader = channelLoader;
        reservationMutations.Recorded += PublishReservationMutation;
        dispatchTask = Task.Run(() => DispatchLoopAsync(disposeCts.Token));
    }

    private void PublishReservationMutation(ReservationMutationResult mutation)
    {
        if (Volatile.Read(ref disposed) != 0) return;

        // SYSTEM_EPG_TYPED_EVENT_BOUNDARY_INVARIANT:
        // source=Epg の予約行は、定時EPG・録画前EPG確認をScheduler/Storeで管理するための内部Intentであり、
        // ユーザー録画予約ではない。内部行の Completed/Failed を RecordingCompleted/RecordingFailed へ
        // 投影すると、利用中のプラグインへ「録画が完了/失敗した」という偽の意味を配送する。
        // domain journal はWake/内部projectionの確定差分として保持したまま、外部Typed Event境界では遮断する。
        // EPGの公開終端通知は EpgScheduler が確定後に発行する EpgCompleted/EpgFailed を唯一の正規経路とする。
        var reservation = mutation.After ?? mutation.Before;
        if (ReservationOriginClassifier.Classify(reservation).Origin == ReservationOriginKind.SystemEpg)
        {
            log.Add("PLUGIN_TYPED_EVENT_SUPPRESS", "SystemEpg",
                $"result=SUPPRESSED mutationKind={mutation.Kind} reservation=R{mutation.ReservationId} source=Epg reason=internal_system_epg_reservation_not_recording_event publicEpgRoute=EpgCompleted/EpgFailed rule=typed_event_contract");
            return;
        }

        var type = mutation.Kind switch
        {
            ReservationMutationKind.Added => TvAirEventType.ReservationAdded,
            ReservationMutationKind.Removed => TvAirEventType.ReservationRemoved,
            ReservationMutationKind.Enabled => TvAirEventType.ReservationEnabled,
            ReservationMutationKind.Disabled => TvAirEventType.ReservationDisabled,
            ReservationMutationKind.ConflictChanged => TvAirEventType.ReservationConflictChanged,
            ReservationMutationKind.RecordingStarted => TvAirEventType.RecordingStarted,
            ReservationMutationKind.RecordingCompleted => TvAirEventType.RecordingCompleted,
            ReservationMutationKind.RecordingFailed => TvAirEventType.RecordingFailed,
            _ => TvAirEventType.ReservationUpdated
        };
        PublishReservation(type, mutation.Before, mutation.After, mutation.Metadata, mutation.ChangedFields.ToArray());
    }

    public IDisposable BeginOutboxScope(out Action commit)
    {
        ThrowIfDisposed();
        var parent = ambientScope.Value;
        var scope = new OutboxScope(this, parent);
        ambientScope.Value = scope;
        commit = scope.Commit;
        return scope;
    }

    public IDisposable Subscribe(string pluginId, TvAirEventType? eventType, Action<TvAirEventDto> handler)
    {
        ThrowIfDisposed();
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var subscriber = new Subscriber(normalizedPluginId, eventType, handler);
        int count;
        lock (gate)
        {
            subscribers.Add(subscriber);
            count = subscribers.Count(x => x.EventType == eventType);
        }
        log.Add("PLUGIN_TYPED_EVENT_SUBSCRIBE", normalizedPluginId, $"eventType={(eventType?.ToString() ?? "All")} result=OK subscriberCount={count} rule=typed_event_contract");
        return new Subscription(() =>
        {
            lock (gate) subscribers.Remove(subscriber);
        });
    }

    public IDisposable Subscribe(Action<TvAirEventDto> handler) => Subscribe("host", null, handler);

    internal TvAirEventDto? CurrentDispatchEvent => currentDispatchEvent;

    public void Publish(TvAirEventDto source)
    {
        ThrowIfDisposed();
        var scope = ambientScope.Value;
        if (scope is not null)
        {
            scope.Events.Add(source);
            return;
        }

        Enqueue(source);
    }

    // TYPED_EVENT_OUTBOX_BACKPRESSURE_INVARIANT:
    // 型付きイベントは順序と欠落なしを契約とするため、容量超過時にDropOldest/DropNewestへ
    // 逃がさない。固定wait/sleep/delayを追加せず、4096件の有界outboxが空くまでWriterの
    // backpressureで生産側を待たせる。TvAIrを使うほど未配送イベントが無制限にメモリへ
    // 蓄積するCreateUnboundedへ戻さない。配送正本、イベント順序、subscriber契約は変更しない。
    private void Enqueue(TvAirEventDto source)
    {
        var pending = Interlocked.Increment(ref pendingEventCount);
        UpdateMaxQueueDepth(pending);
        try
        {
            outbox.Writer.WriteAsync(source, disposeCts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            Interlocked.Decrement(ref pendingEventCount);
            log.Add("PLUGIN_TYPED_EVENT_OUTBOX_REJECTED", source.EventType.ToString(), "reason=disposing rule=typed_event_outbox");
        }
        catch (ChannelClosedException)
        {
            Interlocked.Decrement(ref pendingEventCount);
            log.Add("PLUGIN_TYPED_EVENT_OUTBOX_REJECTED", source.EventType.ToString(), "reason=writer_closed rule=typed_event_outbox");
        }
    }

    private void UpdateMaxQueueDepth(int depth)
    {
        while (true)
        {
            var current = Volatile.Read(ref maxPendingEventCount);
            if (depth <= current || Interlocked.CompareExchange(ref maxPendingEventCount, depth, current) == current)
                return;
        }
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var source in outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref pendingEventCount);
                Dispatch(source);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Dispatch(TvAirEventDto source)
    {
        var occurredAt = source.OccurredAt == default ? DateTimeOffset.Now : source.OccurredAt;
        var entityId = source.EntityId ?? source.ReservationId;
        var dto = new TvAirEventDto
        {
            EventInstanceId = string.IsNullOrWhiteSpace(source.EventInstanceId) ? Guid.NewGuid().ToString("N") : source.EventInstanceId,
            OccurredAt = occurredAt, Timestamp = occurredAt, Sequence = Interlocked.Increment(ref sequence),
            EntityId = entityId, EntityVersion = ResolveEntityVersion(source, entityId),
            OperationId = source.OperationId, SourceOwnerId = source.SourceOwnerId, DataRevision = source.DataRevision, ChangeKind = source.ChangeKind,
            EventType = source.EventType, ReservationId = source.ReservationId, ServiceName = ResolveEventServiceName(source), ProgramTitle = source.ProgramTitle,
            Reservation = NormalizeSnapshot(source.Reservation), BeforeReservation = NormalizeSnapshot(source.BeforeReservation), AfterReservation = NormalizeSnapshot(source.AfterReservation),
            ChangedFields = source.ChangedFields, RecordingResult = NormalizeRecordingResult(source.RecordingResult), RuntimeWindowLifecycle = source.RuntimeWindowLifecycle, Details = source.Details
        };
        Subscriber[] targets;
        lock (gate)
        {
            var candidates = subscribers.Where(x => x.EventType is null || x.EventType == dto.EventType);
            if (dto.EventType == TvAirEventType.RuntimeWindowLifecycleChanged
                && !string.IsNullOrWhiteSpace(dto.RuntimeWindowLifecycle?.PluginId))
            {
                var ownerPluginId = PluginIdentity.Normalize(dto.RuntimeWindowLifecycle.PluginId);
                candidates = candidates.Where(x => string.Equals(x.PluginId, ownerPluginId, StringComparison.OrdinalIgnoreCase));
            }
            targets = candidates.ToArray();
        }
        var delivered = 0; var failed = 0;
        foreach (var target in targets)
        {
            var previousDispatchEvent = currentDispatchEvent;
            currentDispatchEvent = dto;
            try
            {
                target.Handler(dto);
                delivered++;
            }
            catch (Exception ex)
            {
                failed++;
                log.Add("PLUGIN_TYPED_EVENT_DISPATCH_FAILED", target.PluginId, $"eventType={dto.EventType} eventInstanceId={dto.EventInstanceId} entityId={dto.EntityId ?? "-"} exceptionType={ex.GetType().Name} message={Safe(ex.Message)} rule=typed_event_outbox");
            }
            finally
            {
                currentDispatchEvent = previousDispatchEvent;
            }
        }
        log.Add("PLUGIN_TYPED_EVENT_DISPATCH", dto.EventType.ToString(), $"eventInstanceId={dto.EventInstanceId} entityId={dto.EntityId ?? "-"} occurredAt={dto.OccurredAt:O} sequence={dto.Sequence} subscriberCount={targets.Length} deliveredCount={delivered} failedCount={failed} pluginIds={Safe(string.Join(",", targets.Select(x => x.PluginId).Distinct(StringComparer.OrdinalIgnoreCase)))} payloadReservation={dto.Reservation is not null} payloadBefore={dto.BeforeReservation is not null} payloadAfter={dto.AfterReservation is not null} rule=typed_event_outbox");
    }

    private string ResolveCurrentServiceName(ushort networkId, ushort transportStreamId, ushort serviceId, string? fallback)
    {
        try
        {
            return ServiceIdentityContract.ResolveCurrentServiceName(
                channelLoader.Load().Targets,
                networkId,
                transportStreamId,
                serviceId,
                fallback);
        }
        catch
        {
            return fallback?.Trim() ?? string.Empty;
        }
    }

    private string? ResolveEventServiceName(TvAirEventDto source)
    {
        var snapshot = source.AfterReservation ?? source.Reservation ?? source.BeforeReservation;
        if (snapshot is not null)
            return ResolveCurrentServiceName(snapshot.NetworkId, snapshot.TransportStreamId, snapshot.ServiceId, source.ServiceName ?? snapshot.ServiceName);
        if (source.RecordingResult is { } result)
            return ResolveCurrentServiceName(result.NetworkId, result.TransportStreamId, result.ServiceId, source.ServiceName ?? result.ServiceName);
        return source.ServiceName;
    }

    private TvAirReservationSnapshotDto? NormalizeSnapshot(TvAirReservationSnapshotDto? source)
    {
        if (source is null) return null;
        return new TvAirReservationSnapshotDto
        {
            ReservationId = source.ReservationId,
            NetworkId = source.NetworkId,
            TransportStreamId = source.TransportStreamId,
            ServiceId = source.ServiceId,
            EventId = source.EventId,
            ServiceName = ResolveCurrentServiceName(source.NetworkId, source.TransportStreamId, source.ServiceId, source.ServiceName),
            EventTitle = source.EventTitle,
            ScheduledStartTime = source.ScheduledStartTime,
            StartTime = source.StartTime,
            EndTime = source.EndTime,
            Enabled = source.Enabled,
            HasConflict = source.HasConflict,
            ReservationSource = source.ReservationSource,
            Status = source.Status
        };
    }

    private TvAirRecordingResultDto? NormalizeRecordingResult(TvAirRecordingResultDto? source)
    {
        if (source is null) return null;
        return new TvAirRecordingResultDto
        {
            ReservationId = source.ReservationId,
            RecordingId = source.RecordingId,
            ServiceName = ResolveCurrentServiceName(source.NetworkId, source.TransportStreamId, source.ServiceId, source.ServiceName),
            EventTitle = source.EventTitle,
            NetworkId = source.NetworkId,
            TransportStreamId = source.TransportStreamId,
            ServiceId = source.ServiceId,
            EventId = source.EventId,
            ScheduledStartTime = source.ScheduledStartTime,
            Genre = source.Genre,
            GenreCodes = source.GenreCodes,
            ActualStartTime = source.ActualStartTime,
            ActualEndTime = source.ActualEndTime,
            Result = source.Result,
            EndReason = source.EndReason,
            FilePath = source.FilePath,
            FileCreated = source.FileCreated,
            Drop = source.Drop,
            Error = source.Error,
            Scramble = source.Scramble,
            QualityDataAvailable = source.QualityDataAvailable,
            QualityCompleteness = source.QualityCompleteness,
            QualitySource = source.QualitySource,
            ResourceReleaseState = source.ResourceReleaseState,
            ResultFinalized = source.ResultFinalized
        };
    }

    private void PublishReservation(
        TvAirEventType type,
        Reservation? before,
        Reservation? after,
        IReadOnlyDictionary<string, string?>? metadata = null,
        params string[] changedFields)
    {
        var current = after ?? before;
        if (current is null) return;

        // RESERVATION_TYPED_EVENT_PROJECTION_INVARIANT:
        // Reservation正本の生識別子とMutation確定metadataを、全Reservation系Typed Eventへ同じ経路で投影する。
        // RecordingFailedだけで後段補完したり、Pluginごとの専用payloadを作らない。
        var details = metadata is null || metadata.Count == 0
            ? new Dictionary<string, string>()
            : metadata.Where(x => x.Value is not null)
                .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);

        Publish(new TvAirEventDto
        {
            EventType = type,
            EntityId = $"reservation:R{current.Id}",
            EntityVersion = current.DataVersion,
            DataRevision = current.DataVersion,
            ReservationId = $"R{current.Id}",
            ServiceName = current.ServiceName,
            ProgramTitle = current.Title,
            Reservation = ToSnapshot(current),
            BeforeReservation = before is null ? null : ToSnapshot(before),
            AfterReservation = after is null ? null : ToSnapshot(after),
            ChangedFields = changedFields,
            Details = details
        });
    }


    // TYPED_EVENT_ENTITY_VERSION_LIFETIME_INVARIANT:
    // EntityVersion はイベントHub内の永続辞書で採番しない。予約はDataVersion、外部EPGは
    // StoreRevision、ViewerはGenerationという各正本の世代をそのまま使用する。EPG実行や
    // 録画結果のようにEntityId自体が一回限りの事象は1とする。TvAIrを使うほど過去EntityIdが
    // メモリへ蓄積する実装へ戻さず、世代の意味を維持したまま正本から投影する。
    private static long ResolveEntityVersion(TvAirEventDto source, string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return 0;
        if (source.DataRevision is > 0) return source.DataRevision.Value;
        if (source.EntityVersion > 0) return source.EntityVersion;

        // 現行の一回限りEntity。EntityIdが毎回新規なので、保持して加算する意味がない。
        if (entityId.StartsWith("epg:", StringComparison.Ordinal)
            || entityId.StartsWith("recording:", StringComparison.Ordinal))
            return 1;

        // 正本世代が未設定の持続Entityを黙って採番しない。契約漏れを0で可視化する。
        return 0;
    }

    internal static TvAirReservationSnapshotDto ToSnapshot(Reservation r) => new()
    {
        ReservationId = $"R{r.Id}",
        NetworkId = r.NetworkId,
        TransportStreamId = r.TransportStreamId,
        ServiceId = r.ServiceId,
        EventId = r.EventId,
        ServiceName = r.ServiceName,
        EventTitle = r.Title,
        ScheduledStartTime = new DateTimeOffset(r.ScheduledStartTime ?? r.StartTime),
        StartTime = new DateTimeOffset(r.StartTime),
        EndTime = new DateTimeOffset(r.EndTime),
        Enabled = r.IsEnabled,
        HasConflict = r.IsConflicted,
        ReservationSource = r.Source.ToString(),
        Status = r.Status.ToString()
    };
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r',' ').Replace('\n',' ');
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        reservationMutations.Recorded -= PublishReservationMutation;
        log.Add("PLUGIN_TYPED_EVENT_OUTBOX", "Dispose",
            $"result=BEGIN pending={Volatile.Read(ref pendingEventCount)} maxPending={Volatile.Read(ref maxPendingEventCount)} capacity={OutboxCapacity} rule=typed_event_outbox");
        outbox.Writer.TryComplete();
        try
        {
            if (!dispatchTask.Wait(TimeSpan.FromSeconds(5)))
            {
                disposeCts.Cancel();
                dispatchTask.GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            disposeCts.Dispose();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private sealed class OutboxScope : IDisposable
    {
        private readonly PluginTypedEventHub owner;
        private readonly OutboxScope? parent;
        private bool committed;
        private bool disposed;

        public OutboxScope(PluginTypedEventHub owner, OutboxScope? parent)
        {
            this.owner = owner;
            this.parent = parent;
        }

        public List<TvAirEventDto> Events { get; } = new();

        public void Commit()
        {
            if (disposed || committed) return;
            committed = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.ambientScope.Value = parent;
            if (Events.Count == 0) return;

            owner.log.Add("PLUGIN_TYPED_EVENT_OUTBOX_SCOPE", "Operation",
                $"result={(committed ? "COMMITTED" : "MUTATION_COMMITTED_OPERATION_INCOMPLETE")} eventCount={Events.Count} rule=typed_event_outbox");

            if (parent is not null)
                parent.Events.AddRange(Events);
            else
                foreach (var item in Events) owner.Enqueue(item);
        }
    }

    private sealed record Subscriber(string PluginId, TvAirEventType? EventType, Action<TvAirEventDto> Handler);
    private sealed class Subscription(Action dispose) : IDisposable { private Action? action = dispose; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }
}
