using System.Text.Json.Serialization;
using TvAIr.Core;

namespace TvAIr.Schedule;

public sealed class TunerAllocationDebugSnapshot
{
    public DateTime GeneratedAt { get; set; }
    public string Version { get; set; } = "v34.01-debug";
    public TunerAllocationDebugSettings Settings { get; set; } = new();
    public Dictionary<string, int> TunerLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TunerAllocationDebugSummary Summary { get; set; } = new();
    public List<TunerAllocationDebugGroup> Groups { get; set; } = new();
    public List<TunerAllocationDebugEvent> Skipped { get; set; } = new();
    public List<TunerAllocationDebugTraceEntry> Trace { get; set; } = new();
}

public sealed class TunerAllocationDebugTraceEntry
{
    public string Stage { get; set; } = "";
    public string Group { get; set; } = "";
    public int ReservationId { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class TunerAllocationDebugSettings
{
    public bool LaterProgramPriority { get; set; }

    // Effective chain mode used by the allocator. This remains false when the
    // setting is enabled but no explicit user-chain pair exists.
    public bool PseudoContinuousRecording { get; set; }

    // v0.11.582: Keep configured/effective chain state separate in diagnostics.
    // This is audit-only and must not affect allocation.
    public bool ConfiguredPseudoContinuousRecording { get; set; }
    public bool ChainModeEnabled { get; set; }
    public int UserChainCandidatePairs { get; set; }

    public int PreStartMarginSeconds { get; set; }
    public int PostEndMarginSeconds { get; set; }
}

public sealed class TunerAllocationDebugSummary
{
    public int EvaluatedCount { get; set; }
    public int AllocatedCount { get; set; }
    public int ConflictCount { get; set; }
    public int SkippedCount { get; set; }
}

public sealed class TunerAllocationDebugGroup
{
    public int GroupIndex { get; set; }
    public string Wave { get; set; } = "";
    public int Limit { get; set; }
    public DateTime OccupyStart { get; set; }
    public DateTime OccupyEnd { get; set; }
    public List<TunerAllocationDebugEvent> Events { get; set; } = new();
}

public sealed class TunerAllocationDebugEvent
{
    public int ReservationId { get; set; }
    public int DisplayNo { get; set; }
    public ReservationSource Source { get; set; }
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = "";
    public string Group { get; set; } = "";
    public string Title { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public ushort ServiceId { get; set; }
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort EventId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime OccupyStart { get; set; }
    public DateTime OccupyEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string TunerName { get; set; } = "";
    public string Result { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsConflicted { get; set; }
    public int? ChainPredecessorId { get; set; }
    public int? ChainSuccessorId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceRuleId { get; set; }

    public string SourceRuleName { get; set; } = "";
}
