namespace TvAIrEpgRec.CommonTsRoute;

/// <summary>
/// release_contract integrated TvAIrEpgRec route attachment for the DirectRecorderBridge-derived TS runtime.
/// TvAIrEpgRec is the successor executable name; it must not launch DirectRecorderBridge.exe as a child process.
/// The DirectRecorderBridge recording/runtime lineage is kept inside this process boundary, then mode-specific EPG logic is attached after service selection.
/// </summary>
internal static class DirectRecorderTsRouteFacade
{
    public const string Rule = "release_contract";

    public static CommonTsRouteAttachment AttachForMode(string mode, DirectRecorderTsRouteRequest request)
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
            OwnerLineage = "DirectRecorderBridge-derived tuner lifecycle ported into TvAIrEpgRec boundary; DirectRecorderBridge executable and source remain untouched",
            ProductionRecordRoute = "DirectRecorderBridge",
            ProductionRecordRouteSwitchAllowed = false,
            ExistingRecordRouteTouched = false,
            DirectRecorderRuntimeCodeShared = false,
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
            DirectRecorderIdentity = new DirectRecorderIdentityContract
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
                "TvAIrEpgRec-owned tuner acquire/open ported from DirectRecorderBridge behavior",
                "BonDriver OpenTuner",
                "SetChannel(chspace, chi)",
                "NID/TSID/SID identity",
                "PAT/PMT/SDT service confirmation",
                "TargetServiceReady()",
                normalizedMode == "record" ? "service-scoped TS packets" : "transport-stream SI/EIT packets, preserving PID 0x0029 for logo capture",
                "CloseTuner/Release/FreeLibrary/stop-cooldown in TvAIrEpgRec process"
            ],
            ModeSpecificStages = normalizedMode switch
            {
                "epg" => ["transport-stream schedule EIT extraction", "preserve SI/CDT PID 0x0029 for service-logo extraction", "ARIB decode via AribDecodeBridge", "intermediate EPG model", "validated return to TvAIr body"],
                "epg-check" => ["short target-program timing confirmation", "dbWrite=false"],
                "record" => ["record execution after staged migration"],
                _ => ["no production mode-specific stage attached"]
            },
            StopLine =
            [
                "Do not use station-name partial matching.",
                "Do not use NEXT string search.",
                "Do not add EPG-only SID resolver as production logic.",
                "Do not switch production recording in this stage.",
                "Do not launch DirectRecorderBridge.exe from TvAIrEpgRec.",
                "SleepGuard/process monitoring target is TvAIrEpgRec.exe only."
            ],
            ValidationIssues = issues
        };
    }

    private static IEnumerable<string> Validate(DirectRecorderTsRouteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BonDriver)) yield return "bonDriver_missing";
        if (request.NetworkId <= 0) yield return "networkId_missing";
        if (request.TransportStreamId <= 0) yield return "transportStreamId_missing";
        if (request.ServiceId <= 0) yield return "serviceId_missing";
        if (request.ChannelSpace < 0) yield return "channelSpace_invalid";
        if (request.ChannelIndex < 0) yield return "channelIndex_invalid";
    }
}

internal sealed record DirectRecorderTsRouteRequest(
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
    public string OwnerLineage { get; set; } = string.Empty;
    public string ProductionRecordRoute { get; set; } = string.Empty;
    public bool ProductionRecordRouteSwitchAllowed { get; set; }
    public bool ExistingRecordRouteTouched { get; set; }
    public bool DirectRecorderRuntimeCodeShared { get; set; }
    public bool FacadeAttached { get; set; }
    public bool RouteReadyForMode { get; set; }
    public DirectRecorderTsRouteRequest? Request { get; set; }
    public ServiceTripletContract? Identity { get; set; }
    public DirectRecorderIdentityContract? DirectRecorderIdentity { get; set; }
    public string[] RequiredSharedStages { get; set; } = [];
    public string[] ModeSpecificStages { get; set; } = [];
    public string[] StopLine { get; set; } = [];
    public List<string> ValidationIssues { get; set; } = new();
}

internal sealed class DirectRecorderIdentityContract
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
