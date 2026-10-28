using System.Text.Json;
using TvAIr.Core;
using TvAIr.Epg;

namespace TvAIr.Schedule;

internal sealed class KeywordRuleImportCommand : ReservationTopologyCommand
{
    private readonly IReadOnlyList<KeywordRule> rules;
    private readonly KeywordMatcher matcher;
    private ReservationScheduler? scheduler;
    internal KeywordRuleMutationResult? Result { get; private set; }
    internal KeywordRuleImportCommand(IReadOnlyList<KeywordRule> requested, KeywordMatcher matcher)
    {
        rules = Array.AsReadOnly(JsonSerializer.Deserialize<KeywordRule[]>(JsonSerializer.Serialize(requested))!);
        this.matcher = matcher;
    }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RequiresRuleRefreshAfterCommit => Result is { Applied: true };
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler = value;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result is not null) return;
        (scheduler ?? throw new InvalidOperationException("Rule import scheduler is unavailable."))
            .ExecuteKeywordRuleImportCore(store, rules, matcher, result => Result = result);
    }
}
