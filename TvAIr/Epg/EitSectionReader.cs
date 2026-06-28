namespace TvAIr.Epg;

internal sealed class EitSectionReader
{
    private readonly Dictionary<(ushort Sid, byte TableId), SectionTracker> sections = new();
    private readonly List<EpgTitleDecode> titleDecodes = new();
    private readonly List<EpgEventObservation> eventObservations = new();
    private readonly List<RawEitSectionSnapshot> rawSections = new();
    // Program identity must include start/duration. event_id alone can be reused or appear in
    // multiple schedule-table fragments; merging only by event_id causes title/body from
    // different program instances to overwrite each other.
    private readonly Dictionary<(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, DateTime Start, int DurationSeconds), MutableEvent> events = new();
    private int eitSectionCount;
    private int shortDescriptorCount;
    private int decodeAttemptCount;
    private int extendedWithoutShortCount;
    private int descriptorRecoveryCount;
    private int eventObservationSequence;
    private int rawSectionShortResolverCandidates;
    private int rawSectionShortResolverMerged;
    private int rawSectionShortResolverUnresolved;
    private bool rawSectionShortResolverApplied;
    private const int MaxDecodes = 160;

    public int EitSectionCount => eitSectionCount;
    public int ShortEventDescriptorCount => shortDescriptorCount;
    public int DecodeAttemptCount => decodeAttemptCount;
    public int ExtendedWithoutShortCount => extendedWithoutShortCount;
    public int DescriptorRecoveryCount => descriptorRecoveryCount;
    public int RawSectionShortResolverCandidates => rawSectionShortResolverCandidates;
    public int RawSectionShortResolverMerged => rawSectionShortResolverMerged;
    public int RawSectionShortResolverUnresolved => rawSectionShortResolverUnresolved;
    public IReadOnlyList<EpgTitleDecode> TitleDecodes => titleDecodes;
    public IReadOnlyList<EpgEventObservation> EventObservations => eventObservations;

