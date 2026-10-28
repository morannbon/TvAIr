using TvAIr.Core;

namespace TvAIr.Epg;

// Evaluation has no persistence side effects. The allocation-owned transaction must
// validate this complete scheduled set before applying its removal decisions.
internal sealed record KeywordRuleReconcilePlan(IReadOnlyList<Reservation> ExpectedScheduled,
    IReadOnlyList<int> StaleIds, int Preserved, int PreservedSourceMissing, string RuleName);
