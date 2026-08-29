namespace TvAIr.Core;

public enum ApplicationOperationState { Running, PowerTransition, Quiescing, Stopped }

/// <summary>
/// Process-wide admission gate for new physical operations during shutdown.
/// Existing stop/finalization paths remain allowed so owned workers can converge.
/// </summary>
public sealed class ApplicationOperationGate
{
    private readonly object _gate = new();
    private readonly LogRepository _log;
    private ApplicationOperationState _state = ApplicationOperationState.Running;
    private long _generation;

    public ApplicationOperationGate(LogRepository log) => _log = log;

    public ApplicationOperationState State { get { lock (_gate) return _state; } }
    public long Generation { get { lock (_gate) return _generation; } }
    public bool IsRunning => State == ApplicationOperationState.Running;

    public bool BeginQuiescing(string reason)
    {
        lock (_gate)
        {
            if (_state == ApplicationOperationState.Quiescing || _state == ApplicationOperationState.Stopped) return false;
            _state = ApplicationOperationState.Quiescing;
            _generation++;
            _log.Add("APP_OPERATION_GATE", "Quiescing",
                $"result=BEGIN reason={Safe(reason)} generation={_generation} action=reject_new_physical_operations rule=shutdown_admission_contract");
            return true;
        }
    }

    public bool TryBeginPowerTransition(string reason, out long generation)
    {
        lock (_gate)
        {
            if (_state != ApplicationOperationState.Running)
            {
                generation = _generation;
                return false;
            }

            _state = ApplicationOperationState.PowerTransition;
            generation = ++_generation;
            _log.Add("APP_OPERATION_GATE", "PowerTransition",
                $"result=BEGIN reason={Safe(reason)} generation={generation} action=reject_new_physical_operations rule=power_transition_admission_contract");
            return true;
        }
    }

    public bool EndPowerTransition(long generation, string reason)
    {
        lock (_gate)
        {
            if (_state != ApplicationOperationState.PowerTransition || _generation != generation)
                return false;

            _state = ApplicationOperationState.Running;
            _generation++;
            _log.Add("APP_OPERATION_GATE", "Running",
                $"result=RESUME reason={Safe(reason)} transitionGeneration={generation} generation={_generation} rule=power_transition_admission_contract");
            return true;
        }
    }

    public void MarkStopped(string reason)
    {
        lock (_gate)
        {
            if (_state == ApplicationOperationState.Stopped) return;
            _state = ApplicationOperationState.Stopped;
            _generation++;
            _log.Add("APP_OPERATION_GATE", "Stopped",
                $"result=END reason={Safe(reason)} generation={_generation} rule=shutdown_admission_contract");
        }
    }

    public bool TryAdmit(string operation, string owner, out string reason)
    {
        lock (_gate)
        {
            if (_state == ApplicationOperationState.Running)
            {
                reason = string.Empty;
                return true;
            }
            reason = _state switch
            {
                ApplicationOperationState.PowerTransition => "power_transition",
                ApplicationOperationState.Quiescing => "application_quiescing",
                _ => "application_stopped"
            };
            _log.Add("APP_OPERATION_REJECTED", owner,
                $"result=REJECTED operation={Safe(operation)} reason={reason} state={_state} generation={_generation} rule=shutdown_admission_contract");
            return false;
        }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
