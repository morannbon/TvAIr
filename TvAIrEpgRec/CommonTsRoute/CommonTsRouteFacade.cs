namespace TvAIrEpgRec.CommonTsRoute;

/// <summary>
/// release_contract integrated TvAIrEpgRec common TS route.
/// TvAIrEpgRec owns recording, EPG acquisition, and pre-record EPG-check execution in one process.
/// TvAIrEpgRec owns the integrated BonDriver/OpenTuner/SetChannel/TS-read route; no alternate recorder executable or fallback route participates in execution.
/// </summary>
internal static class CommonTsRouteFacade
{
    public const string Rule = "release_contract";

    public static CommonTsRouteAttachment AttachForMode(string mode, CommonTsRouteRequest request)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "unknown" : mode.Trim().ToLowerInvariant();
        var issues = Validate(request).ToList();
        var routeReady = issues.Count == 0;
        return new CommonTsRouteAttachment
        {
            Rule = Rule,
            Mode = normalizedMode,
            RouteBeforeMode = true,
            ModeAfterServiceScopedTs = normalizedMode == "record",
            Owner = "TvAIrEpgRec common TS runtime",
            RecordExecutionRoute = "TvAIrEpgRec",
            LegacyFallbackRouteAvailable = false,
            LegacyExecutableUsed = false,
            ExternalRecorderRuntimeDependency = false,
            FacadeAttached = true,
            RouteReadyForMode = routeReady,
            Request = request,
            Identity = new ServiceTripletContract(
                request.NetworkId,
                request.TransportStreamId,
                request.ServiceId,
                request.ChannelSpace,
                request.ChannelIndex,
                request.BonDriver),
            ServiceIdentity = new ServiceExecutionIdentityContract
            {
                ExpectedNetworkId = request.NetworkId,
                ExpectedTransportStreamId = request.TransportStreamId,
                ExpectedServiceId = request.ServiceId,
                ChannelSpace = request.ChannelSpace,
                ChannelIndex = request.ChannelIndex,
                BonDriver = request.BonDriver,
                SetChannel = new SetChannelIdentity(request.ChannelSpace, request.ChannelIndex)
            },
            RequiredSharedStages =
            [
                "TvAIrEpgRec-owned tuner acquire/open",
                "BonDriver OpenTuner",
                "SetChannel(chspace, chi)",
                "NID/TSID/SID identity",
                "PAT/PMT/SDT service confirmation",
                "TargetServiceReady()",
                normalizedMode == "record" ? "service-scoped TS packets" : "transport-stream SI/EIT packets, preserving PID 0x0029 for logo capture",
                "CloseTuner/Release/FreeLibrary in TvAIrEpgRec process"
            ],
            ModeSpecificStages = normalizedMode switch
            {
                "epg" => ["transport-stream schedule EIT extraction", "preserve SI/CDT PID 0x0029 for service-logo extraction", "ARIB decode via AribDecodeBridge", "intermediate EPG model", "validated return to TvAIr body"],
                "epg-check" => ["short target-program timing confirmation", "dbWrite=false"],
                "record" => ["TvAIrEpgRec record execution"],
                _ => ["no production mode-specific stage attached"]
            },
            StopLine =
            [
                "Do not use station-name partial matching.",
                "Do not use NEXT string search.",
                "Do not add EPG-only SID resolver as production logic.",
                "Do not add a parallel recording route outside TvAIrEpgRec.",
                "Do not add a legacy recorder executable dependency or fallback route.",
                "SleepGuard/process monitoring target is TvAIrEpgRec.exe only."
            ],
            ValidationIssues = issues
        };
    }

    private static IEnumerable<string> Validate(CommonTsRouteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BonDriver)) yield return "bonDriver_missing";
        if (request.NetworkId <= 0) yield return "networkId_missing";
        if (request.TransportStreamId <= 0) yield return "transportStreamId_missing";
        if (request.ServiceId <= 0) yield return "serviceId_missing";
        if (request.ChannelSpace < 0) yield return "channelSpace_invalid";
        if (request.ChannelIndex < 0) yield return "channelIndex_invalid";
    }
}

internal sealed record CommonTsRouteRequest(
    string Group,
    string Tuner,
    string Did,
    string BonDriver,
    string? BonDriverPath,
    string ServiceName,
    int NetworkId,
    int TransportStreamId,
    int ServiceId,
    int ChannelSpace,
    int ChannelIndex);

internal sealed class CommonTsRouteAttachment
{
    public string Rule { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public bool RouteBeforeMode { get; set; }
    public bool ModeAfterServiceScopedTs { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string RecordExecutionRoute { get; set; } = string.Empty;
    public bool LegacyFallbackRouteAvailable { get; set; }
    public bool LegacyExecutableUsed { get; set; }
    public bool ExternalRecorderRuntimeDependency { get; set; }
    public bool FacadeAttached { get; set; }
    public bool RouteReadyForMode { get; set; }
    public CommonTsRouteRequest? Request { get; set; }
    public ServiceTripletContract? Identity { get; set; }
    public ServiceExecutionIdentityContract? ServiceIdentity { get; set; }
    public string[] RequiredSharedStages { get; set; } = [];
    public string[] ModeSpecificStages { get; set; } = [];
    public string[] StopLine { get; set; } = [];
    public List<string> ValidationIssues { get; set; } = new();
}

internal sealed class ServiceExecutionIdentityContract
{
    public int ExpectedNetworkId { get; set; }
    public int ExpectedTransportStreamId { get; set; }
    public int ExpectedServiceId { get; set; }
    public int ChannelSpace { get; set; }
    public int ChannelIndex { get; set; }
    public string BonDriver { get; set; } = string.Empty;
    public SetChannelIdentity SetChannel { get; set; } = new(0, 0);
}

internal sealed record SetChannelIdentity(int Chspace, int Chi);
