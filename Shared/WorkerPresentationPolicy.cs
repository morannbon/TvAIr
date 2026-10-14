namespace TvAIr.Workers;

// One immutable presentation decision crosses the process boundary. Job metadata
// describes content only; neither startup window flags nor state updates select visibility.
public enum WorkerPresentationPolicy
{
    Hidden,
    Taskbar
}

public static class WorkerPresentationContract
{
    public const string ArgumentName = "worker-presentation";

    public static WorkerPresentationPolicy Select(bool eligible, bool enabled)
        => eligible && enabled ? WorkerPresentationPolicy.Taskbar : WorkerPresentationPolicy.Hidden;

    public static string Serialize(WorkerPresentationPolicy policy)
        => policy == WorkerPresentationPolicy.Taskbar ? "taskbar" : "hidden";

    public static WorkerPresentationPolicy Parse(string? value)
        => string.Equals(value, "taskbar", StringComparison.OrdinalIgnoreCase)
            ? WorkerPresentationPolicy.Taskbar
            : WorkerPresentationPolicy.Hidden;
}