    public bool TryRead(ReadOnlySpan<byte> section)
    {
        if (section.Length < 18) return false;
        var tableId = section[0];
        if (tableId < 0x4E || tableId > 0x6F) return false;

        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var sectionEnd = Math.Min(section.Length, 3 + sectionLength);
        var dataEnd = sectionEnd - 4;
        if (dataEnd < 14) return false;

        eitSectionCount++;
        var serviceId = U16(section, 3);
        var sectionNumber = section[6];
        var lastSectionNumber = section[7];
        var versionNumber = (byte)((section[5] >> 1) & 0x1F);
        var transportStreamId = U16(section, 8);
        var networkId = U16(section, 10);
        var segmentLastSectionNumber = section[12];

        rawSections.Add(new RawEitSectionSnapshot(networkId, transportStreamId, serviceId, tableId, sectionNumber, lastSectionNumber, versionNumber, section.Slice(0, sectionEnd).ToArray()));

        TrackSection(serviceId, tableId, sectionNumber, lastSectionNumber, segmentLastSectionNumber);

        var pos = 14;
        while (pos + 12 <= dataEnd)
        {
            var eventId = U16(section, pos);
            var start = DecodeJst(section.Slice(pos + 2, 5));
            var duration = DecodeDuration(section.Slice(pos + 7, 3));
            var descLoopLength = ((section[pos + 10] & 0x0F) << 8) | section[pos + 11];
            var descStart = pos + 12;
            var descEnd = descStart + descLoopLength;
            var eventOffset = pos;
            var nextEventOffset = descEnd;
            var previousTailStart = Math.Max(14, eventOffset - 24);
            var previousTailHex = eventOffset > previousTailStart ? Hex(section.Slice(previousTailStart, eventOffset - previousTailStart)) : string.Empty;
            var eventHeaderHex = Hex(section.Slice(eventOffset, Math.Min(12, Math.Max(0, dataEnd - eventOffset))));
            if (descEnd > dataEnd)
            {
                AddNoDescriptorDecode(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, lastSectionNumber, descLoopLength, descStart, "descriptor_loop_range_invalid");
                EnsureEvent(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, versionNumber, start, duration, string.Empty, string.Empty, string.Empty, string.Empty, "none", "not_attempted", "descriptor_loop_range_invalid", string.Empty, string.Empty, string.Empty, string.Empty);
                break;
            }

            var descriptorPos = descStart;
            var foundShort = false;
            var genreCodes = string.Empty;
            var descriptorLoopHex = Hex(section.Slice(descStart, descLoopLength));
            var descriptorLoopHeadHex = Hex(section.Slice(descStart, Math.Min(descLoopLength, 96)));
            var descriptorLoopTailStart = descStart + Math.Max(0, descLoopLength - 96);
            var descriptorLoopTailHex = descLoopLength > 0 ? Hex(section.Slice(descriptorLoopTailStart, Math.Min(96, descLoopLength))) : string.Empty;
            var nextEventHeaderHex = descEnd < dataEnd ? Hex(section.Slice(descEnd, Math.Min(16, dataEnd - descEnd))) : string.Empty;
            var windowStart = Math.Max(14, eventOffset - 32);
            var windowEnd = Math.Min(dataEnd, Math.Max(descEnd, eventOffset) + 32);
            var eventRawWindowHex = windowEnd > windowStart ? Hex(section.Slice(windowStart, windowEnd - windowStart)) : string.Empty;
            var rawShortEventDescriptorHex = string.Empty;
            var rawExtendedEventDescriptorHex = string.Empty;
            var rawContentDescriptorHex = string.Empty;
            while (descriptorPos + 2 <= descEnd)
            {
                var tag = section[descriptorPos];
                var len = section[descriptorPos + 1];
                var next = descriptorPos + 2 + len;
                if (next > descEnd)
                {
                    AddNoDescriptorDecode(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, lastSectionNumber, descLoopLength, descriptorPos, "descriptor_range_invalid");
                    RecoverDescriptorTail(section, networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, lastSectionNumber, versionNumber, start, duration, descLoopLength, descriptorPos, descEnd, descriptorLoopHex, ref foundShort, ref rawShortEventDescriptorHex, ref rawExtendedEventDescriptorHex, rawContentDescriptorHex, genreCodes);
                    descriptorPos = descEnd;
                    break;
                }

                if (tag == 0x4D)
                {
                    foundShort = true;
                    shortDescriptorCount++;
                    rawShortEventDescriptorHex = MergeRawHex(rawShortEventDescriptorHex, Hex(section.Slice(descriptorPos, 2 + len)));
                    var decodedShort = AddShortDecode(section, networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, lastSectionNumber, descLoopLength, descriptorPos, len);
                    EnsureEvent(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, versionNumber, start, duration, decodedShort.Title, decodedShort.Text, string.Empty, genreCodes, decodedShort.DecodeRoute, decodedShort.DecodeStatus, decodedShort.BoundaryStatus, descriptorLoopHex, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex, rawContentDescriptorHex);
                }
                else if (tag == 0x4E)
                {
                    rawExtendedEventDescriptorHex = MergeRawHex(rawExtendedEventDescriptorHex, Hex(section.Slice(descriptorPos, 2 + len)));
                    EnsureEvent(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, versionNumber, start, duration, string.Empty, string.Empty, string.Empty, genreCodes, "none", "not_attempted", "extended_descriptor_raw_only", descriptorLoopHex, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex, rawContentDescriptorHex);
                }
                else if (tag == 0x54)
                {
                    rawContentDescriptorHex = MergeRawHex(rawContentDescriptorHex, Hex(section.Slice(descriptorPos, 2 + len)));
                    genreCodes = MergeGenreCodes(genreCodes, ReadContentDescriptorGenreCodes(section, descriptorPos, len));
                    if (!string.IsNullOrEmpty(genreCodes))
                    {
                        EnsureEvent(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, versionNumber, start, duration, string.Empty, string.Empty, string.Empty, genreCodes, "none", "not_attempted", "content_descriptor_only", descriptorLoopHex, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex, rawContentDescriptorHex);
                    }
                }
                descriptorPos = next;
            }

            eventObservations.Add(new EpgEventObservation(
                networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, lastSectionNumber, versionNumber,
                sectionLength, dataEnd, eventOffset, descLoopLength, descStart, descEnd, nextEventOffset,
                start, duration, descriptorLoopHex, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex, rawContentDescriptorHex,
                eventHeaderHex, previousTailHex, descriptorLoopHeadHex, descriptorLoopTailHex, nextEventHeaderHex, eventRawWindowHex, ++eventObservationSequence));

            if (!foundShort)
            {
                if (!string.IsNullOrEmpty(rawExtendedEventDescriptorHex)) extendedWithoutShortCount++;
                AddNoDescriptorDecode(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, lastSectionNumber, descLoopLength, descStart, "no_short_event_descriptor");
                EnsureEvent(networkId, transportStreamId, serviceId, eventId, tableId, sectionNumber, versionNumber, start, duration, string.Empty, string.Empty, string.Empty, genreCodes, "none", "not_attempted", "no_short_event_descriptor", descriptorLoopHex, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex, rawContentDescriptorHex);
            }

            pos = descEnd;
        }

        return true;
    }

    public IReadOnlyList<EpgEventObservation> BuildEventObservations()
        => eventObservations
            .Where(e => e.Start != DateTime.MinValue && e.DurationSeconds > 0)
            .OrderBy(e => e.ServiceId)
            .ThenBy(e => e.Start)
            .ThenBy(e => e.TableId)
            .ThenBy(e => e.SectionNumber)
            .ToArray();


