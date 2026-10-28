using TvAIr.Schedule;

namespace TvAIr.Epg;

internal readonly record struct EpgImportOutcome(int CommittedCount, bool Deferred, bool ParsedCapture = false)
{
    internal bool ShouldRetryCapture => CommittedCount == 0 && !Deferred && !ParsedCapture;
    internal static EpgImportOutcome Empty => new(0, false);
    internal static async Task<EpgImportOutcome> FromCommittedAsync(
        EpgCaptureCommitCommand command, CancellationToken cancellation)
    {
        await command.WaitForCommitAsync(cancellation).ConfigureAwait(false);
        return From(command);
    }

    internal static EpgImportOutcome From(EpgCaptureCommitCommand command)
    {
        // Read completion first: if another flight finishes concurrently, the count
        // cannot be observed as zero together with a completed successful capture.
        var complete = command.IsComplete;
        var committed = command.Result?.Capture?.Upsert.Count ?? 0;
        return new(committed, !complete, ParsedCapture: true);
    }
}
