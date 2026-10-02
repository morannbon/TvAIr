using System.Runtime.InteropServices;

namespace TvAIr.Core;

/// <summary>
/// TvAIr の Windows 自動起動を Windows Task Scheduler 2.0 の標準APIで管理する。
///
/// 契約:
/// - INI StartupEnabled が正本。
/// - ON では現在ユーザーの対話ログオン時に TvAIr.exe を通常起動する TvAIr_AutoStart タスクを作成/現行化する。
/// - OFF では TvAIr_AutoStart タスクを削除する。
/// - Wake タスク、予約、EPG、単一インスタンス制御の責務は持たない。
/// </summary>
public sealed class WindowsAutoStartService
{
    private const string TaskName = "TvAIr_AutoStart";

    // Task Scheduler 2.0 constants (taskschd.h)
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunlevelLua = 0;

    private readonly LogRepository _log;

    public WindowsAutoStartService(LogRepository log)
    {
        _log = log;
    }

    public bool Set(bool enabled)
    {
        try
        {
            if (enabled)
                return RegisterOrRefreshTask();

            // Taskが既に無くても成功扱い。
            return DeleteTask();
        }
        catch (Exception ex)
        {
            _log.Add("Startup", "AutoStart",
                $"Windows自動起動設定の更新に失敗しました: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool RegisterOrRefreshTask()
    {
        var exe = GetExecutablePath();
        var workingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;

        dynamic? service = null;
        dynamic? root = null;
        dynamic? definition = null;
        dynamic? trigger = null;
        dynamic? action = null;
        dynamic? registeredTask = null;

        try
        {
            service = CreateTaskService();
            service.Connect();
            root = service.GetFolder("\\");
            definition = service.NewTask(0);

            definition.RegistrationInfo.Description = "TvAIr をユーザーログオン時に自動起動します。";

            // デスクトップUIを持つ通常のWindowsアプリとして、ログオン済みユーザーの
            // interactive tokenで起動する。SYSTEM/Boot triggerや資格情報保存は使わない。
            var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
            definition.Principal.UserId = currentUser;
            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = TaskRunlevelLua;

            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
            definition.Settings.ExecutionTimeLimit = "PT0S";

            trigger = definition.Triggers.Create(TaskTriggerLogon);
            trigger.UserId = currentUser;
            trigger.Enabled = true;

            action = definition.Actions.Create(TaskActionExec);
            action.Path = exe;
            action.WorkingDirectory = workingDirectory;

            registeredTask = root.RegisterTaskDefinition(
                TaskName,
                definition,
                TaskCreateOrUpdate,
                null,
                null,
                TaskLogonInteractiveToken,
                null);

            _log.Add("Startup", "AutoStart",
                $"result=REGISTERED task={TaskName} trigger=UserLogon logonType=InteractiveToken runLevel=LeastPrivilege exe={exe} workingDirectory={workingDirectory} rule=windows_autostart_standard_logon_task_contract");
            return true;
        }
        finally
        {
            ReleaseCom(registeredTask);
            ReleaseCom(action);
            ReleaseCom(trigger);
            ReleaseCom(definition);
            ReleaseCom(root);
            ReleaseCom(service);
        }
    }

    private bool DeleteTask()
    {
        dynamic? service = null;
        dynamic? root = null;
        dynamic? existing = null;

        try
        {
            service = CreateTaskService();
            service.Connect();
            root = service.GetFolder("\\");

            try
            {
                existing = root.GetTask(TaskName);
            }
            catch (COMException ex) when (IsTaskNotFound(ex))
            {
                return true;
            }

            root.DeleteTask(TaskName, 0);
            _log.Add("Startup", "AutoStart",
                $"result=DELETED task={TaskName} rule=windows_autostart_standard_logon_task_contract");
            return true;
        }
        finally
        {
            ReleaseCom(existing);
            ReleaseCom(root);
            ReleaseCom(service);
        }
    }

    private static dynamic CreateTaskService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new InvalidOperationException("Windows Task Scheduler 2.0 APIを利用できません。");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Windows Task Schedulerサービスへ接続できません。");
    }

    private static bool IsTaskNotFound(COMException ex)
    {
        // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
        return ex.HResult == unchecked((int)0x80070002);
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
            return Path.GetFullPath(processPath);

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TvAIr.exe"));
    }
}