    public IReadOnlyList<EpgEventAccumulatorAudit> BuildAccumulatorAudits()
    {
        ResolveResidualShortDescriptorsFromRawSections();

        static bool IsScheduleTitleCarrierTable(byte tableId) => tableId >= 0x50 && tableId <= 0x57;
        static bool IsScheduleBodyTable(byte tableId) => tableId >= 0x58 && tableId <= 0x5F;
        static string Tables(HashSet<byte> tables) => tables.Count == 0
            ? "-"
            : string.Join('.', tables.OrderBy(x => x).Select(x => $"0x{x:X2}"));
        static int RawBytes(string raw) => string.IsNullOrWhiteSpace(raw)
            ? 0
            : raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => NormalizeRawHexToken(x).Length / 2)
                .Sum();

        return events.Values
            .Where(e => e.Start != DateTime.MinValue && e.DurationSeconds > 0)
            .OrderBy(e => e.ServiceId)
            .ThenBy(e => e.Start)
            .ThenBy(e => e.EventId)
            .Select(e =>
            {
                var hasScheduleTitleCarrier = e.ShortSourceTables.Any(IsScheduleTitleCarrierTable);
                var hasScheduleBodyCarrier = e.ExtendedSourceTables.Any(IsScheduleBodyTable);
                var hasExpectedPair = e.ExtendedSourceTables
                    .Where(IsScheduleBodyTable)
                    .Any(bodyTable => e.ShortSourceTables.Contains((byte)(bodyTable - 0x08)));
                return new EpgEventAccumulatorAudit(
                    e.NetworkId,
                    e.TransportStreamId,
                    e.ServiceId,
                    e.EventId,
                    e.Start,
                    e.DurationSeconds,
                    e.BestTableId,
                    e.SectionNumber,
                    e.ObservationCount,
                    e.ShortDescriptorObservationCount,
                    e.ExtendedDescriptorObservationCount,
                    Tables(e.SourceTables),
                    Tables(e.ShortSourceTables),
                    Tables(e.ExtendedSourceTables),
                    !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex),
                    !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex),
                    hasScheduleTitleCarrier,
                    hasScheduleBodyCarrier,
                    hasExpectedPair,
                    RawBytes(e.RawShortEventDescriptorHex),
                    RawBytes(e.RawExtendedEventDescriptorHex));
            })
            .ToArray();
    }

    public IReadOnlyList<ParsedEpgEvent> BuildEvents()
    {
        ResolveResidualShortDescriptorsFromRawSections();

        return events.Values
            .Where(e => e.Start != DateTime.MinValue && e.DurationSeconds > 0)
            .OrderBy(e => e.ServiceId)
            .ThenBy(e => e.Start)
            .Select(e => new ParsedEpgEvent(
                e.NetworkId,
                e.TransportStreamId,
                e.ServiceId,
                e.EventId,
                e.BestTableId,
                e.Start,
                e.DurationSeconds,
                e.Title,
                e.Description,
                e.GenreCodes,
                e.TitleDecodeRoute,
                e.TitleDecodeStatus,
                e.BoundaryStatus,
                e.SectionNumber,
                e.VersionNumber,
                e.RawDescriptorLoopHex,
                e.RawShortEventDescriptorHex,
                e.RawExtendedEventDescriptorHex,
                e.RawContentDescriptorHex))
            .ToArray();
    }

    public IReadOnlyList<EpgSectionStatus> BuildSectionStatuses()
    {
        return sections
            .Select(kv =>
            {
                var st = kv.Value;
                var expected = st.LastSectionNumber + 1;
                var segExpected = st.SegmentExpected.Values.Sum(x => (int)x);
                var segSeen = st.SegmentSeen.Values.Sum(x => x.Count);
                var missing = st.SegmentExpected
                    .Where(x => !st.SegmentSeen.TryGetValue(x.Key, out var seen) || seen.Count < x.Value)
                    .Select(x => x.Key)
                    .OrderBy(x => x)
                    .Take(16)
                    .ToArray();
                return new EpgSectionStatus(kv.Key.Sid, kv.Key.TableId, st.LastSectionNumber, st.Seen.Count, expected, segSeen, segExpected, missing);
            })
            .OrderBy(x => x.ServiceId)
            .ThenBy(x => x.TableId)
            .ToArray();
    }

    private DecodedShortEvent AddShortDecode(ReadOnlySpan<byte> section, ushort nid, ushort tsid, ushort sid, ushort eid, byte tableId, byte sectionNumber, byte lastSectionNumber, int descriptorLoopLength, int descriptorOffset, int descriptorLength)
    {
        if (titleDecodes.Count >= MaxDecodes)
        {
            var skipped = ShortEventDescriptorReader.Read(section, descriptorOffset, descriptorLength);
            var skippedTitle = AribPsiSiDecoder.Decode(skipped.EventNameBytes);
            var skippedText = AribPsiSiDecoder.Decode(skipped.TextBytes);
            return new DecodedShortEvent(skippedTitle.Text, skippedText.Text, skippedTitle.Route, skippedTitle.Status, skipped.BoundaryStatus);
        }
        var parsed = ShortEventDescriptorReader.Read(section, descriptorOffset, descriptorLength);
        var title = AribPsiSiDecoder.Decode(parsed.EventNameBytes);
        var text = AribPsiSiDecoder.Decode(parsed.TextBytes);
        if (parsed.EventNameBytes.Length > 0) decodeAttemptCount++;

        var emptyReason = parsed.BoundaryStatus != "arib_fields_read" && parsed.BoundaryStatus != "descriptor_consumed_mismatch"
            ? parsed.BoundaryStatus
            : parsed.EventNameBytes.Length == 0
                ? "event_name_length_zero"
                : title.Text.Length == 0
                    ? title.Status
                    : "none";

        titleDecodes.Add(new EpgTitleDecode(
            nid, tsid, sid, eid, tableId, sectionNumber, lastSectionNumber,
            descriptorLoopLength, descriptorOffset, descriptorLength,
            parsed.BoundaryStatus,
            parsed.Iso639LanguageCode,
            parsed.EventNameLength,
            parsed.EventNameBytes.Length,
            ShortEventDescriptorReader.Hex(parsed.EventNameBytes, 48),
            title.Route,
            title.Status,
            title.Text,
            title.Text.Length,
            parsed.TextLength,
            parsed.TextBytes.Length,
            ShortEventDescriptorReader.Hex(parsed.TextBytes, 32),
            text.Text.Length > 80 ? text.Text[..80] : text.Text,
            emptyReason));
        return new DecodedShortEvent(title.Text, text.Text, title.Route, title.Status, parsed.BoundaryStatus);
    }


    private void RecoverDescriptorTail(
        ReadOnlySpan<byte> section,
        ushort nid,
        ushort tsid,
        ushort sid,
        ushort eid,
        byte tableId,
        byte sectionNumber,
        byte lastSectionNumber,
        byte versionNumber,
        DateTime start,
        int durationSeconds,
        int descriptorLoopLength,
        int invalidDescriptorOffset,
        int descEnd,
        string descriptorLoopHex,
        ref bool foundShort,
        ref string rawShortEventDescriptorHex,
        ref string rawExtendedEventDescriptorHex,
        string rawContentDescriptorHex,
        string genreCodes)
    {
        // v0.11.682: descriptor boundary recovery is deliberately conservative.
        // If one malformed descriptor length overruns the loop, keep the original
        // raw descriptor loop for audit, but scan the remaining bytes only for
        // fully self-contained ARIB 0x4D/0x4E descriptors.  Nothing is invented,
        // clamped, or written as a fabricated descriptor.
        var pos = Math.Max(invalidDescriptorOffset + 1, 0);
        while (pos + 2 <= descEnd)
        {
            var tag = section[pos];
            if (tag != 0x4D && tag != 0x4E)
            {
                pos++;
                continue;
            }

            var len = section[pos + 1];
            var next = pos + 2 + len;
            if (next > descEnd || len <= 0)
            {
                pos++;
                continue;
            }

            if (tag == 0x4D && IsPlausibleShortEventDescriptor(section, pos, len))
            {
                foundShort = true;
                shortDescriptorCount++;
                descriptorRecoveryCount++;
                rawShortEventDescriptorHex = MergeRawHex(rawShortEventDescriptorHex, Hex(section.Slice(pos, 2 + len)));
                var decodedShort = AddShortDecode(section, nid, tsid, sid, eid, tableId, sectionNumber, lastSectionNumber, descriptorLoopLength, pos, len);
                EnsureEvent(nid, tsid, sid, eid, tableId, sectionNumber, versionNumber, start, durationSeconds, decodedShort.Title, decodedShort.Text, string.Empty, genreCodes, decodedShort.DecodeRoute, decodedShort.DecodeStatus, "descriptor_tail_recovered_0x4D", descriptorLoopHex, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex, rawContentDescriptorHex);
                pos = next;
                continue;
            }

            if (tag == 0x4E && IsPlausibleExtendedEventDescriptor(section, pos, len))
            {
                descriptorRecoveryCount++;
                rawExtendedEventDescriptorHex = MergeRawHex(rawExtendedEventDescriptorHex, Hex(section.Slice(pos, 2 + len)));
                EnsureEvent(nid, tsid, sid, eid, tableId, sectionNumber, versionNumber, start, durationSeconds, string.Empty, string.Empty, string.Empty, genreCodes, "none", "not_attempted", "descriptor_tail_recovered_0x4E", descriptorLoopHex, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex, rawContentDescriptorHex);
                pos = next;
                continue;
            }

            pos++;
        }
    }

    private static bool IsPlausibleShortEventDescriptor(ReadOnlySpan<byte> section, int descriptorOffset, int descriptorLength)
    {
        var payloadOffset = descriptorOffset + 2;
        var descriptorEnd = payloadOffset + descriptorLength;
        if (descriptorLength < 5 || descriptorEnd > section.Length) return false;
        if (!IsIso639Like(section.Slice(payloadOffset, 3))) return false;
        var nameLenOffset = payloadOffset + 3;
        var nameLen = section[nameLenOffset];
        var textLenOffset = nameLenOffset + 1 + nameLen;
        if (textLenOffset >= descriptorEnd) return false;
        var textLen = section[textLenOffset];
        return textLenOffset + 1 + textLen <= descriptorEnd;
    }

    private static bool IsPlausibleExtendedEventDescriptor(ReadOnlySpan<byte> section, int descriptorOffset, int descriptorLength)
    {
        var payloadOffset = descriptorOffset + 2;
        var descriptorEnd = payloadOffset + descriptorLength;
        if (descriptorLength < 6 || descriptorEnd > section.Length) return false;
        if (!IsIso639Like(section.Slice(payloadOffset + 1, 3))) return false;
        var p = payloadOffset + 4;
        if (p >= descriptorEnd) return false;
        var itemsLength = section[p++];
        if (p + itemsLength > descriptorEnd) return false;
        return true;
    }

    private static bool IsIso639Like(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 3) return false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (!((b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z'))) return false;
        }
        return true;
    }

    private void AddNoDescriptorDecode(ushort nid, ushort tsid, ushort sid, ushort eid, byte tableId, byte sectionNumber, byte lastSectionNumber, int descriptorLoopLength, int descriptorOffset, string reason)
    {
        if (titleDecodes.Count >= MaxDecodes) return;
        titleDecodes.Add(new EpgTitleDecode(
            nid, tsid, sid, eid, tableId, sectionNumber, lastSectionNumber,
            descriptorLoopLength, descriptorOffset, 0,
            reason, string.Empty, 0, 0, string.Empty,
            "none", "not_attempted", string.Empty, 0,
            0, 0, string.Empty, string.Empty, reason));
    }

    private void TrackSection(ushort sid, byte tableId, byte sectionNumber, byte lastSectionNumber, byte segmentLastSectionNumber)
    {
        var key = (sid, tableId);
        if (!sections.TryGetValue(key, out var tracker))
        {
            tracker = new SectionTracker();
            sections[key] = tracker;
        }
        if (lastSectionNumber > tracker.LastSectionNumber) tracker.LastSectionNumber = lastSectionNumber;
        tracker.Seen.Add(sectionNumber);

        if (tableId >= 0x50 && tableId <= 0x6F)
        {
            var seg = (byte)(sectionNumber >> 3);
            var firstInSeg = (byte)(seg << 3);
            var lastInSeg = segmentLastSectionNumber;

            // ARIB STD-B10 EIT schedule は 8 section 単位の segment を持ち、
            // その segment の終端は EIT ヘッダの segment_last_section_number が正本。
            // last_section_number から 8 section 固定で推定すると、放送側が短い segment を出した時に
            // 欠落していない section を missing 扱いし、取得状態の判断を誤る。
            if (lastInSeg < firstInSeg || lastInSeg > lastSectionNumber)
            {
                lastInSeg = Math.Min(lastSectionNumber, (byte)((seg << 3) | 0x07));
            }

            var expected = (byte)Math.Max(1, lastInSeg - firstInSeg + 1);
            if (!tracker.SegmentExpected.TryGetValue(seg, out var cur) || expected > cur) tracker.SegmentExpected[seg] = expected;
            if (!tracker.SegmentSeen.TryGetValue(seg, out var seen)) tracker.SegmentSeen[seg] = seen = new HashSet<byte>();
            seen.Add(sectionNumber);
        }
    }

    private void EnsureEvent(ushort nid, ushort tsid, ushort sid, ushort eid, byte tableId, byte sectionNumber, byte versionNumber, DateTime start, int durationSeconds, string title, string description, string extendedDescription, string genreCodes, string decodeRoute, string decodeStatus, string boundaryStatus, string rawDescriptorLoopHex, string rawShortEventDescriptorHex, string rawExtendedEventDescriptorHex, string rawContentDescriptorHex)
    {
        if (start == DateTime.MinValue || durationSeconds <= 0) return;
        var key = (nid, tsid, sid, eid, start, durationSeconds);
        if (!events.TryGetValue(key, out var ev))
        {
            ev = new MutableEvent
            {
                NetworkId = nid,
                TransportStreamId = tsid,
                ServiceId = sid,
                EventId = eid,
                BestTableId = tableId,
                SectionNumber = sectionNumber,
                VersionNumber = versionNumber,
                Start = start,
                DurationSeconds = durationSeconds,
                Title = title ?? string.Empty,
                Description = description ?? string.Empty,
                GenreCodes = NormalizeGenreCodes(genreCodes),
                TitleDecodeRoute = decodeRoute ?? string.Empty,
                TitleDecodeStatus = decodeStatus ?? string.Empty,
                BoundaryStatus = boundaryStatus ?? string.Empty,
                RawDescriptorLoopHex = rawDescriptorLoopHex ?? string.Empty,
                RawShortEventDescriptorHex = rawShortEventDescriptorHex ?? string.Empty,
                RawExtendedEventDescriptorHex = rawExtendedEventDescriptorHex ?? string.Empty,
                RawContentDescriptorHex = rawContentDescriptorHex ?? string.Empty
            };
            UpdateAccumulatorMetadata(ev, tableId, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex);
            events[key] = ev;
            return;
        }

        // 同一 service/event_id/start/duration が複数table/sectionで見える場合だけ、
        // 取得TS内の同一番組descriptorをここで集約する。
        // event_id単独では結合しない。0x58/0x59などでshort_event_descriptorが無い
        // 後続fragmentにより、既に得た0x4D/titleを空で上書きしない。
        if (!string.IsNullOrEmpty(title) || string.IsNullOrEmpty(ev.Title))
        {
            ev.Title = title ?? string.Empty;
            ev.TitleDecodeRoute = decodeRoute ?? string.Empty;
            ev.TitleDecodeStatus = decodeStatus ?? string.Empty;
            ev.BoundaryStatus = boundaryStatus ?? string.Empty;
            ev.BestTableId = tableId;
            ev.SectionNumber = sectionNumber;
            ev.VersionNumber = versionNumber;
        }
        if (!string.IsNullOrEmpty(description) || string.IsNullOrEmpty(ev.Description))
        {
            ev.Description = description ?? string.Empty;
        }
        ev.RawDescriptorLoopHex = MergeRawHex(ev.RawDescriptorLoopHex, rawDescriptorLoopHex);
        ev.RawShortEventDescriptorHex = MergeRawHex(ev.RawShortEventDescriptorHex, rawShortEventDescriptorHex);
        ev.RawExtendedEventDescriptorHex = MergeRawHex(ev.RawExtendedEventDescriptorHex, rawExtendedEventDescriptorHex);
        ev.RawContentDescriptorHex = MergeRawHex(ev.RawContentDescriptorHex, rawContentDescriptorHex);
        var normalizedGenreCodes = NormalizeGenreCodes(genreCodes);
        if (!string.IsNullOrEmpty(normalizedGenreCodes))
        {
            ev.GenreCodes = MergeGenreCodes(ev.GenreCodes, normalizedGenreCodes);
        }
        UpdateAccumulatorMetadata(ev, tableId, rawShortEventDescriptorHex, rawExtendedEventDescriptorHex);
        ev.Start = start;
        ev.DurationSeconds = durationSeconds;
    }


    private void ResolveResidualShortDescriptorsFromRawSections()
    {
        if (rawSectionShortResolverApplied) return;
        rawSectionShortResolverApplied = true;

        var residual = events.Values
            .Where(e => e.Start != DateTime.MinValue && e.DurationSeconds > 0)
            .Where(e => string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex) && !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex))
            .ToList();

        rawSectionShortResolverCandidates = residual.Count;
        foreach (var ev in residual)
        {
            var hit = FindStrictRawShortInCapturedSections(ev);
            if (hit is null)
            {
                rawSectionShortResolverUnresolved++;
                continue;
            }

            ev.RawShortEventDescriptorHex = MergeRawHex(ev.RawShortEventDescriptorHex, hit.RawShortEventDescriptorHex);
            ev.RawDescriptorLoopHex = MergeRawHex(ev.RawDescriptorLoopHex, hit.RawDescriptorLoopHex);
            if (!string.IsNullOrEmpty(hit.DecodedTitle) || string.IsNullOrEmpty(ev.Title))
            {
                ev.Title = hit.DecodedTitle;
                ev.Description = hit.DecodedText;
                ev.TitleDecodeRoute = hit.DecodeRoute;
                ev.TitleDecodeStatus = hit.DecodeStatus;
                ev.BoundaryStatus = hit.BoundaryStatus;
                ev.BestTableId = hit.TableId;
                ev.SectionNumber = hit.SectionNumber;
                ev.VersionNumber = hit.VersionNumber;
            }
            ev.ShortSourceTables.Add(hit.TableId);
            ev.ShortDescriptorObservationCount++;
            UpdateAccumulatorMetadata(ev, hit.TableId, hit.RawShortEventDescriptorHex, string.Empty);
            rawSectionShortResolverMerged++;
        }
    }

    private RawShortSectionHit? FindStrictRawShortInCapturedSections(MutableEvent target)
    {
        RawShortSectionHit? best = null;
        foreach (var snapshot in rawSections)
        {
            if (snapshot.NetworkId != target.NetworkId || snapshot.TransportStreamId != target.TransportStreamId || snapshot.ServiceId != target.ServiceId)
                continue;
            if (!IsShortCarrierTable(snapshot.TableId))
                continue;
            var hit = TryFindStrictRawShortInSection(snapshot, target);
            if (hit is null)
                continue;
            if (best is null)
            {
                best = hit;
                continue;
            }
            if (hit.TableId < best.TableId || (hit.TableId == best.TableId && hit.SectionNumber < best.SectionNumber))
                best = hit;
        }
        return best;
    }

    private static bool IsShortCarrierTable(byte tableId)
        => tableId == 0x4E || tableId == 0x4F || (tableId >= 0x50 && tableId <= 0x57);

    private RawShortSectionHit? TryFindStrictRawShortInSection(RawEitSectionSnapshot snapshot, MutableEvent target)
    {
        var section = snapshot.Section.AsSpan();
        if (section.Length < 18) return null;
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var sectionEnd = Math.Min(section.Length, 3 + sectionLength);
        var dataEnd = sectionEnd - 4;
        if (dataEnd < 14) return null;

        var pos = 14;
        while (pos + 12 <= dataEnd)
        {
            var eventId = U16(section, pos);
            var start = DecodeJst(section.Slice(pos + 2, 5));
            var duration = DecodeDuration(section.Slice(pos + 7, 3));
            var descLoopLength = ((section[pos + 10] & 0x0F) << 8) | section[pos + 11];
            var descStart = pos + 12;
            var descEnd = descStart + descLoopLength;
            if (descEnd > dataEnd) return null;

            if (eventId == target.EventId && start == target.Start && duration == target.DurationSeconds)
            {
                var descriptorLoopHex = Hex(section.Slice(descStart, descLoopLength));
                var descriptorPos = descStart;
                while (descriptorPos + 2 <= descEnd)
                {
                    var tag = section[descriptorPos];
                    var len = section[descriptorPos + 1];
                    var next = descriptorPos + 2 + len;
                    if (next > descEnd) break;
                    if (tag == 0x4D)
                    {
                        var rawShort = Hex(section.Slice(descriptorPos, 2 + len));
                        var decoded = AddShortDecode(section, snapshot.NetworkId, snapshot.TransportStreamId, snapshot.ServiceId, eventId, snapshot.TableId, snapshot.SectionNumber, snapshot.LastSectionNumber, descLoopLength, descriptorPos, len);
                        return new RawShortSectionHit(snapshot.TableId, snapshot.SectionNumber, snapshot.VersionNumber, descriptorLoopHex, rawShort, decoded.Title, decoded.Text, decoded.DecodeRoute, decoded.DecodeStatus, decoded.BoundaryStatus);
                    }
                    descriptorPos = next;
                }
            }

            pos = descEnd;
        }
        return null;
    }

    private static void UpdateAccumulatorMetadata(MutableEvent ev, byte tableId, string rawShortEventDescriptorHex, string rawExtendedEventDescriptorHex)
    {
        ev.SourceTables.Add(tableId);
        ev.ObservationCount++;
        if (!string.IsNullOrWhiteSpace(rawShortEventDescriptorHex))
        {
            ev.ShortSourceTables.Add(tableId);
            ev.ShortDescriptorObservationCount++;
        }
        if (!string.IsNullOrWhiteSpace(rawExtendedEventDescriptorHex))
        {
            ev.ExtendedSourceTables.Add(tableId);
            ev.ExtendedDescriptorObservationCount++;
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes);

    private static string MergeRawHex(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left)) return NormalizeRawHexSequence(right);
        if (string.IsNullOrWhiteSpace(right)) return NormalizeRawHexSequence(left);

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddRawHexTokens(tokens, seen, left);
        AddRawHexTokens(tokens, seen, right);
        return string.Join(";", tokens);
    }

    private static string NormalizeRawHexSequence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddRawHexTokens(tokens, seen, raw);
        return string.Join(";", tokens);
    }

    private static void AddRawHexTokens(List<string> tokens, HashSet<string> seen, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var token in raw.Split(new[] { ';', ',', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizeRawHexToken(token);
            if (normalized.Length == 0) continue;
            if (seen.Add(normalized)) tokens.Add(normalized);
        }
    }

    private static string NormalizeRawHexToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length < 2) return string.Empty;
        return (hex.Length & 1) == 1 ? hex[..^1] : hex;
    }

    private static string PreferRaw(string? current, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(current)) return incoming ?? string.Empty;
        if (string.IsNullOrWhiteSpace(incoming)) return current ?? string.Empty;
        return incoming.Length > current.Length ? incoming : current;
    }

    private static string ReadContentDescriptorGenreCodes(ReadOnlySpan<byte> section, int descriptorOffset, int descriptorLength)
    {
        var payloadStart = descriptorOffset + 2;
        var payloadEnd = payloadStart + descriptorLength;
        if (descriptorLength <= 0 || payloadStart < 0 || payloadEnd > section.Length) return string.Empty;
        var codes = new List<string>();
        for (var p = payloadStart; p + 1 < payloadEnd; p += 2)
        {
            var level1 = (section[p] >> 4) & 0x0F;
            var level2 = section[p] & 0x0F;
            if (level1 <= 0x0F) codes.Add(level1.ToString("X1") + level2.ToString("X1"));
        }
        return NormalizeGenreCodes(string.Join(",", codes));
    }

    private static string NormalizeGenreCodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outCodes = new List<string>();
        foreach (var part in raw.Split(new[] { ',', ';', '|', '/', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) token = token[2..];
            if (token.Length == 0) continue;
            var hex = token.ToUpperInvariant();
            if (hex.Length == 1) hex += "0";
            if (hex.Length > 2) hex = hex[..2];
            if (hex.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')) && seen.Add(hex))
                outCodes.Add(hex);
        }
        return string.Join(",", outCodes);
    }

    private static string MergeGenreCodes(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left)) return NormalizeGenreCodes(right);
        if (string.IsNullOrWhiteSpace(right)) return NormalizeGenreCodes(left);
        return NormalizeGenreCodes(left + "," + right);
    }

    private static DateTime DecodeJst(ReadOnlySpan<byte> b)
    {
        if (b.Length < 5 || b[0] == 0xFF) return DateTime.MinValue;
        var mjd = (b[0] << 8) | b[1];
        var yp = (int)((mjd - 15078.2) / 365.25);
        var mp = (int)((mjd - 14956.1 - (int)(yp * 365.25)) / 30.6001);
        var day = mjd - 14956 - (int)(yp * 365.25) - (int)(mp * 30.6001);
        var k = mp is 14 or 15 ? 1 : 0;
        var year = yp + k + 1900;
        var month = mp - 1 - k * 12;
        var hour = Bcd(b[2]);
        var minute = Bcd(b[3]);
        var second = Bcd(b[4]);
        if (year is < 1900 or > 2100 || month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59) return DateTime.MinValue;
        try { return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local); }
        catch { return DateTime.MinValue; }
    }

    private static int DecodeDuration(ReadOnlySpan<byte> b)
        => b.Length < 3 ? 0 : Bcd(b[0]) * 3600 + Bcd(b[1]) * 60 + Bcd(b[2]);

    private static int Bcd(byte v) => ((v >> 4) & 0x0F) * 10 + (v & 0x0F);

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    private sealed record RawEitSectionSnapshot(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, byte TableId, byte SectionNumber, byte LastSectionNumber, byte VersionNumber, byte[] Section);

    private sealed record RawShortSectionHit(byte TableId, byte SectionNumber, byte VersionNumber, string RawDescriptorLoopHex, string RawShortEventDescriptorHex, string DecodedTitle, string DecodedText, string DecodeRoute, string DecodeStatus, string BoundaryStatus);

    private sealed class SectionTracker
    {
        public byte LastSectionNumber;
        public readonly HashSet<byte> Seen = new();
        public readonly Dictionary<byte, byte> SegmentExpected = new();
        public readonly Dictionary<byte, HashSet<byte>> SegmentSeen = new();
    }


    private sealed class MutableEvent
    {
        public ushort NetworkId;
        public ushort TransportStreamId;
        public ushort ServiceId;
        public ushort EventId;
        public byte BestTableId;
        public byte SectionNumber;
        public byte VersionNumber;
        public DateTime Start;
        public int DurationSeconds;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public string GenreCodes = string.Empty;
        public string TitleDecodeRoute = string.Empty;
        public string TitleDecodeStatus = string.Empty;
        public string BoundaryStatus = string.Empty;
        public string RawDescriptorLoopHex = string.Empty;
        public string RawShortEventDescriptorHex = string.Empty;
        public string RawExtendedEventDescriptorHex = string.Empty;
        public string RawContentDescriptorHex = string.Empty;
        public readonly HashSet<byte> SourceTables = new();
        public readonly HashSet<byte> ShortSourceTables = new();
        public readonly HashSet<byte> ExtendedSourceTables = new();
        public int ObservationCount;
        public int ShortDescriptorObservationCount;
        public int ExtendedDescriptorObservationCount;
    }

    private readonly record struct DecodedShortEvent(string Title, string Text, string DecodeRoute, string DecodeStatus, string BoundaryStatus);
}
