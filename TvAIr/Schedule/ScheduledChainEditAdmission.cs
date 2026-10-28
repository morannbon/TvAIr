namespace TvAIr.Schedule;

/// <summary>
/// In-process admission for chain edits and runtime publication. Try-entry never waits
/// for active work; recovery can asynchronously await edit completion.
/// Owns synchronization only, not reservation/session state. All producers must use the
/// same Host instance to exclude edits from runtime admission/publication.
/// No DB, control, callback, or asynchronous work runs under the internal monitor.
/// </summary>
public sealed class ScheduledChainEditAdmission
{
    private readonly object gate = new();
    private int runtimePublishers;
    private bool editing;
    private TaskCompletionSource<bool>? editCompletion;

    public bool TryEnterRuntimePublication(out IDisposable? lease)
    {
        lock (gate)
        {
            lease = null;
            if (editing) return false;
            var admitted = new AdmissionLease(this, edit: false);
            runtimePublishers = checked(runtimePublishers + 1);
            lease = admitted;
            return true;
        }
    }

    public bool TryEnterEdit(out IDisposable? lease)
    {
        lock (gate)
        {
            lease = null;
            if (editing || runtimePublishers != 0) return false;
            var admitted = new AdmissionLease(this, edit: true);
            editCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            editing = true;
            lease = admitted;
            return true;
        }
    }

    /// <summary>
    /// Recovery must not lose its one-shot reattach opportunity because an edit is busy.
    /// Waits only for edit completion; no polling, timer, or worker/session state is stored.
    /// </summary>
    public async ValueTask<IDisposable> EnterRuntimePublicationAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (gate)
            {
                if (!editing)
                {
                    var admitted = new AdmissionLease(this, edit: false);
                    runtimePublishers = checked(runtimePublishers + 1);
                    return admitted;
                }
                wait = editCompletion!.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Exit(bool edit)
    {
        TaskCompletionSource<bool>? completed = null;
        lock (gate)
        {
            if (edit)
            {
                editing = false;
                completed = editCompletion;
                editCompletion = null;
            }
            else runtimePublishers--;
        }
        completed?.TrySetResult(true);
    }

    private sealed class AdmissionLease(ScheduledChainEditAdmission owner, bool edit) : IDisposable
    {
        private ScheduledChainEditAdmission? admission = owner;
        public void Dispose() => Interlocked.Exchange(ref admission, null)?.Exit(edit);
    }
}
