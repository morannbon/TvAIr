using TvAIrPlugin.Runtime;

namespace TvAIrPlugin.Viewers;

public static class TvAirViewerActivation
{
    public const string Activate = "activate";
    public const string Preserve = "preserve";
}

public sealed record TvAirServiceIdentityDto
{
    public required ushort NetworkId { get; init; }
    public required ushort TransportStreamId { get; init; }
    public required ushort ServiceId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
}

public sealed record TvAirViewerProfileDto
{
    public required string ViewerProfileId { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsAvailable { get; init; }
    public required bool IsDefault { get; init; }
    public required IReadOnlyList<string> BroadcastGroups { get; init; }
    public required string LogicalViewerSlotId { get; init; }
    /// <summary>TvAIr設定でこのViewerに割り当てられた各放送波内の実デバイス番号。</summary>
    public int TvTestFrameIndex { get; init; }
    public string? CurrentViewerSessionId { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record TvAirViewerSessionDto
{
    public required string ViewerSessionId { get; init; }
    public required string ViewerProfileId { get; init; }
    public required string LogicalViewerSlotId { get; init; }
    public required long Generation { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public int? ProcessId { get; init; }
    public TvAirServiceIdentityDto? CurrentService { get; init; }
    public int? ChannelSpace { get; init; }
    public int? ChannelIndex { get; init; }
}

public sealed record TvAirViewerStartRequest
{
    public required string ViewerProfileId { get; init; }
    public required TvAirServiceIdentityDto Service { get; init; }
    public string? ViewerSessionId { get; init; }
    public long? ExpectedGeneration { get; init; }
    public bool PreserveViewerWindowState { get; init; }
    /// <summary>Foreground policy. Activate is explicit; Preserve or null remains silent for launch, retune, and same-service no-op.</summary>
    public string? ViewerActivation { get; init; }
    public bool RetuneExistingViewer { get; init; }
}

/// <summary>
/// Requests explicit regeneration of the current managed Viewer Session while preserving
/// the Viewer Profile, logical slot, tuner identity, service identity, and channel identity
/// owned by the current session. This operation ends the old session and creates a new one.
/// </summary>
public sealed record TvAirViewerRestartRequest
{
    /// <summary>Current Viewer Session to restart.</summary>
    public required string ViewerSessionId { get; init; }

    /// <summary>Current generation used to reject stale operations.</summary>
    public required long ExpectedGeneration { get; init; }

    /// <summary>Captures and restores the managed Viewer window state across process replacement.</summary>
    public bool PreserveViewerWindowState { get; init; }

    /// <summary>Either <see cref="TvAirViewerActivation.Preserve"/> or <see cref="TvAirViewerActivation.Activate"/>. Null defaults to preserve.</summary>
    public string? ViewerActivation { get; init; }

    /// <summary>Optional diagnostic reason, limited to 256 characters.</summary>
    public string? Reason { get; init; }
}

public sealed record TvAirViewerActivateRequest
{
    public required string ViewerSessionId { get; init; }
    public required long ExpectedGeneration { get; init; }
}

public sealed record TvAirViewerStopRequest
{
    public required string ViewerSessionId { get; init; }
    public required long ExpectedGeneration { get; init; }
}

public sealed record TvAirViewerOperationDto
{
    public required string State { get; init; }
    public required string ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public string ViewerProfileId { get; init; } = string.Empty;
    public string ViewerSessionId { get; init; } = string.Empty;
    public long Generation { get; init; }
    public string LeaseId { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public TvAirServiceIdentityDto? CurrentService { get; init; }
    public TvAirViewerSessionDto? Session { get; init; }
    public string Diagnostics { get; init; } = string.Empty;
    // SDK 1.1.2 binary-compatibility surface. Host no longer performs foreground restore for preserve retunes.
    public string FocusPolicyRequested { get; init; } = string.Empty;
    public string FocusPolicyApplied { get; init; } = string.Empty;
    public long? ForegroundBeforeHwnd { get; init; }
    public int? ForegroundBeforePid { get; init; }
    public long? ForegroundAfterRetuneHwnd { get; init; }
    public int? ForegroundAfterRetunePid { get; init; }
    public long? ForegroundFinalHwnd { get; init; }
    public int? ForegroundFinalPid { get; init; }
    public bool ForegroundChanged { get; init; }
    public bool ChangedToTargetViewer { get; init; }
    public bool RestorationAttempted { get; init; }
    public bool RestorationSucceeded { get; init; }
    public bool FocusPreserved { get; init; }
    public string FocusPreserveFailureReason { get; init; } = string.Empty;
    /// <summary>True when the requested Viewer transition completed authoritatively.</summary>
    public bool OperationCompleted { get; init; }
    /// <summary>True when the transition completed with a non-fatal quality warning.</summary>
    public bool HasWarning { get; init; }
    /// <summary>True when timer-driven or other automatic workflows may continue.</summary>
    public bool ContinuationRecommended { get; init; }
}

public interface ITvAirViewersApi
{
    IReadOnlyList<TvAirViewerProfileDto> ListProfiles();
    IReadOnlyList<TvAirViewerSessionDto> ListSessions();
    TvAirViewerSessionDto? GetSession(string viewerSessionId);
    Task<TvAirOperationResult<TvAirViewerOperationDto>> StartAsync(TvAirViewerStartRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Explicitly restarts the current managed Viewer Session. Success returns a new Session ID,
    /// generation, lease, and process. Failure diagnostics describe the completed stage.
    /// </summary>
    Task<TvAirOperationResult<TvAirViewerOperationDto>> RestartAsync(TvAirViewerRestartRequest request, CancellationToken cancellationToken = default);
    Task<TvAirOperationResult<TvAirViewerOperationDto>> ActivateAsync(TvAirViewerActivateRequest request, CancellationToken cancellationToken = default);
    Task<TvAirOperationResult<TvAirViewerOperationDto>> StopAsync(TvAirViewerStopRequest request, CancellationToken cancellationToken = default);
}
