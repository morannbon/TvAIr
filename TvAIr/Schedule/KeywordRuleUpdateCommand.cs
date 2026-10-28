using System.Text.Json;
using TvAIr.Core;
using TvAIr.Epg;

namespace TvAIr.Schedule;

internal sealed class KeywordRuleUpdateCommand : ReservationTopologyCommand
{
    private readonly KeywordRule rule;
    private readonly KeywordMatcher matcher;
    private ReservationScheduler? scheduler;
    internal KeywordRuleMutationResult? Result { get; private set; }
    internal KeywordRuleUpdateCommand(KeywordRule requested, KeywordMatcher matcher)
    {
        rule = JsonSerializer.Deserialize<KeywordRule>(JsonSerializer.Serialize(requested))!;
        this.matcher = matcher;
    }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RequiresRuleRefreshAfterCommit => Result is { Applied: true } && rule.Enabled;
    internal override bool RequiresProgramRuleRefreshAfterCommit => Result is { Applied: true };
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler = value;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result is not null) return;
        (scheduler ?? throw new InvalidOperationException("Rule update scheduler is unavailable."))
            .ExecuteKeywordRuleUpdateCore(store, rule, matcher, result => Result = result);
    }
}
