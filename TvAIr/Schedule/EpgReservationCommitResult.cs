using TvAIr.Epg;

namespace TvAIr.Schedule;

// A capture is exposed only after the EPG and reservation transaction has committed.
internal sealed record EpgReservationCommitResult(bool Applied, string Reason,
    EpgCaptureCommitResult? Capture, IReadOnlyList<int> RemovedIds);
